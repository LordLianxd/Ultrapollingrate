using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HidusbfModernGui
{
    // Los dos modos del DIAGRAMA de asignacion de botones: el panel donde se mapea el mando.
    // No es un tema para toda la app: la app es oscura y se queda oscura. Aqui la lamina del
    // mando existe en dos versiones (tinta oscura sobre papel blanco, y al reves), y esto
    // dice cual se dibuja.
    public enum AppTheme { Noche, Dia }

    // Preferencias de la INTERFAZ, separadas del estado del mando a proposito: borrar la
    // intencion de luz (active.json) es un caso normal y no puede llevarse por delante lo
    // que el usuario eligio aqui.
    public sealed class UiPrefs
    {
        // Modo del diagrama de botones. Noche es el de siempre.
        public AppTheme Theme { get; set; } = AppTheme.Noche;
        public int ObsPort { get; set; } = UiPrefsStore.DefaultObsPort;

        // Si ya se enseno el globo que explica que la X no cierra, sino que manda la app a la
        // bandeja. Se ensena UNA vez en la vida de la instalacion: la primera vez es una
        // sorpresa que hay que explicar, la decima seria ruido.
        public bool TrayHintShown { get; set; }

        // Si la tarjeta ESPECIFICACIONES TECNICAS de la pagina de dispositivos esta desplegada.
        // Arranca plegada: son campos de diagnostico, no de uso diario. Se recuerda porque
        // quien la abre suele quererla abierta siempre, y volver a plegarla en cada arranque
        // seria pelearse con el usuario.
        public bool SpecsExpanded { get; set; }
    }

    // Espejo de los demas stores: mismo %APPDATA%\UltraPolling, escritura con copia
    // .backup, enums por nombre.
    public static class UiPrefsStore
    {
        // 8787 no es especial: es alto, libre en una instalacion tipica y facil de teclear.
        // Si esta ocupado, StreamerServer prueba los siguientes (ver Task 7).
        public const int DefaultObsPort = 8787;

        private static string? _overrideDir;

        internal static void OverrideDirectoryForTests(string? dir) => _overrideDir = dir;

        private static string Directory_ => _overrideDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraPolling");

        public static string Path => System.IO.Path.Combine(Directory_, "ui.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static UiPrefs Load()
        {
            try
            {
                if (!File.Exists(Path)) return new UiPrefs();
                string json = File.ReadAllText(Path);
                if (string.IsNullOrWhiteSpace(json)) return new UiPrefs();

                var p = JsonSerializer.Deserialize<UiPrefs>(json, Options) ?? new UiPrefs();
                // El puerto se sanea AQUI, al leer, para que ningun consumidor tenga que
                // volver a validarlo. 1024 es el limite de los puertos privilegiados.
                if (p.ObsPort < 1024 || p.ObsPort > 65535) p.ObsPort = DefaultObsPort;
                return p;
            }
            catch (Exception ex)
            {
                // Un ui.json roto jamas puede impedir que la app abra: se abre como siempre.
                Debug.WriteLine($"UiPrefsStore.Load fallo, valores por defecto: {ex.Message}");
                return new UiPrefs();
            }
        }

        public static OpResult Save(UiPrefs prefs)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory_);
                if (File.Exists(Path)) File.Copy(Path, Path + ".backup", true);
                File.WriteAllText(Path, JsonSerializer.Serialize(prefs, Options));
                return OpResult.Ok();
            }
            catch (Exception ex)
            {
                return OpResult.Fail($"No se pudieron guardar las preferencias: {ex.Message}");
            }
        }
    }
}
