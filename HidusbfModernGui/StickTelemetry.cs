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

        // Un DualSense sano se queda muy por debajo de esto.
        public const double DriftOk = 0.02;

        // A partir de aqui la deriva ya se nota jugando.
        public const double DriftLeve = 0.05;

        private readonly (double X, double Y)[] _trail = new (double X, double Y)[TrailLength];
        private int _count;
        private int _head;

        private (double X, double Y)? _previous;
        private int _newValueCount;
        private int _pushCount;

        private int _restRun;

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

            _trail[_head] = (x, y);
            _head = (_head + 1) % _trail.Length;
            if (_count < _trail.Length) _count++;

            _pushCount++;
            if (_previous is { } prev && (prev.X != x || prev.Y != y))
                _newValueCount++;
            _previous = (x, y);

            UpdateDrift(x, y);
        }

        // Solo se mide la deriva cuando hay RestSamples muestras seguidas dentro de
        // RestRadius del origen: eso es "reposo". Si el usuario esta moviendo el stick la
        // racha se corta y la ultima lectura buena de Drift/DriftRadius se conserva tal cual,
        // en vez de calcularse sobre ruido de movimiento.
        private void UpdateDrift(double x, double y)
        {
            double r = Math.Sqrt(x * x + y * y);
            if (r <= RestRadius)
            {
                _restRun++;
            }
            else
            {
                _restRun = 0;
                return;
            }

            if (_restRun < RestSamples) return;

            DriftRadius = r;
            Drift = r <= DriftOk ? DriftLevel.Ok
                : r <= DriftLeve ? DriftLevel.Leve
                : DriftLevel.Alta;
        }

        // Cuenta cuantas muestras difieren de la anterior y lo escala por la tasa de
        // reportes: es la cifra que separa "llegan 8000 reportes" de "el stick dice algo
        // nuevo 8000 veces". Una tasa de reportes imposible no puede inventar una tasa de
        // valores, asi que se responde 0.
        public double NewValuesPerSecond(double reportHz)
        {
            if (!double.IsFinite(reportHz) || reportHz <= 0) return 0.0;

            // El primer Push no tiene con que compararse, asi que las comparaciones
            // posibles son pushCount - 1, no pushCount.
            int comparisons = _pushCount - 1;
            if (comparisons <= 0) return 0.0;

            return (double)_newValueCount / comparisons * reportHz;
        }

        public void Reset()
        {
            _count = 0;
            _head = 0;
            _previous = null;
            _newValueCount = 0;
            _pushCount = 0;
            _restRun = 0;
            DriftRadius = 0.0;
            Drift = DriftLevel.Unknown;
        }
    }
}
