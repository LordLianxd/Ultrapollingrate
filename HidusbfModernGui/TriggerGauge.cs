using System;

namespace HidusbfModernGui
{
    // Geometria del arco de puntos que rodea cada gatillo en "Recorrido de los gatillos".
    //
    // Puro y sin WPF a proposito: es lo unico de esta pantalla que se puede probar sin mando ni
    // ventana, y es justo donde estaria un error silencioso - un arco que se llena al reves o
    // que se sale por un punto solo se ve mirandolo, y mirarlo exige hardware.
    //
    // El espejo del lado derecho NO vive aqui: lo hace el control con un ScaleTransform. Meterlo
    // en la matematica duplicaria cada caso de prueba para no ganar nada.
    public static class TriggerGauge
    {
        // Puntos del arco. Son los guiones del dibujo: discretos a proposito, porque un arco
        // continuo a este tamano se lee como una linea y pierde la sensacion de "marcador".
        public const int TickCount = 44;

        // Angulos en grados, medidos en el sentido de las agujas desde las 12. El arco arranca
        // abajo (donde el dedo empuja) y sube rodeando el gatillo, que es el sentido en que se
        // siente el recorrido.
        public const double StartAngleDeg = 200.0;
        public const double SweepDeg = 320.0;

        // Convierte el recorrido del gatillo en el angulo de la cabeza del arco.
        // Fuera de 0..1 se recorta; NaN se trata como 0.
        //
        // El guarda de NaN no es paranoia: hoy mismo, en RateStability, unas comparaciones que
        // parecian completas dejaban pasar valores no finitos porque en IEEE754 toda comparacion
        // con NaN es falsa. Aqui un NaN sin recortar saldria como un arco dibujado en la nada.
        public static double AngleFor(double value01)
            => StartAngleDeg + SweepDeg * Clamp01(value01);

        // Cuantos puntos van encendidos. Se redondea al mas cercano para que el ultimo punto se
        // encienda al llegar al tope y no un pelo antes.
        public static int LitTicks(double value01)
            => (int)Math.Round(Clamp01(value01) * TickCount, MidpointRounding.AwayFromZero);

        public static bool IsTickLit(int index, double value01)
            => index >= 0 && index < TickCount && index < LitTicks(value01);

        // El punto donde cae el umbral configurado, o null si no hay umbral.
        //
        // 0 devuelve null a proposito y no el punto cero: en esta pantalla 0% significa "hair
        // trigger apagado, el gatillo queda progresivo". Dibujar una marca en el origen diria
        // que hay un umbral en el 0%, que es una afirmacion distinta y falsa.
        public static int? ThresholdTick(double threshold01)
        {
            if (double.IsNaN(threshold01) || threshold01 <= 0) return null;
            int i = (int)Math.Round(Clamp01(threshold01) * (TickCount - 1), MidpointRounding.AwayFromZero);
            return i;
        }

        // Angulo de un punto del arco por su indice. El indice se recorta en vez de dejar que
        // se salga: un guion dibujado fuera del arco no se ve como un error, se ve como suciedad.
        //
        // Sin guarda de "TickCount <= 1": es constante y vale 44, asi que el compilador marca
        // esa rama como codigo muerto. Si algun dia TickCount pasa a ser configurable, hay que
        // volver a ponerla - la division de abajo se va a infinito con TickCount == 1.
        public static double AngleOfTick(int index)
        {
            double t = (double)Math.Clamp(index, 0, TickCount - 1) / (TickCount - 1);
            return StartAngleDeg + SweepDeg * t;
        }

        // Centro de un punto sobre la circunferencia, en coordenadas de pantalla (Y hacia abajo).
        // El -90 pasa de "0 grados = las 12" al 0 grados del coseno, que apunta a las 3.
        public static (double X, double Y) TickCentre(int index, double cx, double cy, double radius)
        {
            double rad = (AngleOfTick(index) - 90.0) * Math.PI / 180.0;
            return (cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
        }

        private static double Clamp01(double v)
            => double.IsNaN(v) ? 0.0 : Math.Clamp(v, 0.0, 1.0);
    }
}
