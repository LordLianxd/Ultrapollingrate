using System;
using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    public class RainbowWalkerTests
    {
        // The ask, literally: +1, +1, +1. Every colour, in order, none unshown - the first
        // Step returns the first colour rather than jumping past it.
        [Fact]
        public void EachStepReturnsTheNextColour()
        {
            var walker = new RainbowWalker(RainbowStyle.Smooth);
            var ramp = ColourRamp.For(RainbowStyle.Smooth);

            for (int i = 0; i < 20; i++)
                Assert.Equal(ramp[i], walker.Step());
        }

        // Walk three full loops and prove no two consecutive colours differ by more than 1 -
        // including across the seam - AND that the walk actually moves. The no-gap half of
        // this is really ColourRamp's property (ColourRampTests already covers it in
        // isolation); a walker frozen at index 0 forever would also pass it, since diff = 0
        // is always <= 1. The advancing assertion is sound because
        // ColourRampTests.NoColourAppearsTwice establishes every colour in a ramp is
        // distinct, so consecutive Step() calls - which always land on different indices -
        // must return different colours.
        [Theory]
        [InlineData(RainbowStyle.Vivid)]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void WalkingNeverGaps(RainbowStyle style)
        {
            var walker = new RainbowWalker(style);
            var previous = walker.Step();

            for (int i = 0; i < ColourRamp.For(style).Count * 3; i++)
            {
                var next = walker.Step();
                Assert.NotEqual(previous, next);

                int step = Math.Max(Math.Abs(previous.R - next.R),
                           Math.Max(Math.Abs(previous.G - next.G), Math.Abs(previous.B - next.B)));
                Assert.True(step <= 1,
                    $"{style} gapped {step} at step {i}: #{previous.R:X2}{previous.G:X2}{previous.B:X2} -> #{next.R:X2}{next.G:X2}{next.B:X2}");
                previous = next;
            }
        }

        [Fact]
        public void ItLoopsForever()
        {
            var walker = new RainbowWalker(RainbowStyle.Smooth);
            int count = ColourRamp.For(RainbowStyle.Smooth).Count;

            var first = walker.Step();
            for (int i = 1; i < count; i++) walker.Step();
            Assert.Equal(first, walker.Step());
        }

        [Fact]
        public void EachStyleWalksItsOwnRamp()
        {
            Assert.Equal(ColourRamp.For(RainbowStyle.Vivid)[0], new RainbowWalker(RainbowStyle.Vivid).Step());
            Assert.Equal(ColourRamp.For(RainbowStyle.Balanced)[0], new RainbowWalker(RainbowStyle.Balanced).Step());
        }

        // Windows delivers timer ticks on a 15.625ms cadence and no finer - measured against a
        // real DispatcherTimer, and timeBeginPeriod(1) does not move it. So an interval must be
        // a whole number of ticks, or it rounds up and the walk runs slower than the UI claims.
        [Theory]
        [InlineData(1, 15.625)]
        [InlineData(2, 31.25)]
        [InlineData(3, 46.875)]
        [InlineData(12, 187.5)]
        public void IntervalFor_IsAWholeNumberOfTimerTicks(int ticksPerColour, double expectedMs)
        {
            Assert.Equal(expectedMs, RainbowWalker.IntervalFor(ticksPerColour).TotalMilliseconds, 3);
        }

        // Measured: n=1 delivers 64/s, n=2 delivers 32/s, n=3 delivers 21.3/s. These are the
        // numbers the UI shows the user, so they must be the numbers the timer actually keeps.
        [Theory]
        [InlineData(1, 64.0)]
        [InlineData(2, 32.0)]
        [InlineData(3, 21.33)]
        [InlineData(12, 5.33)]
        public void ColoursPerSecond_MatchesWhatTheTimerDelivers(int ticksPerColour, double expected)
        {
            Assert.Equal(expected, RainbowWalker.ColoursPerSecond(ticksPerColour), 2);
        }

        // Feeds DispatcherTimer.Interval, which throws on zero or negative - inside a tick, that
        // takes the app down. The slider cannot produce these, but a profile or a later edit could.
        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(999)]
        public void IntervalFor_OutOfRange_IsClampedToSomethingUsable(int ticksPerColour)
        {
            var interval = RainbowWalker.IntervalFor(ticksPerColour);
            Assert.InRange(interval.TotalMilliseconds, 15.625, 187.5);
        }

        // The cycle time is an OUTPUT: it falls out of the ramp length and the real rate.
        // Smooth has 681 colours, so at n=1 (64/s) a lap is 10.6s.
        [Fact]
        public void CycleSeconds_FallsOutOfTheRampLengthAndTheRealRate()
        {
            var walker = new RainbowWalker(RainbowStyle.Smooth);
            int count = ColourRamp.For(RainbowStyle.Smooth).Count;

            Assert.Equal(count / 64.0, walker.CycleSeconds(1), 2);
            Assert.Equal(count / 32.0, walker.CycleSeconds(2), 2);
        }
    }
}
