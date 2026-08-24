using System;
using System.IO;

namespace NayfWindows;

/// <summary>
/// Where the app keeps everything it remembers about this user — the sign-in
/// session, settings, memories and saved agent tasks. The folder was called
/// Nayf before the app was renamed to Vayme, so the first resolve after an
/// upgrade brings the old folder's files across; without that the user is
/// silently signed out and their memories look deleted.
/// </summary>
public static class AppPaths
{
    private const string FolderName = "Vayme";
    private const string LegacyFolderName = "Nayf";

    private static readonly Lazy<string> LazyDataDirectory = new(Resolve);

    /// <summary>
    /// %LOCALAPPDATA%\Vayme, created on first use. Resolved once per process:
    /// the migration below has to run exactly once, and every caller wants the
    /// same answer anyway.
    /// </summary>
    public static string DataDirectory => LazyDataDirectory.Value;

    /// <summary>Full path to a file directly inside <see cref="DataDirectory"/>.</summary>
    public static string InDataDirectory(string fileName) =>
        Path.Combine(DataDirectory, fileName);

    private static string Resolve()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string directory = Path.Combine(localAppData, FolderName);
        string legacyDirectory = Path.Combine(localAppData, LegacyFolderName);

        // Only worth migrating when the new folder doesn't exist yet. Once the app
        // has written a single file of its own, the pre-rename copy is stale and
        // pulling it forward would undo whatever the user did after upgrading.
        bool shouldMigrate = !Directory.Exists(directory) && Directory.Exists(legacyDirectory);

        Directory.CreateDirectory(directory);

        if (shouldMigrate)
        {
            try
            {
                // Copy rather than move: a move interrupted halfway loses data
                // outright, and leaving the old folder in place means a build from
                // before the rename still finds the state it expects.
                foreach (string source in Directory.GetFiles(legacyDirectory))
                {
                    string destination = Path.Combine(directory, Path.GetFileName(source));
                    if (!File.Exists(destination))
                        File.Copy(source, destination);
                }
            }
            catch
            {
                // Whatever made the copy fail — a locked file, a permission the
                // profile doesn't have — the app still has to start. The user gets
                // a fresh state rather than a launch failure.
            }
        }

        return directory;
    }
}
