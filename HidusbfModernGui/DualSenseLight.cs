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
