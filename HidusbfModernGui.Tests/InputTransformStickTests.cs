using System;
using HidusbfModernGui;
using Xunit;

public class InputTransformStickTests
{
    [Fact]
    public void InsideInnerDeadzone_IsZero()
    {
        var (x, y) = InputTransform.ApplyStick(new StickInput(0.05, 0.0), 0.10, 1.0, ResponseCurve.Normal);
        Assert.Equal(0.0, x, 3);
        Assert.Equal(0.0, y, 3);
    }

    [Fact]
    public void AtOuterEdge_IsFullTilt()
    {
        var (x, y) = InputTransform.ApplyStick(new StickInput(1.0, 0.0), 0.10, 1.0, ResponseCurve.Normal);
        Assert.Equal(1.0, x, 2);
        Assert.Equal(0.0, y, 2);
    }

    [Fact]
    public void JustPastInnerDeadzone_StartsFromZero_NotAJump()
    {
        // Rescale: a magnitude just above the inner deadzone maps to near 0, not to a step.
        var (x, _) = InputTransform.ApplyStick(new StickInput(0.11, 0.0), 0.10, 1.0, ResponseCurve.Normal);
        Assert.True(x > 0.0 && x < 0.05);
    }

    [Fact]
    public void OuterDeadzone_ReachesFullBeforePhysicalMax()
    {
        // With outer deadzone 0.90, a 0.90 magnitude already means full output.
        var (x, _) = InputTransform.ApplyStick(new StickInput(0.90, 0.0), 0.0, 0.90, ResponseCurve.Normal);
        Assert.Equal(1.0, x, 2);
    }

    [Fact]
    public void PrecisaCurve_IsGentlerThanNormal_MidRange()
    {
        var (xp, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, ResponseCurve.Precisa);
        var (xn, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, ResponseCurve.Normal);
        Assert.True(xp < xn);   // más control fino en el centro
    }

    [Fact]
    public void RapidaCurve_IsSharperThanNormal_MidRange()
    {
        var (xr, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, ResponseCurve.Rapida);
        var (xn, _) = InputTransform.ApplyStick(new StickInput(0.5, 0.0), 0.0, 1.0, ResponseCurve.Normal);
        Assert.True(xr > xn);
    }

    [Fact]
    public void Direction_IsPreserved_OnDiagonal()
    {
        var (x, y) = InputTransform.ApplyStick(new StickInput(0.6, 0.6), 0.1, 1.0, ResponseCurve.Normal);
        Assert.Equal(x, y, 3);   // 45° se mantiene 45°
    }
}
