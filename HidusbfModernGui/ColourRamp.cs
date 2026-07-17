using System;
using System.Collections.Generic;

namespace HidusbfModernGui
{
    // Every distinct colour a style produces, in the order it produces them.
    //
    // The rainbow used to compute its colour from elapsed time, which holds the cycle
    // duration exact and skips colours to do it - a Vivid cycle has 1530 distinct colours
    // and a 30-second cycle at 30 ticks/second only has 900 frames to show them in. It also
    // meant a late tick produced a further colour, so DispatcherTimer's Background priority
    // turned UI load into visible jumps.
    //
    // Walking this list instead inverts the invariant: the colours are exact and the cycle
    // takes however long it takes. Nobody can see that a cycle ran 31.4s instead of 30.0s.
    // Everybody can see a colour that got skipped.
    public sealed class ColourRamp
    {
        private static readonly Dictionary<RainbowStyle, ColourRamp> Cache = new();
        private static readonly object CacheLock = new();

        // Fine enough that consecutive samples never differ by more than one 8-bit step,
        // for every style. Balanced is the demanding one: its chroma changes fast with hue
        // in the greens, so equal hue steps are not equal colour steps.
        private const int Samples = 360_000;

        public IReadOnlyList<(byte R, byte G, byte B)> Colours { get; }
        public int Count => Colours.Count;

        // Wraps in both directions, so a walker never has to think about the boundary.
        public (byte R, byte G, byte B) this[int index]
        {
            get
            {
                int i = index % Count;
                if (i < 0) i += Count;
                return Colours[i];
            }
        }

        private ColourRamp(IReadOnlyList<(byte, byte, byte)> colours) => Colours = colours;

        public static ColourRamp For(RainbowStyle style)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(style, out var cached)) return cached;

                var ramp = Build(style);
                Cache[style] = ramp;
                return ramp;
            }
        }

        // Resamples RainbowEffect.ColourAt densely and keeps each colour the first time it
        // appears. The result is the same palette in the same order, minus the duplicates a
        // dense sample produces.
        private static ColourRamp Build(RainbowStyle style)
        {
            var colours = new List<(byte, byte, byte)>();
            var seen = new HashSet<int>();

            // The period is arbitrary: sampling t across one period covers the hue circle
            // exactly once, whatever the number.
            const double period = 1.0;

            for (int i = 0; i < Samples; i++)
            {
                var c = RainbowEffect.ColourAt(i / (double)Samples * period, period, style);
                int key = (c.R << 16) | (c.G << 8) | c.B;
                if (seen.Add(key)) colours.Add((c.R, c.G, c.B));
            }

            return new ColourRamp(colours);
        }
    }
}
