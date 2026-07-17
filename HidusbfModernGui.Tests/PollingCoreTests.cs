using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    // Rates that cannot be expressed at a given speed must come back as null so the
    // caller can raise an error. The original code fell back to a default bInterval,
    // which turned "give me 2000Hz" into a silent 125Hz.
    public class RateMappingTests
    {
        [Theory]
        [InlineData(2000)]
        [InlineData(4000)]
        [InlineData(8000)]
        public void HighRates_AreNotRepresentable_AtFullSpeed(int rate)
        {
            Assert.Null(PollingCore.TryMapRateToBInterval(rate, UsbSpeed.Full));
        }

        [Theory]
        [InlineData(2000)]
        [InlineData(4000)]
        [InlineData(8000)]
        public void HighRates_AreNotRepresentable_AtLowSpeed(int rate)
        {
            Assert.Null(PollingCore.TryMapRateToBInterval(rate, UsbSpeed.Low));
        }

        [Fact]
        public void UnknownSpeed_NeverGuesses()
        {
            Assert.Null(PollingCore.TryMapRateToBInterval(1000, UsbSpeed.Unknown));
            Assert.Null(PollingCore.TryMapRateToBInterval(125, UsbSpeed.Unknown));
            Assert.Null(PollingCore.TryMapBIntervalToRate(4, UsbSpeed.Unknown));
        }

        [Fact]
        public void UnsupportedRate_IsRejected()
        {
            Assert.Null(PollingCore.TryMapRateToBInterval(333, UsbSpeed.High));
            Assert.Null(PollingCore.TryMapRateToBInterval(0, UsbSpeed.High));
            Assert.Null(PollingCore.TryMapRateToBInterval(16000, UsbSpeed.High));
        }

        // High Speed interrupt endpoints: interval = 2^(bInterval-1) microframes,
        // one microframe being 125us. bInterval=4 -> 8 microframes -> 1ms -> 1000Hz.
        [Theory]
        [InlineData(8000, 1)]
        [InlineData(4000, 2)]
        [InlineData(2000, 3)]
        [InlineData(1000, 4)]
        [InlineData(500, 5)]
        [InlineData(250, 6)]
        [InlineData(125, 7)]
        public void HighSpeed_UsesMicroframeExponent(int rate, int expectedBInterval)
        {
            Assert.Equal(expectedBInterval, PollingCore.TryMapRateToBInterval(rate, UsbSpeed.High));
        }

        // Full Speed expresses bInterval directly in milliseconds.
        [Theory]
        [InlineData(1000, 1)]
        [InlineData(500, 2)]
        [InlineData(250, 4)]
        [InlineData(125, 8)]
        public void FullSpeed_UsesMillisecondsDirectly(int rate, int expectedBInterval)
        {
            Assert.Equal(expectedBInterval, PollingCore.TryMapRateToBInterval(rate, UsbSpeed.Full));
        }

        // SuperSpeed follows the same microframe scheme as High Speed. The original
        // code treated anything that was not High Speed as Full Speed.
        [Fact]
        public void SuperSpeed_IsTreatedAsMicroframes_NotFullSpeed()
        {
            Assert.True(PollingCore.UsesMicroframes(UsbSpeed.Super));
            Assert.Equal(4, PollingCore.TryMapRateToBInterval(1000, UsbSpeed.Super));
        }

        [Theory]
        [InlineData(UsbSpeed.High, 1000)]
        [InlineData(UsbSpeed.High, 8000)]
        [InlineData(UsbSpeed.Full, 125)]
        [InlineData(UsbSpeed.Full, 1000)]
        public void MappingRoundTrips(UsbSpeed speed, int rate)
        {
            var bInterval = PollingCore.TryMapRateToBInterval(rate, speed);
            Assert.NotNull(bInterval);
            Assert.Equal(rate, PollingCore.TryMapBIntervalToRate(bInterval!.Value, speed));
        }
    }

    public class PatchParameterTests
    {
        // README.ENG.TXT (2025/11/05): "PatchUSBPort has 0 and 1 values".
        // The original code wrote the mode index (0-3) into it, so 4kHz-8kHz wrote 3.
        [Theory]
        [InlineData(DriverMode.NoPatch)]
        [InlineData(DriverMode.Rate1k)]
        [InlineData(DriverMode.Rate2k4k)]
        [InlineData(DriverMode.Rate4k8k)]
        public void PatchUsbPort_IsAlwaysZeroOrOne(DriverMode mode)
        {
            var (_, port) = PollingCore.GetPatchParams(mode);
            Assert.InRange(port, 0, 1);
        }

        // PatchUSBXHCI: 0 = disable, 1 = 1k, 2 = 2k-4k, 3 = 4k-8k.
        [Theory]
        [InlineData(DriverMode.NoPatch, 0)]
        [InlineData(DriverMode.Rate1k, 1)]
        [InlineData(DriverMode.Rate2k4k, 2)]
        [InlineData(DriverMode.Rate4k8k, 3)]
        public void PatchUsbXhci_MatchesDocumentedLevels(DriverMode mode, int expected)
        {
            var (xhci, _) = PollingCore.GetPatchParams(mode);
            Assert.Equal(expected, xhci);
        }

        [Fact]
        public void NoPatch_DisablesBothPatchTargets()
        {
            Assert.Equal((0, 0), PollingCore.GetPatchParams(DriverMode.NoPatch));
        }
    }

    public class EffectiveModeTests
    {
        // A NoPatch build cannot patch, whatever the registry claims. This is the
        // exact state found on the dev machine: NoPatch installed while the UI
        // reported "1kHz".
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void NonPatchingBuild_AlwaysResolvesToNoPatch(int registryValue)
        {
            Assert.Equal(DriverMode.NoPatch, PollingCore.ResolveEffectiveMode(false, registryValue));
        }

        [Fact]
        public void NonPatchingBuild_WithNoRegistryValue_IsStillNoPatch()
        {
            Assert.Equal(DriverMode.NoPatch, PollingCore.ResolveEffectiveMode(false, null));
        }

        // Without the registry value the driver falls back to a default baked into
        // the binary, which we cannot read. Report unknown rather than inventing 1kHz.
        [Fact]
        public void PatchingBuild_WithoutRegistryValue_IsUnknown()
        {
            Assert.Null(PollingCore.ResolveEffectiveMode(true, null));
        }

        [Theory]
        [InlineData(0, DriverMode.NoPatch)]
        [InlineData(1, DriverMode.Rate1k)]
        [InlineData(2, DriverMode.Rate2k4k)]
        [InlineData(3, DriverMode.Rate4k8k)]
        public void PatchingBuild_FollowsRegistryValue(int registryValue, DriverMode expected)
        {
            Assert.Equal(expected, PollingCore.ResolveEffectiveMode(true, registryValue));
        }

        [Fact]
        public void PatchingBuild_WithGarbageRegistryValue_IsUnknown()
        {
            Assert.Null(PollingCore.ResolveEffectiveMode(true, 99));
        }
    }

    public class HighRateSlotTests
    {
        // Full Speed bInterval is whole milliseconds and cannot go below 1ms, so
        // 2k-8k has no native representation there. The patched driver reinterprets
        // the out-of-range 31/62 slots (bInterval 32/16) as the high rates instead -
        // the 2016 mechanism from README.2kHz-8kHz.ENG.TXT.
        [Theory]
        [InlineData(31, DriverMode.Rate2k4k, 2000)]
        [InlineData(62, DriverMode.Rate2k4k, 4000)]
        [InlineData(31, DriverMode.Rate4k8k, 4000)]
        [InlineData(62, DriverMode.Rate4k8k, 8000)]
        public void PatchedModes_ReinterpretSlotsAsHighRates_OnFullSpeed(int slot, DriverMode mode, int expected)
        {
            Assert.Equal(expected, PollingCore.ResolveHighRateSlot(slot, mode, UsbSpeed.Full));
        }

        // Low Speed has the exact same millisecond-only limitation as Full Speed, so
        // it gets the same smuggling channel.
        [Theory]
        [InlineData(31, DriverMode.Rate2k4k, 2000)]
        [InlineData(62, DriverMode.Rate2k4k, 4000)]
        [InlineData(31, DriverMode.Rate4k8k, 4000)]
        [InlineData(62, DriverMode.Rate4k8k, 8000)]
        public void PatchedModes_ReinterpretSlotsAsHighRates_OnLowSpeed(int slot, DriverMode mode, int expected)
        {
            Assert.Equal(expected, PollingCore.ResolveHighRateSlot(slot, mode, UsbSpeed.Low));
        }

        // Under 1kHz / NoPatch there is nothing to smuggle: the same slots are
        // literal downclocking rates on the speeds that use the smuggling channel.
        [Theory]
        [InlineData(31, DriverMode.Rate1k, UsbSpeed.Full, 31)]
        [InlineData(62, DriverMode.Rate1k, UsbSpeed.Full, 62)]
        [InlineData(31, DriverMode.NoPatch, UsbSpeed.Full, 31)]
        [InlineData(62, DriverMode.NoPatch, UsbSpeed.Full, 62)]
        [InlineData(31, DriverMode.Rate1k, UsbSpeed.Low, 31)]
        [InlineData(62, DriverMode.NoPatch, UsbSpeed.Low, 62)]
        public void UnpatchedModes_KeepSlotsLiteral_OnLowFullSpeed(int slot, DriverMode mode, UsbSpeed speed, int expected)
        {
            Assert.Equal(expected, PollingCore.ResolveHighRateSlot(slot, mode, speed));
        }

        // High and Super Speed reach 2000/4000/8000Hz natively through their own
        // microframe bInterval values (3/2/1 - see TryMapRateToBInterval), a path
        // added to Setup.exe in 2022. They never need the 31/62 smuggling channel,
        // so on these speeds bInterval 9 and 8 stay exactly what USB says they are -
        // 31Hz and 62Hz - in every driver mode, including the patched ones.
        [Theory]
        [InlineData(UsbSpeed.High, DriverMode.NoPatch)]
        [InlineData(UsbSpeed.High, DriverMode.Rate1k)]
        [InlineData(UsbSpeed.High, DriverMode.Rate2k4k)]
        [InlineData(UsbSpeed.High, DriverMode.Rate4k8k)]
        [InlineData(UsbSpeed.Super, DriverMode.NoPatch)]
        [InlineData(UsbSpeed.Super, DriverMode.Rate1k)]
        [InlineData(UsbSpeed.Super, DriverMode.Rate2k4k)]
        [InlineData(UsbSpeed.Super, DriverMode.Rate4k8k)]
        public void MicroframeSpeeds_KeepSlotsLiteral_InEveryMode(UsbSpeed speed, DriverMode mode)
        {
            Assert.Equal(31, PollingCore.ResolveHighRateSlot(31, mode, speed));
            Assert.Equal(62, PollingCore.ResolveHighRateSlot(62, mode, speed));
        }

        // Unknown speed cannot be assumed to be Low/Full, so it must never be
        // promised a reinterpretation it may not actually get.
        [Theory]
        [InlineData(DriverMode.NoPatch)]
        [InlineData(DriverMode.Rate1k)]
        [InlineData(DriverMode.Rate2k4k)]
        [InlineData(DriverMode.Rate4k8k)]
        public void UnknownSpeed_KeepsSlotsLiteral(DriverMode mode)
        {
            Assert.Equal(31, PollingCore.ResolveHighRateSlot(31, mode, UsbSpeed.Unknown));
            Assert.Equal(62, PollingCore.ResolveHighRateSlot(62, mode, UsbSpeed.Unknown));
        }

        [Fact]
        public void NonSlotValues_AreRejected()
        {
            Assert.Null(PollingCore.ResolveHighRateSlot(125, DriverMode.Rate4k8k, UsbSpeed.Full));
        }
    }

    public class LatencyTests
    {
        // The XAML latency panel hardcoded "32.0 ms" for slot 31 even when the mode
        // relabelled it to 2000Hz. Latency must follow the resolved rate.
        [Theory]
        [InlineData(8000, 0.125)]
        [InlineData(4000, 0.25)]
        [InlineData(2000, 0.5)]
        [InlineData(1000, 1.0)]
        [InlineData(125, 8.0)]
        [InlineData(62, 16.129)]
        [InlineData(31, 32.258)]
        public void LatencyIsInverseOfRate(int rate, double expectedMs)
        {
            Assert.Equal(expectedMs, PollingCore.LatencyMs(rate), 3);
        }

        // Full Speed is where the 31/62 smuggling channel actually lives, so under
        // 2k-4k mode slot 31 is really 2000Hz - half a millisecond, not 32.
        [Fact]
        public void Slot31_Under2k4k_OnFullSpeed_ReportsHalfMillisecond_NotThirtyTwo()
        {
            var rate = PollingCore.ResolveHighRateSlot(31, DriverMode.Rate2k4k, UsbSpeed.Full);
            Assert.Equal(0.5, PollingCore.LatencyMs(rate!.Value), 3);
        }

        // Same slot, same mode, High Speed device: it already reaches 2000Hz
        // natively via its own bInterval=3, so this slot is untouched - still
        // literal 31Hz, so still ~32ms.
        [Fact]
        public void Slot31_Under2k4k_OnHighSpeed_StaysThirtyTwoMilliseconds()
        {
            var rate = PollingCore.ResolveHighRateSlot(31, DriverMode.Rate2k4k, UsbSpeed.High);
            Assert.Equal(32.258, PollingCore.LatencyMs(rate!.Value), 3);
        }
    }

    public class ModeParsingTests
    {
        [Theory]
        [InlineData(DriverMode.NoPatch)]
        [InlineData(DriverMode.Rate1k)]
        [InlineData(DriverMode.Rate2k4k)]
        [InlineData(DriverMode.Rate4k8k)]
        public void DescribeAndParseRoundTrip(DriverMode mode)
        {
            Assert.Equal(mode, PollingCore.ParseMode(PollingCore.DescribeMode(mode)));
        }

        [Fact]
        public void UnknownText_ParsesToNull()
        {
            Assert.Null(PollingCore.ParseMode("Active"));
            Assert.Null(PollingCore.ParseMode(""));
        }
    }

    public class RateMeasurementTests
    {
        // The reason the median is the headline figure. These are the real numbers a
        // spike measured off the DualSense: a 0.007 ms burst and a 2.627 ms hiccup
        // around a ~1 ms median. A mean would be dragged off the truth by them.
        [Fact]
        public void Median_IgnoresTheOutliersThatWouldWreckAMean()
        {
            var gaps = new List<double> { 0.007, 0.998, 0.999, 1.001, 2.627 };
            var s = PollingCore.Summarise(gaps);

            Assert.NotNull(s);
            Assert.Equal(0.999, s!.Value.MedianGapMs, 3);
            Assert.Equal(1001.0, s.Value.MedianHz, 0);

            // The mean of those gaps is ~1.126 ms -> ~888 Hz. The median says ~1001 Hz,
            // which is what the device is actually doing.
            Assert.True(Math.Abs(s.Value.MedianHz - 1001) < Math.Abs(1000.0 / gaps.Average() - 1001));
        }

        [Fact]
        public void Median_OfEvenCount_AveragesTheMiddlePair()
        {
            var s = PollingCore.Summarise(new List<double> { 1.0, 2.0, 3.0, 4.0 });
            Assert.Equal(2.5, s!.Value.MedianGapMs, 3);
        }

        // "No data" and "0 Hz" are different claims. Returning a zeroed sample would let
        // the UI render a rate for a device that reported nothing.
        [Fact]
        public void NoGaps_IsNull_NotZero()
        {
            Assert.Null(PollingCore.Summarise(new List<double>()));
            Assert.Null(PollingCore.Summarise(null!));
        }

        // Two reports timestamped identically mean the clock did not tick, not that the
        // device polled infinitely fast.
        [Fact]
        public void NonPositiveGaps_AreDiscardedAsArtefacts()
        {
            Assert.Null(PollingCore.Summarise(new List<double> { 0, -1, 0 }));

            var s = PollingCore.Summarise(new List<double> { 0, 1.0, 0 });
            Assert.NotNull(s);
            Assert.Equal(1, s!.Value.Count);
            Assert.Equal(1.0, s.Value.MedianGapMs, 3);
        }

        [Fact]
        public void SingleGap_IsEnoughToSummarise()
        {
            var s = PollingCore.Summarise(new List<double> { 0.125 });
            Assert.Equal(0.125, s!.Value.MedianGapMs, 3);
            Assert.Equal(8000, s.Value.MedianHz, 0);
            Assert.Equal(1, s.Value.Count);
        }

        [Fact]
        public void MinAndMax_AreTheExtremes_NotTheHeadline()
        {
            var s = PollingCore.Summarise(new List<double> { 0.007, 0.998, 2.627 });
            Assert.Equal(0.007, s!.Value.MinGapMs, 3);
            Assert.Equal(2.627, s.Value.MaxGapMs, 3);
            Assert.Equal(0.998, s.Value.MedianGapMs, 3);
        }

        [Theory]
        [InlineData(1.0, 1000)]
        [InlineData(0.125, 8000)]
        [InlineData(0.5, 2000)]
        [InlineData(8.0, 125)]
        public void RateFromGap_IsTheInverse(double gapMs, double expectedHz)
        {
            Assert.Equal(expectedHz, PollingCore.RateFromGapMs(gapMs), 0);
        }

        [Fact]
        public void RateFromGap_OfZeroOrLess_IsZero_NotInfinity()
        {
            Assert.Equal(0, PollingCore.RateFromGapMs(0));
            Assert.Equal(0, PollingCore.RateFromGapMs(-1));
        }

        // The spike's real result: 1001.2 Hz measured against 1000 Hz configured. That
        // must read as a match, or the app would cry wolf on a working device.
        [Fact]
        public void SpikeResult_CountsAsAMatch()
        {
            Assert.True(PollingCore.RateMatches(1001.2, 1000));
        }

        [Theory]
        [InlineData(1000, 1000, true)]
        [InlineData(1099, 1000, true)]   // just inside 10%
        [InlineData(901, 1000, true)]
        [InlineData(1101, 1000, false)]  // just outside
        [InlineData(899, 1000, false)]
        public void RateMatches_HonoursTheTolerance(double measured, int requested, bool expected)
        {
            Assert.Equal(expected, PollingCore.RateMatches(measured, requested));
        }

        // The case this whole feature exists for: 8000 Hz asked, 1000 Hz delivered.
        [Fact]
        public void RequestedEightK_DeliveredOneK_IsNotAMatch()
        {
            Assert.False(PollingCore.RateMatches(1000, 8000));
        }

        [Fact]
        public void RateMatches_WithNothingMeasured_IsNotAMatch()
        {
            Assert.False(PollingCore.RateMatches(0, 1000));
            Assert.False(PollingCore.RateMatches(1000, 0));
        }
    }

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
}
