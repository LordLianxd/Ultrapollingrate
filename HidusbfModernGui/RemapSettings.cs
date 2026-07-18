using System;
using System.Collections.Generic;

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
    }
}
