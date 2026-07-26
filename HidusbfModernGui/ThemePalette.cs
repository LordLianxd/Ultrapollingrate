using System;
using System.Collections.Generic;

namespace HidusbfModernGui
{
    // Los dos modos de la app. Noche es el de siempre; Dia es la MISMA disciplina
    // monocroma invertida, no una paleta nueva de colores.
    public enum AppTheme { Noche, Dia }

    // Una entrada de paleta: la clave del recurso y su color. Cadena hex y no un tipo de
    // WPF a proposito, para que esto sea dato puro y se pueda probar sin arrancar WPF.
    public readonly record struct PaletteEntry(string Key, string Hex);

    // La paleta de cada modo. Es la UNICA lista de colores de la app: si un color no esta
    // aqui, o es uno de los tres pinceles fijos del diagrama, no deberia existir.
    public static class ThemePalette
    {
        // Las claves que ThemeManager buscara en los recursos de la aplicacion. Deben
        // coincidir exactamente con los x:Key de Theme.xaml.
        public static readonly IReadOnlyList<string> Keys = new[]
        {
            "BgBrush", "SurfaceBrush", "SurfaceAltBrush", "BorderBrush",
            "TextDataBrush", "TextLabelBrush", "TextMutedBrush", "PadIdleBrush",
            "StatusOkBrush", "StatusWarnBrush", "StatusErrorBrush",
        };

        public static IReadOnlyList<PaletteEntry> For(AppTheme theme) =>
            theme == AppTheme.Dia ? Dia : Noche;

        // Modo noche: exactamente los valores con los que la app se diseno.
        private static readonly PaletteEntry[] Noche =
        {
            new("BgBrush",          "#000000"),
            new("SurfaceBrush",     "#0A0A0A"),
            new("SurfaceAltBrush",  "#111111"),
            new("BorderBrush",      "#1F1F1F"),
            new("TextDataBrush",    "#FFFFFF"),
            new("TextLabelBrush",   "#8A8A8A"),
            new("TextMutedBrush",   "#4A4A4A"),
            new("PadIdleBrush",     "#3A3A3A"),
            new("StatusOkBrush",    "#00C853"),
            new("StatusWarnBrush",  "#FFAB00"),
            new("StatusErrorBrush", "#FF3D00"),
        };

        // Modo dia. Ojo con dos cosas que NO son una inversion mecanica:
        //
        // - Las superficies van hacia ABAJO. Sobre negro un panel se separa aclarandose;
        //   sobre blanco tiene que oscurecerse, o no se ve que hay un panel.
        // - Los tres colores de estado bajan de luminosidad. #00C853 sobre blanco es casi
        //   ilegible; #00A344 dice lo mismo y se lee. Siguen codificando solo hechos del
        //   sistema, que es la unica licencia que tiene el color en esta app.
        private static readonly PaletteEntry[] Dia =
        {
            new("BgBrush",          "#FFFFFF"),
            new("SurfaceBrush",     "#F4F4F4"),
            new("SurfaceAltBrush",  "#E9E9E9"),
            new("BorderBrush",      "#D4D4D4"),
            new("TextDataBrush",    "#0A0A0A"),
            new("TextLabelBrush",   "#5C5C5C"),
            new("TextMutedBrush",   "#9C9C9C"),
            new("PadIdleBrush",     "#C6C6C6"),
            new("StatusOkBrush",    "#00A344"),
            new("StatusWarnBrush",  "#A66A00"),
            new("StatusErrorBrush", "#C62828"),
        };
    }
}
