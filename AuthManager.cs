using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>
/// Handles Supabase email/password authentication by calling the GoTrue REST
/// API directly (no SDK dependency). Persists the session to disk so the user
/// stays logged in across launches, and transparently refreshes the access
/// token before it expires. Mirrors the Mac app's AuthManager.
///
/// The access token (a Supabase JWT) is forwarded to the Cloudflare proxy on
/// every chat/TTS request so the Worker can verify identity and credits.
/// </summary>
public sealed class AuthManager : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isAuthenticated;
    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set { _isAuthenticated = value; OnPropertyChanged(); }
    }

    private string? _currentUserEmail;
    public string? CurrentUserEmail
    {
        get => _currentUserEmail;
        private set { _currentUserEmail = value; OnPropertyChanged(); }
    }

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly string _sessionFilePath;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    public AuthManager()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        _sessionFilePath = AppPaths.InDataDirectory("auth.json");
    }

    // MARK: - Public operations

    /// <summary>
    /// Restores a persisted session on launch. If a refresh token is stored,
    /// exchanges it for a fresh access token. Returns true when the user ends
    /// up authenticated.
    /// </summary>
    public async Task<bool> RestoreSessionAsync()
    {
        try
        {
            // Both branches below end at the sign-in window, so record which one it
            // was: "never had a session" and "had one we couldn't use" look identical
            // from outside, and telling them apart is the whole diagnosis when
            // someone reports being asked to log in again.
            if (!File.Exists(_sessionFilePath))
            {
                Logger.Log("AuthManager", "No stored session on disk.");
                return false;
            }

            var json = await File.ReadAllTextAsync(_sessionFilePath);
            var stored = JsonSerializer.Deserialize<StoredSession>(json);
            if (stored?.RefreshToken is null)
            {
                Logger.Log("AuthManager", "Stored session has no refresh token.");
                return false;
            }

            _refreshToken = stored.RefreshToken;
            _accessToken = stored.AccessToken;
            _accessTokenExpiresAt = DateTimeOffset.FromUnixTimeSeconds(stored.ExpiresAt);
            CurrentUserEmail = stored.Email;

            // Force a refresh so we start with a known-good token.
            var refreshed = await RefreshAccessTokenAsync();
            IsAuthenticated = refreshed;
            return refreshed;
        }
        catch (Exception ex)
        {
            Logger.Log("AuthManager", $"RestoreSession failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Signs in with email + password. Throws AuthException on failure.</summary>
    public async Task SignInAsync(string email, string password)
    {
        var url = $"{NayfConfig.SupabaseURL}/auth/v1/token?grant_type=password";
        var body = JsonSerializer.Serialize(new { email, password });
        var session = await PostAuthAsync(url, body);
        ApplySession(session);
    }

    /// <summary>
    /// Creates a new account. If the project requires email confirmation, no
    /// session is returned — the caller should tell the user to check their
    /// inbox. Throws AuthException on failure.
    /// </summary>
    /// <returns>True if the user is signed in immediately; false if email
    /// confirmation is required.</returns>
    public async Task<bool> SignUpAsync(string email, string password)
    {
        var url = $"{NayfConfig.SupabaseURL}/auth/v1/signup";
        var body = JsonSerializer.Serialize(new { email, password });
        var session = await PostAuthAsync(url, body);

        if (string.IsNullOrEmpty(session.AccessToken))
            return false; // email confirmation required — not signed in yet

        ApplySession(session);
        return true;
    }

    public void SignOut()
    {
        _accessToken = null;
        _refreshToken = null;
        _accessTokenExpiresAt = DateTimeOffset.MinValue;
        CurrentUserEmail = null;
        IsAuthenticated = false;
        try { if (File.Exists(_sessionFilePath)) File.Delete(_sessionFilePath); }
        catch (Exception ex) { Logger.Log("AuthManager", $"SignOut cleanup failed: {ex.Message}"); }
    }

    /// <summary>
    /// Returns a currently-valid access token, refreshing it first if it has
    /// expired (or is about to). Returns null when the user is not logged in.
    /// </summary>
    public async Task<string?> CurrentAccessTokenAsync()
    {
        if (_accessToken != null && DateTimeOffset.UtcNow < _accessTokenExpiresAt.AddSeconds(-60))
            return _accessToken;

        if (_refreshToken == null) return null;

        return await RefreshAccessTokenAsync() ? _accessToken : null;
    }

    // MARK: - Private

    private async Task<bool> RefreshAccessTokenAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            // Another caller may have refreshed while we waited.
            if (_accessToken != null && DateTimeOffset.UtcNow < _accessTokenExpiresAt.AddSeconds(-60))
                return true;

            if (_refreshToken == null) return false;

            var url = $"{NayfConfig.SupabaseURL}/auth/v1/token?grant_type=refresh_token";
            var body = JsonSerializer.Serialize(new { refresh_token = _refreshToken });
            var session = await PostAuthAsync(url, body);
            if (!ApplySession(session))
                throw new AuthException("The server returned a session without tokens.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log("AuthManager", $"Token refresh failed: {ex.Message}");

            // Only sign out when the server actually refuses the token. A timeout,
            // a DNS failure or a 5xx says nothing about whether it is still good —
            // and SignOut deletes the stored session, so treating those as a
            // rejection means opening Vayme before the network is up costs the user
            // their login permanently. Leave the session alone and try again later.
            if (IsTokenRejection(ex))
            {
                Logger.Log("AuthManager", "Refresh token rejected — signing out.");
                SignOut();
            }

            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// True only when Supabase itself turned the refresh token down. 408 and 429 are
    /// excluded: they are the server asking us to come back later, not a verdict on
    /// the token.
    /// </summary>
    private static bool IsTokenRejection(Exception ex)
        => ex is AuthException { StatusCode: { } status } &&
           (int)status is >= 400 and < 500 and not 408 and not 429;

    private async Task<SessionResponse> PostAuthAsync(string url, string jsonBody)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("apikey", NayfConfig.SupabaseAnonKey);

        using var response = await _httpClient.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AuthException(ParseErrorMessage(responseText, response.StatusCode),
                response.StatusCode);

        var session = JsonSerializer.Deserialize<SessionResponse>(responseText);
        return session ?? throw new AuthException("Unexpected empty response from the server.");
    }

    /// <summary>
    /// Adopts and persists a session from the server. Returns false — without
    /// touching the current one — if the response carried no usable tokens.
    /// </summary>
    private bool ApplySession(SessionResponse session)
    {
        if (string.IsNullOrEmpty(session.AccessToken) || string.IsNullOrEmpty(session.RefreshToken))
            return false;

        _accessToken = session.AccessToken;
        _refreshToken = session.RefreshToken;
        // expires_in is seconds from now; fall back to 1h if absent.
        var lifetime = session.ExpiresIn > 0 ? session.ExpiresIn : 3600;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);
        CurrentUserEmail = session.User?.Email;
        IsAuthenticated = true;

        PersistSession();
        return true;
    }

    private void PersistSession()
    {
        try
        {
            var stored = new StoredSession
            {
                AccessToken = _accessToken,
                RefreshToken = _refreshToken,
                ExpiresAt = _accessTokenExpiresAt.ToUnixTimeSeconds(),
                Email = CurrentUserEmail
            };
            File.WriteAllText(_sessionFilePath, JsonSerializer.Serialize(stored));
        }
        catch (Exception ex)
        {
            Logger.Log("AuthManager", $"Persist failed: {ex.Message}");
        }
    }

    private static string ParseErrorMessage(string responseText, System.Net.HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            // GoTrue returns either {error_description}, {msg}, or {error}.
            foreach (var key in new[] { "error_description", "msg", "error", "message" })
            {
                if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var raw = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(raw)) return Friendly(raw!);
                }
            }
        }
        catch { /* not JSON — fall through */ }

        return $"Authentication failed ({(int)status}).";
    }

    private static string Friendly(string raw)
    {
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("invalid login credentials")) return "Invalid email or password.";
        if (lower.Contains("email not confirmed")) return "Check your inbox and confirm your email first.";
        if (lower.Contains("already registered") || lower.Contains("already been registered"))
            return "An account with this email already exists.";
        if (lower.Contains("password") && lower.Contains("at least"))
            return "Password must be at least 6 characters.";
        if (lower.Contains("unable to validate email") || lower.Contains("invalid email"))
            return "Please enter a valid email address.";
        return raw;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // MARK: - DTOs

    private sealed class SessionResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
        [JsonPropertyName("user")] public UserDto? User { get; set; }
    }

    private sealed class UserDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
    }

    private sealed class StoredSession
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public long ExpiresAt { get; set; }
        public string? Email { get; set; }
    }
}

/// <summary>Thrown when an auth operation fails, carrying a user-facing message.</summary>
public sealed class AuthException : Exception
{
    /// <summary>The HTTP status the server replied with, when there was one.</summary>
    public System.Net.HttpStatusCode? StatusCode { get; }

    public AuthException(string message, System.Net.HttpStatusCode? statusCode = null)
        : base(message) => StatusCode = statusCode;
}
