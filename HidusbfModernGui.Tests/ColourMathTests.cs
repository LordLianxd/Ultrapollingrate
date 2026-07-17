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

    public class OklabTests
    {
        // Grey has no chroma at any lightness, in any perceptual space.
        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(128, 128, 128)]
        [InlineData(255, 255, 255)]
        public void RgbToOklab_Grey_HasNoChroma(byte r, byte g, byte b)
        {
            var (_, a, bb) = ColourMath.RgbToOklab(r, g, b);
            Assert.Equal(0, Math.Sqrt(a * a + bb * bb), 2);
        }

        // Lightness must rise monotonically with grey level, or "constant lightness" means
        // nothing.
        [Fact]
        public void RgbToOklab_LightnessRisesWithGreyLevel()
        {
            double last = -1;
            for (int v = 0; v <= 255; v += 15)
            {
                var (L, _, _) = ColourMath.RgbToOklab((byte)v, (byte)v, (byte)v);
                Assert.True(L > last, $"L went backwards at {v}");
                last = L;
            }
        }

        // White is the top of the scale by definition.
        [Fact]
        public void RgbToOklab_WhiteIsLightnessOne()
        {
            var (L, _, _) = ColourMath.RgbToOklab(255, 255, 255);
            Assert.Equal(1.0, L, 2);
        }

        [Fact]
        public void RgbToOklab_BlackIsLightnessZero()
        {
            var (L, _, _) = ColourMath.RgbToOklab(0, 0, 0);
            Assert.Equal(0.0, L, 2);
        }

        // The round trip is what the sweep depends on: ask for a lightness and hue, get a
        // colour that reads back as that lightness and hue.
        [Theory]
        [InlineData(0.65, 0.10, 0)]
        [InlineData(0.65, 0.10, 120)]
        [InlineData(0.65, 0.10, 240)]
        [InlineData(0.70, 0.08, 45)]
        public void OklchToRgb_RoundTripsThroughRgbToOklab(double L, double C, double h)
        {
            var (r, g, b) = ColourMath.OklchToRgb(L, C, h);
            var (L2, a2, b2) = ColourMath.RgbToOklab(r, g, b);

            Assert.Equal(L, L2, 2);
            Assert.Equal(C, Math.Sqrt(a2 * a2 + b2 * b2), 2);

            double h2 = Math.Atan2(b2, a2) * 180 / Math.PI;
            if (h2 < 0) h2 += 360;

            // Hue is circular: 0 and 360 are the same colour, so compare the distance
            // around the wheel rather than the raw numbers. Rounding to 8 bits lands hue 0
            // at 359.87, and a naive Assert.Equal(0, 360) fails on a colour that is exactly
            // right - which is the same fact the Hue360_EqualsHue0 test above exists for.
            double diff = Math.Abs(h - h2);
            if (diff > 180) diff = 360 - diff;
            Assert.True(diff < 1, $"hue {h} came back as {h2:F2} (off by {diff:F2} degrees)");
        }

        [Fact]
        public void OklchToRgb_ZeroChroma_IsGrey()
        {
            var (r, g, b) = ColourMath.OklchToRgb(0.5, 0, 137);
            Assert.Equal(r, g);
            Assert.Equal(g, b);
        }

        // 360 and 0 are the same hue. A sweep that wrapped to a different colour would
        // jump once per cycle - exactly the defect this work removes.
        [Fact]
        public void OklchToRgb_Hue360_EqualsHue0()
        {
            Assert.Equal(ColourMath.OklchToRgb(0.65, 0.1, 0), ColourMath.OklchToRgb(0.65, 0.1, 360));
        }

        [Fact]
        public void OklchInGamut_LowChroma_IsAlwaysReachable()
        {
            for (int h = 0; h < 360; h += 10)
                Assert.True(ColourMath.OklchInGamut(0.65, 0.05, h), $"hue {h} should fit");
        }

        // Nothing is this saturated in sRGB. If this passes, the gamut test is not testing.
        [Fact]
        public void OklchInGamut_AbsurdChroma_IsNever()
        {
            for (int h = 0; h < 360; h += 10)
                Assert.False(ColourMath.OklchInGamut(0.65, 0.9, h), $"hue {h} cannot fit");
        }

        // MaxChroma's whole job: the answer must fit, and one step above it must not.
        [Theory]
        [InlineData(0.65, 0)]
        [InlineData(0.65, 120)]
        [InlineData(0.65, 240)]
        [InlineData(0.70, 300)]
        public void MaxChroma_IsTheBoundary(double L, double h)
        {
            double c = ColourMath.MaxChroma(L, h);
            Assert.True(ColourMath.OklchInGamut(L, c, h), "the answer is out of gamut");
            Assert.False(ColourMath.OklchInGamut(L, c + 0.01, h), "there was room for more");
        }

        // Measured on this machine: at L=0.65 chroma ranges ~0.111 to ~0.309 across the
        // wheel. The point is that it VARIES - a fixed chroma must fit the worst hue.
        [Fact]
        public void MaxChroma_VariesByHue()
        {
            double min = 9, max = 0;
            for (int h = 0; h < 360; h += 10)
            {
                double c = ColourMath.MaxChroma(0.65, h);
                min = Math.Min(min, c);
                max = Math.Max(max, c);
            }
            Assert.True(max / min > 2, $"expected real variation, got {min:F3}..{max:F3}");
        }

        // Colour needs room to exist: near black and near white there is far less of it
        // than in the mid-tones. Asserted as a comparison rather than an absolute, because
        // near L=0 every channel is within the gamut epsilon and MaxChroma returns a
        // meaninglessly large number - the answer there is noise, not a boundary.
        [Fact]
        public void MaxChroma_IsLargestInTheMidTones()
        {
            double mid = ColourMath.MaxChroma(0.65, 30);
            Assert.True(ColourMath.MaxChroma(0.98, 30) < mid, "near-white should hold less chroma");
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

    public class RainbowStyleTests
    {
        // The ruler. Measuring uniformity with a weighted RGB distance is circular - it is
        // not perceptual, which is the entire property under test.
        private static double DeltaE((byte R, byte G, byte B) c1, (byte R, byte G, byte B) c2)
        {
            var (L1, a1, b1) = ColourMath.RgbToOklab(c1.R, c1.G, c1.B);
            var (L2, a2, b2) = ColourMath.RgbToOklab(c2.R, c2.G, c2.B);
            return Math.Sqrt(Math.Pow(L1 - L2, 2) + Math.Pow(a1 - a2, 2) + Math.Pow(b1 - b2, 2));
        }

        // Largest perceptual step / smallest, over a full cycle. 1.0 would be perfect.
        private static double StepRatio(RainbowStyle style)
        {
            double worst = 0, best = double.MaxValue;
            for (double t = 0; t < 6; t += 6.0 / 180)
            {
                double d = DeltaE(RainbowEffect.ColourAt(t, 6, style),
                                  RainbowEffect.ColourAt(t + 6.0 / 180, 6, style));
                worst = Math.Max(worst, d);
                best = Math.Min(best, d);
            }
            return worst / Math.Max(best, 1e-9);
        }

        private static double LightnessSwing(RainbowStyle style)
        {
            double lo = 9, hi = 0;
            for (double t = 0; t < 6; t += 0.05)
            {
                var c = RainbowEffect.ColourAt(t, 6, style);
                var (L, _, _) = ColourMath.RgbToOklab(c.R, c.G, c.B);
                lo = Math.Min(lo, L);
                hi = Math.Max(hi, L);
            }
            return hi / Math.Max(lo, 1e-9);
        }

        // This is the whole point of the change. The user sees the 37x as the cycle jumping
        // between six colours instead of flowing. Measured before this work: 36.9x.
        [Fact]
        public void Vivid_LurchesBadly_WhichIsWhyTheOthersExist()
        {
            Assert.True(StepRatio(RainbowStyle.Vivid) > 20,
                "if this dropped, HsvToRgb changed and the premise needs re-measuring");
        }

        [Fact]
        public void Smooth_IsAtLeastFiveTimesMoreEvenThanVivid()
        {
            double vivid = StepRatio(RainbowStyle.Vivid);
            double smooth = StepRatio(RainbowStyle.Smooth);
            Assert.True(smooth * 5 < vivid, $"vivid {vivid:F1}x vs smooth {smooth:F1}x");
        }

        [Fact]
        public void Balanced_SitsBetweenTheOtherTwo()
        {
            double smooth = StepRatio(RainbowStyle.Smooth);
            double balanced = StepRatio(RainbowStyle.Balanced);
            double vivid = StepRatio(RainbowStyle.Vivid);
            Assert.True(smooth < balanced, $"smooth {smooth:F1} should beat balanced {balanced:F1}");
            Assert.True(balanced < vivid, $"balanced {balanced:F1} should beat vivid {vivid:F1}");
        }

        // A vivid blue is dark, so a vivid sweep necessarily pulses. The OKLCH styles hold
        // lightness flat by construction.
        [Theory]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void OklchStyles_DoNotPulse(RainbowStyle style)
        {
            Assert.True(LightnessSwing(style) < 1.1, $"{style} swings {LightnessSwing(style):F2}x");
        }

        [Fact]
        public void Vivid_Pulses()
        {
            Assert.True(LightnessSwing(RainbowStyle.Vivid) > 1.5);
        }

        // The honest cost, asserted so nobody "fixes" the dullness by cranking chroma until
        // the gamut clamps and the jumps return.
        [Fact]
        public void Smooth_IsLessSaturatedThanVivid_AndThatIsPhysics()
        {
            Assert.True(MeanChroma(RainbowStyle.Smooth) < MeanChroma(RainbowStyle.Vivid));
        }

        [Fact]
        public void Balanced_RecoversSaturationOverSmooth()
        {
            Assert.True(MeanChroma(RainbowStyle.Balanced) > MeanChroma(RainbowStyle.Smooth));
        }

        private static double MeanChroma(RainbowStyle style)
        {
            double sum = 0;
            int n = 0;
            for (double t = 0; t < 6; t += 0.05)
            {
                var c = RainbowEffect.ColourAt(t, 6, style);
                var (_, a, b) = ColourMath.RgbToOklab(c.R, c.G, c.B);
                sum += Math.Sqrt(a * a + b * b);
                n++;
            }
            return sum / n;
        }

        // Every style must still be a cycle, or the loop visibly snaps once per revolution.
        [Theory]
        [InlineData(RainbowStyle.Vivid)]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void EveryStyle_IsPeriodic(RainbowStyle style)
        {
            Assert.Equal(RainbowEffect.ColourAt(1.5, 6, style), RainbowEffect.ColourAt(7.5, 6, style));
        }

        // Runs inside a 33 ms timer tick. An exception there takes the app down.
        [Theory]
        [InlineData(RainbowStyle.Vivid)]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void EveryStyle_SurvivesANonsensePeriod(RainbowStyle style)
        {
            var c = RainbowEffect.ColourAt(2, 0, style);
            Assert.Equal(RainbowEffect.ColourAt(0, 1, style), c);
        }

        // The old two-argument overload is Vivid. Callers that never opt in must not change
        // behaviour.
        [Fact]
        public void TheOldOverload_IsStillVivid()
        {
            Assert.Equal(RainbowEffect.ColourAt(1.7, 6), RainbowEffect.ColourAt(1.7, 6, RainbowStyle.Vivid));
        }

        // SmoothChroma is a ceiling, not a preference: it is the most chroma every hue can
        // hold at Smooth's lightness. Raise it and some hues fall outside sRGB, get
        // clamped, and the clamp is a visible discontinuity - exactly what this effect
        // exists to remove. Nothing else here would catch that: clamping barely moves
        // lightness, so OklchStyles_DoNotPulse stays green even when a third of the wheel
        // is clipping.
        //
        // The private L/C constants aren't visible here, so they are recovered from an
        // unclipped sample instead of hardcoded: hue 0 has a gamut ceiling around 0.26 at
        // this lightness (nowhere near the ~0.11 ceiling at hue ~200 that actually bites),
        // so reading back Smooth's own colour at hue 0 through RgbToOklab yields the real
        // L and C it is asking for, whatever they are set to.
        [Fact]
        public void Smooth_NeverAsksForAColourOutsideTheGamut()
        {
            var reference = RainbowEffect.ColourAt(0, 6, RainbowStyle.Smooth);
            var (L, a, b) = ColourMath.RgbToOklab(reference.R, reference.G, reference.B);
            double C = Math.Sqrt(a * a + b * b);

            for (double h = 0; h < 360; h += 0.5)
            {
                Assert.True(ColourMath.OklchInGamut(L, C, h),
                    $"Smooth asks for L={L:F4} C={C:F4} at hue {h:F1}, which sRGB cannot deliver");
            }
        }
    }
}
