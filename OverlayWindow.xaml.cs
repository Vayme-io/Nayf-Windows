using Microsoft.UI.Xaml;

namespace NayfWindows;

/// <summary>
/// Shell window — never shown. Exists only to satisfy the WinUI 3 XAML
/// partial class requirement. All overlay rendering is handled by NativeOverlayWindow.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        // Immediately hide — we never want this WinUI window to appear
        this.AppWindow.Hide();
    }
}
