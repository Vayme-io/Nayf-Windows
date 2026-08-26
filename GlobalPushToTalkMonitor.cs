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
///
/// And a third: Shift held on its own, which opens the region-focus lasso. It has the
/// same shape as push-to-talk — a hold with a beginning and an end — but a stricter
/// entry condition, because Shift is the busiest key on the keyboard: it leads every
/// capital letter and every Shift+click, and neither of those is a request to draw.
/// </summary>
public sealed class GlobalPushToTalkMonitor : IDisposable
{
    public event Action? PushToTalkPressed;
    public event Action? PushToTalkReleased;

    /// <summary>Alt+T: the user wants to type their request rather than speak it.</summary>
    public event Action? TextInputRequested;

    /// <summary>Shift has been held on its own long enough to mean "let me circle something".</summary>
    public event Action? RegionFocusStarted;

    /// <summary>Shift released while the lasso was open — whatever was drawn is the region.</summary>
    public event Action? RegionFocusFinished;

    /// <summary>The lasso was abandoned: Escape, or Shift turning out to be part of a shortcut.</summary>
    public event Action? RegionFocusCancelled;

    /// <summary>
    /// Where the user let go of the left mouse button, in virtual-screen pixels. Raised
    /// only between <see cref="StartWatchingClicks"/> and <see cref="StopWatchingClicks"/>,
    /// and never in place of the click — the app underneath still receives it.
    /// </summary>
    public event Action<System.Drawing.Point>? MouseClicked;

    /// <summary>
    /// The one key a walkthrough step is waiting for has been pressed. Raised only between
    /// <see cref="WatchForKey"/> and <see cref="StopWatchingKeys"/>, only for that exact key,
    /// and never in place of it — the app underneath still receives the keystroke.
    /// </summary>
    public event Action? WatchedKeyPressed;

    /// <summary>
    /// The virtual-key code a step is waiting for, or 0 for none.
    ///
    /// One specific key rather than a general key-down event, because the keyboard hook sees
    /// everything the user types and there is no reason for any of it to reach the rest of
    /// the app. Matching in here means the only thing that leaves this class is "the key we
    /// were told to expect happened".
    /// </summary>
    private uint _watchedKeyCode;

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
    /// How long Ctrl+Alt must be held *alone* before Vayme starts listening.
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

    /// <summary>True between the confirmed Alt hold and the release that ends it.</summary>
    private bool _isRegionFocusActive;

    /// <summary>
    /// Set the moment Shift is joined by anything else, and cleared only when every modifier
    /// is back up. Without it, releasing the A of Shift+A while still holding Shift would
    /// land back on a bare Shift and start drawing over the document being typed into.
    /// </summary>
    private bool _regionInvalidated;

    /// <summary>
    /// True between the Escape key-down that closed a lasso and its key-up, so both ends of
    /// that press are swallowed together and the app underneath never sees half of it.
    /// </summary>
    private bool _regionEscapeHeld;

    /// <summary>
    /// How long Shift must be held *alone* before the lasso opens.
    ///
    /// Longer than <see cref="ArmingDelay"/>, and longer than the Mac's 0.2s, because ⌥ does
    /// nothing on its own whereas Shift is pressed constantly — it leads every capital letter
    /// and every Shift+click. This delay is the gap between pressing Shift and pressing what
    /// it modifies, and it has to be long enough to sit out an ordinary one.
    /// </summary>
    private static readonly TimeSpan RegionHoldDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherQueueTimer _regionHoldTimer;

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

        _regionHoldTimer = _dispatcherQueue.CreateTimer();
        _regionHoldTimer.Interval = RegionHoldDelay;
        _regionHoldTimer.IsRepeating = false;
        _regionHoldTimer.Tick += (_, _) => OnRegionHoldElapsed();
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
        _regionHoldTimer.Stop();
        _isPttActive = false;
        _isDisarmed = false;
        _textChordHeld = false;
        _isRegionFocusActive = false;
        _regionInvalidated = false;
        _regionEscapeHeld = false;
        _watchedKeyCode = 0;
        _otherKeysDown.Clear();
    }

    /// <summary>
    /// Starts reporting clicks through <see cref="MouseClicked"/>.
    ///
    /// The hook is installed only while something is actually waiting for a click. A
    /// low-level mouse hook is called for every mouse *move* on the machine as well, and
    /// there is no reason for Vayme's process to be woken thousands of times a minute for
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

    /// <summary>
    /// Starts reporting one key through <see cref="WatchedKeyPressed"/>.
    ///
    /// No hook to install — the keyboard hook is already there for push-to-talk, so this is
    /// only a filter on what it reports. Passing 0 watches for nothing.
    /// </summary>
    public void WatchForKey(uint virtualKey)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _watchedKeyCode = virtualKey;
            Logger.Log("GlobalPTT", $"Watching for key 0x{virtualKey:X2}");
        });
    }

    /// <summary>Stops reporting the watched key.</summary>
    public void StopWatchingKeys() => _dispatcherQueue.TryEnqueue(() => _watchedKeyCode = 0);

    /// <summary>
    /// The virtual-key code for a key named the way a person would say it — "M", "Enter",
    /// "F5", "Space" — or 0 when it isn't one we can watch for.
    ///
    /// Deliberately narrow. A step whose key isn't here waits to be told it is done instead,
    /// which is slower but always works; guessing at a code would leave the user pressing a
    /// key nothing was listening for.
    /// </summary>
    public static uint VirtualKeyFor(string? keyName)
    {
        var name = keyName?.Trim() ?? "";
        if (name.Length == 0) return 0;

        // A single letter or digit is its own code — the VK values for A–Z and 0–9 are the
        // ASCII codes of the uppercase characters.
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
        }

        // F1–F24 run consecutively from VK_F1.
        if ((name[0] == 'F' || name[0] == 'f') && name.Length is 2 or 3 &&
            int.TryParse(name.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
        {
            return (uint)(0x70 + fn - 1);
        }

        return name.ToLowerInvariant() switch
        {
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "space" or "spacebar" => 0x20,
            "tab" => 0x09,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "page up" or "pgup" => 0x21,
            "pagedown" or "page down" or "pgdn" => 0x22,
            "up" or "up arrow" => 0x26,
            "down" or "down arrow" => 0x28,
            "left" or "left arrow" => 0x25,
            "right" or "right arrow" => 0x27,
            _ => 0
        };
    }

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
    /// the whole point of the step — and Vayme is only noting that it happened.
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

            // The key a walkthrough step is waiting for. Reported, never swallowed: pressing
            // it is the user operating their own application — the whole point of the step —
            // and Vayme is only noting that it happened.
            if (isKeyDown && _watchedKeyCode != 0 && kbStruct.vkCode == _watchedKeyCode)
            {
                // Ctrl+S is not the S the step asked for. Shift is allowed through because a
                // capital letter is still that letter, and a step may well name one.
                bool modified =
                    NativeMethods.IsKeyDown(NativeMethods.VK_LCONTROL) ||
                    NativeMethods.IsKeyDown(NativeMethods.VK_RCONTROL) ||
                    NativeMethods.IsKeyDown(NativeMethods.VK_LMENU) ||
                    NativeMethods.IsKeyDown(NativeMethods.VK_RMENU) ||
                    NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) ||
                    NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);

                // Same rule as the text chord: never call out from inside a hook.
                if (!modified) _dispatcherQueue.TryEnqueue(() => WatchedKeyPressed?.Invoke());
            }

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

        // Region focus wants the two Shifts told apart, and it has to be asked here rather
        // than inside that state machine: only this scope has Down(), and on the very event
        // that starts the hold — left Shift going down — the async key state has not caught
        // up yet, so querying the system directly answers "up" and the hold never begins.
        bool leftShiftOnly = Down(NativeMethods.VK_LSHIFT) && !Down(NativeMethods.VK_RSHIFT);

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
            _regionInvalidated = false;
        }

        bool comboDown = ctrl && alt && !altGr && !shift && !win && _otherKeysDown.Count == 0;

        // Ctrl+Alt plus anything else is somebody using a shortcut, not asking to talk.
        if (ctrl && alt && !comboDown) _isDisarmed = true;

        SetListening(comboDown && !_isDisarmed);

        // Both chords get a say in whether the key is swallowed, and both must run: they
        // watch different keys, and short-circuiting would leave one of them blind to an
        // event the other happened to claim first.
        bool swallowForRegion = UpdateRegionFocusState(
            vkCode, isKeyDown, ctrl, alt, altGr, shift, win, leftShiftOnly);
        bool swallowForText = UpdateTextChordState(vkCode, isKeyDown, isRepeat, ctrl, alt, altGr, shift, win);
        return swallowForRegion || swallowForText;
    }

    /// <summary>
    /// The region-focus hold: left Shift, by itself, for <see cref="RegionHoldDelay"/>.
    ///
    /// <para><b>Left Shift only.</b> Holding the *right* Shift for eight seconds is the
    /// Windows accessibility shortcut that turns on Filter Keys, and a lasso is easily held
    /// that long. Left Shift has no such timer on it.</para>
    ///
    /// <para><b>Nothing is swallowed except Escape.</b> Unlike Alt, releasing Shift on its own
    /// does nothing to the app underneath, so there is nothing to suppress — and suppressing
    /// it would be actively harmful, because an application that never sees the release goes
    /// on believing Shift is down and types in capitals from then on.</para>
    /// </summary>
    /// <returns>True if this key event should be swallowed rather than passed on.</returns>
    private bool UpdateRegionFocusState(uint vkCode, bool isKeyDown,
        bool ctrl, bool alt, bool altGr, bool shift, bool win, bool leftShiftOnly)
    {
        // Second half of the Escape that closed a lasso, swallowed to match its key-down.
        if ((int)vkCode == NativeMethods.VK_ESCAPE && !isKeyDown && _regionEscapeHeld)
        {
            _regionEscapeHeld = false;
            return true;
        }

        bool shiftAlone = leftShiftOnly && !ctrl && !alt && !altGr && !win &&
                          _otherKeysDown.Count == 0;

        if (shift && !shiftAlone) _regionInvalidated = true;

        if (shiftAlone && !_regionInvalidated)
        {
            if (!_isRegionFocusActive && !_regionHoldTimer.IsRunning) _regionHoldTimer.Start();
            return false;
        }

        _regionHoldTimer.Stop();
        if (!_isRegionFocusActive) return false;
        _isRegionFocusActive = false;

        // Shift going up is the user finishing their loop. Anything else arriving on top of it
        // means they were reaching for a shortcut and the lasso was never the point.
        if (!shift)
        {
            Logger.Log("GlobalPTT", "Region focus released");
            _dispatcherQueue.TryEnqueue(() => RegionFocusFinished?.Invoke());
            return false;
        }

        Logger.Log("GlobalPTT", "Region focus cancelled");
        _dispatcherQueue.TryEnqueue(() => RegionFocusCancelled?.Invoke());

        // Escape here means "not this after all", so it belongs to the lasso rather than to
        // whatever is behind it — which would otherwise close its dialog at the same time.
        if ((int)vkCode == NativeMethods.VK_ESCAPE && isKeyDown)
        {
            _regionEscapeHeld = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The Shift hold survived <see cref="RegionHoldDelay"/>. Re-read the keys before opening
    /// anything, exactly as <see cref="OnArmingElapsed"/> does — the hold may have ended
    /// while the delay ran, and an overlay put up for a key nobody is holding never comes down.
    /// </summary>
    private void OnRegionHoldElapsed()
    {
        _regionHoldTimer.Stop();

        if (_isRegionFocusActive || _regionInvalidated || !IsShiftAloneHeldNow())
        {
            // Logged because every reason this fires and then declines is invisible from the
            // outside: the user held the key, nothing appeared, and nothing said why.
            Logger.Log("GlobalPTT",
                $"Region hold declined (active={_isRegionFocusActive} " +
                $"invalidated={_regionInvalidated} shiftAlone={IsShiftAloneHeldNow()} " +
                $"otherKeys={_otherKeysDown.Count})");
            return;
        }

        // A button already down means a Shift+drag is in progress — extending a selection,
        // scrubbing a timeline, resizing from a handle. The Shift there belongs to the drag.
        if (IsAnyMouseButtonDown())
        {
            _regionInvalidated = true;
            Logger.Log("GlobalPTT", "Region hold declined (mouse button down)");
            return;
        }

        _isRegionFocusActive = true;
        Logger.Log("GlobalPTT", "Region focus hold confirmed");
        RegionFocusStarted?.Invoke();
    }

    /// <summary>
    /// Whether any mouse button is physically down. Read from the system rather than through
    /// a hook: a low-level mouse hook fires on every mouse *move* on the machine, which is far
    /// too high a price for a question asked once per Shift hold.
    /// </summary>
    internal static bool IsAnyMouseButtonDown() =>
        NativeMethods.IsKeyDown(NativeMethods.VK_LBUTTON) ||
        NativeMethods.IsKeyDown(NativeMethods.VK_RBUTTON) ||
        NativeMethods.IsKeyDown(NativeMethods.VK_MBUTTON);

    /// <summary>Reads the bare-Shift hold straight from the system, with no event in flight.</summary>
    private bool IsShiftAloneHeldNow()
    {
        bool ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_LCONTROL) ||
                    NativeMethods.IsKeyDown(NativeMethods.VK_RCONTROL);
        bool alt = NativeMethods.IsKeyDown(NativeMethods.VK_LMENU) ||
                   NativeMethods.IsKeyDown(NativeMethods.VK_RMENU);
        bool win = NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) ||
                   NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);

        return NativeMethods.IsKeyDown(NativeMethods.VK_LSHIFT) &&
               !NativeMethods.IsKeyDown(NativeMethods.VK_RSHIFT) &&
               !ctrl && !alt && !win && _otherKeysDown.Count == 0;
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
    public const int VK_ESCAPE = 0x1B;

    // Mouse buttons are virtual keys too, so GetAsyncKeyState reports them.
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_MBUTTON = 0x04;

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
