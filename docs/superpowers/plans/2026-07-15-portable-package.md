# UltraPolling Portable Package — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce one folder that runs UltraPolling on any Windows x64 machine with nothing installed, and a repeatable script that rebuilds it.

**Architecture:** A self-contained single-file publish carries the .NET 9 runtime inside the executable. `DRIVER/` sits beside it on disk — it cannot go inside the bundle, because `SystemManager` hashes those `.sys` files to identify the installed driver and copies them to `System32` when the mode changes. A PowerShell script does publish → assemble → verify so packaging is reproducible rather than a manual dance nobody remembers.

**Tech Stack:** .NET 9 (`net9.0-windows`), WPF, `dotnet publish` with `PublishSingleFile` + `SelfContained`, PowerShell 5.1 for the packaging script.

## Global Constraints

- **The `DRIVER/` tree must be copied byte-for-byte.** `SystemManager.IdentifyInstalledBuild()` SHA-256s every `hidusbf.sys` under it and compares against `C:\Windows\System32\drivers\hidusbf.sys`. One altered byte and the app reports `Unrecognised driver` and refuses to identify a build it shipped itself.
- **`DRIVER/` must live beside the .exe, not inside it.** `SystemManager.ResolveDriverDir()` walks up at most 6 directories from `AppDomain.CurrentDomain.BaseDirectory` looking for a folder named `DRIVER` that `FindArchDir` accepts. Files inside a single-file bundle are not on disk and cannot be found, hashed, or copied to `System32`.
- **`IncludeAllContentForSelfExtract` must stay false.** It extracts the whole bundle to a temp directory and repoints `AppContext.BaseDirectory` there — which would send `ResolveDriverDir` hunting for `DRIVER` under `%TEMP%`. `IncludeNativeLibrariesForSelfExtract=true` is fine; it does not move `BaseDirectory`.
- **`PublishTrimmed` must be false.** WPF resolves XAML types by reflection; trimming removes types the linker cannot see referenced and the app fails at runtime, not at build.
- **The manifest must survive.** `app.manifest` declares `requireAdministrator`. Without it the app silently cannot write `LowerFilters`, `bInterval` or the service.
- Target framework `net9.0-windows`, runtime identifier `win-x64`. Do not change either.
- Build from the repo root: `C:\Users\Administrator\Downloads\hidusbf-master`. Branch `redesign/monochrome`.
- Git identity is not configured globally. Commit with:
  `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "..."`
  End every commit message with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

---

### Task 1: Publish properties and the executable's name

The shipped file should be called what the product is called. The window title, the icon and the user's previous build are all `UltraPolling`; only the project folder says `HidusbfModernGui`.

**Files:**
- Modify: `HidusbfModernGui/HidusbfModernGui.csproj`

**Interfaces:**
- Produces: an assembly named `UltraPolling` (so `UltraPolling.exe` / `UltraPolling.dll`), consumed by Task 2's script and Task 3's verification.

- [ ] **Step 1: Set the assembly name**

In `HidusbfModernGui/HidusbfModernGui.csproj`, inside the existing `<PropertyGroup>`, after `<ApplicationIcon>app.ico</ApplicationIcon>`:

```xml
    <!-- The product is UltraPolling; only the project folder is HidusbfModernGui.
         This names the output UltraPolling.exe without renaming the project. -->
    <AssemblyName>UltraPolling</AssemblyName>
    <RootNamespace>HidusbfModernGui</RootNamespace>
```

`RootNamespace` is pinned explicitly because setting `AssemblyName` would otherwise let it follow along, and every `.cs` file declares `namespace HidusbfModernGui`.

**Nothing about the publish shape goes in the .csproj.** `SelfContained` and `RuntimeIdentifier` there would apply to `dotnet build` too — every development build would copy the whole .NET runtime into `bin/`, making the edit-compile loop slow and the output huge, forever. Publish settings belong in a publish profile, which only applies to `dotnet publish`.

- [ ] **Step 2: Create the publish profile**

Create `HidusbfModernGui/Properties/PublishProfiles/Portable.pubxml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- Portable build: carries the .NET runtime, runs on any Windows x64 with nothing
     installed. Applies to `dotnet publish` only, never to `dotnet build`. -->
<Project>
  <PropertyGroup>
    <PublishProtocol>FileSystem</PublishProtocol>
    <Configuration>Release</Configuration>
    <TargetFramework>net9.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>

    <!-- Bundles native libraries. Safe: it does NOT move AppContext.BaseDirectory.
         Do NOT add IncludeAllContentForSelfExtract - that one extracts everything to
         %TEMP% and repoints BaseDirectory there, which would send ResolveDriverDir
         hunting for DRIVER under the temp folder. -->
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>

    <!-- WPF resolves XAML types by reflection. Trimming drops types the linker cannot
         see referenced, and the failure lands at runtime, not at build. -->
    <PublishTrimmed>false</PublishTrimmed>
    <DebugType>none</DebugType>
  </PropertyGroup>
</Project>
```

`DebugType=none` stops a `.pdb` being emitted at all, which is tidier than copying it and then deleting it.

- [ ] **Step 3: Verify the rename did not break the build or the tests**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.` and `bin\Debug\net9.0-windows\UltraPolling.exe` exists — **no `win-x64` subfolder**, because the RID lives in the publish profile and not in the project. If you see one, the publish settings leaked into the .csproj.

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 97`. The test project links `PollingCore.cs` by path, not by assembly reference, so the rename must not touch it. If the count differs, stop and report.

Run: `cd VerifyState && dotnet run`
Expected: `Build (by hash) : NoPatch`, `ModeText (shown UI) : No Patch`, and a non-zero device count. `VerifyState` links `SystemManager.cs` by path too.

**Do not assert a specific device count.** It is environmental — it changes the moment anything is plugged in or out, and it was 7 earlier in this session and 8 now. What matters is that the driver is still identified by hash and that scanning still returns devices.

- [ ] **Step 4: Commit**

```bash
git add HidusbfModernGui/HidusbfModernGui.csproj HidusbfModernGui/Properties/PublishProfiles/Portable.pubxml
git commit -m "build: name the output UltraPolling.exe, add the portable publish profile

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: The folder's own README

Written before the script, because the script copies this file. Building it the other way round leaves a script that throws on its last stage.

**Files:**
- Create: `packaging/LEEME.txt`

**Interfaces:**
- Produces: `packaging/LEEME.txt`, copied into the package by Task 3's script.

- [ ] **Step 1: Write it**

Create `packaging/LEEME.txt`. This ships to the user, so it is in Spanish and says only what is true:

```text
ULTRAPOLLING
============

Utilidad para cambiar la tasa de sondeo (polling rate) de dispositivos USB,
construida sobre el driver hidusbf de SweetLow.

USO
---
1. Ejecuta UltraPolling.exe como Administrador (lo pide solo).
2. Selecciona un dispositivo de la lista.
3. Activa FILTRO.
4. Elige una TASA OBJETIVO.
5. Pulsa RECONECTAR (REPLUG).

RECONECTAR es el que aplica el cambio. Hace cuatro cosas encadenadas: quita el
dispositivo del arbol PnP, espera 2 segundos, lo re-enumera y lo reinicia. Un
reinicio PnP a secas no basta: el dispositivo nunca abandona el bus USB y sus
descriptores no se releen, que es donde hidusbf escribe la tasa.

LA CARPETA DRIVER
-----------------
No la borres ni la muevas. La app la busca al lado del ejecutable, hashea los
.sys que hay dentro para saber que driver tienes instalado, y copia desde ahi
cuando cambias de modo. Sin ella, la app arranca pero no puede identificar ni
instalar nada.

MODO DEL DRIVER (pestana Sistema)
---------------------------------
Es el techo de velocidad de todo el sistema:

  NOPATCH      maximo 1000 Hz
  1kHz         maximo 1000 Hz, levanta el limite en dispositivos Low Speed
  2kHz-4kHz    hasta 4000 Hz
  4kHz-8kHz    hasta 8000 Hz

Los modos con patch parchean el codigo de Windows en memoria (usbxhci.sys) para
levantar el limite de sondeo. Requieren Memory Integrity DESACTIVADO en
Seguridad de Windows > Aislamiento del nucleo.

Cambiar de modo reemplaza el archivo del driver, y Windows bloquea el archivo de
un driver cargado. Si falla, quita el FILTRO de todos los dispositivos y
reinicialos, o reinicia el PC.

LO QUE ESTA APP NO HACE
-----------------------
No mide la tasa real. Te dice la tasa que PIDIO, nunca la que ocurre. Para
comprobar que un overclock funciono de verdad hace falta una herramienta de
medida aparte (por ejemplo Mouse Rate Checker).

Una tasa alta tampoco crea datos: si el firmware del dispositivo genera reportes
a 1000 Hz, sondearlo a 8000 Hz devuelve el mismo dato repetido.

CREDITOS
--------
Driver hidusbf: SweetLow  -  https://github.com/LordOfMice/hidusbf
Firma de los drivers: Battle Beaver Customs
```

- [ ] **Step 2: Commit**

```bash
git add packaging/LEEME.txt
git commit -m "docs: add the README that ships inside the portable folder

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: The packaging script

**Files:**
- Create: `package.ps1`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: the `UltraPolling` assembly name and the `Portable.pubxml` profile from Task 1; `packaging/LEEME.txt` from Task 2.
- Produces: `dist/UltraPolling/` containing `UltraPolling.exe`, `DRIVER/`, `SweetLow.CER`, `LEEME.txt`, and the upstream READMEs.

- [ ] **Step 1: Write the script**

Create `package.ps1` at the repo root:

```powershell
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
    [string]$Configuration = 'Release',
    [string]$OutputRoot    = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
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
```

- [ ] **Step 2: Ignore the output**

Append to `.gitignore`:

```gitignore

# Packaging output. Rebuilt by package.ps1; a 150 MB self-contained bundle has no
# business in git - the same reason UltraPolling.exe is ignored at the root.
/dist/
```

- [ ] **Step 3: Verify the script parses before running it**

`ParseFile` takes two out-parameters; both need real variables, and it needs a full path string:

```powershell
$tokens = $null; $errs = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path .\package.ps1).Path, [ref]$tokens, [ref]$errs)
if ($errs) { $errs } else { 'parses clean' }
```
Expected: `parses clean`. Any parse error listed must be fixed before Task 4.

- [ ] **Step 4: Commit**

```bash
git add package.ps1 .gitignore
git commit -m "build: add package.ps1 to produce the portable folder

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Build the package and prove it works from the folder

A green publish proves nothing. The one thing that can silently break here is `BaseDirectory` inside a single-file bundle: if it points at an extraction directory instead of the folder holding the exe, `ResolveDriverDir` hunts for `DRIVER` under `%TEMP%`, finds nothing, and the app runs while reporting it cannot identify any driver.

**Files:** none

- [ ] **Step 1: Run the packaging script**

Run: `powershell -ExecutionPolicy Bypass -File .\package.ps1`
Expected: the five stages complete, `21 .sys files verified identical`, and a final `Package ready: ...\dist\UltraPolling (NNN MB)`.

- [ ] **Step 2: Confirm the folder's shape**

Run:
```powershell
Get-ChildItem .\dist\UltraPolling | Select-Object Name, Length
Test-Path .\dist\UltraPolling\DRIVER\AMD64_AS\NoPatch\hidusbf.sys
```
Expected: `UltraPolling.exe`, `DRIVER`, `SweetLow.CER`, `LEEME.txt` and the READMEs. `True` for the .sys path. **No `.pdb`, no loose `.dll`** — if any appear, the publish was not single-file.

- [ ] **Step 3: Prove the driver files survived, independently of the script**

Run:
```powershell
$a = Get-FileHash .\DRIVER\AMD64_AS\NoPatch\hidusbf.sys -Algorithm SHA256
$b = Get-FileHash .\dist\UltraPolling\DRIVER\AMD64_AS\NoPatch\hidusbf.sys -Algorithm SHA256
"source: $($a.Hash)"; "dist  : $($b.Hash)"; "match : $($a.Hash -eq $b.Hash)"
```
Expected: `match : True`. The script checks this itself, but the script is the thing under test.

- [ ] **Step 4: Prove BaseDirectory resolves DRIVER from the packaged layout**

This is the step this task exists for. Run the packaged exe and read what it reports:

```powershell
Start-Process .\dist\UltraPolling\UltraPolling.exe
Start-Sleep -Seconds 12
```

Take a screenshot, or read the status bar. Expected: the device list populates, and the System tab shows `ACTIVO` = **No Patch** with an amber dot.

**The failure signature to watch for:** if the status bar reads `DRIVER folder not found (looked under ...)`, or the System tab shows `Not Installed` / `Unrecognised driver`, then `BaseDirectory` is pointing at an extraction directory and `ResolveDriverDir` cannot see `DRIVER`. If that happens, do not paper over it by hardcoding a path — report it. The fix would be to stop `IncludeAllContentForSelfExtract` from being set, or to drop `PublishSingleFile`.

Close the app afterwards: `Get-Process UltraPolling | Stop-Process -Force`

- [ ] **Step 5: Record what could not be verified here**

The build machine has .NET 9 Desktop 9.0.16 installed, so **running the package here does not prove it is self-contained** — it would run either way. The script's size gate (>40 MB) is strong circumstantial evidence, and it is the best this machine can offer.

The claim "runs on any Windows x64 with nothing installed" is therefore **untested**. It can only be settled on a machine without the .NET 9 Desktop runtime. Say so in the report rather than implying the package was proven portable.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: verify the portable package resolves DRIVER from its own folder

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Notes for the implementer

**Why `DRIVER/` cannot be embedded.** It is tempting to treat it as content and bundle it. Two things break: `IdentifyInstalledBuild()` opens each `.sys` with `File.OpenRead` to SHA-256 it, and `ChangeDriverMode` `File.Copy`s one into `C:\Windows\System32\drivers`. Both need a real path. Bundled content is not on the filesystem.

**Why the hash check is not ceremony.** The whole reason this app can be trusted about the driver is that it identifies the installed build by content rather than believing the registry. That property dies the moment a packaging step alters a byte — and it would die silently, reporting `Unrecognised driver` for a file the package itself shipped.

**Do not add a `.pdb` to the folder.** It is debug symbols, forty times the size of the compiled code, and useless to a user.
