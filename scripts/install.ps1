<#
.SYNOPSIS
    Hall9k bootstrap install (backlog 42): fetches the latest GitHub release for Windows
    x64, verifies its checksum, asks consent, and hands off to the release's own
    `h9k install --from-release` for the actual placement — binaries into
    ~/.hall9k/bin, h9k onto the PATH, the canonical skill set into ~/.hall9k/skills,
    Hall9k's own Postgres definition written (never started). Finishes by running
    `h9k doctor` so a fresh machine ends setup knowing what still needs attention.

    Needs no repo checkout and no .NET SDK — only `gh`, authenticated against the
    release's repository (a private repo's releases need `gh auth login` first).

.PARAMETER Yes
    Skip the consent prompt.

.PARAMETER Repo
    The GitHub repository to fetch releases from (default: Hallmanac/hall9k).

.PARAMETER Tag
    A specific release tag instead of the latest.

.EXAMPLE
    iwr https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.ps1 | iex

.EXAMPLE
    & ([scriptblock]::Create((iwr https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.ps1).Content)) -Yes
#>
[CmdletBinding()]
param(
    [switch]$Yes,
    [string]$Repo = "Hallmanac/hall9k",
    [string]$Tag = ""
)

$ErrorActionPreference = "Stop"

function Fail($message) {
    Write-Error "hall9k install: $message"
    exit 1
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "gh (the GitHub CLI) is required — install it from https://cli.github.com, then gh auth login."
}
& gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    Fail "gh is not authenticated — run gh auth login first (needed to read this repository's releases)."
}

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($arch -ne [System.Runtime.InteropServices.Architecture]::X64) {
    Fail "no release for Windows on $arch — release.yml builds win-x64 only."
}
$rid = "win-x64"
$archiveName = "hall9k-$rid.zip"

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("h9k-install-" + [System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $workDir | Out-Null
try {
    Write-Host "Fetching the latest release for $rid from $Repo…"
    $downloadArgs = @("release", "download")
    if ($Tag) { $downloadArgs += $Tag }
    $downloadArgs += @("--repo", $Repo, "--pattern", $archiveName, "--pattern", "checksums.txt", "--dir", $workDir, "--clobber")
    & gh @downloadArgs
    if ($LASTEXITCODE -ne 0) {
        Fail "gh release download failed — check gh auth status and that $Repo has a release carrying $archiveName."
    }

    $archivePath = Join-Path $workDir $archiveName
    $checksumsPath = Join-Path $workDir "checksums.txt"
    if (-not (Test-Path $archivePath)) { Fail "$archiveName was not among the downloaded release assets." }
    if (-not (Test-Path $checksumsPath)) { Fail "checksums.txt was not among the downloaded release assets — cannot verify $archiveName." }

    Write-Host "Verifying checksum…"
    $expectedLine = Select-String -Path $checksumsPath -Pattern ([regex]::Escape("  $archiveName") + '$') | Select-Object -First 1
    if (-not $expectedLine) { Fail "$archiveName has no entry in checksums.txt." }
    $expected = ($expectedLine.Line -split '\s+')[0]
    $actual = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected.ToLowerInvariant() -ne $actual) {
        Fail "checksum mismatch for $archiveName (expected $expected, got $actual) — refusing to install a payload that does not match what was published."
    }
    Write-Host "Checksum verified."

    if (-not $Yes) {
        $reply = Read-Host "This will place h9k and h9kd in ~/.hall9k/bin, add h9k to your PATH, and publish Hall9k's skills to ~/.hall9k/skills. Nothing is started or registered as a background service. Continue? [y/N]"
        if ($reply -notmatch '^(y|yes)$') {
            Fail "Cancelled — nothing was changed. Re-run with -Yes to skip this prompt."
        }
    }

    Write-Host "Unpacking…"
    $payload = Join-Path $workDir "payload"
    Expand-Archive -Path $archivePath -DestinationPath $payload -Force

    Write-Host "Installing…"
    & (Join-Path $payload "h9k.exe") install --from-release $payload --no-restart
    if ($LASTEXITCODE -ne 0) {
        Fail "h9k install --from-release failed (exit $LASTEXITCODE)."
    }

    # Re-resolve h9k for the doctor run: install may have just put it on the PATH for
    # the first time, which this already-running process has not picked up yet.
    $installedH9k = Join-Path $env:USERPROFILE ".hall9k\bin\h9k.exe"
    $h9k = if (Test-Path $installedH9k) { $installedH9k } else { Join-Path $payload "h9k.exe" }

    Write-Host ""
    Write-Host "Running h9k doctor…"
    & $h9k doctor
}
finally {
    Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Done. Open a new terminal so h9k resolves on your PATH."
