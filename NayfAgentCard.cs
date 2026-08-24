using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace NayfWindows;

/// <summary>
/// The floating result card for one agent task — the Windows counterpart of the Mac's
/// <c>AgentResultPanel</c>. It sits in the top-right corner as a small accent chip and
/// opens to the full result when the pointer reaches it, so a finished task stays within
/// reach without taking a corner of the screen hostage.
///
/// <para>Not a window itself: it is the state machine over two of them. Both states are
/// layered, per-pixel-alpha windows — <see cref="NativeAgentCardWindow"/> opened and
/// <see cref="NativeAgentAvatarWindow"/> collapsed — because neither shape is one DWM will
/// cut for a top-level window, and exactly one of them is on screen at a time.</para>
/// </summary>
public sealed class NayfAgentCard
{
    /// <summary>How long the card stays open after the pointer leaves it.</summary>
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// How long a freshly-opened card stays expanded before collapsing on its own. The
    /// user opened it to read the result, so it opens showing the result; the chip is
    /// what it settles down to once they have.
    /// </summary>
    private static readonly TimeSpan InitialDwell = TimeSpan.FromSeconds(8);

    // MARK: - State

    /// <summary>Raised when the user dismisses the card, so the host can drop it.</summary>
    public event Action<NayfAgentCard>? Dismissed;

    /// <summary>Raised when the user asks to continue this task.</summary>
    public event Action<Guid>? FollowUpRequested;

    public Guid TaskId { get; private set; }

    /// <summary>How far down from the top of the stack this card sits, in points.</summary>
    public int OffsetY { get; private set; }

    /// <summary>
    /// How much of the stack this card is currently taking — the opened card's height, or
    /// the chip canvas when it is folded away. The host stacks by this rather than by a
    /// fixed pitch, because the two differ by 144pt and anything below would land on top
    /// of an opened card.
    /// </summary>
    public int StackHeight => _isExpanded
        ? (int)NayfAgentCardArt.Height
        : (int)NayfAgentAvatarArt.CanvasSize;

    /// <summary>Raised when this card opens or folds away, so the stack can close up behind it.</summary>
    public event Action? StackHeightChanged;

    private string _summary = "";
    private bool _isExpanded = true;
    private bool _isFollowUpListening;
    private DateTimeOffset _pointerLeftAt = DateTimeOffset.MaxValue;
    private DateTimeOffset _openedAt;
    private NativeMethods.RECT _workArea;
    private double _scale = 1.0;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _hoverTimer;
    private DispatcherTimer? _copyRevertTimer;

    private readonly NativeAgentCardWindow _card;
    private NativeAgentAvatarWindow? _avatar;
    private bool _closed;

    public NayfAgentCard()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _card = new NativeAgentCardWindow();
        _card.DismissClicked += () => OnUiThread(Dismiss);
        _card.CopyClicked += () => OnUiThread(CopySummary);
        _card.FollowUpClicked += () => OnUiThread(ToggleFollowUp);

        // Hover is polled rather than taken from the window's own enter/leave messages.
        // The card swaps between two windows of very different sizes as it opens and
        // closes, which moves its edges out from under a stationary cursor — the messages
        // then fire, or fail to, according to which window the cursor ended up inside, and
        // the card latches open or shut. Asking where the cursor actually is sidesteps it.
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hoverTimer.Tick += (_, _) => UpdateHoverState();
    }

    /// <summary>
    /// Both native windows raise their events on their own message-loop threads, and
    /// everything they lead to — dismissing the card, arming a follow-up — belongs to the
    /// UI thread.
    /// </summary>
    private void OnUiThread(Action action) => _dispatcher.TryEnqueue(() => { if (!_closed) action(); });

    // MARK: - Presentation

    /// <summary>
    /// Shows this card for <paramref name="task"/> at <paramref name="offsetY"/> points down
    /// the stack, or refreshes it in place if it is already up.
    /// </summary>
    public void ShowTask(SavedAgentTask task, int offsetY)
    {
        bool isNew = TaskId != task.Id;
        TaskId = task.Id;
        OffsetY = offsetY;
        _summary = task.Summary;

        _card.SetContent(task.Title, task.Summary, task.Accent);

        // A refresh of a card the user is already reading must not yank it shut or throw
        // it back open — only a card arriving for the first time gets the opening dwell.
        if (isNew)
        {
            _isExpanded = true;
            _isFollowUpListening = false;
            _card.SetListening(false);
            _card.SetCopyLabel("Copy");

            // The chip drifts on a phase taken from the task id, so it is built per task
            // rather than per card and replaced if a card is ever reused for another one.
            _avatar?.Dispose();
            _avatar = new NativeAgentAvatarWindow(task.Id);
            _avatar.Clicked += OnAvatarClicked;
        }

        _openedAt = DateTimeOffset.UtcNow;
        _pointerLeftAt = DateTimeOffset.MaxValue;
        (_workArea, _scale) = MeasureTarget();

        ApplyExpansionState();
        _hoverTimer.Start();
    }

    /// <summary>Moves the card up or down the stack, e.g. after one above it closes or opens.</summary>
    public void MoveToOffset(int offsetY)
    {
        if (OffsetY == offsetY) return;
        OffsetY = offsetY;
        _avatar?.MoveToOffset(offsetY);
        _card.MoveToOffset(offsetY);
    }

    public void Dismiss()
    {
        _hoverTimer.Stop();
        _avatar?.Hide();
        _card.Hide();
        Dismissed?.Invoke(this);
    }

    /// <summary>
    /// Tears both windows down. Destroyed rather than pooled: a card belongs to a task and
    /// there is no bound on how many tasks a session runs through.
    /// </summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;

        _hoverTimer.Stop();
        _copyRevertTimer?.Stop();
        _copyRevertTimer = null;

        _avatar?.Dispose();
        _avatar = null;
        _card.Dispose();
    }

    // MARK: - Hover

    /// <summary>
    /// Opens the card while the pointer is over it and closes it a beat after it leaves.
    /// The delay is what makes the card survive the diagonal drag from its chip to its
    /// buttons, which briefly clips the corner and would otherwise close it mid-reach.
    /// </summary>
    private void UpdateHoverState()
    {
        bool pointerInside = _isExpanded
            ? _card.ContainsCursor()
            // Collapsed, the card window is off screen and the chip is standing in for it —
            // and the chip is smaller than its own frame, most of which is the clear margin
            // its glow needs. Asking that window where its target is beats measuring a
            // rectangle the user can't see.
            : _avatar?.ContainsCursor() ?? false;

        if (pointerInside)
        {
            _pointerLeftAt = DateTimeOffset.MaxValue;
            if (!_isExpanded)
            {
                _isExpanded = true;
                ApplyExpansionState();
            }
            return;
        }

        if (!_isExpanded) return;

        // A card that just opened holds itself open long enough to be read, whether or
        // not the pointer ever finds it — the user asked to see this, and having it fold
        // away before they look up would defeat opening it at all.
        if (DateTimeOffset.UtcNow - _openedAt < InitialDwell) return;

        // Never collapse out from under a follow-up: the label is the only indication
        // that Vayme is listening, and it lives on the expanded card.
        if (_isFollowUpListening) return;

        if (_pointerLeftAt == DateTimeOffset.MaxValue)
        {
            _pointerLeftAt = DateTimeOffset.UtcNow;
            return;
        }

        if (DateTimeOffset.UtcNow - _pointerLeftAt < CollapseDelay) return;

        _isExpanded = false;
        ApplyExpansionState();
    }

    /// <summary>Clicking the chip opens the card, for anyone who doesn't wait on hover.</summary>
    private void OnAvatarClicked() => OnUiThread(() =>
    {
        if (_isExpanded) return;
        _openedAt = DateTimeOffset.UtcNow;
        _pointerLeftAt = DateTimeOffset.MaxValue;
        _isExpanded = true;
        ApplyExpansionState();
    });

    /// <summary>
    /// Swaps between the two windows this card is made of. Exactly one is up at a time —
    /// both at once would show the result and the chip stacked on each other in the corner.
    /// </summary>
    private void ApplyExpansionState()
    {
        // Before either window is placed, not after: this card has just changed height, and
        // the host has to push the rest of the stack clear before anything is put on screen
        // at an offset that is about to be wrong.
        StackHeightChanged?.Invoke();

        if (_isExpanded)
        {
            _avatar?.Hide();
            _card.Show(OffsetY, _workArea, _scale);
        }
        else
        {
            _card.Hide();
            // Handed this card's work area and scale rather than letting the chip find its
            // own, so both states land on the same screen at the same size.
            _avatar?.Show(_card.Accent, OffsetY, _workArea, _scale);
        }
    }

    /// <summary>
    /// The monitor the card belongs to, and its scale. Fixed when the card is shown rather
    /// than re-decided per frame, so a card doesn't walk between displays with the cursor.
    /// </summary>
    private static (NativeMethods.RECT WorkArea, double Scale) MeasureTarget()
    {
        var workArea = new NativeMethods.RECT();
        double scale = 1.0;

        if (NativeMethods.GetCursorPos(out var pt))
        {
            IntPtr monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new NativeMethods.MONITORINFOEX
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                };
                if (NativeMethods.GetMonitorInfo(monitor, ref info)) workArea = info.rcWork;

                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                    scale = dpiX / 96.0;
            }
        }

        if (workArea.Right == workArea.Left)
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref workArea, 0);

        return (workArea, scale);
    }

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    private const int MDT_EFFECTIVE_DPI = 0;

    // MARK: - Actions

    private void CopySummary()
    {
        if (_summary.Length == 0) return;

        bool copied = CopyToClipboard(_summary);

        // The button confirms itself — a card with no notification area of its own has
        // nowhere else to say the copy happened.
        _card.SetCopyLabel(copied ? "Copied" : "Copy failed");

        _copyRevertTimer?.Stop();
        _copyRevertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _copyRevertTimer.Tick += (_, _) =>
        {
            _copyRevertTimer?.Stop();
            _copyRevertTimer = null;
            _card.SetCopyLabel("Copy");
        };
        _copyRevertTimer.Start();
    }

    private void ToggleFollowUp()
    {
        _isFollowUpListening = !_isFollowUpListening;
        _card.SetListening(_isFollowUpListening);
        FollowUpRequested?.Invoke(TaskId);
    }

    /// <summary>
    /// Called by the host when the turn started from this card is over, so the button
    /// stops claiming Vayme is listening once it has stopped.
    /// </summary>
    public void EndFollowUp()
    {
        if (!_isFollowUpListening) return;
        _isFollowUpListening = false;
        _card.SetListening(false);
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard through Win32 rather than the WinRT
    /// <c>Clipboard</c> class. Vayme is unpackaged, and the WinRT clipboard's behaviour
    /// without package identity is not something to find out about from a button that
    /// silently does nothing. <see cref="SelectedTextReader"/> reads the clipboard the
    /// same way for the same reason.
    /// </summary>
    private static bool CopyToClipboard(string text)
    {
        IntPtr block = IntPtr.Zero;
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                EmptyClipboard();

                int bytes = (text.Length + 1) * 2; // UTF-16 plus its terminator
                block = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (block == IntPtr.Zero) return false;

                IntPtr target = GlobalLock(block);
                if (target == IntPtr.Zero) return false;
                try { System.Runtime.InteropServices.Marshal.Copy((text + "\0").ToCharArray(), 0, target, text.Length + 1); }
                finally { GlobalUnlock(block); }

                // The clipboard owns the block once this succeeds — freeing it here
                // would hand the next paste a dangling pointer.
                if (SetClipboardData(CF_UNICODETEXT, block) == IntPtr.Zero) return false;
                block = IntPtr.Zero;
                return true;
            }
            finally { CloseClipboard(); }
        }
        catch { return false; }
        finally { if (block != IntPtr.Zero) GlobalFree(block); }
    }

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
