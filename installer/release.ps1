<#
.SYNOPSIS
    Builds a Vayme release and publishes it so installed copies update themselves.

.DESCRIPTION
    Every installed copy asks the Worker what the current Windows build is, roughly
    every six hours, and installs it if this machine is behind. That check reads one
    file - windows-latest.json in the nayf-releases bucket - so publishing a release
    means getting that file and the installer it names into R2, in that order.

    The steps, and why each one is here:

      1. Write the version into BOTH NayfWindows.csproj and Nayf.iss. The app compares
         its own assembly version against the published one, so a build that reports
         1.1.0 while the manifest says 1.2.0 downloads, installs, and then finds itself
         still behind - an update loop that repeats every six hours forever.
      2. Publish and compile the installer.
      3. Upload the installer under its versioned name, and again under the stable
         name the website's download button points at.
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
$installerName  = "Vayme-Setup-$Version.exe"
$installerPath  = Join-Path $PSScriptRoot "Output\$installerName"
$innoCompiler   = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'

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

$csproj = Get-Content $csprojPath -Raw
$csprojStamped = [regex]::Replace($csproj, '<Version>[^<]*</Version>', "<Version>$Version</Version>", 1)
if ($csprojStamped -eq $csproj -and $csproj -notmatch [regex]::Escape("<Version>$Version</Version>")) {
    throw "Could not find a <Version> element in $csprojPath"
}
[System.IO.File]::WriteAllText($csprojPath, $csprojStamped, (New-Object System.Text.UTF8Encoding($false)))

$inno = Get-Content $innoScriptPath -Raw
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

Write-Step 'Compiling the installer'

if (-not (Test-Path $innoCompiler)) {
    throw "Inno Setup 6 not found at $innoCompiler - install it from https://jrsoftware.org/isdl.php"
}

& $innoCompiler $innoScriptPath
Assert-LastExitCode 'ISCC'

if (-not (Test-Path $installerPath)) { throw "Expected installer at $installerPath" }

$installerHash = (Get-FileHash -Algorithm SHA256 -Path $installerPath).Hash
$installerSize = [math]::Round((Get-Item $installerPath).Length / 1MB, 1)

Write-Host "    $installerName - $installerSize MB"
Write-Host "    sha256 $installerHash"

if ($BuildOnly) {
    Write-Step 'BuildOnly - nothing published'
    Write-Host "    The installer is at $installerPath"
    return
}

# ---------------------------------------------------------------------------
# 3. Upload the installer (versioned, then the stable website link)
# ---------------------------------------------------------------------------

Write-Step 'Uploading the installer'

$installerContentType = 'application/vnd.microsoft.portable-executable'

# The name the updater fetches. Versioned, so it is safe to cache forever and can
# never be served stale the way an overwritten name can be.
npx wrangler r2 object put "$bucket/$installerName" --file="$installerPath" --content-type="$installerContentType" --remote
Assert-LastExitCode "wrangler r2 object put $installerName"

# The name the website's download button points at. Overwritten every release, which
# is why the updater does not use it.
npx wrangler r2 object put "$bucket/$stableInstallerKey" --file="$installerPath" --content-type="$installerContentType" --remote
Assert-LastExitCode "wrangler r2 object put $stableInstallerKey"

# ---------------------------------------------------------------------------
# 4. Announce it
# ---------------------------------------------------------------------------

Write-Step 'Publishing the release manifest'

$manifest = [ordered]@{
    version = $Version
    url     = "$downloadBaseUrl/$installerName"
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

Write-Step "Vayme $Version is live"
Write-Host '    Installed copies pick it up within about six hours, or immediately'
Write-Host '    from Settings -> Check for updates.'
Write-Host ''
Write-Host '    Still to do by hand:'
Write-Host '      - commit the version bump'
Write-Host '      - publish the site from Lovable if the download page changed'
