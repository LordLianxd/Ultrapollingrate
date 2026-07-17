# UltraPolling Monochrome Redesign — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace UltraPolling's generic dashboard skin with a true-black monochrome instrument UI where colour only ever encodes real system state.

**Architecture:** The backend stays. `SystemManager`'s logic is untouched; `PollingCore` gains one pure function (`DeviceStatusLevel`) so colour decisions are unit-testable rather than eyeballed. The view models move out of `SystemManager.cs` into their own files. The palette moves into a static `Theme.xaml` (no light variant), and `MainWindow.xaml` is rebuilt section by section as a master-detail layout.

**Tech Stack:** .NET 9 (`net9.0-windows`), WPF, xunit 2.9.2, `Microsoft.Win32.Registry`, `System.ServiceProcess.ServiceController`.

**Spec:** `docs/superpowers/specs/2026-07-15-monochrome-redesign-design.md`

## Global Constraints

- **Colour never decorates.** Before giving any element a colour, you must be able to name the system fact it encodes. Otherwise it is grey.
- **`SystemManager`'s logic must not change.** No edits to registry access, service control, device scanning, or hash-based driver identification. Moving type declarations to other files is allowed; changing what they do is not.
- **The 71 existing `PollingCore` tests must stay green without being modified.**
- Target framework: `net9.0-windows`. Do not change it.
- Palette (exact hex, no substitutions): `Bg #000000`, `Surface #0A0A0A`, `SurfaceAlt #111111`, `Border #1F1F1F`, `TextData #FFFFFF`, `TextLabel #8A8A8A`, `TextMuted #4A4A4A`, `StatusOk #00C853`, `StatusWarn #FFAB00`, `StatusError #FF3D00`.
- No indigo, no blue. `#6366F1`, `#818CF8`, `#0F172A`, `#1E293B`, `#312E81`, `#EEF2FF` must not appear anywhere when the plan is done.
- Data fonts: `FontFamily="Cascadia Mono, Consolas, Courier New"`. The fallback is mandatory — Cascadia Mono is not guaranteed to be installed; Consolas is.
- UI fonts: `FontFamily="Segoe UI Variable Display, Segoe UI"`.
- There is no light theme. Do not add one, and do not add settings persistence.
- Build with `dotnet build` from `HidusbfModernGui/`. Test with `dotnet test` from `HidusbfModernGui.Tests/`.

---

### Task 1: Create a git safety net

The repo has no version control. This plan rewrites ~75 KB of XAML; without history there is no way back. Do this first.

**Files:**
- Create: `.gitignore`

- [ ] **Step 1: Confirm there is no repo yet**

Run: `git rev-parse --is-inside-work-tree`
Expected: `fatal: not a git repository (or any of the parent directories): .git`

If it prints `true`, skip to Step 5.

- [ ] **Step 2: Create `.gitignore`**

```gitignore
bin/
obj/
*.user
```

Note: `DRIVER/**/*.sys` are deliberately NOT ignored. They are the shipped payload and `SystemManager.IdentifyInstalledBuild()` hashes them to identify the installed driver.

- [ ] **Step 3: Initialise and stage**

```bash
git init
git add .
```

- [ ] **Step 4: Commit the current working state as the rollback point**

```bash
git commit -m "chore: baseline before monochrome redesign

Backend already rewritten and verified: hash-based driver identification,
explicit patch parameters, no silent rate fallbacks. 71 PollingCore tests green."
```

- [ ] **Step 5: Branch off, leaving the baseline untouched**

All redesign work happens on a branch so the default branch stays as a clean
rollback point.

```bash
git checkout -b redesign/monochrome
```

- [ ] **Step 6: Verify the rollback point exists**

Run: `git log --oneline -1 && git status --short && git branch --show-current`
Expected: one commit listed, a clean working tree, and `redesign/monochrome`.

---

### Task 2: Add `DeviceStatusLevel` to `PollingCore`

The colour of a device's status dot must be decided by a tested pure function, not by XAML triggers. This is what keeps the "colour never decorates" rule verifiable.

**Files:**
- Modify: `HidusbfModernGui/PollingCore.cs`
- Test: `HidusbfModernGui.Tests/PollingCoreTests.cs`

**Interfaces:**
- Consumes: `UsbSpeed` (already in `PollingCore.cs`)
- Produces: `enum StatusLevel { Idle, Ok, Warn, Error }` and
  `static StatusLevel PollingCore.DeviceStatusLevel(bool filterActive, UsbSpeed speed, int? resolvedRate)`
  — used by Task 3's `UsbDeviceModel.Status` and Task 6's list binding.

- [ ] **Step 1: Write the failing tests**

Append to `HidusbfModernGui.Tests/PollingCoreTests.cs`, inside `namespace HidusbfModernGui.Tests`:

```csharp
    public class DeviceStatusLevelTests
    {
        // Unknown speed wins over everything: we cannot compute an interval
        // safely, so the rate controls get blocked and the dot goes red.
        [Theory]
        [InlineData(true, 1000)]
        [InlineData(false, null)]
        [InlineData(true, null)]
        public void UnknownSpeed_IsAlwaysError(bool filterActive, int? rate)
        {
            Assert.Equal(StatusLevel.Error,
                PollingCore.DeviceStatusLevel(filterActive, UsbSpeed.Unknown, rate));
        }

        // No filter means the device exists but we do not manage it. Grey.
        [Theory]
        [InlineData(UsbSpeed.High)]
        [InlineData(UsbSpeed.Full)]
        [InlineData(UsbSpeed.Low)]
        public void NoFilter_IsIdle(UsbSpeed speed)
        {
            Assert.Equal(StatusLevel.Idle, PollingCore.DeviceStatusLevel(false, speed, null));
        }

        // Filter attached but no rate pinned: the filter is doing nothing.
        [Fact]
        public void FilterWithoutRate_IsWarn()
        {
            Assert.Equal(StatusLevel.Warn, PollingCore.DeviceStatusLevel(true, UsbSpeed.High, null));
        }

        [Theory]
        [InlineData(8000)]
        [InlineData(1000)]
        [InlineData(125)]
        public void FilterWithRate_IsOk(int rate)
        {
            Assert.Equal(StatusLevel.Ok, PollingCore.DeviceStatusLevel(true, UsbSpeed.High, rate));
        }

        // Downclocking is a documented, working use case in SweetLow's README.
        // A mouse deliberately pinned to 31Hz did exactly what was asked, so it
        // must not be flagged. "The driver is capped" is a fact about the driver
        // and belongs in the header, not on the device row.
        [Theory]
        [InlineData(31)]
        [InlineData(62)]
        public void DeliberateDownclocking_IsOk_NotWarn(int rate)
        {
            Assert.Equal(StatusLevel.Ok, PollingCore.DeviceStatusLevel(true, UsbSpeed.Full, rate));
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: FAIL — `The name 'StatusLevel' does not exist in the current context`.

- [ ] **Step 3: Implement**

In `HidusbfModernGui/PollingCore.cs`, add after the `DriverMode` enum:

```csharp
    // What a device's status dot says. Resolved by PollingCore.DeviceStatusLevel
    // so the colour rule is covered by tests instead of eyeballed in the designer.
    public enum StatusLevel
    {
        Idle,   // grey  - present but not managed by us
        Ok,     // green - doing what was asked
        Warn,   // amber - attention needed
        Error   // red   - cannot act safely
    }
```

And add to the `PollingCore` class:

```csharp
        // Whether THIS DEVICE is doing what was asked. Deliberately says nothing
        // about whether the rate is "high": downclocking a mouse to 31Hz is a
        // documented, working use case, not a warning. Whether the driver is
        // capped is a fact about the driver and is reported in the header.
        public static StatusLevel DeviceStatusLevel(bool filterActive, UsbSpeed speed, int? resolvedRate)
        {
            if (speed == UsbSpeed.Unknown) return StatusLevel.Error;
            if (!filterActive) return StatusLevel.Idle;
            if (resolvedRate == null) return StatusLevel.Warn;
            return StatusLevel.Ok;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: PASS — `Failed: 0, Passed: 83` (71 existing + 12 new).

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/PollingCore.cs HidusbfModernGui.Tests/PollingCoreTests.cs
git commit -m "feat: add tested DeviceStatusLevel so status colour is not eyeballed"
```

---

### Task 3: Extract view models out of `SystemManager.cs`

`SystemManager.cs` is ~700 lines holding `OpResult`, `DriverBuild`, `DriverState`, `UsbDeviceModel`, `PnpDeviceRaw` and the manager itself. The redesign needs to add display properties to the models, and doing that inside the manager's file would mean editing a file the Global Constraints say must not change behaviour. Splitting by responsibility resolves this and makes each file small enough to reason about.

**Files:**
- Create: `HidusbfModernGui/OpResult.cs`
- Create: `HidusbfModernGui/DriverState.cs`
- Create: `HidusbfModernGui/UsbDeviceModel.cs`
- Modify: `HidusbfModernGui/SystemManager.cs` (delete the moved declarations only)

**Interfaces:**
- Produces: the same public types at the same namespace (`HidusbfModernGui`), so no call site changes. Task 4 adds display properties to `UsbDeviceModel` and `DriverState`.

- [ ] **Step 1: Move `OpResult` to its own file**

Create `HidusbfModernGui/OpResult.cs`, cutting the type verbatim from `SystemManager.cs`:

```csharp
namespace HidusbfModernGui
{
    // Result of an operation that touches the system. Carries the reason on failure
    // so the UI can tell the user what actually went wrong instead of a generic box.
    public readonly record struct OpResult(bool Success, string? Error)
    {
        public static OpResult Ok() => new(true, null);
        public static OpResult Fail(string error) => new(false, error);
    }
}
```

Delete that declaration from `SystemManager.cs`.

- [ ] **Step 2: Move `DriverBuild` and `DriverState` to their own file**

Create `HidusbfModernGui/DriverState.cs`, cutting both types verbatim from `SystemManager.cs` (the `DriverBuild` enum and the `DriverState` class, including `ModeText`, `Warning` and `CanOverclockBeyond1k`). Add `using System;` at the top.

Delete both declarations from `SystemManager.cs`.

- [ ] **Step 3: Move `UsbDeviceModel` and `PnpDeviceRaw` to their own file**

Create `HidusbfModernGui/UsbDeviceModel.cs`, cutting both types verbatim from `SystemManager.cs`. Add `using System;` at the top.

Delete both declarations from `SystemManager.cs`.

- [ ] **Step 4: Verify nothing changed behaviourally**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.`

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: PASS — `Failed: 0, Passed: 83`.

Run: `cd VerifyState && dotnet run`
Expected: identical output to before the move — `Build (by hash) : NoPatch`, `ModeText (shown UI) : No Patch`, 7 devices.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/
git commit -m "refactor: split view models out of SystemManager into focused files"
```

---

### Task 4: Add display properties to the view models

**Files:**
- Modify: `HidusbfModernGui/UsbDeviceModel.cs`
- Modify: `HidusbfModernGui/DriverState.cs`

**Interfaces:**
- Consumes: `PollingCore.DeviceStatusLevel` (Task 2), `StatusLevel` (Task 2)
- Produces: `UsbDeviceModel.StatusDot` (`StatusLevel`), `UsbDeviceModel.SpeedText` (`string`), `UsbDeviceModel.BIntervalText` (`string`), `UsbDeviceModel.FilterText` (`string`), `DriverState.HeaderStatus` (`StatusLevel`), `DriverState.ServiceStatusLevel` (`StatusLevel`) — all consumed by Tasks 5–7's bindings.

**Named `StatusDot`, not `Status`.** `UsbDeviceModel` already has a `public string Status` holding the raw PnP status text (`"OK"`, `"Error"`, …), which `IsConnected` reads and which `SystemManager.ScanDevices` populates. That property is frozen, so the new `StatusLevel` one takes a different name. Bind device-row dots to `StatusDot`.

- [ ] **Step 1: Add the device display properties**

In `HidusbfModernGui/UsbDeviceModel.cs`, add to `UsbDeviceModel` after `IntervalModeText`:

```csharp
        // Colour of this row's status dot. Delegated to PollingCore so the rule
        // lives under test.
        public StatusLevel Status => PollingCore.DeviceStatusLevel(FilterActive, BusSpeed, ResolvedRate);

        public string SpeedText => BusSpeed switch
        {
            UsbSpeed.Super => "SuperSpeed",
            UsbSpeed.High => "High Speed",
            UsbSpeed.Full => "Full Speed",
            UsbSpeed.Low => "Low Speed",
            _ => "Unknown"
        };

        // The raw register value, shown because this is an instrument: the user
        // should be able to check our arithmetic against the USB spec.
        public string BIntervalText
        {
            get
            {
                if (SelectedRate is null or 0) return "--";
                var b = PollingCore.TryMapRateToBInterval(SelectedRate.Value, BusSpeed);
                return b?.ToString() ?? "--";
            }
        }

        public string FilterText => FilterActive ? "ON" : "OFF";
```

- [ ] **Step 2: Add the driver header properties**

In `HidusbfModernGui/DriverState.cs`, add to `DriverState`:

```csharp
        // The one place that reports the driver being capped. Device rows must not
        // repeat it: whether a device does what was asked and whether the driver
        // can exceed 1000Hz are different facts.
        public StatusLevel HeaderStatus
        {
            get
            {
                if (Build is DriverBuild.Missing or DriverBuild.Unrecognised) return StatusLevel.Error;
                if (!EffectiveMode.HasValue) return StatusLevel.Error;
                if (MemoryIntegrityEnabled && Build == DriverBuild.Patching) return StatusLevel.Error;
                return CanOverclockBeyond1k ? StatusLevel.Ok : StatusLevel.Warn;
            }
        }

        public StatusLevel ServiceStatusLevel =>
            ServiceStatus.Equals("Running", StringComparison.OrdinalIgnoreCase)
                ? StatusLevel.Ok
                : StatusLevel.Error;
```

- [ ] **Step 3: Verify**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add HidusbfModernGui/UsbDeviceModel.cs HidusbfModernGui/DriverState.cs
git commit -m "feat: add display properties for the monochrome instrument UI"
```

---

### Task 5: Create `Theme.xaml`

The palette is now static (no light variant), so it belongs in its own dictionary rather than inline in a 75 KB window. This is the file that carries the identity.

**Files:**
- Create: `HidusbfModernGui/Theme.xaml`
- Create: `HidusbfModernGui/StatusLevelToBrushConverter.cs`
- Modify: `HidusbfModernGui/App.xaml`

**Interfaces:**
- Produces: brush keys `BgBrush`, `SurfaceBrush`, `SurfaceAltBrush`, `BorderBrush`, `TextDataBrush`, `TextLabelBrush`, `TextMutedBrush`, `StatusOkBrush`, `StatusWarnBrush`, `StatusErrorBrush`; style keys `DataText`, `RateDisplay`, `FieldLabel`, `SectionHeading`, `StatusDot`; converter key `StatusToBrush`. All consumed by Tasks 6–8.

- [ ] **Step 1: Write the converter that turns a `StatusLevel` into a brush**

Create `HidusbfModernGui/StatusLevelToBrushConverter.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HidusbfModernGui
{
    // Maps a tested StatusLevel to a brush. The XAML never picks a colour itself;
    // it asks this, which asks PollingCore.
    //
    // BrushFor is the single place this mapping exists. Code-behind calls it
    // directly; XAML goes through Convert. Do not re-implement the switch anywhere
    // else — two copies of a colour rule is how a colour rule starts lying.
    public class StatusLevelToBrushConverter : IValueConverter
    {
        public static Brush BrushFor(StatusLevel level)
        {
            string key = level switch
            {
                StatusLevel.Ok => "StatusOkBrush",
                StatusLevel.Warn => "StatusWarnBrush",
                StatusLevel.Error => "StatusErrorBrush",
                _ => "TextMutedBrush"
            };
            return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => BrushFor(value is StatusLevel level ? level : StatusLevel.Idle);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Write `Theme.xaml`**

Create `HidusbfModernGui/Theme.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:HidusbfModernGui">

    <!-- Palette. Colour only ever encodes a real system fact; everything else is
         black, grey or white. No indigo, no blue. -->
    <SolidColorBrush x:Key="BgBrush"          Color="#000000"/>
    <SolidColorBrush x:Key="SurfaceBrush"     Color="#0A0A0A"/>
    <SolidColorBrush x:Key="SurfaceAltBrush"  Color="#111111"/>
    <SolidColorBrush x:Key="BorderBrush"      Color="#1F1F1F"/>
    <SolidColorBrush x:Key="TextDataBrush"    Color="#FFFFFF"/>
    <SolidColorBrush x:Key="TextLabelBrush"   Color="#8A8A8A"/>
    <SolidColorBrush x:Key="TextMutedBrush"   Color="#4A4A4A"/>

    <SolidColorBrush x:Key="StatusOkBrush"    Color="#00C853"/>
    <SolidColorBrush x:Key="StatusWarnBrush"  Color="#FFAB00"/>
    <SolidColorBrush x:Key="StatusErrorBrush" Color="#FF3D00"/>

    <local:StatusLevelToBrushConverter x:Key="StatusToBrush"/>

    <!-- Cascadia Mono is not guaranteed to be installed; Consolas is. The
         fallback chain is not optional. -->
    <FontFamily x:Key="MonoFont">Cascadia Mono, Consolas, Courier New</FontFamily>
    <FontFamily x:Key="UiFont">Segoe UI Variable Display, Segoe UI</FontFamily>

    <!-- Numbers are the product: tabular mono so 1000 Hz -> 500 Hz does not shift
         the layout. -->
    <Style x:Key="DataText" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource MonoFont}"/>
        <Setter Property="Foreground" Value="{StaticResource TextDataBrush}"/>
        <Setter Property="FontSize" Value="13"/>
    </Style>

    <Style x:Key="RateDisplay" TargetType="TextBlock" BasedOn="{StaticResource DataText}">
        <Setter Property="FontSize" Value="46"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>

    <Style x:Key="FieldLabel" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource UiFont}"/>
        <Setter Property="Foreground" Value="{StaticResource TextLabelBrush}"/>
        <Setter Property="FontSize" Value="11"/>
    </Style>

    <Style x:Key="SectionHeading" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource UiFont}"/>
        <Setter Property="Foreground" Value="{StaticResource TextLabelBrush}"/>
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>

    <Style x:Key="StatusDot" TargetType="Ellipse">
        <Setter Property="Width" Value="6"/>
        <Setter Property="Height" Value="6"/>
        <Setter Property="VerticalAlignment" Value="Center"/>
    </Style>

    <!-- Flat, square, bordered. No rounded pill buttons. -->
    <Style x:Key="InstrumentButton" TargetType="Button">
        <Setter Property="FontFamily" Value="{StaticResource UiFont}"/>
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="Foreground" Value="{StaticResource TextDataBrush}"/>
        <Setter Property="Background" Value="{StaticResource SurfaceAltBrush}"/>
        <Setter Property="Padding" Value="14,8"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{StaticResource BorderBrush}"
                            BorderThickness="1"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <!-- Reuses a palette token rather than inventing a hover
                                 shade: every hex in this file is one of the ten. -->
                            <Setter TargetName="Bd" Property="Background" Value="{StaticResource BorderBrush}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Foreground" Value="{StaticResource TextMutedBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

</ResourceDictionary>
```

- [ ] **Step 3: Merge it in `App.xaml`**

Replace the contents of `HidusbfModernGui/App.xaml` with:

```xml
<Application x:Class="HidusbfModernGui.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:HidusbfModernGui"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Theme.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 4: Verify it builds**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.` The window still looks old — nothing consumes the theme yet.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/Theme.xaml HidusbfModernGui/StatusLevelToBrushConverter.cs HidusbfModernGui/App.xaml
git commit -m "feat: add monochrome instrument theme"
```

---

### Task 6: Rebuild the window shell — chrome, sidebar, header

From here `MainWindow.xaml` is rebuilt. Work top-down so the file compiles at each task boundary.

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml` (replace `<Window.Resources>` and the shell)
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `Theme.xaml` keys (Task 5), `DriverState.ModeText` / `HeaderStatus` / `ServiceStatus` / `ServiceStatusLevel` (Task 4)
- Produces: named elements `DriverModeText`, `DriverModeDot`, `ServiceStatusText`, `ServiceStatusDot`, `DevicesListBox`, `MainTabControl`, `StatusLogText` — consumed by Tasks 7–9.

- [ ] **Step 1: Strip the old resources and theme machinery**

In `MainWindow.xaml`, delete every `<SolidColorBrush>` from `<Window.Resources>` (the 9 light-theme brushes). Keep the geometry `<Geometry>` resources for icons, but delete `SunIconPath` and `MoonIconPath`.

Set the window root:

```xml
        Title="UltraPolling" 
        Height="740" Width="1080"
        WindowStyle="None" 
        AllowsTransparency="True" 
        Background="Transparent"
        FontFamily="{StaticResource UiFont}"
        Loaded="Window_Loaded"
```

- [ ] **Step 2: Delete the theme toggle from the code-behind**

In `MainWindow.xaml.cs`, delete the `_isDarkMode` field and the entire `ThemeToggleButton_Click` method. Delete the `ThemeToggleButton` and `ThemeToggleIcon` elements from `MainWindow.xaml`.

Rationale: there is one theme now. See the spec's Decisions section.

- [ ] **Step 3: Replace the header status card**

Delete the `CardDarkBrush` border containing `ServiceStatusDot` / `ServiceStatusText` / `ServiceProgressRing` / `ServiceProgressText`, and the whole bottom "Active Overclock" coverage card containing `OverclockStatusDot` / `OverclockStatusText` / `OverclockProgressRing` / `OverclockProgressText`.

Replace the header with:

```xml
<Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}" BorderThickness="0,0,0,1" Height="52">
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center" Margin="20,0">

        <!-- Named Header*, not DriverModeText: the System view (Task 9) declares
             a DriverModeText of its own, and duplicate x:Name does not compile. -->
        <TextBlock Text="DRIVER" Style="{StaticResource SectionHeading}" VerticalAlignment="Center"/>
        <Ellipse x:Name="HeaderModeDot" Style="{StaticResource StatusDot}" Margin="10,0,6,0"/>
        <TextBlock x:Name="HeaderModeText" Text="--" Style="{StaticResource DataText}" VerticalAlignment="Center"/>

        <Rectangle Width="1" Height="18" Fill="{StaticResource BorderBrush}" Margin="20,0"/>

        <TextBlock Text="SERVICIO" Style="{StaticResource SectionHeading}" VerticalAlignment="Center"/>
        <Ellipse x:Name="ServiceStatusDot" Style="{StaticResource StatusDot}" Margin="10,0,6,0"/>
        <TextBlock x:Name="ServiceStatusText" Text="--" Style="{StaticResource DataText}" VerticalAlignment="Center"/>

    </StackPanel>
</Border>
```

- [ ] **Step 4: Rewrite `RefreshStatus` and delete the gauge code**

In `MainWindow.xaml.cs`, delete `SetOverclockGauge` and `ApplyGauge` entirely (they drove the rings, which are gone — expressing "which of 4 modes" as a percentage was invented precision).

Replace `RefreshStatus` with:

```csharp
        // Reads the real system state: the hash of the installed .sys plus the
        // registry, never what this app wrote earlier.
        private void RefreshStatus()
        {
            _driverState = SystemManager.GetDriverState();

            // Two places show the mode: the header (always visible) and the System
            // view. Both read the same resolved state.
            HeaderModeText.Text = _driverState.ModeText;
            HeaderModeDot.Fill = StatusBrush(_driverState.HeaderStatus);
            DriverModeText.Text = _driverState.ModeText;

            ServiceStatusText.Text = _driverState.ServiceStatus;
            ServiceStatusDot.Fill = StatusBrush(_driverState.ServiceStatusLevel);

            int selectedIndex = _driverState.EffectiveMode switch
            {
                DriverMode.NoPatch => 0,
                DriverMode.Rate1k => 1,
                DriverMode.Rate2k4k => 2,
                DriverMode.Rate4k8k => 3,
                _ => -1
            };

            GlobalModeComboBox.SelectionChanged -= GlobalModeComboBox_SelectionChanged;
            GlobalModeComboBox.SelectedIndex = selectedIndex;
            GlobalModeComboBox.SelectionChanged += GlobalModeComboBox_SelectionChanged;

            if (!string.IsNullOrEmpty(_driverState.Warning)) LogStatus(_driverState.Warning!);
        }

        // Delegates to the converter's static mapping. Do NOT re-implement the
        // StatusLevel switch here: one colour rule, one place.
        private static Brush StatusBrush(StatusLevel level)
            => StatusLevelToBrushConverter.BrushFor(level);
```

- [ ] **Step 5: Restyle the sidebar**

The sidebar keeps its three destinations and its window buttons; only the skin changes. Replace its `Border` and nav buttons with:

```xml
<Border Grid.Column="0" Background="{StaticResource SurfaceBrush}"
        BorderBrush="{StaticResource BorderBrush}" BorderThickness="0,0,1,0" Width="64">
    <Grid>
        <StackPanel VerticalAlignment="Center">
            <Button Style="{StaticResource SidebarButton}" Click="DashboardNavBtn_Click" ToolTip="Dispositivos">
                <Path Data="{StaticResource HomeIconPath}" Fill="{StaticResource TextDataBrush}" Stretch="Uniform" Width="18" Height="18"/>
            </Button>
            <Button Style="{StaticResource SidebarButton}" Click="SettingsNavBtn_Click" ToolTip="Sistema">
                <Path Data="{StaticResource SettingsIconPath}" Fill="{StaticResource TextLabelBrush}" Stretch="Uniform" Width="18" Height="18"/>
            </Button>
            <Button Style="{StaticResource SidebarButton}" Click="RefreshDevicesBtn_Click" ToolTip="Refrescar">
                <Path Data="{StaticResource RefreshIconPath}" Fill="{StaticResource TextLabelBrush}" Stretch="Uniform" Width="18" Height="18"/>
            </Button>
        </StackPanel>

        <StackPanel VerticalAlignment="Bottom" Margin="0,0,0,16">
            <Button Style="{StaticResource SidebarButton}" Click="MinimizeButton_Click" ToolTip="Minimizar">
                <Path Data="{StaticResource MinIconPath}" Fill="{StaticResource TextLabelBrush}" Stretch="Uniform" Width="12" Height="12"/>
            </Button>
            <Button Style="{StaticResource SidebarButton}" Click="CloseButton_Click" ToolTip="Cerrar">
                <Path Data="{StaticResource CloseIconPath}" Fill="{StaticResource TextLabelBrush}" Stretch="Uniform" Width="12" Height="12"/>
            </Button>
        </StackPanel>
    </Grid>
</Border>
```

All five geometry keys used here (`HomeIconPath`, `SettingsIconPath`, `RefreshIconPath`, `MinIconPath`, `CloseIconPath`) are already declared in `MainWindow.xaml` — verified. Do not rename them.

Add the sidebar button style to `Theme.xaml`:

```xml
    <Style x:Key="SidebarButton" TargetType="Button">
        <Setter Property="Width" Value="44"/>
        <Setter Property="Height" Value="44"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="HorizontalAlignment" Value="Center"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" Background="{TemplateBinding Background}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{StaticResource SurfaceAltBrush}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

The old red close button is gone: closing a window is not an error state, and red is reserved for the driver actually being down.

- [ ] **Step 6: Restyle the status bar**

The log line stays — it is the only place `DriverState.Warning` surfaces. Replace its border with:

```xml
<Border Grid.Row="2" Background="{StaticResource SurfaceBrush}"
        BorderBrush="{StaticResource BorderBrush}" BorderThickness="0,1,0,0" Height="30">
    <Grid Margin="16,0">
        <TextBlock x:Name="StatusLogText" Text="" Style="{StaticResource DataText}"
                   FontSize="11" Foreground="{StaticResource TextLabelBrush}"
                   VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
        <TextBlock Text="ADMIN" Style="{StaticResource SectionHeading}"
                   HorizontalAlignment="Right" VerticalAlignment="Center"/>
    </Grid>
</Border>
```

- [ ] **Step 7: Verify**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.`

- [ ] **Step 8: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs HidusbfModernGui/Theme.xaml
git commit -m "feat: black shell with text-based driver status, drop donut gauges"
```

---

### Task 7: Rebuild the master list

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`

**Interfaces:**
- Consumes: `UsbDeviceModel.Name` / `ChildrenSummary` / `DisplayRate` / `Status` (Task 4), `StatusToBrush` (Task 5)

- [ ] **Step 1: Replace the device list item template**

Replace the whole `DevicesListBox` `ItemTemplate` with:

```xml
<ListBox x:Name="DevicesListBox"
         Background="Transparent" BorderThickness="0"
         ScrollViewer.HorizontalScrollBarVisibility="Disabled"
         HorizontalContentAlignment="Stretch">
    <ListBox.ItemContainerStyle>
        <Style TargetType="ListBoxItem">
            <Setter Property="Padding" Value="0"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                        <Border x:Name="Row" Background="Transparent"
                                BorderBrush="{StaticResource BorderBrush}" BorderThickness="0,0,0,1">
                            <ContentPresenter/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Row" Property="Background" Value="{StaticResource SurfaceAltBrush}"/>
                            </Trigger>
                            <Trigger Property="IsSelected" Value="True">
                                <Setter TargetName="Row" Property="Background" Value="{StaticResource SurfaceAltBrush}"/>
                                <Setter TargetName="Row" Property="BorderBrush" Value="{StaticResource TextDataBrush}"/>
                                <Setter TargetName="Row" Property="BorderThickness" Value="2,0,0,1"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </ListBox.ItemContainerStyle>

    <ListBox.ItemTemplate>
        <DataTemplate>
            <Grid Margin="16,12">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <!-- The icon is grey, always. Device type is not a status, so it
                     does not get a colour. IconKind already resolves to one of
                     Mouse / Keyboard / Gamepad / Usb. -->
                <Path Grid.Column="0" Fill="{StaticResource TextLabelBrush}" Stretch="Uniform"
                      Width="16" Height="16" VerticalAlignment="Center" Margin="0,0,14,0">
                    <Path.Style>
                        <Style TargetType="Path">
                            <Setter Property="Data" Value="{StaticResource UsbIconPath}"/>
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding IconKind}" Value="Mouse">
                                    <Setter Property="Data" Value="{StaticResource MouseIconPath}"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding IconKind}" Value="Keyboard">
                                    <Setter Property="Data" Value="{StaticResource KeyboardIconPath}"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding IconKind}" Value="Gamepad">
                                    <Setter Property="Data" Value="{StaticResource GamepadIconPath}"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Path.Style>
                </Path>

                <StackPanel Grid.Column="1">
                    <TextBlock Text="{Binding Name}" Foreground="{StaticResource TextDataBrush}" FontSize="13"/>
                    <TextBlock Text="{Binding ChildrenSummary}" Style="{StaticResource FieldLabel}"
                               TextTrimming="CharacterEllipsis" Margin="0,3,0,0"
                               ToolTip="{Binding ChildrenSummary}"/>
                </StackPanel>

                <TextBlock Grid.Column="2" Text="{Binding DisplayRate}" Style="{StaticResource DataText}"
                           VerticalAlignment="Center" Margin="12,0"/>

                <Ellipse Grid.Column="3" Style="{StaticResource StatusDot}"
                         Fill="{Binding StatusDot, Converter={StaticResource StatusToBrush}}"/>
            </Grid>
        </DataTemplate>
    </ListBox.ItemTemplate>
</ListBox>
```

Selection is indicated by a 2px white left edge, not a coloured fill — selection is not a system fact.

- [ ] **Step 2: Replace the list heading**

```xml
<StackPanel Orientation="Horizontal" Margin="16,0,0,10">
    <TextBlock Text="DISPOSITIVOS" Style="{StaticResource SectionHeading}"/>
    <TextBlock x:Name="DeviceCountText" Text="0" Style="{StaticResource DataText}"
               FontSize="11" Foreground="{StaticResource TextLabelBrush}" Margin="10,0,0,0"/>
</StackPanel>
```

Delete the old `Sorted by Connection` dropdown and the magnifier `Path` using `SearchIconPath`.

- [ ] **Step 3: Feed the count**

In `MainWindow.xaml.cs`, inside `ApplyFilters()`, after `DevicesListBox.ItemsSource = filtered;` add:

```csharp
            DeviceCountText.Text = filtered.Count.ToString();
```

- [ ] **Step 4: Verify**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git commit -m "feat: monochrome master list with tested status dots"
```

---

### Task 8: Build the detail panel

The old right-hand panel was already bound to `DevicesListBox.SelectedItem` — a detail panel disguised as a graph. This makes it honest.

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `UsbDeviceModel.DisplayRate` / `LatencyText` / `SpeedText` / `BIntervalText` / `FilterText` / `InstanceId` / `Status` / `SpeedKnown` (Task 4), `DriverState.ModeText` (Task 4)
- Produces: named element `DetailRateCombo`, handler `DetailRateCombo_SelectionChanged`

- [ ] **Step 1: Delete the fake graph**

Delete the `<!-- Smooth stability line graph (Simulated spline path) -->` border and everything inside it: both `<Path>` elements with hardcoded Bezier `Data`, the highlight `<Ellipse>`, and the `10s ago / 5s ago / Now` timeline `Grid`.

These were coordinates typed by hand. They never measured anything.

- [ ] **Step 2: Replace the whole Performance Monitor panel**

```xml
<Grid DataContext="{Binding SelectedItem, ElementName=DevicesListBox}" Margin="24">
    <!-- Empty state: shown until a device is picked. Better than "--" in every field. -->
    <TextBlock Text="Selecciona un dispositivo" Style="{StaticResource FieldLabel}"
               VerticalAlignment="Center" HorizontalAlignment="Center">
        <TextBlock.Style>
            <Style TargetType="TextBlock" BasedOn="{StaticResource FieldLabel}">
                <Setter Property="Visibility" Value="Collapsed"/>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding SelectedItem, ElementName=DevicesListBox}" Value="{x:Null}">
                        <Setter Property="Visibility" Value="Visible"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </TextBlock.Style>
    </TextBlock>

    <StackPanel>
        <StackPanel.Style>
            <Style TargetType="StackPanel">
                <Setter Property="Visibility" Value="Visible"/>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding SelectedItem, ElementName=DevicesListBox}" Value="{x:Null}">
                        <Setter Property="Visibility" Value="Collapsed"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </StackPanel.Style>

        <TextBlock Text="{Binding Name}" Foreground="{StaticResource TextDataBrush}" FontSize="15"/>

        <TextBlock Text="{Binding DisplayRate}" Style="{StaticResource RateDisplay}" Margin="0,18,0,0"/>
        <TextBlock Text="{Binding LatencyText}" Style="{StaticResource DataText}"
                   Foreground="{StaticResource TextLabelBrush}" FontSize="15"/>

        <Rectangle Height="1" Fill="{StaticResource BorderBrush}" Margin="0,22"/>

        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="110"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <Grid.RowDefinitions>
                <RowDefinition Height="26"/>
                <RowDefinition Height="26"/>
                <RowDefinition Height="26"/>
                <RowDefinition Height="26"/>
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0" Grid.Column="0" Text="VELOCIDAD" Style="{StaticResource FieldLabel}"/>
            <TextBlock Grid.Row="0" Grid.Column="1" Text="{Binding SpeedText}" Style="{StaticResource DataText}"/>
            <Ellipse   Grid.Row="0" Grid.Column="2" Style="{StaticResource StatusDot}"
                       Fill="{Binding StatusDot, Converter={StaticResource StatusToBrush}}"/>

            <TextBlock Grid.Row="1" Grid.Column="0" Text="bINTERVAL" Style="{StaticResource FieldLabel}"/>
            <TextBlock Grid.Row="1" Grid.Column="1" Text="{Binding BIntervalText}" Style="{StaticResource DataText}"/>

            <TextBlock Grid.Row="2" Grid.Column="0" Text="FILTRO" Style="{StaticResource FieldLabel}"/>
            <TextBlock Grid.Row="2" Grid.Column="1" Text="{Binding FilterText}" Style="{StaticResource DataText}"/>

            <TextBlock Grid.Row="3" Grid.Column="0" Text="INTERVALO" Style="{StaticResource FieldLabel}"/>
            <TextBlock Grid.Row="3" Grid.Column="1" Text="{Binding IntervalModeText}" Style="{StaticResource DataText}"/>
        </Grid>

        <TextBlock Text="INSTANCE ID" Style="{StaticResource FieldLabel}" Margin="0,14,0,3"/>
        <TextBlock Text="{Binding InstanceId}" Style="{StaticResource DataText}" FontSize="10"
                   Foreground="{StaticResource TextLabelBrush}" TextWrapping="Wrap"/>

        <TextBlock Text="TASA OBJETIVO" Style="{StaticResource FieldLabel}" Margin="0,22,0,6"/>
        <ComboBox x:Name="DetailRateCombo" Width="150" HorizontalAlignment="Left"
                  FontFamily="{StaticResource MonoFont}"
                  SelectionChanged="DetailRateCombo_SelectionChanged"/>

        <StackPanel Orientation="Horizontal" Margin="0,22,0,0">
            <Button Content="FILTRO" Style="{StaticResource InstrumentButton}"
                    Click="FilterToggle_Click" Margin="0,0,10,0"/>
            <Button Content="REINICIAR" Style="{StaticResource InstrumentButton}"
                    Click="RestartDevice_Click"/>
        </StackPanel>
    </StackPanel>
</Grid>
```

- [ ] **Step 3: Consolidate rate selection into the detail panel**

The rate selector existed twice (a `ComboBox` per row and a context menu). It now lives in one place.

In `MainWindow.xaml.cs`, delete `RateComboBox_Loaded`, `RateComboBox_SelectionChanged`, `MenuItemRate_Click`, `SetComboBoxToRate`, `CardFilterToggle_Click`, `CardRestartDevice_Click`, `MenuItemToggleFilter_Click`, `MenuItemRestart_Click`, `MenuItemCopyId_Click`, `ThreeDotsButton_Click` and `CopyId_Click`. Delete the per-row `ComboBox`, the three-dots `Button` and its `ContextMenu` from the XAML.

Add:

```csharp
        // The list selection drives the detail panel, so the rate options are
        // rebuilt whenever the selected device changes.
        private void DevicesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PopulateRateCombo(DevicesListBox.SelectedItem as UsbDeviceModel);
        }

        private void PopulateRateCombo(UsbDeviceModel? model)
        {
            DetailRateCombo.SelectionChanged -= DetailRateCombo_SelectionChanged;
            DetailRateCombo.Items.Clear();

            if (model == null)
            {
                DetailRateCombo.IsEnabled = false;
                DetailRateCombo.SelectionChanged += DetailRateCombo_SelectionChanged;
                return;
            }

            // Slots 31/62 are relabelled through the driver mode AND this device's
            // bus speed, so a Full Speed device is never offered a rate the patch
            // cannot give it.
            foreach (var slot in new[] { 0, 31, 62, 125, 250, 500, 1000 })
            {
                string label = slot == 0
                    ? "Default"
                    : $"{PollingCore.ResolveHighRateSlot(slot, ActiveMode, model.BusSpeed) ?? slot} Hz";

                bool reachable = slot == 0 ||
                                 PollingCore.TryMapRateToBInterval(slot, model.BusSpeed) != null;

                DetailRateCombo.Items.Add(new ComboBoxItem { Content = label, Tag = slot, IsEnabled = reachable });
            }

            foreach (ComboBoxItem item in DetailRateCombo.Items)
            {
                if ((int)item.Tag == (model.SelectedRate ?? 0)) { DetailRateCombo.SelectedItem = item; break; }
            }

            DetailRateCombo.IsEnabled = model.SpeedKnown;
            DetailRateCombo.ToolTip = model.SpeedKnown
                ? null
                : "Velocidad de bus desconocida: el intervalo no se puede calcular con seguridad.";

            DetailRateCombo.SelectionChanged += DetailRateCombo_SelectionChanged;
        }

        private void DetailRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is not UsbDeviceModel model) return;
            if (DetailRateCombo.SelectedItem is not ComboBoxItem item) return;

            int rate = (int)item.Tag;
            if (model.SelectedRate == rate) return;
            if (!ApplyRate(model, rate)) PopulateRateCombo(model);
        }
```

Wire the list in `MainWindow.xaml`: add `SelectionChanged="DevicesListBox_SelectionChanged"` to `DevicesListBox`.

In `FilterToggle_Click`, replace the `CheckBox` cast with the button flow:

```csharp
        private void FilterToggle_Click(object sender, RoutedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is UsbDeviceModel model) ToggleFilter(model);
        }
```

In `RestartDevice_Click`, replace the `DataContext` cast:

```csharp
        private async void RestartDevice_Click(object sender, RoutedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is not UsbDeviceModel model) return;
            if (sender is not Button btn) return;

            btn.IsEnabled = false;
            await RestartOne(model);
            btn.IsEnabled = true;
        }
```

- [ ] **Step 4: Verify**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git commit -m "feat: real detail panel replaces the simulated graph"
```

---

### Task 9: Restyle the System view and remove dead code

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

- [ ] **Step 1: Remove the dead search filter**

In `ApplyFilters()`, the `searchText` local is always `""`, so the whole match is dead. Replace the method body's filter with:

```csharp
        private void ApplyFilters()
        {
            bool onlyControllers = OnlyControllersCheck.IsChecked == true;
            bool onlyFiltered = OnlyFilteredCheck.IsChecked == true;

            var filtered = _allDevices.Where(d =>
                (!onlyControllers || d.IconKind == "Gamepad") &&
                (!onlyFiltered || d.FilterActive)).ToList();

            DevicesListBox.ItemsSource = filtered;
            DeviceCountText.Text = filtered.Count.ToString();
        }
```

Delete the `SearchIconPath` geometry from `MainWindow.xaml`. There was never a search box behind it.

- [ ] **Step 2: Rebuild the System view**

Replace the whole Settings `TabItem` content with:

```xml
<ScrollViewer VerticalScrollBarVisibility="Auto">
    <StackPanel Margin="24" MaxWidth="620" HorizontalAlignment="Left">

        <TextBlock Text="SERVICIO" Style="{StaticResource SectionHeading}"/>
        <Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
                BorderThickness="1" Padding="18" Margin="0,8,0,20">
            <StackPanel>
                <TextBlock Text="Driver hidusbf (lower filter). Requiere Administrador."
                           Style="{StaticResource FieldLabel}" TextWrapping="Wrap"/>
                <StackPanel Orientation="Horizontal" Margin="0,14,0,0">
                    <Button x:Name="InstallServiceBtn" Content="INSTALAR" Style="{StaticResource InstrumentButton}"
                            Click="InstallServiceBtn_Click" Margin="0,0,10,0"/>
                    <Button x:Name="UninstallServiceBtn" Content="DESINSTALAR" Style="{StaticResource InstrumentButton}"
                            Click="UninstallServiceBtn_Click" Margin="0,0,10,0"/>
                    <Button x:Name="RestartAllBtn" Content="REINICIAR FILTRADOS" Style="{StaticResource InstrumentButton}"
                            Click="RestartAllBtn_Click"/>
                </StackPanel>
            </StackPanel>
        </Border>

        <TextBlock Text="MODO DEL DRIVER" Style="{StaticResource SectionHeading}"/>
        <Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
                BorderThickness="1" Padding="18" Margin="0,8,0,20">
            <StackPanel>
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="ACTIVO" Style="{StaticResource FieldLabel}" VerticalAlignment="Center"/>
                    <TextBlock x:Name="DriverModeText" Text="--" Style="{StaticResource DataText}"
                               Margin="10,0,0,0" VerticalAlignment="Center"/>
                </StackPanel>
                <TextBlock Text="El modo lo fija PatchUSBXHCI en el registro, no el archivo .sys instalado."
                           Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Margin="0,10,0,0"/>
                <ComboBox x:Name="GlobalModeComboBox" Width="200" HorizontalAlignment="Left"
                          FontFamily="{StaticResource MonoFont}" Margin="0,12,0,0"
                          SelectionChanged="GlobalModeComboBox_SelectionChanged">
                    <ComboBoxItem Content="NOPATCH"/>
                    <ComboBoxItem Content="1kHz"/>
                    <ComboBoxItem Content="2kHz-4kHz"/>
                    <ComboBoxItem Content="4kHz-8kHz"/>
                </ComboBox>
            </StackPanel>
        </Border>

        <TextBlock Text="LISTA" Style="{StaticResource SectionHeading}"/>
        <Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
                BorderThickness="1" Padding="18" Margin="0,8,0,0">
            <StackPanel>
                <CheckBox x:Name="OnlyControllersCheck" Content="Solo controles y gamepads"
                          Foreground="{StaticResource TextDataBrush}" FontSize="12"
                          Checked="Filter_Changed" Unchecked="Filter_Changed"/>
                <CheckBox x:Name="OnlyFilteredCheck" Content="Solo dispositivos con filtro"
                          Foreground="{StaticResource TextDataBrush}" FontSize="12" Margin="0,10,0,0"
                          Checked="Filter_Changed" Unchecked="Filter_Changed"/>
            </StackPanel>
        </Border>

    </StackPanel>
</ScrollViewer>
```

Note the `1kHz` item content: `PollingCore.ParseMode` splits on the first space, so the old `1kHz (1000Hz)` label parsed fine — but the bare label is what the mode round-trips to. Keep it bare.

Every remaining `{DynamicResource ...Brush}` reference anywhere in `MainWindow.xaml` must become `{StaticResource ...Brush}` pointing at a `Theme.xaml` key. The old dynamic brushes no longer exist, so any leftover resolves to nothing and renders transparent.

- [ ] **Step 3: Prove no blue survives**

Run:
```bash
grep -inE "6366F1|818CF8|0F172A|1E293B|312E81|EEF2FF|E6E9F2|AccentIndigo|AccentLightIndigo|SidebarBg|PanelBg|WindowBg|CardDark|InputBg|TextPrimary|TextSecondary" HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
```
Expected: no output. Any hit is a leftover from the old palette — fix it before committing.

- [ ] **Step 4: Verify**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git commit -m "feat: restyle system view, drop dead search filter"
```

---

### Task 10: Verify the whole thing

WPF binding errors do not fail the build — they fail silently at runtime. This task exists because a green build proves nothing about a redesign.

**Files:** none

- [ ] **Step 1: Full test run**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Failed: 0, Passed: 83`.

- [ ] **Step 2: Confirm the backend probe still agrees**

Run: `cd VerifyState && dotnet run`
Expected: unchanged from the baseline — `Build (by hash) : NoPatch`, `ModeText : No Patch`, 7 devices, `Full Speed` offered `slot 31 -> 31 Hz` under 4kHz-8kHz.

- [ ] **Step 3: Run the app and read the binding log**

Run: `cd HidusbfModernGui && dotnet run`

In the build output window, search for `System.Windows.Data Error`. Expected: none. Each one is a binding pointing at a property that does not exist — a silent hole in the UI.

- [ ] **Step 4: Check against the spec, by eye**

The engineer cannot verify this alone. Confirm with the user:
- Background is true black, not navy.
- No indigo anywhere.
- The only colour on screen is status dots.
- Numbers are monospaced and do not shift the layout when the rate changes.
- With no device selected, the detail panel says "Selecciona un dispositivo" rather than showing `--` in every field.
- The driver header reads amber (`No Patch` is capped), and the service dot is green while it is running.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: verify monochrome redesign end to end"
```

---

## Notes for the implementer

**Do not touch `SystemManager`'s logic.** It was rewritten and verified against the live system on 2026-07-15: it identifies the installed driver by SHA-256 rather than trusting the registry, writes `PatchUSBPort` as 0/1 only (the documented range), and refuses rate changes it cannot make safely instead of silently writing 125 Hz. Task 3 moves type declarations out of its file; that is all.

**The rings are gone on purpose.** They were connected earlier the same day, having previously been hardcoded decoration. They are still being deleted, because expressing "which of 4 driver modes" as a percentage is invented precision — "100%" for 4kHz-8kHz is a number nobody measured.

**The graph is gone until it can measure.** The right-hand panel showed a hand-typed Bezier curve with `10s ago / 5s ago / Now` labels under it. Real polling-rate measurement is a separate project; leaving a decorative graph in the meantime is the exact dishonesty this redesign exists to remove.
