using System;
using System.Collections.Generic;
using System.Linq;

namespace HidusbfModernGui
{
    // A summary of measured inter-report gaps. The median is the headline figure, not
    // the mean: a spike against the real DualSense measured gaps from 0.007 ms (two
    // reports arriving back to back) to 2.627 ms (a hiccup) around a 0.998 ms median.
    // A mean would be dragged around by those outliers and the readout would flicker
    // noise at exactly the moment it should inspire confidence.
    public readonly record struct RateSample(double MedianGapMs, double MinGapMs, double MaxGapMs, int Count)
    {
        public double MedianHz => PollingCore.RateFromGapMs(MedianGapMs);
    }
    // USB bus speed as reported by the PnP device property. Values match the
    // USB_DEVICE_SPEED enumeration used by the Windows USB stack.
    public enum UsbSpeed
    {
        Unknown = 0,
        Low = 1,
        Full = 2,
        High = 3,
        Super = 4
    }

    // Patching level of the hidusbf driver. Per README.ENG.TXT (2025/11/05) this is
    // driven by the PatchUSBXHCI registry value, not by which .sys file is installed.
    public enum DriverMode
    {
        NoPatch = 0,
        Rate1k = 1,
        Rate2k4k = 2,
        Rate4k8k = 3
    }

    // What a device's status dot says. Resolved by PollingCore.DeviceStatusLevel
    // so the colour rule is covered by tests instead of eyeballed in the designer.
    public enum StatusLevel
    {
        Idle,   // grey  - present but not managed by us
        Ok,     // green - doing what was asked
        Warn,   // amber - attention needed
        Error   // red   - cannot act safely
    }

    // Pure mapping logic for polling rates, endpoint intervals and driver modes.
    // Kept free of registry/file/process access so it can be tested directly.
    public static class PollingCore
    {
        // Low and Full Speed interrupt endpoints express bInterval directly in
        // milliseconds, so the achievable rate is 1000/bInterval.
        // High and Super Speed use microframes: interval = 2^(bInterval-1) * 125us.
        public static bool UsesMicroframes(UsbSpeed speed) =>
            speed == UsbSpeed.High || speed == UsbSpeed.Super;

        // Returns the bInterval to write for a requested rate, or null when the rate
        // cannot be expressed at this speed. Never guesses: a null result means the
        // caller must surface an error rather than silently writing a wrong rate.
        public static int? TryMapRateToBInterval(int rate, UsbSpeed speed)
        {
            if (speed == UsbSpeed.Unknown) return null;

            if (UsesMicroframes(speed))
            {
                return rate switch
                {
                    31 => 9,
                    62 => 8,
                    125 => 7,
                    250 => 6,
                    500 => 5,
                    1000 => 4,
                    2000 => 3,
                    4000 => 2,
                    8000 => 1,
                    _ => null
                };
            }

            // Full / Low Speed. 2000Hz and above are not representable here: the
            // 2k-8k modes require a High Speed device on an xHCI controller.
            return rate switch
            {
                31 => 32,
                62 => 16,
                125 => 8,
                250 => 4,
                500 => 2,
                1000 => 1,
                _ => null
            };
        }

        // Inverse of TryMapRateToBInterval. Returns null for values this driver did
        // not write or that do not map to a rate we present in the UI.
        public static int? TryMapBIntervalToRate(int bInterval, UsbSpeed speed)
        {
            if (speed == UsbSpeed.Unknown) return null;

            if (UsesMicroframes(speed))
            {
                return bInterval switch
                {
                    9 => 31,
                    8 => 62,
                    7 => 125,
                    6 => 250,
                    5 => 500,
                    4 => 1000,
                    3 => 2000,
                    2 => 4000,
                    1 => 8000,
                    _ => null
                };
            }

            return bInterval switch
            {
                32 => 31,
                16 => 62,
                8 => 125,
                4 => 250,
                2 => 500,
                1 => 1000,
                _ => null
            };
        }

        // Registry values for a mode, per README.ENG.TXT (2025/11/05):
        //   PatchUSBPort accepts 0 and 1 only  (patches USBPORT.SYS - UHCI/OHCI/EHCI)
        //   PatchUSBXHCI accepts 0,1,2,3       (patches USBXHCI.SYS - xHCI)
        //     0 = disable, 1 = 1k, 2 = 2k-4k, 3 = 4k-8k
        // We always write both explicitly so behaviour never depends on the built-in
        // default of whichever .sys build happens to be installed.
        public static (int PatchUsbXhci, int PatchUsbPort) GetPatchParams(DriverMode mode) => mode switch
        {
            DriverMode.NoPatch => (0, 0),
            DriverMode.Rate1k => (1, 1),
            DriverMode.Rate2k4k => (2, 1),
            DriverMode.Rate4k8k => (3, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        // What the driver will actually do, given the installed binary and the
        // registry value. A non-patching build cannot patch no matter what the
        // registry says, so the binary wins.
        public static DriverMode? ResolveEffectiveMode(bool isPatchingBuild, int? patchUsbXhci)
        {
            if (!isPatchingBuild) return DriverMode.NoPatch;
            if (patchUsbXhci == null) return null; // binary default - not knowable from outside
            return patchUsbXhci switch
            {
                0 => DriverMode.NoPatch,
                1 => DriverMode.Rate1k,
                2 => DriverMode.Rate2k4k,
                3 => DriverMode.Rate4k8k,
                _ => null
            };
        }

        // Low/Full Speed interrupt endpoints express bInterval directly in whole
        // milliseconds and cannot go below 1ms (1000Hz), so 2k-8k has no native
        // representation there at all. The 2016 mechanism (README.2kHz-8kHz.ENG.TXT)
        // works around that by having the patched driver reinterpret the out-of-range
        // 31/62 slots (bInterval 32 and 16) as the high rates instead:
        //   1kHz driver       31 = 31Hz     62 = 62Hz
        //   2kHz-4kHz driver  31 = 2000Hz   62 = 4000Hz
        //   4kHz-8kHz driver  31 = 4000Hz   62 = 8000Hz
        //
        // High and Super Speed do not need this smuggling channel: their microframe
        // endpoints (interval = 2^(bInterval-1) * 125us, added to Setup.exe in 2022
        // per README.ENG.TXT "USB High Speed devices support") already reach
        // 2000/4000/8000Hz natively via their own bInterval values (3/2/1 - see
        // TryMapRateToBInterval). So on these speeds bInterval 9 and 8 - the 31/62
        // slots - are simply 31Hz and 62Hz, exactly as USB_ENDPOINT_DESCRIPTOR says;
        // nothing reinterprets them, in any driver mode.
        //
        // Unknown speed cannot be assumed to be Low/Full, so it is never promised a
        // reinterpretation it may not get: it stays literal, same as High/Super.
        public static int? ResolveHighRateSlot(int slot, DriverMode mode, UsbSpeed speed)
        {
            if (slot != 31 && slot != 62) return null;
            if (speed != UsbSpeed.Low && speed != UsbSpeed.Full) return slot;

            return mode switch
            {
                DriverMode.Rate2k4k => slot == 31 ? 2000 : 4000,
                DriverMode.Rate4k8k => slot == 31 ? 4000 : 8000,
                _ => slot // NoPatch / 1k: literal downclocking rates
            };
        }

        // Latency of a single polling interval, in milliseconds.
        public static double LatencyMs(int rate) => rate <= 0 ? 0 : 1000.0 / rate;

        public static string DescribeMode(DriverMode mode) => mode switch
        {
            DriverMode.NoPatch => "No Patch",
            DriverMode.Rate1k => "1kHz",
            DriverMode.Rate2k4k => "2kHz-4kHz",
            DriverMode.Rate4k8k => "4kHz-8kHz",
            _ => "Unknown"
        };

        // Rate implied by a single inter-report gap. A gap of zero or less is not a
        // 0 Hz rate, it is an absence of information - the caller must not render it.
        public static double RateFromGapMs(double gapMs) => gapMs <= 0 ? 0 : 1000.0 / gapMs;

        // Collapses measured gaps into the figures the UI shows. Returns null rather
        // than a zeroed sample when there is nothing to summarise: "no data" and
        // "0 Hz" are different claims, and this app exists because the old one
        // confused that kind of thing.
        public static RateSample? Summarise(IReadOnlyList<double> gapsMs)
        {
            if (gapsMs == null || gapsMs.Count == 0) return null;

            // Non-positive gaps are measurement artefacts, not readings. Two reports
            // timestamped identically say the clock did not tick, not that the device
            // polled infinitely fast.
            var sorted = gapsMs.Where(g => g > 0).OrderBy(g => g).ToArray();
            if (sorted.Length == 0) return null;

            double median = sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;

            return new RateSample(median, sorted[0], sorted[sorted.Length - 1], sorted.Length);
        }

        // Whether what the device is actually doing matches what was asked of it.
        // This is the whole product: the app could only ever report the rate it
        // requested, never the one that occurs.
        public static bool RateMatches(double measuredHz, int requestedHz, double tolerancePct = 10)
        {
            if (requestedHz <= 0 || measuredHz <= 0) return false;
            return Math.Abs(measuredHz - requestedHz) / requestedHz * 100.0 <= tolerancePct;
        }

        public static DriverMode? ParseMode(string text) => text?.Trim().ToLowerInvariant() switch
        {
            "no patch" or "nopatch" => DriverMode.NoPatch,
            "1khz" => DriverMode.Rate1k,
            "2khz-4khz" => DriverMode.Rate2k4k,
            "4khz-8khz" => DriverMode.Rate4k8k,
            _ => null
        };

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
    }
}
