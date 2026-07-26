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

    // El caso que encontro la revision adversarial: caminar el stick hacia afuera un
    // paso chico a la vez (0.01, 0.02, ..., 0.14), sin salirse nunca de RestRadius
    // (0.15), hacia que la version vieja -que media la ULTIMA muestra, no la media de
    // la racha- terminara en Alta con solo 0.14 de desplazamiento. Eso es un
    // movimiento deliberado y lento, no una deriva; no debe sonar la alarma.
    [Fact]
    public void Drift_SlowDeliberateWalkWithinRestRadius_DoesNotReportAlta()
    {
        var t = EnReposo(0, 0);
        for (int i = 1; i <= 14; i++) t.Push(i / 100.0, 0);
        Assert.NotEqual(DriftLevel.Alta, t.Drift);
    }

    // El arreglo no puede quedarse ciego a una deriva real: un stick quieto de verdad,
    // con el temblor de mano tipico (unas milesimas), sobre un offset grande, debe
    // seguir reportando Alta.
    [Fact]
    public void Drift_GenuineRestWithTinyJitter_StillReportsAlta()
    {
        var t = new StickTelemetry();
        double[] jitter = { 0.0, 0.005, -0.005, 0.003, -0.003 };
        for (int i = 0; i < 60; i++)
            t.Push(0.12 + jitter[i % jitter.Length], 0);
        Assert.Equal(DriftLevel.Alta, t.Drift);
    }

    // Esta prueba antes se llamaba "RestRunBrokenPartway...DoesNotReport" y exigia
    // Unknown mientras una racha no terminara sin interrupciones: esa expectativa
    // pertenecia a la regla vieja por RACHA (tolerancia por muestra), la misma que
    // se reescribio porque no distingue ruido de movimiento. Fisicamente esta
    // secuencia es un stick quieto en 0.1 con UN salto de contacto ruidoso en el
    // medio, no un stick en movimiento: 25 muestras en 0.1, un pico a 0.13, otras 25
    // en 0.1. Con la regla por tendencia esa muestra suelta pesa 1/30 de la ventana
    // y no mueve la media de mitad a mitad, asi que la ventana SI es reposo y
    // reportar Alta con un radio cercano a 0.1 es la respuesta correcta. Negarse a
    // reportar por un solo valor atipico es la misma falla de "queda mudo" que
    // motivo reescribir la regla: un pad con un contacto sucio no reportaria nunca.
    [Fact]
    public void Drift_SingleOutlierDoesNotBlindTheIndicator()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < StickTelemetry.RestSamples - 5; i++) t.Push(0.1, 0);
        // Un pico de ruido de un solo contacto: se aleja de la media pero sigue
        // dentro de RestRadius.
        t.Push(0.1 + StickTelemetry.RestSpread * 1.5, 0);
        for (int i = 0; i < StickTelemetry.RestSamples - 5; i++) t.Push(0.1, 0);

        Assert.Equal(DriftLevel.Alta, t.Drift);
        Assert.Equal(0.1, t.DriftRadius, 2);
    }

    // NewValuesPerSecond debe ser una ventana deslizante, no un promedio de toda la
    // vida del objeto: muchisimas muestras identicas seguidas de una rafaga de
    // TrailLength muestras todas distintas debe dar una tasa cercana a la tasa de
    // reportes completa, no una tasa diluida por las muestras viejas.
    [Fact]
    public void NewValues_BurstAfterManyIdenticalPushes_IsNotDilutedByLifetimeAverage()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 100_000; i++) t.Push(0.5, 0.5);

        for (int i = 0; i < StickTelemetry.TrailLength; i++) t.Push(i / 1000.0, 0);

        double rate = t.NewValuesPerSecond(1000);
        Assert.True(rate > 900.0, $"se esperaba una tasa cercana a 1000, salio {rate}");
    }

    // El caso que encontro la revision adversarial sobre la version por RACHA: un
    // potenciometro gastado que alterna entre dos codigos de contacto cerca del
    // centro. Cada muestra se aleja de la anterior mas que RestSpread, asi que la
    // regla vieja (tolerancia por muestra contra la media de la racha) cortaba la
    // racha en CADA Push y Drift se quedaba en Unknown para siempre - exactamente el
    // pad roto que este indicador deberia atrapar. Esta es la prueba de regresion:
    // tiene que reportar Alta, no Unknown.
    [Fact]
    public void Drift_DeterministicAlternationNearRest_ReportsAltaNotUnknown()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 300; i++)
            t.Push(i % 2 == 0 ? 0.10 + 0.012 : 0.10 - 0.012, 0);

        Assert.Equal(DriftLevel.Alta, t.Drift);
        Assert.NotEqual(DriftLevel.Unknown, t.Drift);
    }

    // La misma alternancia determinista, a varias amplitudes alrededor de un offset
    // real: mientras la tendencia (media de mitad a mitad de la ventana) no se mueva,
    // debe seguir siendo reposo y reportar Alta, sea cual sea el tamano del salto
    // muestra a muestra.
    [Theory]
    [InlineData(0.01)]  // 0.5x RestSpread
    [InlineData(0.02)]  // 1x RestSpread
    [InlineData(0.04)]  // 2x RestSpread
    public void Drift_AlternationAtVariousAmplitudes_ReportsAlta(double amplitude)
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 300; i++)
            t.Push(i % 2 == 0 ? 0.10 + amplitude : 0.10 - amplitude, 0);

        Assert.Equal(DriftLevel.Alta, t.Drift);
    }

    // La caminata lenta y deliberada (el fallo original que motivo la version por
    // racha) tiene que seguir sin sonar la alarma con la regla nueva por tendencia:
    // las medias de las dos mitades de la ventana difieren de sobra por encima de
    // RestSpread mientras el stick recorre 0.01 a 0.14.
    [Fact]
    public void Drift_SlowWalk_StillDoesNotReportAlta_UnderTrendRule()
    {
        var t = EnReposo(0, 0);
        for (int i = 1; i <= 14; i++) t.Push(i / 100.0, 0);
        Assert.NotEqual(DriftLevel.Alta, t.Drift);
    }

    // Reposo genuino con temblor de mano de verdad (no una alternancia perfecta) sobre
    // un offset grande: la tendencia de ambas mitades de la ventana es la misma, asi
    // que debe seguir reportando Alta.
    [Fact]
    public void Drift_GenuineRestWithRandomishJitter_ReportsAlta()
    {
        var t = new StickTelemetry();
        var rng = new Random(12345);
        for (int i = 0; i < 300; i++)
        {
            double jitter = (rng.NextDouble() - 0.5) * 2 * 0.012;
            t.Push(0.10 + jitter, 0);
        }
        Assert.Equal(DriftLevel.Alta, t.Drift);
    }
}
