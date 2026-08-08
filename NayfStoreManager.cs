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

/// <summary>A product the user can buy, priced and fulfilled by Paddle.</summary>
/// <param name="Id">
/// The Paddle price ID. It must exist in the Worker's PADDLE_PRICE_GRANTS map, which is
/// what decides how many tokens the purchase grants — the counts below are display only.
/// </param>
public sealed record PaddleProduct(
    string Id,
    string DisplayName,
    string DisplayPrice,
    int TokenCount,
    bool IsSubscription);

/// <summary>
/// The catalogue, mirroring NayfStoreManager.swift and the Worker's price map. Prices are
/// shown as strings rather than computed, because Paddle localises what the user actually
/// pays at checkout and any number formatted here would only be an approximation of it.
/// </summary>
public static class NayfPaddleProducts
{
    /// <summary>Highlighted in each section as the one most people want.</summary>
    public const string RecommendedSubscriptionId = "pri_01ksm2zm62mzzbdhys0np52cwd"; // Nayf Plus
    public const string RecommendedPackId = "pri_01ksm2wmk1sednv65vh4733fg0";         // Standard Pack

    public static readonly IReadOnlyList<PaddleProduct> Subscriptions = new[]
    {
        new PaddleProduct(RecommendedSubscriptionId, "Nayf Plus", "$5.04/mo", 1_000_000, true),
        new PaddleProduct("pri_01ksm30vg4a13jg6e50x981d7v", "Nayf Pro", "$13.12/mo", 3_000_000, true),
    };

    public static readonly IReadOnlyList<PaddleProduct> TokenPacks = new[]
    {
        new PaddleProduct("pri_01ksm2sptqr47rsr49whxk31dk", "Starter Pack", "$1.01", 150_000, false),
        new PaddleProduct(RecommendedPackId, "Standard Pack", "$3.05", 500_000, false),
        new PaddleProduct("pri_01ksm2y0fb1bp1tqkqmrd8t43j", "Pro Pack", "$8.15", 1_500_000, false),
    };
}

/// <summary>
/// The automatic acknowledgment of receipt returned by a withdrawal request. It confirms
/// the request arrived — deliberately not that it was approved.
/// </summary>
public sealed record WithdrawalAcknowledgment(string? Reference, string? ReceivedAt, string Message);

/// <summary>
/// Buys tokens through Paddle, mirroring NayfStoreManager.swift. There is no in-app
/// payment step: the Worker creates a Paddle transaction with the user's Supabase id in
/// custom_data, and this opens the resulting checkout in the browser. Paddle's webhook
/// credits the tokens, so the app never handles a card number or a purchase receipt.
///
/// Also submits right-of-withdrawal requests, which the checkout page is required to
/// offer next to the purchase itself.
/// </summary>
public sealed class NayfStoreManager : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly AuthManager _authManager;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public NayfStoreManager(AuthManager authManager)
    {
        _authManager = authManager;
    }

    // MARK: - Checkout state

    private bool _isCreatingCheckout;
    /// <summary>True while the Worker is being asked for a checkout URL.</summary>
    public bool IsCreatingCheckout
    {
        get => _isCreatingCheckout;
        private set { _isCreatingCheckout = value; OnPropertyChanged(); }
    }

    private string? _checkoutError;
    public string? CheckoutError
    {
        get => _checkoutError;
        private set { _checkoutError = value; OnPropertyChanged(); }
    }

    private bool _hasBrowserCheckoutOpen;
    /// <summary>
    /// True once the browser has been sent to Paddle. The page swaps to a "finish in the
    /// browser" screen, because payment completes somewhere this app can't observe.
    /// </summary>
    public bool HasBrowserCheckoutOpen
    {
        get => _hasBrowserCheckoutOpen;
        private set { _hasBrowserCheckoutOpen = value; OnPropertyChanged(); }
    }

    // MARK: - Withdrawal state

    private bool _isSubmittingWithdrawal;
    public bool IsSubmittingWithdrawal
    {
        get => _isSubmittingWithdrawal;
        private set { _isSubmittingWithdrawal = value; OnPropertyChanged(); }
    }

    private string? _withdrawalError;
    public string? WithdrawalError
    {
        get => _withdrawalError;
        private set { _withdrawalError = value; OnPropertyChanged(); }
    }

    private WithdrawalAcknowledgment? _withdrawalAcknowledgment;
    public WithdrawalAcknowledgment? WithdrawalAcknowledgment
    {
        get => _withdrawalAcknowledgment;
        private set { _withdrawalAcknowledgment = value; OnPropertyChanged(); }
    }

    // MARK: - Checkout

    /// <summary>
    /// Asks the Worker for a Paddle checkout for this product and opens it in the user's
    /// browser. Tokens are credited by Paddle's webhook once payment clears, so nothing
    /// here waits for or confirms the payment.
    /// </summary>
    public async Task OpenCheckoutAsync(PaddleProduct product)
    {
        IsCreatingCheckout = true;
        CheckoutError = null;
        try
        {
            var accessToken = await _authManager.CurrentAccessTokenAsync();
            if (accessToken == null)
            {
                CheckoutError = "Sign in to Nayf to buy tokens.";
                return;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{NayfConfig.WorkerBaseURL}/create-checkout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { price_id = product.Id }), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Log("Store", $"create-checkout returned HTTP {(int)response.StatusCode}");
                CheckoutError = "Couldn't start the checkout. Please try again.";
                return;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var checkoutUrl = document.RootElement.TryGetProperty("checkout_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(checkoutUrl))
            {
                CheckoutError = "Got an invalid checkout link from the server.";
                return;
            }

            Process.Start(new ProcessStartInfo(checkoutUrl) { UseShellExecute = true });
            HasBrowserCheckoutOpen = true;
        }
        catch (Exception ex)
        {
            Logger.Log("Store", $"create-checkout failed: {ex.Message}");
            CheckoutError = $"Couldn't start the checkout: {ex.Message}";
        }
        finally
        {
            IsCreatingCheckout = false;
        }
    }

    /// <summary>Returns the page to the plan list, after "Back to plans" or a finished purchase.</summary>
    public void ResetCheckoutState()
    {
        HasBrowserCheckoutOpen = false;
        CheckoutError = null;
    }

    // MARK: - Right of withdrawal

    /// <summary>
    /// Submits a right-of-withdrawal request and stores the acknowledgment of receipt.
    /// The refund itself is reviewed by us and issued through Paddle — the acknowledgment
    /// only records that the request arrived.
    /// </summary>
    public async Task RequestWithdrawalAsync(string? reason = null, string? priceId = null)
    {
        IsSubmittingWithdrawal = true;
        WithdrawalError = null;
        try
        {
            var accessToken = await _authManager.CurrentAccessTokenAsync();
            if (accessToken == null)
            {
                WithdrawalError = "Sign in to Nayf to submit a withdrawal request.";
                return;
            }

            var payload = new Dictionary<string, string>();
            if (reason != null) payload["reason"] = reason;
            if (priceId != null) payload["price_id"] = priceId;

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{NayfConfig.WorkerBaseURL}/withdraw-request");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            bool acknowledged = response.IsSuccessStatusCode
                && root.TryGetProperty("acknowledged", out var flag)
                && flag.ValueKind == JsonValueKind.True;

            if (!acknowledged)
            {
                Logger.Log("Store", $"withdraw-request returned HTTP {(int)response.StatusCode}");
                WithdrawalError = "Couldn't submit your request. Please try again.";
                return;
            }

            WithdrawalAcknowledgment = new WithdrawalAcknowledgment(
                Reference: ReadString(root, "reference"),
                ReceivedAt: ReadString(root, "received_at"),
                Message: ReadString(root, "message") ?? "We've received your withdrawal request.");
        }
        catch (Exception ex)
        {
            Logger.Log("Store", $"withdraw-request failed: {ex.Message}");
            WithdrawalError = "Couldn't submit your request. Please try again.";
        }
        finally
        {
            IsSubmittingWithdrawal = false;
        }
    }

    /// <summary>Clears the acknowledgment and error, returning the control to its button.</summary>
    public void ResetWithdrawalState()
    {
        WithdrawalAcknowledgment = null;
        WithdrawalError = null;
    }

    // MARK: - Private

    /// <summary>Reads an optional string field, treating JSON null as absent.</summary>
    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
