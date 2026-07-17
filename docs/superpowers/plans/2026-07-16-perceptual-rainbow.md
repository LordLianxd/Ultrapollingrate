# A Rainbow That Does Not Lurch — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the linear-HSV rainbow, whose perceptual step size varies 37×, with an OKLab sweep whose steps are near-uniform — so the cycle reads as a continuous flow instead of jumping between red, orange, yellow, green, cyan, blue, purple.

**Architecture:** `ColourMath` gains OKLab ↔ sRGB conversion and a gamut-mapped OKLCH → RGB. `RainbowEffect.ColourAt` sweeps hue in OKLCH at constant lightness instead of constant HSV. A `RainbowStyle` control lets the user pick their point on a trade-off no measurement can settle for them. All pure and tested; the UI change is one enum and one slider.

**Tech Stack:** .NET 9 (`net9.0-windows`), WPF.

## The measurements this plan is built on

Not theory — run against the shipping `ColourMath`, using OKLab ΔE as the ruler:

```
                                pulso    pasos    viveza
HSV lineal (the current code)   2.14x    36.9x    0.248
OKLCH L=0.70 C=0.10 fixed       1.00x     2.6x    0.100
OKLCH L=0.65 max chroma/hue     1.00x     5.9x    0.184
```

- **pulso** — how much perceived lightness swings over the cycle. 1.00 is flat.
- **pasos** — largest perceptual step ÷ smallest, sampled every 2°. 1.0 is perfectly even.
- **viveza** — mean chroma. Higher is more saturated.

**The 36.9× is the user's complaint.** It is not that HSV omits colours — it produces all of them. It is that it crosses some of them 37 times faster than others, so the eye latches onto the six vertices and reads the rest as jumps.

A first attempt measured step uniformity with a Rec.709-weighted RGB distance and reported HSV at 3.5×, making OKLab look *worse*. That ruler is not perceptual, so it was circular: judging uniformity with a non-uniform yardstick. The numbers above use OKLab ΔE. **Do not reintroduce an RGB-distance metric.**

An earlier suspicion — that `DualSenseLight.Apply` was too slow for the 33 ms tick and the timer was dropping frames — was measured and **disproved**: Apply takes 1.0 ms median, the device-tree walk 0.1 ms. Performance is not the problem. Do not "optimise" it as part of this work.

## The trade-off no measurement can settle

**A vivid blue is dark. That is physics, not a bug.** sRGB's blue primary has a relative luminance of 0.07 against yellow's 0.93. So:

- Constant lightness (no pulse) forces every hue down to what blue can manage → pastel.
- Maximum vividness forces lightness to swing 13× → the pulse comes back.

Max-chroma-per-hue is the middle: lightness stays flat, and each hue takes as much chroma as sRGB allows it. Vividness recovers to 0.184 of HSV's 0.248, and the step ratio lands at 5.9× — six times better than now, not as clean as 2.6×. Its own cost: chroma varies 2.8× across the wheel, so **saturation** pulses even though brightness does not.

This plan ships all three and lets the user choose, because the right answer is the one they like when they look at the controller. It defaults to `Smooth` (fixed chroma) since the reported complaint is jumpiness, not dullness.

## Global Constraints

- **`SystemManager.cs`, `DualSenseLight.cs`, `LightProfile.cs` and `ColourPicker.xaml*` must not change.** Not one line.
- **`PollingCore.cs` must not change.**
- **`ColourMath.HsvToRgb` and `RgbToHsv` must not change.** The picker depends on them and has its own tests. This plan *adds* functions.
- The 173 existing tests must stay green **without being modified**.
- Palette — exactly these ten, no eleventh: `#000000`, `#0A0A0A`, `#111111`, `#1F1F1F`, `#FFFFFF`, `#8A8A8A`, `#4A4A4A`, `#00C853`, `#FFAB00`, `#FF3D00`.
- **Colour never decorates.** The picker, the presets and the swatch are the only exceptions.
- `ColourMath.cs` must stay free of WPF — the test project links it by path precisely so it does not pull WPF in.
- Target framework `net9.0-windows`. Branch `redesign/monochrome`. Build from `HidusbfModernGui/`.
- Git identity is not configured globally. Commit with:
  `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "..."`
  End every commit message with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

---

### Task 1: OKLab conversion in `ColourMath`

**Files:**
- Modify: `HidusbfModernGui/ColourMath.cs`
- Test: `HidusbfModernGui.Tests/ColourMathTests.cs`

**Interfaces:**
- Produces (all consumed by Task 2):
  - `static (double L, double a, double b) ColourMath.RgbToOklab(byte r, byte g, byte b)`
  - `static (byte R, byte G, byte B) ColourMath.OklchToRgb(double L, double C, double hDeg)` — clamps out of gamut
  - `static bool ColourMath.OklchInGamut(double L, double C, double hDeg)`
  - `static double ColourMath.MaxChroma(double L, double hDeg)` — largest in-gamut chroma

- [ ] **Step 1: Write the failing tests**

Append to `HidusbfModernGui.Tests/ColourMathTests.cs`, inside `namespace HidusbfModernGui.Tests`:

```csharp
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
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: FAIL — `'ColourMath' does not contain a definition for 'RgbToOklab'`.

- [ ] **Step 3: Implement**

Add to the `ColourMath` class in `HidusbfModernGui/ColourMath.cs`, leaving `HsvToRgb` and `RgbToHsv` untouched:

```csharp
        // OKLab, Björn Ottosson 2020. Unlike HSV it is perceptually uniform: equal
        // distances look equal. A linear HSV hue sweep crosses some colours 37x faster
        // than others, which the eye reads as jumping between six vertices - measured, and
        // the reason this exists.

        // sRGB byte -> linear light. The gamma curve is why 128 is not half as bright as 255.
        private static double SrgbToLinear(byte v)
        {
            double x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        private static byte LinearToSrgb(double x)
        {
            x = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(Math.Max(x, 0), 1.0 / 2.4) - 0.055;
            return (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
        }

        public static (double L, double a, double b) RgbToOklab(byte r, byte g, byte b)
        {
            double lr = SrgbToLinear(r), lg = SrgbToLinear(g), lb = SrgbToLinear(b);

            double l = Math.Cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
            double m = Math.Cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
            double s = Math.Cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);

            return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                    1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                    0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }

        // OKLCH is OKLab in polar form: lightness, chroma, hue. Sweeping h with L and C
        // held is what makes a rainbow that neither pulses nor lurches.
        private static (double r, double g, double b) OklchToLinear(double L, double C, double hDeg)
        {
            double h = hDeg * Math.PI / 180.0;
            double a = C * Math.Cos(h), bb = C * Math.Sin(h);

            double l_ = L + 0.3963377774 * a + 0.2158037573 * bb;
            double m_ = L - 0.1055613458 * a - 0.0638541728 * bb;
            double s_ = L - 0.0894841775 * a - 1.2914855480 * bb;

            double l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;

            return (+4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                    -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
        }

        // Whether this colour exists in sRGB at all. Asking for more chroma than a hue can
        // hold produces negative or >1 light, which clamping silently flattens - and a
        // clamp IS a jump, right where we are trying to remove one.
        public static bool OklchInGamut(double L, double C, double hDeg)
        {
            var (r, g, b) = OklchToLinear(L, C, hDeg);
            const double e = 0.0005;   // one 8-bit step is ~0.004 in linear light near black
            return r >= -e && r <= 1 + e && g >= -e && g <= 1 + e && b >= -e && b <= 1 + e;
        }

        public static (byte R, byte G, byte B) OklchToRgb(double L, double C, double hDeg)
        {
            var (r, g, b) = OklchToLinear(L, C, hDeg);
            return (LinearToSrgb(r), LinearToSrgb(g), LinearToSrgb(b));
        }

        // The most chroma this hue can hold at this lightness. Binary search rather than a
        // closed form: sRGB's boundary in OKLab is not analytic, and 24 halvings of [0,0.5]
        // lands far inside a single 8-bit step.
        public static double MaxChroma(double L, double hDeg)
        {
            double lo = 0, hi = 0.5;
            for (int i = 0; i < 24; i++)
            {
                double mid = (lo + hi) / 2;
                if (OklchInGamut(L, mid, hDeg)) lo = mid; else hi = mid;
            }
            return lo;
        }
```

- [ ] **Step 4: Run the tests**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 193` (173 existing + 20 new). Any other number, stop and report.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/ColourMath.cs HidusbfModernGui.Tests/ColourMathTests.cs
git commit -m "feat: OKLab conversion, the perceptually uniform colour space

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Three rainbow styles, and a test that proves the fix

**Files:**
- Modify: `HidusbfModernGui/ColourMath.cs` (the `RainbowEffect` class at the bottom)
- Test: `HidusbfModernGui.Tests/ColourMathTests.cs`

**Interfaces:**
- Consumes: `ColourMath.OklchToRgb`, `ColourMath.MaxChroma`, `ColourMath.RgbToOklab` (Task 1)
- Produces:
  - `enum RainbowStyle { Vivid, Smooth, Balanced }`
  - `static (byte R, byte G, byte B) RainbowEffect.ColourAt(double seconds, double cycleSeconds, RainbowStyle style)` — consumed by Task 3

**The existing two-argument `ColourAt` must keep working**, unmodified, with its 8 tests green: it is `Vivid`. Add the overload; do not replace it.

- [ ] **Step 1: Write the failing tests**

Append to `HidusbfModernGui.Tests/ColourMathTests.cs`:

```csharp
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
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: FAIL — `The name 'RainbowStyle' does not exist`.

- [ ] **Step 3: Implement**

Replace the `RainbowEffect` class at the bottom of `HidusbfModernGui/ColourMath.cs`:

```csharp
    // How to trade vividness against smoothness. No measurement settles this - a vivid
    // blue IS dark, so constant brightness and maximum saturation cannot both hold. The
    // user picks.
    public enum RainbowStyle
    {
        // Linear HSV. Saturated, and lurches: measured at 36.9x variation in perceptual
        // step size, which reads as jumping between six colours rather than flowing.
        Vivid,

        // OKLCH at constant lightness and a chroma every hue can hold. Steps 2.6x, no
        // pulse. The cost is pastel: the whole wheel is limited to what blue manages.
        Smooth,

        // OKLCH at constant lightness, each hue taking the most chroma sRGB allows it.
        // Steps 5.9x - still six times better than Vivid - and keeps ~74% of the
        // saturation. Its own cost: chroma varies 2.8x, so saturation pulses even though
        // brightness does not.
        Balanced
    }

    // A colour cycle driven by elapsed time rather than a frame counter, so its speed does
    // not depend on how often the timer actually fires.
    public static class RainbowEffect
    {
        // Measured: 0.10 is the most chroma every hue can hold at L=0.65 without any
        // clipping. Clipping would silently flatten a colour, and a clamp is a jump.
        private const double SmoothLightness = 0.65;
        private const double SmoothChroma = 0.10;
        private const double BalancedLightness = 0.65;

        public static (byte R, byte G, byte B) ColourAt(double seconds, double cycleSeconds)
            => ColourAt(seconds, cycleSeconds, RainbowStyle.Vivid);

        public static (byte R, byte G, byte B) ColourAt(double seconds, double cycleSeconds, RainbowStyle style)
        {
            // A zero or negative period would divide by zero. This runs on a timer, where
            // an exception would take the app down - freeze instead.
            double hue = cycleSeconds <= 0 ? 0 : (seconds / cycleSeconds % 1.0) * 360.0;

            return style switch
            {
                RainbowStyle.Smooth => ColourMath.OklchToRgb(SmoothLightness, SmoothChroma, hue),
                RainbowStyle.Balanced => ColourMath.OklchToRgb(
                    BalancedLightness, ColourMath.MaxChroma(BalancedLightness, hue), hue),
                _ => ColourMath.HsvToRgb(hue, 1, 1)
            };
        }
    }
```

- [ ] **Step 4: Run the tests**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 208` (193 + 15 new). Any other number, stop and report.

- [ ] **Step 5: Commit**

```bash
git add HidusbfModernGui/ColourMath.cs HidusbfModernGui.Tests/ColourMathTests.cs
git commit -m "feat: three rainbow styles, with the trade-off asserted in tests

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: The style picker, and a cycle slow enough to be ambient

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `RainbowStyle`, `RainbowEffect.ColourAt(double, double, RainbowStyle)` (Task 2)
- Produces: named element `RainbowStyleList`

- [ ] **Step 1: Add the style combo and widen the cycle range**

In the EFECTO border, replace the `StackPanel` holding `RainbowCheck` with:

```xml
<StackPanel Orientation="Horizontal">
    <CheckBox x:Name="RainbowCheck" Content="Rainbow" Foreground="{StaticResource TextDataBrush}"
              FontSize="12" VerticalAlignment="Center" Margin="0,0,16,0"
              Checked="Rainbow_Toggled" Unchecked="Rainbow_Toggled"/>
    <ComboBox x:Name="RainbowStyleList" Width="120" VerticalAlignment="Center" Margin="0,0,16,0"
              SelectionChanged="RainbowStyle_Changed"/>
</StackPanel>
<StackPanel Orientation="Horizontal" Margin="0,10,0,0">
    <TextBlock Text="CICLO" Style="{StaticResource FieldLabel}" VerticalAlignment="Center" Margin="0,0,8,0"/>
    <!-- Up to 120s: the old 20s ceiling was still brisk for an ambient effect, and the
         user had already pinned the slider there asking for slower. -->
    <Slider x:Name="RainbowSpeed" Minimum="2" Maximum="120" Value="30" Width="200" VerticalAlignment="Center"/>
    <TextBlock x:Name="RainbowSpeedText" Text="30 s" Style="{StaticResource DataText}"
               VerticalAlignment="Center" Margin="10,0,0,0"/>
</StackPanel>
<TextBlock x:Name="RainbowStyleHint" Style="{StaticResource FieldLabel}" TextWrapping="Wrap" Margin="0,10,0,0"/>
```

Keep the existing "El efecto lo anima UltraPolling…" `TextBlock` below it.

- [ ] **Step 2: Populate the combo**

In `BuildLightControls()`, after the brightness combo is populated and before the presets loop:

```csharp
    foreach (var (label, value) in new (string, RainbowStyle)[]
             {
                 ("Suave", RainbowStyle.Smooth),
                 ("Equilibrado", RainbowStyle.Balanced),
                 ("Vivo", RainbowStyle.Vivid),
             })
        RainbowStyleList.Items.Add(new ComboBoxItem { Content = label, Tag = value });

    // Smooth by default: the reported complaint is that the cycle jumps, not that it is
    // dull. Vivid is the old behaviour, kept for anyone who wants saturation over
    // smoothness.
    RainbowStyleList.SelectedIndex = 0;
    UpdateRainbowHint();
```

- [ ] **Step 3: Add the handlers**

```csharp
private RainbowStyle CurrentRainbowStyle =>
    RainbowStyleList?.SelectedItem is ComboBoxItem { Tag: RainbowStyle s } ? s : RainbowStyle.Smooth;

// The trade-off stated where the choice is made. Every one of these numbers is measured,
// not estimated - see docs/superpowers/plans/2026-07-16-perceptual-rainbow.md.
private void UpdateRainbowHint()
{
    if (RainbowStyleHint == null) return;
    RainbowStyleHint.Text = CurrentRainbowStyle switch
    {
        RainbowStyle.Smooth => "Suave: transicion perfectamente pareja, brillo constante. Los colores salen menos saturados - un azul vivo es oscuro, y no se puede tener las dos cosas.",
        RainbowStyle.Balanced => "Equilibrado: cada tono coge todo el color que puede sin variar el brillo. Mas vivo que Suave, casi tan parejo.",
        _ => "Vivo: maxima saturacion. Da saltos y pulsa - el azul se ve 13 veces mas oscuro que el amarillo."
    };
}

private void RainbowStyle_Changed(object sender, SelectionChangedEventArgs e)
{
    UpdateRainbowHint();
    // No write here: the tick already runs at 30 Hz and picks the style up on its own.
    // Writing here too would just race it.
}
```

- [ ] **Step 4: Use the style in the tick**

In `Rainbow_Tick`, replace the `ColourAt` call:

```csharp
    var (r, g, b) = RainbowEffect.ColourAt(_rainbowClock.Elapsed.TotalSeconds,
                                           RainbowSpeed.Value, CurrentRainbowStyle);
```

- [ ] **Step 5: Verify**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.`

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 208`.

Run the anti-blue gate:
```bash
grep -inE "6366F1|818CF8|0F172A|1E293B|312E81|EEF2FF|E6E9F2|AccentIndigo|SidebarBg|PanelBg|WindowBg|CardDark|InputBg|TextPrimary|TextSecondary" HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs HidusbfModernGui/Theme.xaml
```
Expected: empty.

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git commit -m "feat: rainbow style picker, defaulting to the smooth sweep

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Verify — the only judge that counts is the controller

**Files:** none

- [ ] **Step 1: Tests and build**

Run: `cd HidusbfModernGui.Tests && dotnet test` → `Passed: 208`
Run: `cd HidusbfModernGui && dotnet build` → succeeds

- [ ] **Step 2: Backend untouched**

Run: `cd VerifyState && dotnet run`
Expected: `Build (by hash) : NoPatch`, `ModeText : No Patch`, a non-zero device count. Do not assert an exact count — it is environmental.

- [ ] **Step 3: Binding errors**

Run the app and check the debug output for `System.Windows.Data Error`. Expected: none.

- [ ] **Step 4: Watch the controller**

This is the step the whole plan exists for. The numbers say Smooth is 14× more even than Vivid; **whether it looks right is not something a test can answer.**

- Tick Rainbow with **Suave** and a 30 s cycle. It should glide — no sense of stopping at red, orange, yellow. Compare against **Vivo**, which is the old behaviour: the lurch should be obvious side by side.
- Try **Equilibrado**. More saturated than Suave. Watch for saturation pulsing rather than brightness pulsing — that is its known cost, and whether it reads as a defect is a judgement call.
- Drag CICLO to 120 s. It should crawl.
- **The lightbar is not a monitor.** OKLab assumes sRGB gamma; the LED is likely linear PWM behind a diffuser. If Smooth looks washed out or the improvement is smaller than the numbers promise, that gap is why — report it rather than assuming the maths is wrong.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: verify the perceptual rainbow end to end

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Notes for the implementer

**Do not touch `HsvToRgb` or `RgbToHsv`.** The picker binds to them and they have their own tests. This work adds functions beside them.

**Do not measure uniformity with RGB distance.** A first attempt did, reported HSV at 3.5× instead of 36.9×, and made OKLab look worse than what it replaces. Judging perceptual uniformity needs a perceptual ruler; the tests use OKLab ΔE for exactly this reason.

**Performance is not the problem.** `DualSenseLight.Apply` was measured at 1.0 ms median against a 33 ms budget, and the device-tree walk at 0.1 ms. It is inefficient and it does not matter. Leave it alone.

**Do not raise `SmoothChroma` to make Smooth more vivid.** 0.10 is the measured ceiling at which every hue still fits inside sRGB. Above it, hues clip — and a clamp is exactly the discontinuity this work removes. If Smooth is too dull, that is what `Balanced` is for.
