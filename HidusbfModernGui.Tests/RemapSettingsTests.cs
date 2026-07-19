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

    [Fact]
    public void LeftCurvaturePct_DefaultsTo50()
    {
        var s = new RemapSettings();
        Assert.Equal(50, s.LeftCurvaturePct);
    }

    [Fact]
    public void LeftCurveExponent_PersonalizadaUsesCurvature()
    {
        var s = new RemapSettings { LeftCurve = ResponseCurve.Personalizada, LeftCurvaturePct = 0 };
        Assert.Equal(2.0, s.LeftCurveExponent, 3);
    }

    [Fact]
    public void LeftCurveExponent_NormalPresetIsLinear()
    {
        var s = new RemapSettings { LeftCurve = ResponseCurve.Normal };
        Assert.Equal(1.0, s.LeftCurveExponent, 3);
    }

    [Fact]
    public void CurvePoints_DefaultToFiveDiagonalPoints()
    {
        var s = new RemapSettings();
        Assert.Equal(5, s.LeftCurvePoints.Count);
        Assert.Equal(new CurvePoint(0, 0), s.LeftCurvePoints[0]);
        Assert.Equal(new CurvePoint(1, 1), s.LeftCurvePoints[^1]);
        Assert.Equal(new CurvePoint(0.5, 0.5), s.RightCurvePoints[2]);
    }

    [Fact]
    public void Sanitize_DegradesRetiredCurvesToLineal()
    {
        var s = new RemapSettings { LeftCurve = ResponseCurve.Precisa, RightCurve = ResponseCurve.Digital };
        s.Sanitize();
        Assert.Equal(ResponseCurve.Normal, s.LeftCurve);
        Assert.Equal(ResponseCurve.Normal, s.RightCurve);
    }

    [Fact]
    public void Sanitize_KeepsLinealAndEditor()
    {
        var s = new RemapSettings { LeftCurve = ResponseCurve.Normal, RightCurve = ResponseCurve.Propia };
        s.Sanitize();
        Assert.Equal(ResponseCurve.Normal, s.LeftCurve);
        Assert.Equal(ResponseCurve.Propia, s.RightCurve);
    }

    [Fact]
    public void SanitizePoints_WrongCountOrNull_ResetsToDefault()
    {
        Assert.Equal(RemapSettings.DefaultCurvePoints(), RemapSettings.SanitizePoints(null));
        Assert.Equal(RemapSettings.DefaultCurvePoints(),
            RemapSettings.SanitizePoints(new List<CurvePoint> { new(0, 0), new(1, 1) }));
    }

    [Fact]
    public void SanitizePoints_AnchorsEndpointsAndOrdersX()
    {
        var messy = new List<CurvePoint> { new(0.9, 0.8), new(0.5, 2.0), new(0.1, -1.0), new(0.7, 0.6), new(0.2, 0.3) };
        var r = RemapSettings.SanitizePoints(messy);
        Assert.Equal(5, r.Count);
        Assert.Equal(new CurvePoint(0, 0), r[0]);
        Assert.Equal(new CurvePoint(1, 1), r[4]);
        for (int i = 1; i < 5; i++) Assert.True(r[i].X > r[i - 1].X);          // X estrictamente creciente
        for (int i = 1; i <= 3; i++) Assert.InRange(r[i].Y, 0.0, 1.0);         // Y acotada
    }
}
