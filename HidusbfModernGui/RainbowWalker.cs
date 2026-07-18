using System;

namespace HidusbfModernGui
{
    // Walks a ColourRamp. Speed is expressed in COLOURS PER SECOND. Windows delivers timer
    // ticks on a 15.625ms cadence and no finer (measured), so at most ~64 distinct frames/s
    // are possible. Up to 64 colours/s we show every colour (one per tick). Above that, the
    // timer fires as fast as it can and each tick advances MORE than one colour - fractional,
    // accumulated - so the cycle speeds up past 64/s. Consecutive ramp colours differ by <=1
    // per channel, so advancing a few per tick changes the shown colour by only a few /255 per
    // frame (at the 360/s ceiling that is ~6): still smooth, but no longer literally every colour.
    public sealed class RainbowWalker
    {
        private readonly ColourRamp _ramp;
        private double _pos;   // fractional position along the ramp

        public RainbowWalker(RainbowStyle style) => _ramp = ColourRamp.For(style);

        // Advance by coloursPerTick (>= 0, may be fractional) and return the colour shown NOW
        // (read-then-advance, so the very first call shows ramp[0] rather than stepping over it).
        public (byte R, byte G, byte B) Advance(double coloursPerTick)
        {
            int idx = ((int)Math.Floor(_pos)) % _ramp.Count;
            if (idx < 0) idx += _ramp.Count;
            var colour = _ramp[idx];

            _pos += Math.Max(0.0, coloursPerTick);
            if (_pos >= _ramp.Count) _pos %= _ramp.Count;
            return colour;
        }

        // Compat: one colour per tick.
        public (byte R, byte G, byte B) Step() => Advance(1.0);

        public const double OsTickMs = 15.625;
        private const double FramesPerSecFloor = 1000.0 / OsTickMs;   // ~64
        public const double MinColoursPerSecond = 5.0;
        public const double MaxColoursPerSecond = 360.0;

        // Maps a target colours/s to (timer interval, colours to advance per tick).
        public static (double intervalMs, double coloursPerTick) SpeedPlan(double coloursPerSec)
        {
            coloursPerSec = Math.Clamp(coloursPerSec, MinColoursPerSecond, MaxColoursPerSecond);
            if (coloursPerSec <= FramesPerSecFloor)
            {
                // Slow enough to show every colour: one per tick, whole-tick interval.
                int ticksPerColour = Math.Max(1, (int)Math.Round(FramesPerSecFloor / coloursPerSec));
                return (ticksPerColour * OsTickMs, 1.0);
            }
            // Faster than the timer can show distinct colours: fire every tick, advance >1.
            return (OsTickMs, coloursPerSec / FramesPerSecFloor);
        }

        // Clamped rather than validated: this feeds DispatcherTimer.Interval from inside a tick,
        // where a throw takes the app down.
        public static TimeSpan IntervalFor(double coloursPerSec) =>
            TimeSpan.FromMilliseconds(SpeedPlan(coloursPerSec).intervalMs);

        // The colours/s actually delivered (accounts for whole-tick rounding at slow speeds).
        public static double ActualColoursPerSecond(double coloursPerSec)
        {
            var (intervalMs, perTick) = SpeedPlan(coloursPerSec);
            return perTick * 1000.0 / intervalMs;
        }

        public double CycleSeconds(double coloursPerSec) =>
            _ramp.Count / ActualColoursPerSecond(coloursPerSec);

        // True while every ramp colour is shown (<= the 64/s frame floor).
        public static bool ShowsEveryColour(double coloursPerSec) =>
            coloursPerSec <= FramesPerSecFloor + 0.001;
    }
}
