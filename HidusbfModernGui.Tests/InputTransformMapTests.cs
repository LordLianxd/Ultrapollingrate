using System.Collections.Generic;
using HidusbfModernGui;
using Xunit;

public class InputTransformMapTests
{
    [Fact]
    public void Trigger_BelowPoint_IsZero()
        => Assert.Equal(0.0, InputTransform.ApplyTrigger(0.10, 0.20), 3);

    [Fact]
    public void Trigger_AtPoint_IsFull()
        => Assert.Equal(1.0, InputTransform.ApplyTrigger(0.20, 0.20), 3);

    [Fact]
    public void Trigger_AbovePoint_StaysFull()
        => Assert.Equal(1.0, InputTransform.ApplyTrigger(0.9, 0.20), 3);

    [Fact]
    public void Trigger_PointZero_IsLinearPassthrough()
        => Assert.Equal(0.5, InputTransform.ApplyTrigger(0.5, 0.0), 3);

    [Fact]
    public void Remap_SwapsWhenMapped()
    {
        var table = new Dictionary<PadButton, PadButton> { [PadButton.Cross] = PadButton.Square };
        Assert.Equal(PadButton.Square, InputTransform.Remap(PadButton.Cross, table));
        Assert.Equal(PadButton.Circle, InputTransform.Remap(PadButton.Circle, table)); // sin entrada: igual
    }

    [Theory]
    [InlineData(100, 100, TouchZone.ArribaIzq)]
    [InlineData(1800, 100, TouchZone.ArribaDer)]
    [InlineData(100, 1000, TouchZone.AbajoIzq)]
    [InlineData(1800, 1000, TouchZone.AbajoDer)]
    public void TouchZone_SplitsIntoFourQuadrants(int x, int y, TouchZone expected)
        => Assert.Equal(expected, InputTransform.ResolveTouchZone(true, x, y, 960, 540));

    [Fact]
    public void TouchZone_NotTouched_IsNone()
        => Assert.Equal(TouchZone.None, InputTransform.ResolveTouchZone(false, 100, 100, 960, 540));
}
