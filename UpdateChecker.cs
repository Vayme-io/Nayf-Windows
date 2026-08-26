using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>How far along an update is, in the terms the Settings row shows.</summary>
public enum UpdateStage
{
    /// <summary>Nothing has been checked yet this session.</summary>
    Unknown,
    Checking,
    UpToDate,
    Downloading,
    /// <summary>Fetched and verified. Waiting for a moment the user isn't using Vayme.</summary>
    ReadyToInstall,
    Failed
}

/// <summary>
/// Keeps an installed copy of Vayme current without the user having to fetch anything.
///
/// The installer has always been able to upgrade in place — it carries a stable product
/// code and installs per-user under %LOCALAPPDATA%, so running a newer one replaces the
/// old build silently and without a UAC prompt. What was missing is the part that notices
/// a newer one exists. This is that part: it asks the Worker what the current Windows
/// build is, downloads it if this machine is behind, checks it against the SHA-256 the
/// Worker published, and runs it once the user isn't in the middle of anything.
///
/// The hash check is the load-bearing step and fails closed. This class downloads an
/// executable and then runs it, so a manifest with no hash, a hash that doesn't match,
/// or a URL that isn't https gets the download thrown away rather than executed.
/// </summary>
public sealed class UpdateChecker : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// The version this process is running, normalised to three parts so it compares
    /// against a manifest that says "1.2.0" rather than "1.2.0.0".
    /// </summary>
    public static Version CurrentVersion { get; } =
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

    // At launch, and then every six hours for a copy that stays open. A check is one small
    // request, and starting the app is both the moment a user is most likely to be about to
    // use it and the moment least likely to have anything in flight to interrupt.
    //
    // This used to wait two minutes first, because a machine that has just booted often has
    // no network yet and a check then mostly measures how fast Wi-Fi associates. That is a
    // real problem, but delaying every launch is the expensive answer to it: a failed check
    // is retried instead, closely at first and then further apart, so a cold network costs
    // seconds rather than the whole six-hour interval.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    // How often "can we restart yet?" gets asked once a build is on disk, and how many
    // consecutive yeses it takes. Five quiet minutes rather than one: the gap between two
    // sentences of a conversation is idle too, and restarting into it would read as a crash.
    private static readonly TimeSpan RestartPollInterval = TimeSpan.FromSeconds(30);
    private const int ConsecutiveIdlePollsBeforeInstalling = 10;

    // Infinite on the client itself; every call below carries its own deadline. A 68 MB
    // installer on a slow line legitimately takes minutes, and HttpClient.Timeout would
    // cut the download off mid-stream with the same exception a genuine failure throws.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly JsonSerializerOptions ManifestJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private string? _readyInstallerPath;
    private bool _installStarted;
    private int _idleWatchRunning;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Asked before an update is applied. Returning false defers it — the update stays on
    /// disk and the question comes round again. Supplied by whoever knows what the user is
    /// doing; without it an update installs on the first poll, which is right for a host
    /// that has nothing to interrupt.
    /// </summary>
    public Func<bool>? IsSafeToRestart { get; set; }

    /// <summary>
    /// Raised once the installer is running and the app should shut down.
    ///
    /// The installer kills Vayme itself a moment later, but a taskkill takes whatever
    /// hasn't reached disk with it. Quitting properly first means memories and saved tasks
    /// land before the process goes.
    /// </summary>
    public event Action? RestartRequested;

    private UpdateStage _stage = UpdateStage.Unknown;
    public UpdateStage Stage
    {
        get => _stage;
        private set { if (_stage == value) return; _stage = value; OnPropertyChanged(); }
    }

    /// <summary>The published version, when it is newer than this one. Null otherwise.</summary>
    private Version? _availableVersion;
    public Version? AvailableVersion
    {
        get => _availableVersion;
        private set { if (_availableVersion == value) return; _availableVersion = value; OnPropertyChanged(); }
    }

    private int _downloadPercent;
    public int DownloadPercent
    {
        get => _downloadPercent;
        private set { if (_downloadPercent == value) return; _downloadPercent = value; OnPropertyChanged(); }
    }

    /// <summary>What to tell the user when a check or a download didn't work.</summary>
    private string _failureMessage = "";
    public string FailureMessage
    {
        get => _failureMessage;
        private set { if (_failureMessage == value) return; _failureMessage = value; OnPropertyChanged(); }
    }

    /// <summary>Begins checking in the background, and keeps checking for the app's lifetime.</summary>
    public void Start() => _ = RunAsync(_shutdown.Token);

    /// <summary>
    /// Checks now because the user asked, from the Settings row. Same work as the
    /// background loop, minus the wait.
    /// </summary>
    public async Task CheckNowAsync()
    {
        await CheckAsync(_shutdown.Token);
        if (Stage == UpdateStage.ReadyToInstall)
            _ = InstallWhenIdleAsync(_shutdown.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan retryDelay = FirstRetryDelay;

            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckAsync(cancellationToken);

                // Once a build is sitting on disk there is nothing left to poll a server
                // for. What the loop waits on from here is the machine going quiet.
                if (Stage == UpdateStage.ReadyToInstall)
                {
                    await InstallWhenIdleAsync(cancellationToken);
                    return;
                }

                if (Stage == UpdateStage.Failed)
                {
                    // Doubling rather than retrying at a fixed rate: the usual cause is a
                    // network that hasn't finished coming up, which clears in seconds, but
                    // the same failure covers an outage that won't clear at all — and that
                    // one shouldn't be asked about every fifteen seconds all day.
                    await Task.Delay(retryDelay, cancellationToken);
                    retryDelay = TimeSpan.FromTicks(
                        Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
                    continue;
                }

                retryDelay = FirstRetryDelay;
                await Task.Delay(CheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The app is closing.
        }
        catch (Exception ex)
        {
            Logger.Log("Update", $"Update loop stopped: {ex}");
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        // Zero-wait rather than queueing: if a check is already running, the answer this
        // one would give is the answer already on its way.
        if (!await _checkGate.WaitAsync(0, cancellationToken)) return;

        try
        {
            if (Stage == UpdateStage.ReadyToInstall) return;

            Stage = UpdateStage.Checking;

            ReleaseManifest? manifest = await FetchManifestAsync(cancellationToken);
            if (manifest is null)
            {
                Fail("Vayme couldn't check for updates.");
                return;
            }

            if (!Version.TryParse(manifest.Version, out Version? published))
            {
                Logger.Log("Update", $"Published version isn't a version: \"{manifest.Version}\"");
                Fail("Vayme couldn't check for updates.");
                return;
            }

            published = Normalize(published);
            Logger.Log("Update", $"installed={CurrentVersion} published={published}");

            if (published <= CurrentVersion)
            {
                AvailableVersion = null;
                Stage = UpdateStage.UpToDate;
                return;
            }

            AvailableVersion = published;

            string? installerPath = await ObtainInstallerAsync(manifest, published, cancellationToken);
            if (installerPath is null)
            {
                Fail($"Vayme couldn't download {published}.");
                return;
            }

            _readyInstallerPath = installerPath;
            Stage = UpdateStage.ReadyToInstall;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log("Update", $"Check failed: {ex}");
            Fail("Vayme couldn't check for updates.");
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private static async Task<ReleaseManifest?> FetchManifestAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));

        using HttpResponseMessage response =
            await Http.GetAsync(NayfConfig.LatestWindowsReleaseEndpoint, deadline.Token);

        if (!response.IsSuccessStatusCode)
        {
            Logger.Log("Update", $"Release manifest returned {(int)response.StatusCode}");
            return null;
        }

        string json = await response.Content.ReadAsStringAsync(deadline.Token);
        return JsonSerializer.Deserialize<ReleaseManifest>(json, ManifestJsonOptions);
    }

    /// <summary>
    /// Returns the path of an installer that has been verified against the manifest, or
    /// null if there isn't one to be had right now.
    /// </summary>
    private async Task<string?> ObtainInstallerAsync(
        ReleaseManifest manifest, Version version, CancellationToken cancellationToken)
    {
        string directory = UpdateDirectory;
        Directory.CreateDirectory(directory);
        string installerPath = Path.Combine(directory, $"Vayme-Setup-{version}.exe");

        // A build already fetched and verified outlives the app, so an update that arrives
        // while someone is mid-something isn't downloaded again after the next launch.
        if (File.Exists(installerPath) && await MatchesPublishedHashAsync(installerPath, manifest.Sha256, cancellationToken))
            return installerPath;

        DownloadPercent = 0;
        Stage = UpdateStage.Downloading;

        if (!await DownloadAsync(manifest.Url, installerPath, cancellationToken))
            return null;

        if (!await MatchesPublishedHashAsync(installerPath, manifest.Sha256, cancellationToken))
        {
            // Nothing runs that didn't hash to what the manifest promised. The likeliest
            // cause is benign — an edge still serving the previous build for up to an hour
            // after upload — and the next check picks the new one up once that clears.
            Logger.Log("Update", "Downloaded installer didn't match the published hash — discarded");
            TryDelete(installerPath);
            return null;
        }

        RemoveSupersededInstallers(directory, installerPath);
        return installerPath;
    }

    private static async Task<bool> MatchesPublishedHashAsync(
        string path, string? expectedHex, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedHex))
        {
            // Fail closed. An unhashed manifest is indistinguishable from a tampered one,
            // and what happens next is that this file gets executed.
            Logger.Log("Update", "Manifest carried no sha256 — refusing to run the download");
            return false;
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Log("Update", $"Could not hash {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> DownloadAsync(
        string url, string destinationPath, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            // Over plain http the installer could be swapped in flight — and so could the
            // manifest that carries the hash it is checked against.
            Logger.Log("Update", $"Refusing a non-https installer URL: {url}");
            return false;
        }

        string partialPath = destinationPath + ".partial";

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));

        try
        {
            using HttpResponseMessage response =
                await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            await using (Stream source = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (FileStream destination = File.Create(partialPath))
            {
                byte[] buffer = new byte[81920];
                long bytesWritten = 0;
                int bytesRead;

                while ((bytesRead = await source.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), deadline.Token);
                    bytesWritten += bytesRead;

                    if (totalBytes is > 0)
                        DownloadPercent = (int)(bytesWritten * 100 / totalBytes.Value);
                }
            }

            // Moved into place only once the whole file is there, so a download cut off
            // halfway can never be mistaken for a complete one on the next launch.
            File.Move(partialPath, destinationPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(partialPath);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log("Update", $"Download failed: {ex.Message}");
            TryDelete(partialPath);
            return false;
        }
    }

    private async Task InstallWhenIdleAsync(CancellationToken cancellationToken)
    {
        // One watcher only — the background loop and the Settings row can both arrive here.
        if (Interlocked.Exchange(ref _idleWatchRunning, 1) == 1) return;

        try
        {
            int consecutiveIdlePolls = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RestartPollInterval, cancellationToken);

                bool idle = IsSafeToRestart?.Invoke() ?? true;
                consecutiveIdlePolls = idle ? consecutiveIdlePolls + 1 : 0;

                if (consecutiveIdlePolls < ConsecutiveIdlePollsBeforeInstalling) continue;

                InstallNow();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // The app is closing. The installer stays on disk for next launch.
        }
        finally
        {
            Interlocked.Exchange(ref _idleWatchRunning, 0);
        }
    }

    /// <summary>
    /// Runs the verified installer and asks the app to shut down. Called by the idle
    /// watcher, and by the Settings row when the user would rather not wait.
    /// </summary>
    public void InstallNow()
    {
        string? installerPath = _readyInstallerPath;
        if (_installStarted || installerPath is null || !File.Exists(installerPath)) return;
        _installStarted = true;

        try
        {
            Logger.Log("Update", $"Installing {AvailableVersion} from {installerPath}");

            Process.Start(new ProcessStartInfo(installerPath)
            {
                // MERGETASKS keeps the installer's optional tasks from being re-applied
                // behind the user's back. Silently, they default to selected — so an update
                // would put back a desktop shortcut the user had deleted and re-tick "start
                // when I sign in" for someone who had turned it off. Deselecting them
                // touches nothing that is already there; the app owns both settings after
                // the first install anyway.
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL " +
                            "/MERGETASKS=\"!desktopicon,!startup\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _installStarted = false;
            Logger.Log("Update", $"Could not start the installer: {ex.Message}");
            Fail("Vayme couldn't install the update.");
            return;
        }

        RestartRequested?.Invoke();
    }

    /// <summary>
    /// Downloads live in the user's temp folder rather than beside the app or in
    /// %LOCALAPPDATA%\Vayme: an abandoned 68 MB installer there would sit forever, and
    /// the installer overwrites its own directory as part of installing.
    /// </summary>
    private static string UpdateDirectory => Path.Combine(Path.GetTempPath(), "Vayme", "updates");

    private static void RemoveSupersededInstallers(string directory, string keepPath)
    {
        try
        {
            foreach (string file in Directory.GetFiles(directory, "Vayme-Setup-*.exe"))
                if (!string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
                    TryDelete(file);
        }
        catch (Exception ex)
        {
            Logger.Log("Update", $"Could not tidy old installers: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* a leftover file in temp is not worth failing an update over */ }
    }

    private void Fail(string message)
    {
        FailureMessage = message;
        Stage = UpdateStage.Failed;
    }

    /// <summary>
    /// Three parts, always. Assembly versions carry four and manifests carry three, and
    /// Version treats an absent part as lower than a zero — so 1.2.0 would otherwise
    /// compare as older than the 1.2.0.0 already installed, and the update never lands.
    /// </summary>
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));

    /// <summary>
    /// Cancelled, not disposed. A check or a download may still be unwinding through the
    /// token when the app quits, and a disposed source turns that into an
    /// ObjectDisposedException on the way out of a process that is already leaving.
    /// </summary>
    public void Dispose() => _shutdown.Cancel();

    /// <summary>
    /// What the Worker publishes at /latest/windows, written by the release script.
    /// </summary>
    private sealed class ReleaseManifest
    {
        public string Version { get; set; } = "";
        public string Url { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
}
