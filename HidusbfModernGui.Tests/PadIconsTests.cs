using System;
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class PadIconsTests
{
    [Fact]
    public void EveryButton_HasEitherAPathOrText()
    {
        foreach (PadButton b in Enum.GetValues<PadButton>())
        {
            if (b == PadButton.None) continue;
            bool hasPath = !string.IsNullOrWhiteSpace(PadIcons.PathOf(b));
            bool hasText = !string.IsNullOrWhiteSpace(PadIcons.TextOf(b));
            Assert.True(hasPath ^ hasText, $"{b} debe tener icono O texto, no ambos ni ninguno");
        }
    }

    [Fact]
    public void FaceAndDpad_UseIcons()
    {
        foreach (var b in new[] { PadButton.Cross, PadButton.Circle, PadButton.Square, PadButton.Triangle,
                                  PadButton.DpadUp, PadButton.DpadDown, PadButton.DpadLeft, PadButton.DpadRight })
            Assert.False(string.IsNullOrWhiteSpace(PadIcons.PathOf(b)), $"{b} sin icono");
    }

    [Fact]
    public void ShouldersTriggersAndSticks_UseTheirPrintedText()
    {
        Assert.Equal("L1", PadIcons.TextOf(PadButton.L1));
        Assert.Equal("R2", PadIcons.TextOf(PadButton.R2));
        Assert.Equal("L3", PadIcons.TextOf(PadButton.L3));
        Assert.Null(PadIcons.PathOf(PadButton.L1));
    }

    [Fact]
    public void OnlyFaceButtons_AreFilledBadges()
    {
        foreach (var b in new[] { PadButton.Cross, PadButton.Circle, PadButton.Square, PadButton.Triangle })
            Assert.True(PadIcons.IsFilledBadge(b), $"{b} deberia ir en circulo relleno");
        foreach (var b in new[] { PadButton.DpadUp, PadButton.L1, PadButton.Share })
            Assert.False(PadIcons.IsFilledBadge(b), $"{b} no lleva circulo");
    }

    [Fact]
    public void Dpad_DirectionsAreFourDistinctShapes()
    {
        var paths = new[] { PadButton.DpadUp, PadButton.DpadDown, PadButton.DpadLeft, PadButton.DpadRight }
                    .Select(PadIcons.PathOf).ToList();
        Assert.Equal(4, paths.Distinct().Count());
    }

    [Fact]
    public void None_HasNeither()
    {
        Assert.Null(PadIcons.PathOf(PadButton.None));
        Assert.Null(PadIcons.TextOf(PadButton.None));
    }
}
