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
/// Tokens are sold by the amount spent rather than as named packs and plans. This replaced
/// a five-product catalogue (Starter / Standard / Pro packs plus Plus and Pro subscriptions)
/// that charged a different price per token at every size and needed five Paddle prices kept
/// in step with the Worker.
///
/// The user says how much they want to SPEND, so everything here works in CENTS — the
/// smallest whole unit money has. A Paddle price carries a quantity, so the quantity simply
/// is the amount in cents, which keeps $7.50 and $7.37 both exact with no snapping and no
/// rounding surprise. Mirrors NayfTokenPricing in NayfStoreManager.swift.
/// </summary>
public static class NayfTokenPricing
{
    /// <summary>
    /// Tokens credited per cent spent — a flat $5 per million at every amount. Must match
    /// TOKENS_PER_CENT in the Worker.
    /// </summary>
    public const int TokensPerCent = 2_000;

    /// <summary>
    /// Smallest purchase, in cents ($2). Mirrors MIN_CENTS_PER_CHECKOUT in the Worker, which
    /// rejects anything below it with a 400.
    /// </summary>
    public const int MinimumCents = 200;

    /// <summary>Largest purchase, in cents ($100). Mirrors MAX_CENTS_PER_CHECKOUT.</summary>
    public const int MaximumCents = 10_000;

    /// <summary>What the amount field starts on: $5, which buys a round 1M tokens.</summary>
    public const int DefaultCents = 500;

    /// <summary>
    /// The Paddle price ID for a one-off purchase. Must match the PADDLE_TOKEN_PRICES map in
    /// the Worker, and the value in NayfStoreManager.swift.
    /// </summary>
    public const string OneTimePriceId = "pri_01m1124hbdhem9fcwpxaqywm8r";

    /// <summary>
    /// The same unit billed monthly, used when the user turns on automatic top-up.
    /// </summary>
    public const string MonthlyPriceId = "pri_01m112m5cw8wk75rfxxam78k41";

    /// <summary>The shortcut amounts offered next to the field: $2 / $5 / $10 / $25.</summary>
    public static readonly IReadOnlyList<int> QuickPickCents = new[] { 200, 500, 1_000, 2_500 };

    /// <summary>Total tokens bought for a given amount.</summary>
    public static int Tokens(int cents) => TokensPerCent * cents;

    /// <summary>The amount formatted for display — "$7.50", always two decimals.</summary>
    public static string FormattedPrice(int cents)
        => string.Format(CultureInfo.InvariantCulture, "${0}.{1:00}", cents / 100, cents % 100);

    /// <summary>
    /// Token count in the same shape the rest of the panel shows balances — "1.5M" at or
    /// above a million, "400k" below it, so the shortcut row reads as one series rather than
    /// two. Invariant, because this sits beside a dollar amount and one string with two
    /// different decimal separators in it reads as a bug.
    /// </summary>
    public static string FormattedTokens(int cents)
    {
        int total = Tokens(cents);
        // "0.#" drops a trailing zero by itself, so a round million reads "1M", not "1.0M".
        return total >= 1_000_000
            ? (total / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
            : (total / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
    }

    /// <summary>
    /// Parses what the user typed into a whole number of cents, or null if it isn't a usable
    /// amount.
    ///
    /// Deliberately tolerant of how people actually type money: a leading "$", spaces, and —
    /// because this ships from Sweden — a comma decimal separator. <c>decimal</c> rather than
    /// <c>double</c>, so "7.50" cannot land on 749 cents through binary floating-point error.
    ///
    /// Range is NOT enforced here: the field has to accept "1" on the way to typing "12"
    /// without the text vanishing. <see cref="IsValid"/> is the gate.
    /// </summary>
    public static int? CentsFromTypedAmount(string typedAmount)
    {
        string normalized = (typedAmount ?? "").Replace("$", "").Replace(',', '.').Trim();
        if (normalized.Length == 0) return null;

        // Invariant with only a decimal point allowed: the comma above has already been
        // normalised into one, so letting the current culture read it as a group separator
        // would turn "7,50" into 750 dollars rather than 750 cents.
        if (!decimal.TryParse(
                normalized,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out decimal amount)
            || amount < 0)
        {
            return null;
        }

        // Round to the nearest cent, so someone pasting "7.499" gets 750 rather than a
        // rejection. Away from zero to match NSDecimalRound's .plain on the Mac.
        decimal cents = Math.Round(amount * 100, 0, MidpointRounding.AwayFromZero);
        if (cents > int.MaxValue) return null;
        return (int)cents;
    }

    /// <summary>Whether an amount is inside the range the Worker will accept.</summary>
    public static bool IsValid(int cents) => cents >= MinimumCents && cents <= MaximumCents;
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
    /// Asks the Worker for a Paddle checkout for this amount and opens it in the user's
    /// browser. Tokens are credited by Paddle's webhook once payment clears, so nothing
    /// here waits for or confirms the payment.
    ///
    /// The quantity sent is only a request. The Worker grants tokens from the quantity
    /// Paddle reports as actually paid, never from what the client asked for, so this call
    /// cannot inflate a balance.
    /// </summary>
    /// <param name="cents">How much to spend, in cents.</param>
    /// <param name="repeatsMonthly">
    /// When true, buys the recurring price instead, so the same amount is topped up
    /// automatically every month until the user cancels.
    /// </param>
    public async Task OpenCheckoutAsync(int cents, bool repeatsMonthly)
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

            // Clamped rather than trusted: the Worker rejects an out-of-range amount with a
            // 400, and a UI bug should not reach the user dressed as a payment failure.
            int centsToSpend = Math.Clamp(
                cents, NayfTokenPricing.MinimumCents, NayfTokenPricing.MaximumCents);
            string priceId = repeatsMonthly
                ? NayfTokenPricing.MonthlyPriceId
                : NayfTokenPricing.OneTimePriceId;

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{NayfConfig.WorkerBaseURL}/create-checkout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { price_id = priceId, quantity = centsToSpend }),
                Encoding.UTF8,
                "application/json");

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

    /// <summary>Returns the page to the amount field, after "Back" or a finished purchase.</summary>
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

    /// <summary>Reads an optional string field, treating JSON null as absent.</summary>
    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
