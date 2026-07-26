using System;
using System.Globalization;
using System.Linq;
using HidusbfModernGui;
using Xunit;

public class ThemePaletteTests
{
    // Un hex "#RRGGBB" a su luminancia relativa aproximada (0..255). Sirve para afirmar
    // "esto es mas claro que aquello" sin depender de WPF.
    private static double Luma(string hex)
    {
        int r = int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber);
        int g = int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber);
        int b = int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static string Hex(AppTheme t, string key)
        => ThemePalette.For(t).Single(e => e.Key == key).Hex;

    [Fact]
    public void BothThemes_DefineExactlyTheSameKeys()
    {
        var noche = ThemePalette.For(AppTheme.Noche).Select(e => e.Key).OrderBy(k => k);
        var dia = ThemePalette.For(AppTheme.Dia).Select(e => e.Key).OrderBy(k => k);
        Assert.Equal(noche, dia);
        // Y esas claves son justo las que ThemeManager va a buscar en los recursos.
        Assert.Equal(ThemePalette.Keys.OrderBy(k => k), noche);
    }

    [Fact]
    public void NoThemeRepeatsAKey()
    {
        foreach (AppTheme t in Enum.GetValues<AppTheme>())
        {
            var keys = ThemePalette.For(t).Select(e => e.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }
    }

    [Fact]
    public void EveryColour_IsASixDigitHex()
    {
        foreach (AppTheme t in Enum.GetValues<AppTheme>())
            foreach (var e in ThemePalette.For(t))
            {
                Assert.StartsWith("#", e.Hex);
                Assert.Equal(7, e.Hex.Length);
                Assert.True(int.TryParse(e.Hex.Substring(1), NumberStyles.HexNumber,
                                         CultureInfo.InvariantCulture, out _),
                            $"{t}/{e.Key} = '{e.Hex}' no es un hex de 6 digitos");
            }
    }

    // Guarda de regresion: el modo Noche DEBE reproducir la paleta que la app ya tiene.
    // Cambiar de tema y volver a Noche no puede dejar la app de otro color que al abrir.
    [Fact]
    public void Noche_MatchesTheShippedPalette()
    {
        Assert.Equal("#000000", Hex(AppTheme.Noche, "BgBrush"));
        Assert.Equal("#0A0A0A", Hex(AppTheme.Noche, "SurfaceBrush"));
        Assert.Equal("#111111", Hex(AppTheme.Noche, "SurfaceAltBrush"));
        Assert.Equal("#1F1F1F", Hex(AppTheme.Noche, "BorderBrush"));
        Assert.Equal("#FFFFFF", Hex(AppTheme.Noche, "TextDataBrush"));
        Assert.Equal("#8A8A8A", Hex(AppTheme.Noche, "TextLabelBrush"));
        Assert.Equal("#4A4A4A", Hex(AppTheme.Noche, "TextMutedBrush"));
        Assert.Equal("#3A3A3A", Hex(AppTheme.Noche, "PadIdleBrush"));
    }

    [Fact]
    public void Dia_InvertsBackgroundAndText()
    {
        Assert.True(Luma(Hex(AppTheme.Dia, "BgBrush")) > 200, "el fondo de dia debe ser claro");
        Assert.True(Luma(Hex(AppTheme.Dia, "TextDataBrush")) < 60, "el texto de dia debe ser oscuro");
    }

    // Sobre negro un panel es MAS claro que el fondo; sobre blanco tiene que ser MAS oscuro,
    // o desaparece. Esto es lo que impide "invertir" la paleta a ciegas.
    [Fact]
    public void Surfaces_SeparateFromTheBackgroundInBothThemes()
    {
        Assert.True(Luma(Hex(AppTheme.Noche, "SurfaceBrush")) > Luma(Hex(AppTheme.Noche, "BgBrush")));
        Assert.True(Luma(Hex(AppTheme.Dia, "SurfaceBrush")) < Luma(Hex(AppTheme.Dia, "BgBrush")));
    }

    // La jerarquia del texto (dato > etiqueta > apagado) tiene que sobrevivir al cambio.
    [Fact]
    public void TextHierarchy_HoldsInBothThemes()
    {
        foreach (AppTheme t in Enum.GetValues<AppTheme>())
        {
            double data = Luma(Hex(t, "TextDataBrush"));
            double label = Luma(Hex(t, "TextLabelBrush"));
            double muted = Luma(Hex(t, "TextMutedBrush"));
            double bg = Luma(Hex(t, "BgBrush"));
            // El dato es el que mas contrasta con el fondo; el apagado el que menos.
            Assert.True(Math.Abs(data - bg) > Math.Abs(label - bg), $"{t}: dato vs etiqueta");
            Assert.True(Math.Abs(label - bg) > Math.Abs(muted - bg), $"{t}: etiqueta vs apagado");
        }
    }

    [Fact]
    public void StatusColours_StayReadableAgainstTheirBackground()
    {
        foreach (AppTheme t in Enum.GetValues<AppTheme>())
        {
            double bg = Luma(Hex(t, "BgBrush"));
            foreach (var key in new[] { "StatusOkBrush", "StatusWarnBrush", "StatusErrorBrush" })
                Assert.True(Math.Abs(Luma(Hex(t, key)) - bg) > 40,
                            $"{t}/{key} se confunde con el fondo");
        }
    }
}
