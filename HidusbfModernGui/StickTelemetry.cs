using System;
using System.Collections.Generic;

namespace HidusbfModernGui
{
    // Que tan lejos del centro esta el stick cuando nadie lo toca. Unknown no es un fallo:
    // es no haber tenido todavia un tramo de reposo del que medir.
    public enum DriftLevel { Unknown, Ok, Leve, Alta }

    // El rastro visual, la deriva en reposo y la tasa de valores nuevos de un stick.
    //
    // Guarda POSICIONES, no tiempos: el rastro es un dibujo para la pantalla, no una medida.
    // La deriva en cambio si necesita saber "esto es reposo" antes de significar algo: medir
    // el centro mientras alguien mueve el stick daria un numero sin sentido, asi que se
    // congela en la ultima lectura buena hasta el siguiente tramo de reposo real.
    public sealed class StickTelemetry
    {
        // A 60 fps es un segundo de rastro: suficiente para ver la forma de un giro y corto
        // para que el circulo no se emborrone.
        public const int TrailLength = 120;

        // Muestras seguidas dentro de RestRadius para considerar que el stick esta quieto.
        public const int RestSamples = 30;

        // Radio que separa "quieto" de "en movimiento" para detectar el reposo.
        public const double RestRadius = 0.15;

        // Cuanto puede moverse la media entre la primera y la segunda mitad de la
        // ventana de RestSamples muestras y seguir contando como reposo. NO es una
        // tolerancia por muestra: una muestra sola, comparada contra la media de la
        // racha, no distingue temblor de movimiento, porque un barrido lento tiene
        // pasos mas chicos que el propio temblor. Lo que si los distingue es la
        // TENDENCIA: el temblor tiene una media estable de punta a punta de la
        // ventana; un movimiento, por lento que sea, mueve esa media. Por eso se
        // parte la ventana en dos mitades y se compara la media de cada una.
        public const double RestSpread = 0.02;

        // Un DualSense sano se queda muy por debajo de esto.
        public const double DriftOk = 0.02;

        // A partir de aqui la deriva ya se nota jugando.
        public const double DriftLeve = 0.05;

        private readonly (double X, double Y)[] _trail = new (double X, double Y)[TrailLength];
        private int _count;
        private int _head;

        // Anillo paralelo al de _trail: por cada hueco, si esa muestra tenia con que
        // compararse (_hasComparison) y si resulto distinta de la anterior (_isNew).
        // Con esto NewValuesPerSecond se calcula sobre las ultimas TrailLength
        // muestras, no sobre la vida entera del objeto, en O(1) por Push.
        private readonly bool[] _hasComparison = new bool[TrailLength];
        private readonly bool[] _isNew = new bool[TrailLength];
        private int _windowComparisons;
        private int _windowNewCount;

        private (double X, double Y)? _previous;

        public IReadOnlyList<(double X, double Y)> Trail
        {
            get
            {
                var result = new (double X, double Y)[_count];
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head - _count + i + _trail.Length) % _trail.Length;
                    result[i] = _trail[idx];
                }
                return result;
            }
        }

        public double DriftRadius { get; private set; }

        public DriftLevel Drift { get; private set; } = DriftLevel.Unknown;

        public void Push(double x, double y)
        {
            // Guarda escrita con IsFinite, no con comparaciones: en IEEE754 toda comparacion
            // con NaN da falso, asi que un guarda con solo < o > deja pasar NaN sin darse
            // cuenta. Este proyecto ya se comio ese fallo en RateStability.
            if (!double.IsFinite(x) || !double.IsFinite(y)) return;

            // El primer Push de la vida del objeto no tiene con que compararse.
            bool hasComparison = _previous.HasValue;
            bool isNew = hasComparison && (_previous!.Value.X != x || _previous.Value.Y != y);

            // Antes de pisar el hueco _head hay que sacar de la ventana lo que guardaba,
            // si es que ya habia algo (el anillo esta lleno una vez que _count llega a
            // TrailLength). Si no se hace esto la cuenta de la ventana crece sin parar
            // en vez de deslizarse.
            if (_count == _trail.Length && _hasComparison[_head])
            {
                _windowComparisons--;
                if (_isNew[_head]) _windowNewCount--;
            }

            _hasComparison[_head] = hasComparison;
            _isNew[_head] = isNew;
            if (hasComparison)
            {
                _windowComparisons++;
                if (isNew) _windowNewCount++;
            }

            _trail[_head] = (x, y);
            _head = (_head + 1) % _trail.Length;
            if (_count < _trail.Length) _count++;

            _previous = (x, y);

            UpdateDrift();
        }

        // Solo se mide la deriva cuando hay RestSamples muestras Y esas muestras dicen
        // "reposo". La version vieja de esto comparaba cada muestra nueva contra la
        // media de la racha (RestSpread como tolerancia POR MUESTRA) y cortaba la
        // racha entera al primer desvio. Eso se rompe con un potenciometro gastado que
        // alterna entre dos codigos de contacto cerca del centro: cada muestra se aleja
        // de la anterior mas que RestSpread, asi que la racha se corta en CADA Push y
        // Drift se queda en Unknown para siempre, aunque el stick no se este moviendo -
        // exactamente el pad roto que este indicador deberia atrapar. El problema de
        // fondo es que "quieto" contra "en movimiento" no se puede decidir muestra por
        // muestra: un contacto ruidoso en un stick quieto de verdad salta de muestra a
        // muestra tanto como (o mas que) un barrido lento y deliberado. Lo unico que
        // los distingue es la TENDENCIA a lo largo de la ventana: el ruido tiene una
        // media estable de punta a punta; el movimiento no.
        //
        // Por eso ahora se mira la ventana completa de las ultimas RestSamples
        // muestras: se parte en dos mitades y se compara la media de la primera contra
        // la de la segunda (RestSpread). Si difieren poco, es la misma tendencia -
        // reposo, aunque salte muestra a muestra. Ademas cada muestra de la ventana
        // debe seguir dentro de RestRadius del centro, para que un stick clavado a
        // fondo no cuente como quieto solo porque no se mueve.
        //
        // Se recalcula desde el anillo _trail en cada Push (O(RestSamples), no O(1)):
        // RestSamples es chico (30) asi que el costo es insignificante, y recorrer la
        // ventana entera deja el calculo obvio a simple vista en vez de tener que
        // confiar en una actualizacion incremental de dos mitades que se deslizan.
        //
        // Si la ventana no es reposo (se sale de RestRadius o la tendencia se mueve),
        // no se toca nada: la ultima lectura buena de Drift/DriftRadius se conserva tal
        // cual, en vez de recalcularse sobre ruido de movimiento.
        private void UpdateDrift()
        {
            if (_count < RestSamples) return;

            int half = RestSamples / 2;
            int second = RestSamples - half;
            double sumFirstX = 0, sumFirstY = 0, sumSecondX = 0, sumSecondY = 0;

            for (int i = 0; i < RestSamples; i++)
            {
                int idx = (_head - RestSamples + i + _trail.Length) % _trail.Length;
                var s = _trail[idx];
                double r = Math.Sqrt(s.X * s.X + s.Y * s.Y);
                if (r > RestRadius)
                {
                    // Al menos una muestra de la ventana esta lejos del centro: no es
                    // reposo, sea cual sea la tendencia.
                    return;
                }

                if (i < half) { sumFirstX += s.X; sumFirstY += s.Y; }
                else { sumSecondX += s.X; sumSecondY += s.Y; }
            }

            double meanFirstX = sumFirstX / half, meanFirstY = sumFirstY / half;
            double meanSecondX = sumSecondX / second, meanSecondY = sumSecondY / second;
            double dx = meanSecondX - meanFirstX, dy = meanSecondY - meanFirstY;
            double trend = Math.Sqrt(dx * dx + dy * dy);
            if (trend > RestSpread)
            {
                // La media se mueve de la primera mitad de la ventana a la segunda:
                // es tendencia, no ruido - el stick se esta moviendo de verdad.
                return;
            }

            double meanX = (sumFirstX + sumSecondX) / RestSamples;
            double meanY = (sumFirstY + sumSecondY) / RestSamples;
            double meanR = Math.Sqrt(meanX * meanX + meanY * meanY);
            DriftRadius = meanR;
            Drift = meanR <= DriftOk ? DriftLevel.Ok
                : meanR <= DriftLeve ? DriftLevel.Leve
                : DriftLevel.Alta;
        }

        // Cuenta cuantas muestras difieren de la anterior dentro de las ultimas
        // TrailLength (una ventana deslizante, no la vida entera del objeto) y lo
        // escala por la tasa de reportes: es la cifra que separa "llegan 8000 reportes"
        // de "el stick dice algo nuevo 8000 veces". Un promedio de toda la vida del
        // objeto se queda sordo despues de muchas muestras: a las 100000 muestras un
        // arranque nuevo de 10 muestras distintas apenas mueve la aguja, y esto es un
        // monitor EN VIVO. Una tasa de reportes imposible no puede inventar una tasa de
        // valores, asi que se responde 0.
        public double NewValuesPerSecond(double reportHz)
        {
            if (!double.IsFinite(reportHz) || reportHz <= 0) return 0.0;
            if (_windowComparisons <= 0) return 0.0;

            return (double)_windowNewCount / _windowComparisons * reportHz;
        }

        public void Reset()
        {
            _count = 0;
            _head = 0;
            Array.Clear(_hasComparison, 0, _hasComparison.Length);
            Array.Clear(_isNew, 0, _isNew.Length);
            _windowComparisons = 0;
            _windowNewCount = 0;
            _previous = null;
            DriftRadius = 0.0;
            Drift = DriftLevel.Unknown;
        }
    }
}
