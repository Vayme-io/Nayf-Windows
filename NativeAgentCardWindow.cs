using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace NayfWindows;

/// <summary>
/// The opened agent result card, drawn by <see cref="NayfAgentCardArt"/> into a layered,
/// per-pixel-alpha window.
///
/// <para>This was a WinUI window until the card was redrawn to match the Mac's. The Mac
/// rounds it at 18 with continuous curvature; DWM rounds a top-level window at 8 and has
/// no setting for anything else, and a system backdrop paints the whole window rectangle
/// behind the XAML tree, so a tile rounded at 18 in XAML only leaves a squarer slab
/// showing past its corners. Per-pixel alpha is the only way to get the shape.</para>
///
/// <para>Unlike the status pill and the action toast, this window is neither click-through
/// nor decorative: it carries three controls and a scrolling summary, so it hit-tests its
/// own layout and reports hover back into the artwork. Layered windows hit-test on alpha,
/// which means the transparent corners pass clicks through to whatever is behind them
/// without any of that being asked for.</para>
/// </summary>
public sealed class NativeAgentCardWindow : IDisposable
{
    // Raised on the window's own thread.
    public event Action? DismissClicked;
    public event Action? CopyClicked;
    public event Action? FollowUpClicked;

    private readonly object _gate = new();

    /// <summary>
    /// One window class per card, for the same reason the idle chip has one per chip:
    /// RegisterClassEx fails for the second card and CreateWindowEx then quietly hands it
    /// the FIRST card's WndProc, so every click on the newer card would act on the older
    /// one — and dismissing that one would leave the survivor calling a collected delegate.
    /// </summary>
    private readonly string _className = "NayfAgentCard_" + Guid.NewGuid().ToString("N");

    /// <summary>Set once the window exists, so the first <see cref="Show"/> can't outrun it.</summary>
    private readonly ManualResetEventSlim _ready = new();

    // Guarded by _gate: written from the UI thread, read on the window's thread.
    private readonly AgentCardModel _model = new();
    private bool _visible;
    private int _offsetY;
    private NativeMethods.RECT _workArea;
    private float _scale = 1f;
    private bool _placementDirty = true;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _messageThread;
    private NativeMethods.WndProc? _wndProcDelegate;

    // Window-thread only.
    private AgentCardLayout _layout = new();
    private int _windowX, _windowY, _pxWidth, _pxHeight;
    private bool _isWindowShown;
    private bool _trackingMouse;
    private bool _disposed;

    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOPMOST     = 0x00000008;
    private const int WS_POPUP          = unchecked((int)0x80000000);

    private const int SW_HIDE           = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_SETCURSOR     = 0x0020;
    private const uint WM_MOUSEMOVE     = 0x0200;
    private const uint WM_LBUTTONUP     = 0x0202;
    private const uint WM_MOUSEWHEEL    = 0x020A;
    private const uint WM_MOUSELEAVE    = 0x02A3;
    private const int  MA_NOACTIVATE    = 3;

    /// <summary>Asks the window's own thread to redraw from the current model.</summary>
    private const uint WM_APP_INVALIDATE = 0x8000 + 0x20;

    /// <summary>Asks the window's own thread to tear itself down — see <see cref="Dispose"/>.</summary>
    private const uint WM_APP_TEARDOWN   = 0x8000 + 0x21;

    /// <summary>How far one notch of the wheel moves the summary.</summary>
    private const float WheelStep = 40f;

    public NativeAgentCardWindow()
    {
        _messageThread = new Thread(RunWindowThread)
        {
            IsBackground = true,
            Name = "NayfAgentCard"
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();

        // Waited for rather than polled: with the window in hand every later call can just
        // post to it, instead of every one of them having to cope with not having a window
        // yet. It takes a few milliseconds.
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    // MARK: - Presentation

    /// <summary>
    /// Puts the card on screen <paramref name="offsetY"/> points down the stack.
    ///
    /// <para>The work area and DPI scale are handed in rather than looked up, so the card
    /// and the chip it collapses to land on the same screen at the same size — see
    /// <see cref="NativeAgentAvatarWindow.Show"/>, which takes them for the same reason.</para>
    /// </summary>
    public void Show(int offsetY, NativeMethods.RECT workArea, double scale)
    {
        lock (_gate)
        {
            _offsetY = offsetY;
            _workArea = workArea;
            _scale = scale > 0 ? (float)scale : 1f;
            _visible = true;
            _placementDirty = true;
        }
        Invalidate();
    }

    public void Hide()
    {
        lock (_gate)
        {
            _visible = false;
            // Nothing is hovered on a card that isn't there, and a card that comes back up
            // with a button still lit would be lit by a cursor that has long since moved.
            _model.Hover = AgentCardTarget.None;
        }
        Invalidate();
    }

    public void MoveToOffset(int offsetY)
    {
        lock (_gate)
        {
            _offsetY = offsetY;
            _placementDirty = true;
        }
        Invalidate();
    }

    /// <summary>Sets what the card says. The summary scrolls back to the top when it changes.</summary>
    public void SetContent(string title, string summary, Color accent)
    {
        lock (_gate)
        {
            if (_model.Summary != summary) _model.ScrollOffset = 0f;
            _model.Title = title;
            _model.Summary = summary;
            _model.Accent = accent;
        }
        Invalidate();
    }

    /// <summary>The task's colour, so the chip this card collapses to can be painted in it.</summary>
    public Color Accent
    {
        get { lock (_gate) return _model.Accent; }
    }

    /// <summary>The Copy button reports back through its own label — see the card host.</summary>
    public void SetCopyLabel(string label)
    {
        lock (_gate) _model.CopyLabel = label;
        Invalidate();
    }

    public void SetListening(bool listening)
    {
        lock (_gate) _model.Listening = listening;
        Invalidate();
    }

    /// <summary>Whether the cursor is anywhere on the card, used to hold it open.</summary>
    public bool ContainsCursor()
    {
        lock (_gate) { if (!_visible) return false; }
        if (_hwnd == IntPtr.Zero) return false;
        if (!NativeMethods.GetCursorPos(out var pt)) return false;
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect)) return false;

        return pt.X >= rect.Left && pt.X < rect.Right &&
               pt.Y >= rect.Top && pt.Y < rect.Bottom;
    }

    private void Invalidate()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        PostMessage(_hwnd, WM_APP_INVALIDATE, IntPtr.Zero, IntPtr.Zero);
    }

    // MARK: - Window

    private void RunWindowThread()
    {
        _wndProcDelegate = WndProc;
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc   = _wndProcDelegate,
            hInstance     = NativeMethods.GetModuleHandle(null),
            lpszClassName = _className
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            _className, "Nayf Agent Result",
            WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        _ready.Set();
        if (_hwnd == IntPtr.Zero) return;

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        // Only once the loop is done and the window with it, or the atom stays claimed.
        UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // The card is clicked but must never pull the foreground off whatever the user
            // was typing into, which is why the window is WS_EX_NOACTIVATE at all.
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;

            // The window class carries no cursor, so nothing would otherwise set one and
            // the card would inherit whatever the last window the pointer crossed left
            // behind — a text caret dragged in from an editor, most of the time.
            case WM_SETCURSOR:
                SetCursor(LoadCursor(IntPtr.Zero,
                    HitTestCursor() == AgentCardTarget.None ? IDC_ARROW : IDC_HAND));
                return new IntPtr(1);

            case WM_MOUSEMOVE:
                OnMouseMove(LoWord(lParam), HiWord(lParam));
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                _trackingMouse = false;
                SetHover(AgentCardTarget.None);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                OnClick(HitTest(LoWord(lParam), HiWord(lParam)));
                return IntPtr.Zero;

            case WM_MOUSEWHEEL:
                OnWheel((short)HiWord(wParam));
                return IntPtr.Zero;

            case WM_APP_INVALIDATE:
                try { RenderFrame(); }
                catch (Exception ex) { Logger.Log("AgentCard", $"frame failed: {ex.Message}"); }
                return IntPtr.Zero;

            case WM_APP_TEARDOWN:
                NativeMethods.DestroyWindow(hWnd);
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // MARK: - Input

    private void OnMouseMove(int x, int y)
    {
        if (!_trackingMouse)
        {
            // Without this there is no WM_MOUSELEAVE, and whichever button the pointer was
            // over when it left the card stays lit until it comes back.
            var track = new TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = TME_LEAVE,
                hwndTrack = _hwnd,
                dwHoverTime = 0
            };
            _trackingMouse = TrackMouseEvent(ref track);
        }

        SetHover(HitTest(x, y));
    }

    private void SetHover(AgentCardTarget target)
    {
        lock (_gate)
        {
            if (_model.Hover == target) return;
            _model.Hover = target;
        }
        try { RenderFrame(); }
        catch (Exception ex) { Logger.Log("AgentCard", $"hover frame failed: {ex.Message}"); }
    }

    private void OnClick(AgentCardTarget target)
    {
        switch (target)
        {
            case AgentCardTarget.Dismiss:  DismissClicked?.Invoke(); break;
            case AgentCardTarget.Copy:     CopyClicked?.Invoke(); break;
            case AgentCardTarget.FollowUp: FollowUpClicked?.Invoke(); break;
        }
    }

    private void OnWheel(int delta)
    {
        lock (_gate)
        {
            if (_layout.ScrollMax <= 0f) return;
            float step = delta / 120f * WheelStep * _scale;
            _model.ScrollOffset = Math.Clamp(_model.ScrollOffset - step, 0f, _layout.ScrollMax);
        }
        try { RenderFrame(); }
        catch (Exception ex) { Logger.Log("AgentCard", $"scroll frame failed: {ex.Message}"); }
    }

    /// <summary>Which control the cursor is over right now, in client coordinates.</summary>
    private AgentCardTarget HitTestCursor()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return AgentCardTarget.None;
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect)) return AgentCardTarget.None;
        return HitTest(pt.X - rect.Left, pt.Y - rect.Top);
    }

    private AgentCardTarget HitTest(int x, int y)
    {
        var layout = _layout;
        if (layout.Dismiss.Contains(x, y)) return AgentCardTarget.Dismiss;
        if (layout.Copy.Contains(x, y)) return AgentCardTarget.Copy;
        if (layout.FollowUp.Contains(x, y)) return AgentCardTarget.FollowUp;
        return AgentCardTarget.None;
    }

    private static int LoWord(IntPtr value) => unchecked((short)(long)value);
    private static int HiWord(IntPtr value) => unchecked((short)((long)value >> 16));

    // MARK: - Frame

    private void RenderFrame()
    {
        if (_hwnd == IntPtr.Zero || _disposed) return;

        AgentCardModel model;
        bool visible;
        int offsetY;
        NativeMethods.RECT workArea;
        float scale;
        bool placementDirty;
        lock (_gate)
        {
            model = _model.Clone();
            visible = _visible;
            offsetY = _offsetY;
            workArea = _workArea;
            scale = _scale;
            placementDirty = _placementDirty;
            _placementDirty = false;
        }

        if (!visible)
        {
            if (_isWindowShown)
            {
                ShowWindow(_hwnd, SW_HIDE);
                _isWindowShown = false;
            }
            return;
        }

        if (placementDirty || _pxWidth == 0) Place(offsetY, workArea, scale);

        using var canvas = NayfAgentCardArt.Render(model, scale, out var layout);
        _layout = layout;

        // The summary can shorten — a task refreshed with a briefer result — and leave the
        // old offset scrolled past the end of the new text.
        if (model.ScrollOffset > layout.ScrollMax)
        {
            lock (_gate) _model.ScrollOffset = layout.ScrollMax;
        }

        Premultiply(canvas);
        ApplyLayeredWindow(canvas, _windowX, _windowY);

        if (!_isWindowShown)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _isWindowShown = true;
        }

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Pins the card to the top-right of the work area, on the same insets the chip uses —
    /// the two are one card in two states and must not appear to jump as it opens.
    /// </summary>
    private void Place(int offsetY, NativeMethods.RECT workArea, float scale)
    {
        if (workArea.Right == workArea.Left)
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref workArea, 0);

        _pxWidth = (int)MathF.Ceiling(NayfAgentCardArt.Width * scale);
        _pxHeight = (int)MathF.Ceiling(NayfAgentCardArt.Height * scale);
        _windowX = workArea.Right - (int)(NayfAgentCardHost.RightInset * scale) - _pxWidth;
        _windowY = workArea.Top + (int)((NayfAgentCardHost.TopInset + offsetY) * scale);
    }

    // MARK: - Layered window update

    /// <summary>
    /// Scales each colour channel by its own alpha, in place.
    ///
    /// <para>GDI+ works in straight alpha and UpdateLayeredWindow reads premultiplied, and
    /// nothing in between converts. Handing it straight alpha makes the compositor add the
    /// full colour where it should add a fraction of it — the same fix the idle chip needs,
    /// and here it is what keeps the antialiased corners from ringing white.</para>
    /// </summary>
    private static unsafe void Premultiply(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < data.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < data.Width; x++)
                {
                    byte* px = row + x * 4;   // B, G, R, A
                    int a = px[3];
                    if (a == 255) continue;
                    if (a == 0) { px[0] = px[1] = px[2] = 0; continue; }
                    px[0] = (byte)(px[0] * a / 255);
                    px[1] = (byte)(px[1] * a / 255);
                    px[2] = (byte)(px[2] * a / 255);
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
    }

    private void ApplyLayeredWindow(Bitmap bitmap, int windowX, int windowY)
    {
        IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDC    = CreateCompatibleDC(screenDC);
        IntPtr hBmp     = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBmp   = SelectObject(memDC, hBmp);

        var size  = new SIZE  { cx = bitmap.Width, cy = bitmap.Height };
        var ptSrc = new PT    { x = 0, y = 0 };
        var ptDst = new PT    { x = windowX, y = windowY };
        var blend = new BLEND { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };

        UpdateLayeredWindow(_hwnd, screenDC, ref ptDst, ref size,
                            memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);

        SelectObject(memDC, oldBmp);
        DeleteObject(hBmp);
        DeleteDC(memDC);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
    }

    /// <summary>
    /// There is a card per task and they are thrown away as tasks are dismissed, so the
    /// teardown has to be complete: a window can only be destroyed by the thread that
    /// created it — calling DestroyWindow from here just fails — and the message loop has
    /// to be told to stop, or every task the user runs leaves a thread behind.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_APP_TEARDOWN, IntPtr.Zero, IntPtr.Zero);
            _hwnd = IntPtr.Zero;
        }

        _messageThread?.Join(TimeSpan.FromMilliseconds(500));
        _messageThread = null;
        _ready.Dispose();
    }

    // MARK: - Interop

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct PT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLEND { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    private const uint TME_LEAVE = 0x00000002;
    private const int IDC_ARROW = 32512;
    private const int IDC_HAND  = 32649;

    private const byte AC_SRC_OVER  = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int  ULW_ALPHA    = 0x02;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref PT pptSrc, int crKey, ref BLEND pblend, int dwFlags);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
