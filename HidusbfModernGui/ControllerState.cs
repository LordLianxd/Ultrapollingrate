namespace HidusbfModernGui
{
    // Estado normalizado del mando, independiente del hardware: sticks y gatillos en
    // punto flotante, botones por bandera. La capa de E/S traduce el reporte HID crudo
    // a esto, y el nucleo de transformacion trabaja solo con esto.
    public readonly record struct StickInput(double X, double Y);   // -1..1

    public enum ResponseCurve { Precisa, Normal, Rapida, Personalizada, Dinamica, Digital }

    // Cuadrante del touchpad tocado (None = sin toque). El remapeo asigna un boton por zona.
    public enum TouchZone { None, ArribaIzq, ArribaDer, AbajoIzq, AbajoDer }

    // Botones del mando (superset DS4/DualSense). El remapeo mapea de uno a otro.
    public enum PadButton
    {
        None,
        Cross, Circle, Square, Triangle,
        DpadUp, DpadDown, DpadLeft, DpadRight,
        L1, R1, L2, R2, L3, R3,
        Share, Options, PS, TouchpadClick
    }

    public sealed class ControllerState
    {
        public StickInput Left { get; set; }
        public StickInput Right { get; set; }
        public double L2 { get; set; }   // 0..1
        public double R2 { get; set; }   // 0..1
        public System.Collections.Generic.HashSet<PadButton> Pressed { get; set; } = new();
        // Touchpad: coordenadas crudas del primer toque y si hay toque.
        public bool TouchActive { get; set; }
        public int TouchX { get; set; }  // 0..1920 aprox
        public int TouchY { get; set; }  // 0..1080 aprox
    }
}
