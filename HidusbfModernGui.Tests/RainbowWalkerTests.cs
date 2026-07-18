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

        [Fact]
        public void SpeedPlan_AtOrBelow64_ShowsEveryColour_OneColourPerTick()
        {
            var (intervalMs, perTick) = RainbowWalker.SpeedPlan(32);
            Assert.Equal(1.0, perTick, 3);
            Assert.True(intervalMs >= 15.625);              // interval is a whole-tick multiple
            Assert.True(RainbowWalker.ShowsEveryColour(32));
        }

        [Fact]
        public void SpeedPlan_Above64_FiresEveryTick_AdvancesMultipleColours()
        {
            var (intervalMs, perTick) = RainbowWalker.SpeedPlan(180);
            Assert.Equal(15.625, intervalMs, 3);            // fastest the timer allows
            Assert.True(perTick > 2.5 && perTick < 3.0);    // 180/64 ~= 2.81
            Assert.False(RainbowWalker.ShowsEveryColour(180));
        }

        [Fact]
        public void SpeedPlan_Clamps()
        {
            Assert.Equal(RainbowWalker.SpeedPlan(5).coloursPerTick,   RainbowWalker.SpeedPlan(1).coloursPerTick,   3);
            Assert.Equal(RainbowWalker.SpeedPlan(360).coloursPerTick, RainbowWalker.SpeedPlan(999).coloursPerTick, 3);
        }

        [Fact]
        public void ActualColoursPerSecond_MatchesTarget_WhenTargetIsAWholeTickRate()
        {
            // 64/s is exactly one colour per 15.625ms tick.
            Assert.Equal(64.0, RainbowWalker.ActualColoursPerSecond(64), 0);
            Assert.Equal(180.0, RainbowWalker.ActualColoursPerSecond(180), 0);
        }

        [Fact]
        public void Advance_Fractional_AccumulatesAcrossTicks()
        {
            var w = new RainbowWalker(RainbowStyle.Smooth);
            var first = w.Advance(0.5);          // returns ramp[0], pos -> 0.5
            var second = w.Advance(0.5);         // still ramp[0], pos -> 1.0
            var third = w.Advance(0.5);          // ramp[1], pos -> 1.5
            Assert.Equal(first, second);         // half-steps do not skip
            Assert.NotEqual(second, third);
        }

        [Fact]
        public void Advance_One_MatchesStep()
        {
            var a = new RainbowWalker(RainbowStyle.Vivid);
            var b = new RainbowWalker(RainbowStyle.Vivid);
            for (int i = 0; i < 10; i++)
                Assert.Equal(a.Step(), b.Advance(1.0));
        }
    }
}
