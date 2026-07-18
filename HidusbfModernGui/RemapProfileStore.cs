using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Un preset con nombre para el remapeador: la misma pareja (Name, Settings) que
    // LightProfile empareja (Name, color/rate), pero para RemapSettings.
    public sealed class RemapProfile
    {
        public string Name { get; set; } = "";
        public RemapSettings Settings { get; set; } = new();
    }

    // Espejo de ProfileStore/IntentStore: mismo %APPDATA%\UltraPolling, misma escritura
    // atomica con copia .backup, mismos Options (enums como nombre). Se duplica a proposito
    // para no tocar ProfileStore/LightProfile.cs ni IntentStore/LightIntent.cs, que estan
    // congelados.
    public static class RemapProfileStore
    {
        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "remap-profiles.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static List<RemapProfile> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new List<RemapProfile>();

                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new List<RemapProfile>();

                return JsonSerializer.Deserialize<List<RemapProfile>>(json, Options) ?? new List<RemapProfile>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RemapProfileStore.Load failed, starting empty: {ex.Message}");
                return new List<RemapProfile>();
            }
        }

        public static OpResult Save(IEnumerable<RemapProfile> profiles)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);
                File.WriteAllText(Path, JsonSerializer.Serialize(profiles, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudieron guardar los perfiles del remapeador: {ex.Message}");
            }
        }
    }
}
