using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Los colores que el usuario guarda con el "+", en %APPDATA%\UltraPolling\palette.json.
    // Se guardan como hex de 6 digitos y no como objetos {R,G,B}: el archivo es legible y
    // editable a mano, que es media gracia de tenerlo en JSON.
    public static class PaletteStore
    {
        // Tope de 12. La fila es horizontal y vive dentro de una tarjeta: sin tope, el color
        // numero 30 empuja la tarjeta fuera de la ventana.
        public const int MaxColours = 12;

        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "palette.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static List<string> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new List<string>();
                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new List<string>();

                var raw = JsonSerializer.Deserialize<List<string>>(json, Options) ?? new List<string>();
                // Un archivo editado a mano puede traer basura mezclada. Se queda lo que sea
                // un color y se tira el resto, en vez de perder la paleta entera por una linea.
                return raw
                    .Where(s => ColourMath.TryParseHex(s, out _, out _, out _))
                    .Select(s => { ColourMath.TryParseHex(s, out byte r, out byte g, out byte b); return ColourMath.ToHex(r, g, b); })
                    .Take(MaxColours)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PaletteStore.Load fallo, paleta vacia: {ex.Message}");
                return new List<string>();
            }
        }

        public static OpResult Save(IEnumerable<string> colours)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);
                File.WriteAllText(Path, JsonSerializer.Serialize(colours, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudo guardar la paleta: {ex.Message}");
            }
        }

        // Pura, para poder probarla: normaliza, rechaza lo que no sea color y lo que ya este,
        // y al llegar al tope tira el mas viejo. Devuelve si el color entro.
        public static bool Add(List<string> current, string? hex)
        {
            if (current == null) return false;
            if (!ColourMath.TryParseHex(hex, out byte r, out byte g, out byte b)) return false;

            string norm = ColourMath.ToHex(r, g, b);
            if (current.Contains(norm, StringComparer.OrdinalIgnoreCase)) return false;

            current.Add(norm);
            while (current.Count > MaxColours) current.RemoveAt(0);
            return true;
        }
    }
}
