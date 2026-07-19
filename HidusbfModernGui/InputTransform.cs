using System;
using System.Linq;

namespace HidusbfModernGui
{
    // Transformaciones puras del input. Sin WPF, sin hardware: entra un valor, sale otro.
    public static class InputTransform
    {
        // Deadzone radial (por magnitud, no por eje, para no deformar diagonales) + reescalado
        // entre inner y outer + curva de respuesta. Entrada y salida en -1..1.
        public static (double X, double Y) ApplyStick(StickInput s, double innerDeadzone,
                                                       double outerDeadzone, ResponseCurve curve)
            => ApplyStick(s, innerDeadzone, outerDeadzone, PresetExponent(curve));

        // Misma logica que la sobrecarga por enum, pero con el exponente de la curva pasado
        // directamente. Esto permite tanto los presets (Precisa/Normal/Rapida) como la curva
        // Personalizada, cuyo exponente sale del % de curvatura elegido por el usuario.
        public static (double X, double Y) ApplyStick(StickInput s, double innerDeadzone,
                                                       double outerDeadzone, double exponent)
        {
            double mag = Math.Sqrt(s.X * s.X + s.Y * s.Y);
            if (mag <= innerDeadzone || mag <= 0.0) return (0.0, 0.0);

            double outer = Math.Max(outerDeadzone, innerDeadzone + 1e-6);
            // Reescala [inner, outer] -> [0, 1].
            double t = (mag - innerDeadzone) / (outer - innerDeadzone);
            t = Math.Clamp(t, 0.0, 1.0);
            t = Math.Pow(t, exponent);

            double ux = s.X / mag, uy = s.Y / mag;   // direccion unitaria (preserva el angulo)
            return (ux * t, uy * t);
        }

        // Exponente fijo de cada preset. >1 = mas control fino cerca del centro (Precisa);
        // <1 = mas agresivo (Rapida); 1 = lineal (Normal). Personalizada no tiene exponente
        // propio aqui: su valor sale de CurvatureExponent segun el % elegido por el usuario,
        // asi que este metodo le da 1.0 (lineal) como neutro por si se usa sin ese contexto.
        public static double PresetExponent(ResponseCurve curve) => curve switch
        {
            ResponseCurve.Precisa => 1.8,
            ResponseCurve.Rapida  => 0.6,
            _                     => 1.0,   // Normal, Personalizada (ver RemapSettings.LeftCurveExponent)
        };

        // Convierte el % de curvatura (0..100) de la curva Personalizada en un exponente:
        // 0% = 2.0 (mas preciso cerca del centro), 50% = 1.0 (lineal), 100% = 0.5 (mas agresivo).
        public static double CurvatureExponent(int curvaturePct)
            => Math.Pow(2.0, (50 - Math.Clamp(curvaturePct, 0, 100)) / 50.0);

        // Funcion de forma: recibe la magnitud normalizada t (0..1, ya sin deadzone) y devuelve
        // la salida 0..1 segun la curva. Un solo lugar para las 6 curvas.
        public static double Shape(double t, ResponseCurve curve, int curvaturePct)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            switch (curve)
            {
                case ResponseCurve.Normal:   return t;
                case ResponseCurve.Precisa:  return Math.Pow(t, PresetExponent(ResponseCurve.Precisa));
                case ResponseCurve.Rapida:   return Math.Pow(t, PresetExponent(ResponseCurve.Rapida));
                case ResponseCurve.Personalizada: return Math.Pow(t, CurvatureExponent(curvaturePct));
                case ResponseCurve.Digital:  return t < 0.5 ? 0.0 : 1.0;
                case ResponseCurve.Dinamica:
                {
                    // Sigmoide simetrica: suave en el centro y al borde, empinada en medio.
                    if (t <= 0.0) return 0.0;
                    if (t >= 1.0) return 1.0;
                    const double k = 2.2;
                    double a = Math.Pow(t, k), b = Math.Pow(1.0 - t, k);
                    return a / (a + b);
                }
                case ResponseCurve.Propia: return t;
                default: return t;
            }
        }

        // Deadzone radial + rescale [inner,outer]->[0,1] + Shape por curva. La via que usa la app.
        public static (double X, double Y) ApplyStick(StickInput s, double innerDeadzone,
                                                      double outerDeadzone, ResponseCurve curve, int curvaturePct)
        {
            double mag = Math.Sqrt(s.X * s.X + s.Y * s.Y);
            if (mag <= innerDeadzone || mag <= 0.0) return (0.0, 0.0);
            double outer = Math.Max(outerDeadzone, innerDeadzone + 1e-6);
            double t = Math.Clamp((mag - innerDeadzone) / (outer - innerDeadzone), 0.0, 1.0);
            t = Shape(t, curve, curvaturePct);
            double ux = s.X / mag, uy = s.Y / mag;
            return (ux * t, uy * t);
        }

        // Hair trigger: por debajo del punto = 0; en el punto o mas = a fondo (1.0) de inmediato,
        // con point==0 como passthrough lineal (sin efecto hair-trigger).
        public static double ApplyTrigger(double value, double triggerPoint)
        {
            double p = Math.Clamp(triggerPoint, 0.0, 0.99);
            if (p <= 0.0) return Math.Clamp(value, 0.0, 1.0);
            return value < p ? 0.0 : 1.0;
        }

        public static PadButton Remap(PadButton pressed,
            System.Collections.Generic.IReadOnlyDictionary<PadButton, PadButton> table)
            => table != null && table.TryGetValue(pressed, out var to) ? to : pressed;

        public static TouchZone ResolveTouchZone(bool touched, int x, int y, int xSplit, int ySplit)
        {
            if (!touched) return TouchZone.None;
            bool left = x < xSplit, top = y < ySplit;
            return (top, left) switch
            {
                (true, true)   => TouchZone.ArribaIzq,
                (true, false)  => TouchZone.ArribaDer,
                (false, true)  => TouchZone.AbajoIzq,
                (false, false) => TouchZone.AbajoDer,
            };
        }

        // Curva Editor: interpolacion cubica monotona de Fritsch-Carlson (PCHIP) por los puntos
        // del usuario. Pasa exactamente por cada punto, es suave, y NUNCA sobreimpulsa (la
        // salida entre dos puntos queda dentro del rango de esos puntos): entre puntos vecinos
        // la mira jamas hace algo que el usuario no dibujo. Con <2 puntos degrada a lineal.
        public static double ShapeCustom(double t, System.Collections.Generic.IReadOnlyList<CurvePoint>? points)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            if (points == null || points.Count < 2) return t;

            var p = points.OrderBy(q => q.X).ToArray();
            int n = p.Length;
            if (t <= p[0].X) return Math.Clamp(p[0].Y, 0.0, 1.0);
            if (t >= p[n - 1].X) return Math.Clamp(p[n - 1].Y, 0.0, 1.0);

            // Secantes de cada tramo y tangentes en cada punto.
            var h = new double[n - 1];
            var delta = new double[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                h[i] = Math.Max(p[i + 1].X - p[i].X, 1e-9);
                delta[i] = (p[i + 1].Y - p[i].Y) / h[i];
            }
            var m = new double[n];
            m[0] = delta[0];
            m[n - 1] = delta[n - 2];
            for (int i = 1; i < n - 1; i++)
                m[i] = delta[i - 1] * delta[i] <= 0 ? 0.0 : (delta[i - 1] + delta[i]) / 2.0;

            // Limitador de Fritsch-Carlson: recorta las tangentes que producirian sobreimpulso.
            for (int i = 0; i < n - 1; i++)
            {
                if (delta[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
                double a = m[i] / delta[i], b = m[i + 1] / delta[i];
                double s = a * a + b * b;
                if (s > 9.0)
                {
                    double tau = 3.0 / Math.Sqrt(s);
                    m[i] = tau * a * delta[i];
                    m[i + 1] = tau * b * delta[i];
                }
            }

            // Evaluacion del hermite cubico en el tramo que contiene t.
            int k = 0;
            while (k < n - 2 && t > p[k + 1].X) k++;
            double u = (t - p[k].X) / h[k];
            double u2 = u * u, u3 = u2 * u;
            double y = p[k].Y * (2 * u3 - 3 * u2 + 1)
                     + h[k] * m[k] * (u3 - 2 * u2 + u)
                     + p[k + 1].Y * (-2 * u3 + 3 * u2)
                     + h[k] * m[k + 1] * (u3 - u2);
            return Math.Clamp(y, 0.0, 1.0);
        }

        // Shape con la curva Editor: Propia usa los puntos; el resto ignora points y delega.
        public static double Shape(double t, ResponseCurve curve, int curvaturePct,
                                   System.Collections.Generic.IReadOnlyList<CurvePoint>? points)
            => curve == ResponseCurve.Propia ? ShapeCustom(t, points) : Shape(t, curve, curvaturePct);

        // ApplyStick con puntos: identico al overload de curva+curvatura, pero la forma puede
        // ser la curva Editor. Es la via que usa RemapEngine.
        public static (double X, double Y) ApplyStick(StickInput s, double innerDeadzone,
            double outerDeadzone, ResponseCurve curve, int curvaturePct,
            System.Collections.Generic.IReadOnlyList<CurvePoint>? points)
        {
            double mag = Math.Sqrt(s.X * s.X + s.Y * s.Y);
            if (mag <= innerDeadzone || mag <= 0.0) return (0.0, 0.0);
            double outer = Math.Max(outerDeadzone, innerDeadzone + 1e-6);
            double t = Math.Clamp((mag - innerDeadzone) / (outer - innerDeadzone), 0.0, 1.0);
            t = Shape(t, curve, curvaturePct, points);
            double ux = s.X / mag, uy = s.Y / mag;
            return (ux * t, uy * t);
        }
    }
}
