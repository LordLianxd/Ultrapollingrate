using System.Windows;
using System.Windows.Controls;

namespace HidusbfModernGui
{
    // Decide en runtime con que se dibuja el mando: si hay un skin valido instalado en
    // %APPDATA%\UltraPolling\skins\, se usa; si no (el caso por defecto y el de cualquier
    // skin roto), el mando vectorial propio. El resto de la app habla solo con este host:
    // Update() es identico en ambos caminos.
    public partial class PadVisualHost : UserControl
    {
        public PadVisualHost()
        {
            InitializeComponent();
            ReloadSkin();
        }

        public string StatusText { get; private set; } = "Mando vectorial";

        public void ReloadSkin()
        {
            bool ok = false;
            var dir = PadSkinLoader.FindFirstSkinDir(PadSkinLoader.DefaultSkinsRoot);
            if (dir != null)
            {
                var (skin, err) = PadSkinLoader.Load(dir);
                if (skin != null && Skinned.Load(skin))
                {
                    ok = true;
                    StatusText = $"Skin: {skin.Name}";
                }
                else
                {
                    StatusText = $"Skin invalido ({err ?? "no se pudo dibujar"}), usando el vectorial";
                }
            }
            else
            {
                StatusText = "Mando vectorial";
            }

            Skinned.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            Vector.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        }

        public void Update(ControllerState s)
        {
            if (Skinned.Visibility == Visibility.Visible) Skinned.Update(s);
            else Vector.Update(s);
        }

        private bool _streamerBackground;
        public bool StreamerBackground
        {
            get => _streamerBackground;
            set { _streamerBackground = value; Vector.StreamerBackground = value; Skinned.StreamerBackground = value; }
        }

        public bool ShowCalibration
        {
            get => Skinned.ShowCalibration;
            set => Skinned.ShowCalibration = value;
        }
    }
}
