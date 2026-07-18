using System;
using HidusbfModernGui;
using Xunit;

public class InputTransformCurveTests
{
    [Fact]
    public void PresetExponent_NormalIsLinear()
    {
        Assert.Equal(1.0, InputTransform.PresetExponent(ResponseCurve.Normal), 3);
    }

    [Fact]
    public void PresetExponent_PrecisaIsAboveOne()
    {
        Assert.True(InputTransform.PresetExponent(ResponseCurve.Precisa) > 1.0);
    }

    [Fact]
    public void PresetExponent_RapidaIsBelowOne()
    {
        Assert.True(InputTransform.PresetExponent(ResponseCurve.Rapida) < 1.0);
    }

    [Fact]
    public void CurvatureExponent_ZeroIsMostPrecise()
    {
        Assert.Equal(2.0, InputTransform.CurvatureExponent(0), 3);
    }

    [Fact]
    public void CurvatureExponent_FiftyIsLinear()
    {
        Assert.Equal(1.0, InputTransform.CurvatureExponent(50), 3);
    }

    [Fact]
    public void CurvatureExponent_HundredIsMostAggressive()
    {
        Assert.Equal(0.5, InputTransform.CurvatureExponent(100), 3);
    }

    [Fact]
    public void ApplyStick_ExponentOverload_LinearAtOne()
    {
        var (x, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, 1.0);
        Assert.Equal(0.5, x, 3);
    }

    [Fact]
    public void ApplyStick_ExponentOverload_GentlerAboveOne_MidRange()
    {
        var (x, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, 2.0);
        Assert.True(x < 0.5);
    }

    [Fact]
    public void ApplyStick_ExponentOverload_SharperBelowOne_MidRange()
    {
        var (x, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, 0.5);
        Assert.True(x > 0.5);
    }

    [Fact]
    public void ApplyStick_EnumOverload_MatchesExponentOverload_ForPrecisa()
    {
        var s = new StickInput(0.6, 0.2);
        var viaEnum = InputTransform.ApplyStick(s, 0.1, 1.0, ResponseCurve.Precisa);
        var viaExponent = InputTransform.ApplyStick(s, 0.1, 1.0,
            InputTransform.PresetExponent(ResponseCurve.Precisa));
        Assert.Equal(viaEnum.X, viaExponent.X, 3);
        Assert.Equal(viaEnum.Y, viaExponent.Y, 3);
    }
}
