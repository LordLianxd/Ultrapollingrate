using HidusbfModernGui;
using Xunit;

public class RemapSettingsTests
{
    [Fact]
    public void Defaults_AreNeutral()
    {
        var s = new RemapSettings();
        Assert.Equal(0.0, s.LeftInnerDeadzone, 3);   // 0% -> 0.0
        Assert.Equal(1.0, s.LeftOuterDeadzone, 3);   // 100% -> 1.0
        Assert.Equal(ResponseCurve.Normal, s.LeftCurve);
        Assert.Equal(0.0, s.L2Point, 3);
    }

    [Fact]
    public void PercentagesConvertToFractions()
    {
        var s = new RemapSettings { LeftDeadzonePct = 15, LeftReachPct = 90, L2PointPct = 20 };
        Assert.Equal(0.15, s.LeftInnerDeadzone, 3);
        Assert.Equal(0.90, s.LeftOuterDeadzone, 3);
        Assert.Equal(0.20, s.L2Point, 3);
    }

    [Fact]
    public void PercentagesClampToUiRanges()
    {
        var s = new RemapSettings { LeftDeadzonePct = 99, LeftReachPct = 10 };
        Assert.Equal(0.30, s.LeftInnerDeadzone, 3);   // tope 30%
        Assert.Equal(0.70, s.LeftOuterDeadzone, 3);   // piso 70%
    }
}
