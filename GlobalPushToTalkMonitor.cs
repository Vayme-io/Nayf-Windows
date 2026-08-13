using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace NayfWindows;

/// <summary>
/// Installs a system-wide low-level keyboard hook to detect Ctrl+Alt press/release
/// for push-to-talk. Uses WH_KEYBOARD_LL so it works even when the app is in
/// the background. Mirrors Mac's CGEvent tap approach.
///
/// "Ctrl+Alt" here means Ctrl and the *left* Alt with nothing else held — no Shift,
/// no Windows key, no letter. Right Alt is excluded because on European layouts it
/// is AltGr, and the keyboard driver synthesises a left-Ctrl press alongside it, so
/// counting it would fire push-to-talk every time the user typed @, $, {, [ or \.
///
/// "Nothing else held" can only be judged over time, not from a single event, which
/// is what <see cref="ArmingDelay"/> and <see cref="_isDisarmed"/> are for.
///
/// The same hook carries a second chord, Alt+T, for typing a request instead of
/// speaking it. That one is momentary — it fires once on key-down and has no
/// release to wait for.
/// </summary>
public sealed class GlobalPushToTalkMonitor : IDisposable
{
    public event Action? PushToTalkPressed;
    public event Action? PushToTalkReleased;

    /// <summary>Alt+T: the user wants to type their request rather than speak it.</summary>
    public event Action? TextInputRequested;

    /// <summary>
    /// Where the user let go of the left mouse button, in virtual-screen pixels. Raised
    /// only between <see cref="StartWatchingClicks"/> and <see cref="StopWatchingClicks"/>,
    /// and never in place of the click — the app underneath still receives it.
    /// </summary>
    public event Action<System.Drawing.Point>? MouseClicked;

    private IntPtr _hookHandle = IntPtr.Zero;
    private readonly NativeMethods.LowLevelKeyboardProc _hookCallback;
    private readonly DispatcherQueue _dispatcherQueue;

    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private readonly NativeMethods.LowLevelMouseProc _mouseHookCallback;

    private bool _isPttActive = false;

    /// <summary>
    /// Non-modifier keys currently held. Ctrl+Alt+Del, Ctrl+Alt+arrow and every
    /// other Ctrl+Alt shortcut passes through this hook, and none of them is a
    /// request to start talking.
    /// </summary>
    private readonly HashSet<uint> _otherKeysDown = new();

    /// <summary>
    /// Set once Ctrl+Alt is joined by anything else, and cleared only when every
    /// modifier is back up. Without it, releasing the V of Ctrl+Alt+V while still
    /// holding the modifiers would land back on the bare chord and start listening.
    /// </summary>
    private bool _isDisarmed;

    /// <summary>
    /// True between the Alt+T key-down we acted on and the matching key-up, so both
    /// ends of that one press are swallowed together.
    /// </summary>
    private bool _textChordHeld;

    /// <summary>
    /// How long Ctrl+Alt must be held *alone* before Nayf starts listening.
    ///
    /// A keyboard delivers one key per event, so Ctrl+Alt+V unavoidably passes
    /// through Ctrl+Alt on its way. Reacting the instant the chord matches meant
    /// every such shortcut started a recording and stopped it a frame later. This
    /// waits long enough to see whether another key is on its way, while staying
    /// short enough that push-to-talk still feels immediate — speech doesn't begin
    /// for a few hundred milliseconds after the keys go down anyway.
    /// </summary>
    private static readonly TimeSpan ArmingDelay = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherQueueTimer _armingTimer;

    public GlobalPushToTalkMonitor()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        // Keep a strong reference so the delegate is not garbage collected while the hook is active
        _hookCallback = HookCallback;
        _mouseHookCallback = MouseHookCallback;

        // The hook is installed from this same thread, so its callback and this timer
        // both run on the dispatcher — the state below needs no locking.
        _armingTimer = _dispatcherQueue.CreateTimer();
        _armingTimer.Interval = ArmingDelay;
        _armingTimer.IsRepeating = false;
        _armingTimer.Tick += (_, _) => OnArmingElapsed();
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;
        var hModule = NativeMethods.GetModuleHandle(null);
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _hookCallback,
            hModule,
            0
        );
        if (_hookHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Logger.Log("GlobalPTT", $"Failed to install keyboard hook, error={error}");
        }
        else
        {
            Logger.Log("GlobalPTT", "Keyboard hook installed");
        }
    }

    public void Stop()
    {
        // Directly, not via StopWatchingClicks: Stop runs at shutdown, and by then the
        // dispatcher may never get round to a queued item.
        UnhookMouse();

        if (_hookHandle == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _armingTimer.Stop();
        _isPttActive = false;
        _isDisarmed = false;
        _textChordHeld = false;
        _otherKeysDown.Clear();
    }

    /// <summary>
    /// Starts reporting clicks through <see cref="MouseClicked"/>.
    ///
    /// The hook is installed only while something is actually waiting for a click. A
    /// low-level mouse hook is called for every mouse *move* on the machine as well, and
    /// there is no reason for Nayf's process to be woken thousands of times a minute for
    /// the rest of the session.
    /// </summary>
    public void StartWatchingClicks()
    {
        // The hook's callback runs on whichever thread installed it, which has to be one
        // that pumps messages — so it is installed from the dispatcher like the keyboard
        // one, whoever calls this.
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_mouseHookHandle != IntPtr.Zero) return;

            _mouseHookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _mouseHookCallback,
                NativeMethods.GetModuleHandle(null),
                0
            );

            if (_mouseHookHandle == IntPtr.Zero)
                Logger.Log("GlobalPTT", $"Failed to install mouse hook, error={Marshal.GetLastWin32Error()}");
            else
                Logger.Log("GlobalPTT", "Mouse hook installed");
        });
    }

    /// <summary>Stops reporting clicks and takes the hook back out.</summary>
    public void StopWatchingClicks() => _dispatcherQueue.TryEnqueue(UnhookMouse);

    private void UnhookMouse()
    {
        if (_mouseHookHandle == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
        _mouseHookHandle = IntPtr.Zero;
        Logger.Log("GlobalPTT", "Mouse hook removed");
    }

    /// <summary>
    /// Watches for the click that finishes a walkthrough step.
    ///
    /// It never swallows anything. The click is the user operating their own application —
    /// the whole point of the step — and Nayf is only noting that it happened.
    /// </summary>
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == (int)NativeMethods.WM_LBUTTONUP)
        {
            var mouseStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var point = new System.Drawing.Point(mouseStruct.pt.X, mouseStruct.pt.Y);

            // Same rule as the text chord: never call out from inside a hook.
            _dispatcherQueue.TryEnqueue(() => MouseClicked?.Invoke(point));
        }

        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kbStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            bool isKeyDown = (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN);
            bool isKeyUp = (wParam == (IntPtr)NativeMethods.WM_KEYUP || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP);

            // Swallowing the key stops Alt+T from reaching the app underneath as
            // well, where it would trip whatever T is the menu mnemonic for.
            if ((isKeyDown || isKeyUp) && UpdateComboState(kbStruct.vkCode, isKeyDown))
                return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>
    /// Recomputes whether the push-to-talk combo is held, given the key event being
    /// delivered right now.
    ///
    /// Modifier state is read live from the system rather than accumulated across
    /// events. Tracking it in fields meant that any press whose release this hook
    /// never saw — a shortcut that hands off to the secure desktop, a window that
    /// swallows the key-up, anything that takes focus mid-chord — left a modifier
    /// latched down forever, and from then on Ctrl alone or Alt alone was enough to
    /// start recording. Reading the real state can't go stale.
    /// </summary>
    /// <returns>True if this key event should be swallowed rather than passed on.</returns>
    private bool UpdateComboState(uint vkCode, bool isKeyDown)
    {
        uint key = NormalizeToSide(vkCode);

        // The event we're handling hasn't necessarily reached the async key state
        // yet, so let it speak for its own key and query the system for the rest.
        bool Down(int vk) => vk == (int)key ? isKeyDown : NativeMethods.IsKeyDown(vk);

        // Only ever ask about a specific side. VK_CONTROL and VK_MENU answer for both
        // at once, so they cannot be corrected by the override above — and on the
        // AltGr key-up, VK_MENU still reads down while VK_RMENU already reads up,
        // which is precisely the moment AltGr masquerades as Ctrl + left Alt.
        bool ctrl = Down(NativeMethods.VK_LCONTROL) || Down(NativeMethods.VK_RCONTROL);
        bool alt = Down(NativeMethods.VK_LMENU);
        bool altGr = Down(NativeMethods.VK_RMENU);
        bool shift = Down(NativeMethods.VK_LSHIFT) || Down(NativeMethods.VK_RSHIFT);
        bool win = Down(NativeMethods.VK_LWIN) || Down(NativeMethods.VK_RWIN);

        // Whether this key was already down before the event — holding a key makes the
        // keyboard repeat its key-down, and one press should mean one chord.
        bool isRepeat = isKeyDown && _otherKeysDown.Contains(vkCode);

        if (!IsModifier(key))
        {
            if (isKeyDown) _otherKeysDown.Add(vkCode);
            else _otherKeysDown.Remove(vkCode);
        }

        // Once every modifier is up, forget any key whose release went missing —
        // otherwise a single unseen key-up would wedge push-to-talk off for good —
        // and take the chance to re-arm, since nothing is being held any more.
        if (!ctrl && !alt && !altGr && !shift && !win)
        {
            _otherKeysDown.Clear();
            _isDisarmed = false;
        }

        bool comboDown = ctrl && alt && !altGr && !shift && !win && _otherKeysDown.Count == 0;

        // Ctrl+Alt plus anything else is somebody using a shortcut, not asking to talk.
        if (ctrl && alt && !comboDown) _isDisarmed = true;

        SetListening(comboDown && !_isDisarmed);

        return UpdateTextChordState(vkCode, isKeyDown, isRepeat, ctrl, alt, altGr, shift, win);
    }

    /// <summary>
    /// The Alt+T chord. Unlike push-to-talk there is nothing to wait for: the request
    /// to type is complete the moment the key goes down, so it fires there and then.
    ///
    /// Left Alt only — right Alt is AltGr, which the layout turns into Ctrl+Alt, so
    /// AltGr+T would fire this on every keyboard that has one.
    /// </summary>
    /// <returns>True if this key event should be swallowed rather than passed on.</returns>
    private bool UpdateTextChordState(uint vkCode, bool isKeyDown, bool isRepeat,
        bool ctrl, bool alt, bool altGr, bool shift, bool win)
    {
        if ((int)vkCode != NativeMethods.VK_T) return false;

        if (!isKeyDown)
        {
            if (!_textChordHeld) return false;
            _textChordHeld = false;
            return true;
        }

        // Repeats belong to a press already handled — swallow them so the app
        // underneath doesn't start receiving a stream of T's mid-chord.
        if (_textChordHeld) return true;

        // T has to be the only non-modifier held, or this is the tail of some
        // longer shortcut that happens to pass through Alt+T.
        if (isRepeat || !alt || altGr || ctrl || shift || win || _otherKeysDown.Count != 1)
            return false;

        _textChordHeld = true;
        Logger.Log("GlobalPTT", "Text chord pressed");

        // Never call out from inside the hook: Windows unhooks callbacks that overrun
        // LowLevelHooksTimeout, and showing a window is nowhere near fast enough.
        _dispatcherQueue.TryEnqueue(() => TextInputRequested?.Invoke());
        return true;
    }

    /// <summary>
    /// Drives the press/release transition. Starting is deferred by
    /// <see cref="ArmingDelay"/> so a chord still being typed never triggers it;
    /// stopping is immediate, because a release is never ambiguous.
    /// </summary>
    private void SetListening(bool shouldListen)
    {
        if (!shouldListen)
        {
            _armingTimer.Stop();
            if (!_isPttActive) return;

            _isPttActive = false;
            Logger.Log("GlobalPTT", "Combo released");
            _dispatcherQueue.TryEnqueue(() => PushToTalkReleased?.Invoke());
            return;
        }

        if (_isPttActive || _armingTimer.IsRunning) return;
        _armingTimer.Start();
    }

    /// <summary>
    /// The chord survived the arming delay. Re-check it against live key state
    /// before committing — the keys may have gone up while we waited.
    /// </summary>
    private void OnArmingElapsed()
    {
        _armingTimer.Stop();
        if (_isPttActive || _isDisarmed || !IsComboHeldNow()) return;

        _isPttActive = true;
        Logger.Log("GlobalPTT", "Combo pressed");
        PushToTalkPressed?.Invoke();
    }

    /// <summary>Reads the combo straight from the system, with no event in flight.</summary>
    private bool IsComboHeldNow()
    {
        bool ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_LCONTROL) ||
                    NativeMethods.IsKeyDown(NativeMethods.VK_RCONTROL);
        bool alt = NativeMethods.IsKeyDown(NativeMethods.VK_LMENU);
        bool altGr = NativeMethods.IsKeyDown(NativeMethods.VK_RMENU);
        bool shift = NativeMethods.IsKeyDown(NativeMethods.VK_LSHIFT) ||
                     NativeMethods.IsKeyDown(NativeMethods.VK_RSHIFT);
        bool win = NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) ||
                   NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);

        return ctrl && alt && !altGr && !shift && !win && _otherKeysDown.Count == 0;
    }

    /// <summary>
    /// Maps a side-agnostic modifier onto its left-hand key. A real keyboard always
    /// reports a side through this hook; the bare codes only arrive from injected
    /// input, and Windows applies those to the left key too.
    /// </summary>
    private static uint NormalizeToSide(uint vkCode) => (int)vkCode switch
    {
        NativeMethods.VK_CONTROL => NativeMethods.VK_LCONTROL,
        NativeMethods.VK_MENU => NativeMethods.VK_LMENU,
        NativeMethods.VK_SHIFT => NativeMethods.VK_LSHIFT,
        _ => vkCode
    };

    private static bool IsModifier(uint vkCode) => (int)vkCode is
        NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL or
        NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU or
        NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT or
        NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// P/Invoke declarations for Win32 API calls used throughout the app.
/// Centralizing them here avoids duplication across files.
/// </summary>
public static class NativeMethods
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;   // Alt key
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;   // AltGr on European layouts
    public const int VK_SHIFT = 0x10;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_T = 0x54;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>True while the key is physically held, regardless of focus.</summary>
    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    // Window style constants
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const uint LWA_ALPHA = 0x00000002;
    public const uint LWA_COLORKEY = 0x00000001;

    // SetWindowPos flags
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    // DWM
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int leftWidth;
        public int rightWidth;
        public int topHeight;
        public int bottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // Notification icon flags
    public const uint NIF_MESSAGE = 0x01;
    public const uint NIF_ICON = 0x02;
    public const uint NIF_TIP = 0x04;
    public const uint NIF_GUID = 0x20;
    public const uint NIM_ADD = 0x00;
    public const uint NIM_MODIFY = 0x01;
    public const uint NIM_DELETE = 0x02;
    public const uint NIM_SETVERSION = 0x04;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint WM_APP_TRAY = 0x8001;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint NIN_SELECT = 0x0400;
    public const uint NIN_KEYSELECT = 0x0401;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    public const int WH_MOUSE_LL = 14;

    /// <summary>
    /// The mouse equivalent of <see cref="KBDLLHOOKSTRUCT"/>. <c>pt</c> is already in
    /// virtual-screen pixels, which is the space annotations are drawn in.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
    public static readonly IntPtr IDC_ARROW = new(32512);
    public static readonly IntPtr IDC_HAND = new(32649);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadImage(IntPtr hInstance, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // Windows refuses SetForegroundWindow from a process the user hasn't just
    // interacted with. Sharing an input queue with the current foreground thread
    // for the duration of the call is what lifts that — see ForceForeground.
    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    /// <summary>
    /// Takes the foreground reliably, even when the request came from a global
    /// hotkey rather than a click on one of our own windows.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == hwnd)
        {
            SetFocus(hwnd);
            return;
        }

        uint targetThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = targetThread != 0 && targetThread != thisThread &&
                        AttachThreadInput(thisThread, targetThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, targetThread, false);
        }
    }

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public const int SW_MINIMIZE = 6;
    public const int SW_SHOWMINNOACTIVE = 7;

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpmsg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2,
        int nWidthEllipse, int nHeightEllipse);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    public const uint SPI_GETWORKAREA = 0x0030;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
        ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
    public const uint MONITORINFOF_PRIMARY = 0x00000001;
}
