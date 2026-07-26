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

    // 0.07 cae en el rango Leve nuevo (0.05, 0.10]; con los umbrales viejos (DriftOk
    // 0.02 / DriftLeve 0.05) el 0.035 original ya caia en Leve, pero con los nuevos
    // (calibrados con hardware real - ver comentario en StickTelemetry.DriftOk) ese
    // mismo 0.035 seria Ok, asi que hay que reapuntar el offset, no solo el nombre.
    [Fact]
    public void Drift_SmallOffset_IsLeve()
        => Assert.Equal(DriftLevel.Leve, EnReposo(0.07, 0).Drift);

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

    // Y una vez medida, moverse no la borra de inmediato: se conserva la ultima lectura
    // buena por un rato (DriftHoldPushes). Pero solo por un rato: una lectura que no se
    // puede reconfirmar deja de ser una lectura actual, y mostrarla como si lo fuera
    // seria mentir - la misma razon por la que el medidor de polling devuelve null en
    // vez de una tasa vieja, y la pastilla de regularidad se colapsa en vez de mostrar
    // una palabra que ya no corresponde al numero de al lado. Por eso un movimiento
    // breve conserva la lectura, pero un movimiento sostenido mas alla de
    // DriftHoldPushes la hace expirar a Unknown.
    [Fact]
    public void Drift_SurvivesBriefMovementThenExpires()
    {
        var t = EnReposo(0.12, 0);
        Assert.Equal(DriftLevel.Alta, t.Drift);

        // Rafaga corta, bien por debajo del plazo: la lectura se conserva.
        for (int i = 0; i < 50; i++) t.Push(-0.9, 0.4);
        Assert.Equal(DriftLevel.Alta, t.Drift);

        // Movimiento sostenido mas alla de DriftHoldPushes (sumado a los 50 de arriba):
        // se agoto el plazo sin reconfirmar, la lectura expira.
        for (int i = 0; i < StickTelemetry.DriftHoldPushes; i++) t.Push(-0.9, 0.4);
        Assert.Equal(DriftLevel.Unknown, t.Drift);
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
    // secuencia es un stick quieto en 0.11 con UN salto de contacto ruidoso en el
    // medio, no un stick en movimiento: 25 muestras en 0.11, un pico a 0.14, otras 25
    // en 0.11. Con la regla por tendencia esa muestra suelta pesa 1/30 de la ventana
    // y no mueve la media de mitad a mitad, asi que la ventana SI es reposo y
    // reportar Alta con un radio cercano a 0.11 es la respuesta correcta. Negarse a
    // reportar por un solo valor atipico es la misma falla de "queda mudo" que
    // motivo reescribir la regla: un pad con un contacto sucio no reportaria nunca.
    // Offset base 0.11 (no 0.1): con DriftLeve en 0.10, un offset de exactamente 0.1
    // cae justo en el borde Leve/Alta y el resultado dependeria de redondeo de punto
    // flotante en vez de la propiedad que este test quiere probar. 0.11 deja margen
    // real por encima de DriftLeve y por debajo de RestRadius (0.15) aun con el pico.
    [Fact]
    public void Drift_SingleOutlierDoesNotBlindTheIndicator()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < StickTelemetry.RestSamples - 5; i++) t.Push(0.11, 0);
        // Un pico de ruido de un solo contacto: se aleja de la media pero sigue
        // dentro de RestRadius.
        t.Push(0.11 + StickTelemetry.RestSpread * 1.5, 0);
        for (int i = 0; i < StickTelemetry.RestSamples - 5; i++) t.Push(0.11, 0);

        Assert.Equal(DriftLevel.Alta, t.Drift);
        Assert.Equal(0.11, t.DriftRadius, 2);
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
    // Base 0.13 (no 0.10): con DriftLeve en 0.10, un centro de alternancia en 0.10
    // promedia exactamente al borde Leve/Alta (los dos lados de la alternancia se
    // cancelan) en vez de caer claramente en Alta, que es lo que este test quiere
    // fijar.
    [Fact]
    public void Drift_DeterministicAlternationNearRest_ReportsAltaNotUnknown()
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 300; i++)
            t.Push(i % 2 == 0 ? 0.13 + 0.012 : 0.13 - 0.012, 0);

        Assert.Equal(DriftLevel.Alta, t.Drift);
        Assert.NotEqual(DriftLevel.Unknown, t.Drift);
    }

    // La misma alternancia determinista, a varias amplitudes alrededor de un offset
    // real: mientras la tendencia (media de mitad a mitad de la ventana) no se mueva,
    // debe seguir siendo reposo y reportar Alta, sea cual sea el tamano del salto
    // muestra a muestra.
    // Base 0.105, no 0.10 ni el 0.13 de los otros tests de alternancia: la amplitud
    // mas grande de este theory (0.04) empuja la muestra de arriba a base+amplitud, y
    // eso tiene que seguir por debajo de RestRadius (0.15) para que la ventana
    // califique como reposo. Con base 0.13 esa muestra llegaria a 0.17 y ninguna
    // ventana calificaria nunca (Drift se quedaria en Unknown, no en Alta). 0.105 deja
    // 0.005 de margen bajo RestRadius con la amplitud mas grande y otros 0.005 por
    // encima de DriftLeve (0.10) - un margen real, aunque angosto: es el hueco que
    // queda entre "recien por encima del umbral nuevo" y "el limite de RestRadius"
    // cuando la amplitud de la alternancia es grande. No hay forma de agrandar ese
    // margen sin bajar la amplitud maxima o subir RestRadius, y esta prueba no toca
    // ninguna de las dos.
    [Theory]
    [InlineData(0.01)]  // 0.5x RestSpread
    [InlineData(0.02)]  // 1x RestSpread
    [InlineData(0.04)]  // 2x RestSpread
    public void Drift_AlternationAtVariousAmplitudes_ReportsAlta(double amplitude)
    {
        var t = new StickTelemetry();
        for (int i = 0; i < 300; i++)
            t.Push(i % 2 == 0 ? 0.105 + amplitude : 0.105 - amplitude, 0);

        Assert.Equal(DriftLevel.Alta, t.Drift);
    }

    // Reposo genuino con temblor de mano de verdad (no una alternancia perfecta) sobre
    // un offset grande: la tendencia de ambas mitades de la ventana es la misma, asi
    // que debe seguir reportando Alta.
    // Base 0.13, no 0.10 (mismo motivo que en las otras pruebas de alternancia): con
    // DriftLeve en 0.10, un centro en 0.10 deja el resultado a merced de para donde
    // se incline el ruido aleatorio en cada ventana, en vez de fijar sin ambiguedad
    // que un reposo genuino con temblor reporta Alta.
    [Fact]
    public void Drift_GenuineRestWithRandomishJitter_ReportsAlta()
    {
        var t = new StickTelemetry();
        var rng = new Random(12345);
        for (int i = 0; i < 300; i++)
        {
            double jitter = (rng.NextDouble() - 0.5) * 2 * 0.012;
            t.Push(0.13 + jitter, 0);
        }
        Assert.Equal(DriftLevel.Alta, t.Drift);
    }

    // El defecto real que encontro la revision adversarial sobre la regla por
    // TENDENCIA: un barrido monotono mas lento que RestSpread/(RestSamples/2) (unos
    // 0.00133 por Push) mueve la media de mitad a mitad de la ventana menos que
    // RestSpread, asi que se lee como reposo y un pad sano que esta siendo barrido
    // reporta Alta falso. Ese falso Alta por si solo ya es malo, pero lo que de verdad
    // rompia el indicador era lo que pasaba despues: en cuanto el stick cruza
    // RestRadius ninguna ventana vuelve a calificar como reposo, asi que sin la
    // expiracion esa lectura falsa se quedaba clavada en Alta para siempre, incluso
    // con el stick bien lejos del centro y en movimiento activo. Este test es la
    // regresion de ese defecto: el barrido puede dar Alta falso en el camino, pero
    // para el final tiene que haber vuelto a Unknown, no quedarse pegado.
    [Fact]
    public void Drift_SlowWalkPastRestRadius_FalseAltaExpiresInsteadOfFreezing()
    {
        var t = EnReposo(0, 0);
        const double step = 0.00133;

        double x = 0;
        bool sawFalseAlta = false;
        while (x <= 0.9)
        {
            x += step;
            t.Push(x, 0);
            if (t.Drift == DriftLevel.Alta) sawFalseAlta = true;
        }
        Assert.True(sawFalseAlta, "se esperaba que el barrido diera Alta falso en algun punto del camino");

        // El stick sigue clavado lejos del centro (fuera de RestRadius): ninguna
        // ventana puede calificar como reposo. Basta con seguir empujando el mismo
        // valor hasta agotar DriftHoldPushes sin confirmar para que la lectura expire.
        for (int i = 0; i < StickTelemetry.DriftHoldPushes; i++) t.Push(x, 0);

        Assert.Equal(DriftLevel.Unknown, t.Drift);
    }

    // El mismo defecto al paso mas lento que encontro la revision (0.0001 por Push):
    // el piso de velocidad de la regla por tendencia es mas bajo todavia, asi que el
    // falso Alta aparece igual, y la expiracion tiene que sacarlo igual.
    [Fact]
    public void Drift_SlowerWalkPastRestRadius_FalseAltaExpiresInsteadOfFreezing()
    {
        var t = EnReposo(0, 0);
        const double step = 0.0001;

        double x = 0;
        while (x <= 0.9) { x += step; t.Push(x, 0); }

        for (int i = 0; i < StickTelemetry.DriftHoldPushes; i++) t.Push(x, 0);

        Assert.Equal(DriftLevel.Unknown, t.Drift);
    }

    // La expiracion no puede resolver el defecto cegando al indicador frente a una
    // deriva real: un stick genuinamente quieto con deriva grande tiene que seguir
    // reportando Alta indefinidamente mientras se queda quieto, mucho mas alla de
    // DriftHoldPushes - cada Push en reposo reconfirma la lectura y resetea el plazo,
    // asi que nunca deberia expirar. Si esto fallara, seria el peor desenlace posible:
    // un pad con deriva real que deja de avisar con solo esperar.
    [Fact]
    public void Drift_GenuineRestOverManyPushes_KeepsReportingAlta_DoesNotExpire()
    {
        var t = EnReposo(0.12, 0, StickTelemetry.DriftHoldPushes * 3);
        Assert.Equal(DriftLevel.Alta, t.Drift);
    }
}
