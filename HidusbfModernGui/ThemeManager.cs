using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace HidusbfModernGui
{
    // Cambia el tema MUTANDO el Color de los pinceles que ya estan en los recursos de la
    // aplicacion, en vez de reemplazarlos.
    //
    // Por que: los ~400 StaticResource del XAML resuelven UNA vez y se quedan con la
    // INSTANCIA del pincel. Reemplazar el recurso no los tocaria (habria que reabrir cada
    // ventana); cambiarle el Color, que es una DependencyProperty, los repinta a todos al
    // instante. Es lo que permite tener modo dia sin convertir 397 referencias a
    // DynamicResource ni recargar la ventana debajo del usuario.
    //
    // El unico enemigo es un pincel congelado (Freeze): es inmutable y se quedaria del
    // color viejo sin decir nada. Por eso Apply los devuelve en vez de tragarselos.
    public static class ThemeManager
    {
        public static AppTheme Current { get; private set; } = AppTheme.Noche;

        // Devuelve las claves que NO se pudieron aplicar (ausentes de los recursos o
        // congeladas). Lista vacia = tema aplicado entero.
        public static IReadOnlyList<string> Apply(AppTheme theme)
        {
            var failed = new List<string>();
            var res = Application.Current?.Resources;
            if (res == null) return ThemePalette.Keys;

            foreach (var entry in ThemePalette.For(theme))
            {
                if (res[entry.Key] is not SolidColorBrush brush || brush.IsFrozen)
                {
                    failed.Add(entry.Key);
                    continue;
                }
                brush.Color = (Color)ColorConverter.ConvertFromString(entry.Hex);
            }

            Current = theme;
            return failed;
        }
    }
}
