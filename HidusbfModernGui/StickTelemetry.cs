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

        // Cuanto puede alejarse una muestra de la media de la racha actual y seguir
        // contando como parte del mismo reposo. Un temblor de mano real en un stick
        // quieto queda muy por debajo de esto; un movimiento deliberado, aunque sea
        // lento, lo supera enseguida. Sin este limite, "dentro de RestRadius del
        // centro" tambien deja pasar un barrido lento del stick de punta a punta:
        // radio-al-centro no es lo mismo que "las muestras estan quietas entre si".
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

        private int _restRun;
        private double _restMeanX;
        private double _restMeanY;

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

            UpdateDrift(x, y);
        }

        // Solo se mide la deriva cuando hay RestSamples muestras seguidas de reposo, y
        // "reposo" quiere decir dos cosas a la vez: cerca del centro (RestRadius, para
        // que un stick clavado a fondo no cuente como quieto) Y cerca de las demas
        // muestras de la racha (RestSpread, contra la media que se lleva de la racha).
        // Solo el radio-al-centro no alcanza: caminar el stick hacia afuera un paso
        // pequeno a la vez, sin salirse nunca de RestRadius, tambien pasaria esa prueba
        // aunque el stick se este moviendo de verdad. La media es incremental para no
        // recorrer la racha en cada Push. Si el usuario mueve el stick la racha se
        // corta y la ultima lectura buena de Drift/DriftRadius se conserva tal cual, en
        // vez de recalcularse sobre ruido de movimiento.
        private void UpdateDrift(double x, double y)
        {
            double r = Math.Sqrt(x * x + y * y);
            if (r > RestRadius)
            {
                _restRun = 0;
                return;
            }

            if (_restRun > 0)
            {
                double dx = x - _restMeanX;
                double dy = y - _restMeanY;
                double spread = Math.Sqrt(dx * dx + dy * dy);
                if (spread > RestSpread)
                {
                    // Se aleja demasiado de la media de la racha actual: no es la misma
                    // racha de reposo, aunque siga dentro de RestRadius del centro.
                    _restRun = 0;
                }
            }

            if (_restRun == 0)
            {
                _restMeanX = x;
                _restMeanY = y;
                _restRun = 1;
            }
            else
            {
                _restRun++;
                _restMeanX += (x - _restMeanX) / _restRun;
                _restMeanY += (y - _restMeanY) / _restRun;
            }

            if (_restRun < RestSamples) return;

            double meanR = Math.Sqrt(_restMeanX * _restMeanX + _restMeanY * _restMeanY);
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
            _restRun = 0;
            _restMeanX = 0.0;
            _restMeanY = 0.0;
            DriftRadius = 0.0;
            Drift = DriftLevel.Unknown;
        }
    }
}
