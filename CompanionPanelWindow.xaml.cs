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

    /// <summary>
    /// Raised when the user presses the power button. Handled by the app rather than here,
    /// so quitting from the panel tears down exactly what quitting from the tray does.
    /// </summary>
    public event Action? QuitRequested;

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
        BuildQuickPicks();
        UpdateTokensView();

        // Updates run on a background loop and nearly always progress while the panel is
        // shut, so the row is drawn from the checker's current state rather than left
        // showing whatever it said the last time someone looked at it.
        _companionManager.Updates.PropertyChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(RefreshUpdateRow);
        RefreshUpdateRow();

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

        // Agents tab: the saved-task grid. Rebound rather than bound once, because the
        // grid shows the tasks newest-activity-first and that order is a snapshot — a
        // continued task moves to the front, which no collection change would express.
        _companionManager.AgentTasks.Tasks.CollectionChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateAgentsView);
        UpdateAgentsView();

        // Agent task UI: the one thing a running task puts in the panel is a confirmation
        // prompt, and that is a question addressed to the user. How the task is getting on
        // is the status pill's business and stays out of here.
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
                // The message too, not only the flag: a second failure of a different kind
                // while the banner is already up never touches the flag, and would
                // otherwise leave the first reason on screen.
                case nameof(CompanionManager.MicrophonePermissionNeeded):
                case nameof(CompanionManager.SpeechProblemMessage):
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

    private void UpdateMicPermissionBanner(bool needed)
    {
        MicPermissionBanner.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        if (!needed) return;

        MicPermissionMessage.Text = _companionManager.SpeechProblemMessage;
        OpenSpeechSettingsButton.Content = _companionManager.SpeechProblem switch
        {
            SpeechProblem.MicrophoneBlocked => "Open Microphone Settings",
            SpeechProblem.MicrophoneUnavailable => "Open Sound Settings",
            _ => "Open Sound Settings"
        };
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
        HidePanel();
        QuitRequested?.Invoke();
    }

    /// <summary>
    /// Shows which tier Vayme routed the last turn to. The model is chosen
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
        ClearHistoryCaption.Text = "Cleared — Vayme is starting fresh";
    }

    /// <summary>
    /// Shows the running build's version. Read from the executable rather than
    /// hard-coded so it can't drift from what the user actually has installed.
    /// </summary>
    private void UpdateVersionText()
    {
        string version = typeof(CompanionPanelWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = $"Vayme for Windows · {version}";
    }

    /// <summary>
    /// Draws the Updates row from what the checker is doing. It is a status line first and
    /// a button second — Vayme applies updates on its own, so the only tap that does
    /// anything substantial is the one on a build already downloaded and waiting.
    /// </summary>
    private void RefreshUpdateRow()
    {
        UpdateChecker updates = _companionManager.Updates;
        bool working = updates.Stage is UpdateStage.Checking or UpdateStage.Downloading;

        UpdateRowSpinner.IsActive = working;
        UpdateRowSpinner.Visibility = working ? Visibility.Visible : Visibility.Collapsed;

        (UpdateRowTitle.Text, UpdateRowCaption.Text) = updates.Stage switch
        {
            UpdateStage.Checking =>
                ("Checking for updates…", "Asking whether there's a newer build"),

            UpdateStage.Downloading =>
                ($"Downloading Vayme {updates.AvailableVersion}",
                 $"{updates.DownloadPercent}% — it installs once you're not using Vayme"),

            UpdateStage.ReadyToInstall =>
                ($"Vayme {updates.AvailableVersion} is ready",
                 "It installs itself once you're idle. Tap to restart and update now."),

            UpdateStage.UpToDate =>
                ("Vayme is up to date", $"You're on {UpdateChecker.CurrentVersion}. Tap to check again."),

            UpdateStage.Failed =>
                ("Check for updates", updates.FailureMessage),

            _ => ("Check for updates", "Vayme keeps itself up to date automatically")
        };
    }

    private void UpdateRow_Click(object sender, RoutedEventArgs e)
    {
        UpdateChecker updates = _companionManager.Updates;

        // Mid-check or mid-download a tap has nothing useful to do, and starting a second
        // check would only reset the progress the row is currently showing.
        if (updates.Stage is UpdateStage.Checking or UpdateStage.Downloading) return;

        if (updates.Stage == UpdateStage.ReadyToInstall)
        {
            updates.InstallNow();
            return;
        }

        _ = updates.CheckNowAsync();
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

    // Same guard as the startup switch: syncing the toggle to the stored setting
    // raises Toggled, and that isn't the user flipping it.
    private bool _syncingRoastToggle;

    private void RefreshRoastModeToggle()
    {
        _syncingRoastToggle = true;
        RoastModeToggle.IsOn = _companionManager.RoastMode;
        _syncingRoastToggle = false;
    }

    private void RoastModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingRoastToggle) return;
        _companionManager.RoastMode = RoastModeToggle.IsOn;
    }

    /// <summary>
    /// The panel goes away first: the card lands in the middle of the screen and Vayme talks
    /// over it, and a settings pane still sitting there would be the one thing on screen the
    /// tour isn't about.
    /// </summary>
    private void CapabilitiesButton_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
        _companionManager.RunCapabilitiesShowcase();
    }

    private void OpenSpeechSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsPage("ms-settings:privacy-speech");
    }

    /// <summary>
    /// Opens the page that fixes what the banner is complaining about, which is not always
    /// the speech privacy page the Settings row goes to.
    /// </summary>
    private void FixSpeechProblemButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsPage(_companionManager.SpeechProblem switch
        {
            SpeechProblem.MicrophoneBlocked => "ms-settings:privacy-microphone",

            // Sound rather than privacy: nothing is blocked, there is simply no device
            // answering. This page is where a headset that is off or disconnected shows up.
            // Deliberately no longer the speech privacy page — nothing Vayme does needs it
            // now that transcription runs on this machine.
            _ => "ms-settings:sound"
        });
    }

    private static void OpenSettingsPage(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Log("Panel", $"Could not open {uri}: {ex.Message}");
        }
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

    private enum PanelPage { Home, Memory, Agents, Connections, Tokens, Settings }

    private void BackButton_Click(object sender, RoutedEventArgs e) => ShowPage(PanelPage.Home);
    private void MemoryButton_Click(object sender, RoutedEventArgs e) => ShowPage(PanelPage.Memory);
    private void AgentsButton_Click(object sender, RoutedEventArgs e) => ShowPage(PanelPage.Agents);
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

    /// <summary>Whether the withdrawal control is showing its confirmation step.</summary>
    private bool _isConfirmingWithdrawal;

    /// <summary>
    /// What the amount field currently parses to, in cents, or null if it isn't a number at
    /// all. Out-of-range values are kept rather than discarded: the user is told which bound
    /// they crossed, which needs the number they typed.
    /// </summary>
    private int? _typedCents = NayfTokenPricing.DefaultCents;

    private readonly Dictionary<int, Button> _quickPickChips = new();

    /// <summary>
    /// Builds the shortcut chips from the pricing constants rather than restating the
    /// amounts in markup, so the row can never drift from what the field accepts.
    /// </summary>
    private void BuildQuickPicks()
    {
        foreach (int cents in NayfTokenPricing.QuickPickCents)
        {
            // From NayfFonts rather than the resource dictionary. The brushes below live in
            // RootGrid.Resources, but the font faces are published on Application.Resources so
            // that every window shares one answer - and this indexer only looks in the
            // dictionary it is called on. Unlike {StaticResource}, it does not walk up to the
            // application's, so asking RootGrid for "UiFont" throws.
            var chip = new Button
            {
                Tag = cents,
                Content = NayfTokenPricing.FormattedPrice(cents),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontFamily = new FontFamily(NayfFonts.UiFamily),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(14, 6, 14, 6)
            };
            chip.Click += QuickPick_Click;

            _quickPickChips[cents] = chip;
            QuickPickRow.Children.Add(chip);
        }
    }

    /// <summary>
    /// Writes the shortcut amount into the field rather than buying straight away, so the
    /// chips and the field are never two different answers to the same question. The
    /// resulting TextChanged repaints everything else.
    /// </summary>
    private void QuickPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int cents }) return;
        // Without the "$", which the field never contains - it is drawn beside it.
        AmountField.Text = NayfTokenPricing.FormattedPrice(cents).TrimStart('$');
        AmountField.SelectionStart = AmountField.Text.Length;
    }

    private void AmountField_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Off `sender`, not the field: this fires while the markup is still being parsed,
        // when the starting text is applied, and at that point neither this TextBox's own
        // generated field nor anything declared below it has been assigned yet. Repainting
        // then would dereference a null button. The constructor paints once itself.
        _typedCents = NayfTokenPricing.CentsFromTypedAmount(((TextBox)sender).Text);
        if (BuyButtonText is null) return;

        UpdateTokensView();
    }

    /// <summary>
    /// Only ever changes which Paddle price the purchase uses, so nothing here needs to do
    /// more than relabel the button.
    /// </summary>
    private void AutoTopUpSwitch_Toggled(object sender, RoutedEventArgs e) => UpdateTokensView();

    /// <summary>
    /// Sends the user to Paddle for the amount they typed. Nothing is charged here — the
    /// browser takes the focus, which blur-dismisses the panel, and the tokens arrive by
    /// webhook once payment clears.
    /// </summary>
    private async void BuyButton_Click(object sender, RoutedEventArgs e)
    {
        var store = _companionManager.Store;
        if (store.IsCreatingCheckout) return;
        if (_typedCents is not { } cents || !NayfTokenPricing.IsValid(cents)) return;

        await store.OpenCheckoutAsync(cents, AutoTopUpSwitch.IsOn);
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

    private void BackToAmountButton_Click(object sender, RoutedEventArgs e)
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
    /// Paints the tokens page from the store. The page has two faces — the amount to buy,
    /// and the "finish in the browser" state it switches to once checkout has been handed
    /// off — and which one shows is the store's business, not the panel's, because the panel
    /// is closed at the moment that changes.
    /// </summary>
    private void UpdateTokensView()
    {
        var store = _companionManager.Store;

        bool handedOff = store.HasBrowserCheckoutOpen;
        TokensBuyView.Visibility = handedOff ? Visibility.Collapsed : Visibility.Visible;
        TokensCheckoutView.Visibility = handedOff ? Visibility.Visible : Visibility.Collapsed;

        // The hint carries whichever of the two things is true: what the amount buys, or why
        // it won't go through. An amount that isn't a number at all says neither, because
        // "minimum $2.00" is the wrong answer to an empty field someone is still typing in.
        bool isValidAmount = _typedCents is { } typed && NayfTokenPricing.IsValid(typed);
        AmountHintText.Text = _typedCents switch
        {
            null => "Enter an amount",
            { } c when c < NayfTokenPricing.MinimumCents
                => $"minimum {NayfTokenPricing.FormattedPrice(NayfTokenPricing.MinimumCents)}",
            { } c when c > NayfTokenPricing.MaximumCents
                => $"maximum {NayfTokenPricing.FormattedPrice(NayfTokenPricing.MaximumCents)}",
            { } c => $"{NayfTokenPricing.FormattedTokens(c)} tokens"
        };
        AmountHintText.Foreground = (Brush)RootGrid.Resources[
            _typedCents is null || isValidAmount ? "TextTertiary" : "Danger"];

        // A chip reads as selected only while the field says exactly what it would set, so
        // typing an amount of your own visibly steps outside the shortcuts.
        foreach (var (cents, chip) in _quickPickChips)
        {
            bool selected = _typedCents == cents;
            chip.Background = selected
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0x0A, 0x84, 0xFF))
                : (Brush)RootGrid.Resources["SurfaceLow"];
            chip.Foreground = (Brush)RootGrid.Resources[selected ? "AccentLink" : "TextSecondary"];
            chip.IsEnabled = !store.IsCreatingCheckout;
        }

        // The button says what it is about to do, in the same words as the amount and the
        // token count above it, so the two never have to be read together to be believed.
        BuyButtonText.Text = isValidAmount && _typedCents is { } amount
            ? AutoTopUpSwitch.IsOn
                ? $"Get {NayfTokenPricing.FormattedTokens(amount)} tokens monthly — {NayfTokenPricing.FormattedPrice(amount)}/mo"
                : $"Buy {NayfTokenPricing.FormattedTokens(amount)} tokens — {NayfTokenPricing.FormattedPrice(amount)}"
            : "Buy tokens";
        BuyButton.IsEnabled = isValidAmount && !store.IsCreatingCheckout;
        BuySpinner.IsActive = store.IsCreatingCheckout;
        BuySpinner.Visibility = store.IsCreatingCheckout ? Visibility.Visible : Visibility.Collapsed;
        AmountField.IsEnabled = !store.IsCreatingCheckout;
        AutoTopUpSwitch.IsEnabled = !store.IsCreatingCheckout;

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

    private void MemoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string memory }) return;
        _companionManager.Memory.Remove(memory);
    }

    private void ClearMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        _companionManager.Memory.ClearAll();
    }

    // MARK: - Agents

    /// <summary>
    /// Opens a saved task on its floating card. The panel gets out of the way: the card
    /// is a separate always-on-top surface, and leaving the panel open in front of it
    /// would hide the thing the tap just asked for.
    /// </summary>
    private void AgentTaskTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var task = _companionManager.AgentTasks.Task(id);
        if (task == null) return;

        HidePanel();
        _companionManager.OpenSavedAgent(task);
    }

    private void AgentTaskDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        _companionManager.AgentTasks.Remove(id);
    }

    private void ClearAgentsButton_Click(object sender, RoutedEventArgs e)
    {
        _companionManager.AgentTasks.ClearAll();
    }

    private void UpdateAgentsView()
    {
        var tasks = _companionManager.AgentTasks.TasksNewestFirst;
        bool has = tasks.Count > 0;

        AgentTaskList.ItemsSource = tasks;
        AgentTaskList.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        AgentsEmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ClearAgentsButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        if (AgentsPage.Visibility == Visibility.Visible) FitWindowToContent();
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
        AgentsPage.Visibility = page == PanelPage.Agents ? Visibility.Visible : Visibility.Collapsed;
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
                PanelPage.Agents => "Agents",
                PanelPage.Connections => "Connect apps",
                PanelPage.Tokens => "Get tokens",
                _ => "Settings"
            };

        // The grid is ordered by most recent activity, and a task continued while this
        // page was closed has moved since it was last built.
        if (page == PanelPage.Agents)
            UpdateAgentsView();

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
        // value — the installer, another Vayme window, or the user editing
        // startup apps in Windows Settings can all change it behind our back.
        if (page == PanelPage.Settings)
        {
            RefreshStartupToggle();
            RefreshRoastModeToggle();
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
