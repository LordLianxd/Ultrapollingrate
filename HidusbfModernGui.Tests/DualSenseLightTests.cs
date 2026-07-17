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
