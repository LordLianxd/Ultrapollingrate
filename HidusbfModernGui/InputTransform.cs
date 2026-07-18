using System;

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
    }
}
