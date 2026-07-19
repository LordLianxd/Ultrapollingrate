using System;
using System.Collections.Generic;
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

    private static List<CurvePoint> Diagonal() => new()
    {
        new(0, 0), new(0.25, 0.25), new(0.5, 0.5), new(0.75, 0.75), new(1, 1),
    };

    [Fact]
    public void ShapeCustom_DiagonalPoints_IsIdentity()
    {
        foreach (var t in new[] { 0.0, 0.1, 0.33, 0.5, 0.77, 1.0 })
            Assert.Equal(t, InputTransform.ShapeCustom(t, Diagonal()), 3);
    }

    [Fact]
    public void ShapeCustom_PassesThroughEveryPoint()
    {
        var pts = new List<CurvePoint> { new(0, 0), new(0.3, 0.6), new(0.7, 0.65), new(1, 1) };
        foreach (var p in pts)
            Assert.Equal(p.Y, InputTransform.ShapeCustom(p.X, pts), 3);
    }

    [Fact]
    public void ShapeCustom_NoOvershoot_BetweenPoints()
    {
        // Subida brusca y luego casi plano: un spline ingenuo sobreimpulsa por encima de 0.9
        // entre 0.5 y 1.0; PCHIP no debe salirse de [0.9, 1.0] en ese tramo.
        var pts = new List<CurvePoint> { new(0, 0), new(0.5, 0.9), new(1, 1) };
        for (double t = 0.5; t <= 1.0; t += 0.05)
        {
            double y = InputTransform.ShapeCustom(t, pts);
            Assert.InRange(y, 0.9 - 1e-9, 1.0 + 1e-9);
        }
    }

    [Fact]
    public void ShapeCustom_FlatSegment_StaysFlat()
    {
        var pts = new List<CurvePoint> { new(0, 0.5), new(0.5, 0.5), new(1, 1) };
        Assert.Equal(0.5, InputTransform.ShapeCustom(0.25, pts), 3);
    }

    [Fact]
    public void ShapeCustom_ClampsOutsideAndHandlesUnsorted()
    {
        var pts = new List<CurvePoint> { new(1, 1), new(0, 0), new(0.5, 0.8) };  // desordenados
        Assert.Equal(0.0, InputTransform.ShapeCustom(-0.5, pts), 3);
        Assert.Equal(1.0, InputTransform.ShapeCustom(1.5, pts), 3);
        Assert.Equal(0.8, InputTransform.ShapeCustom(0.5, pts), 3);
    }

    [Fact]
    public void ShapeCustom_NullOrTooFewPoints_IsLinear()
    {
        Assert.Equal(0.4, InputTransform.ShapeCustom(0.4, null), 3);
        Assert.Equal(0.4, InputTransform.ShapeCustom(0.4, new List<CurvePoint> { new(0, 0) }), 3);
    }

    [Fact]
    public void Shape_Propia_WithoutPoints_IsLinear()
    {
        Assert.Equal(0.6, InputTransform.Shape(0.6, ResponseCurve.Propia, 50), 3);
    }

    [Fact]
    public void ApplyStick_WithCustomPoints_UsesThem()
    {
        var pts = new List<CurvePoint> { new(0, 0), new(0.5, 0.9), new(1, 1) };
        var (x, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0,
                                               ResponseCurve.Propia, 50, pts);
        Assert.Equal(0.9, x, 2);
    }
}
