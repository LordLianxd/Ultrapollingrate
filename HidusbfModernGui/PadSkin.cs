using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Un skin del visualizador: una imagen BASE del mando y, por cada parte, de que archivo
    // sale, que region se recorta (Src) y donde se pega sobre la base (Dst). Todas las
    // coordenadas estan en PIXELES DE LA IMAGEN BASE - ese es el sistema de coordenadas del
    // skin, y el control lo escala entero con un Viewbox.
    //
    // El arte de un skin NO vive en el repo: los skins se instalan en
    // %APPDATA%\UltraPolling\skins\<nombre>\ (ver README). La app siempre funciona sin
    // ninguno: sin skin valido se dibuja el mando vectorial propio.
    public sealed class SkinRect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public bool IsValid => W > 0 && H > 0 && X >= 0 && Y >= 0;
    }

    public sealed class SkinPart
    {
        public string File { get; set; } = "";
        public SkinRect Src { get; set; } = new();      // region en el archivo de sprites
        public SkinRect? Src2 { get; set; }             // opcional: 2do estado (p.ej. stick pulsado)
        public SkinRect Dst { get; set; } = new();      // destino en pixeles de la base
    }

    public sealed class PadSkin
    {
        public string Name { get; set; } = "";
        public string BaseFile { get; set; } = "";
        public double BaseWidth { get; set; }
        public double BaseHeight { get; set; }
        // Radio (en pixeles de la base) que recorre el centro del stick de tope a tope.
        public double StickRadius { get; set; } = 20;
        public Dictionary<string, SkinPart> Parts { get; set; } = new();

        // Ruta absoluta del directorio del skin; la rellena el cargador.
        public string Directory { get; set; } = "";

        // Claves que el renderer entiende. Un skin puede definir todas, algunas o ninguna:
        // lo que no defina, simplemente no se dibuja (degradacion elegante).
        public static readonly string[] RequiredPartKeys =
        {
            "stick.left", "stick.right",
            "btn.cross", "btn.circle", "btn.square", "btn.triangle",
            "dpad.up", "dpad.down", "dpad.left", "dpad.right",
            "btn.l1", "btn.r1", "btn.l2", "btn.r2",
            "btn.share", "btn.options", "btn.ps", "btn.touchpad",
        };
    }

    public static class PadSkinLoader
    {
        public const string ManifestName = "skin.json";

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        // Carga y VALIDA un skin. Nunca lanza: devuelve (null, motivo) ante cualquier
        // problema, porque un skin roto jamas puede tumbar la app - solo se cae al
        // visualizador vectorial.
        public static (PadSkin? Skin, string? Error) Load(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir))
                    return (null, $"No existe la carpeta del skin: {dir}");

                string manifest = Path.Combine(dir, ManifestName);
                if (!File.Exists(manifest))
                    return (null, $"Falta {ManifestName} en {dir}");

                var skin = JsonSerializer.Deserialize<PadSkin>(File.ReadAllText(manifest), Options);
                if (skin == null) return (null, "El manifiesto del skin esta vacio.");

                if (skin.BaseWidth <= 0 || skin.BaseHeight <= 0)
                    return (null, "BaseWidth/BaseHeight deben ser mayores que cero.");

                if (string.IsNullOrWhiteSpace(skin.BaseFile) ||
                    !File.Exists(Path.Combine(dir, skin.BaseFile)))
                    return (null, $"Falta la imagen base del skin: {skin.BaseFile}");

                // Partes: se descarta la que apunte a un archivo inexistente o traiga
                // rectangulos invalidos, en vez de invalidar el skin entero.
                // OJO: System.Text.Json pisa el inicializador "= new()" con null si el
                // manifiesto trae "Parts": null explicito - sin este null-coalesce, un
                // skin por lo demas valido se rechazaba entero por una NullReferenceException.
                skin.Parts = (skin.Parts ?? new())
                    .Where(kv => kv.Value != null
                              && !string.IsNullOrWhiteSpace(kv.Value.File)
                              && File.Exists(Path.Combine(dir, kv.Value.File))
                              && kv.Value.Src.IsValid && kv.Value.Dst.IsValid)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                if (skin.StickRadius <= 0) skin.StickRadius = 20;
                skin.Directory = dir;
                if (string.IsNullOrWhiteSpace(skin.Name)) skin.Name = Path.GetFileName(dir);
                return (skin, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PadSkinLoader.Load fallo: {ex.Message}");
                return (null, $"No se pudo leer el skin: {ex.Message}");
            }
        }

        // Primer subdirectorio de skinsRoot que contenga un manifiesto (orden alfabetico,
        // estable). null si no hay ninguno - el caso normal de una instalacion limpia.
        public static string? FindFirstSkinDir(string skinsRoot)
        {
            try
            {
                if (!System.IO.Directory.Exists(skinsRoot)) return null;
                return System.IO.Directory.GetDirectories(skinsRoot)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, ManifestName)));
            }
            catch { return null; }
        }

        // Carpeta estandar de skins del usuario.
        public static string DefaultSkinsRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UltraPolling", "skins");
    }
}
