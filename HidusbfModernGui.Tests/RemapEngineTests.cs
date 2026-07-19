using System.Collections.Generic;
using HidusbfModernGui;
using Xunit;

// RemapEngine.Transform es la transformacion pura que enchufa RemapSettings entre el mando
// fisico (lo que ve el usuario en la UI) y el DS4 virtual. Aqui se prueba end-to-end que
// cada ajuste de la UI (deadzone, curva, hair-trigger, remapeo, zona del touchpad) llega
// de verdad a la salida. La I/O (reader/virtual/HidHide) se prueba a mano en el spike.
public class RemapEngineTests
{
    // ---- Sticks -------------------------------------------------------------

    [Fact]
    public void DefaultSettings_PassSticksThrough()
    {
        var s = new RemapSettings();   // deadzone 0, alcance 100, curva Normal
        var input = new ControllerState { Left = new StickInput(0.5, 0.3) };

        var outp = RemapEngine.Transform(input, s);

        Assert.Equal(0.5, outp.Left.X, 3);
        Assert.Equal(0.3, outp.Left.Y, 3);
    }

    [Fact]
    public void Deadzone_ZeroesSmallStickInput()
    {
        var s = new RemapSettings { LeftDeadzonePct = 20 };   // inner 0.20
        var input = new ControllerState { Left = new StickInput(0.10, 0.0) };

        var outp = RemapEngine.Transform(input, s);

        Assert.Equal(0.0, outp.Left.X, 3);
        Assert.Equal(0.0, outp.Left.Y, 3);
    }

    [Fact]
    public void Curve_IsAppliedToStick()
    {
        var s = new RemapSettings { LeftCurve = ResponseCurve.Precisa };   // exponente 1.8
        var input = new ControllerState { Left = new StickInput(0.5, 0.0) };

        var outp = RemapEngine.Transform(input, s);

        // Precisa suaviza el centro: 0.5 -> ~0.287, claramente por debajo del lineal.
        Assert.True(outp.Left.X < 0.5);
    }

    [Fact]
    public void RightStick_UsesRightSettings()
    {
        var s = new RemapSettings { RightDeadzonePct = 25 };   // inner 0.25, solo derecho
        var input = new ControllerState
        {
            Left = new StickInput(0.10, 0.0),   // el izquierdo no tiene deadzone: pasa
            Right = new StickInput(0.10, 0.0),  // el derecho cae en su deadzone: a cero
        };

        var outp = RemapEngine.Transform(input, s);

        Assert.True(outp.Left.X > 0.0);
        Assert.Equal(0.0, outp.Right.X, 3);
    }

    // ---- Gatillos -----------------------------------------------------------

    [Fact]
    public void HairTrigger_FiresFullBeforePhysicalMax()
    {
        var s = new RemapSettings { L2PointPct = 30 };          // dispara al 30%
        var input = new ControllerState { L2 = 0.5 };           // medio recorrido

        var outp = RemapEngine.Transform(input, s);

        Assert.Equal(1.0, outp.L2, 3);                          // analog a fondo
        Assert.Contains(PadButton.L2, outp.Pressed);           // y el boton L2 dispara
    }

    [Fact]
    public void HairTrigger_BelowPoint_IsZeroAndButtonReleased()
    {
        var s = new RemapSettings { L2PointPct = 30 };
        var input = new ControllerState { L2 = 0.2 };           // por debajo del punto

        var outp = RemapEngine.Transform(input, s);

        Assert.Equal(0.0, outp.L2, 3);
        Assert.DoesNotContain(PadButton.L2, outp.Pressed);
    }

    [Fact]
    public void Trigger_PassthroughWhenPointZero_KeepsPhysicalButton()
    {
        var s = new RemapSettings { L2PointPct = 0 };           // sin hair-trigger
        var input = new ControllerState
        {
            L2 = 0.4,
            Pressed = new HashSet<PadButton> { PadButton.L2 },  // el fisico ya lo marca
        };

        var outp = RemapEngine.Transform(input, s);

        Assert.Equal(0.4, outp.L2, 3);                          // analog tal cual
        Assert.Contains(PadButton.L2, outp.Pressed);           // se respeta el boton fisico
    }

    // ---- Remapeo de botones -------------------------------------------------

    [Fact]
    public void ButtonRemap_TranslatesPressedButton()
    {
        var s = new RemapSettings
        {
            ButtonRemap = new Dictionary<PadButton, PadButton> { [PadButton.Cross] = PadButton.Square },
        };
        var input = new ControllerState { Pressed = new HashSet<PadButton> { PadButton.Cross } };

        var outp = RemapEngine.Transform(input, s);

        Assert.Contains(PadButton.Square, outp.Pressed);
        Assert.DoesNotContain(PadButton.Cross, outp.Pressed);
    }

    [Fact]
    public void ButtonRemap_LeavesUnmappedButtonsAlone()
    {
        var s = new RemapSettings();   // tabla vacia
        var input = new ControllerState { Pressed = new HashSet<PadButton> { PadButton.Circle } };

        var outp = RemapEngine.Transform(input, s);

        Assert.Contains(PadButton.Circle, outp.Pressed);
    }

    // ---- Touchpad por zonas -------------------------------------------------

    [Fact]
    public void TouchZone_MapsToButton()
    {
        var s = new RemapSettings
        {
            TouchZoneMap = new Dictionary<TouchZone, PadButton> { [TouchZone.ArribaIzq] = PadButton.Triangle },
        };
        // Arriba-izquierda del touchpad ~1920x1080: x<960, y<540.
        var input = new ControllerState { TouchActive = true, TouchX = 100, TouchY = 100 };

        var outp = RemapEngine.Transform(input, s);

        Assert.Contains(PadButton.Triangle, outp.Pressed);
    }

    [Fact]
    public void TouchZone_NoTouch_AddsNothing()
    {
        var s = new RemapSettings
        {
            TouchZoneMap = new Dictionary<TouchZone, PadButton> { [TouchZone.ArribaIzq] = PadButton.Triangle },
        };
        var input = new ControllerState { TouchActive = false, TouchX = 100, TouchY = 100 };

        var outp = RemapEngine.Transform(input, s);

        Assert.DoesNotContain(PadButton.Triangle, outp.Pressed);
    }

    // ---- Defensivo ----------------------------------------------------------

    [Fact]
    public void NullSettings_ReturnsInputUnchanged()
    {
        var input = new ControllerState { Left = new StickInput(0.7, 0.0) };
        var outp = RemapEngine.Transform(input, null!);
        Assert.Equal(0.7, outp.Left.X, 3);
    }

    [Fact]
    public void CustomCurvePoints_ReachTheOutput()
    {
        var s = new RemapSettings
        {
            LeftCurve = ResponseCurve.Propia,
            LeftCurvePoints = new() { new(0, 0), new(0.5, 0.9), new(1, 1) },
        };
        var input = new ControllerState { Left = new StickInput(0.5, 0.0) };
        var outp = RemapEngine.Transform(input, s);
        Assert.Equal(0.9, outp.Left.X, 2);
    }
}
