using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using WinRT;
using WinRT.Interop;

namespace NayfWindows;

/// <summary>
/// The companion panel window — a floating dropdown that appears when the
/// user clicks the system tray icon. Shows voice state, last transcript,
/// streaming response, model selector, and settings.
/// Mirrors CompanionPanelView.swift + MenuBarPanelManager.swift.
/// </summary>
public sealed partial class CompanionPanelWindow : Window
{
    private readonly CompanionManager _companionManager;
    private bool _isVisible = false;
    private DateTimeOffset _lastHidden = DateTimeOffset.MinValue;
    private int _anchorCenterX;
    private NativeMethods.POINT _anchorPoint;
    private ScaleTransform? _contentScale;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;

    public bool IsPanelVisible => _isVisible;

    /// <summary>Raised when the user clicks Sign Out; the app handles the flow.</summary>
    public event Action? SignOutRequested;

    public CompanionPanelWindow(CompanionManager companionManager)
    {
        _companionManager = companionManager;
        InitializeComponent();
        SetupWindow();
        SubscribeToCompanionManager();

        _companionManager.Auth.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AuthManager.CurrentUserEmail))
                DispatcherQueue.TryEnqueue(() => UpdateAccountEmail(_companionManager.Auth.CurrentUserEmail));
        };
        UpdateAccountEmail(_companionManager.Auth.CurrentUserEmail);

        // Connection state can land while the panel is closed (the connect flow finishes
        // in the browser), so the page redraws from the manager rather than from whatever
        // it last showed.
        _companionManager.Integrations.PropertyChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateConnectionsView);
        UpdateConnectionsView();

        // Same reasoning for checkout: it finishes on Paddle's page in the browser,
        // which took the focus and closed the panel on the way there.
        _companionManager.Store.PropertyChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateTokensView);
        BuildProductCards();
        UpdateTokensView();

        UpdateModelSelection(_companionManager.ActiveModel);
        UpdateCreditBalance();
        UpdateCursorColorSelection(_companionManager.SelectedCursorColor);
        UpdateVoiceStateUI(_companionManager.VoiceState);
        UpdateVersionText();
        ShowPage(PanelPage.Home);

        // Memory tab: bind the list and toggle the empty state as it changes.
        MemoryList.ItemsSource = _companionManager.Memory.Memories;
        _companionManager.Memory.Memories.CollectionChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateMemoryView);
        UpdateMemoryView();

        // Agent task UI: bind the live step list and react to confirmation prompts.
        AgentStepsList.ItemsSource = _companionManager.AgentManager.AgentSteps;
        _companionManager.AgentManager.AgentSteps.CollectionChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateAgentTaskVisibility);
        _companionManager.AgentManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NayfAgentManager.PendingConfirmationRequest))
                DispatcherQueue.TryEnqueue(UpdateAgentConfirmation);
        };

        // Re-fit the window whenever the content's height changes (e.g. a
        // response appears) so there's never empty space or clipping.
        ContentStack.SizeChanged += (_, _) => FitWindowToContent();

        // Entrance transform: the panel unfolds upward out of the taskbar
        // (scale anchored at the bottom-center + fade).
        _contentScale = new ScaleTransform();
        ContentStack.RenderTransform = _contentScale;
        ContentStack.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 1);
    }

    /// <summary>Plays the scale-up + fade-in entrance, like the panel unfolding from the notch.</summary>
    private void PlayUnfoldAnimation()
    {
        if (_contentScale == null) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var sb = new Storyboard();

        void Add(DependencyObject target, string property, double from, double to, double ms)
        {
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = ease,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);
            sb.Children.Add(anim);
        }

        Add(_contentScale, "ScaleY", 0.86, 1.0, 260);
        Add(_contentScale, "ScaleX", 0.96, 1.0, 260);
        Add(ContentStack, "Opacity", 0.0, 1.0, 200);
        sb.Begin();
    }

    private void UpdateAccountEmail(string? email)
    {
        bool signedIn = !string.IsNullOrWhiteSpace(email);
        AccountEmailText.Text = signedIn ? email : "Not signed in";
        SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetupWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);

        // Tool window (no taskbar button, no Alt+Tab). WS_EX_NOACTIVATE is
        // deliberately omitted — the panel needs to activate so the acrylic
        // material renders and it dismisses on blur, like the Start menu.
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW);

        // Remove title bar using AppWindow presenter
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = false;

        // Dark-mode rounded appearance
        int darkModeValue = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */,
            ref darkModeValue, sizeof(int));

        if (Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;

        // Frosted, translucent acrylic. Base is the Start-menu material but is
        // nearly opaque, so the tint is dialled back by hand to let more of the
        // desktop through. The tint stays a dark near-black rather than switching
        // to the Thin kind, which washes out to a light grey.
        // NOTE: assigning Kind resets the tint properties, so it must come first.
        if (DesktopAcrylicController.IsSupported())
        {
            _backdropConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Dark
            };
            _acrylicController = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
            _acrylicController.TintColor = Windows.UI.Color.FromArgb(255, 24, 24, 27);
            _acrylicController.TintOpacity = 0.50f;       // how strongly the tint stains
            _acrylicController.LuminosityOpacity = 0.78f; // lower = more background shows
            _acrylicController.FallbackColor = Windows.UI.Color.FromArgb(255, 28, 28, 30);
            _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
        }
        else
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        // Dismiss when the panel loses focus (clicking elsewhere), like Start.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                HidePanel();
        };

        // Hide initially
        appWindow.Hide();
    }

    private void SubscribeToCompanionManager()
    {
        _companionManager.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(CompanionManager.VoiceState):
                    UpdateVoiceStateUI(_companionManager.VoiceState);
                    break;
                case nameof(CompanionManager.LastTranscript):
                    UpdateLastTranscript(_companionManager.LastTranscript);
                    break;
                case nameof(CompanionManager.StreamingResponseText):
                    UpdateResponseText(_companionManager.StreamingResponseText);
                    break;
                case nameof(CompanionManager.MicrophonePermissionNeeded):
                    UpdateMicPermissionBanner(_companionManager.MicrophonePermissionNeeded);
                    break;
                case nameof(CompanionManager.TokenBalanceText):
                case nameof(CompanionManager.IsOutOfCredits):
                    UpdateCreditBalance();
                    break;
                case nameof(CompanionManager.ActiveModel):
                    UpdateModelSelection(_companionManager.ActiveModel);
                    break;
            }
        };
    }

    private void UpdateVoiceStateUI(CompanionVoiceState state)
    {
        (string label, Windows.UI.Color color) = state switch
        {
            CompanionVoiceState.Listening => ("Listening", Windows.UI.Color.FromArgb(255, 0, 122, 255)),
            CompanionVoiceState.Processing => ("Processing", Windows.UI.Color.FromArgb(255, 255, 159, 10)),
            CompanionVoiceState.Responding => ("Responding", Windows.UI.Color.FromArgb(255, 52, 199, 89)),
            _ => ("Active", Windows.UI.Color.FromArgb(255, 52, 199, 89))
        };

        HeaderStatusText.Text = label;
        StatusDot.Fill = new SolidColorBrush(color);

        // Mic button: red while listening, otherwise the cursor accent color.
        bool listening = state == CompanionVoiceState.Listening;
        bool busy = state == CompanionVoiceState.Processing || state == CompanionVoiceState.Responding;
        TapToTalkLabel.Text = listening ? "Listening…" : busy ? "Thinking…" : "Tap to talk";

        var accent = listening ? Windows.UI.Color.FromArgb(255, 255, 102, 97) : AccentColor();
        MicGlyph.Foreground = new SolidColorBrush(accent);
        MicCircle.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
    }

    /// <summary>The current cursor color, as a Windows.UI.Color, for accenting the panel.</summary>
    private Windows.UI.Color AccentColor()
    {
        var c = _companionManager.SelectedCursorColor.ToDrawingColor();
        return Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
    }

    private void UpdateLastTranscript(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            LastTranscriptText.Visibility = Visibility.Collapsed;
        }
        else
        {
            LastTranscriptText.Text = $"You: {transcript}";
            LastTranscriptText.Visibility = Visibility.Visible;
        }
    }

    private void UpdateResponseText(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            ResponseContainer.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResponseDisplayText.Text = responseText;
            ResponseContainer.Visibility = Visibility.Visible;
        }
    }

    private void UpdateMicPermissionBanner(bool needed)
    {
        MicPermissionBanner.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAgentTaskVisibility()
    {
        AgentTaskContainer.Visibility = _companionManager.AgentManager.AgentSteps.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAgentConfirmation()
    {
        var request = _companionManager.AgentManager.PendingConfirmationRequest;
        if (request == null)
        {
            AgentConfirmContainer.Visibility = Visibility.Collapsed;
        }
        else
        {
            AgentConfirmCommand.Text = request.CommandText;
            AgentConfirmReason.Text = request.CommandDescription;
            AgentConfirmContainer.Visibility = Visibility.Visible;
        }
    }

    private void AgentApproveButton_Click(object sender, RoutedEventArgs e)
        => _companionManager.AgentManager.ApproveConfirmation(true);

    private void AgentDenyButton_Click(object sender, RoutedEventArgs e)
        => _companionManager.AgentManager.ApproveConfirmation(false);

    private void UpdateCreditBalance()
    {
        CreditBalanceText.Text = _companionManager.TokenBalanceText;
        bool out_ = _companionManager.IsOutOfCredits;
        CreditBalanceText.Foreground = new SolidColorBrush(out_
            ? Windows.UI.Color.FromArgb(255, 255, 69, 58)    // red when empty
            : Windows.UI.Color.FromArgb(255, 142, 142, 147)); // gray otherwise
        TopUpButton.Content = out_ ? "Get more" : "Top up";

        // The tokens page repeats the balance in its own words, since by then the
        // footer that carried it has scrolled out of the user's attention.
        TokenBalancePillText.Text = _companionManager.TokenBalance is null
            ? "Loading tokens…"
            : out_ ? "You have no tokens remaining"
                   : $"You have {_companionManager.TokenBalanceText} remaining";
    }

    /// <summary>
    /// Shows the panel positioned above the tray icon. Mirrors MenuBarPanelManager's
    /// showPanel() which positions the panel below the menu bar icon on Mac.
    /// On Windows, the taskbar is at the bottom so we show it above the tray rect.
    /// </summary>
    public void ShowNearTray(NativeMethods.RECT trayRect)
    {
        if (_isVisible) { HidePanel(); return; }

        // If the panel just blur-dismissed (e.g. the same click that closed it
        // is what hit the tray icon), treat this as a close, not a reopen.
        if ((DateTimeOffset.UtcNow - _lastHidden).TotalMilliseconds < 250) return;

        ShowPanelAt(trayRect);
    }

    /// <summary>
    /// Shows the panel above the tray icon if it isn't already visible, without
    /// toggling it closed. Used to surface the panel automatically (e.g. when
    /// the microphone permission banner needs the user's attention).
    /// </summary>
    /// <returns>True if the panel was shown; false if it was already visible or
    /// was just dismissed (so the caller can treat the click as a close).</returns>
    public bool EnsureVisibleNearTray(NativeMethods.RECT trayRect)
    {
        if (_isVisible) return false;
        if ((DateTimeOffset.UtcNow - _lastHidden).TotalMilliseconds < 250) return false;
        ShowPanelAt(trayRect);
        return true;
    }

    private void ShowPanelAt(NativeMethods.RECT trayRect)
    {
        // Anchor on the clicked icon (tray or taskbar). Keep the full point so we
        // can pick the monitor it's on; fall back to the cursor, then primary.
        if (trayRect.Right > trayRect.Left)
        {
            _anchorPoint = new NativeMethods.POINT
            {
                X = (trayRect.Left + trayRect.Right) / 2,
                Y = (trayRect.Top + trayRect.Bottom) / 2
            };
        }
        else if (NativeMethods.GetCursorPos(out var cursor))
        {
            _anchorPoint = cursor;
        }
        _anchorCenterX = _anchorPoint.X;
        ShowPanelCore();
    }

    private void ShowPanelCore()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Show();
        Activate(); // take focus so the acrylic renders active and blur-dismiss works
        _isVisible = true;

        // Size + position the window to fit the content height.
        FitWindowToContent();

        // Unfold from the notch with a scale-up + fade entrance.
        PlayUnfoldAnimation();
    }

    /// <summary>
    /// Resizes the window to exactly fit the content stack and re-anchors it
    /// above the taskbar, centered on the icon that opened it. Runs on show and
    /// whenever the content height changes.
    /// </summary>
    private void FitWindowToContent()
    {
        if (!_isVisible) return;

        double dipWidth = ContentStack.ActualWidth > 0 ? ContentStack.ActualWidth : ContentStack.Width;
        double dipHeight = ContentStack.ActualHeight;
        if (dipWidth <= 0 || dipHeight <= 0) return;

        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int w = (int)Math.Ceiling(dipWidth * scale);
        int h = (int)Math.Ceiling((dipHeight + 1) * scale);

        const int margin = 12;
        // Use the work area of the monitor the icon was clicked on, so the panel
        // appears on that monitor (not always the primary one).
        var workArea = GetWorkAreaForPoint(_anchorPoint);

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        // ResizeClient sizes the *client* area to the content so the window
        // border doesn't eat into the padding (which made the right edge tight).
        // The window then has an invisible resize frame around that, so we read
        // the real outer size afterward to position it above the taskbar.
        appWindow.ResizeClient(new Windows.Graphics.SizeInt32(w, h));
        NativeMethods.GetWindowRect(hwnd, out var outer);
        int outerW = outer.Right - outer.Left;
        int outerH = outer.Bottom - outer.Top;

        int x = Math.Clamp(_anchorCenterX - outerW / 2,
            workArea.Left + margin,
            Math.Max(workArea.Left + margin, workArea.Right - outerW - margin));
        int y = workArea.Bottom - outerH - margin;

        appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    /// <summary>
    /// Returns the work area (screen minus taskbar) of the monitor containing
    /// <paramref name="pt"/>, falling back to the primary monitor.
    /// </summary>
    private static NativeMethods.RECT GetWorkAreaForPoint(NativeMethods.POINT pt)
    {
        IntPtr hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
        };
        if (hMon != IntPtr.Zero && NativeMethods.GetMonitorInfo(hMon, ref info))
            return info.rcWork;

        var workArea = new NativeMethods.RECT();
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref workArea, 0);
        return workArea;
    }

    public void HidePanel()
    {
        if (!_isVisible) return;
        AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                WindowNative.GetWindowHandle(this))).Hide();
        _isVisible = false;
        _lastHidden = DateTimeOffset.UtcNow;
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.Exit();
    }

    /// <summary>
    /// Shows which tier Nayf routed the last turn to. The model is chosen
    /// automatically (screen-coordinate turns get the high-res vision tier), so this
    /// is a read-only indicator rather than a picker.
    /// </summary>
    private void UpdateModelSelection(string modelId) =>
        ActiveModelText.Text = modelId switch
        {
            NayfConfig.ScreenModel => "Opus",
            NayfConfig.LightModel => "Sonnet",
            _ => "Auto"
        };

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _companionManager.ClearConversationHistory();
        // Clearing produces no visible change on its own, so say it happened.
        ClearHistoryCaption.Text = "Cleared — Nayf is starting fresh";
    }

    /// <summary>
    /// Shows the running build's version. Read from the executable rather than
    /// hard-coded so it can't drift from what the user actually has installed.
    /// </summary>
    private void UpdateVersionText()
    {
        string version = typeof(CompanionPanelWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = $"Nayf for Windows · {version}";
    }

    // Set while the toggle is being synced from the registry, so the resulting
    // Toggled event isn't mistaken for the user flipping the switch.
    private bool _syncingStartupToggle;

    private void RefreshStartupToggle()
    {
        _syncingStartupToggle = true;
        StartupToggle.IsOn = NayfStartup.IsEnabled;
        _syncingStartupToggle = false;
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingStartupToggle) return;

        // If Windows rejected the change, snap back rather than leave the switch
        // showing a setting that isn't actually in effect.
        if (!NayfStartup.SetEnabled(StartupToggle.IsOn))
            RefreshStartupToggle();
    }

    private void OpenSpeechSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("ms-settings:privacy-speech") { UseShellExecute = true });
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
        SignOutRequested?.Invoke();
    }

    private void TopUpButton_Click(object sender, RoutedEventArgs e) => ShowPage(PanelPage.Tokens);

    private void CursorColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag &&
            Enum.TryParse<NayfCursorColor>(tag, out var color))
        {
            _companionManager.SelectedCursorColor = color;
            UpdateCursorColorSelection(color);
            UpdateVoiceStateUI(_companionManager.VoiceState); // re-accent the mic
        }
    }

    private enum PanelPage { Home, Memory, Connections, Tokens, Settings }

    private void BackButton_Click(object sender, RoutedEventArgs e) => ShowPage(PanelPage.Home);
    private void MemoryButton_Click(object sender, RoutedEventArgs e) => ShowPage(PanelPage.Memory);
    private void ConnectAppsButton_Click(object sender, RoutedEventArgs e) => ShowPage(PanelPage.Connections);
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowPage(PanelPage.Settings);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => HidePanel();
    private void TapToTalkButton_Click(object sender, RoutedEventArgs e) => _companionManager.ToggleTapToTalk();


    /// <summary>
    /// Starts the Google consent flow. Handing focus to the browser blur-dismisses the
    /// panel, so this deliberately isn't awaited — the manager keeps polling for the
    /// result on its own, and the page shows the finished state when reopened.
    /// </summary>
    private void ConnectCalendarButton_Click(object sender, RoutedEventArgs e)
        => _ = _companionManager.Integrations.ConnectGoogleAsync();

    private void DisconnectCalendarButton_Click(object sender, RoutedEventArgs e)
        => _ = _companionManager.Integrations.DisconnectAsync(NayfIntegrationsManager.GoogleProvider);

    private void ConnectGitHubButton_Click(object sender, RoutedEventArgs e)
        => _ = _companionManager.Integrations.ConnectGitHubAsync();

    private void DisconnectGitHubButton_Click(object sender, RoutedEventArgs e)
        => _ = _companionManager.Integrations.DisconnectAsync(NayfIntegrationsManager.GitHubProvider);

    /// <summary>
    /// Paints the Connections page from the manager's state. Nothing here is assumed —
    /// "connected" comes from the Worker, which stores the tokens against the signed-in
    /// account, so a provider connected on another device already reads as connected.
    /// </summary>
    private void UpdateConnectionsView()
    {
        var integrations = _companionManager.Integrations;

        UpdateProviderCard(
            NayfIntegrationsManager.GoogleProvider,
            idleSubtitle: "Read your schedule and create events",
            brandColor: Windows.UI.Color.FromArgb(255, 0x42, 0x85, 0xF4),
            CalendarIconBackground, CalendarIcon, CalendarConnectedDot, CalendarStatusText,
            ConnectCalendarButton, DisconnectCalendarButton);

        UpdateProviderCard(
            NayfIntegrationsManager.GitHubProvider,
            idleSubtitle: "Issues and pull requests",
            brandColor: Windows.UI.Color.FromArgb(255, 0xD5, 0xD5, 0xDA),
            GitHubIconBackground, GitHubIcon, GitHubConnectedDot, GitHubStatusText,
            ConnectGitHubButton, DisconnectGitHubButton);

        bool hasError = !string.IsNullOrWhiteSpace(integrations.ErrorMessage);
        ConnectionsErrorText.Text = integrations.ErrorMessage ?? "";
        ConnectionsErrorText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;

        // Say so on the home screen too, so the user doesn't have to open the page to
        // find out whether anything is connected.
        ConnectAppsLabel.Text = integrations.HasAnyConnection ? "Connected apps" : "Connect apps";

        FitWindowToContent();
    }

    /// <summary>
    /// Drives one provider card through its Connect / Connecting / Connected states. Both
    /// cards behave identically, so they share this rather than each keeping its own copy
    /// that could drift.
    /// </summary>
    private void UpdateProviderCard(
        string provider,
        string idleSubtitle,
        Windows.UI.Color brandColor,
        Border iconBackground,
        FontIcon icon,
        FrameworkElement connectedDot,
        TextBlock statusText,
        Button connectButton,
        Button disconnectButton)
    {
        var integrations = _companionManager.Integrations;
        bool connected = integrations.IsConnected(provider);
        bool connecting = integrations.IsConnecting(provider);

        // A connected integration turns green, and its subtitle stops advertising what
        // it would do and starts reporting what it is.
        connectedDot.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        statusText.Text = connected ? "Connected" : idleSubtitle;

        var tint = connected ? Windows.UI.Color.FromArgb(255, 0x4D, 0xD1, 0x85) : brandColor;
        icon.Foreground = new SolidColorBrush(tint);
        iconBackground.Background =
            new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, tint.R, tint.G, tint.B));

        connectButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        connectButton.Content = connecting ? "Connecting…" : "Connect";
        connectButton.IsEnabled = !connecting;
        disconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: - Tokens page

    /// <summary>The product being bought, so only its card spins rather than all five.</summary>
    private string? _pendingProductId;

    /// <summary>Whether the withdrawal control is showing its confirmation step.</summary>
    private bool _isConfirmingWithdrawal;

    private readonly Dictionary<string, (Button Card, ProgressRing Spinner)> _productCards = new();

    /// <summary>
    /// Builds the five product cards from the catalogue rather than restating each one in
    /// markup, so prices and token counts live in exactly one place — next to the Paddle
    /// price IDs they belong to.
    /// </summary>
    private void BuildProductCards()
    {
        foreach (var product in NayfPaddleProducts.Subscriptions)
            SubscriptionsList.Children.Add(
                BuildProductCard(product, product.Id == NayfPaddleProducts.RecommendedSubscriptionId));

        foreach (var product in NayfPaddleProducts.TokenPacks)
            TokenPacksList.Children.Add(
                BuildProductCard(product, product.Id == NayfPaddleProducts.RecommendedPackId));
    }

    private Button BuildProductCard(PaddleProduct product, bool isRecommended)
    {
        var uiFont = new FontFamily((string)RootGrid.Resources["UiFont"]);
        var primary = (Brush)RootGrid.Resources["TextPrimary"];
        var tertiary = (Brush)RootGrid.Resources["TextTertiary"];
        var accent = (Brush)RootGrid.Resources["AccentLink"];

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(new TextBlock
        {
            Text = product.DisplayName,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = uiFont,
            Foreground = primary,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (isRecommended)
        {
            titleRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0x0A, 0x84, 0xFF)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "POPULAR",
                    FontSize = 8.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontFamily = uiFont,
                    CharacterSpacing = 60,
                    Foreground = accent
                }
            });
        }

        var details = new StackPanel { Spacing = 3 };
        details.Children.Add(titleRow);
        details.Children.Add(new TextBlock
        {
            Text = product.IsSubscription
                ? $"{FormatTokenCount(product.TokenCount)} every month"
                : $"{FormatTokenCount(product.TokenCount)}, one time",
            FontSize = 11,
            FontFamily = uiFont,
            Foreground = tertiary
        });

        var spinner = new ProgressRing
        {
            Width = 15,
            Height = 15,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center
        };

        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        trailing.Children.Add(spinner);
        trailing.Children.Add(new TextBlock
        {
            Text = product.DisplayPrice,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = uiFont,
            Foreground = primary,
            VerticalAlignment = VerticalAlignment.Center
        });

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(details);
        Grid.SetColumn(trailing, 1);
        layout.Children.Add(trailing);

        var card = new Button
        {
            Tag = product,
            Content = layout,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)RootGrid.Resources["SurfaceLow"],
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 11)
        };
        card.Click += ProductCard_Click;

        _productCards[product.Id] = (card, spinner);
        return card;
    }

    private static string FormatTokenCount(int tokens)
        => tokens >= 1_000_000
            ? $"{tokens / 1_000_000.0:0.#}M tokens"
            : $"{tokens / 1_000.0:0.#}k tokens";

    /// <summary>
    /// Sends the user to Paddle for this product. Nothing is charged here — the browser
    /// takes the focus, which blur-dismisses the panel, and the tokens arrive by webhook.
    /// </summary>
    private async void ProductCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PaddleProduct product }) return;
        if (_companionManager.Store.IsCreatingCheckout) return;

        _pendingProductId = product.Id;
        UpdateTokensView();

        await _companionManager.Store.OpenCheckoutAsync(product);

        _pendingProductId = null;
        UpdateTokensView();
    }

    /// <summary>
    /// Confirms the purchase finished. Paddle's webhook credits the account a moment after
    /// payment clears, so the balance is read twice — once now, once after it has had time
    /// to land — rather than leaving the user looking at a stale number.
    /// </summary>
    private async void PurchaseCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        _companionManager.Store.ResetCheckoutState();
        UpdateTokensView();

        await _companionManager.FetchCreditBalanceAsync();
        await Task.Delay(TimeSpan.FromSeconds(4));
        await _companionManager.FetchCreditBalanceAsync();
    }

    private void BackToPlansButton_Click(object sender, RoutedEventArgs e)
    {
        _companionManager.Store.ResetCheckoutState();
        UpdateTokensView();
    }

    private void WithdrawalTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        _isConfirmingWithdrawal = true;
        _companionManager.Store.ResetWithdrawalState();
        UpdateTokensView();
    }

    private void WithdrawalCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _isConfirmingWithdrawal = false;
        UpdateTokensView();
    }

    private async void WithdrawalSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        await _companionManager.Store.RequestWithdrawalAsync();
        // Stay on the confirmation step if it failed, so the error has somewhere to show
        // and the button they just pressed is still there to press again.
        if (_companionManager.Store.WithdrawalAcknowledgment != null)
            _isConfirmingWithdrawal = false;
        UpdateTokensView();
    }

    private void WithdrawalDoneButton_Click(object sender, RoutedEventArgs e)
    {
        _isConfirmingWithdrawal = false;
        _companionManager.Store.ResetWithdrawalState();
        UpdateTokensView();
    }

    /// <summary>
    /// Paints the tokens page from the store. The page has two faces — the plan list, and
    /// the "finish in the browser" state it switches to once checkout has been handed off —
    /// and which one shows is the store's business, not the panel's, because the panel is
    /// closed at the moment that changes.
    /// </summary>
    private void UpdateTokensView()
    {
        var store = _companionManager.Store;

        bool handedOff = store.HasBrowserCheckoutOpen;
        TokensPlansView.Visibility = handedOff ? Visibility.Collapsed : Visibility.Visible;
        TokensCheckoutView.Visibility = handedOff ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (id, card) in _productCards)
        {
            bool busy = store.IsCreatingCheckout && id == _pendingProductId;
            card.Spinner.IsActive = busy;
            card.Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            card.Card.IsEnabled = !store.IsCreatingCheckout;
        }

        bool hasCheckoutError = !string.IsNullOrWhiteSpace(store.CheckoutError);
        CheckoutErrorText.Text = store.CheckoutError ?? "";
        CheckoutErrorText.Visibility = hasCheckoutError ? Visibility.Visible : Visibility.Collapsed;

        // The withdrawal control is one of three things at a time: an offer, a
        // confirmation, or a receipt.
        bool acknowledged = store.WithdrawalAcknowledgment != null;
        WithdrawalTriggerButton.Visibility =
            !acknowledged && !_isConfirmingWithdrawal ? Visibility.Visible : Visibility.Collapsed;
        WithdrawalConfirmCard.Visibility =
            !acknowledged && _isConfirmingWithdrawal ? Visibility.Visible : Visibility.Collapsed;
        WithdrawalAcknowledgedCard.Visibility =
            acknowledged ? Visibility.Visible : Visibility.Collapsed;

        WithdrawalSubmitButton.Content =
            store.IsSubmittingWithdrawal ? "Submitting…" : "Submit withdrawal request";
        WithdrawalSubmitButton.IsEnabled = !store.IsSubmittingWithdrawal;
        WithdrawalCancelButton.IsEnabled = !store.IsSubmittingWithdrawal;

        bool hasWithdrawalError = !string.IsNullOrWhiteSpace(store.WithdrawalError);
        WithdrawalErrorText.Text = store.WithdrawalError ?? "";
        WithdrawalErrorText.Visibility = hasWithdrawalError ? Visibility.Visible : Visibility.Collapsed;

        if (store.WithdrawalAcknowledgment is { } ack)
        {
            WithdrawalAcknowledgmentText.Text = ack.Message;
            bool hasReference = !string.IsNullOrWhiteSpace(ack.Reference);
            WithdrawalReferenceText.Text = hasReference ? $"Reference: {ack.Reference}" : "";
            WithdrawalReferenceText.Visibility = hasReference ? Visibility.Visible : Visibility.Collapsed;
        }

        if (TokensPage.Visibility == Visibility.Visible) FitWindowToContent();
    }

    private void ClearMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        _companionManager.Memory.ClearAll();
    }

    private void UpdateMemoryView()
    {
        bool has = _companionManager.Memory.HasMemories;
        MemoryList.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        MemoryEmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        // No point offering to clear an empty list.
        ClearMemoryButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        FitWindowToContent();
    }

    /// <summary>
    /// Switches the visible page. Subpages are reached from the bottom action bar
    /// rather than tabs, so the header swaps its wordmark for a back button and
    /// the page's title — that back button is the only way home.
    /// </summary>
    private void ShowPage(PanelPage page)
    {
        HomePage.Visibility = page == PanelPage.Home ? Visibility.Visible : Visibility.Collapsed;
        MemoryPage.Visibility = page == PanelPage.Memory ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPage.Visibility = page == PanelPage.Connections ? Visibility.Visible : Visibility.Collapsed;
        TokensPage.Visibility = page == PanelPage.Tokens ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == PanelPage.Settings ? Visibility.Visible : Visibility.Collapsed;

        bool home = page == PanelPage.Home;
        HeaderWordmark.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        HeaderBackArea.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        // The gear is the way in, so it has nowhere to go once you're there.
        SettingsButton.Visibility = page == PanelPage.Settings ? Visibility.Collapsed : Visibility.Visible;

        if (!home)
            PageTitleText.Text = page switch
            {
                PanelPage.Memory => "Memory",
                PanelPage.Connections => "Connect apps",
                PanelPage.Tokens => "Get tokens",
                _ => "Settings"
            };

        // Re-ask the Worker on every visit — the account may have connected or
        // disconnected something on another device since we last looked.
        if (page == PanelPage.Connections)
            _ = _companionManager.Integrations.RefreshStatusAsync();

        // Likewise for the balance: a purchase made in the browser is credited by
        // Paddle's webhook, with nothing on screen at the time to hear about it.
        if (page == PanelPage.Tokens)
        {
            _ = _companionManager.FetchCreditBalanceAsync();
            UpdateTokensView();
        }

        // Read the live registry state each time rather than trusting a cached
        // value — the installer, another Nayf window, or the user editing
        // startup apps in Windows Settings can all change it behind our back.
        if (page == PanelPage.Settings)
        {
            RefreshStartupToggle();
            ClearHistoryCaption.Text = "Forget what we've talked about this session";
        }

        FitWindowToContent();
    }

    /// <summary>
    /// Rings the active cursor-color swatch in its own color, so the selection
    /// reads as "this color is on" rather than as a neutral highlight.
    /// </summary>
    private void UpdateCursorColorSelection(NayfCursorColor selected)
    {
        var clear = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        SolidColorBrush Ring(NayfCursorColor c)
        {
            var d = c.ToDrawingColor();
            return new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, d.R, d.G, d.B));
        }

        ColorBlue.BorderBrush = selected == NayfCursorColor.Blue ? Ring(selected) : clear;
        ColorRed.BorderBrush = selected == NayfCursorColor.Red ? Ring(selected) : clear;
        ColorYellow.BorderBrush = selected == NayfCursorColor.Yellow ? Ring(selected) : clear;
        ColorGreen.BorderBrush = selected == NayfCursorColor.Green ? Ring(selected) : clear;
    }
}
