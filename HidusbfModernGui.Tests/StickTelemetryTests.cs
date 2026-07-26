using System.Linq;
using HidusbfModernGui;
using Xunit;

public class StickTelemetryTests
{
    private static StickTelemetry EnReposo(double x, double y, int muestras = 60)
    {
        var t = new StickTelemetry();
        for (int i = 0; i < muestras; i++) t.Push(x, y);
        return t;
    }

    [Fact]
    public void Trail_StartsEmpty() => Assert.Empty(new StickTelemetry().Trail);

    [Fact]
    public void Trail_NeverGrowsPastItsWindow()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < StickTelemetry.TrailLength * 3; i++) t.Push(0.1, 0.1);
        Assert.Equal(StickTelemetry.TrailLength, t.Trail.Count);
    }

    // El rastro se dibuja del mas viejo al mas nuevo: si el orden se invierte, la estela
    // sale por delante del punto.
    [Fact]
    public void Trail_KeepsTheNewestLast()
    {
        var t = new StickTelemetry();
        t.Push(0.1, 0); t.Push(0.2, 0); t.Push(0.3, 0);
        Assert.Equal(0.3, t.Trail.Last().X, 6);
        Assert.Equal(0.1, t.Trail.First().X, 6);
    }

    [Fact]
    public void Drift_BeforeAnyRest_IsUnknown()
        => Assert.Equal(DriftLevel.Unknown, new StickTelemetry().Drift);

    [Fact]
    public void Drift_PerfectlyCentred_IsOk()
    {
        var t = EnReposo(0, 0);
        Assert.Equal(DriftLevel.Ok, t.Drift);
        Assert.Equal(0.0, t.DriftRadius, 6);
    }

    [Fact]
    public void Drift_SmallOffset_IsLeve()
        => Assert.Equal(DriftLevel.Leve, EnReposo(0.035, 0).Drift);

    [Fact]
    public void Drift_BigOffset_IsAlta()
        => Assert.Equal(DriftLevel.Alta, EnReposo(0.12, 0).Drift);

    // Mientras el stick se MUEVE no hay reposo, asi que no se mide deriva: medir el centro
    // mientras alguien apunta daria un numero sin sentido.
    [Fact]
    public void Drift_WhileMoving_StaysUnknown()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 200; i++) t.Push(i % 2 == 0 ? -0.8 : 0.8, 0);
        Assert.Equal(DriftLevel.Unknown, t.Drift);
    }

    // Y una vez medida, moverse no la borra: se conserva la ultima lectura buena.
    [Fact]
    public void Drift_SurvivesLaterMovement()
    {
        var t = EnReposo(0.12, 0);
        Assert.Equal(DriftLevel.Alta, t.Drift);
        for (int i = 0; i < 50; i++) t.Push(-0.9, 0.4);
        Assert.Equal(DriftLevel.Alta, t.Drift);
    }

    [Fact]
    public void NewValues_AllIdentical_IsZero()
        => Assert.Equal(0.0, EnReposo(0.5, 0.5).NewValuesPerSecond(1000), 3);

    [Fact]
    public void NewValues_EveryPushDifferent_MatchesTheReportRate()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 100; i++) t.Push(i / 100.0, 0);
        Assert.Equal(1000.0, t.NewValuesPerSecond(1000), 1);
    }

    // La mitad de las muestras repiten: la tasa de valores nuevos es la mitad.
    [Fact]
    public void NewValues_HalfRepeated_IsHalfTheRate()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 100; i++) t.Push((i / 2) / 100.0, 0);
        Assert.Equal(500.0, t.NewValuesPerSecond(1000), 25.0);
    }

    // Una tasa de reportes imposible no puede producir una tasa de valores inventada.
    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NewValues_WithAnImpossibleReportRate_IsZero(double hz)
        => Assert.Equal(0.0, EnReposo(0.2, 0.2).NewValuesPerSecond(hz), 6);

    // Ejes no finitos no pueden entrar al rastro: un punto en NaN no se dibuja, se pierde.
    [Fact]
    public void Push_IgnoresNonFiniteSamples()
    {
        var t = new StickTelemetry();
        t.Push(double.NaN, 0);
        t.Push(0, double.PositiveInfinity);
        Assert.Empty(t.Trail);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var t = EnReposo(0.12, 0);
        t.Reset();
        Assert.Empty(t.Trail);
        Assert.Equal(DriftLevel.Unknown, t.Drift);
    }
}
