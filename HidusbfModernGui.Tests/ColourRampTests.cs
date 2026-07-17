using System;
using System.Linq;
using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    public class ColourRampTests
    {
        // THE property this whole feature exists for. Every consecutive pair differs by at
        // most 1 in each channel - so walking the ramp cannot skip a colour, whatever the
        // timer does. If this fails, the rainbow steps.
        [Theory]
        [InlineData(RainbowStyle.Vivid)]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void EveryStepIsAtMostOne(RainbowStyle style)
        {
            var ramp = ColourRamp.For(style);
            for (int i = 1; i < ramp.Count; i++)
            {
                var a = ramp[i - 1];
                var b = ramp[i];
                int step = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                Assert.True(step <= 1,
                    $"{style} jumps {step} at index {i}: #{a.R:X2}{a.G:X2}{a.B:X2} -> #{b.R:X2}{b.G:X2}{b.B:X2}");
            }
        }

        // It is a loop, so the seam matters as much as the middle. A ramp that walks
        // smoothly and then snaps on the wrap is still a rainbow that jumps - once a cycle,
        // at the same place, which is worse than random.
        [Theory]
        [InlineData(RainbowStyle.Vivid)]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void TheLoopClosesWithoutASnap(RainbowStyle style)
        {
            var ramp = ColourRamp.For(style);
            var last = ramp[ramp.Count - 1];
            var first = ramp[0];
            int step = Math.Max(Math.Abs(last.R - first.R), Math.Max(Math.Abs(last.G - first.G), Math.Abs(last.B - first.B)));
            Assert.True(step <= 1,
                $"{style} snaps {step} on the wrap: #{last.R:X2}{last.G:X2}{last.B:X2} -> #{first.R:X2}{first.G:X2}{first.B:X2}");
        }

        // No colour twice: a repeat would mean the walk stalls, and the light would visibly
        // hesitate at that point every cycle.
        [Theory]
        [InlineData(RainbowStyle.Vivid)]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void NoColourAppearsTwice(RainbowStyle style)
        {
            var ramp = ColourRamp.For(style);
            var keys = ramp.Colours.Select(c => (c.R << 16) | (c.G << 8) | c.B).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        // Measured against the shipping RainbowEffect. If a ramp collapses to a handful of
        // entries the walk is not covering the wheel, and every other test here would still
        // pass on a two-colour ramp.
        [Theory]
        [InlineData(RainbowStyle.Vivid, 1400)]
        [InlineData(RainbowStyle.Smooth, 600)]
        [InlineData(RainbowStyle.Balanced, 1200)]
        public void TheRampIsAsLongAsTheStyleHasColours(RainbowStyle style, int atLeast)
        {
            Assert.True(ColourRamp.For(style).Count >= atLeast,
                $"{style} only has {ColourRamp.For(style).Count} colours");
        }

        // The walk runs forever in both directions; the caller should never have to think
        // about the boundary.
        [Fact]
        public void TheIndexerWraps()
        {
            var ramp = ColourRamp.For(RainbowStyle.Smooth);
            Assert.Equal(ramp[0], ramp[ramp.Count]);
            Assert.Equal(ramp[1], ramp[ramp.Count + 1]);
            Assert.Equal(ramp[ramp.Count - 1], ramp[-1]);
        }

        // Building a ramp walks hundreds of thousands of samples. Doing that on every tick
        // would put it on the UI thread up to 64 times a second.
        [Fact]
        public void RampsAreCachedPerStyle()
        {
            Assert.Same(ColourRamp.For(RainbowStyle.Vivid), ColourRamp.For(RainbowStyle.Vivid));
            Assert.NotSame(ColourRamp.For(RainbowStyle.Vivid), ColourRamp.For(RainbowStyle.Smooth));
        }

        // Vivid's ramp must still be the vivid colours - the ramp is a resampling of
        // ColourAt, not a new palette.
        [Fact]
        public void VividStartsAtRed()
        {
            Assert.Equal(((byte)255, (byte)0, (byte)0), ColourRamp.For(RainbowStyle.Vivid)[0]);
        }
    }
}
