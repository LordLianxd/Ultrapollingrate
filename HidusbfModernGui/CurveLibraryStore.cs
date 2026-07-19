using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Una curva del Editor guardada con nombre por el usuario: los 5 puntos tal como los
    // dibujo. Independiente de los perfiles del remapeo: una curva se puede aplicar a
    // cualquier stick en cualquier momento desde "MIS CURVAS".
    public sealed class SavedCurve
    {
        public string Name { get; set; } = "";
        public List<CurvePoint> Points { get; set; } = RemapSettings.DefaultCurvePoints();
    }

    // Espejo de RemapProfileStore: mismo %APPDATA%\UltraPolling, misma escritura atomica
    // con copia .backup, mismos Options. Archivo propio (curves.json) para que borrar un
    // perfil nunca arrastre una curva y viceversa.
    public static class CurveLibraryStore
    {
        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "curves.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static List<SavedCurve> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new List<SavedCurve>();

                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new List<SavedCurve>();

                var list = JsonSerializer.Deserialize<List<SavedCurve>>(json, Options) ?? new List<SavedCurve>();
                // Un JSON editado a mano no puede romper la UI del editor (asume 5 puntos
                // ordenados con extremos fijos): se sanea con la misma regla que el resto.
                foreach (var c in list)
                    c.Points = RemapSettings.SanitizePoints(c.Points);
                return list;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CurveLibraryStore.Load failed, starting empty: {ex.Message}");
                return new List<SavedCurve>();
            }
        }

        public static OpResult Save(IEnumerable<SavedCurve> curves)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);
                File.WriteAllText(Path, JsonSerializer.Serialize(curves, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudieron guardar las curvas: {ex.Message}");
            }
        }
    }
}
