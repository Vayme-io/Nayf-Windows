using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>
/// Manages Vayme's cloud integrations (Google Calendar first): connecting via OAuth,
/// reading which providers are connected, and disconnecting. It talks ONLY to the
/// Cloudflare Worker — the OAuth tokens live server-side in Supabase and never reach
/// the app. Every call is authenticated with the user's Supabase access token.
/// Mirrors NayfIntegrationsManager.swift.
///
/// Because the Worker stores those tokens against the Supabase *user* rather than the
/// device, a provider connected on the Mac is already connected here. Nothing needs
/// syncing — this class just has to ask.
///
/// The connect flow opens the user's browser for Google's consent screen; the Worker
/// stores the tokens when they approve. The browser has no way to call back into the
/// app, so after opening it we poll the status endpoint until the connection appears.
/// That poll deliberately lives here rather than in the panel: handing focus to the
/// browser blur-dismisses the panel, and the poll has to outlive it.
/// </summary>
public sealed class NayfIntegrationsManager : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public const string GoogleProvider = "google";
    public const string GitHubProvider = "github";

    /// <summary>How long to keep watching for the browser half of the flow to finish.</summary>
    private const int ConnectPollAttempts = 12;
    private const int ConnectPollIntervalMilliseconds = 2500;

    private readonly AuthManager _authManager;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    private HashSet<string> _connectedProviders = new(StringComparer.OrdinalIgnoreCase);

    public NayfIntegrationsManager(AuthManager authManager)
    {
        _authManager = authManager;
    }

    // MARK: - State

    private bool _isRefreshing;
    /// <summary>True while a status request is in flight, so the page can show progress.</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { _isRefreshing = value; OnPropertyChanged(); }
    }

    private string? _connectingProvider;
    /// <summary>The provider id currently mid-connect (so only that card shows a spinner), or null.</summary>
    public string? ConnectingProvider
    {
        get => _connectingProvider;
        private set { _connectingProvider = value; OnPropertyChanged(); }
    }

    private string? _errorMessage;
    /// <summary>User-facing failure text, or null. Cleared when a new attempt starts.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    /// <summary>Provider ids the user currently has connected (e.g. "google").</summary>
    public IReadOnlyCollection<string> ConnectedProviders => _connectedProviders;

    public bool IsConnected(string provider) => _connectedProviders.Contains(provider);
    public bool IsGoogleConnected => IsConnected(GoogleProvider);
    public bool IsGitHubConnected => IsConnected(GitHubProvider);

    /// <summary>True when at least one provider is connected — drives the home-screen label.</summary>
    public bool HasAnyConnection => _connectedProviders.Count > 0;

    public bool IsConnecting(string provider)
        => string.Equals(ConnectingProvider, provider, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True once a status response has come back, so the UI can tell "not connected"
    /// apart from "we haven't asked yet" and avoid flashing a Connect button at a
    /// user who is already connected.
    /// </summary>
    public bool HasLoadedStatus { get; private set; }

    // MARK: - Status

    /// <summary>Loads which providers are connected from the Worker.</summary>
    public async Task RefreshStatusAsync()
    {
        var accessToken = await _authManager.CurrentAccessTokenAsync();
        if (accessToken == null) return;

        IsRefreshing = true;
        try
        {
            using var response = await PostAsync("/integrations/status", accessToken, body: null);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Log("Integrations", $"status returned HTTP {(int)response.StatusCode}");
                return;
            }

            var responseText = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(responseText);
            if (!document.RootElement.TryGetProperty("connected", out var connected) ||
                connected.ValueKind != JsonValueKind.Array)
                return;

            var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in connected.EnumerateArray())
            {
                if (entry.TryGetProperty("provider", out var providerProperty) &&
                    providerProperty.GetString() is { Length: > 0 } providerId)
                    providers.Add(providerId);
            }

            HasLoadedStatus = true;
            SetConnectedProviders(providers);
        }
        catch (Exception ex)
        {
            Logger.Log("Integrations", $"status failed: {ex.Message}");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    // MARK: - Connect / disconnect

    public Task ConnectGoogleAsync() => ConnectAsync(GoogleProvider, "/oauth/google/start");
    public Task ConnectGitHubAsync() => ConnectAsync(GitHubProvider, "/oauth/github/start");

    /// <summary>Routes a provider id to its connect flow, for callers driving a card generically.</summary>
    public Task ConnectAsync(string provider) => provider switch
    {
        GoogleProvider => ConnectGoogleAsync(),
        GitHubProvider => ConnectGitHubAsync(),
        _ => Task.CompletedTask
    };

    /// <summary>
    /// Starts a provider's connect flow: asks the Worker for the consent URL, opens it in
    /// the user's browser, then polls status until the connection shows up (or times out).
    /// The browser can't call back into the app, so polling is how we learn it succeeded.
    /// </summary>
    private async Task ConnectAsync(string provider, string startPath)
    {
        var accessToken = await _authManager.CurrentAccessTokenAsync();
        if (accessToken == null)
        {
            ErrorMessage = "Sign in to Vayme first.";
            return;
        }

        ErrorMessage = null;
        ConnectingProvider = provider;
        try
        {
            using var response = await PostAsync(startPath, accessToken, body: null);
            if (!response.IsSuccessStatusCode)
            {
                ErrorMessage = "Couldn't start the connection. Please try again.";
                return;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var authUrl = document.RootElement.TryGetProperty("authUrl", out var authUrlProperty)
                ? authUrlProperty.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(authUrl))
            {
                ErrorMessage = "Got an invalid sign-in link from the server.";
                return;
            }

            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            await PollStatusUntilConnectedAsync(provider);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Couldn't start the connection: {ex.Message}";
        }
        finally
        {
            ConnectingProvider = null;
        }
    }

    /// <summary>
    /// Disconnects a provider. The Worker deletes the stored tokens and revokes the grant,
    /// so this ends the connection everywhere — including on the Mac.
    /// </summary>
    public async Task DisconnectAsync(string provider)
    {
        var accessToken = await _authManager.CurrentAccessTokenAsync();
        if (accessToken == null) return;

        ErrorMessage = null;
        try
        {
            using var response = await PostAsync("/integrations/disconnect", accessToken, new { provider });
            if (!response.IsSuccessStatusCode)
            {
                ErrorMessage = "Couldn't disconnect. Please try again.";
                return;
            }

            var remaining = new HashSet<string>(_connectedProviders, StringComparer.OrdinalIgnoreCase);
            remaining.Remove(provider);
            SetConnectedProviders(remaining);
        }
        catch (Exception ex)
        {
            Logger.Log("Integrations", $"disconnect {provider} failed: {ex.Message}");
            ErrorMessage = "Couldn't disconnect. Please try again.";
        }
    }

    /// <summary>Re-checks status every ~2.5s for up to ~30s, stopping as soon as it connects.</summary>
    private async Task PollStatusUntilConnectedAsync(string provider)
    {
        for (int attempt = 0; attempt < ConnectPollAttempts; attempt++)
        {
            await Task.Delay(ConnectPollIntervalMilliseconds);
            await RefreshStatusAsync();
            if (IsConnected(provider)) return;
        }
    }

    // MARK: - Private

    private async Task<HttpResponseMessage> PostAsync(string path, string accessToken, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{NayfConfig.WorkerBaseURL}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        return await _httpClient.SendAsync(request);
    }

    /// <summary>
    /// Swaps in a new connected set, announcing the change only when it actually differs —
    /// the connect poll calls this every 2.5s and each notification re-lays out the panel.
    /// </summary>
    private void SetConnectedProviders(HashSet<string> providers)
    {
        if (providers.SetEquals(_connectedProviders)) return;
        _connectedProviders = providers;
        OnPropertyChanged(nameof(ConnectedProviders));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
