# DualSense Input Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Read the DualSense's input report in real time so the app can show live controller state and measure the true polling interval from the controller's own clock.

**Architecture:** A pure parser (`DualSenseInputParser`) turns a raw HID byte span into an immutable `DualSenseInput`, with zero hardware and zero I/O — so every offset, bitmask and packing rule is testable from a byte array. A separate reader (`DualSenseReader`) owns the blocking HID read loop on its own thread and raises parsed frames as events. The UI never touches bytes; the parser never touches a device. This split is what makes the protocol testable, and it is the foundation every later feature (curves, deadzones, remapping) builds on.

**Tech Stack:** C# / .NET 9 / WPF, xUnit, Win32 HID via `CreateFile` + `FileStream` (the same P/Invoke pattern already used in `DualSenseLight.cs`).

## Global Constraints

- **Code comments in English, user-facing strings in Spanish.** This matches the existing codebase exactly (see `DualSenseLight.cs` comments vs `OpResult.Fail("No se pudieron guardar los perfiles")`).
- **Comments explain WHY, not WHAT.** Every non-obvious offset or inverted flag gets a comment saying why it is that way. Match the density in `DualSenseLight.cs`.
- **Pure logic must be testable without hardware.** Any function that can be a static pure function over bytes must be one.
- **Never write to the controller in this plan.** This plan is read-only. Output reports are out of scope.
- **Target framework:** `net9.0-windows`. Nullable enabled. Implicit usings enabled.
- **Test framework:** xUnit, matching `HidusbfModernGui.Tests`.
- **No new NuGet dependencies.** Use `System.Buffers.Binary.BinaryPrimitives` from the BCL for endian reads.
- **Offsets are verified against the Linux kernel `hid-playstation.c` struct `dualsense_input_report`**, which is 63 bytes overlaid at `&data[1]` (USB) / `&data[2]` (BT). Do not "fix" an offset without a source.

---

## Reference: the USB input report map (report `0x01`, 64 bytes)

Index 0 is the report ID. **Bluetooth (report `0x31`, 78 bytes) shifts every field by exactly +1** because of one extra header byte at index 1.

| USB idx | BT idx | Field | Notes |
|---|---|---|---|
| 0 | 0 | Report ID | `0x01` USB / `0x31` BT |
| 1 | 2 | Left stick X | `0x00` left → `0xFF` right |
| 2 | 3 | Left stick Y | `0x00` **up** → `0xFF` down |
| 3 | 4 | Right stick X | |
| 4 | 5 | Right stick Y | |
| 5 | 6 | L2 analog | `0x00` released → `0xFF` full |
| 6 | 7 | R2 analog | |
| 7 | 8 | Sequence number | rolling counter |
| 8 | 9 | buttons[0] | hat (low nibble) + face buttons |
| 9 | 10 | buttons[1] | L1 R1 L2 R2 Create Options L3 R3 |
| 10 | 11 | buttons[2] | PS, touchpad click, mute (+ Edge paddles) |
| 11 | 12 | buttons[3] | unused on the base model |
| 16–21 | 17–22 | Gyro X, Y, Z | 3 × int16 little-endian, signed |
| 22–27 | 23–28 | Accel X, Y, Z | 3 × int16 little-endian, signed |
| 28–31 | 29–32 | Sensor timestamp | uint32 LE, **units of 0.33 µs** |
| 33–36 | 34–37 | Touch point 1 | 4 bytes |
| 37–40 | 38–41 | Touch point 2 | 4 bytes |
| 53 | 54 | status[0] | battery: low nibble level, high nibble charging |
| 54 | 55 | status[1] | headphone / mic detect / mic mute state |
| 74–77 | — | CRC32 | **BT only** |

**Sources:** Linux `hid-playstation.c` (primary), SDL `SDL_hidapi_ps5.c`, nondebug/dualsense HID descriptors. Note that **Ohjurot/DualSense-Windows swaps gyro and accel** — do not use it as a reference for motion offsets.

---

## File Structure

- **Create `HidusbfModernGui/DualSenseInput.cs`** — the immutable value types (`DualSenseInput`, `StickState`, `TouchPoint`, `Vector3s`) and the enums (`DsButtons`, `HatDirection`, `ChargingState`, `DsConnection`). Data only, no logic.
- **Create `HidusbfModernGui/DualSenseInputParser.cs`** — the pure parser. Static, no state, no I/O. This is where every offset lives.
- **Create `HidusbfModernGui/DualSenseReader.cs`** — owns the HID handle and the read thread. The only file here that touches hardware.
- **Create `HidusbfModernGui/PollingSample.cs`** — turns a stream of sensor timestamps into a measured interval and jitter. Pure.
- **Create `HidusbfModernGui.Tests/DualSenseInputParserTests.cs`** — parser tests.
- **Create `HidusbfModernGui.Tests/PollingSampleTests.cs`** — polling maths tests.
- **Modify `HidusbfModernGui/MainWindow.xaml` + `.xaml.cs`** — the live view.

`DualSenseLight.cs` is **not touched** by this plan. It has known bugs (Bluetooth layout, brightness flag, DualShock 4 false positive) but those are the output path and belong to a separate plan.

---

### Task 1: Input value types and the parser skeleton

Establishes the shape, the USB/BT offset base, and length validation. Sticks, triggers and the sequence number are the simplest fields, so they prove the offset base works before the bitfields land.

**Files:**
- Create: `HidusbfModernGui/DualSenseInput.cs`
- Create: `HidusbfModernGui/DualSenseInputParser.cs`
- Test: `HidusbfModernGui.Tests/DualSenseInputParserTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum DsConnection { Usb, Bluetooth }`
  - `readonly record struct StickState(byte X, byte Y)`
  - `readonly record struct DualSenseInput` with properties `DsConnection Connection`, `StickState LeftStick`, `StickState RightStick`, `byte L2`, `byte R2`, `byte SequenceNumber`. Later tasks add more properties to this same record.
  - `static class DualSenseInputParser` with `static bool TryParse(ReadOnlySpan<byte> report, out DualSenseInput input)`

- [ ] **Step 1: Write the failing test**

Create `HidusbfModernGui.Tests/DualSenseInputParserTests.cs`:

```csharp
using System;
using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    public class DualSenseInputParserTests
    {
        // A USB report is 64 bytes and starts with report ID 0x01. Anything else is
        // not a report we can read, and guessing would produce plausible garbage.
        private static byte[] UsbReport()
        {
            var r = new byte[64];
            r[0] = 0x01;
            return r;
        }

        // Bluetooth uses report 0x31, is 78 bytes, and carries one extra header byte
        // at index 1 - so every field below shifts by exactly +1.
        private static byte[] BtReport()
        {
            var r = new byte[78];
            r[0] = 0x31;
            return r;
        }

        [Fact]
        public void Usb_ReadsSticksAtTheDocumentedOffsets()
        {
            var r = UsbReport();
            r[1] = 0x10; r[2] = 0x20;   // left  X, Y
            r[3] = 0x30; r[4] = 0x40;   // right X, Y

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(DsConnection.Usb, input.Connection);
            Assert.Equal(new StickState(0x10, 0x20), input.LeftStick);
            Assert.Equal(new StickState(0x30, 0x40), input.RightStick);
        }

        [Fact]
        public void Usb_ReadsAnalogTriggers()
        {
            var r = UsbReport();
            r[5] = 0x7F;   // L2
            r[6] = 0xFF;   // R2 fully pressed

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(0x7F, input.L2);
            Assert.Equal(0xFF, input.R2);
        }

        // Bluetooth is the same report shifted one byte. Getting this wrong is the
        // single most common DualSense bug, so it is pinned from the first test.
        [Fact]
        public void Bluetooth_ShiftsEveryFieldByExactlyOne()
        {
            var r = BtReport();
            r[2] = 0x10; r[3] = 0x20;   // left  X, Y  (USB 1, 2)
            r[4] = 0x30; r[5] = 0x40;   // right X, Y  (USB 3, 4)
            r[6] = 0x7F;                // L2          (USB 5)
            r[7] = 0xFF;                // R2          (USB 6)

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(DsConnection.Bluetooth, input.Connection);
            Assert.Equal(new StickState(0x10, 0x20), input.LeftStick);
            Assert.Equal(new StickState(0x30, 0x40), input.RightStick);
            Assert.Equal(0x7F, input.L2);
            Assert.Equal(0xFF, input.R2);
        }

        [Fact]
        public void Usb_ReadsTheSequenceNumber()
        {
            var r = UsbReport();
            r[7] = 42;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(42, input.SequenceNumber);
        }

        // A freshly connected Bluetooth controller sends a 10-byte 0x01 report with
        // only sticks and buttons - no motion, no touchpad, no battery. Its layout is
        // NOT the USB report truncated, so parsing it as one would silently produce
        // wrong values. Refuse it; the reader unlocks the full report separately.
        [Fact]
        public void MinimalBluetoothReport_IsRefused_NotMisparsed()
        {
            var r = new byte[10];
            r[0] = 0x01;

            Assert.False(DualSenseInputParser.TryParse(r, out _));
        }

        [Theory]
        [InlineData(0x02)]   // an output report ID, never an input one
        [InlineData(0x05)]   // a feature report ID
        [InlineData(0x00)]
        public void UnknownReportId_IsRefused(byte id)
        {
            var r = UsbReport();
            r[0] = id;

            Assert.False(DualSenseInputParser.TryParse(r, out _));
        }

        [Fact]
        public void ShortReport_IsRefused_NotReadOutOfBounds()
        {
            Assert.False(DualSenseInputParser.TryParse(new byte[] { 0x01, 0x02 }, out _));
            Assert.False(DualSenseInputParser.TryParse(ReadOnlySpan<byte>.Empty, out _));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test HidusbfModernGui.Tests --filter DualSenseInputParserTests`
Expected: FAIL — compile error, `DualSenseInputParser` and `DsConnection` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `HidusbfModernGui/DualSenseInput.cs`:

```csharp
namespace HidusbfModernGui
{
    // How the controller is attached. It decides the offset base for every field:
    // Bluetooth carries one extra header byte, so BT index = USB index + 1.
    public enum DsConnection
    {
        Usb,
        Bluetooth
    }

    // A stick as the controller reports it: 8-bit per axis, centre around 0x80.
    // Deliberately raw. Curves, deadzones and float conversion belong to a later
    // layer - this type is what came off the wire, nothing else.
    //
    // Note Y is inverted relative to intuition: 0x00 is UP.
    public readonly record struct StickState(byte X, byte Y);

    // One frame of controller state. Immutable: a frame is a fact about a moment,
    // and later layers transform it into new frames rather than mutating it.
    public readonly record struct DualSenseInput
    {
        public DsConnection Connection { get; init; }
        public StickState LeftStick { get; init; }
        public StickState RightStick { get; init; }

        // Analog trigger travel, 0x00 released to 0xFF fully pressed. The digital
        // "is it pressed" bits live separately in the button bitfield.
        public byte L2 { get; init; }
        public byte R2 { get; init; }

        // Rolling counter. A gap between consecutive frames means dropped reports.
        public byte SequenceNumber { get; init; }
    }
}
```

Create `HidusbfModernGui/DualSenseInputParser.cs`:

```csharp
using System;

namespace HidusbfModernGui
{
    // Turns a raw HID input report into a DualSenseInput. Pure and static on
    // purpose: every offset and bitmask in the protocol is then testable from a
    // byte array, with no controller plugged in.
    //
    // Offsets verified against the Linux kernel's hid-playstation.c struct
    // dualsense_input_report, cross-checked with SDL's SDL_hidapi_ps5.c and the
    // controller's own HID report descriptors.
    public static class DualSenseInputParser
    {
        private const byte REPORT_ID_USB = 0x01;
        private const byte REPORT_ID_BT = 0x31;

        // The device advertises these exact lengths; DualSenseLight already relies on
        // the same distinction (InputReportByteLength 64 = USB, 78 = BT).
        private const int LENGTH_USB = 64;
        private const int LENGTH_BT = 78;

        // Offsets below are USB-relative, with index 0 being the report ID. For
        // Bluetooth add `base` (1) to every one of them.
        private const int OFF_LEFT_X = 1, OFF_LEFT_Y = 2;
        private const int OFF_RIGHT_X = 3, OFF_RIGHT_Y = 4;
        private const int OFF_L2 = 5, OFF_R2 = 6;
        private const int OFF_SEQ = 7;

        public static bool TryParse(ReadOnlySpan<byte> report, out DualSenseInput input)
        {
            input = default;

            if (!TryGetLayout(report, out DsConnection connection, out int b))
                return false;

            input = new DualSenseInput
            {
                Connection = connection,
                LeftStick = new StickState(report[b + OFF_LEFT_X], report[b + OFF_LEFT_Y]),
                RightStick = new StickState(report[b + OFF_RIGHT_X], report[b + OFF_RIGHT_Y]),
                L2 = report[b + OFF_L2],
                R2 = report[b + OFF_R2],
                SequenceNumber = report[b + OFF_SEQ]
            };
            return true;
        }

        // Decides connection kind and the offset base, and refuses anything we cannot
        // parse correctly.
        //
        // The 10-byte 0x01 report is the trap: a Bluetooth controller sends it until
        // something reads feature report 0x05. It is NOT the USB report truncated -
        // its triggers move to the end - so parsing it with USB offsets would yield
        // confident nonsense. Refusing is the only safe answer.
        private static bool TryGetLayout(ReadOnlySpan<byte> report, out DsConnection connection, out int offsetBase)
        {
            connection = default;
            offsetBase = 0;

            if (report.Length < 1) return false;

            switch (report[0])
            {
                case REPORT_ID_USB when report.Length >= LENGTH_USB:
                    connection = DsConnection.Usb;
                    offsetBase = 0;
                    return true;

                case REPORT_ID_BT when report.Length >= LENGTH_BT:
                    connection = DsConnection.Bluetooth;
                    offsetBase = 1;
                    return true;

                default:
                    return false;
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test HidusbfModernGui.Tests --filter DualSenseInputParserTests`
Expected: PASS — 7 tests.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/DualSenseInput.cs HidusbfModernGui/DualSenseInputParser.cs HidusbfModernGui.Tests/DualSenseInputParserTests.cs
git commit -m "feat: parse DualSense sticks and triggers from USB and Bluetooth reports"
```

---

### Task 2: Buttons and the D-pad hat

The bitfields. L1, R1, L3 and R3 all live in one byte; the hat shares a byte with the face buttons but is a value, not a mask.

**Files:**
- Modify: `HidusbfModernGui/DualSenseInput.cs`
- Modify: `HidusbfModernGui/DualSenseInputParser.cs`
- Test: `HidusbfModernGui.Tests/DualSenseInputParserTests.cs`

**Interfaces:**
- Consumes: `DualSenseInput`, `DualSenseInputParser.TryParse` from Task 1.
- Produces:
  - `[Flags] enum DsButtons : uint` with members `None, Square, Cross, Circle, Triangle, L1, R1, L2Digital, R2Digital, Create, Options, L3, R3, Ps, TouchpadClick, Mute`
  - `enum HatDirection : byte { Up, UpRight, Right, DownRight, Down, DownLeft, Left, UpLeft, Neutral }`
  - `DualSenseInput.Buttons` (type `DsButtons`) and `DualSenseInput.Hat` (type `HatDirection`)

- [ ] **Step 1: Write the failing test**

Add to `HidusbfModernGui.Tests/DualSenseInputParserTests.cs`, inside the class:

```csharp
        // Byte 8 low nibble is the hat, high nibble the face buttons.
        [Theory]
        [InlineData(0x10, DsButtons.Square)]
        [InlineData(0x20, DsButtons.Cross)]
        [InlineData(0x40, DsButtons.Circle)]
        [InlineData(0x80, DsButtons.Triangle)]
        public void Usb_ReadsFaceButtons(byte raw, DsButtons expected)
        {
            var r = UsbReport();
            r[8] = (byte)(raw | 0x08);   // 0x08 = hat neutral, so only the face bit is set

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(expected, input.Buttons & expected);
        }

        // Byte 9 carries the shoulders, the digital trigger bits and the stick clicks.
        // L3 and R3 are the stick clicks - bits 6 and 7, not 0 and 1.
        [Theory]
        [InlineData(0x01, DsButtons.L1)]
        [InlineData(0x02, DsButtons.R1)]
        [InlineData(0x04, DsButtons.L2Digital)]
        [InlineData(0x08, DsButtons.R2Digital)]
        [InlineData(0x10, DsButtons.Create)]
        [InlineData(0x20, DsButtons.Options)]
        [InlineData(0x40, DsButtons.L3)]
        [InlineData(0x80, DsButtons.R3)]
        public void Usb_ReadsShouldersAndStickClicks(byte raw, DsButtons expected)
        {
            var r = UsbReport();
            r[8] = 0x08;   // hat neutral
            r[9] = raw;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(expected, input.Buttons & expected);
        }

        [Theory]
        [InlineData(0x01, DsButtons.Ps)]
        [InlineData(0x02, DsButtons.TouchpadClick)]
        [InlineData(0x04, DsButtons.Mute)]
        public void Usb_ReadsSystemButtons(byte raw, DsButtons expected)
        {
            var r = UsbReport();
            r[8] = 0x08;
            r[10] = raw;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(expected, input.Buttons & expected);
        }

        // The hat is a VALUE 0-7 clockwise from up, not a bitfield. Treating it as
        // bits is the classic mistake and yields impossible diagonals.
        [Theory]
        [InlineData(0, HatDirection.Up)]
        [InlineData(1, HatDirection.UpRight)]
        [InlineData(2, HatDirection.Right)]
        [InlineData(3, HatDirection.DownRight)]
        [InlineData(4, HatDirection.Down)]
        [InlineData(5, HatDirection.DownLeft)]
        [InlineData(6, HatDirection.Left)]
        [InlineData(7, HatDirection.UpLeft)]
        [InlineData(8, HatDirection.Neutral)]
        public void Usb_ReadsHatAsADirection(byte raw, HatDirection expected)
        {
            var r = UsbReport();
            r[8] = raw;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(expected, input.Hat);
        }

        // Values 9-15 are possible on the wire. The kernel clamps them to neutral;
        // without this an enum cast produces a value no switch handles.
        [Theory]
        [InlineData(9)]
        [InlineData(12)]
        [InlineData(15)]
        public void Usb_OutOfRangeHat_ClampsToNeutral(byte raw)
        {
            var r = UsbReport();
            r[8] = raw;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(HatDirection.Neutral, input.Hat);
        }

        // The face buttons share byte 8 with the hat. Reading one must not corrupt
        // the other.
        [Fact]
        public void Usb_HatAndFaceButtonsCoexistInOneByte()
        {
            var r = UsbReport();
            r[8] = 0x02 | 0x20;   // hat Right + Cross

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(HatDirection.Right, input.Hat);
            Assert.True(input.Buttons.HasFlag(DsButtons.Cross));
            Assert.False(input.Buttons.HasFlag(DsButtons.Square));
        }

        [Fact]
        public void Bluetooth_ReadsButtonsAtTheShiftedOffsets()
        {
            var r = BtReport();
            r[9] = 0x08;    // hat neutral   (USB 8)
            r[10] = 0xC0;   // L3 | R3       (USB 9)

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.True(input.Buttons.HasFlag(DsButtons.L3));
            Assert.True(input.Buttons.HasFlag(DsButtons.R3));
        }

        [Fact]
        public void NoButtonsPressed_IsNone()
        {
            var r = UsbReport();
            r[8] = 0x08;   // hat neutral, nothing else

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(DsButtons.None, input.Buttons);
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test HidusbfModernGui.Tests --filter DualSenseInputParserTests`
Expected: FAIL — compile error, `DsButtons` and `HatDirection` do not exist.

- [ ] **Step 3: Write minimal implementation**

Add to `HidusbfModernGui/DualSenseInput.cs`:

```csharp
    // Every button on the base DualSense. The controller packs these into three
    // bytes as individual bits; this flattens them into one flags enum so callers
    // never think about which byte a button came from.
    //
    // L2Digital/R2Digital are the "past the threshold" bits. The analog travel is a
    // separate byte - the same trigger reports twice, in two different ways.
    [Flags]
    public enum DsButtons : uint
    {
        None = 0,

        // buttons[0] high nibble - the low nibble is the hat, not a mask.
        Square = 1 << 0,
        Cross = 1 << 1,
        Circle = 1 << 2,
        Triangle = 1 << 3,

        // buttons[1]
        L1 = 1 << 4,
        R1 = 1 << 5,
        L2Digital = 1 << 6,
        R2Digital = 1 << 7,
        Create = 1 << 8,
        Options = 1 << 9,
        L3 = 1 << 10,   // left stick click
        R3 = 1 << 11,   // right stick click

        // buttons[2]
        Ps = 1 << 12,
        TouchpadClick = 1 << 13,   // the physical click, not a touch
        Mute = 1 << 14
    }

    // The D-pad reports a direction, not a set of bits. Clockwise from up, with a
    // dedicated neutral value.
    public enum HatDirection : byte
    {
        Up = 0,
        UpRight = 1,
        Right = 2,
        DownRight = 3,
        Down = 4,
        DownLeft = 5,
        Left = 6,
        UpLeft = 7,
        Neutral = 8
    }
```

Add the two properties to `DualSenseInput`:

```csharp
        public DsButtons Buttons { get; init; }
        public HatDirection Hat { get; init; }
```

In `HidusbfModernGui/DualSenseInputParser.cs`, add the offsets and masks:

```csharp
        private const int OFF_BUTTONS0 = 8, OFF_BUTTONS1 = 9, OFF_BUTTONS2 = 10;

        // buttons[0]: low nibble is the hat, high nibble the face buttons.
        private const byte B0_HAT_MASK = 0x0F;
        private const byte B0_SQUARE = 0x10, B0_CROSS = 0x20, B0_CIRCLE = 0x40, B0_TRIANGLE = 0x80;

        private const byte B1_L1 = 0x01, B1_R1 = 0x02, B1_L2 = 0x04, B1_R2 = 0x08;
        private const byte B1_CREATE = 0x10, B1_OPTIONS = 0x20, B1_L3 = 0x40, B1_R3 = 0x80;

        private const byte B2_PS = 0x01, B2_TOUCHPAD = 0x02, B2_MUTE = 0x04;

        // Anything above 8 is out of range. The kernel clamps it; so do we, because
        // an unclamped cast yields an enum value no switch arm handles.
        private const byte HAT_NEUTRAL = 8;
```

Add the two decoding helpers:

```csharp
        private static DsButtons ReadButtons(ReadOnlySpan<byte> r, int b)
        {
            byte b0 = r[b + OFF_BUTTONS0];
            byte b1 = r[b + OFF_BUTTONS1];
            byte b2 = r[b + OFF_BUTTONS2];

            DsButtons buttons = DsButtons.None;

            if ((b0 & B0_SQUARE) != 0) buttons |= DsButtons.Square;
            if ((b0 & B0_CROSS) != 0) buttons |= DsButtons.Cross;
            if ((b0 & B0_CIRCLE) != 0) buttons |= DsButtons.Circle;
            if ((b0 & B0_TRIANGLE) != 0) buttons |= DsButtons.Triangle;

            if ((b1 & B1_L1) != 0) buttons |= DsButtons.L1;
            if ((b1 & B1_R1) != 0) buttons |= DsButtons.R1;
            if ((b1 & B1_L2) != 0) buttons |= DsButtons.L2Digital;
            if ((b1 & B1_R2) != 0) buttons |= DsButtons.R2Digital;
            if ((b1 & B1_CREATE) != 0) buttons |= DsButtons.Create;
            if ((b1 & B1_OPTIONS) != 0) buttons |= DsButtons.Options;
            if ((b1 & B1_L3) != 0) buttons |= DsButtons.L3;
            if ((b1 & B1_R3) != 0) buttons |= DsButtons.R3;

            if ((b2 & B2_PS) != 0) buttons |= DsButtons.Ps;
            if ((b2 & B2_TOUCHPAD) != 0) buttons |= DsButtons.TouchpadClick;
            if ((b2 & B2_MUTE) != 0) buttons |= DsButtons.Mute;

            return buttons;
        }

        private static HatDirection ReadHat(ReadOnlySpan<byte> r, int b)
        {
            byte hat = (byte)(r[b + OFF_BUTTONS0] & B0_HAT_MASK);
            return (HatDirection)(hat >= HAT_NEUTRAL ? HAT_NEUTRAL : hat);
        }
```

Wire them into the object initialiser in `TryParse`:

```csharp
                SequenceNumber = report[b + OFF_SEQ],
                Buttons = ReadButtons(report, b),
                Hat = ReadHat(report, b)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test HidusbfModernGui.Tests --filter DualSenseInputParserTests`
Expected: PASS — all tests, including the 7 from Task 1.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/DualSenseInput.cs HidusbfModernGui/DualSenseInputParser.cs HidusbfModernGui.Tests/DualSenseInputParserTests.cs
git commit -m "feat: decode DualSense buttons and the D-pad hat"
```

---

### Task 3: Touchpad

Two simultaneous touch points, 4 bytes each, with 12-bit coordinates packed across a shared middle byte and an **inverted** active bit.

**Files:**
- Modify: `HidusbfModernGui/DualSenseInput.cs`
- Modify: `HidusbfModernGui/DualSenseInputParser.cs`
- Test: `HidusbfModernGui.Tests/DualSenseInputParserTests.cs`

**Interfaces:**
- Consumes: `DualSenseInput`, `DualSenseInputParser` from Tasks 1–2.
- Produces:
  - `readonly record struct TouchPoint(bool Active, byte Id, ushort X, ushort Y)`
  - `DualSenseInput.Touch1` and `DualSenseInput.Touch2`, both `TouchPoint`
  - `const int DualSenseInputParser.TouchpadWidth = 1920`, `TouchpadHeight = 1080`

- [ ] **Step 1: Write the failing test**

Add to `DualSenseInputParserTests.cs`:

```csharp
        // The active bit is INVERTED: bit 7 set means NO finger. This is the single
        // most common touchpad bug, so it is pinned in both directions.
        [Fact]
        public void Touch_HighBitSet_MeansNoFinger()
        {
            var r = UsbReport();
            r[33] = 0x80;   // inactive

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.False(input.Touch1.Active);
        }

        [Fact]
        public void Touch_HighBitClear_MeansFingerDown()
        {
            var r = UsbReport();
            r[33] = 0x00;   // active, id 0

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.True(input.Touch1.Active);
        }

        [Fact]
        public void Touch_ReadsTheRollingId()
        {
            var r = UsbReport();
            r[33] = 0x2A;   // active, id 42

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.True(input.Touch1.Active);
            Assert.Equal(42, input.Touch1.Id);
        }

        // X is 12 bits: the low 8 in byte 34, the high 4 in the LOW nibble of 35.
        // Y is 12 bits: the low 4 in the HIGH nibble of 35, the high 8 in byte 36.
        // The two coordinates share byte 35, one nibble each.
        [Fact]
        public void Touch_UnpacksTwelveBitCoordinates()
        {
            var r = UsbReport();
            r[33] = 0x00;   // active
            r[34] = 0x34;   // X low  8 bits
            r[35] = 0x72;   // X high nibble = 2, Y low nibble = 7
            r[36] = 0x1A;   // Y high 8 bits

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(0x234, input.Touch1.X);   // (0x2 << 8) | 0x34
            Assert.Equal(0x1A7, input.Touch1.Y);   // (0x1A << 4) | 0x7
        }

        [Fact]
        public void Touch_MaximumCoordinates_DoNotOverflowIntoEachOther()
        {
            var r = UsbReport();
            r[33] = 0x00;
            r[34] = 0xFF; r[35] = 0xFF; r[36] = 0xFF;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(0xFFF, input.Touch1.X);
            Assert.Equal(0xFFF, input.Touch1.Y);
        }

        // The second point is the same 4-byte shape at bytes 37-40. Two fingers must
        // decode independently.
        [Fact]
        public void Touch_ReadsBothPointsIndependently()
        {
            var r = UsbReport();
            r[33] = 0x01; r[34] = 0x10; r[35] = 0x00; r[36] = 0x00;   // point 1 active
            r[37] = 0x80;                                              // point 2 inactive

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.True(input.Touch1.Active);
            Assert.Equal(0x10, input.Touch1.X);
            Assert.False(input.Touch2.Active);
        }

        [Fact]
        public void Bluetooth_ReadsTouchpadAtTheShiftedOffsets()
        {
            var r = BtReport();
            r[34] = 0x00;   // active   (USB 33)
            r[35] = 0x55;   // X low    (USB 34)

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.True(input.Touch1.Active);
            Assert.Equal(0x55, input.Touch1.X);
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test HidusbfModernGui.Tests --filter DualSenseInputParserTests`
Expected: FAIL — compile error, `TouchPoint` does not exist.

- [ ] **Step 3: Write minimal implementation**

Add to `HidusbfModernGui/DualSenseInput.cs`:

```csharp
    // One finger on the touchpad. Id is a rolling counter the controller bumps on
    // each new contact, which is how you tell "moved" from "lifted and re-placed".
    public readonly record struct TouchPoint(bool Active, byte Id, ushort X, ushort Y);
```

Add to `DualSenseInput`:

```csharp
        public TouchPoint Touch1 { get; init; }
        public TouchPoint Touch2 { get; init; }
```

Add to `DualSenseInputParser.cs`:

```csharp
        // The touchpad reports in its own pixel space, independent of the screen.
        public const int TouchpadWidth = 1920;
        public const int TouchpadHeight = 1080;

        private const int OFF_TOUCH1 = 33, OFF_TOUCH2 = 37;

        // Bit 7 of the first byte is set when there is NO contact. Inverted from what
        // the name "active" suggests, so it is read through a helper rather than
        // inline at both call sites.
        private const byte TOUCH_INACTIVE = 0x80;
        private const byte TOUCH_ID_MASK = 0x7F;

        // Reads one 4-byte touch point.
        //
        // The packing: X takes byte 1 plus the LOW nibble of byte 2; Y takes the HIGH
        // nibble of byte 2 plus byte 3. Both are 12 bits and they share byte 2.
        private static TouchPoint ReadTouch(ReadOnlySpan<byte> r, int at)
        {
            byte b0 = r[at], b1 = r[at + 1], b2 = r[at + 2], b3 = r[at + 3];

            return new TouchPoint(
                Active: (b0 & TOUCH_INACTIVE) == 0,
                Id: (byte)(b0 & TOUCH_ID_MASK),
                X: (ushort)(((b2 & 0x0F) << 8) | b1),
                Y: (ushort)((b3 << 4) | ((b2 & 0xF0) >> 4)));
        }
```

Wire into `TryParse`:

```csharp
                Hat = ReadHat(report, b),
                Touch1 = ReadTouch(report, b + OFF_TOUCH1),
                Touch2 = ReadTouch(report, b + OFF_TOUCH2)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test HidusbfModernGui.Tests --filter DualSenseInputParserTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/DualSenseInput.cs HidusbfModernGui/DualSenseInputParser.cs HidusbfModernGui.Tests/DualSenseInputParserTests.cs
git commit -m "feat: decode the DualSense touchpad's two contact points"
```

---

### Task 4: Motion, timestamp and battery

Gyro and accel as signed 16-bit triples, the sensor clock, and the battery nibbles.

**Files:**
- Modify: `HidusbfModernGui/DualSenseInput.cs`
- Modify: `HidusbfModernGui/DualSenseInputParser.cs`
- Test: `HidusbfModernGui.Tests/DualSenseInputParserTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–3.
- Produces:
  - `readonly record struct Vector3s(short X, short Y, short Z)`
  - `enum ChargingState { Discharging, Charging, Full, TemperatureError, ChargingError, Unknown }`
  - `DualSenseInput.Gyro`, `.Accel` (both `Vector3s`), `.SensorTimestamp` (`uint`), `.BatteryPercent` (`int`), `.Charging` (`ChargingState`)

- [ ] **Step 1: Write the failing test**

Add to `DualSenseInputParserTests.cs`:

```csharp
        // Gyro is at 16-21, accel at 22-27. Ohjurot/DualSense-Windows has these two
        // SWAPPED; the Linux kernel and dualsense-controller-python agree on this
        // order. If motion ever looks wrong, suspect the source, not this test.
        [Fact]
        public void Usb_ReadsGyroAndAccelAtTheKernelOffsets()
        {
            var r = UsbReport();
            // gyro X = 0x0102, Y = 0x0304, Z = 0x0506  (little-endian)
            r[16] = 0x02; r[17] = 0x01;
            r[18] = 0x04; r[19] = 0x03;
            r[20] = 0x06; r[21] = 0x05;
            // accel X = 0x0708, Y = 0x090A, Z = 0x0B0C
            r[22] = 0x08; r[23] = 0x07;
            r[24] = 0x0A; r[25] = 0x09;
            r[26] = 0x0C; r[27] = 0x0B;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(new Vector3s(0x0102, 0x0304, 0x0506), input.Gyro);
            Assert.Equal(new Vector3s(0x0708, 0x090A, 0x0B0C), input.Accel);
        }

        // Motion axes are SIGNED. Reading them unsigned makes half of every rotation
        // jump to ~65000 instead of going negative.
        [Fact]
        public void Usb_MotionAxesAreSigned()
        {
            var r = UsbReport();
            r[16] = 0xFF; r[17] = 0xFF;   // -1
            r[18] = 0x00; r[19] = 0x80;   // short.MinValue

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(-1, input.Gyro.X);
            Assert.Equal(short.MinValue, input.Gyro.Y);
        }

        // The controller's own clock, uint32 little-endian. This is the field that
        // makes real polling measurement possible, so it is read exactly.
        [Fact]
        public void Usb_ReadsTheSensorTimestamp()
        {
            var r = UsbReport();
            r[28] = 0x78; r[29] = 0x56; r[30] = 0x34; r[31] = 0x12;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(0x12345678u, input.SensorTimestamp);
        }

        // Byte 53: low nibble is the level (0-10, each unit ~10%), high nibble the
        // charging state. The kernel adds 5 to land in the middle of each 10% band.
        [Theory]
        [InlineData(0x00, 5, ChargingState.Discharging)]    // level 0 -> 0-9%,  midpoint 5
        [InlineData(0x05, 55, ChargingState.Discharging)]   // level 5 -> 50-59%
        [InlineData(0x0A, 100, ChargingState.Discharging)]  // level 10 -> capped at 100
        [InlineData(0x15, 55, ChargingState.Charging)]      // high nibble 1 = charging
        public void Usb_DecodesBatteryLevelAndState(byte raw, int expectedPercent, ChargingState expectedState)
        {
            var r = UsbReport();
            r[53] = raw;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(expectedPercent, input.BatteryPercent);
            Assert.Equal(expectedState, input.Charging);
        }

        // High nibble 2 means "full" and the level nibble is not meaningful.
        [Fact]
        public void Usb_BatteryFull_Is100Regardless()
        {
            var r = UsbReport();
            r[53] = 0x20;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(100, input.BatteryPercent);
            Assert.Equal(ChargingState.Full, input.Charging);
        }

        // Error states report no meaningful level. Showing a percentage would be a
        // lie, so they report 0 and a state the UI can render honestly.
        [Theory]
        [InlineData(0xA0, ChargingState.TemperatureError)]
        [InlineData(0xB0, ChargingState.TemperatureError)]
        [InlineData(0xF0, ChargingState.ChargingError)]
        [InlineData(0x70, ChargingState.Unknown)]
        public void Usb_BatteryErrorStates_ReportZeroNotAGuess(byte raw, ChargingState expectedState)
        {
            var r = UsbReport();
            r[53] = raw;

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(0, input.BatteryPercent);
            Assert.Equal(expectedState, input.Charging);
        }

        [Fact]
        public void Bluetooth_ReadsMotionAndBatteryAtTheShiftedOffsets()
        {
            var r = BtReport();
            r[17] = 0x02; r[18] = 0x01;   // gyro X    (USB 16-17)
            r[54] = 0x05;                 // battery   (USB 53)

            Assert.True(DualSenseInputParser.TryParse(r, out var input));
            Assert.Equal(0x0102, input.Gyro.X);
            Assert.Equal(55, input.BatteryPercent);
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test HidusbfModernGui.Tests --filter DualSenseInputParserTests`
Expected: FAIL — compile error, `Vector3s` and `ChargingState` do not exist.

- [ ] **Step 3: Write minimal implementation**

Add to `HidusbfModernGui/DualSenseInput.cs`:

```csharp
    // A raw motion triple as the controller reports it: signed 16-bit per axis.
    // Not converted to physical units - that needs the calibration feature report,
    // which is a separate concern.
    public readonly record struct Vector3s(short X, short Y, short Z);

    // The high nibble of the battery status byte. The error states carry no usable
    // level, so they are distinct values rather than a bool.
    public enum ChargingState
    {
        Discharging,
        Charging,
        Full,
        TemperatureError,
        ChargingError,
        Unknown
    }
```

Add to `DualSenseInput`:

```csharp
        public Vector3s Gyro { get; init; }
        public Vector3s Accel { get; init; }

        // The controller's own clock in units of 0.33 microseconds. This is the only
        // timebase not polluted by Windows scheduling, which is what makes it worth
        // reading: it measures the real polling interval rather than our own latency.
        public uint SensorTimestamp { get; init; }

        // 0-100, or 0 when the controller reports an error state rather than a level.
        public int BatteryPercent { get; init; }
        public ChargingState Charging { get; init; }
```

Add to `DualSenseInputParser.cs` — the `using` first:

```csharp
using System.Buffers.Binary;
```

Then the offsets and readers:

```csharp
        private const int OFF_GYRO = 16, OFF_ACCEL = 22, OFF_TIMESTAMP = 28;
        private const int OFF_STATUS0 = 53;

        private const byte STATUS0_LEVEL_MASK = 0x0F;
        private const byte STATUS0_CHARGE_MASK = 0xF0;

        private static Vector3s ReadVector3s(ReadOnlySpan<byte> r, int at) =>
            new Vector3s(
                BinaryPrimitives.ReadInt16LittleEndian(r.Slice(at, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(r.Slice(at + 2, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(r.Slice(at + 4, 2)));

        // Battery lives in one byte, split into two nibbles that mean different
        // things. The level nibble is 0-10 where each unit is a 10% band, so the
        // kernel's *10+5 lands in the middle of the band rather than at its edge.
        private static (int Percent, ChargingState State) ReadBattery(ReadOnlySpan<byte> r, int b)
        {
            byte status = r[b + OFF_STATUS0];
            int level = status & STATUS0_LEVEL_MASK;
            int charge = (status & STATUS0_CHARGE_MASK) >> 4;

            return charge switch
            {
                0x0 => (Math.Min(level * 10 + 5, 100), ChargingState.Discharging),
                0x1 => (Math.Min(level * 10 + 5, 100), ChargingState.Charging),
                0x2 => (100, ChargingState.Full),
                0xA or 0xB => (0, ChargingState.TemperatureError),
                0xF => (0, ChargingState.ChargingError),
                _ => (0, ChargingState.Unknown)
            };
        }
```

Wire into `TryParse` — read the battery before the initialiser, since it returns a tuple:

```csharp
            var (batteryPercent, chargingState) = ReadBattery(report, b);

            input = new DualSenseInput
            {
                // ... existing properties ...
                Touch2 = ReadTouch(report, b + OFF_TOUCH2),
                Gyro = ReadVector3s(report, b + OFF_GYRO),
                Accel = ReadVector3s(report, b + OFF_ACCEL),
                SensorTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(b + OFF_TIMESTAMP, 4)),
                BatteryPercent = batteryPercent,
                Charging = chargingState
            };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test HidusbfModernGui.Tests --filter DualSenseInputParserTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/DualSenseInput.cs HidusbfModernGui/DualSenseInputParser.cs HidusbfModernGui.Tests/DualSenseInputParserTests.cs
git commit -m "feat: decode DualSense motion, sensor clock and battery"
```

---

### Task 5: Real polling measurement from the sensor clock

The differentiating feature. Windows arrival times are polluted by scheduling; the controller's own clock is not. This turns a stream of timestamps into a measured interval and jitter.

**Files:**
- Create: `HidusbfModernGui/PollingSample.cs`
- Test: `HidusbfModernGui.Tests/PollingSampleTests.cs`

**Interfaces:**
- Consumes: `DualSenseInput.SensorTimestamp` (uint) from Task 4.
- Produces:
  - `sealed class SensorClockMeter` with `void Add(uint timestamp)`, `PollingStats? Current { get; }`, `void Reset()`
  - `readonly record struct PollingStats(double IntervalMicroseconds, double Hz, double JitterMicroseconds, int SampleCount)`

- [ ] **Step 1: Write the failing test**

Create `HidusbfModernGui.Tests/PollingSampleTests.cs`:

```csharp
using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    public class PollingSampleTests
    {
        // The controller's clock ticks every 0.33 microseconds, so a 1 ms interval is
        // 1000 / 0.33 = ~3030 ticks. These tests work in ticks and assert microseconds.
        private const double TicksPerMicrosecond = 1.0 / 0.33;

        private static uint TicksFor(double microseconds) => (uint)(microseconds * TicksPerMicrosecond);

        // One sample cannot produce an interval - there is nothing to subtract from.
        // Reporting a number here would be inventing one.
        [Fact]
        public void OneSample_ProducesNoStats()
        {
            var meter = new SensorClockMeter();
            meter.Add(1000);

            Assert.Null(meter.Current);
        }

        [Fact]
        public void EmptyMeter_ProducesNoStats()
        {
            Assert.Null(new SensorClockMeter().Current);
        }

        // A steady 1000 Hz stream: 1000 microseconds between reports.
        [Fact]
        public void SteadyOneMillisecondInterval_Reads1000Hz()
        {
            var meter = new SensorClockMeter();
            uint t = 0;
            for (int i = 0; i < 10; i++)
            {
                meter.Add(t);
                t += TicksFor(1000);
            }

            var stats = meter.Current;
            Assert.NotNull(stats);
            Assert.Equal(1000, stats!.Value.IntervalMicroseconds, precision: 0);
            Assert.Equal(1000, stats.Value.Hz, precision: 0);
        }

        // 8000 Hz is the polling rate hidusbf exists to reach: 125 microseconds.
        [Fact]
        public void SteadyEightKilohertz_Reads8000Hz()
        {
            var meter = new SensorClockMeter();
            uint t = 0;
            for (int i = 0; i < 20; i++)
            {
                meter.Add(t);
                t += TicksFor(125);
            }

            var stats = meter.Current;
            Assert.NotNull(stats);

            // A range, not an equality: the tick unit is 0.33 us, so 125 us does not
            // land on a whole number of ticks and the reconstructed rate lands near
            // 8000 rather than exactly on it. Demanding exactness here would be
            // testing the rounding, not the measurement.
            Assert.InRange(stats!.Value.Hz, 7900, 8100);
        }

        // A perfectly steady stream has no jitter. If this reports non-zero, the
        // maths is wrong.
        [Fact]
        public void SteadyStream_HasNoJitter()
        {
            var meter = new SensorClockMeter();
            uint t = 0;
            for (int i = 0; i < 10; i++)
            {
                meter.Add(t);
                t += TicksFor(1000);
            }

            Assert.Equal(0, meter.Current!.Value.JitterMicroseconds, precision: 1);
        }

        // Jitter is what the user actually cares about: a high average rate with wild
        // spread feels worse than a steady lower one.
        [Fact]
        public void UnsteadyStream_ReportsJitter()
        {
            var meter = new SensorClockMeter();

            // Seed first, then step: N gaps must produce exactly N intervals. Adding
            // inside the loop before stepping would produce N-1 and skew the mean.
            uint t = 0;
            meter.Add(t);
            foreach (var gap in new double[] { 500, 1500, 500, 1500 })
            {
                t += TicksFor(gap);
                meter.Add(t);
            }

            var stats = meter.Current;
            Assert.NotNull(stats);

            // Mean of 500/1500/500/1500 is 1000 - the same as the steady stream above.
            // That is the point: the average alone cannot tell the two apart, which is
            // why jitter is reported separately.
            Assert.Equal(1000, stats!.Value.IntervalMicroseconds, precision: 0);
            Assert.True(stats.Value.JitterMicroseconds > 400,
                $"expected visible jitter, got {stats.Value.JitterMicroseconds}");
        }

        // The clock is uint32 in 0.33 us units, so it wraps roughly every 23 minutes.
        // Unhandled, the wrap produces one enormous negative interval that poisons the
        // average for the whole window.
        [Fact]
        public void ClockWrap_DoesNotPoisonTheAverage()
        {
            var meter = new SensorClockMeter();
            uint step = TicksFor(1000);
            uint t = uint.MaxValue - (step * 3);

            for (int i = 0; i < 10; i++)
            {
                meter.Add(t);
                t += step;   // wraps mid-loop
            }

            var stats = meter.Current;
            Assert.NotNull(stats);
            Assert.Equal(1000, stats!.Value.IntervalMicroseconds, precision: 0);
        }

        [Fact]
        public void Reset_ClearsEverything()
        {
            var meter = new SensorClockMeter();
            meter.Add(0);
            meter.Add(TicksFor(1000));
            Assert.NotNull(meter.Current);

            meter.Reset();
            Assert.Null(meter.Current);
        }

        // A duplicate timestamp means the controller re-sent without its clock
        // advancing. A zero interval would read as infinite Hz, so it is dropped.
        [Fact]
        public void DuplicateTimestamp_IsIgnored_NotInfiniteHz()
        {
            var meter = new SensorClockMeter();
            meter.Add(1000);
            meter.Add(1000);

            Assert.Null(meter.Current);
        }

        // The window is bounded so the display tracks the present rather than
        // averaging over the whole session.
        [Fact]
        public void OldSamplesFallOutOfTheWindow()
        {
            var meter = new SensorClockMeter(windowSize: 4);
            uint t = 0;

            for (int i = 0; i < 3; i++) { meter.Add(t); t += TicksFor(2000); }   // slow
            for (int i = 0; i < 8; i++) { meter.Add(t); t += TicksFor(1000); }   // then fast

            // The slow samples must have aged out entirely.
            Assert.Equal(1000, meter.Current!.Value.IntervalMicroseconds, precision: 0);
            Assert.Equal(4, meter.Current!.Value.SampleCount);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test HidusbfModernGui.Tests --filter PollingSampleTests`
Expected: FAIL — compile error, `SensorClockMeter` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `HidusbfModernGui/PollingSample.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace HidusbfModernGui
{
    // A measured polling interval, from the controller's clock rather than ours.
    public readonly record struct PollingStats(
        double IntervalMicroseconds,
        double Hz,
        double JitterMicroseconds,
        int SampleCount);

    // Measures the real polling interval from the DualSense's own sensor clock.
    //
    // Why this exists: measuring arrival times on the PC measures Windows' scheduler
    // as much as the USB stack, so a 1 kHz controller can look like 900 Hz with huge
    // spread purely because our thread was descheduled. The controller stamps every
    // report from its own clock before sending, so differencing those stamps gives
    // the interval the device actually achieved. That is the number that proves
    // whether an overclock worked.
    //
    // Not thread-safe. The reader thread owns it; the UI reads a snapshot.
    public sealed class SensorClockMeter
    {
        // The DualSense's sensor clock ticks every 0.33 microseconds.
        private const double MicrosecondsPerTick = 0.33;

        private readonly int _windowSize;
        private readonly Queue<double> _intervals;
        private uint? _previous;

        public SensorClockMeter(int windowSize = 256)
        {
            if (windowSize < 2)
                throw new ArgumentOutOfRangeException(nameof(windowSize),
                    "Se necesitan al menos 2 muestras para medir un intervalo.");

            _windowSize = windowSize;
            _intervals = new Queue<double>(windowSize);
        }

        public void Add(uint timestamp)
        {
            if (_previous is uint prev)
            {
                // Unsigned subtraction wraps correctly on its own: when the clock rolls
                // over, (small - large) in uint arithmetic yields the true small delta.
                // Doing this in signed arithmetic is what produces the huge negative
                // spike that poisons the average.
                uint deltaTicks = timestamp - prev;

                // A zero delta means the clock did not advance between reports. It is
                // not a 0 us interval - it is no information - and dividing by it would
                // report infinite Hz.
                if (deltaTicks != 0)
                {
                    _intervals.Enqueue(deltaTicks * MicrosecondsPerTick);
                    while (_intervals.Count > _windowSize) _intervals.Dequeue();
                }
                else
                {
                    // Do not advance _previous either: the next real report should be
                    // differenced against the last timestamp that meant something.
                    return;
                }
            }

            _previous = timestamp;
        }

        // Null until there is at least one interval. An interval needs two samples,
        // and reporting a rate from one would be inventing data.
        public PollingStats? Current
        {
            get
            {
                if (_intervals.Count < 1) return null;

                double mean = _intervals.Average();
                if (mean <= 0) return null;

                // Mean absolute deviation rather than standard deviation: it is what a
                // user reads as "how far off is it typically", and it is not dominated
                // by one outlier the way a squared measure is.
                double jitter = _intervals.Count > 1
                    ? _intervals.Average(i => Math.Abs(i - mean))
                    : 0;

                return new PollingStats(
                    IntervalMicroseconds: mean,
                    Hz: 1_000_000.0 / mean,
                    JitterMicroseconds: jitter,
                    SampleCount: _intervals.Count);
            }
        }

        public void Reset()
        {
            _intervals.Clear();
            _previous = null;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test HidusbfModernGui.Tests --filter PollingSampleTests`
Expected: PASS — 10 tests.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/PollingSample.cs HidusbfModernGui.Tests/PollingSampleTests.cs
git commit -m "feat: measure real polling interval from the DualSense sensor clock"
```

---

### Task 6: The reader thread

The only file in this plan that touches hardware. Owns the HID handle, the read loop and the Bluetooth unlock.

**Files:**
- Create: `HidusbfModernGui/DualSenseReader.cs`

**Interfaces:**
- Consumes: `DualSenseInputParser.TryParse`, `DualSenseInput`, `SensorClockMeter` from Tasks 1–5; `HidDeviceLocator.FindHidPaths(string)` and `PollingMeter.TryGetCaps(string)` from the existing codebase; `OpResult` from the existing codebase.
- Produces:
  - `sealed class DualSenseReader : IDisposable` with `OpResult Start(string usbInstanceId)`, `void Stop()`, `event EventHandler<DualSenseInput>? FrameReceived`, `event EventHandler<string>? Faulted`, `PollingStats? Polling { get; }`

No test task: this class is I/O and threading around already-tested pure code. Its logic is delegation. Verification is Task 7 — seeing live values on screen.

- [ ] **Step 1: Write the implementation**

Create `HidusbfModernGui/DualSenseReader.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HidusbfModernGui
{
    // Reads input reports from a DualSense on a dedicated thread and raises each
    // parsed frame.
    //
    // A dedicated thread rather than async: the HID read is a blocking call that
    // returns roughly every 1-8 ms forever, which is a thread's whole life. Putting
    // that on the thread pool would occupy a pool thread permanently and starve
    // everything else.
    //
    // Read-only by design. This class never writes an output report - lights are
    // DualSenseLight's job, and mixing the two would put a write on the read thread.
    public sealed class DualSenseReader : IDisposable
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;

        // Reading feature report 0x05 (calibration) is what makes a Bluetooth
        // controller switch from its minimal 10-byte report to the full 0x31 one.
        // We do not use the calibration data - the read itself is the trigger.
        private const byte FEATURE_CALIBRATION = 0x05;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, int length);

        private Thread? _thread;
        private CancellationTokenSource? _cts;
        private SafeFileHandle? _handle;
        private readonly SensorClockMeter _meter = new();

        // Raised on the reader thread, not the UI thread. Subscribers that touch WPF
        // must marshal - MainWindow does this via Dispatcher.
        public event EventHandler<DualSenseInput>? FrameReceived;

        // Raised once when the loop gives up. The message is user-facing Spanish.
        public event EventHandler<string>? Faulted;

        public PollingStats? Polling => _meter.Current;

        public bool IsRunning => _thread?.IsAlive == true;

        public OpResult Start(string usbInstanceId)
        {
            if (IsRunning) return OpResult.Fail("Ya se está leyendo este mando.");

            var paths = HidDeviceLocator.FindHidPaths(usbInstanceId);
            if (paths.Count == 0)
                return OpResult.Fail("Este dispositivo no expone interfaz HID.");

            foreach (var path in paths)
            {
                var caps = PollingMeter.TryGetCaps(path);
                if (caps == null) continue;

                // 64 = USB, 78 = Bluetooth. Anything else on a Sony VID is not a
                // DualSense input interface - a DualShock 4 or an audio endpoint.
                int len = caps.Value.InputReportByteLength;
                if (len != 64 && len != 78) continue;

                // Shared access: DSX, Steam or a game may hold the controller too, and
                // taking it exclusively would break them. Reading does not need
                // exclusivity - only remapping would, and that is not this plan.
                var handle = CreateFile(path, GENERIC_READ,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"DualSenseReader: CreateFile failed on {path}: {err}");
                    handle.Dispose();
                    continue;
                }

                UnlockBluetoothReports(handle);

                _handle = handle;
                _meter.Reset();
                _cts = new CancellationTokenSource();
                _thread = new Thread(() => ReadLoop(handle, len, _cts.Token))
                {
                    IsBackground = true,   // never block app shutdown on a blocked read
                    Name = "ReadControllerInput",
                    Priority = ThreadPriority.AboveNormal   // an 8 kHz stream is latency-sensitive
                };
                _thread.Start();
                return OpResult.Ok();
            }

            return OpResult.Fail("No se encontró ninguna interfaz de entrada del mando.");
        }

        // A Bluetooth DualSense sends only a minimal 10-byte report until something
        // reads feature report 0x05. Harmless over USB, where the full report is the
        // default - so it is unconditional rather than branching on connection type
        // we have not established yet.
        private static void UnlockBluetoothReports(SafeFileHandle handle)
        {
            try
            {
                // The feature report buffer must be FeatureReportByteLength; 64 is what
                // the DualSense advertises. Byte 0 is the report ID.
                var buffer = new byte[64];
                buffer[0] = FEATURE_CALIBRATION;
                HidD_GetFeature(handle, buffer, buffer.Length);
            }
            catch (Exception ex)
            {
                // Failing here only means Bluetooth stays in minimal mode, which the
                // parser refuses cleanly. Not worth aborting the whole read.
                Debug.WriteLine($"DualSenseReader: calibration read failed: {ex.Message}");
            }
        }

        private void ReadLoop(SafeFileHandle handle, int reportLength, CancellationToken token)
        {
            var buffer = new byte[reportLength];

            try
            {
                using var stream = new FileStream(handle, FileAccess.Read, reportLength, false);

                while (!token.IsCancellationRequested)
                {
                    int read = stream.Read(buffer, 0, reportLength);
                    if (read <= 0) continue;

                    if (!DualSenseInputParser.TryParse(buffer.AsSpan(0, read), out var input))
                        continue;   // minimal BT report or a report we do not know

                    _meter.Add(input.SensorTimestamp);
                    FrameReceived?.Invoke(this, input);
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // An unplugged controller lands here. It is a normal event, not a
                // crash, so it becomes a message rather than an unhandled exception on
                // a background thread - which would take the process down.
                Debug.WriteLine($"DualSenseReader.ReadLoop: {ex.Message}");
                Faulted?.Invoke(this, "Se perdió la conexión con el mando.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DualSenseReader.ReadLoop (cancelled): {ex.Message}");
            }
        }

        public void Stop()
        {
            _cts?.Cancel();

            // Closing the handle is what actually unblocks the pending read; cancelling
            // the token alone would leave the thread parked in stream.Read until the
            // controller sent one more report.
            _handle?.Dispose();
            _handle = null;

            // Bounded join: if the thread is wedged, shutting down matters more than
            // a tidy exit. It is a background thread, so it cannot hold the app open.
            _thread?.Join(TimeSpan.FromMilliseconds(500));
            _thread = null;

            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose() => Stop();
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build HidusbfModernGui`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the full test suite to check nothing regressed**

Run: `dotnet test HidusbfModernGui.Tests`
Expected: PASS — every test from Tasks 1–5 plus the pre-existing suite.

- [ ] **Step 4: Commit**

```bash
git add HidusbfModernGui/DualSenseReader.cs
git commit -m "feat: read DualSense input reports on a dedicated thread"
```

---

### Task 7: Live controller view

Proves the pipeline against real hardware. Until a human sees a stick move on screen, none of the above is verified.

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `DualSenseReader`, `DualSenseInput`, `PollingStats`, `DsButtons`, `HatDirection`.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Add the view**

In `MainWindow.xaml`, on the existing controller/light page, add a diagnostics panel. Match the surrounding page's existing styles and theme resources rather than introducing new ones:

```xml
<StackPanel x:Name="LiveInputPanel" Margin="0,16,0,0">
    <TextBlock Text="Estado en vivo" FontWeight="SemiBold" Margin="0,0,0,8"/>

    <TextBlock x:Name="LiveSticks" Text="Sticks: —" Margin="0,2"/>
    <TextBlock x:Name="LiveTriggers" Text="Gatillos: —" Margin="0,2"/>
    <TextBlock x:Name="LiveButtons" Text="Botones: —" Margin="0,2"/>
    <TextBlock x:Name="LiveTouch" Text="Touchpad: —" Margin="0,2"/>
    <TextBlock x:Name="LiveBattery" Text="Batería: —" Margin="0,2"/>

    <TextBlock Text="Polling real (reloj del mando)" FontWeight="SemiBold" Margin="0,12,0,4"/>
    <TextBlock x:Name="LivePolling" Text="—" Margin="0,2"/>
</StackPanel>
```

- [ ] **Step 2: Wire the reader**

In `MainWindow.xaml.cs`, add `using System.Windows.Threading;` for `DispatcherTimer`, then the field and the handlers:

```csharp
        private readonly DualSenseReader _reader = new();

        // The last frame, published by the reader thread and sampled by the UI timer.
        //
        // Boxed into an object deliberately. DualSenseInput is a ~15-field record
        // struct, and assigning one is NOT atomic - the reader could be halfway
        // through writing it while the UI reads, yielding a frame with some fields
        // from one report and some from the next. A reference write IS atomic, so
        // boxing makes the swap all-or-nothing. `volatile` then stops the UI thread
        // caching a stale reference forever.
        //
        // The alternative is a lock, but the UI never needs THE newest frame - only a
        // recent one - so paying for a lock 8000 times a second buys nothing.
        private volatile object? _latestFrame;

        private DispatcherTimer? _liveTimer;

        private void StartLiveInput(string usbInstanceId)
        {
            _reader.FrameReceived += (_, frame) => _latestFrame = frame;   // boxes
            _reader.Faulted += (_, message) => Dispatcher.Invoke(() =>
            {
                LivePolling.Text = message;
                StopLiveInput();
            });

            var result = _reader.Start(usbInstanceId);
            if (!result.Success)
            {
                // OpResult.Error is string?; the project has nullable enabled, so it
                // needs a fallback rather than assigning null to Text.
                LivePolling.Text = result.Error ?? "No se pudo leer el mando.";
                return;
            }

            // 30 Hz, not per frame. The controller sends up to 8000 reports a second
            // and WPF cannot redraw that fast - marshalling each one to the dispatcher
            // would flood the message queue and freeze the window. The reader keeps
            // the newest frame; the UI samples it.
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _liveTimer.Tick += (_, _) => RenderLatestFrame();
            _liveTimer.Start();
        }

        private void RenderLatestFrame()
        {
            if (_latestFrame is not DualSenseInput f) return;

            LiveSticks.Text = $"Sticks:  izq {f.LeftStick.X},{f.LeftStick.Y}   der {f.RightStick.X},{f.RightStick.Y}";
            LiveTriggers.Text = $"Gatillos:  L2 {f.L2}   R2 {f.R2}";

            string buttons = f.Buttons == DsButtons.None ? "ninguno" : f.Buttons.ToString();
            string hat = f.Hat == HatDirection.Neutral ? "" : $"   cruceta {f.Hat}";
            LiveButtons.Text = $"Botones: {buttons}{hat}";

            LiveTouch.Text = f.Touch1.Active
                ? $"Touchpad: {f.Touch1.X},{f.Touch1.Y}" + (f.Touch2.Active ? $"   +  {f.Touch2.X},{f.Touch2.Y}" : "")
                : "Touchpad: sin contacto";

            string charging = f.Charging switch
            {
                ChargingState.Charging => " (cargando)",
                ChargingState.Full => " (completa)",
                ChargingState.TemperatureError => " (error de temperatura)",
                ChargingState.ChargingError => " (error de carga)",
                ChargingState.Unknown => " (desconocido)",
                _ => ""
            };
            LiveBattery.Text = $"Batería: {f.BatteryPercent}%{charging}   ·   {f.Connection}";

            LivePolling.Text = _reader.Polling is PollingStats p
                ? $"{p.Hz:N0} Hz   ·   {p.IntervalMicroseconds:N0} µs   ·   jitter ±{p.JitterMicroseconds:N0} µs   ·   {p.SampleCount} muestras"
                : "midiendo…";
        }

        private void StopLiveInput()
        {
            _liveTimer?.Stop();
            _liveTimer = null;
            _reader.Stop();
            _latestFrame = null;
        }
```

Call `StopLiveInput()` from the existing window-closing path, beside the other cleanup.

- [ ] **Step 3: Build**

Run: `dotnet build HidusbfModernGui`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Verify against real hardware**

This step is the point of the whole plan. Run the app with a DualSense connected over USB and confirm, by eye:

1. **Sticks** — move each stick. Both numbers change, 0–255. Push **up**: Y goes toward **0**, not 255. If it goes to 255, the axis is inverted and something is wrong.
2. **Triggers** — pull L2 and R2 slowly. Values sweep 0→255 smoothly.
3. **Buttons** — press every one and confirm the name matches the physical button. **Check L1, R1, L3 and R3 specifically**: L3/R3 are the stick *clicks*. If pressing L3 shows `L1`, the bit order is wrong.
4. **Cruceta** — press each of the 8 directions, then release. Released must read as no cruceta text at all, not `Up`.
5. **Touchpad** — one finger, then two. Coordinates within 1920×1080. Lifting must read "sin contacto" immediately; if it stays stuck showing a position, the inverted active bit is being read the wrong way round.
6. **Battery** — plausible percentage; plug/unplug the cable and watch it flip to "(cargando)".
7. **Polling** — the headline. Note the Hz. Then change the hidusbf rate, restart the device, and confirm the number **follows**. This is the number that proves the overclock is real.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git commit -m "feat: live DualSense input view with real polling measurement"
```

---

## Out of scope, deliberately

These are real and known, and each needs its own plan:

- **`DualSenseLight.cs` bugs.** Three, all confirmed: (1) it builds a USB-layout report over Bluetooth, where the controller needs report ID `0x31`, every offset shifted +2, a CRC32 at bytes 74–77, and a 547-byte write — so lights silently do nothing over Bluetooth; (2) `led_brightness` at byte 43 is gated by `valid_flag2` at byte 39 bit `0x01`, which is never set, so brightness is likely ignored; (3) `IsPlayStation` matches on `VID_054C` alone, so a DualShock 4 (`PID_05C4`) passes and receives DualSense-layout reports. Output path, separate plan.
- **Adaptive triggers.** Including trigger stops, which need no interception and no drivers.
- **Curves, deadzones, remapping.** These need a virtual controller (ViGEmBus) and device hiding (HidHide), which is a product decision, not just code.
- **Motion calibration.** Gyro/accel are raw here. Physical units need feature report `0x05`'s calibration data.
- **DualShock 4 input.** Different report layout entirely.
