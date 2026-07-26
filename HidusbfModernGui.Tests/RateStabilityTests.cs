using HidusbfModernGui;
using Xunit;

public class RateStabilityTests
{
    [Fact]
    public void TooFewSamples_IsNoData()
        => Assert.Equal(RateSteadiness.NoData,
                        RateStability.Classify(0.125, 0.130, RateStability.MinSamples - 1));

    [Fact]
    public void AtTheSampleFloor_ItDoesClassify()
        => Assert.Equal(RateSteadiness.Regular,
                        RateStability.Classify(0.125, 0.130, RateStability.MinSamples));

    // Un mando a 8000 Hz cuyo p95 se sale un 4% de la mediana: eso es ir fino.
    [Fact]
    public void TightSpread_IsRegular()
        => Assert.Equal(RateSteadiness.Regular, RateStability.Classify(0.125, 0.130, 500));

    // El p95 al doble de la mediana: uno de cada veinte reportes llega tardisimo.
    [Fact]
    public void WideSpread_IsIrregular()
        => Assert.Equal(RateSteadiness.Irregular, RateStability.Classify(0.125, 0.250, 500));

    // El limite es inclusivo: justo en el techo todavia cuenta como regular, para que un
    // dispositivo que roza el umbral no parpadee entre los dos estados en cada refresco.
    [Fact]
    public void ExactlyAtTheCeiling_IsRegular()
        => Assert.Equal(RateSteadiness.Regular,
                        RateStability.Classify(1.0, RateStability.JitterCeiling, 500));

    [Fact]
    public void JustOverTheCeiling_IsIrregular()
        => Assert.Equal(RateSteadiness.Irregular,
                        RateStability.Classify(1.0, RateStability.JitterCeiling + 0.01, 500));

    // Numeros imposibles no se interpretan: una mediana cero o negativa significa que la
    // medida no sirve, no que el mando vaya infinitamente rapido.
    [Theory]
    [InlineData(0.0, 0.130)]
    [InlineData(-1.0, 0.130)]
    [InlineData(0.125, -0.1)]
    public void ImpossibleNumbers_AreNoData(double median, double p95)
        => Assert.Equal(RateSteadiness.NoData, RateStability.Classify(median, p95, 500));

    // Un p95 por DEBAJO de la mediana es imposible por definicion; si llegara, no debe
    // leerse como "muy regular": es una medida rota.
    [Fact]
    public void P95BelowTheMedian_IsNoData()
        => Assert.Equal(RateSteadiness.NoData, RateStability.Classify(0.125, 0.100, 500));

    // NaN hace que toda comparacion de falso, asi que sin guarda de finitud el valor se
    // cuela por todos los "if" y sale como Irregular: un diagnostico seguro sobre una
    // muestra rota. Cubre mediana NaN, p95 NaN y los dos a la vez.
    [Theory]
    [InlineData(double.NaN, 0.130)]
    [InlineData(0.125, double.NaN)]
    [InlineData(double.NaN, double.NaN)]
    public void NaN_IsNoData(double median, double p95)
        => Assert.Equal(RateSteadiness.NoData, RateStability.Classify(median, p95, 500));

    // Infinity <= Infinity es verdadero en IEEE754, asi que sin guarda de finitud una
    // medida totalmente rota (mediana y p95 en +Infinity) pasaba todos los "if" y
    // terminaba en Regular: el chip mas tranquilizador para la medida mas rota posible.
    [Fact]
    public void BothInfinity_IsNoData()
        => Assert.Equal(RateSteadiness.NoData,
                        RateStability.Classify(double.PositiveInfinity, double.PositiveInfinity, 500));

    // Un p95 en +Infinity con mediana finita tambien es una medida rota, no un flujo
    // erratico de verdad.
    [Fact]
    public void P95Infinity_IsNoData()
        => Assert.Equal(RateSteadiness.NoData,
                        RateStability.Classify(0.125, double.PositiveInfinity, 500));
}
