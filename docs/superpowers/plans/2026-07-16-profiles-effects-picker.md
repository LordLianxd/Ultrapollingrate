# Profiles, Rainbow and a Real Colour Picker — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Three things the light page is missing — an HSV colour picker instead of RGB sliders, an animated rainbow effect, and profiles that carry both the polling rate and the lights.

**Architecture:** Three independent pieces layered on what exists. `ColourMath` and `RainbowEffect` are pure and tested. `ProfileStore` serialises to JSON under `%APPDATA%`, with a backup before every write. The picker is a WPF control drawing an HSV square with a hue strip. Everything writes through the existing `DualSenseLight.Apply`; nothing new touches HID.

**Tech Stack:** .NET 9 (`net9.0-windows`), WPF, `System.Text.Json`.

## What exists and must not be re-invented

- `DualSenseLight.Apply(string usbInstanceId, LightState state)` → `OpResult`. Verified against the real DualSense: the lightbar went orange, and the player LEDs light symmetric patterns.
- `LightState(byte R, byte G, byte B, PlayerLeds Player, LedBrightness Brightness)`.
- `PollingCore.TryMapRateToBInterval(int rate, UsbSpeed speed)` → `int?` — null when the rate is not reachable.
- `SystemManager.SetDeviceRate(string instanceId, string driverKey, int rate, UsbSpeed speed)` → `OpResult`.
- `MainWindow` has `_allDevices`, `LogStatus(string)`, `ShowError(string,string)`, `_updatingLight`, `_lightDebounce`, `ApplyLightNow()`, `CurrentLight()`, `RefreshPlayStationDevices()`.

## Global Constraints

- **`SystemManager.cs` and `DualSenseLight.cs` must not change.** Not one line.
- **`PollingCore.cs` may only gain pure functions.** The 139 tests must stay green **without being modified**.
- Palette — exactly these ten, no eleventh: `#000000`, `#0A0A0A`, `#111111`, `#1F1F1F`, `#FFFFFF`, `#8A8A8A`, `#4A4A4A`, `#00C853`, `#FFAB00`, `#FF3D00`.
- **Colour never decorates.** The sanctioned exceptions are the colour swatch, the preset swatches and the picker itself — they show the colour that *is* the data. Everything else stays monochrome.
- Data font `Cascadia Mono, Consolas, Courier New`; UI font `Segoe UI Variable Display, Segoe UI`. No light theme.
- Target framework `net9.0-windows`. Branch `redesign/monochrome`. Build from `HidusbfModernGui/`.
- Git identity is not configured globally. Commit with:
  `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "..."`
  End every commit message with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

## Three decisions this plan encodes

**A profile carries the rate but never replugs.** Applying a rate writes `bInterval`, which does nothing until the device is re-enumerated. A profile click silently disconnecting the user's controller would be a hostile surprise. So the profile writes, and the page already tells the truth: the headline number is the **measured** rate, and a mismatch already raises "Escrita pero no aplicada — pulsa RECONECTAR". The existing honesty machinery covers this; do not add an auto-replug.

**The effect stops when the app closes.** A rainbow is a loop of writes, not a state the controller stores. Closing UltraPolling leaves the pad on its last colour. The UI must say so — a user who thinks the pad keeps cycling will think it broke.

**Touching a colour turns the effect off.** While the rainbow owns the colour, the picker and presets are meaningless. Rather than disabling them, picking a colour ends the effect. That is the least surprising rule: the last thing you touched wins.

---

### Task 1: `ColourMath` and `RainbowEffect` — pure, tested

**Files:**
- Create: `HidusbfModernGui/ColourMath.cs`
- Test: `HidusbfModernGui.Tests/ColourMathTests.cs`

**Interfaces:**
- Produces:
  - `static (byte R, byte G, byte B) ColourMath.HsvToRgb(double h, double s, double v)` — h in [0,360), s and v in [0,1]
  - `static (double H, double S, double V) ColourMath.RgbToHsv(byte r, byte g, byte b)`
  - `static (byte R, byte G, byte B) RainbowEffect.ColourAt(double seconds, double cycleSeconds)` — used by Task 4's timer

- [ ] **Step 1: Write the failing tests**

Create `HidusbfModernGui.Tests/ColourMathTests.cs`:

```csharp
using System;
using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    public class ColourMathTests
    {
        [Theory]
        [InlineData(0, 1, 1, 255, 0, 0)]       // red
        [InlineData(120, 1, 1, 0, 255, 0)]     // green
        [InlineData(240, 1, 1, 0, 0, 255)]     // blue
        [InlineData(60, 1, 1, 255, 255, 0)]    // yellow
        [InlineData(180, 1, 1, 0, 255, 255)]   // cyan
        [InlineData(300, 1, 1, 255, 0, 255)]   // magenta
        public void HsvToRgb_HitsThePrimaries(double h, double s, double v, byte r, byte g, byte b)
        {
            var c = ColourMath.HsvToRgb(h, s, v);
            Assert.Equal((r, g, b), c);
        }

        [Fact]
        public void HsvToRgb_ZeroValue_IsBlackWhateverTheHue()
        {
            Assert.Equal(((byte)0, (byte)0, (byte)0), ColourMath.HsvToRgb(200, 1, 0));
        }

        [Fact]
        public void HsvToRgb_ZeroSaturation_IsGreyScaledByValue()
        {
            Assert.Equal(((byte)255, (byte)255, (byte)255), ColourMath.HsvToRgb(200, 0, 1));
            Assert.Equal(((byte)128, (byte)128, (byte)128), ColourMath.HsvToRgb(200, 0, 0.502));
        }

        // 360 and 0 are the same hue. A picker dragged to the far edge must not wrap to
        // a different colour than the near edge.
        [Fact]
        public void HsvToRgb_Hue360_EqualsHue0()
        {
            Assert.Equal(ColourMath.HsvToRgb(0, 1, 1), ColourMath.HsvToRgb(360, 1, 1));
        }

        [Theory]
        [InlineData(255, 0, 0, 0)]
        [InlineData(0, 255, 0, 120)]
        [InlineData(0, 0, 255, 240)]
        public void RgbToHsv_RecoversTheHue(byte r, byte g, byte b, double expectedHue)
        {
            var (h, s, v) = ColourMath.RgbToHsv(r, g, b);
            Assert.Equal(expectedHue, h, 1);
            Assert.Equal(1.0, s, 2);
            Assert.Equal(1.0, v, 2);
        }

        // Black has no hue and no saturation. Reporting a hue for it would make a picker
        // jump when the user drags value down to zero and back up.
        [Fact]
        public void RgbToHsv_Black_HasNoSaturation()
        {
            var (_, s, v) = ColourMath.RgbToHsv(0, 0, 0);
            Assert.Equal(0, s, 3);
            Assert.Equal(0, v, 3);
        }

        [Theory]
        [InlineData(255, 100, 0)]
        [InlineData(12, 200, 90)]
        [InlineData(0, 0, 255)]
        [InlineData(255, 255, 255)]
        public void RgbToHsv_RoundTrips(byte r, byte g, byte b)
        {
            var (h, s, v) = ColourMath.RgbToHsv(r, g, b);
            var back = ColourMath.HsvToRgb(h, s, v);

            // One count of rounding is acceptable; more means the conversion is lossy in
            // a way the user would see as the picker drifting.
            Assert.True(Math.Abs(back.R - r) <= 1, $"R {back.R} vs {r}");
            Assert.True(Math.Abs(back.G - g) <= 1, $"G {back.G} vs {g}");
            Assert.True(Math.Abs(back.B - b) <= 1, $"B {back.B} vs {b}");
        }
    }

    public class RainbowEffectTests
    {
        [Fact]
        public void ColourAt_StartsAtRed()
        {
            Assert.Equal(((byte)255, (byte)0, (byte)0), RainbowEffect.ColourAt(0, 6));
        }

        // The whole point of a cycle: t and t+period are the same colour, or the loop
        // would visibly jump every time it came round.
        [Theory]
        [InlineData(0)]
        [InlineData(1.5)]
        [InlineData(4.2)]
        public void ColourAt_IsPeriodic(double t)
        {
            Assert.Equal(RainbowEffect.ColourAt(t, 6), RainbowEffect.ColourAt(t + 6, 6));
        }

        [Fact]
        public void ColourAt_HalfwayIsCyan()
        {
            // Half the cycle = 180 degrees of hue = cyan.
            Assert.Equal(((byte)0, (byte)255, (byte)255), RainbowEffect.ColourAt(3, 6));
        }

        [Fact]
        public void ColourAt_IsFullySaturatedAndBright()
        {
            // A rainbow that drifts toward grey is a broken rainbow. Every sample must
            // sit on the outer edge of the hue wheel.
            for (double t = 0; t < 6; t += 0.25)
            {
                var (r, g, b) = RainbowEffect.ColourAt(t, 6);
                Assert.Equal(255, Math.Max(r, Math.Max(g, b)));
                Assert.Equal(0, Math.Min(r, Math.Min(g, b)));
            }
        }

        // A zero or negative period would divide by zero. Freeze rather than throw: this
        // runs on a timer, and an exception there would take the app down.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ColourAt_WithNonsensePeriod_FreezesAtRed(double period)
        {
            Assert.Equal(((byte)255, (byte)0, (byte)0), RainbowEffect.ColourAt(2, period));
        }
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: FAIL — `The name 'ColourMath' does not exist`.

- [ ] **Step 3: Implement**

Create `HidusbfModernGui/ColourMath.cs`:

```csharp
using System;

namespace HidusbfModernGui
{
    // HSV <-> RGB. A picker works in HSV because that is how people think about colour -
    // "the same blue but darker" is one axis in HSV and three in RGB.
    public static class ColourMath
    {
        // h in [0,360], s and v in [0,1].
        public static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;   // 360 and 0 are the same hue
            s = Math.Clamp(s, 0, 1);
            v = Math.Clamp(v, 0, 1);

            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            (double r, double g, double b) p = h switch
            {
                < 60 => (c, x, 0),
                < 120 => (x, c, 0),
                < 180 => (0, c, x),
                < 240 => (0, x, c),
                < 300 => (x, 0, c),
                _ => (c, 0, x)
            };

            return ((byte)Math.Round((p.r + m) * 255),
                    (byte)Math.Round((p.g + m) * 255),
                    (byte)Math.Round((p.b + m) * 255));
        }

        public static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double d = max - min;

            // Grey has no hue. Returning an arbitrary one would make a picker's handle
            // jump when the user drags value to zero and back.
            double h = 0;
            if (d > 0)
            {
                if (max == rd) h = 60 * (((gd - bd) / d) % 6);
                else if (max == gd) h = 60 * ((bd - rd) / d + 2);
                else h = 60 * ((rd - gd) / d + 4);
            }
            if (h < 0) h += 360;

            return (h, max == 0 ? 0 : d / max, max);
        }
    }

    // A colour cycle driven by elapsed time rather than by a frame counter, so its speed
    // does not depend on how often the timer actually fires.
    public static class RainbowEffect
    {
        public static (byte R, byte G, byte B) ColourAt(double seconds, double cycleSeconds)
        {
            // A zero or negative period would divide by zero. This runs on a timer, where
            // an exception would take the app down - freeze instead.
            if (cycleSeconds <= 0) return ColourMath.HsvToRgb(0, 1, 1);

            double hue = (seconds / cycleSeconds % 1.0) * 360.0;
            return ColourMath.HsvToRgb(hue, 1, 1);
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 164` (139 existing + 25 new). Any other number, stop and report.

`ColourMath.cs` must be linked into the test project the way the others are:
`<Compile Include="..\HidusbfModernGui\ColourMath.cs" Link="ColourMath.cs" />`

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/ColourMath.cs HidusbfModernGui.Tests/ColourMathTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git commit -m "feat: HSV conversion and a time-driven rainbow, both pure

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: `LightProfile` and `ProfileStore`

**Files:**
- Create: `HidusbfModernGui/LightProfile.cs`
- Test: `HidusbfModernGui.Tests/ProfileStoreTests.cs`

**Interfaces:**
- Produces:
  - `sealed class LightProfile` with `Name`, `Rate` (`int?`), `R`, `G`, `B`, `Player`, `Brightness`, `Rainbow` (bool)
  - `static class ProfileStore` with `List<LightProfile> Load()`, `OpResult Save(IEnumerable<LightProfile>)`, `string Path { get; }`

- [ ] **Step 1: Write the failing tests**

Create `HidusbfModernGui.Tests/ProfileStoreTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    public class ProfileStoreTests : IDisposable
    {
        private readonly string _dir;

        public ProfileStoreTests()
        {
            // A real temp directory, not a mock: this class exists to touch the disk, so
            // testing it against a fake would test nothing.
            _dir = Path.Combine(Path.GetTempPath(), "ultrapolling-tests-" + Guid.NewGuid().ToString("N"));
            ProfileStore.OverrideDirectoryForTests(_dir);
        }

        public void Dispose()
        {
            ProfileStore.OverrideDirectoryForTests(null);
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static LightProfile Sample(string name = "CoD") => new LightProfile
        {
            Name = name,
            Rate = 1000,
            R = 255, G = 100, B = 0,
            Player = PlayerLeds.Player1,
            Brightness = LedBrightness.High,
            Rainbow = false
        };

        [Fact]
        public void Load_WithNoFile_IsEmpty_NotAnException()
        {
            Assert.Empty(ProfileStore.Load());
        }

        [Fact]
        public void SaveThenLoad_RoundTripsEveryField()
        {
            var saved = ProfileStore.Save(new[] { Sample() });
            Assert.True(saved.Success, saved.Error);

            var loaded = ProfileStore.Load().Single();
            Assert.Equal("CoD", loaded.Name);
            Assert.Equal(1000, loaded.Rate);
            Assert.Equal(255, loaded.R);
            Assert.Equal(100, loaded.G);
            Assert.Equal(0, loaded.B);
            Assert.Equal(PlayerLeds.Player1, loaded.Player);
            Assert.Equal(LedBrightness.High, loaded.Brightness);
            Assert.False(loaded.Rainbow);
        }

        // A profile that does not touch the rate is a real case - "just make it red".
        [Fact]
        public void NullRate_SurvivesTheRoundTrip()
        {
            var p = Sample();
            p.Rate = null;
            ProfileStore.Save(new[] { p });
            Assert.Null(ProfileStore.Load().Single().Rate);
        }

        [Fact]
        public void Save_OverwritesRatherThanAppending()
        {
            ProfileStore.Save(new[] { Sample("uno"), Sample("dos") });
            ProfileStore.Save(new[] { Sample("tres") });

            var loaded = ProfileStore.Load();
            Assert.Single(loaded);
            Assert.Equal("tres", loaded[0].Name);
        }

        // DSX backs up its save file before every write, and it is the cheapest possible
        // insurance: a crash mid-write would otherwise lose every profile the user has.
        [Fact]
        public void Save_BacksUpThePreviousFileFirst()
        {
            ProfileStore.Save(new[] { Sample("original") });
            ProfileStore.Save(new[] { Sample("reemplazo") });

            string backup = ProfileStore.Path + ".backup";
            Assert.True(File.Exists(backup), "no backup was written");
            Assert.Contains("original", File.ReadAllText(backup));
        }

        // The first write has nothing to back up. Copying a non-existent file would throw
        // and lose the very first profile the user ever saves.
        [Fact]
        public void Save_FirstEverWrite_NeedsNoBackup()
        {
            var result = ProfileStore.Save(new[] { Sample() });
            Assert.True(result.Success, result.Error);
            Assert.False(File.Exists(ProfileStore.Path + ".backup"), "backed up a file that did not exist yet");
        }

        // A corrupt file must not take the app down on startup. Losing the profiles is
        // bad; refusing to launch is worse.
        [Fact]
        public void Load_WithCorruptJson_IsEmpty_NotAnException()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(ProfileStore.Path, "{ this is not json");
            Assert.Empty(ProfileStore.Load());
        }

        [Fact]
        public void Load_WithAnEmptyFile_IsEmpty()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(ProfileStore.Path, "");
            Assert.Empty(ProfileStore.Load());
        }

        [Fact]
        public void Save_CreatesTheDirectoryIfItIsMissing()
        {
            Assert.False(Directory.Exists(_dir));
            Assert.True(ProfileStore.Save(new[] { Sample() }).Success);
            Assert.True(File.Exists(ProfileStore.Path));
        }
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: FAIL — `The name 'ProfileStore' does not exist`.

- [ ] **Step 3: Implement**

Create `HidusbfModernGui/LightProfile.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HidusbfModernGui
{
    // A saved setup. It carries the polling rate as well as the lights, because that is
    // the pairing nothing else offers: DSX will not touch the rate, and hidusbf knows
    // nothing about the lightbar.
    //
    // A mutable class rather than a record: System.Text.Json needs a parameterless
    // constructor and settable properties to round-trip this without extra ceremony.
    public sealed class LightProfile
    {
        public string Name { get; set; } = "";

        // Null means "do not touch the rate" - a profile that only changes the colour.
        public int? Rate { get; set; }

        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public PlayerLeds Player { get; set; } = PlayerLeds.Player1;
        public LedBrightness Brightness { get; set; } = LedBrightness.High;
        public bool Rainbow { get; set; }

        public LightState ToLightState() => new LightState(R, G, B, Player, Brightness);
    }

    // Profiles on disk as JSON, under %APPDATA%. Backed up before every write.
    public static class ProfileStore
    {
        private static string? _overrideDir;

        // Tests need a real directory to write to; this class exists to touch the disk,
        // so faking the filesystem would test nothing.
        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "profiles.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // Enums as names, so a hand-edited file reads "Player1" rather than "4" and
            // a future reordering of the enum cannot silently remap someone's profiles.
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static List<LightProfile> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new List<LightProfile>();

                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new List<LightProfile>();

                return JsonSerializer.Deserialize<List<LightProfile>>(json, Options) ?? new List<LightProfile>();
            }
            catch (Exception ex)
            {
                // Losing the profiles is bad; refusing to launch because of a corrupt file
                // is worse. The backup beside it is the way back.
                Debug.WriteLine($"ProfileStore.Load failed, starting empty: {ex.Message}");
                return new List<LightProfile>();
            }
        }

        public static OpResult Save(IEnumerable<LightProfile> profiles)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);

                // Copy the old file aside before overwriting. A crash mid-write would
                // otherwise take every profile the user has with it.
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);

                File.WriteAllText(Path, JsonSerializer.Serialize(profiles, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudieron guardar los perfiles: {ex.Message}");
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 173` (164 + 9 new).

Link the new file into the test project:
`<Compile Include="..\HidusbfModernGui\LightProfile.cs" Link="LightProfile.cs" />`

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/LightProfile.cs HidusbfModernGui.Tests/ProfileStoreTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git commit -m "feat: profiles carrying rate and lights, saved with a backup

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: The HSV picker control

**Files:**
- Create: `HidusbfModernGui/ColourPicker.xaml`
- Create: `HidusbfModernGui/ColourPicker.xaml.cs`

**Interfaces:**
- Consumes: `ColourMath.HsvToRgb`, `ColourMath.RgbToHsv` (Task 1)
- Produces: a `UserControl` named `ColourPicker` with:
  - `Color SelectedColor { get; set; }` — a dependency property
  - `event EventHandler? ColorChanged`

- [ ] **Step 1: The XAML**

Create `HidusbfModernGui/ColourPicker.xaml`:

```xml
<UserControl x:Class="HidusbfModernGui.ColourPicker"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <StackPanel>
        <!-- The saturation/value square. White to hue across, transparent to black down:
             two gradients over each other give every shade of one hue. -->
        <Border BorderBrush="{StaticResource BorderBrush}" BorderThickness="1" Width="260" Height="160"
                HorizontalAlignment="Left">
            <Grid x:Name="SvSquare" ClipToBounds="True"
                  MouseLeftButtonDown="Sv_MouseDown" MouseMove="Sv_MouseMove" MouseLeftButtonUp="Sv_MouseUp">
                <Rectangle x:Name="HueLayer"/>
                <Rectangle>
                    <Rectangle.Fill>
                        <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                            <GradientStop Offset="0" Color="#FFFFFFFF"/>
                            <GradientStop Offset="1" Color="#00FFFFFF"/>
                        </LinearGradientBrush>
                    </Rectangle.Fill>
                </Rectangle>
                <Rectangle>
                    <Rectangle.Fill>
                        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                            <GradientStop Offset="0" Color="#00000000"/>
                            <GradientStop Offset="1" Color="#FF000000"/>
                        </LinearGradientBrush>
                    </Rectangle.Fill>
                </Rectangle>
                <!-- A ring, not a filled dot: it has to stay visible on both white and
                     black, and an outline reads on either. -->
                <Ellipse x:Name="SvCursor" Width="12" Height="12" Stroke="#FFFFFF" StrokeThickness="2"
                         IsHitTestVisible="False" HorizontalAlignment="Left" VerticalAlignment="Top"/>
            </Grid>
        </Border>

        <!-- The hue strip. -->
        <Border BorderBrush="{StaticResource BorderBrush}" BorderThickness="1" Width="260" Height="18"
                HorizontalAlignment="Left" Margin="0,10,0,0">
            <Grid x:Name="HueStrip" ClipToBounds="True"
                  MouseLeftButtonDown="Hue_MouseDown" MouseMove="Hue_MouseMove" MouseLeftButtonUp="Hue_MouseUp">
                <Rectangle>
                    <Rectangle.Fill>
                        <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                            <GradientStop Offset="0.000" Color="#FF0000"/>
                            <GradientStop Offset="0.167" Color="#FFFF00"/>
                            <GradientStop Offset="0.333" Color="#00FF00"/>
                            <GradientStop Offset="0.500" Color="#00FFFF"/>
                            <GradientStop Offset="0.667" Color="#0000FF"/>
                            <GradientStop Offset="0.833" Color="#FF00FF"/>
                            <GradientStop Offset="1.000" Color="#FF0000"/>
                        </LinearGradientBrush>
                    </Rectangle.Fill>
                </Rectangle>
                <Rectangle x:Name="HueCursor" Width="3" Fill="#FFFFFF" IsHitTestVisible="False"
                           HorizontalAlignment="Left"/>
            </Grid>
        </Border>
    </StackPanel>
</UserControl>
```

The gradient stops here are raw hex on purpose: they are the hue wheel itself, which is the data this control exists to show. They are not palette colours and must not be replaced with palette brushes.

- [ ] **Step 2: The code-behind**

Create `HidusbfModernGui/ColourPicker.xaml.cs`:

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HidusbfModernGui
{
    // An HSV picker: a saturation/value square plus a hue strip. People think in HSV -
    // "the same blue but darker" is one axis here and three in RGB.
    public partial class ColourPicker : UserControl
    {
        private double _h = 240, _s = 1, _v = 1;
        private bool _draggingSv, _draggingHue;

        // Set while this control writes SelectedColor itself, so its own update does not
        // come back through the property-changed callback and fight the drag.
        private bool _internal;

        public static readonly DependencyProperty SelectedColorProperty =
            DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(ColourPicker),
                new FrameworkPropertyMetadata(Colors.Blue,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

        public Color SelectedColor
        {
            get => (Color)GetValue(SelectedColorProperty);
            set => SetValue(SelectedColorProperty, value);
        }

        public event EventHandler? ColorChanged;

        public ColourPicker()
        {
            InitializeComponent();
            Loaded += (_, _) => Redraw();
        }

        private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var picker = (ColourPicker)d;
            if (picker._internal) return;

            var c = (Color)e.NewValue;
            (picker._h, picker._s, picker._v) = ColourMath.RgbToHsv(c.R, c.G, c.B);
            picker.Redraw();
        }

        private void Emit()
        {
            var (r, g, b) = ColourMath.HsvToRgb(_h, _s, _v);

            _internal = true;
            SelectedColor = Color.FromRgb(r, g, b);
            _internal = false;

            ColorChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Redraw()
        {
            if (HueLayer == null) return;

            // The square shows every shade of the current hue, so its base is that hue at
            // full saturation and value.
            var (hr, hg, hb) = ColourMath.HsvToRgb(_h, 1, 1);
            HueLayer.Fill = new SolidColorBrush(Color.FromRgb(hr, hg, hb));

            double w = SvSquare.ActualWidth, h = SvSquare.ActualHeight;
            if (w > 0 && h > 0)
                SvCursor.Margin = new Thickness(_s * w - 6, (1 - _v) * h - 6, 0, 0);

            if (HueStrip.ActualWidth > 0)
                HueCursor.Margin = new Thickness(_h / 360.0 * HueStrip.ActualWidth - 1.5, 0, 0, 0);
        }

        private void SetSvFrom(Point p)
        {
            double w = SvSquare.ActualWidth, h = SvSquare.ActualHeight;
            if (w <= 0 || h <= 0) return;

            _s = Math.Clamp(p.X / w, 0, 1);
            _v = Math.Clamp(1 - p.Y / h, 0, 1);
            Redraw();
            Emit();
        }

        private void SetHueFrom(Point p)
        {
            double w = HueStrip.ActualWidth;
            if (w <= 0) return;

            _h = Math.Clamp(p.X / w, 0, 1) * 360;
            Redraw();
            Emit();
        }

        // Capturing the mouse is what lets a drag continue past the edge of the square.
        // Without it the handle sticks the moment the pointer leaves.
        private void Sv_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = true;
            SvSquare.CaptureMouse();
            SetSvFrom(e.GetPosition(SvSquare));
        }

        private void Sv_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingSv) SetSvFrom(e.GetPosition(SvSquare));
        }

        private void Sv_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = false;
            SvSquare.ReleaseMouseCapture();
        }

        private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = true;
            HueStrip.CaptureMouse();
            SetHueFrom(e.GetPosition(HueStrip));
        }

        private void Hue_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingHue) SetHueFrom(e.GetPosition(HueStrip));
        }

        private void Hue_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = false;
            HueStrip.ReleaseMouseCapture();
        }
    }
}
```

- [ ] **Step 3: Verify**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.`

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 173`.

- [ ] **Step 4: Commit**

```bash
git add HidusbfModernGui/ColourPicker.xaml HidusbfModernGui/ColourPicker.xaml.cs
git commit -m "feat: an HSV colour picker

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Wire the picker, the rainbow and the profiles into the page

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-3, plus the existing `DualSenseLight.Apply`, `SystemManager.SetDeviceRate`, `PollingCore.TryMapRateToBInterval`

- [ ] **Step 1: Replace the RGB sliders with the picker**

In the light page's COLOR border, delete the three `StackPanel`s holding `RedSlider`, `GreenSlider` and `BlueSlider`, and put the picker where they were:

```xml
<local:ColourPicker x:Name="Picker" ColorChanged="Picker_ColorChanged" HorizontalAlignment="Left"/>
```

Keep `PresetRow`, `ColourSwatch` and `HexText`. `MainWindow.xaml` already declares `xmlns:local="clr-namespace:HidusbfModernGui"`.

- [ ] **Step 2: Add the effect and profile UI**

After the LED DE JUGADOR border:

```xml
<TextBlock Text="EFECTO" Style="{StaticResource SectionHeading}" Margin="0,20,0,0"/>
<Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
        BorderThickness="1" Padding="18" Margin="0,8,0,0">
    <StackPanel>
        <StackPanel Orientation="Horizontal">
            <CheckBox x:Name="RainbowCheck" Content="Rainbow" Foreground="{StaticResource TextDataBrush}"
                      FontSize="12" VerticalAlignment="Center" Margin="0,0,20,0"
                      Checked="Rainbow_Toggled" Unchecked="Rainbow_Toggled"/>
            <TextBlock Text="CICLO" Style="{StaticResource FieldLabel}" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <Slider x:Name="RainbowSpeed" Minimum="1" Maximum="20" Value="6" Width="160" VerticalAlignment="Center"/>
            <TextBlock x:Name="RainbowSpeedText" Text="6 s" Style="{StaticResource DataText}"
                       VerticalAlignment="Center" Margin="10,0,0,0"/>
        </StackPanel>
        <!-- Said plainly, because a user who thinks the pad stores the effect will think
             it broke when it stops. -->
        <TextBlock Text="El efecto lo anima UltraPolling. Al cerrar la app el mando se queda en el ultimo color."
                   Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Margin="0,10,0,0"/>
    </StackPanel>
</Border>

<TextBlock Text="PERFILES" Style="{StaticResource SectionHeading}" Margin="0,20,0,0"/>
<Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
        BorderThickness="1" Padding="18" Margin="0,8,0,0">
    <StackPanel>
        <TextBlock Text="Un perfil guarda la tasa y la luz. La tasa se escribe pero no se aplica hasta pulsar RECONECTAR."
                   Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Margin="0,0,0,12"/>
        <StackPanel Orientation="Horizontal">
            <ComboBox x:Name="ProfileList" Width="180" HorizontalAlignment="Left" Margin="0,0,10,0"
                      DisplayMemberPath="Name"/>
            <Button Content="APLICAR" Style="{StaticResource InstrumentButton}" Click="ApplyProfile_Click" Margin="0,0,10,0"/>
            <Button Content="GUARDAR" Style="{StaticResource InstrumentButton}" Click="SaveProfile_Click" Margin="0,0,10,0"/>
            <Button Content="BORRAR" Style="{StaticResource InstrumentButton}" Click="DeleteProfile_Click"/>
        </StackPanel>
        <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
            <TextBlock Text="NOMBRE" Style="{StaticResource FieldLabel}" VerticalAlignment="Center" Width="60"/>
            <TextBox x:Name="ProfileName" Width="180" Background="{StaticResource SurfaceAltBrush}"
                     Foreground="{StaticResource TextDataBrush}" BorderBrush="{StaticResource BorderBrush}"
                     BorderThickness="1" Padding="6,4" FontFamily="{StaticResource UiFont}"/>
            <CheckBox x:Name="ProfileIncludesRate" Content="Incluir la tasa actual" Margin="14,0,0,0"
                      Foreground="{StaticResource TextDataBrush}" FontSize="12" VerticalAlignment="Center"
                      IsChecked="True"/>
        </StackPanel>
    </StackPanel>
</Border>
```

- [ ] **Step 3: The code-behind**

```csharp
private DispatcherTimer? _rainbowTimer;
private readonly Stopwatch _rainbowClock = new Stopwatch();
private List<LightProfile> _profiles = new List<LightProfile>();

private void Picker_ColorChanged(object? sender, EventArgs e)
{
    if (_updatingLight) return;

    // Touching a colour ends the effect: while the rainbow owns the colour, a picked one
    // would be overwritten within 33 ms. The last thing you touched wins.
    if (RainbowCheck.IsChecked == true) RainbowCheck.IsChecked = false;

    UpdateSwatch();

    _lightDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    _lightDebounce.Tick -= LightDebounce_Tick;
    _lightDebounce.Tick += LightDebounce_Tick;
    _lightDebounce.Stop();
    _lightDebounce.Start();
}

private void Rainbow_Toggled(object sender, RoutedEventArgs e)
{
    if (RainbowCheck.IsChecked == true)
    {
        _rainbowClock.Restart();
        _rainbowTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };  // ~30 Hz
        _rainbowTimer.Tick -= Rainbow_Tick;
        _rainbowTimer.Tick += Rainbow_Tick;
        _rainbowTimer.Start();
        LogStatus("Rainbow activo. Se detiene al cerrar la app.");
    }
    else
    {
        _rainbowTimer?.Stop();
        _rainbowClock.Stop();
    }
}

private void Rainbow_Tick(object? sender, EventArgs e)
{
    if (PlayStationList.SelectedItem is not UsbDeviceModel model) return;

    RainbowSpeedText.Text = $"{RainbowSpeed.Value:0} s";
    var (r, g, b) = RainbowEffect.ColourAt(_rainbowClock.Elapsed.TotalSeconds, RainbowSpeed.Value);

    // The picker follows the effect so the UI shows what the pad is doing. _updatingLight
    // stops that from bouncing back through Picker_ColorChanged and killing the effect on
    // its first tick.
    _updatingLight = true;
    try
    {
        Picker.SelectedColor = Color.FromRgb(r, g, b);
        UpdateSwatch();
    }
    finally { _updatingLight = false; }

    var state = new LightState(r, g, b,
        (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag,
        (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag);
    DualSenseLight.Apply(model.InstanceId, state);
}

private void LoadProfiles()
{
    _profiles = ProfileStore.Load();
    ProfileList.ItemsSource = null;
    ProfileList.ItemsSource = _profiles;
    if (_profiles.Count > 0) ProfileList.SelectedIndex = 0;
}

private void SaveProfile_Click(object sender, RoutedEventArgs e)
{
    string name = ProfileName.Text.Trim();
    if (string.IsNullOrEmpty(name))
    {
        LogStatus("Ponle un nombre al perfil primero.");
        return;
    }

    var c = Picker.SelectedColor;
    var p = new LightProfile
    {
        Name = name,
        R = c.R, G = c.G, B = c.B,
        Player = (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag,
        Brightness = (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag,
        Rainbow = RainbowCheck.IsChecked == true,
        Rate = ProfileIncludesRate.IsChecked == true
            ? (DevicesListBox.SelectedItem as UsbDeviceModel)?.ResolvedRate
            : null
    };

    // Same name replaces, rather than silently accumulating duplicates the user cannot
    // tell apart in the list.
    _profiles.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    _profiles.Add(p);

    var result = ProfileStore.Save(_profiles);
    if (!result.Success) { ShowError("Perfil no guardado", result.Error!); return; }

    LoadProfiles();
    LogStatus($"Perfil '{name}' guardado.");
}

private void ApplyProfile_Click(object sender, RoutedEventArgs e)
{
    if (ProfileList.SelectedItem is not LightProfile p) { LogStatus("Selecciona un perfil."); return; }
    if (PlayStationList.SelectedItem is not UsbDeviceModel model) { LogStatus("Selecciona un mando."); return; }

    _updatingLight = true;
    try
    {
        Picker.SelectedColor = Color.FromRgb(p.R, p.G, p.B);
        foreach (ComboBoxItem i in PlayerLedList.Items)
            if ((PlayerLeds)i.Tag == p.Player) { PlayerLedList.SelectedItem = i; break; }
        foreach (ComboBoxItem i in BrightnessList.Items)
            if ((LedBrightness)i.Tag == p.Brightness) { BrightnessList.SelectedItem = i; break; }
        RainbowCheck.IsChecked = p.Rainbow;
    }
    finally { _updatingLight = false; }

    UpdateSwatch();
    if (!p.Rainbow) ApplyLightNow();
    else Rainbow_Toggled(sender, e);

    if (p.Rate == null) { LogStatus($"Perfil '{p.Name}' aplicado."); return; }

    // The rate goes to the device selected on the Dashboard, which is where rates live.
    // Applying it here would otherwise silently target the wrong device.
    if (DevicesListBox.SelectedItem is not UsbDeviceModel target)
    {
        LogStatus($"Perfil '{p.Name}' aplicado (luz). Selecciona un dispositivo en Dispositivos para su tasa.");
        return;
    }

    var rateResult = SystemManager.SetDeviceRate(target.InstanceId, target.DriverKey, p.Rate.Value, target.BusSpeed);
    LogStatus(rateResult.Success
        // Deliberately not auto-replugging: yanking the user's controller off the bus
        // because they clicked a profile would be a hostile surprise. The detail panel
        // already shows measured-vs-requested and says to press RECONECTAR.
        ? $"Perfil '{p.Name}' aplicado. Tasa {p.Rate} Hz escrita: pulsa RECONECTAR para que surta efecto."
        : $"Perfil '{p.Name}': luz aplicada, pero la tasa fallo: {rateResult.Error}");
}

private void DeleteProfile_Click(object sender, RoutedEventArgs e)
{
    if (ProfileList.SelectedItem is not LightProfile p) { LogStatus("Selecciona un perfil."); return; }

    _profiles.RemoveAll(x => x.Name == p.Name);
    var result = ProfileStore.Save(_profiles);
    if (!result.Success) { ShowError("Perfil no borrado", result.Error!); return; }

    LoadProfiles();
    LogStatus($"Perfil '{p.Name}' borrado. Hay una copia en {ProfileStore.Path}.backup");
}
```

- [ ] **Step 4: Rewire what the sliders used to drive**

`UpdateSwatch()` read `RedSlider.Value` and friends, which are gone. Point it at the picker:

```csharp
private void UpdateSwatch()
{
    if (ColourSwatch == null) return;
    var c = Picker.SelectedColor;
    ColourSwatch.Background = new SolidColorBrush(c);
    HexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
```

`CurrentLight()` likewise:

```csharp
private LightState CurrentLight()
{
    var c = Picker.SelectedColor;
    return new LightState(c.R, c.G, c.B,
        (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag,
        (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag);
}
```

`Preset_Click` and `RestoreLight_Click` set `RedSlider.Value` etc. Replace those assignments with `Picker.SelectedColor = Color.FromRgb(...)`, keeping the existing `try/finally` around `_updatingLight`.

Add `LoadProfiles();` to `RefreshPlayStationDevices()`.

Stop the rainbow in `CloseButton_Click`, beside the meter:

```csharp
_rainbowTimer?.Stop();
```

- [ ] **Step 5: Verify**

Run: `cd HidusbfModernGui && dotnet build` → succeeds.
Run: `cd HidusbfModernGui.Tests && dotnet test` → `Passed: 173`.

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git commit -m "feat: HSV picker, rainbow effect and profiles on the light page

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Verify end to end

**Files:** none

- [ ] **Step 1: Tests and build**

Run: `cd HidusbfModernGui.Tests && dotnet test` → `Passed: 173`
Run: `cd HidusbfModernGui && dotnet build` → succeeds

- [ ] **Step 2: Backend untouched**

Run: `cd VerifyState && dotnet run`
Expected: `Build (by hash) : NoPatch`, `ModeText : No Patch`, a non-zero device count. Do not assert an exact count — it is environmental.

- [ ] **Step 3: The palette gate**

```bash
grep -inE "6366F1|818CF8|0F172A|1E293B|312E81|EEF2FF|E6E9F2|AccentIndigo|SidebarBg|PanelBg|WindowBg|CardDark|InputBg|TextPrimary|TextSecondary" HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs HidusbfModernGui/Theme.xaml
```
Expected: empty.

`ColourPicker.xaml` is exempt: its gradient stops are the hue wheel, which is the data the control exists to show.

- [ ] **Step 4: Binding errors**

Run the app and check the debug output for `System.Windows.Data Error`. Expected: none.

- [ ] **Step 5: Drive it — hardware, eyes on the controller**

- **Drag inside the picker square** → the pad follows when you stop, not during. No stutter.
- **Drag the hue strip** → the pad sweeps through the spectrum.
- **Tick Rainbow** → the pad cycles smoothly, and the picker's handle moves with it.
- **While the rainbow runs, click a preset** → the rainbow stops and the preset colour holds. This is the rule "the last thing you touched wins".
- **Save a profile** named something, with "Incluir la tasa actual" ticked.
- **Change the colour, then apply the profile** → the colour comes back.
- **Check the file** at `%APPDATA%\UltraPolling\profiles.json` — it should be readable JSON with enum names, not numbers.
- **Save twice, then look for** `profiles.json.backup`.
- **Applying a profile with a rate** must NOT disconnect the controller. It writes the rate and says to press RECONECTAR.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: verify profiles, rainbow and picker end to end

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Notes for the implementer

**`_updatingLight` is load-bearing in three new places.** The rainbow tick sets the picker; applying a profile sets the picker and both combos; a preset sets the picker. Without the guard each would bounce back through `Picker_ColorChanged`, and since that handler turns the rainbow off, **the rainbow would kill itself on its first tick**. Every one of them uses `try/finally`.

**The rainbow writes ~30 times a second.** That is the point — it is an animation. But it also means the anti-cheat warning on this page matters more, not less: leaving a rainbow running during a match is continuous unsigned HID traffic to a gamepad.

**Do not auto-replug when a profile carries a rate.** The honest machinery already exists: the detail panel shows the measured rate as the headline and raises "Escrita pero no aplicada" when it disagrees with the request. A profile that yanked the controller off the bus would be a hostile surprise.

**The backup is not ceremony.** DSX backs up its save file before every write, and it is the cheapest insurance there is: without it, one bad write loses every profile the user has.
