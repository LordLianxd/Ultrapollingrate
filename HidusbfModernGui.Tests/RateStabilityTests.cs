using HidusbfModernGui;
using Xunit;

public class RateStabilityTests
{
    [Fact]
    public void TooFewSamples_IsNoData()
        => Assert.Equal(RateSteadiness.NoData,
                        RateStability.Classify(0.125, 0.130, 0.135, RateStability.MinSamples - 1));

    [Fact]
    public void AtTheSampleFloor_ItDoesClassify()
        => Assert.Equal(RateSteadiness.Regular,
                        RateStability.Classify(0.125, 0.130, 0.135, RateStability.MinSamples));

    // Un mando a 8000 Hz cuyo p95 se sale un 4% de la mediana: eso es ir fino.
    [Fact]
    public void TightSpread_IsRegular()
        => Assert.Equal(RateSteadiness.Regular, RateStability.Classify(0.125, 0.130, 0.135, 500));

    // El p95 al doble de la mediana: uno de cada veinte reportes llega tardisimo.
    [Fact]
    public void WideSpread_IsIrregular()
        => Assert.Equal(RateSteadiness.Irregular, RateStability.Classify(0.125, 0.250, 0.260, 500));

    // El limite es inclusivo: justo en el techo todavia cuenta como regular, para que un
    // dispositivo que roza el umbral no parpadee entre los dos estados en cada refresco.
    [Fact]
    public void ExactlyAtTheCeiling_IsRegular()
        => Assert.Equal(RateSteadiness.Regular,
                        RateStability.Classify(1.0, RateStability.JitterCeiling, 1.4, 500));

    [Fact]
    public void JustOverTheCeiling_IsIrregular()
        => Assert.Equal(RateSteadiness.Irregular,
                        RateStability.Classify(1.0, RateStability.JitterCeiling + 0.01, 1.4, 500));

    // Numeros imposibles no se interpretan: una mediana cero o negativa significa que la
    // medida no sirve, no que el mando vaya infinitamente rapido.
    [Theory]
    [InlineData(0.0, 0.130)]
    [InlineData(-1.0, 0.130)]
    [InlineData(0.125, -0.1)]
    public void ImpossibleNumbers_AreNoData(double median, double p95)
        => Assert.Equal(RateSteadiness.NoData, RateStability.Classify(median, p95, 0.135, 500));

    // Un p95 por DEBAJO de la mediana es imposible por definicion; si llegara, no debe
    // leerse como "muy regular": es una medida rota.
    [Fact]
    public void P95BelowTheMedian_IsNoData()
        => Assert.Equal(RateSteadiness.NoData, RateStability.Classify(0.125, 0.100, 0.100, 500));

    // NaN hace que toda comparacion de falso, asi que sin guarda de finitud el valor se
    // cuela por todos los "if" y sale como Irregular: un diagnostico seguro sobre una
    // muestra rota. Cubre mediana NaN, p95 NaN y los dos a la vez.
    [Theory]
    [InlineData(double.NaN, 0.130)]
    [InlineData(0.125, double.NaN)]
    [InlineData(double.NaN, double.NaN)]
    public void NaN_IsNoData(double median, double p95)
        => Assert.Equal(RateSteadiness.NoData, RateStability.Classify(median, p95, 0.135, 500));

    // Infinity <= Infinity es verdadero en IEEE754, asi que sin guarda de finitud una
    // medida totalmente rota (mediana y p95 en +Infinity) pasaba todos los "if" y
    // terminaba en Regular: el chip mas tranquilizador para la medida mas rota posible.
    [Fact]
    public void BothInfinity_IsNoData()
        => Assert.Equal(RateSteadiness.NoData,
                        RateStability.Classify(double.PositiveInfinity, double.PositiveInfinity, 0.135, 500));

    // Un p95 en +Infinity con mediana finita tambien es una medida rota, no un flujo
    // erratico de verdad.
    [Fact]
    public void P95Infinity_IsNoData()
        => Assert.Equal(RateSteadiness.NoData,
                        RateStability.Classify(0.125, double.PositiveInfinity, 0.135, 500));

    // El p99 es un double mas: sin su propio guarda de finitud, NaN o Infinity en el se
    // cuelan por el resto de comparaciones igual que le pasaba antes a mediana y p95.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void P99NonFinite_IsNoData(double p99)
        => Assert.Equal(RateSteadiness.NoData, RateStability.Classify(0.125, 0.130, p99, 500));

    // El caso que motiva I2: un 5% de huecos de 1000 ms (19.7 Hz reales) con p95 = mediana.
    // El p95 no los ve -son menos del 5% de la ventana-, pero el p99 si: 1000 ms es 1000x
    // la mediana de 1 ms, muy por encima del techo de magnitud, asi que el respaldo lo
    // marca IRREGULAR aunque el veredicto por p95 solo hubiera dicho REGULAR.
    [Fact]
    public void CatastrophicHoles_AreCaughtByTheMagnitudeBackstop()
        => Assert.Equal(RateSteadiness.Irregular,
                        RateStability.Classify(1.0, 1.0, 1000.0, 1024));

    // Un unico pico del planificador (una captura real de DualSense: max/mediana = 2.63)
    // no debe disparar el respaldo de magnitud: sigue muy por debajo del techo de 10x, asi
    // que el veredicto lo sigue decidiendo el p95 de siempre.
    [Fact]
    public void SingleHiccup_StaysRegular_DespiteTheMagnitudeBackstop()
        => Assert.Equal(RateSteadiness.Regular,
                        RateStability.Classify(0.998, 1.030, 2.627, 500));

    // Justo en el techo de magnitud: inclusivo, igual que JitterCeiling, para que un
    // p99 que roza el limite no parpadee entre los dos estados.
    [Fact]
    public void ExactlyAtTheMagnitudeCeiling_IsRegular()
        => Assert.Equal(RateSteadiness.Regular,
                        RateStability.Classify(1.0, 1.0, RateStability.MagnitudeCeiling, 500));

    [Fact]
    public void JustOverTheMagnitudeCeiling_IsIrregular()
        => Assert.Equal(RateSteadiness.Irregular,
                        RateStability.Classify(1.0, 1.0, RateStability.MagnitudeCeiling + 0.01, 500));
}
