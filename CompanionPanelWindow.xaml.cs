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

    public CompanionPanelWindow(CompanionManager companionManager)
    {
        _companionManager = companionManager;
        InitializeComponent();
        SetupWindow();
        SubscribeToCompanionManager();
    }

    private void SetupWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);

        // Make this a tool window (no taskbar button, no Alt+Tab appearance)
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

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

        // Position the panel above the tray icon
        int x = Math.Clamp(trayRect.Left - panelWidth / 2,
            workArea.Left + margin,
            workArea.Right - panelWidth - margin);
        int y = workArea.Bottom - panelHeight - margin;

        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, panelWidth, panelHeight));
        appWindow.Show();
        _isVisible = true;

        // Install a click-outside monitor to auto-dismiss the panel
        InstallClickOutsideMonitor();
    }

    public void HidePanel()
    {
        if (!_isVisible) return;
        AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                WindowNative.GetWindowHandle(this))).Hide();
        _isVisible = false;
    }

    private void InstallClickOutsideMonitor()
    {
        // Poll for clicks outside the panel window — simplified approach
        // A production version would use a global mouse hook
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (!_isVisible) { timer.Stop(); return; }

            if (NativeMethods.GetCursorPos(out var pt))
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                NativeMethods.GetWindowRect(hwnd, out var rect);

                bool cursorIsOutside =
                    pt.X < rect.Left || pt.X > rect.Right ||
                    pt.Y < rect.Top || pt.Y > rect.Bottom;

                // Only auto-dismiss on left mouse button click outside
                if (cursorIsOutside && IsLeftMouseButtonDown())
                {
                    HidePanel();
                    timer.Stop();
                }
            }
        };
        timer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private static bool IsLeftMouseButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

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
}
