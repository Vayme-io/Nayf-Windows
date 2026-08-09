using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using WinRT;
using WinRT.Interop;

namespace NayfWindows;

/// <summary>
/// Email/password sign-in and sign-up window shown when the user isn't
/// authenticated. On success, fires <see cref="AuthenticationSucceeded"/> so
/// the app can dismiss this window and start the companion. Mirrors the Mac
/// app's AuthView.
/// </summary>
public sealed partial class AuthWindow : Window
{
    private readonly AuthManager _authManager;
    private bool _isSignUpMode;
    private bool _isBusy;

    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private bool _isClosing;

    /// <summary>Raised once the user successfully signs in or signs up.</summary>
    public event Action? AuthenticationSucceeded;

    public AuthWindow(AuthManager authManager)
    {
        _authManager = authManager;
        InitializeComponent();
        SetupWindow();
        LoadLogo();

        // Submit on Enter from either field; Escape closes the window.
        EmailBox.KeyDown += OnFieldKeyDown;
        PasswordBox.KeyDown += OnFieldKeyDown;
        RootGrid.KeyDown += OnFieldKeyDown;

        // Size the window to fit its content and keep it centred.
        ContentStack.SizeChanged += OnContentSizeChanged;
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e) => FitWindowToContent();

    private void SetupWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        // Borderless, frameless — no title bar — exactly like the companion panel.
        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        appWindow.SetPresenter(presenter);
        appWindow.Title = "Nayf";

        // Dark-mode rounded appearance.
        int darkMode = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */,
            ref darkMode, sizeof(int));

        if (Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;

        // Standard (Base) acrylic — the same frosted, dark, Start-menu-like
        // material the companion panel uses.
        if (DesktopAcrylicController.IsSupported())
        {
            _backdropConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Dark
            };
            _acrylicController = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
            _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
        }
        else
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        // Release the backdrop material when the window goes away, and stop resizing
        // first: layout can still settle as the window is destroyed, and resizing one
        // that no longer has a live HWND faults in the XAML layer instead of throwing.
        Closed += (_, _) =>
        {
            _isClosing = true;
            ContentStack.SizeChanged -= OnContentSizeChanged;
            _acrylicController?.Dispose();
            _acrylicController = null;
        };

        // Reasonable initial size; FitWindowToContent refines it once measured.
        appWindow.ResizeClient(new SizeInt32(380, 470));
        CenterOnPrimary(appWindow, 380, 470);
    }

    /// <summary>Loads the real Nayf logo from the app's Assets folder.</summary>
    private void LoadLogo()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "NayfIcon.png");
            if (File.Exists(path))
                LogoImage.Source = new BitmapImage(new Uri(path));
        }
        catch (Exception ex)
        {
            Logger.Log("AuthWindow", $"Logo load failed: {ex.Message}");
        }
    }

    /// <summary>Resizes the window to fit its content and re-centres it.</summary>
    private void FitWindowToContent()
    {
        if (_isClosing) return;

        double dipWidth = ContentStack.ActualWidth > 0 ? ContentStack.ActualWidth : ContentStack.Width;
        double dipHeight = ContentStack.ActualHeight;
        if (dipWidth <= 0 || dipHeight <= 0) return;

        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int w = (int)Math.Ceiling(dipWidth * scale);
        int h = (int)Math.Ceiling((dipHeight + 1) * scale);

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        appWindow.ResizeClient(new SizeInt32(w, h));
        NativeMethods.GetWindowRect(hwnd, out var outer);
        CenterOnPrimary(appWindow, outer.Right - outer.Left, outer.Bottom - outer.Top);
    }

    private static void CenterOnPrimary(AppWindow appWindow, int outerW, int outerH)
    {
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        int x = area.WorkArea.X + (area.WorkArea.Width - outerW) / 2;
        int y = area.WorkArea.Y + (area.WorkArea.Height - outerH) / 2;
        appWindow.Move(new PointInt32(x, y));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            _ = SubmitAsync();
        else if (e.Key == VirtualKey.Escape)
            Close();
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e) => _ = SubmitAsync();

    private void ToggleModeButton_Click(object sender, RoutedEventArgs e)
    {
        _isSignUpMode = !_isSignUpMode;
        HideMessage();

        if (_isSignUpMode)
        {
            SubtitleText.Text = "Create your account";
            SubmitButton.Content = "Create Account";
            ToggleHintText.Text = "Already have an account?";
            ToggleModeButton.Content = "Sign in";
        }
        else
        {
            SubtitleText.Text = "Sign in to continue";
            SubmitButton.Content = "Sign In";
            ToggleHintText.Text = "Don't have an account?";
            ToggleModeButton.Content = "Create one";
        }
    }

    private async Task SubmitAsync()
    {
        if (_isBusy) return;

        var email = EmailBox.Text?.Trim() ?? "";
        var password = PasswordBox.Password ?? "";

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ShowMessage("Enter your email and password.", isError: true);
            return;
        }

        SetBusy(true);
        HideMessage();

        try
        {
            if (_isSignUpMode)
            {
                bool signedIn = await _authManager.SignUpAsync(email, password);
                if (!signedIn)
                {
                    // Email confirmation required — can't proceed yet.
                    SetBusy(false);
                    ShowMessage("Account created. Check your inbox to confirm your email, then sign in.",
                        isError: false);
                    ToggleToSignIn();
                    return;
                }
            }
            else
            {
                await _authManager.SignInAsync(email, password);
            }

            AuthenticationSucceeded?.Invoke();
        }
        catch (AuthException ex)
        {
            SetBusy(false);
            ShowMessage(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            Logger.Log("AuthWindow", $"Unexpected auth error: {ex}");
            ShowMessage("Something went wrong. Check your connection and try again.", isError: true);
        }
    }

    private void ToggleToSignIn()
    {
        if (_isSignUpMode) ToggleModeButton_Click(this, new RoutedEventArgs());
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        LoadingRing.IsActive = busy;
        SubmitButton.IsEnabled = !busy;
        EmailBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        ToggleModeButton.IsEnabled = !busy;
    }

    private void ShowMessage(string text, bool isError)
    {
        MessageText.Text = text;
        MessageText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            isError
                ? Windows.UI.Color.FromArgb(255, 255, 69, 58)   // red
                : Windows.UI.Color.FromArgb(255, 52, 199, 89));  // green
        MessageText.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        MessageText.Visibility = Visibility.Collapsed;
    }
}
