using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace NayfWindows;

/// <summary>
/// Keeps one copy of Vayme running at a time, and points a second launch at the copy
/// that is already there.
///
/// <para>Nothing enforced this before, and two copies do not coexist quietly. Each one
/// installs its own low-level keyboard hook, so Ctrl+Alt reaches both and two panels
/// race to answer it; each one opens the microphone, so whichever loses is handed
/// silence and shows the user a warning about their microphone that has nothing to do
/// with their microphone. Both were reported from the same machine, and both stop
/// being possible here.</para>
///
/// <para>The case this is really for is an upgrade that did not finish the job: the
/// installer runs, the old build is still in the tray because Restart Manager could not
/// close it, and the new build launches beside it. The user then has two Vaymes, one of
/// them a version behind, and no way to tell from the tray which icon is which.</para>
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// Session-local rather than <c>Global\</c>, deliberately: two people signed into the
    /// same machine each get their own Vayme, which is right — the microphone and the
    /// keyboard they are competing for are per-session too.
    ///
    /// The installer names this same string in its <c>AppMutex</c> directive, which is how
    /// it knows to close a running copy before overwriting the files underneath it. The
    /// two have to stay in step.
    /// </summary>
    public const string MutexName = "Vayme.SingleInstance";

    /// <summary>
    /// Held for the life of the process. Static because a mutex that gets collected is a
    /// mutex that gets released, and a released mutex would let a second copy in an hour
    /// into the session Vayme is still using.
    /// </summary>
    private static Mutex? _mutex;

    /// <summary>
    /// True if this process is the only Vayme, false if another one already has the
    /// session — in which case that one has been asked to show itself and this one should
    /// exit without starting.
    /// </summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName);
            if (_mutex.WaitOne(TimeSpan.Zero)) return true;
        }
        catch (AbandonedMutexException)
        {
            // The previous Vayme died without releasing it. The wait still succeeded and
            // this process now owns the mutex, which is the outcome we wanted; the
            // exception is only Windows mentioning how it became free.
            return true;
        }
        catch (Exception)
        {
            // Somebody else's object under the same name, or a session that will not let
            // us create one. Refusing to start over that would be worse than the duplicate
            // it is meant to prevent.
            return true;
        }

        ActivateRunningInstance();
        return false;
    }

    /// <summary>
    /// Asks the Vayme that is already running to open its panel.
    ///
    /// <para>Without this the second launch would simply vanish, and "I clicked the icon
    /// and nothing happened" is the exact complaint that started this — swapping one
    /// silent failure for another is not a fix.</para>
    ///
    /// <para>The message goes to the tray window rather than to a window the user can see,
    /// because the panel is hidden most of the time and the tray message loop is the one
    /// part of Vayme that is always listening. It reuses the tray menu's own Open command,
    /// so a launch and a click on "Open Vayme" end up in the same place.</para>
    /// </summary>
    private static void ActivateRunningInstance()
    {
        try
        {
            var tray = FindWindow(SystemTrayManager.MessageWindowClassName, null);
            if (tray == IntPtr.Zero) return;

            PostMessage(tray, SystemTrayManager.WM_COMMAND,
                (IntPtr)SystemTrayManager.MENU_ID_OPEN, IntPtr.Zero);
        }
        catch
        {
            // The running copy is mid-shutdown, or its tray window has gone. Exiting
            // quietly is still the right end for this process.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
