namespace HidusbfModernGui
{
    // Que tan regular llega el flujo de reportes. NoData no es un fallo: es no haber medido
    // lo suficiente para decir nada, y decirlo es mejor que adivinar.
    //
    // Se habla de REGULARIDAD DEL FLUJO, no de estabilidad del mando: lo que se observa son
    // las horas de llegada de los reportes, y eso puede irregularizarse por el planificador
    // de Windows o por el hub USB sin que al mando le pase nada.
    public enum RateSteadiness { NoData, Regular, Irregular }

    // Compara el hueco del percentil 95 contra la mediana. Si el 95% de los reportes cae
    // dentro de un 35% del intervalo mediano, el flujo es regular.
    //
    // p95 y no el maximo -que RateSample ya trae-: un unico hueco, un pico del planificador
    // o una pausa del GC, dispara el maximo aunque los otros mil huecos sean perfectos.
    // Y no desviacion tipica: un punado de huecos enormes la disparan, y aqui lo que importa
    // es "casi todos llegan a tiempo", no la forma de la distribucion.
    //
    // Pero p95 es un estadistico de RANGO puro: solo cuenta CUANTOS huecos llegan tarde,
    // nunca CUAN tarde. Con la ventana de 1024 huecos los peores 51 son invisibles para el.
    // Un flujo con un 5% de huecos de 1000 ms (19.7 Hz reales) puede tener p95 = mediana y
    // salir REGULAR. Por eso hay un segundo guarda de MAGNITUD sobre el p99, que si ve esos
    // huecos: sirve de respaldo cuando el p95 se queda ciego.
    public static class RateStability
    {
        // Por debajo de esto no se clasifica.
        public const int MinSamples = 30;

        // Inclusivo a proposito: justo en el techo cuenta como regular, para que un
        // dispositivo que roza el umbral no parpadee entre los dos estados en cada refresco.
        public const double JitterCeiling = 1.35;

        // Respaldo de magnitud sobre el p99: un pico aislado del planificador no debe volcar
        // el veredicto (una captura real de DualSense tiene max/mediana = 2.63, muy por debajo
        // de esto), pero una ventana con huecos catastroficos de verdad si.
        public const double MagnitudeCeiling = 10.0;

        public static RateSteadiness Classify(double medianGapMs, double p95GapMs, double p99GapMs, int sampleCount)
        {
            if (sampleCount < MinSamples) return RateSteadiness.NoData;

            // Los guardas de abajo comparan numeros, y IEEE754 hace que toda comparacion
            // con NaN de falso y que Infinity <= Infinity de verdadero: un guarda que
            // parece cubrir "numeros imposibles" deja pasar NaN e Infinity sin tocarlos.
            // El p99 es un double mas: sin este mismo guarda reabre la misma puerta.
            if (!double.IsFinite(medianGapMs) || !double.IsFinite(p95GapMs) || !double.IsFinite(p99GapMs))
                return RateSteadiness.NoData;

            if (medianGapMs <= 0 || p95GapMs <= 0) return RateSteadiness.NoData;

            // Imposible por definicion: si llega, la medida esta rota y no se interpreta.
            if (p95GapMs < medianGapMs) return RateSteadiness.NoData;

            // El p95 no ve los huecos catastroficos si son menos del 5% de la ventana; el
            // p99 si. Esto se comprueba ANTES del veredicto normal para que un flujo asi no
            // se cuele como REGULAR solo porque el p95 esta limpio.
            if (p99GapMs > medianGapMs * MagnitudeCeiling) return RateSteadiness.Irregular;

            return p95GapMs <= medianGapMs * JitterCeiling
                ? RateSteadiness.Regular
                : RateSteadiness.Irregular;
        }
    }
}
