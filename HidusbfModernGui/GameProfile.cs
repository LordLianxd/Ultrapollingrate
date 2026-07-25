using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Un perfil de juego: TODO lo que el usuario configura para una sesion, en un solo
    // sitio - la luz, la configuracion del mando y (opcional) la tasa de sondeo. Sustituye
    // a la pareja LightProfile + RemapProfile, que obligaba a guardar y cargar dos veces
    // cosas que en la practica van juntas ("mi perfil de Warzone").
    //
    // Cualquiera de las dos mitades puede ser null: un perfil que solo cambia la luz deja
    // Remap en null y no toca la configuracion del mando al cargarse, y viceversa.
    public sealed class GameProfile
    {
        public string Name { get; set; } = "";
        public int? Rate { get; set; }              // null = no tocar la tasa
        public LightIntent? Light { get; set; }     // null = no tocar la luz
        public RemapSettings? Remap { get; set; }   // null = no tocar el mando
    }

    // Espejo de los stores existentes: mismo %APPDATA%\UltraPolling, escritura atomica con
    // copia .backup, enums por nombre. Archivo propio (game-profiles.json).
    public static class GameProfileStore
    {
        // El remapeo guarda su ultimo estado como un pseudo-perfil con este nombre. Es
        // estado interno de la app, no un perfil del usuario: no se migra ni se lista.
        public const string LastUsedPseudoProfile = "__ultimo_usado__";

        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "game-profiles.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static List<GameProfile> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new List<GameProfile>();
                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new List<GameProfile>();
                return JsonSerializer.Deserialize<List<GameProfile>>(json, Options) ?? new List<GameProfile>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GameProfileStore.Load failed, starting empty: {ex.Message}");
                return new List<GameProfile>();
            }
        }

        public static OpResult Save(IEnumerable<GameProfile> profiles)
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
                return OpResult.Fail($"No se pudieron guardar los perfiles: {ex.Message}");
            }
        }

        // Funde los perfiles viejos en los nuevos. Pura (no toca disco) para poder probarla.
        // Los que comparten nombre (sin distinguir mayusculas) se unen en uno; los demas
        // entran con la mitad que tengan. Los archivos viejos NO se tocan: siguen en disco
        // como respaldo por si algo sale mal.
        public static List<GameProfile> Migrate(IEnumerable<LightProfile> light, IEnumerable<RemapProfile> remap)
        {
            var byName = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);

            foreach (var l in light ?? Enumerable.Empty<LightProfile>())
            {
                if (l == null || string.IsNullOrWhiteSpace(l.Name)) continue;
                var g = Get(byName, l.Name);
                g.Rate = l.Rate;
                g.Light = new LightIntent
                {
                    Kind = l.Rainbow ? LightIntentKind.Rainbow : LightIntentKind.Static,
                    R = l.R, G = l.G, B = l.B,
                    Player = l.Player,
                    Brightness = l.Brightness,
                };
            }

            foreach (var r in remap ?? Enumerable.Empty<RemapProfile>())
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Name)) continue;
                if (string.Equals(r.Name, LastUsedPseudoProfile, StringComparison.OrdinalIgnoreCase)) continue;
                Get(byName, r.Name).Remap = r.Settings;
            }

            return byName.Values.ToList();

            static GameProfile Get(Dictionary<string, GameProfile> map, string name)
            {
                if (!map.TryGetValue(name, out var g))
                {
                    g = new GameProfile { Name = name };
                    map[name] = g;
                }
                return g;
            }
        }
    }
}
