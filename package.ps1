#Requires -Version 5.1
<#
    Builds the portable UltraPolling folder.

    The output is self-contained: it carries the .NET runtime, so it runs on any
    Windows x64 machine with nothing installed.

    DRIVER/ is copied beside the exe rather than bundled inside it. SystemManager
    hashes those .sys files to identify which driver is installed, and copies them
    into System32 on a mode change - both need real files on disk.
#>
[CmdletBinding()]
param(
    # No -Configuration parameter on purpose. Configuration is one of the properties
    # that define the package's shape, and those live in Portable.pubxml so the script
    # and the profile cannot drift into producing two different packages from one repo.
    # A parameter here would either be ignored (misleading) or override the profile
    # (defeating the point).
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 evaluates param defaults before $PSScriptRoot is
# populated, so the default must be resolved here in the body.
if (-not $OutputRoot) { $OutputRoot = Join-Path $PSScriptRoot 'dist' }
$proj = Join-Path $PSScriptRoot 'HidusbfModernGui\HidusbfModernGui.csproj'
$dest = Join-Path $OutputRoot 'UltraPolling'

Write-Host '== 1/5  Cleaning ==' -ForegroundColor Cyan
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Path $dest -Force | Out-Null

Write-Host '== 2/5  Publishing (self-contained, single file) ==' -ForegroundColor Cyan
$publishDir = Join-Path $OutputRoot '_publish'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# The Portable profile carries the whole publish shape (RID, self-contained, single
# file, no trimming). Passing those as flags here instead would let the script and
# the profile drift apart and produce two different packages from one repo.
dotnet publish $proj -p:PublishProfile=Portable -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $publishDir 'UltraPolling.exe'
if (-not (Test-Path $exe)) { throw "Expected UltraPolling.exe in $publishDir but it is not there." }

# A framework-dependent build is under 1 MB. If the bundle is small, SelfContained
# did not take, and the folder would fail on any machine without .NET 9 - which is
# the entire reason for building it this way.
$exeMb = (Get-Item $exe).Length / 1MB
if ($exeMb -lt 40) { throw ("UltraPolling.exe is only {0:N1} MB - that is not self-contained. Check Portable.pubxml." -f $exeMb) }
Copy-Item $exe $dest

# Nothing else should be here: single-file means one file. Loose DLLs mean the
# bundle did not form.
$strays = Get-ChildItem $publishDir -File | Where-Object { $_.Name -ne 'UltraPolling.exe' }
if ($strays) { $strays | ForEach-Object { Write-Host "   left behind: $($_.Name)" -ForegroundColor DarkYellow } }

Write-Host '== 3/5  Copying DRIVER (must be byte-for-byte) ==' -ForegroundColor Cyan
$srcDriver = Join-Path $PSScriptRoot 'DRIVER'
Copy-Item $srcDriver (Join-Path $dest 'DRIVER') -Recurse -Force

Write-Host '== 4/5  Verifying every .sys is byte-identical ==' -ForegroundColor Cyan
# This is the check that matters. IdentifyInstalledBuild() SHA-256s these files and
# compares them to System32. One altered byte and the app reports "Unrecognised
# driver" for a build it shipped itself.
$bad = 0
Get-ChildItem $srcDriver -Recurse -Filter *.sys | ForEach-Object {
    $rel = $_.FullName.Substring($srcDriver.Length).TrimStart('\')
    $copy = Join-Path (Join-Path $dest 'DRIVER') $rel
    if (-not (Test-Path $copy)) { Write-Host "   MISSING: $rel" -ForegroundColor Red; $script:bad++; return }
    $a = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    $b = (Get-FileHash $copy       -Algorithm SHA256).Hash
    if ($a -ne $b) { Write-Host "   ALTERED: $rel" -ForegroundColor Red; $script:bad++ }
}
if ($bad -gt 0) { throw "$bad driver file(s) did not survive the copy intact. The package is unusable." }
$sysCount = (Get-ChildItem (Join-Path $dest 'DRIVER') -Recurse -Filter *.sys).Count
Write-Host "   $sysCount .sys files verified identical" -ForegroundColor Green

Write-Host '== 5/5  Copying docs and certificate ==' -ForegroundColor Cyan
foreach ($f in @('SweetLow.CER','README.ENG.TXT','README.2kHz-8kHz.ENG.TXT','README.RUS.TXT')) {
    $p = Join-Path $PSScriptRoot $f
    if (Test-Path $p) { Copy-Item $p $dest } else { Write-Host "   (no $f)" -ForegroundColor DarkGray }
}
Copy-Item (Join-Path $PSScriptRoot 'packaging\LEEME.txt') $dest

Remove-Item $publishDir -Recurse -Force

$size = [math]::Round(((Get-ChildItem $dest -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ''
Write-Host "Package ready: $dest  ($size MB)" -ForegroundColor Green
Write-Host 'Copy that folder anywhere. It needs no .NET install. Run as Administrator.'
