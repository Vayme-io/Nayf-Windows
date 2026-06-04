using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace NayfWindows;

/// <summary>
/// Manages the system tray icon and the hidden message-window that receives
/// tray callback messages. Mirrors the macOS NSStatusItem + MenuBarPanelManager.
/// </summary>
public sealed class SystemTrayManager : IDisposable
{
    private readonly Action _onShowPanel;
    private readonly Action _onHidePanel;
    private readonly Action _onQuit;

    private IntPtr _messageWindowHandle = IntPtr.Zero;
    private IntPtr _trayIconHandle = IntPtr.Zero;
    private Thread? _messageLoopThread;
    private bool _panelVisible = false;
    private NativeMethods.NOTIFYICONDATA _notifyIconData;
    private static readonly uint TrayCallbackMessage = NativeMethods.WM_APP_TRAY;
    private NativeMethods.WndProc? _wndProcDelegate;
    private NativeMethods.RECT _trayIconRect;

    public SystemTrayManager(Action onShowPanel, Action onHidePanel, Action onQuit)
    {
        _onShowPanel = onShowPanel;
        _onHidePanel = onHidePanel;
        _onQuit = onQuit;
    }

    public void Initialize()
    {
        // Run the message loop on a dedicated background thread so it doesn't
        // block the WinUI 3 dispatcher thread.
        _messageLoopThread = new Thread(RunMessageLoop)
        {
            Name = "NayfTrayMessageLoop",
            IsBackground = true
        };
        _messageLoopThread.SetApartmentState(ApartmentState.STA);
        _messageLoopThread.Start();
    }

    private void RunMessageLoop()
    {
        // Register a minimal window class for receiving tray callbacks
        _wndProcDelegate = TrayWindowProc;
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = "NayfTrayMessageWindow"
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        _messageWindowHandle = NativeMethods.CreateWindowEx(
            0, "NayfTrayMessageWindow", "NayfTray",
            0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        AddTrayIcon();

        // Standard Win32 message loop
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private void AddTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "NayfIcon.ico");
        IntPtr iconHandle;

        if (File.Exists(iconPath))
        {
            iconHandle = NativeMethods.LoadImage(
                IntPtr.Zero, iconPath,
                1 /* IMAGE_ICON */, 16, 16,
                0x0010 /* LR_LOADFROMFILE */);
        }
        else
        {
            // Fallback to the default application icon
            iconHandle = NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
        }

        _notifyIconData = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _messageWindowHandle,
            uID = 1,
            uFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_MESSAGE,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = iconHandle,
            szTip = "Nayf — AI Cursor Companion"
        };

        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _notifyIconData);

        // Set version to 4 to receive enhanced callback info
        _notifyIconData.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _notifyIconData);
    }

    private IntPtr TrayWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TrayCallbackMessage)
        {
            // With NOTIFYICON_VERSION_4, lParam contains the notification event
            uint notifyEvent = (uint)(lParam.ToInt64() & 0xFFFF);

            if (notifyEvent == NativeMethods.WM_LBUTTONUP ||
                notifyEvent == NativeMethods.NIN_SELECT ||
                notifyEvent == NativeMethods.NIN_KEYSELECT)
            {
                // Get the icon rect from the x,y packed in wParam
                int iconX = (int)(wParam.ToInt64() & 0xFFFF);
                int iconY = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
                _trayIconRect = new NativeMethods.RECT
                {
                    Left = iconX - 8, Top = iconY - 8,
                    Right = iconX + 8, Bottom = iconY + 8
                };

                TogglePanel();
            }
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void TogglePanel()
    {
        _panelVisible = !_panelVisible;
        if (_panelVisible)
            _onShowPanel();
        else
            _onHidePanel();
    }

    /// <summary>
    /// Returns the approximate screen rect of the tray icon so the panel
    /// can position itself directly above it.
    /// </summary>
    public NativeMethods.RECT GetTrayIconRect() => _trayIconRect;

    /// <summary>
    /// Updates the tray icon tooltip to reflect the current voice state.
    /// </summary>
    public void UpdateTooltip(string tooltip)
    {
        if (_messageWindowHandle == IntPtr.Zero) return;
        _notifyIconData.szTip = tooltip;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref _notifyIconData);
    }

    public void Dispose()
    {
        if (_messageWindowHandle != IntPtr.Zero)
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _notifyIconData);
            NativeMethods.DestroyWindow(_messageWindowHandle);
            NativeMethods.PostQuitMessage(0);
            _messageWindowHandle = IntPtr.Zero;
        }
    }
}
