using System;
using System.Collections.Generic;

namespace NayfWindows;

/// <summary>
/// Where Vayme's overlays sit in the always-on-top band, relative to Vayme's own windows.
///
/// <para>Every overlay re-asserts its place in the band — most on a two-second timer, the
/// cursor on every frame — because another app creating a topmost window of its own drops
/// ours in behind it. The trouble is that <c>HWND_TOPMOST</c> means "the front of the whole
/// band", and the Ask Vayme box is in that band too: a couple of seconds after the box
/// opened, the step outline jumped in front of it and drew straight across the text being
/// typed into it.</para>
///
/// <para>An overlay is a mark on someone else's screen. A box the user is typing into is not
/// something to draw over, so overlays assert themselves to the front of everything except
/// Vayme's own interactive windows — which is the whole of what this decides.</para>
/// </summary>
internal static class OverlayZOrder
{
    /// <summary>
    /// Vayme's windows that the overlays must stay under, lowest in the band first.
    /// </summary>
    private static readonly List<IntPtr> Interactive = new();
    private static readonly object Gate = new();

    /// <summary>
    /// Registers a window the overlays must not cover.
    ///
    /// Registration order is the band order, lowest first: the panel is up before the Ask
    /// box opens over it, so an overlay placed behind the panel is behind the box as well.
    /// </summary>
    public static void RegisterInteractive(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (Gate)
            if (!Interactive.Contains(hwnd)) Interactive.Add(hwnd);
    }

    /// <summary>
    /// The <c>hWndInsertAfter</c> for an overlay's SetWindowPos: the lowest of Vayme's
    /// visible interactive windows, or the front of the band when none of them is up.
    /// </summary>
    public static IntPtr InsertAfter()
    {
        lock (Gate)
        {
            foreach (var hwnd in Interactive)
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) continue;

                // Only worth sitting behind while it is itself topmost. Inserting behind a
                // window that has dropped out of the band would take the overlay down with
                // it — a mark that vanishes under the app it is pointing at is a worse
                // failure than the one this exists to fix.
                int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                if ((exStyle & NativeMethods.WS_EX_TOPMOST) == 0) continue;

                return hwnd;
            }
        }

        return NativeMethods.HWND_TOPMOST;
    }
}
