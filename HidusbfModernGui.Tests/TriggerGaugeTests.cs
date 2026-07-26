using System;
using HidusbfModernGui;
using Xunit;

public class TriggerGaugeTests
{
    [Fact]
    public void AngleFor_AtRest_IsTheStart()
        => Assert.Equal(TriggerGauge.StartAngleDeg, TriggerGauge.AngleFor(0), 6);

    [Fact]
    public void AngleFor_FullyPressed_IsTheEnd()
        => Assert.Equal(TriggerGauge.StartAngleDeg + TriggerGauge.SweepDeg, TriggerGauge.AngleFor(1), 6);

    [Fact]
    public void AngleFor_Halfway_IsHalfTheSweep()
        => Assert.Equal(TriggerGauge.StartAngleDeg + TriggerGauge.SweepDeg / 2, TriggerGauge.AngleFor(0.5), 6);

    // El gatillo entrega 0..1, pero un valor sucio no puede sacar el arco de su recorrido:
    // se veria un punto dibujado fuera del dibujo del mando.
    [Theory]
    [InlineData(-0.5)]
    [InlineData(-9999)]
    public void AngleFor_BelowRange_ClampsToTheStart(double v)
        => Assert.Equal(TriggerGauge.StartAngleDeg, TriggerGauge.AngleFor(v), 6);

    [Theory]
    [InlineData(1.5)]
    [InlineData(9999)]
    public void AngleFor_AboveRange_ClampsToTheEnd(double v)
        => Assert.Equal(TriggerGauge.StartAngleDeg + TriggerGauge.SweepDeg, TriggerGauge.AngleFor(v), 6);

    // NaN se trata como reposo. En IEEE754 toda comparacion con NaN es falsa, asi que un
    // recorte escrito solo con comparaciones lo dejaria pasar y el arco se dibujaria en la nada.
    [Fact]
    public void AngleFor_NaN_IsTreatedAsRest()
        => Assert.Equal(TriggerGauge.StartAngleDeg, TriggerGauge.AngleFor(double.NaN), 6);

    [Fact]
    public void AngleFor_Infinity_ClampsInsteadOfEscaping()
    {
        Assert.Equal(TriggerGauge.StartAngleDeg + TriggerGauge.SweepDeg,
                     TriggerGauge.AngleFor(double.PositiveInfinity), 6);
        Assert.Equal(TriggerGauge.StartAngleDeg,
                     TriggerGauge.AngleFor(double.NegativeInfinity), 6);
    }

    [Fact]
    public void LitTicks_AtRest_IsNone()
        => Assert.Equal(0, TriggerGauge.LitTicks(0));

    [Fact]
    public void LitTicks_FullyPressed_IsAllOfThem()
        => Assert.Equal(TriggerGauge.TickCount, TriggerGauge.LitTicks(1));

    // Nunca puede encender mas puntos de los que hay: seria un indice fuera del array al dibujar.
    [Fact]
    public void LitTicks_NeverExceedsTheTickCount()
    {
        foreach (double v in new[] { 1.0, 1.5, 9999, double.PositiveInfinity })
            Assert.True(TriggerGauge.LitTicks(v) <= TriggerGauge.TickCount);
    }

    // Apretar mas nunca puede encender menos: si esto falla, el arco se veria retroceder.
    [Fact]
    public void LitTicks_IsMonotonic()
    {
        int previo = -1;
        for (int i = 0; i <= 100; i++)
        {
            int ahora = TriggerGauge.LitTicks(i / 100.0);
            Assert.True(ahora >= previo);
            previo = ahora;
        }
    }

    [Fact]
    public void IsTickLit_RejectsIndexesOutsideTheArc()
    {
        Assert.False(TriggerGauge.IsTickLit(-1, 1.0));
        Assert.False(TriggerGauge.IsTickLit(TriggerGauge.TickCount, 1.0));
    }

    // 0% significa "hair trigger apagado", no "umbral en el cero". Una marca dibujada en el
    // origen afirmaria que hay un umbral donde no lo hay.
    [Fact]
    public void ThresholdTick_AtZero_IsNoMark()
        => Assert.Null(TriggerGauge.ThresholdTick(0));

    [Fact]
    public void ThresholdTick_NaN_IsNoMark()
        => Assert.Null(TriggerGauge.ThresholdTick(double.NaN));

    [Fact]
    public void ThresholdTick_AtFull_IsTheLastTick()
        => Assert.Equal(TriggerGauge.TickCount - 1, TriggerGauge.ThresholdTick(1));

    [Fact]
    public void ThresholdTick_StaysInsideTheArc()
    {
        for (int i = 1; i <= 100; i++)
        {
            int? t = TriggerGauge.ThresholdTick(i / 100.0);
            Assert.NotNull(t);
            Assert.InRange(t!.Value, 0, TriggerGauge.TickCount - 1);
        }
    }

    [Fact]
    public void AngleOfTick_RunsFromStartToEnd()
    {
        Assert.Equal(TriggerGauge.StartAngleDeg, TriggerGauge.AngleOfTick(0), 6);
        Assert.Equal(TriggerGauge.StartAngleDeg + TriggerGauge.SweepDeg,
                     TriggerGauge.AngleOfTick(TriggerGauge.TickCount - 1), 6);
    }

    // Un indice pasado de rosca se recorta en vez de salirse del arco.
    [Fact]
    public void AngleOfTick_ClampsOutOfRangeIndexes()
    {
        Assert.Equal(TriggerGauge.AngleOfTick(0), TriggerGauge.AngleOfTick(-5), 6);
        Assert.Equal(TriggerGauge.AngleOfTick(TriggerGauge.TickCount - 1),
                     TriggerGauge.AngleOfTick(TriggerGauge.TickCount + 5), 6);
    }

    // El punto del inicio cae ABAJO: el arco arranca donde el dedo empuja. Con StartAngleDeg=200
    // (medido en el sentido de las agujas desde las 12) eso es abajo y a la izquierda, o sea
    // Y por debajo del centro en coordenadas de pantalla.
    [Fact]
    public void TickCentre_StartsBelowTheCentre()
    {
        var (x, y) = TriggerGauge.TickCentre(0, cx: 100, cy: 100, radius: 50);
        Assert.True(y > 100, $"el primer punto deberia caer por debajo del centro, salio Y={y}");
        Assert.True(x < 100, $"y a la izquierda, salio X={x}");
    }

    // Todos los puntos caen sobre la circunferencia: si alguno se sale, el arco no es un arco.
    [Fact]
    public void TickCentre_EveryTickSitsOnTheCircle()
    {
        for (int i = 0; i < TriggerGauge.TickCount; i++)
        {
            var (x, y) = TriggerGauge.TickCentre(i, cx: 0, cy: 0, radius: 50);
            Assert.Equal(50.0, Math.Sqrt(x * x + y * y), 6);
        }
    }
}
