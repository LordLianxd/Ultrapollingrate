using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HidusbfModernGui
{
    public enum LightIntentKind { Static, Rainbow }

    // Lo ultimo que el usuario dejo puesto en el mando. A diferencia de LightProfile (un
    // preset con nombre que ademas guarda la tasa), esto es una sola cosa: el estado vivo
    // de la luz, para reaplicarlo al abrir la app y al reconectar el mando.
    //
    // Clase mutable con props settables por la misma razon que LightProfile: System.Text.Json
    // necesita constructor sin parametros para round-tripear sin ceremonia. Los campos de LED
    // (Player, Brightness) van siempre, tambien en modo Rainbow, porque el tick del rainbow
    // construye su LightState con ellos. El color por-tick NO se guarda: lo deriva el walker.
    public sealed class LightIntent
    {
        public LightIntentKind Kind { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public PlayerLeds Player { get; set; } = PlayerLeds.Player1;
        public LedBrightness Brightness { get; set; } = LedBrightness.High;
        public RainbowStyle Style { get; set; } = RainbowStyle.Smooth;
        public int RainbowColoursPerSecond { get; set; } = 64;

        public LightState ToLightState() => new LightState(R, G, B, Player, Brightness);

        public static LightIntent FromStatic(LightState s) => new LightIntent
        {
            Kind = LightIntentKind.Static,
            R = s.R, G = s.G, B = s.B, Player = s.Player, Brightness = s.Brightness
        };

        public static LightIntent FromRainbow(RainbowStyle style, int coloursPerSecond,
                                              PlayerLeds player, LedBrightness brightness) => new LightIntent
        {
            Kind = LightIntentKind.Rainbow,
            Style = style, RainbowColoursPerSecond = coloursPerSecond,
            Player = player, Brightness = brightness
        };
    }

    // Espejo de ProfileStore: mismo %APPDATA%\UltraPolling, misma escritura atomica con
    // copia .backup, mismos Options (enums como nombre). Se duplica a proposito para no
    // tocar ProfileStore/LightProfile.cs, que estan congelados.
    public static class IntentStore
    {
        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "active.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static LightIntent? Load()
        {
            try
            {
                if (!File.Exists(Path)) return null;
                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<LightIntent>(json, Options);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IntentStore.Load failed, ignoring: {ex.Message}");
                return null;
            }
        }

        public static OpResult Save(LightIntent intent)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);
                File.WriteAllText(Path, JsonSerializer.Serialize(intent, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudo guardar la intencion de luz: {ex.Message}");
            }
        }
    }
}
