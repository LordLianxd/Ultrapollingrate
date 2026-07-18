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
        {
            double mag = Math.Sqrt(s.X * s.X + s.Y * s.Y);
            if (mag <= innerDeadzone || mag <= 0.0) return (0.0, 0.0);

            double outer = Math.Max(outerDeadzone, innerDeadzone + 1e-6);
            // Reescala [inner, outer] -> [0, 1].
            double t = (mag - innerDeadzone) / (outer - innerDeadzone);
            t = Math.Clamp(t, 0.0, 1.0);
            t = ApplyCurve(t, curve);

            double ux = s.X / mag, uy = s.Y / mag;   // direccion unitaria (preserva el angulo)
            return (ux * t, uy * t);
        }

        // Curva de respuesta como exponente sobre la magnitud normalizada. >1 = mas control
        // fino cerca del centro (Precisa); <1 = mas agresivo (Rapida); 1 = lineal (Normal).
        private static double ApplyCurve(double t, ResponseCurve curve) => curve switch
        {
            ResponseCurve.Precisa => Math.Pow(t, 1.8),
            ResponseCurve.Rapida  => Math.Pow(t, 0.6),
            _                     => t,
        };

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
