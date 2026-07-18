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

    [Fact]
    public void Shape_Normal_IsLinear()
    {
        Assert.Equal(0.25, InputTransform.Shape(0.25, ResponseCurve.Normal, 50), 3);
        Assert.Equal(0.80, InputTransform.Shape(0.80, ResponseCurve.Normal, 50), 3);
    }

    [Fact]
    public void Shape_Dinamica_IsAnSCurve()
    {
        // S: pasa por 0.5 en el centro, suave abajo, empinada pasando el medio, simetrica.
        Assert.Equal(0.5, InputTransform.Shape(0.5, ResponseCurve.Dinamica, 50), 3);
        Assert.True(InputTransform.Shape(0.25, ResponseCurve.Dinamica, 50) < 0.25);  // suave cerca del centro
        Assert.True(InputTransform.Shape(0.75, ResponseCurve.Dinamica, 50) > 0.75);  // empinada hacia el borde
        double a = InputTransform.Shape(0.30, ResponseCurve.Dinamica, 50);
        double b = InputTransform.Shape(0.70, ResponseCurve.Dinamica, 50);
        Assert.Equal(1.0, a + b, 3);   // simetrica: f(t) + f(1-t) = 1
        Assert.Equal(0.0, InputTransform.Shape(0.0, ResponseCurve.Dinamica, 50), 3);
        Assert.Equal(1.0, InputTransform.Shape(1.0, ResponseCurve.Dinamica, 50), 3);
    }

    [Fact]
    public void Shape_Digital_Steps()
    {
        Assert.Equal(0.0, InputTransform.Shape(0.49, ResponseCurve.Digital, 50), 3);
        Assert.Equal(1.0, InputTransform.Shape(0.50, ResponseCurve.Digital, 50), 3);
        Assert.Equal(1.0, InputTransform.Shape(0.90, ResponseCurve.Digital, 50), 3);
        Assert.Equal(0.0, InputTransform.Shape(0.0, ResponseCurve.Digital, 50), 3);
    }

    [Fact]
    public void Shape_PowerPresets_MatchExponents()
    {
        Assert.Equal(Math.Pow(0.5, 1.8), InputTransform.Shape(0.5, ResponseCurve.Precisa, 50), 3);
        Assert.Equal(Math.Pow(0.5, 0.6), InputTransform.Shape(0.5, ResponseCurve.Rapida, 50), 3);
        // Personalizada usa la curvatura: 50 -> exponente 1.0 -> lineal.
        Assert.Equal(0.5, InputTransform.Shape(0.5, ResponseCurve.Personalizada, 50), 3);
    }

    [Fact]
    public void ApplyStick_WithCurve_AppliesDeadzoneThenShape()
    {
        // Sin deadzone, Digital: magnitud 0.6 -> full en la direccion del stick.
        var (x, _) = InputTransform.ApplyStick(new StickInput(0.6, 0.0), 0.0, 1.0, ResponseCurve.Digital, 50);
        Assert.Equal(1.0, x, 2);
        var (x2, _) = InputTransform.ApplyStick(new StickInput(0.3, 0.0), 0.0, 1.0, ResponseCurve.Digital, 50);
        Assert.Equal(0.0, x2, 2);   // 0.3 < 0.5 -> 0
    }
}
