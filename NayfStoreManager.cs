using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>
/// One purchasable amount. Each exists twice with the payment provider — once as a one-off
/// and once as a monthly top-up — but that mapping lives on the server; the app only names
/// the amount and whether it repeats. Mirrors TokenBundle in NayfStoreManager.swift.
/// </summary>
/// <param name="Cents">
/// Price in cents, used for display, as the stable identity, and as what the Worker is
/// asked to charge.
/// </param>
public sealed record TokenBundle(int Cents, int Tokens)
{
    /// <summary>"$5.00" — always two decimals.</summary>
    public string FormattedPrice => NayfTokenPricing.FormatPrice(Cents);

    /// <summary>"1M" / "400k", matching how balances read elsewhere in the panel.</summary>
    public string FormattedTokens => NayfTokenPricing.FormatTokens(Tokens);
}

/// <summary>
/// The four amounts on sale, at a flat $5 per million tokens.
///
/// Fixed amounts rather than a typed one. An earlier design sold a single $0.01 unit and
/// set the payment provider's quantity to the number of cents, which let the user name any
/// amount — Paddle's API created those transactions but its checkout refused to render
/// them, so the amount is now baked into the price itself and nothing sends a quantity.
///
/// <b>No payment-provider price ids live here.</b> The app sends the AMOUNT the user picked
/// and the Worker maps it to whichever provider is active, so switching between Stripe and
/// Paddle is a Worker deploy rather than a signed app release. That is not hypothetical:
/// Paddle rejected vayme.io at domain review, and Stripe Managed Payments went in beside it.
///
/// To add an amount: create the pair of prices with the provider at $5/M, add a bundle
/// here, and mirror it in TOKEN_BUNDLES in the Worker, in NayfStoreManager.swift, and on
/// the website.
/// </summary>
public static class NayfTokenPricing
{
    public static readonly IReadOnlyList<TokenBundle> Bundles = new[]
    {
        new TokenBundle(  200,   400_000),
        new TokenBundle(  500, 1_000_000),
        new TokenBundle(1_000, 2_000_000),
        new TokenBundle(2_500, 5_000_000),
    };

    /// <summary>Pre-selected when the purchase page opens: $5, a round 1M tokens.</summary>
    public static TokenBundle DefaultBundle => Bundles[1];

    /// <summary>
    /// The bundle for an amount, falling back to the default one. A selection that isn't on
    /// sale can only come from a stale saved value, and a purchase page has to show
    /// something rather than nothing.
    /// </summary>
    public static TokenBundle BundleForCents(int cents)
    {
        foreach (var bundle in Bundles)
        {
            if (bundle.Cents == cents) return bundle;
        }
        return DefaultBundle;
    }

    /// <summary>An amount formatted for display — "$5.00", always two decimals.</summary>
    public static string FormatPrice(int cents)
        => string.Format(CultureInfo.InvariantCulture, "${0}.{1:00}", cents / 100, cents % 100);

    /// <summary>
    /// A token count in the same shape the rest of the panel shows balances — "1.5M" at or
    /// above a million, "400k" below it, so the row of amounts reads as one series rather
    /// than two. Invariant, because this sits beside a dollar amount and one string with two
    /// different decimal separators in it reads as a bug.
    /// </summary>
    public static string FormatTokens(int tokens)
        // "0.#" drops a trailing zero by itself, so a round million reads "1M", not "1.0M".
        => tokens >= 1_000_000
            ? (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
            : (tokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
}

/// <summary>An active monthly top-up, as reported by the Worker.</summary>
/// <param name="NextChargeAt">
/// When the next charge falls, if the provider told us. Once cancelled this is when it
/// stops instead.
/// </param>
/// <param name="CancelsAtPeriodEnd">
/// Already cancelled, but still running out the month it was paid for.
/// </param>
public sealed record ActiveTokenSubscription(
    int AmountCents, int? Tokens, DateTime? NextChargeAt, bool CancelsAtPeriodEnd)
{
    public string FormattedPrice => NayfTokenPricing.FormatPrice(AmountCents);

    /// <summary>"28 Sep" — short, since it sits inside a narrow panel row.</summary>
    public string? FormattedNextCharge
        => NextChargeAt?.ToString("d MMM", CultureInfo.CurrentCulture);
}

/// <summary>
/// The automatic acknowledgment of receipt returned by a withdrawal request. It confirms
/// the request arrived — deliberately not that it was approved.
/// </summary>
public sealed record WithdrawalAcknowledgment(string? Reference, string? ReceivedAt, string Message);

/// <summary>
/// Buys tokens, mirroring NayfStoreManager.swift. There is no in-app payment step: the
/// Worker creates a checkout with whichever payment provider is active, carrying the
/// user's Supabase id, and this opens the resulting page in the browser. That provider's
/// webhook credits the tokens, so the app never handles a card number or a receipt.
///
/// Also reports and cancels the monthly top-up, and submits right-of-withdrawal requests,
/// which the checkout page is required to offer next to the purchase itself.
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
    /// True once the browser has been sent to the checkout page. The panel swaps to a
    /// "finish in the browser" screen, because payment completes somewhere this app can't
    /// observe.
    /// </summary>
    public bool HasBrowserCheckoutOpen
    {
        get => _hasBrowserCheckoutOpen;
        private set { _hasBrowserCheckoutOpen = value; OnPropertyChanged(); }
    }

    // MARK: - Monthly top-up state

    private bool _isCancellingSubscription;
    /// <summary>True while the cancel request is in flight.</summary>
    public bool IsCancellingSubscription
    {
        get => _isCancellingSubscription;
        private set { _isCancellingSubscription = value; OnPropertyChanged(); }
    }

    private ActiveTokenSubscription? _activeSubscription;
    /// <summary>
    /// The user's active monthly top-up, or null if they have none. Null is also what a
    /// failed lookup leaves behind — a purchase screen should never break because this
    /// couldn't be fetched.
    /// </summary>
    public ActiveTokenSubscription? ActiveSubscription
    {
        get => _activeSubscription;
        private set { _activeSubscription = value; OnPropertyChanged(); }
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

    // MARK: - Monthly top-up

    /// <summary>
    /// Asks the Worker whether this user has an active monthly top-up.
    ///
    /// Deliberately silent on failure: the answer is decoration on the purchase screen, and
    /// a network hiccup shouldn't surface as an error banner over a page that otherwise
    /// works.
    /// </summary>
    public async Task RefreshSubscriptionStatusAsync()
    {
        try
        {
            var accessToken = await _authManager.CurrentAccessTokenAsync();
            if (accessToken == null) return;

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{NayfConfig.WorkerBaseURL}/subscription-status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            // The amount is what the row is built around, so no amount means nothing to
            // show even if the Worker called it active.
            if (!ReadBool(root, "active") || ReadInt(root, "amount_cents") is not { } amountCents)
            {
                ActiveSubscription = null;
                return;
            }

            ActiveSubscription = new ActiveTokenSubscription(
                AmountCents: amountCents,
                Tokens: ReadInt(root, "tokens"),
                NextChargeAt: ReadUnixSeconds(root, "next_charge_at"),
                CancelsAtPeriodEnd: ReadBool(root, "cancels_at_period_end"));
        }
        catch (Exception ex)
        {
            Logger.Log("Store", $"subscription-status failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the monthly top-up at the end of the period already paid for.
    ///
    /// Nothing is taken away: the current month keeps running and tokens already credited
    /// stay on the balance. The Worker finds the subscription from the caller's own token,
    /// so no identifier is sent from here.
    /// </summary>
    public async Task CancelSubscriptionAsync()
    {
        if (IsCancellingSubscription) return;
        IsCancellingSubscription = true;
        try
        {
            var accessToken = await _authManager.CurrentAccessTokenAsync();
            if (accessToken == null)
            {
                CheckoutError = "Sign in to Vayme to manage the monthly top-up.";
                return;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{NayfConfig.WorkerBaseURL}/cancel-subscription");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Log("Store", $"cancel-subscription returned HTTP {(int)response.StatusCode}");
                CheckoutError = "Couldn't cancel the monthly top-up. Please try again.";
                return;
            }

            // Re-read rather than assume, so the date shown is the one the provider holds.
            await RefreshSubscriptionStatusAsync();
        }
        catch (Exception ex)
        {
            Logger.Log("Store", $"cancel-subscription failed: {ex.Message}");
            CheckoutError = "Couldn't cancel the monthly top-up. Please try again.";
        }
        finally
        {
            IsCancellingSubscription = false;
        }
    }

    // MARK: - Checkout

    /// <summary>
    /// Asks the Worker for a checkout for this amount and opens it in the user's browser.
    /// Tokens are credited by the payment provider's webhook once payment clears, so
    /// nothing here waits for or confirms the payment.
    ///
    /// Only the amount is sent. Which provider handles it, which price id that maps to, and
    /// how many tokens it grants all live on the server, so nothing here can influence what
    /// gets credited.
    /// </summary>
    /// <param name="bundle">Which amount to buy.</param>
    /// <param name="repeatsMonthly">
    /// When true, buys the bundle's recurring price instead, so the same amount is topped
    /// up automatically every month until the user cancels.
    /// </param>
    public async Task OpenCheckoutAsync(TokenBundle bundle, bool repeatsMonthly)
    {
        IsCreatingCheckout = true;
        CheckoutError = null;
        try
        {
            var accessToken = await _authManager.CurrentAccessTokenAsync();
            if (accessToken == null)
            {
                CheckoutError = "Sign in to Vayme to buy tokens.";
                return;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{NayfConfig.WorkerBaseURL}/create-checkout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(
                    new { amount_cents = bundle.Cents, recurring = repeatsMonthly }),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.Log("Store", $"create-checkout returned HTTP {(int)response.StatusCode}");
                CheckoutError = $"Couldn't start checkout — {ErrorMessageFrom(body)}";
                return;
            }

            using var document = JsonDocument.Parse(body);
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

    /// <summary>Returns the page to the amounts, after "Back" or a finished purchase.</summary>
    public void ResetCheckoutState()
    {
        HasBrowserCheckoutOpen = false;
        CheckoutError = null;
    }

    // MARK: - Right of withdrawal

    /// <summary>
    /// Submits a right-of-withdrawal request and stores the acknowledgment of receipt.
    /// The refund itself is reviewed by us and issued through the payment provider — the
    /// acknowledgment only records that the request arrived.
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
                WithdrawalError = "Sign in to Vayme to submit a withdrawal request.";
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

    /// <summary>
    /// The message to show for a failed checkout. The Worker returns {"error": "..."} with
    /// wording already shaped for a human, so that is what the user sees — the raw envelope
    /// reached the panel as `Paddle error 500: {"error":…}` and buried the one part that
    /// actually said what went wrong.
    /// </summary>
    private static string ErrorMessageFrom(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var message = ReadString(document.RootElement, "error");
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        catch (JsonException)
        {
            // Not JSON at all — the body itself is the best description available.
        }
        return string.IsNullOrWhiteSpace(body) ? "unknown error" : body.Trim();
    }

    /// <summary>Reads an optional string field, treating JSON null as absent.</summary>
    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads an optional whole-number field, treating JSON null as absent.</summary>
    private static int? ReadInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
                ? number
                : null;

    /// <summary>Reads a boolean field, treating anything else — including absent — as false.</summary>
    private static bool ReadBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Reads a Unix timestamp in seconds, as the payment provider reports period ends, and
    /// returns it in the user's own time zone — the date is read against their calendar,
    /// not UTC's.
    /// </summary>
    private static DateTime? ReadUnixSeconds(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
                : null;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
