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
    }

    // How to trade vividness against smoothness. No measurement settles this - a vivid
    // blue IS dark, so constant brightness and maximum saturation cannot both hold. The
    // user picks.
    public enum RainbowStyle
    {
        // Linear HSV. Saturated, and lurches: measured at 36.9x variation in perceptual
        // step size, which reads as jumping between six colours rather than flowing.
        Vivid,

        // OKLCH at constant lightness and a chroma every hue can hold. Steps 3.8x, no
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
}
