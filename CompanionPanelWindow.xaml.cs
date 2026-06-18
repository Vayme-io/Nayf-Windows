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
            }
        };
    }

    private void UpdateVoiceStateUI(CompanionVoiceState state)
    {
        VoiceStateLabel.Text = _companionManager.VoiceStateLabel;

        StateIndicatorDot.Fill = state switch
        {
            CompanionVoiceState.Listening => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 122, 255)),
            CompanionVoiceState.Processing => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 159, 10)),
            CompanionVoiceState.Responding => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 199, 89)),
            _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 199, 89))
        };
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
    public void EnsureVisibleNearTray(NativeMethods.RECT trayRect)
    {
        if (_isVisible) return;
        if ((DateTimeOffset.UtcNow - _lastHidden).TotalMilliseconds < 250) return;
        ShowPanelAt(trayRect);
    }

    private void ShowPanelAt(NativeMethods.RECT trayRect)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        // Panel dimensions
        const int panelWidth = 340;
        const int panelHeight = 520;
        const int margin = 8;

        // Get work area (screen minus taskbar)
        var workArea = new NativeMethods.RECT();
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref workArea, 0);

        // Center the panel horizontally over the anchor (tray icon or taskbar
        // button). Fall back to the screen center if we don't have a valid rect.
        int anchorX = trayRect.Right > trayRect.Left
            ? (trayRect.Left + trayRect.Right) / 2
            : (workArea.Left + workArea.Right) / 2;

        int x = Math.Clamp(anchorX - panelWidth / 2,
            workArea.Left + margin,
            workArea.Right - panelWidth - margin);
        int y = workArea.Bottom - panelHeight - margin;

        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, panelWidth, panelHeight));
        appWindow.Show();
        Activate(); // take focus so the acrylic renders active and blur-dismiss works
        _isVisible = true;
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

    private void ModelRadio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string modelId)
        {
            _companionManager.SelectedModel = modelId;
        }
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
}
