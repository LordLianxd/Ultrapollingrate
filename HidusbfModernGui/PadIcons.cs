namespace HidusbfModernGui
{
    // El icono de cada boton, como datos de Geometry sobre un lienzo de 24x24. Vectores y no
    // caracteres Unicode: los glifos tipo "△" dependen de la fuente instalada, salen de
    // tamanos distintos entre si y no se parecen a los del mando. Un Geometry se dibuja igual
    // en cualquier equipo, escala sin perder filo y toma el color del tema.
    //
    // Reparto: caras y cruceta llevan forma; hombros, gatillos y sticks llevan TEXTO, porque
    // en el mando real estan serigrafiados "L1"/"R2"/"L3" - dibujarlos como simbolo seria
    // inventar un icono que nadie reconoce.
    public static class PadIcons
    {
        // Petalo de la cruceta apuntando ARRIBA, en 24x24: rectangulo de esquinas redondeadas
        // que termina en punta hacia el centro del mando. Las otras tres direcciones son la
        // misma forma girada 90/180/270 grados, escrita ya rotada para no depender de
        // transformaciones en la vista.
        private const string DpadUp    = "M9,2 H15 A2,2 0 0 1 17,4 V13 L12,18 L7,13 V4 A2,2 0 0 1 9,2 Z";
        private const string DpadDown  = "M9,22 H15 A2,2 0 0 0 17,20 V11 L12,6 L7,11 V20 A2,2 0 0 0 9,22 Z";
        private const string DpadLeft  = "M2,9 V15 A2,2 0 0 0 4,17 H13 L18,12 L13,7 H4 A2,2 0 0 0 2,9 Z";
        private const string DpadRight = "M22,9 V15 A2,2 0 0 1 20,17 H11 L6,12 L11,7 H20 A2,2 0 0 1 22,9 Z";

        // Simbolos de las caras. Van CALADOS sobre un circulo relleno (IsFilledBadge), como
        // en el mando: el trazo es el hueco, no la figura.
        private const string Cross    = "M7,7 L17,17 M17,7 L7,17";
        private const string Circle   = "M12,12 m-5,0 a5,5 0 1,0 10,0 a5,5 0 1,0 -10,0";
        private const string Square   = "M7.5,7.5 H16.5 V16.5 H7.5 Z";
        private const string Triangle = "M12,6.5 L17.5,16.5 H6.5 Z";

        // Share (dos rectangulos superpuestos) y Options (tres lineas), sus serigrafias.
        private const string Share   = "M5,9 H14 V19 H5 Z M10,5 H19 V15 H16";
        private const string Options = "M5,8 H19 M5,12 H19 M5,16 H19";

        public static string? PathOf(PadButton b) => b switch
        {
            PadButton.Cross     => Cross,
            PadButton.Circle    => Circle,
            PadButton.Square    => Square,
            PadButton.Triangle  => Triangle,
            PadButton.DpadUp    => DpadUp,
            PadButton.DpadDown  => DpadDown,
            PadButton.DpadLeft  => DpadLeft,
            PadButton.DpadRight => DpadRight,
            PadButton.Share     => Share,
            PadButton.Options   => Options,
            PadButton.TouchpadClick => "M3,7 H21 V17 H3 Z",
            _ => null,
        };

        public static string? TextOf(PadButton b) => b switch
        {
            PadButton.L1 => "L1",
            PadButton.R1 => "R1",
            PadButton.L2 => "L2",
            PadButton.R2 => "R2",
            PadButton.L3 => "L3",
            PadButton.R3 => "R3",
            PadButton.PS => "PS",
            _ => null,
        };

        // Las cuatro caras se dibujan como simbolo calado dentro de un circulo relleno; el
        // resto va suelto sobre el panel.
        public static bool IsFilledBadge(PadButton b) =>
            b is PadButton.Cross or PadButton.Circle or PadButton.Square or PadButton.Triangle;
    }
}
