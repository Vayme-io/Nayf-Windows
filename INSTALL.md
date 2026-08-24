# Installing Vayme

This is the guide for **using** Vayme. If you want to build it from source, see
[README.md](README.md).

## What you need

- 64-bit Windows 10 (version 1809 or newer) or Windows 11
- A microphone
- An internet connection
- About 300 MB of disk space

Nothing else. You do **not** need to install .NET or the Windows App SDK — Vayme
carries its own copy of both, so there is nothing to install first.

## Get the installer

`Vayme-Setup-<version>.exe`, roughly 68 MB. Either someone sent it to you, or it
is on the [Releases page](https://github.com/Vayme-io/Nayf-Windows/releases).

It is not in the repository itself — it is a build artifact, and too large for git.
To produce one yourself, see [Building the installer](#building-the-installer).

## Install it

Double-click the file. **Windows will try to stop you** — see the next section.

The installer does not ask where to put things. It installs for you only, so it
never asks for an administrator password. It offers two optional extras:

- **Create a desktop shortcut**
- **Start Vayme automatically when I sign in** — worth taking. Vayme is a companion
  that lives in the tray; if it is not running you have to remember to start it.

Everything goes to `%LOCALAPPDATA%\Programs\Vayme`, plus a Start Menu shortcut and
an entry in Add or Remove Programs.

### Windows will warn you — this is expected

You will see a blue **"Windows protected your PC"** box.

Click **More info**, then **Run anyway**.

This appears because the installer is not code-signed. A signing certificate costs
a few hundred dollars a year, and Vayme does not have one yet, so Windows has no
publisher name to show you and warns about anything it does not recognise. The
warning is about the *absence of a certificate*, not about anything it detected in
the file.

If you would rather verify the file than take that on trust, compare its SHA-256
against the published one:

```powershell
Get-FileHash "$env:USERPROFILE\Downloads\Vayme-Setup.exe" -Algorithm SHA256
```

The hash every release is published under is in the release manifest, which is
also what the app itself checks a download against before installing it:

```powershell
(Invoke-RestMethod https://nayf-proxy.vayme.workers.dev/latest/windows).sha256
```

Your browser may also refuse the download for the same reason — choose **Keep** to
override it.

## First launch

**1. Make an account.** Vayme opens a sign-in window. Create an account with an
email and password, or sign in if you already have one. You may have to confirm
your email before you can sign in — check your inbox if the app says so.

**2. Allow the microphone.** Windows asks the first time you hold the push-to-talk
key. Say yes, or Vayme cannot hear you. If you miss the prompt, turn it on under
*Settings → Privacy & security → Microphone*.

**3. Look for the tray icon.** Vayme has no main window. It lives next to the clock,
sometimes hidden behind the **^** arrow — drag it onto the visible part of the tray
so you can reach it. Click it to open the panel.

## Using it

| Shortcut | What it does |
|---|---|
| **Hold Ctrl + Alt** | Talk. Release when finished, and Vayme answers out loud. |
| **Alt + T** | Type a request instead of speaking. Enter sends, Escape closes and keeps the draft. |
| **Hold Shift, then drag** | Circle part of the screen to ask about just that. Hold Shift on its own for a moment and the lasso opens; Shift used in a shortcut or a capital letter is ignored. |

Vayme sees your screen with each request, answers out loud, and points at things
with a blue cursor and on-screen marks. It speaks its answers rather than printing
them — the panel is for controls, not for reading a transcript.

The panel also holds your token balance, the things Vayme has remembered about you
(each of which you can delete), and past tasks.

## Updating

From 1.2.0 onward Vayme updates itself. It asks the release manifest what the
current build is two minutes after launch and every six hours after that, and if
this machine is behind it downloads the new installer, checks it against the
published SHA-256, and installs it silently. A download whose hash does not match
is discarded rather than run. You can also trigger the check by hand from the
panel: **Check for updates**.

**Builds before 1.2.0 have no updater at all.** If you are on 1.0.0 or 1.1.0,
nothing will arrive on its own — install 1.2.0 by hand once, and it will keep
itself current from then on.

Installing over the top by hand still works and is still safe: it closes Vayme if
it is running, installs over the existing copy, and keeps your account, settings
and memories. There is no need to uninstall first.

## Uninstalling

*Settings → Apps → Installed apps → Vayme → Uninstall*, or run `unins000.exe` from
the install folder. This removes the app, the shortcuts and the run-at-startup
entry.

Your account lives on the server, not on your PC, so uninstalling does not delete
it. Local data — your sign-in session, remembered facts and saved tasks — stays in
`%LOCALAPPDATA%\Vayme`. Delete that folder too if you want no trace left.

## If something goes wrong

Vayme writes a log to your Desktop as **`nayf-crash.log`**. It records each startup
step, so the last line before it stopped usually says what failed. Send it along
with any bug report.

**Vayme does not start, or vanishes immediately.** Check `nayf-crash.log`. A build
that stops right after `Session restored` is usually a Windows App SDK problem —
make sure you are on 1.1.0 or newer, since 1.0.0 only ran on machines that already
had the SDK installed.

**"Windows App SDK 2.0 runtime is not installed."** You are running 1.0.0. Get a
newer build; from 1.1.0 the runtime ships inside the app.

**Push-to-talk does nothing.** Check the microphone permission above, and check that
Vayme is actually running (look in the tray). Some full-screen games take exclusive
control of the keyboard and will swallow the shortcut.

**Vayme hears you but never answers.** Usually the token balance — the panel shows
what you have left. Otherwise check your internet connection.

**No sound.** Vayme speaks through your default playback device. If you changed
outputs after starting it, restart Vayme.

## Building the installer

For maintainers. Needs the .NET 10 SDK and [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish\Nayf
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\Nayf.iss
```

The result lands in `installer\Output\`. Keep `Version` in `NayfWindows.csproj` and
`MyAppVersion` in `installer\Nayf.iss` in step — the app and the installer should
report the same build.

The publish step must stay self-contained, and the csproj must keep
`WindowsAppSDKSelfContained`. Without it the payload carries only the Windows App
SDK *bootstrapper*, which looks for a runtime installed on the machine — so the app
runs on a development box and fails on everyone else's.
