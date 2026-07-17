# DualSense Lightbar Colour — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A sidebar page, below the home button, where a user with a PlayStation controller can set its lightbar colour and its player-indicator LEDs.

**Architecture:** The DualSense accepts a 48-byte HID output report. Report ID `0x02` over USB; byte 2 carries flags saying what the packet changes; bytes 45/46/47 are the lightbar's RGB, byte 44 is the player-LED pattern and byte 43 their brightness. `PollingMeter` already owns the HID plumbing (path lookup, capability reading, handle opening), so this reuses it rather than adding a second HID stack. The write path is new — everything in this app so far only reads.

## The player LEDs are a bit pattern, not a number

The five white lights under the touchpad are addressed as a 5-bit mask, and the console lights them symmetrically rather than counting up:

```
PLAYER_1 =  4 = 0b00100    the centre light alone
PLAYER_2 = 10 = 0b01010
PLAYER_3 = 21 = 0b10101
PLAYER_4 = 27 = 0b11011
ALL      = 31 = 0b11111
OFF      =  0 = 0b00000
```

Writing `1` for "player 1" would light the leftmost LED, which is not what a PS5 does. Use the constants; do not compute them.

Brightness (byte 43) is `0` high, `1` medium, `2` low — **inverted**, so a bigger number is dimmer.

**Tech Stack:** .NET 9 (`net9.0-windows`), WPF, `hid.dll` / `setupapi.dll` P/Invoke.

**Reference:** [pydualsense](https://github.com/flok/pydualsense) — its `prepareReport()` is where the byte layout below comes from.

## Measured facts (from spikes, not from the docs)

Run against the user's DualSense, filter and overclock active:

```
VID_054C PID_0CE6, one HID interface, UsagePage=0x01 Usage=0x05
InputReportByteLength   = 64
OutputReportByteLength  = 48     <- NOT 64
FeatureReportByteLength = 64
CreateFile with GENERIC_WRITE    -> succeeds
```

**The report is 48 bytes, not 64.** pydualsense hardcodes 64 for USB; Windows computes 48 from the report descriptor. The LED bytes at 45/46/47 land in the last three bytes of a 48-byte buffer, which is consistent. **Read the length from the device; never hardcode it.** A HID write must be exactly `OutputReportByteLength` bytes or it fails.

An earlier spike read `OutputReportByteLength` as 0 and concluded this feature was impossible. That spike had the `HIDP_CAPS` offsets wrong by four bytes and was quoting a reserved field. The user's report that DSX and Steam work is what exposed it. Trust the marshalled struct.

## Global Constraints

- **`SystemManager.cs` must not change.** No registry, service, scanning, hash identification or replug edits.
- **`PollingCore.cs` may only gain pure functions.** The 116 tests must stay green **without being modified**.
- Palette — exactly these ten, no eleventh: `#000000`, `#0A0A0A`, `#111111`, `#1F1F1F`, `#FFFFFF`, `#8A8A8A`, `#4A4A4A`, `#00C853`, `#FFAB00`, `#FF3D00`.
- **Colour never decorates.** The one deliberate exception is the colour swatch itself: it shows the user the colour they picked, which *is* the data. Everything else in the page obeys the rule.
- Data font `Cascadia Mono, Consolas, Courier New`; UI font `Segoe UI Variable Display, Segoe UI`. No light theme.
- Target framework `net9.0-windows`. Branch `redesign/monochrome`. Build from `HidusbfModernGui/`.
- Git identity is not configured globally. Commit with:
  `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "..."`
  End every commit message with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

## The anti-cheat question, stated once

This app already reads the controller's HID stream. This feature makes it **write** to the controller, which is strictly more invasive. From a kernel anti-cheat's view, an unsigned process sending HID output reports to a gamepad during a match resembles an input-manipulation tool.

DSX and Steam do exactly this and are not banned — but they are known, signed, widely-deployed software. That is not evidence about an unsigned app.

**This does not block the feature.** It does mean the page must not encourage leaving the app open during play. Task 5 adds that warning. Do not soften it.

---

### Task 1: `DualSenseLight` — build and send the report

**Files:**
- Create: `HidusbfModernGui/DualSenseLight.cs`
- Test: `HidusbfModernGui.Tests/DualSenseLightTests.cs`

**Interfaces:**
- Consumes: `HidDeviceLocator.FindHidPaths(string)`, `PollingMeter.TryGetCaps(string)` → `HIDP_CAPS?` (has `OutputReportByteLength`)
- Produces:
  - `enum PlayerLeds : byte { Off = 0, Player1 = 4, Player2 = 10, Player3 = 21, Player4 = 27, All = 31 }`
  - `enum LedBrightness : byte { High = 0, Medium = 1, Low = 2 }`
  - `readonly record struct LightState(byte R, byte G, byte B, PlayerLeds Player, LedBrightness Brightness)`
  - `static bool DualSenseLight.IsPlayStation(UsbDeviceModel)` — used by Task 3's list filter
  - `static byte[] DualSenseLight.BuildLightReport(LightState state, int reportLength)` — pure, tested here
  - `static OpResult DualSenseLight.Apply(string usbInstanceId, LightState state)` — used by Task 4

One report carries both the colour and the player LEDs, so applying them together is one
write rather than two.

- [ ] **Step 1: Write the failing tests**

Create `HidusbfModernGui.Tests/DualSenseLightTests.cs`:

```csharp
using System;
using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    public class DualSenseLightTests
    {
        private static LightState Blue => new LightState(0, 0, 255, PlayerLeds.Off, LedBrightness.High);

        // Report ID 0x02 is the USB output report. Without it the controller ignores
        // the packet entirely.
        [Fact]
        public void Report_StartsWithTheUsbReportId()
        {
            Assert.Equal(0x02, DualSenseLight.BuildLightReport(Blue, 48)[0]);
        }

        // A HID write must be exactly OutputReportByteLength bytes. The device reports
        // 48; pydualsense hardcodes 64. Honour the device.
        [Theory]
        [InlineData(48)]
        [InlineData(64)]
        public void Report_IsExactlyTheLengthTheDeviceAsksFor(int len)
        {
            Assert.Equal(len, DualSenseLight.BuildLightReport(Blue, len).Length);
        }

        // Byte 2 bit 0x04 is "this packet changes the LED strips"; 0x10 is "this packet
        // changes the player indicator LEDs". Without them the bytes are carried and
        // ignored.
        [Fact]
        public void Report_SetsTheLedAndPlayerFlags()
        {
            var r = DualSenseLight.BuildLightReport(Blue, 48);
            Assert.Equal(0x04, r[2] & 0x04);
            Assert.Equal(0x10, r[2] & 0x10);
        }

        // Byte 1 carries the motor and audio flags. Zero means this packet does not
        // touch rumble, triggers or volume - we are setting lights, not taking over the
        // controller.
        [Fact]
        public void Report_NeverTouchesMotorsOrAudio()
        {
            Assert.Equal(0x00, DualSenseLight.BuildLightReport(Blue, 48)[1]);
        }

        [Fact]
        public void Report_CarriesRgbAtTheDocumentedOffsets()
        {
            var r = DualSenseLight.BuildLightReport(new LightState(0x11, 0x22, 0x33, PlayerLeds.Off, LedBrightness.High), 48);
            Assert.Equal(0x11, r[45]);
            Assert.Equal(0x22, r[46]);
            Assert.Equal(0x33, r[47]);
        }

        // The player LEDs are a 5-bit mask over the lights under the touchpad, lit
        // symmetrically. Writing 1 for "player 1" would light the leftmost LED, which is
        // not what a PS5 does.
        [Theory]
        [InlineData(PlayerLeds.Off, 0)]
        [InlineData(PlayerLeds.Player1, 4)]    // 0b00100 - centre only
        [InlineData(PlayerLeds.Player2, 10)]   // 0b01010
        [InlineData(PlayerLeds.Player3, 21)]   // 0b10101
        [InlineData(PlayerLeds.Player4, 27)]   // 0b11011
        [InlineData(PlayerLeds.All, 31)]       // 0b11111
        public void Report_CarriesThePlayerBitPattern(PlayerLeds player, byte expected)
        {
            var r = DualSenseLight.BuildLightReport(new LightState(0, 0, 0, player, LedBrightness.High), 48);
            Assert.Equal(expected, r[44]);
        }

        // Brightness is inverted: a bigger number is dimmer.
        [Theory]
        [InlineData(LedBrightness.High, 0)]
        [InlineData(LedBrightness.Medium, 1)]
        [InlineData(LedBrightness.Low, 2)]
        public void Report_CarriesBrightnessAtByte43(LedBrightness b, byte expected)
        {
            var r = DualSenseLight.BuildLightReport(new LightState(0, 0, 0, PlayerLeds.All, b), 48);
            Assert.Equal(expected, r[43]);
        }

        // Anything we do not deliberately set must be zero. A stray byte in a report
        // this dense could mean a trigger force or a motor speed.
        [Fact]
        public void Report_LeavesEveryOtherByteZero()
        {
            var r = DualSenseLight.BuildLightReport(new LightState(0xAA, 0xBB, 0xCC, PlayerLeds.All, LedBrightness.Low), 48);
            for (int i = 0; i < r.Length; i++)
            {
                if (i is 0 or 2 or 43 or 44 or 45 or 46 or 47) continue;
                Assert.Equal(0, r[i]);
            }
        }

        // 45/46/47 must fit. A device advertising a shorter report cannot carry the
        // colour, and silently writing a truncated packet would be worse than refusing.
        [Fact]
        public void Report_RefusesALengthTooShortToHoldTheColour()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => DualSenseLight.BuildLightReport(Blue, 40));
        }

        // Black is a colour the user can choose, not an absence of one. The flag must
        // still say "apply", or picking black would silently do nothing.
        [Fact]
        public void Report_BlackIsAValidColour_NotAnAbsentOne()
        {
            var r = DualSenseLight.BuildLightReport(new LightState(0, 0, 0, PlayerLeds.Off, LedBrightness.High), 48);
            Assert.Equal(0x04, r[2] & 0x04);
            Assert.Equal(0, r[45]);
        }

        [Theory]
        [InlineData(0x054C, true)]    // Sony
        [InlineData(0x045E, false)]   // Microsoft
        [InlineData(0x046D, false)]   // Logitech
        public void IsPlayStation_MatchesOnSonysVendorId(int vid, bool expected)
        {
            var m = new UsbDeviceModel { InstanceId = $@"USB\VID_{vid:X4}&PID_0CE6\6&227ba791&0&4" };
            Assert.Equal(expected, DualSenseLight.IsPlayStation(m));
        }

        [Fact]
        public void IsPlayStation_IsCaseInsensitive()
        {
            var m = new UsbDeviceModel { InstanceId = @"usb\vid_054c&pid_0ce6\6&227ba791&0&4" };
            Assert.True(DualSenseLight.IsPlayStation(m));
        }

        [Fact]
        public void IsPlayStation_OnGarbage_IsFalse_NotAnException()
        {
            Assert.False(DualSenseLight.IsPlayStation(new UsbDeviceModel { InstanceId = "" }));
            Assert.False(DualSenseLight.IsPlayStation(new UsbDeviceModel { InstanceId = "nonsense" }));
        }
    }
}
```

- [ ] **Step 2: Run them and watch them fail for the right reason**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: FAIL — `The name 'DualSenseLight' does not exist`. If it fails any other way, stop.

- [ ] **Step 3: Implement**

Create `HidusbfModernGui/DualSenseLight.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HidusbfModernGui
{
    // The five white lights under the touchpad. A 5-bit mask, lit symmetrically - the
    // console does not simply count up, so "player 1" is the centre light alone rather
    // than the leftmost. Values from pydualsense's PlayerID enum.
    public enum PlayerLeds : byte
    {
        Off = 0,        // 0b00000
        Player1 = 4,    // 0b00100
        Player2 = 10,   // 0b01010
        Player3 = 21,   // 0b10101
        Player4 = 27,   // 0b11011
        All = 31        // 0b11111
    }

    // Inverted: a bigger number is dimmer.
    public enum LedBrightness : byte
    {
        High = 0,
        Medium = 1,
        Low = 2
    }

    // Everything the lights can be set to. One report carries all of it, so the UI can
    // apply colour and player LEDs in a single write.
    public readonly record struct LightState(byte R, byte G, byte B, PlayerLeds Player, LedBrightness Brightness);

    // Sets the DualSense lights by sending a HID output report. Layout taken from
    // pydualsense's prepareReport(): report 0x02 over USB, byte 2 flags what the packet
    // changes, byte 43 brightness, byte 44 the player mask, bytes 45/46/47 the RGB.
    //
    // Everything else in this app only reads from devices. This writes, and it writes to
    // the user's controller - so it changes exactly the bytes it means to and leaves the
    // rest zero.
    public static class DualSenseLight
    {
        private const byte OUTPUT_REPORT_USB = 0x02;
        private const byte FLAG_LED_STRIPS = 0x04;    // byte 2: sets the lightbar
        private const byte FLAG_PLAYER_LEDS = 0x10;   // byte 2: sets the player indicator
        private const int OFFSET_BRIGHTNESS = 43, OFFSET_PLAYER = 44;
        private const int OFFSET_R = 45, OFFSET_G = 46, OFFSET_B = 47;

        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr sec, uint disposition, uint flags, IntPtr template);

        public static bool IsPlayStation(UsbDeviceModel model) =>
            model?.InstanceId?.IndexOf("VID_054C", StringComparison.OrdinalIgnoreCase) >= 0;

        // The report, built and testable without touching hardware.
        //
        // reportLength comes from the device's OutputReportByteLength - 48 on the
        // DualSense, though pydualsense hardcodes 64. A HID write must be exactly that
        // length, so it is a parameter rather than a constant.
        public static byte[] BuildLightReport(LightState state, int reportLength)
        {
            if (reportLength <= OFFSET_B)
                throw new ArgumentOutOfRangeException(nameof(reportLength),
                    $"An output report of {reportLength} bytes cannot carry the colour, which lives at bytes {OFFSET_R}-{OFFSET_B}.");

            var report = new byte[reportLength];
            report[0] = OUTPUT_REPORT_USB;

            // Byte 1 stays zero: it flags motors, triggers and audio. We are setting
            // lights, not seizing the controller.
            report[1] = 0x00;
            report[2] = FLAG_LED_STRIPS | FLAG_PLAYER_LEDS;

            report[OFFSET_BRIGHTNESS] = (byte)state.Brightness;
            report[OFFSET_PLAYER] = (byte)state.Player;
            report[OFFSET_R] = state.R;
            report[OFFSET_G] = state.G;
            report[OFFSET_B] = state.B;
            return report;
        }

        public static OpResult Apply(string usbInstanceId, LightState state)
        {
            var paths = HidDeviceLocator.FindHidPaths(usbInstanceId);
            if (paths.Count == 0)
                return OpResult.Fail("Este dispositivo no expone interfaz HID.");

            foreach (var path in paths)
            {
                var caps = PollingMeter.TryGetCaps(path);
                if (caps == null || caps.Value.OutputReportByteLength == 0) continue;

                int len = caps.Value.OutputReportByteLength;
                byte[] report;
                try { report = BuildLightReport(state, len); }
                catch (ArgumentOutOfRangeException ex) { return OpResult.Fail(ex.Message); }

                using var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                                              // Shared: DSX, Steam or a game may hold the
                                              // controller too. Exclusive access would
                                              // take it away from them.
                                              FILE_SHARE_READ | FILE_SHARE_WRITE,
                                              IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    return OpResult.Fail(err == 5
                        ? "Acceso denegado al mando. Otra aplicacion lo tiene en exclusiva."
                        : $"No se pudo abrir el mando para escritura (error {err}).");
                }

                try
                {
                    using var stream = new FileStream(handle, FileAccess.Write, len, false);
                    stream.Write(report, 0, len);
                    stream.Flush();
                    return OpResult.Ok();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DualSenseLight.Apply: {ex.Message}");
                    return OpResult.Fail($"El mando rechazo el cambio: {ex.Message}");
                }
            }

            return OpResult.Fail("Ninguna interfaz HID de este dispositivo acepta output reports.");
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 139` (116 existing + 23 new). Any other number, stop and report.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/DualSenseLight.cs HidusbfModernGui.Tests/DualSenseLightTests.cs
git commit -m "feat: build and send the DualSense lightbar report

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Verify against the real controller before building any UI

The tests prove the report is well-formed. They cannot prove the controller accepts it. Everything after this task assumes it does, so settle it now with the smallest possible thing.

**Files:**
- Create: `VerifyState/LightCheck.cs` (temporary; deleted in Step 4)

- [ ] **Step 1: Add a temporary probe**

`VerifyState` already links `SystemManager.cs`. Add `DualSenseLight.cs`, `HidDeviceLocator.cs` and `PollingMeter.cs` to its `<ItemGroup>` the same way, then create `VerifyState/LightCheck.cs`:

```csharp
using System;
using HidusbfModernGui;

public static class LightCheck
{
    // Temporary. Proves the controller accepts the report, then gets deleted.
    public static void Run(string instanceId)
    {
        Console.WriteLine("=== Prueba de la barra de luz ===");
        Console.WriteLine("Mira el mando. Deberia ponerse ROJO, luego VERDE, luego AZUL.\n");

        foreach (var (name, r, g, b) in new[]
                 {
                     ("ROJO",  (byte)255, (byte)0,   (byte)0),
                     ("VERDE", (byte)0,   (byte)255, (byte)0),
                     ("AZUL",  (byte)0,   (byte)0,   (byte)255),
                 })
        {
            var state = new LightState(r, g, b, PlayerLeds.Off, LedBrightness.High);
            var result = DualSenseLight.Apply(instanceId, state);
            Console.WriteLine($"  {name,-6} -> {(result.Success ? "escrito" : "FALLO: " + result.Error)}");
            if (!result.Success) return;
            System.Threading.Thread.Sleep(1500);
        }

        // The player LEDs are the part most likely to be wrong: the bit patterns are not
        // a count, so a mistake here lights the wrong lamps rather than none.
        Console.WriteLine("\n=== Prueba de los LED de jugador ===");
        Console.WriteLine("Mira las 5 luces bajo el touchpad. El patron debe ser SIMETRICO.\n");

        foreach (var (name, p) in new[]
                 {
                     ("PLAYER 1 (solo la del centro)", PlayerLeds.Player1),
                     ("PLAYER 2", PlayerLeds.Player2),
                     ("PLAYER 3", PlayerLeds.Player3),
                     ("PLAYER 4", PlayerLeds.Player4),
                     ("TODAS",    PlayerLeds.All),
                     ("APAGADAS", PlayerLeds.Off),
                 })
        {
            var state = new LightState(0, 0, 255, p, LedBrightness.High);
            var result = DualSenseLight.Apply(instanceId, state);
            Console.WriteLine($"  {name,-30} (mascara {(byte)p,2} = 0b{Convert.ToString((byte)p, 2).PadLeft(5, '0')}) -> {(result.Success ? "escrito" : "FALLO: " + result.Error)}");
            if (!result.Success) return;
            System.Threading.Thread.Sleep(1500);
        }

        Console.WriteLine("\nSi los colores cambiaron y los patrones eran simetricos: funciona.");
        Console.WriteLine("Si decia 'escrito' pero nada cambio, el reporte se acepta y se IGNORA");
        Console.WriteLine("- probablemente falte un flag. No lo des por bueno.");
        Console.WriteLine("Si PLAYER 1 encendio la luz de la IZQUIERDA en vez de la del CENTRO,");
        Console.WriteLine("la mascara esta mal y hay que revisarla antes de seguir.");
    }
}
```

Call it from `VerifyState/Program.cs` with the DualSense's instance ID.

- [ ] **Step 2: Run it and WATCH THE CONTROLLER**

Run: `cd VerifyState && dotnet run`

**This is a hardware test. The report writing successfully is not the pass condition — the controller changing colour is.** A HID write can be accepted and dropped.

Three outcomes, and they mean different things:
- **Colour changes** → the layout is right. Proceed.
- **"escrito" but no colour change** → the report is accepted and ignored. A flag is missing or an offset is off. **Stop and report.** Do not build UI on top of this.
- **The write fails** → read the error. Access denied means something holds the controller exclusively.

- [ ] **Step 3: Report the result honestly**

Write what actually happened into the report file. If the colour did not change, say so and stop the plan here. A UI in front of a write that does nothing is worse than no feature — it is the exact lie this app was rebuilt to stop telling.

- [ ] **Step 4: Remove the probe**

Delete `VerifyState/LightCheck.cs`, its call in `Program.cs`, and the `<Compile Include>` lines added in Step 1. `VerifyState` is a read-only diagnostic; a colour-changing side effect does not belong in it permanently.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: verify the DualSense accepts the lightbar report

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: The sidebar destination

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `SidebarButton` style, `GamepadIconPath` geometry (both already exist)
- Produces: `LightNavBtn_Click` handler; `MainTabControl` index 2

- [ ] **Step 1: Add the button below the home button**

In the sidebar's `StackPanel`, immediately after the Dispositivos button:

```xml
<Button Style="{StaticResource SidebarButton}" Click="LightNavBtn_Click" ToolTip="Color del mando (PlayStation)">
    <Path Data="{StaticResource GamepadIconPath}" Fill="{StaticResource TextLabelBrush}" Stretch="Uniform" Width="18" Height="18"/>
</Button>
```

`GamepadIconPath` is already declared in `Window.Resources` — the device list uses it. Do not add a new geometry.

- [ ] **Step 2: Add the handler**

In `MainWindow.xaml.cs`, next to `SettingsNavBtn_Click`:

```csharp
private void LightNavBtn_Click(object sender, RoutedEventArgs e)
{
    MainTabControl.SelectedIndex = 2;
    RefreshPlayStationDevices();
}
```

- [ ] **Step 3: Verify**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.` The button is present; the page it points at arrives in Task 4. `SelectedIndex = 2` on a two-tab control is ignored by WPF rather than throwing, so this compiles and runs.

- [ ] **Step 4: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git commit -m "feat: add the controller page to the sidebar

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: The light page

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `DualSenseLight.IsPlayStation`, `DualSenseLight.Apply`, `LightState`, `PlayerLeds`, `LedBrightness` (Task 1); `_allDevices` (already a field)
- Produces: named elements `PlayStationList`, `LightPanel`, `LightEmptyState`, `ColourSwatch`, `RedSlider`, `GreenSlider`, `BlueSlider`, `HexText`, `PlayerLedList`, `BrightnessList`, `PresetRow`

## Why this page applies live instead of having an APPLY button

Colour is the one case where an on-screen preview lies. The `#FF6400` on a monitor is not
the `#FF6400` on a plastic lightbar: brightness, the diffuser and the room all change it.
**The only honest preview is the controller itself.**

It also matches what this app was rebuilt to be. Everywhere else it stopped showing
requested values and started showing measured reality. An APPLY button reintroduces
exactly that gap — pick a colour, look at a swatch, hope the device agrees.

The cost is real and must be handled: dragging a slider would otherwise fire hundreds of
HID writes per second at the device. A **50 ms debounce** collapses a drag into one write
when the user stops moving. Do not skip it.

## Restoring matters more than it looks

The controller **cannot return to its default on its own** — it needs reconnecting. Someone
who sets it black and closes the app will think they broke it. `RESTAURAR` (blue, Player 1)
is what Windows gives it, and it is the escape hatch. Do not drop it.

Off is a preset, not an error state: turning the light off is a legitimate preference
(battery, a dark room), so it sits with the other presets rather than in a separate button.

- [ ] **Step 1: Add the TabItem**

After the Settings `TabItem`:

```xml
<!-- PAGE 2: CONTROLLER LIGHT -->
<TabItem Header="Light">
    <Grid Margin="24">
        <!-- Empty state. A page of dead sliders would imply the feature is broken
             rather than inapplicable. -->
        <StackPanel x:Name="LightEmptyState" VerticalAlignment="Center" HorizontalAlignment="Center" Visibility="Collapsed">
            <TextBlock Text="Sin mandos de PlayStation conectados" Style="{StaticResource FieldLabel}" HorizontalAlignment="Center"/>
            <TextBlock Text="Esta funcion solo aplica a DualSense y DualShock." Style="{StaticResource FieldLabel}"
                       Foreground="{StaticResource TextMutedBrush}" HorizontalAlignment="Center" Margin="0,6,0,0"/>
        </StackPanel>

        <StackPanel x:Name="LightPanel" MaxWidth="560" HorizontalAlignment="Left">
            <TextBlock Text="MANDO" Style="{StaticResource SectionHeading}"/>
            <ComboBox x:Name="PlayStationList" Width="320" HorizontalAlignment="Left" Margin="0,8,0,20"
                      DisplayMemberPath="Name" SelectionChanged="PlayStationList_SelectionChanged"/>

            <TextBlock Text="COLOR" Style="{StaticResource SectionHeading}"/>
            <Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
                    BorderThickness="1" Padding="18" Margin="0,8,0,0">
                <StackPanel>
                    <!-- Presets first: nobody arrives wanting a specific hex, they arrive
                         wanting "blue". Built in code so each swatch carries its RGB. -->
                    <StackPanel x:Name="PresetRow" Orientation="Horizontal" Margin="0,0,0,16"/>

                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>

                        <StackPanel Grid.Column="0">
                            <!-- Labelled R/G/B rather than coloured: the sliders are
                                 controls, not status. The swatch is where colour lives. -->
                            <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
                                <TextBlock Text="R" Style="{StaticResource FieldLabel}" Width="16" VerticalAlignment="Center"/>
                                <Slider x:Name="RedSlider" Minimum="0" Maximum="255" Value="0" Width="240"
                                        ValueChanged="LightSlider_Changed" VerticalAlignment="Center"/>
                            </StackPanel>
                            <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
                                <TextBlock Text="G" Style="{StaticResource FieldLabel}" Width="16" VerticalAlignment="Center"/>
                                <Slider x:Name="GreenSlider" Minimum="0" Maximum="255" Value="0" Width="240"
                                        ValueChanged="LightSlider_Changed" VerticalAlignment="Center"/>
                            </StackPanel>
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Text="B" Style="{StaticResource FieldLabel}" Width="16" VerticalAlignment="Center"/>
                                <Slider x:Name="BlueSlider" Minimum="0" Maximum="255" Value="255" Width="240"
                                        ValueChanged="LightSlider_Changed" VerticalAlignment="Center"/>
                            </StackPanel>
                        </StackPanel>

                        <!-- The one place colour is not status: this IS the data. -->
                        <Border Grid.Column="1" x:Name="ColourSwatch" Width="72" Height="72"
                                BorderBrush="{StaticResource BorderBrush}" BorderThickness="1"
                                VerticalAlignment="Top" Margin="20,0,0,0"/>
                    </Grid>

                    <TextBlock x:Name="HexText" Text="#0000FF" Style="{StaticResource DataText}" Margin="0,14,0,0"/>
                </StackPanel>
            </Border>

            <TextBlock Text="LED DE JUGADOR" Style="{StaticResource SectionHeading}" Margin="0,20,0,0"/>
            <Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource BorderBrush}"
                    BorderThickness="1" Padding="18" Margin="0,8,0,0">
                <StackPanel>
                    <TextBlock Text="Las 5 luces bajo el touchpad. El patron es simetrico, como en la consola."
                               Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Margin="0,0,0,12"/>
                    <StackPanel Orientation="Horizontal">
                        <ComboBox x:Name="PlayerLedList" Width="150" HorizontalAlignment="Left" Margin="0,0,14,0"
                                  SelectionChanged="LightCombo_Changed"/>
                        <TextBlock Text="BRILLO" Style="{StaticResource FieldLabel}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                        <ComboBox x:Name="BrightnessList" Width="110" HorizontalAlignment="Left"
                                  SelectionChanged="LightCombo_Changed"/>
                    </StackPanel>
                </StackPanel>
            </Border>

            <!-- The controller cannot return to its default without being reconnected, so
                 this is the only way back for someone who turned the light off. -->
            <Button Content="RESTAURAR" Style="{StaticResource InstrumentButton}"
                    HorizontalAlignment="Left" Margin="0,20,0,0"
                    Click="RestoreLight_Click"
                    ToolTip="Azul y Player 1, lo que Windows le pone por defecto. El mando no puede volver solo."/>

            <TextBlock Style="{StaticResource FieldLabel}" Foreground="{StaticResource StatusWarnBrush}"
                       TextWrapping="Wrap" Margin="0,16,0,0"
                       Text="Cambiar el color escribe en el mando. Cierra UltraPolling antes de jugar online: un proceso sin firmar escribiendo al mando durante una partida se parece a una herramienta de manipulacion de entrada."/>
        </StackPanel>
    </Grid>
</TabItem>
```

- [ ] **Step 2: Add the code-behind**

```csharp
// Collapses a slider drag into one write. Without it, dragging fires a HID write per
// pixel of travel - hundreds a second at the device.
private DispatcherTimer? _lightDebounce;

// Set while code (not the user) moves a control, so programmatic updates do not write.
private bool _updatingLight;

// Populated once. Tag carries the value so handlers read a real value rather than
// parsing a label back into meaning.
private void BuildLightControls()
{
    if (PlayerLedList.Items.Count > 0) return;

    foreach (var (label, value) in new (string, PlayerLeds)[]
             {
                 ("Apagados", PlayerLeds.Off),
                 ("Player 1", PlayerLeds.Player1),
                 ("Player 2", PlayerLeds.Player2),
                 ("Player 3", PlayerLeds.Player3),
                 ("Player 4", PlayerLeds.Player4),
                 ("Todas", PlayerLeds.All),
             })
        PlayerLedList.Items.Add(new ComboBoxItem { Content = label, Tag = value });
    PlayerLedList.SelectedIndex = 1;   // Player 1, what Windows shows

    foreach (var (label, value) in new (string, LedBrightness)[]
             {
                 ("Alto", LedBrightness.High),
                 ("Medio", LedBrightness.Medium),
                 ("Bajo", LedBrightness.Low),
             })
        BrightnessList.Items.Add(new ComboBoxItem { Content = label, Tag = value });
    BrightnessList.SelectedIndex = 0;

    // Presets cover the common case in one click. "Apagado" belongs here: turning the
    // light off is a preference, not an error state.
    foreach (var (name, r, g, b) in new (string, byte, byte, byte)[]
             {
                 ("Azul", 0, 0, 255),
                 ("Rojo", 255, 0, 0),
                 ("Verde", 0, 255, 0),
                 ("Cian", 0, 255, 255),
                 ("Magenta", 255, 0, 255),
                 ("Naranja", 255, 100, 0),
                 ("Blanco", 255, 255, 255),
                 ("Apagado", 0, 0, 0),
             })
    {
        var swatch = new Border
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = name,
            Tag = new byte[] { r, g, b }
        };
        swatch.MouseLeftButtonUp += Preset_Click;
        PresetRow.Children.Add(swatch);
    }
}

private void Preset_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
{
    if (sender is not Border { Tag: byte[] rgb } || rgb.Length != 3) return;

    _updatingLight = true;
    RedSlider.Value = rgb[0];
    GreenSlider.Value = rgb[1];
    BlueSlider.Value = rgb[2];
    _updatingLight = false;

    UpdateSwatch();
    ApplyLightNow();   // A preset click is a decision, not a drag - no need to debounce.
}

private LightState CurrentLight() => new LightState(
    (byte)RedSlider.Value,
    (byte)GreenSlider.Value,
    (byte)BlueSlider.Value,
    (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag,
    (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag);

// A slider moving updates the swatch immediately so the UI stays responsive, and starts
// the debounce so the write waits until the user stops dragging.
private void LightSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
{
    if (_updatingLight || ColourSwatch == null) return;
    UpdateSwatch();

    _lightDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    _lightDebounce.Tick -= LightDebounce_Tick;
    _lightDebounce.Tick += LightDebounce_Tick;
    _lightDebounce.Stop();    // restart the countdown on every move
    _lightDebounce.Start();
}

// A combo box is a discrete choice, not a drag: apply it immediately.
private void LightCombo_Changed(object sender, SelectionChangedEventArgs e)
{
    if (_updatingLight || ColourSwatch == null) return;
    ApplyLightNow();
}

private void LightDebounce_Tick(object? sender, EventArgs e)
{
    _lightDebounce?.Stop();
    ApplyLightNow();
}

private void UpdateSwatch()
{
    if (ColourSwatch == null) return;
    byte r = (byte)RedSlider.Value, g = (byte)GreenSlider.Value, b = (byte)BlueSlider.Value;
    ColourSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
    HexText.Text = $"#{r:X2}{g:X2}{b:X2}";
}

private void ApplyLightNow()
{
    if (PlayStationList.SelectedItem is not UsbDeviceModel model) return;
    if (PlayerLedList.SelectedItem == null || BrightnessList.SelectedItem == null) return;

    var result = DualSenseLight.Apply(model.InstanceId, CurrentLight());
    if (!result.Success) LogStatus($"No se pudo cambiar la luz: {result.Error}");
}

private void RestoreLight_Click(object sender, RoutedEventArgs e)
{
    _updatingLight = true;
    RedSlider.Value = 0;
    GreenSlider.Value = 0;
    BlueSlider.Value = 255;
    PlayerLedList.SelectedIndex = 1;   // Player 1
    BrightnessList.SelectedIndex = 0;  // High
    _updatingLight = false;

    UpdateSwatch();
    ApplyLightNow();
    LogStatus("Luz restaurada: azul, Player 1.");
}

// Only PlayStation controllers reach this page. The rest of the app is vendor-neutral;
// this report layout is Sony's alone.
private void RefreshPlayStationDevices()
{
    BuildLightControls();
    var ps = _allDevices.Where(DualSenseLight.IsPlayStation).ToList();

    PlayStationList.ItemsSource = ps;
    if (ps.Count > 0 && PlayStationList.SelectedItem == null) PlayStationList.SelectedIndex = 0;

    LightEmptyState.Visibility = ps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    LightPanel.Visibility = ps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    UpdateSwatch();
}

// Selecting a different controller must not write to it. The user has not asked for a
// colour on this device yet.
private void PlayStationList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSwatch();
```

- [ ] **Step 3: Verify**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.`

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 139`.

- [ ] **Step 4: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git commit -m "feat: DualSense light page with live apply

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Verify end to end

**Files:** none

- [ ] **Step 1: Tests and build**

Run: `cd HidusbfModernGui.Tests && dotnet test` → `Passed: 139`
Run: `cd HidusbfModernGui && dotnet build` → succeeds

- [ ] **Step 2: Backend untouched**

Run: `cd VerifyState && dotnet run`
Expected: `Build (by hash) : NoPatch`, `ModeText : No Patch`, a non-zero device count. Do not assert an exact count — it is environmental.

- [ ] **Step 3: Binding errors**

Run the app and check the debug output for `System.Windows.Data Error`. Expected: none. WPF fails bindings silently at runtime; a green build proves nothing about a new page full of them.

- [ ] **Step 4: Drive it**

With the DualSense connected, open the light page and **look at the controller** throughout:

- **Click a preset** → the lightbar changes at once.
- **Drag a slider** → the bar follows when you stop, not during the drag. It must not stutter or lag: a write per pixel would.
- **Player LEDs: pick "Player 1"** → **the CENTRE lamp lights, alone.** If the leftmost one lights, the 5-bit mask is wrong. This has not been verified on hardware yet — Task 2 confirmed the colour but not the lamp patterns.
- **Player 2, 3, 4** → symmetric patterns, not a count.
- **Preset "Apagado"** → the bar goes dark.
- **RESTAURAR** → blue, Player 1. This is the only way back: the controller cannot restore itself.
- **Unplug the controller** → the empty state, not dead sliders.
- The anti-cheat warning is visible without scrolling.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: verify the lightbar feature end to end

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Notes for the implementer

**Read the report length from the device.** 48 on this DualSense, not the 64 pydualsense hardcodes. A HID write must be exactly `OutputReportByteLength` or it fails. Task 1's tests cover both lengths so the code cannot quietly assume one.

**Task 2 is not a formality.** It is the only step that proves the controller does anything. A HID write can succeed and be silently dropped. If the colour does not change, stop — do not build a UI in front of a write that does nothing.

**The page applies live, and the 50 ms debounce is what makes that safe.** Colour is the one case where an on-screen swatch lies — the only honest preview is the controller. But a raw live-write would fire a HID report per pixel of slider travel. The debounce collapses a drag into one write. Do not remove it, and do not replace live apply with a button.

**Byte 1 stays zero.** It flags motors, triggers and audio. This feature sets a colour; it should not be able to disturb rumble.

**Trust the marshalled struct, not hand-computed offsets.** An earlier spike read `OutputReportByteLength` from the wrong offset, got a reserved field, and concluded this feature was impossible. It cost a wrong answer to the user. `HIDP_CAPS` is now marshalled properly in `PollingMeter`; use `PollingMeter.TryGetCaps`.
