using System;
using System.Collections.Generic;
using System.Linq;

namespace HidusbfModernGui
{
    // Los valores AMIGABLES que ve el usuario (en %, preajustes) y su conversion a los
    // parametros precisos que consume InputTransform. La UI edita esto; el motor lee los
    // getters derivados. Clase mutable con props settables para round-trip de System.Text.Json.
    public sealed class RemapSettings
    {
        // Sticks (izquierdo)
        public int LeftDeadzonePct { get; set; } = 0;    // 0..30
        public int LeftReachPct { get; set; } = 100;     // 70..100 (avanzado)
        public ResponseCurve LeftCurve { get; set; } = ResponseCurve.Normal;
        public int LeftCurvaturePct { get; set; } = 50;  // 0..100, solo aplica con LeftCurve=Personalizada
        // Sticks (derecho)
        public int RightDeadzonePct { get; set; } = 0;
        public int RightReachPct { get; set; } = 100;
        public ResponseCurve RightCurve { get; set; } = ResponseCurve.Normal;
        public int RightCurvaturePct { get; set; } = 50; // 0..100, solo aplica con RightCurve=Personalizada
        // Gatillos
        public int L2PointPct { get; set; } = 0;         // 0..100
        public int R2PointPct { get; set; } = 0;
        // Remapeo y touchpad
        public Dictionary<PadButton, PadButton> ButtonRemap { get; set; } = new();
        public Dictionary<TouchZone, PadButton> TouchZoneMap { get; set; } = new();
        // Puntos de la curva "Editor" (ResponseCurve.Propia), en 0..1. Siempre 5: extremos
        // (0,0)/(1,1) fijos en la UI y 3 interiores arrastrables. Solo actuan cuando la curva
        // del stick es Propia; se guardan siempre (el usuario no pierde su dibujo al cambiar
        // de preset y volver).
        public List<CurvePoint> LeftCurvePoints { get; set; } = DefaultCurvePoints();
        public List<CurvePoint> RightCurvePoints { get; set; } = DefaultCurvePoints();

        public double LeftInnerDeadzone  => Math.Clamp(LeftDeadzonePct, 0, 30) / 100.0;
        public double LeftOuterDeadzone  => Math.Clamp(LeftReachPct, 70, 100) / 100.0;
        public double RightInnerDeadzone => Math.Clamp(RightDeadzonePct, 0, 30) / 100.0;
        public double RightOuterDeadzone => Math.Clamp(RightReachPct, 70, 100) / 100.0;
        public double L2Point => Math.Clamp(L2PointPct, 0, 100) / 100.0;
        public double R2Point => Math.Clamp(R2PointPct, 0, 100) / 100.0;

        // Exponente efectivo de la curva de respuesta: si el usuario eligio Personalizada,
        // sale de su % de curvatura; si no, es el exponente fijo del preset elegido.
        public double LeftCurveExponent => LeftCurve == ResponseCurve.Personalizada
            ? InputTransform.CurvatureExponent(LeftCurvaturePct)
            : InputTransform.PresetExponent(LeftCurve);
        public double RightCurveExponent => RightCurve == ResponseCurve.Personalizada
            ? InputTransform.CurvatureExponent(RightCurvaturePct)
            : InputTransform.PresetExponent(RightCurve);

        // Desde la v2 el combo de RESPUESTA solo ofrece Lineal y Editor. Los perfiles guardados
        // con los presets retirados (Precisa/Rapida/Dinamica/Digital/Personalizada) se degradan a
        // Lineal AL CARGAR - visible y honesto, en vez de dejar un combo sin seleccion o un motor
        // aplicando una curva que la UI ya no puede mostrar. El enum conserva los valores viejos
        // (los perfiles serializan nombres) y el motor sigue sabiendo aplicarlos; esta coercion es
        // la unica frontera.
        public void Sanitize()
        {
            if (LeftCurve != ResponseCurve.Normal && LeftCurve != ResponseCurve.Propia)
                LeftCurve = ResponseCurve.Normal;
            if (RightCurve != ResponseCurve.Normal && RightCurve != ResponseCurve.Propia)
                RightCurve = ResponseCurve.Normal;
            LeftCurvePoints = SanitizePoints(LeftCurvePoints);
            RightCurvePoints = SanitizePoints(RightCurvePoints);
        }

        // Devuelve SIEMPRE una lista valida de 5 puntos para el editor: extremos anclados en
        // (0,0)/(1,1), X estrictamente creciente (separacion minima 0.03, la misma que impone el
        // arrastre en la UI), Y en 0..1. Cualquier cosa irreparable (null, otro tamano) vuelve a
        // la diagonal por defecto. Internal para que CurveLibraryStore tambien la use al cargar
        // curvas de un JSON editado a mano.
        internal static List<CurvePoint> SanitizePoints(List<CurvePoint>? pts)
        {
            if (pts == null || pts.Count != 5) return DefaultCurvePoints();
            var s = pts.OrderBy(p => p.X).ToList();
            s[0] = new CurvePoint(0.0, 0.0);
            s[4] = new CurvePoint(1.0, 1.0);
            for (int i = 1; i <= 3; i++)
                s[i] = new CurvePoint(Math.Max(s[i].X, s[i - 1].X + 0.03), Math.Clamp(s[i].Y, 0.0, 1.0));
            for (int i = 3; i >= 1; i--)
                s[i] = new CurvePoint(Math.Min(s[i].X, s[i + 1].X - 0.03), s[i].Y);
            return s;
        }

        public static List<CurvePoint> DefaultCurvePoints() => new()
        {
            new(0, 0), new(0.25, 0.25), new(0.5, 0.5), new(0.75, 0.75), new(1, 1),
        };
    }
}
