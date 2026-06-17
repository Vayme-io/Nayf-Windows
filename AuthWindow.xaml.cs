using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
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

    /// <summary>Raised once the user successfully signs in or signs up.</summary>
    public event Action? AuthenticationSucceeded;

    public AuthWindow(AuthManager authManager)
    {
        _authManager = authManager;
        InitializeComponent();
        SetupWindow();

        // Submit on Enter from either field.
        EmailBox.KeyDown += OnFieldKeyDown;
        PasswordBox.KeyDown += OnFieldKeyDown;
    }

    private void SetupWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        appWindow.SetPresenter(presenter);

        // Dark title bar to match the panel.
        int darkMode = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */,
            ref darkMode, sizeof(int));

        appWindow.Title = "Sign in to Nayf";
        appWindow.ResizeClient(new SizeInt32(360, 560));

        // Center on the work area.
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        int x = area.WorkArea.X + (area.WorkArea.Width - 360) / 2;
        int y = area.WorkArea.Y + (area.WorkArea.Height - 560) / 2;
        appWindow.Move(new PointInt32(x, y));
    }

    private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            _ = SubmitAsync();
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
