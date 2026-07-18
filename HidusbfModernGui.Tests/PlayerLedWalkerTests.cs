using HidusbfModernGui;
using Xunit;

public class PlayerLedWalkerTests
{
    [Fact]
    public void Charge_CyclesOuterInnerOff()
    {
        var w = new PlayerLedWalker(PlayerLedEffect.Charge);
        Assert.Equal(4, w.FrameCount);
        Assert.Equal((byte)17, w.MaskAt(0));   // 1 y 4 (par exterior)
        Assert.Equal((byte)27, w.MaskAt(1));   // + 2 y 3
        Assert.Equal((byte)10, w.MaskAt(2));   // solo 2 y 3
        Assert.Equal((byte)0,  w.MaskAt(3));   // apagado
        Assert.Equal((byte)17, w.MaskAt(4));   // vuelve (wrap)
    }

    [Fact]
    public void Twinkle_SweepsOneLedBackAndForth()
    {
        var w = new PlayerLedWalker(PlayerLedEffect.Twinkle);
        Assert.Equal(new byte[] { 1, 2, 4, 8, 16, 8, 4, 2 },
            new[] { w.MaskAt(0), w.MaskAt(1), w.MaskAt(2), w.MaskAt(3),
                    w.MaskAt(4), w.MaskAt(5), w.MaskAt(6), w.MaskAt(7) });
        Assert.Equal(w.MaskAt(0), w.MaskAt(8));   // wrap
    }

    [Fact]
    public void None_IsAllOff()
    {
        var w = new PlayerLedWalker(PlayerLedEffect.None);
        Assert.Equal(0, w.FrameCount);
        Assert.Equal((byte)0, w.MaskAt(0));
        Assert.Equal((byte)0, w.MaskAt(99));
    }

    [Fact]
    public void MaskAt_WrapsNegative()
    {
        var w = new PlayerLedWalker(PlayerLedEffect.Charge);
        Assert.Equal(w.MaskAt(3), w.MaskAt(-1));
    }

    [Fact]
    public void FrameMs_IsPositiveForRealEffects()
    {
        Assert.True(PlayerLedWalker.FrameMsFor(PlayerLedEffect.Charge) > 0);
        Assert.True(PlayerLedWalker.FrameMsFor(PlayerLedEffect.Twinkle) > 0);
    }
}
