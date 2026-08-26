<#
.SYNOPSIS
    Builds a Vayme release and publishes it so installed copies update themselves.

.DESCRIPTION
    Every installed copy asks the Worker what the current Windows build is when it
    starts, and every six hours after that if it is left running, and installs it if
    this machine is behind. That check reads one
    file - windows-latest.json in the nayf-releases bucket - so publishing a release
    means getting that file and the installer it names into R2, in that order.

    The steps, and why each one is here:

      1. Write the version into BOTH NayfWindows.csproj and Nayf.iss. The app compares
         its own assembly version against the published one, so a build that reports
         1.1.0 while the manifest says 1.2.0 downloads, installs, and then finds itself
         still behind - an update loop that repeats every six hours forever.
      2. Publish, then compile the installer TWICE - once whole, once without the
         speech model. The model is ~150 MB and identical in every release, so an
         update that carried it would spend that on every installed copy, every
         release, to deliver a few megabytes of app. Inno never deletes files a
         script does not list, so the update installer lands on an existing install
         and leaves its model alone.
      3. Upload both. The full one goes under the stable name the website's download
         button points at, so a first-time install arrives ready to listen. The
         model-less one goes under the versioned name, which is what the manifest
         names and therefore what the updater fetches.
      4. Upload the manifest LAST. Until it lands, no installed copy knows there is
         anything to fetch - which is exactly the right failure mode if a step above
         goes wrong. The reverse order announces a build that isn't there yet.

    Keep this file ASCII. Windows PowerShell 5.1 reads a script with no byte-order mark
    as Windows-1252, so a UTF-8 em dash arrives as three characters, the last of which
    decodes to a closing smart quote - and PowerShell honours that as a string
    terminator, so the parse dies somewhere unrelated to the actual mistake. The file
    is saved with a BOM too, but ASCII is what keeps that belt-and-braces rather than
    the only thing holding it together.

.EXAMPLE
    .\installer\release.ps1 -Version 1.2.0

.EXAMPLE
    .\installer\release.ps1 -Version 1.2.0 -BuildOnly
    Builds and stamps the version but uploads nothing.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Build and stamp the version, but publish nothing. For checking a build before
    # it becomes something every installed copy will pull down on its own.
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$csprojPath     = Join-Path $repositoryRoot 'NayfWindows.csproj'
$innoScriptPath = Join-Path $PSScriptRoot   'Nayf.iss'
$publishPath    = Join-Path $repositoryRoot 'publish\Nayf'
$innoCompiler   = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'

# The whole thing, model included. What a new user downloads from the website.
$fullInstallerName = "Vayme-Setup-$Version.exe"
$fullInstallerPath = Join-Path $PSScriptRoot "Output\$fullInstallerName"

# Everything but the model. What an installed copy fetches when it updates itself.
$updateInstallerName = "Vayme-Update-$Version.exe"
$updateInstallerPath = Join-Path $PSScriptRoot "Output\$updateInstallerName"

$bucket             = 'nayf-releases'
$stableInstallerKey = 'Nayf-Setup.exe'
$manifestKey        = 'windows-latest.json'
$downloadBaseUrl    = 'https://nayf-proxy.vayme.workers.dev/download/windows'

function Write-Step($message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Assert-LastExitCode($what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}

# ---------------------------------------------------------------------------
# 1. Stamp the version into both places that carry it
# ---------------------------------------------------------------------------

Write-Step "Stamping version $Version"

# Read through .NET, not Get-Content. Windows PowerShell 5.1 reads a file with no
# byte-order mark as Windows-1252, and both of these are UTF-8 without one. Every
# release therefore used to decode the em dashes in their comments into three
# Latin-1 characters and write those back out as UTF-8 - so each run corrupted them
# one layer further, and the diff for a version bump carried a pile of mojibake
# alongside the one line that actually changed.
$csproj = [System.IO.File]::ReadAllText($csprojPath)
$csprojStamped = [regex]::Replace($csproj, '<Version>[^<]*</Version>', "<Version>$Version</Version>", 1)
if ($csprojStamped -eq $csproj -and $csproj -notmatch [regex]::Escape("<Version>$Version</Version>")) {
    throw "Could not find a <Version> element in $csprojPath"
}
[System.IO.File]::WriteAllText($csprojPath, $csprojStamped, (New-Object System.Text.UTF8Encoding($false)))

$inno = [System.IO.File]::ReadAllText($innoScriptPath)
$innoStamped = [regex]::Replace($inno, '#define MyAppVersion "[^"]*"', "#define MyAppVersion `"$Version`"", 1)
if ($innoStamped -eq $inno -and $inno -notmatch [regex]::Escape("#define MyAppVersion `"$Version`"")) {
    throw "Could not find #define MyAppVersion in $innoScriptPath"
}
[System.IO.File]::WriteAllText($innoScriptPath, $innoStamped, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "    NayfWindows.csproj and Nayf.iss both say $Version"

# ---------------------------------------------------------------------------
# 2. Build
# ---------------------------------------------------------------------------

Write-Step 'Publishing the app'

# Cleared first: publish merges into whatever is already there, so a file dropped in
# a previous version would otherwise be carried into this installer forever.
if (Test-Path $publishPath) { Remove-Item $publishPath -Recurse -Force }

dotnet publish $csprojPath -c Release -r win-x64 --self-contained true -o $publishPath
Assert-LastExitCode 'dotnet publish'

Write-Step 'Compiling the installers'

if (-not (Test-Path $innoCompiler)) {
    throw "Inno Setup 6 not found at $innoCompiler - install it from https://jrsoftware.org/isdl.php"
}

# The publish output has to actually contain a model, or the full installer would
# quietly ship without one and every new user would download it themselves on first
# run. The build fetches it, so its absence means something upstream went wrong.
$publishedModel = Join-Path $publishPath 'Models\ggml-base.en.bin'
if (-not (Test-Path $publishedModel)) {
    throw "No speech model in the publish output at $publishedModel - the FetchWhisperModel target in NayfWindows.csproj should have downloaded it."
}

& $innoCompiler $innoScriptPath
Assert-LastExitCode 'ISCC (full)'

& $innoCompiler '/DSkipModel' $innoScriptPath
Assert-LastExitCode 'ISCC (update)'

if (-not (Test-Path $fullInstallerPath))   { throw "Expected installer at $fullInstallerPath" }
if (-not (Test-Path $updateInstallerPath)) { throw "Expected installer at $updateInstallerPath" }

$fullInstallerHash = (Get-FileHash -Algorithm SHA256 -Path $fullInstallerPath).Hash
$fullInstallerSize = [math]::Round((Get-Item $fullInstallerPath).Length / 1MB, 1)

# The manifest carries this one: it is what every installed copy downloads and checks.
$installerHash = (Get-FileHash -Algorithm SHA256 -Path $updateInstallerPath).Hash
$updateInstallerSize = [math]::Round((Get-Item $updateInstallerPath).Length / 1MB, 1)

Write-Host "    $fullInstallerName - $fullInstallerSize MB (website download)"
Write-Host "    sha256 $fullInstallerHash"
Write-Host "    $updateInstallerName - $updateInstallerSize MB (auto-update)"
Write-Host "    sha256 $installerHash"

# The split is the whole point of building twice, so it is worth failing on rather
# than discovering later as a 150 MB update.
if ($updateInstallerSize -ge $fullInstallerSize) {
    throw "The update installer ($updateInstallerSize MB) is not smaller than the full one ($fullInstallerSize MB), so the model was not excluded. Check the SkipModel block in Nayf.iss."
}

if ($BuildOnly) {
    Write-Step 'BuildOnly - nothing published'
    Write-Host "    The installers are at $fullInstallerPath"
    Write-Host "    and $updateInstallerPath"
    return
}

# ---------------------------------------------------------------------------
# 3. Upload the installer (versioned, then the stable website link)
# ---------------------------------------------------------------------------

Write-Step 'Uploading the installers'

$installerContentType = 'application/vnd.microsoft.portable-executable'

# The name the updater fetches, and the one the manifest names. Versioned, so it is
# safe to cache forever and can never be served stale the way an overwritten name can.
npx wrangler r2 object put "$bucket/$updateInstallerName" --file="$updateInstallerPath" --content-type="$installerContentType" --remote
Assert-LastExitCode "wrangler r2 object put $updateInstallerName"

# The full build, kept under its own versioned name so a specific release can always
# be installed from scratch, not only the current one.
npx wrangler r2 object put "$bucket/$fullInstallerName" --file="$fullInstallerPath" --content-type="$installerContentType" --remote
Assert-LastExitCode "wrangler r2 object put $fullInstallerName"

# The name the website's download button points at, which has to be the full build:
# it is somebody's first install, and it should arrive able to listen rather than
# downloading 150 MB of speech model before it can answer them.
npx wrangler r2 object put "$bucket/$stableInstallerKey" --file="$fullInstallerPath" --content-type="$installerContentType" --remote
Assert-LastExitCode "wrangler r2 object put $stableInstallerKey"

# ---------------------------------------------------------------------------
# 4. Announce it
# ---------------------------------------------------------------------------

Write-Step 'Publishing the release manifest'

# Points at the model-less build. An installed copy already has the model, and one
# that somehow does not downloads it by itself on the next start.
$manifest = [ordered]@{
    version = $Version
    url     = "$downloadBaseUrl/$updateInstallerName"
    sha256  = $installerHash
}

# No BOM: the app parses this as JSON, and a byte-order mark at the front of it is
# not valid JSON.
$manifestPath = Join-Path $env:TEMP $manifestKey
[System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json),
    (New-Object System.Text.UTF8Encoding($false)))

npx wrangler r2 object put "$bucket/$manifestKey" --file="$manifestPath" --content-type="application/json" --remote
Assert-LastExitCode "wrangler r2 object put $manifestKey"

# ---------------------------------------------------------------------------
# 5. Verify what a client would actually get
# ---------------------------------------------------------------------------

Write-Step 'Verifying the published release'

# Everything below is fetched unauthenticated over the public internet, exactly as
# an installed copy would fetch it. Checking the R2 upload succeeded is not the same
# as checking a user can reach it: the object and the route that serves it are
# separate things, and only one of them is uploaded here.
#
# The Mac routes are checked too, even though this script cannot break them. Both
# platforms deploy the SAME Cloudflare Worker from separate sources, and
# `wrangler deploy` publishes a whole tree, so whichever side deploys last silently
# deletes any route the other side added. That has already taken the Windows updater
# down once. A release is the one moment someone is watching, so it is the cheapest
# place to notice that the other platform's routes have gone missing.

$workerBaseUrl = 'https://nayf-proxy.vayme.workers.dev'

function Invoke-PublishedUrlCheck {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$What,
        # Installers run from ~70 MB to ~200 MB. Ask for one byte: the point is to
        # prove the route answers, not to pull the file down again on every release.
        [switch]$SingleByte
    )

    $failure = 'no response'

    # An R2 write and a Worker deploy both take a moment to reach every edge, so one
    # miss immediately after uploading means nothing. Three tries over ~20s does.
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $response = $null
        try {
            # HttpWebRequest rather than Invoke-WebRequest, because of Range. Range is
            # one of .NET's restricted headers: Windows PowerShell 5.1 refuses to set it
            # through -Headers and throws before the request is ever made. That throw is
            # a client-side argument error carrying no Response, so a catch block that
            # reads a status code off the response sees nothing and reports the route as
            # unreachable - a green release failing its own verification, which is how
            # this was found. AddRange is the supported way to ask for a byte range.
            $request = [System.Net.HttpWebRequest]::Create($Url)
            $request.Method = 'GET'
            $request.Timeout = 20000
            $request.UserAgent = 'vayme-release-check'
            if ($SingleByte) { $request.AddRange(0, 0) }

            $response = $request.GetResponse()
            $statusCode = [int]$response.StatusCode
            if ($statusCode -eq 200 -or $statusCode -eq 206) {
                Write-Host "    ok   $What"
                return
            }
            $failure = "HTTP $statusCode"
        }
        catch [System.Net.WebException] {
            $failure = $_.Exception.Message
            if ($null -ne $_.Exception.Response) {
                $failure = "HTTP $([int]$_.Exception.Response.StatusCode)"
            }
        }
        catch {
            # Anything that is not a WebException never reached the network, so report it
            # as itself rather than dressing it up as an unreachable route.
            $failure = $_.Exception.Message
        }
        finally {
            if ($null -ne $response) { $response.Close() }
        }

        if ($attempt -lt 3) { Start-Sleep -Seconds 10 }
    }

    Write-Host "    FAIL $What" -ForegroundColor Red
    throw "$What is not reachable at $Url ($failure). A 405 or 404 here usually means a Worker deploy dropped the route rather than anything being wrong with this release."
}

# --- the release this script just published ---

Invoke-PublishedUrlCheck -Url "$downloadBaseUrl/$updateInstallerName" -What "update installer $updateInstallerName" -SingleByte
Invoke-PublishedUrlCheck -Url "$downloadBaseUrl/$fullInstallerName" -What "full installer $fullInstallerName" -SingleByte
Invoke-PublishedUrlCheck -Url $downloadBaseUrl -What 'stable installer link (website download button)' -SingleByte

# The manifest is checked by content, not just reachability. Every installed copy
# trusts the sha256 in this file to decide whether a download was tampered with, so
# a manifest that does not describe the build just uploaded is worse than no
# manifest: it makes every client discard a perfectly good installer.
$publishedManifest = $null
for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
        $publishedManifest = Invoke-RestMethod -Uri "$workerBaseUrl/latest/windows" -TimeoutSec 20
        if ($publishedManifest.version -eq $Version) { break }
    }
    catch {
        $publishedManifest = $null
    }
    if ($attempt -lt 3) { Start-Sleep -Seconds 10 }
}

if ($null -eq $publishedManifest) {
    throw "The release manifest is not being served at $workerBaseUrl/latest/windows"
}
if ($publishedManifest.version -ne $Version) {
    throw "The manifest says version $($publishedManifest.version) but this release is $Version"
}
if ($publishedManifest.sha256 -ne $installerHash) {
    throw "The manifest sha256 does not match the installer just built. Clients would reject the download as tampered with."
}
Write-Host "    ok   manifest reports $Version with a matching sha256"

# --- the Mac routes, which this script must not have broken ---

Invoke-PublishedUrlCheck -Url "$workerBaseUrl/appcast.xml" -What 'Mac appcast (Sparkle update feed)'
Invoke-PublishedUrlCheck -Url "$workerBaseUrl/download" -What 'Mac stable download link' -SingleByte

# The versioned Mac download needs a version number, and this script has no business
# knowing which one is current. Read it out of the appcast that was just confirmed
# live. A parse failure is only a warning: the appcast format belongs to the Mac side
# and may change without this script being the right place to notice.
try {
    $appcastBody = (Invoke-WebRequest -Uri "$workerBaseUrl/appcast.xml" -UseBasicParsing -TimeoutSec 20).Content
    $versionMatch = [regex]::Match($appcastBody, 'sparkle:shortVersionString="([0-9]+(?:\.[0-9]+){1,2})"')
    if ($versionMatch.Success) {
        $latestMacVersion = $versionMatch.Groups[1].Value
        Invoke-PublishedUrlCheck -Url "$workerBaseUrl/download/$latestMacVersion" -What "Mac versioned download ($latestMacVersion)" -SingleByte
    }
    else {
        Write-Host '    warn no version found in the appcast; skipped the versioned Mac download check' -ForegroundColor Yellow
    }
}
catch {
    Write-Host "    warn could not read the appcast to find the current Mac version: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Step "Vayme $Version is live"
Write-Host '    Installed copies pick it up the next time they start, within about six'
Write-Host '    hours if left running, or immediately from Settings -> Check for updates.'
Write-Host ''
Write-Host '    Still to do by hand:'
Write-Host '      - commit the version bump'
Write-Host '      - publish the site from Lovable if the download page changed'
