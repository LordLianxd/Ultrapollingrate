using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace HidusbfModernGui
{
    // Dibuja el mando a partir de un PadSkin: una imagen base + una capa por parte, cada
    // una recortada de su hoja de sprites (CroppedBitmap) y colocada en pixeles de la base.
    // Expone el MISMO Update(ControllerState) que PadVisual, asi el feed no distingue cual
    // de los dos esta activo.
    public partial class SkinnedPadVisual : UserControl
    {
        private PadSkin? _skin;
        private readonly Dictionary<string, Image> _layers = new();   // clave -> capa
        private readonly Dictionary<string, ImageSource> _alt = new(); // clave -> 2do estado (Src2)
        private readonly Dictionary<string, ImageSource> _main = new();
        private readonly List<UIElement> _calibration = new();
        private Image? _baseImage;

        public SkinnedPadVisual() => InitializeComponent();

        private bool _streamerBackground;
        public bool StreamerBackground
        {
            get => _streamerBackground;
            set
            {
                _streamerBackground = value;
                // El skin ya trae su propio fondo transparente (PNG con alfa): en modo
                // streamer solo hay que quitar el fondo del contenedor.
                RootSurface.Background = value ? Brushes.Transparent : (Brush)FindResource("SurfaceBrush");
            }
        }

        private bool _showCalibration;
        public bool ShowCalibration
        {
            get => _showCalibration;
            set { _showCalibration = value; foreach (var e in _calibration) e.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
        }

        // Construye las capas. Devuelve false si la base no se puede decodificar (skin
        // inservible -> el host cae al vectorial).
        public bool Load(PadSkin skin)
        {
            Board.Children.Clear();
            _layers.Clear(); _main.Clear(); _alt.Clear(); _calibration.Clear();
            _skin = skin;

            Board.Width = skin.BaseWidth;
            Board.Height = skin.BaseHeight;

            var baseSrc = TryLoadBitmap(Path.Combine(skin.Directory, skin.BaseFile));
            if (baseSrc == null)
            {
                // Sin base no hay skin: se deja el control en estado limpio (sin _skin) para
                // que un Update() posterior no crea que hay algo cargado. El host cae al
                // mando vectorial al ver el false.
                _skin = null;
                return false;
            }

            _baseImage = new Image { Source = baseSrc, Width = skin.BaseWidth, Height = skin.BaseHeight };
            Canvas.SetLeft(_baseImage, 0);
            Canvas.SetTop(_baseImage, 0);
            Board.Children.Add(_baseImage);

            foreach (var (key, part) in skin.Parts)
            {
                var sheet = TryLoadBitmap(Path.Combine(skin.Directory, part.File));
                if (sheet == null) continue;

                var main = TryCrop(sheet, part.Src);
                if (main == null) continue;

                var img = new Image
                {
                    Source = main,
                    Width = part.Dst.W,
                    Height = part.Dst.H,
                    Visibility = Visibility.Collapsed,   // los estados aparecen al pulsarse
                };
                Canvas.SetLeft(img, part.Dst.X);
                Canvas.SetTop(img, part.Dst.Y);
                Board.Children.Add(img);

                _layers[key] = img;
                _main[key] = main;
                if (part.Src2 != null && part.Src2.IsValid)
                {
                    var alt = TryCrop(sheet, part.Src2);
                    if (alt != null) _alt[key] = alt;
                }

                AddCalibrationBox(key, part.Dst);
            }

            // Los sticks son la unica capa SIEMPRE visible (se mueven, no parpadean).
            foreach (var k in new[] { "stick.left", "stick.right" })
                if (_layers.TryGetValue(k, out var s)) s.Visibility = Visibility.Visible;

            ShowCalibration = _showCalibration;
            return true;
        }

        // Contorno + etiqueta de una parte, oculto salvo en modo calibracion: permite ver
        // de un vistazo si un Dst del manifiesto esta bien puesto sobre la base.
        private void AddCalibrationBox(string key, SkinRect dst)
        {
            var box = new Rectangle
            {
                Width = dst.W, Height = dst.H,
                Stroke = Brushes.Magenta, StrokeThickness = 2,
                Fill = Brushes.Transparent, Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, dst.X); Canvas.SetTop(box, dst.Y);
            Board.Children.Add(box); _calibration.Add(box);

            var tag = new TextBlock
            {
                Text = key, Foreground = Brushes.Magenta, FontSize = 12,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            };
            Canvas.SetLeft(tag, dst.X); Canvas.SetTop(tag, Math.Max(0, dst.Y - 14));
            Board.Children.Add(tag); _calibration.Add(tag);
        }

        public void Update(ControllerState s)
        {
            if (_skin == null) return;

            MoveStick("stick.left", s.Left, s.Pressed.Contains(PadButton.L3));
            MoveStick("stick.right", s.Right, s.Pressed.Contains(PadButton.R3));

            var p = s.Pressed;
            Show("btn.cross", p.Contains(PadButton.Cross));
            Show("btn.circle", p.Contains(PadButton.Circle));
            Show("btn.square", p.Contains(PadButton.Square));
            Show("btn.triangle", p.Contains(PadButton.Triangle));
            Show("dpad.up", p.Contains(PadButton.DpadUp));
            Show("dpad.down", p.Contains(PadButton.DpadDown));
            Show("dpad.left", p.Contains(PadButton.DpadLeft));
            Show("dpad.right", p.Contains(PadButton.DpadRight));
            Show("btn.l1", p.Contains(PadButton.L1));
            Show("btn.r1", p.Contains(PadButton.R1));
            Show("btn.share", p.Contains(PadButton.Share));
            Show("btn.options", p.Contains(PadButton.Options));
            Show("btn.ps", p.Contains(PadButton.PS));
            Show("btn.touchpad", p.Contains(PadButton.TouchpadClick));

            // Gatillos analogicos: la capa aparece y su opacidad sigue el recorrido, asi se
            // ve "cuanto" esta apretado y no solo si/no.
            Fade("btn.l2", PadVisualMath.Fill01(s.L2));
            Fade("btn.r2", PadVisualMath.Fill01(s.R2));
        }

        private void MoveStick(string key, StickInput stick, bool pressed)
        {
            if (_skin == null || !_layers.TryGetValue(key, out var img)) return;
            if (!_skin.Parts.TryGetValue(key, out var part)) return;

            var (dx, dy) = PadVisualMath.StickOffset(stick.X, stick.Y, _skin.StickRadius);
            Canvas.SetLeft(img, part.Dst.X + dx);
            Canvas.SetTop(img, part.Dst.Y + dy);

            // Si el skin trae un 2do recorte para el stick pulsado (L3/R3), se alterna.
            if (_alt.TryGetValue(key, out var altSrc) && _main.TryGetValue(key, out var mainSrc))
                img.Source = pressed ? altSrc : mainSrc;
        }

        private void Show(string key, bool on)
        {
            if (_layers.TryGetValue(key, out var img))
            {
                img.Opacity = 1.0;
                img.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void Fade(string key, double amount)
        {
            if (!_layers.TryGetValue(key, out var img)) return;
            img.Visibility = amount > 0.01 ? Visibility.Visible : Visibility.Collapsed;
            img.Opacity = amount;
        }

        private static BitmapImage? TryLoadBitmap(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                // Cargar entero en memoria: el archivo del usuario no queda bloqueado y el
                // skin sobrevive a que lo muevan o lo borren mientras la app corre.
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // Recorte defensivo: un Src fuera de los limites de la hoja tiraria una excepcion
        // de CroppedBitmap, asi que se acota al tamano real antes de recortar.
        private static CroppedBitmap? TryCrop(BitmapSource sheet, SkinRect r)
        {
            try
            {
                int x = (int)Math.Round(r.X), y = (int)Math.Round(r.Y);
                int w = (int)Math.Round(r.W), h = (int)Math.Round(r.H);
                if (x >= sheet.PixelWidth || y >= sheet.PixelHeight) return null;
                w = Math.Min(w, sheet.PixelWidth - x);
                h = Math.Min(h, sheet.PixelHeight - y);
                if (w <= 0 || h <= 0) return null;
                var c = new CroppedBitmap(sheet, new Int32Rect(x, y, w, h));
                c.Freeze();
                return c;
            }
            catch { return null; }
        }
    }
}
