using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HidusbfModernGui
{
    // A saved setup. It carries the polling rate as well as the lights, because that is
    // the pairing nothing else offers: DSX will not touch the rate, and hidusbf knows
    // nothing about the lightbar.
    //
    // A mutable class rather than a record: System.Text.Json needs a parameterless
    // constructor and settable properties to round-trip this without extra ceremony.
    public sealed class LightProfile
    {
        public string Name { get; set; } = "";

        // Null means "do not touch the rate" - a profile that only changes the colour.
        public int? Rate { get; set; }

        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public PlayerLeds Player { get; set; } = PlayerLeds.Player1;
        public LedBrightness Brightness { get; set; } = LedBrightness.High;
        public bool Rainbow { get; set; }

        public LightState ToLightState() => new LightState(R, G, B, Player, Brightness);
    }

    // Profiles on disk as JSON, under %APPDATA%. Backed up before every write.
    public static class ProfileStore
    {
        private static string? _overrideDir;

        // Tests need a real directory to write to; this class exists to touch the disk,
        // so faking the filesystem would test nothing.
        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "profiles.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // Enums as names, so a hand-edited file reads "Player1" rather than "4" and
            // a future reordering of the enum cannot silently remap someone's profiles.
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static List<LightProfile> Load()
        {
            try
            {
                if (!File.Exists(Path)) return new List<LightProfile>();

                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new List<LightProfile>();

                return JsonSerializer.Deserialize<List<LightProfile>>(json, Options) ?? new List<LightProfile>();
            }
            catch (Exception ex)
            {
                // Losing the profiles is bad; refusing to launch because of a corrupt file
                // is worse. The backup beside it is the way back.
                Debug.WriteLine($"ProfileStore.Load failed, starting empty: {ex.Message}");
                return new List<LightProfile>();
            }
        }

        public static OpResult Save(IEnumerable<LightProfile> profiles)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);

                // Copy the old file aside before overwriting. A crash mid-write would
                // otherwise take every profile the user has with it.
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);

                File.WriteAllText(Path, JsonSerializer.Serialize(profiles, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudieron guardar los perfiles: {ex.Message}");
            }
        }
    }
}
