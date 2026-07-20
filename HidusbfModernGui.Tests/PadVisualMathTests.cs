using HidusbfModernGui;
using Xunit;

public class PadVisualMathTests
{
    [Fact]
    public void StickOffset_Centered_IsZero()
    {
        var (dx, dy) = PadVisualMath.StickOffset(0, 0, 30);
        Assert.Equal(0.0, dx, 3);
        Assert.Equal(0.0, dy, 3);
    }

    [Fact]
    public void StickOffset_FullRight_IsRadiusRight()
    {
        var (dx, dy) = PadVisualMath.StickOffset(1, 0, 30);
        Assert.Equal(30.0, dx, 3);
        Assert.Equal(0.0, dy, 3);
    }

    [Fact]
    public void StickOffset_FullUp_IsRadiusUp_ScreenYInverted()
    {
        // Y=+1 es "arriba" en ControllerState; en pantalla arriba es -Dy.
        var (dx, dy) = PadVisualMath.StickOffset(0, 1, 30);
        Assert.Equal(0.0, dx, 3);
        Assert.Equal(-30.0, dy, 3);
    }

    [Fact]
    public void StickOffset_DiagonalOverMagnitude_ClampedToRadius()
    {
        // (1,1) tiene magnitud 1.414; el pulgar no puede salir del pozo: se acota a radius.
        var (dx, dy) = PadVisualMath.StickOffset(1, 1, 30);
        double mag = System.Math.Sqrt(dx * dx + dy * dy);
        Assert.Equal(30.0, mag, 2);
        Assert.Equal(dx, -dy, 3);   // 45° se mantiene (Dy invertido)
    }

    [Fact]
    public void Fill01_Clamps()
    {
        Assert.Equal(0.0, PadVisualMath.Fill01(-0.5), 3);
        Assert.Equal(1.0, PadVisualMath.Fill01(1.5), 3);
        Assert.Equal(0.4, PadVisualMath.Fill01(0.4), 3);
    }
}
