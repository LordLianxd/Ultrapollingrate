using System;

namespace HidusbfModernGui
{
    // Walks a ColourRamp one entry at a time.
    //
    // Step() takes no time argument, and that is the entire design. The old rainbow computed
    // its colour from elapsed time, so a late tick was answered with a FURTHER colour - and
    // ticks ran late, because the original DispatcherTimer ran at Background priority, below
    // Render and Input. Measured: one dropped tick doubled the jump, three made it seven.
    // (Rainbow_Toggled now builds the timer at Render priority, but the walk stays immune to
    // a late tick either way - that immunity is the point, not a consequence of the priority.)
    //
    // Here, speed is how OFTEN Step() is called (see IntervalFor), never how far it moves. A
    // late tick still advances exactly one colour. The walk slows for that instant; it cannot
    // gap. The cost is that a loop may take slightly longer than nominal - which nobody can
    // see, unlike a skipped colour.
    public sealed class RainbowWalker
    {
        private readonly ColourRamp _ramp;
        private int _index;

        public RainbowWalker(RainbowStyle style) => _ramp = ColourRamp.For(style);

        // Returns where we are, then advances one. Returning first means the very first
        // colour of the ramp is shown rather than stepped over.
        public (byte R, byte G, byte B) Step()
        {
            var colour = _ramp[_index];
            _index = (_index + 1) % _ramp.Count;
            return colour;
        }

        // Windows delivers DispatcherTimer ticks on a 15.625ms cadence and no finer. This is
        // measured, not assumed: asking for 8.3ms (120/s) delivered 15.6ms (64/s), and asking
        // for 33.3ms (30/s) delivered 46.9ms (21.4/s) - it rounds UP to the next tick, so an
        // in-between interval does not run faster, it runs slow while the UI claims otherwise.
        // timeBeginPeriod(1) was tried and does not move this floor.
        //
        // So speed is counted in whole ticks per colour. Every value the slider can produce is
        // one the timer actually keeps, and the cycle length the UI reports is true.
        private const double OsTickMs = 15.625;

        private const int FastestTicksPerColour = 1;    // 64 colours/s
        private const int SlowestTicksPerColour = 12;   // 5.3 colours/s

        // Clamped rather than validated: this feeds DispatcherTimer.Interval from inside a tick,
        // where a throw takes the app down.
        public static TimeSpan IntervalFor(int ticksPerColour) =>
            TimeSpan.FromMilliseconds(Clamp(ticksPerColour) * OsTickMs);

        public static double ColoursPerSecond(int ticksPerColour) =>
            1000.0 / (Clamp(ticksPerColour) * OsTickMs);

        // How long a full loop takes, from the ramp length and the rate the timer really keeps.
        // An OUTPUT, not a setting: a user who picked a lap length would be picking a number the
        // walk can only honour by skipping colours, which is what this class exists to stop.
        public double CycleSeconds(int ticksPerColour) =>
            _ramp.Count / ColoursPerSecond(ticksPerColour);

        private static int Clamp(int ticksPerColour) =>
            Math.Clamp(ticksPerColour, FastestTicksPerColour, SlowestTicksPerColour);
    }
}
