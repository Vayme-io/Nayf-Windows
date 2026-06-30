using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        UpdateModelSelection(_companionManager.SelectedModel);
        UpdateCreditBalance();
        UpdateCursorColorSelection(_companionManager.SelectedCursorColor);

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
    }

    private void UpdateAccountEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            AccountEmailText.Visibility = Visibility.Collapsed;
        }
        else
        {
            AccountEmailText.Text = $"Signed in as {email}";
            AccountEmailText.Visibility = Visibility.Visible;
        }
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

        // Frosted acrylic material like the Windows 11 Start menu.
        SystemBackdrop = new DesktopAcrylicBackdrop();
        if (Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;

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

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Show();
        Activate(); // take focus so the acrylic renders active and blur-dismiss works
        _isVisible = true;

        // Size + position the window to fit the content height.
        FitWindowToContent();
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

    private void ModelSeg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string modelId)
        {
            _companionManager.SelectedModel = modelId;
            UpdateModelSelection(modelId);
        }
    }

    /// <summary>Restyles the segmented model toggle to reflect the selection.</summary>
    private void UpdateModelSelection(string modelId)
    {
        var selectedFill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        var clear = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        var selectedText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        var dimText = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 142, 142, 147));

        bool sonnet = modelId == "claude-sonnet-4-6";
        SonnetSeg.Background = sonnet ? selectedFill : clear;
        SonnetSeg.Foreground = sonnet ? selectedText : dimText;
        OpusSeg.Background = sonnet ? clear : selectedFill;
        OpusSeg.Foreground = sonnet ? dimText : selectedText;
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _companionManager.ClearConversationHistory();
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

    private void TopUpButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://vayme.com/pricing") { UseShellExecute = true });
    }

    private void CursorColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag &&
            Enum.TryParse<NayfCursorColor>(tag, out var color))
        {
            _companionManager.SelectedCursorColor = color;
            UpdateCursorColorSelection(color);
        }
    }

    /// <summary>Draws a white selection ring around the active cursor-color swatch.</summary>
    private void UpdateCursorColorSelection(NayfCursorColor selected)
    {
        var ring = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        var clear = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        ColorBlue.BorderBrush = selected == NayfCursorColor.Blue ? ring : clear;
        ColorRed.BorderBrush = selected == NayfCursorColor.Red ? ring : clear;
        ColorYellow.BorderBrush = selected == NayfCursorColor.Yellow ? ring : clear;
        ColorGreen.BorderBrush = selected == NayfCursorColor.Green ? ring : clear;
    }
}
