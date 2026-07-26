using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HidusbfModernGui
{
    // Selector HSB: vista previa, hex editable y tres barras (tono, saturacion, brillo).
    // La gente piensa en HSB - "el mismo azul pero mas oscuro" es un eje aqui y tres en RGB.
    public partial class ColourPicker : UserControl
    {
        private double _h = 240, _s = 1, _v = 1;

        // Set while this control writes SelectedColor itself, so its own update does not
        // come back through the property-changed callback and fight the drag.
        private bool _internal;

        public static readonly DependencyProperty SelectedColorProperty =
            DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(ColourPicker),
                new FrameworkPropertyMetadata(Colors.Blue,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

        public Color SelectedColor
        {
            get => (Color)GetValue(SelectedColorProperty);
            set => SetValue(SelectedColorProperty, value);
        }

        public event EventHandler? ColorChanged;

        public ColourPicker()
        {
            InitializeComponent();

            // El arcoiris del tono no depende de nada, asi que se construye una vez y no en cada Redraw.
            var arcoiris = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            foreach (var (offset, colour) in new (double, Color)[]
                     {
                         (0.000, Color.FromRgb(255, 0, 0)),   (0.167, Color.FromRgb(255, 255, 0)),
                         (0.333, Color.FromRgb(0, 255, 0)),   (0.500, Color.FromRgb(0, 255, 255)),
                         (0.667, Color.FromRgb(0, 0, 255)),   (0.833, Color.FromRgb(255, 0, 255)),
                         (1.000, Color.FromRgb(255, 0, 0)),
                     })
                arcoiris.GradientStops.Add(new GradientStop(colour, offset));
            HueBar.Background = arcoiris;

            Loaded += (_, _) => Redraw();
        }

        private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var picker = (ColourPicker)d;
            if (picker._internal) return;

            var c = (Color)e.NewValue;
            (picker._h, picker._s, picker._v) = ColourMath.RgbToHsv(c.R, c.G, c.B);
            picker.Redraw();
        }

        private void Emit()
        {
            var (r, g, b) = ColourMath.HsvToRgb(_h, _s, _v);

            // try/finally, because a stranded guard is silent. SelectedColor is a two-way
            // dependency property: once something binds to it, a throwing setter or a
            // coercion callback anywhere in that chain would leave _internal stuck true,
            // and the picker would stop resyncing to external writes forever with no
            // error to show for it.
            try
            {
                _internal = true;
                SelectedColor = Color.FromRgb(r, g, b);
            }
            finally { _internal = false; }

            ColorChanged?.Invoke(this, EventArgs.Empty);
        }

        // Redibuja barras, numeros, hex y vista previa a partir de _h/_s/_v. Bajo _internal para
        // que mover una barra por codigo no se lea como una edicion del usuario.
        private void Redraw()
        {
            _internal = true;
            try
            {
                HueBar.Value = _h;
                SatBar.Value = _s * 100;
                ValBar.Value = _v * 100;

                HueValue.Text = ((int)Math.Round(_h)).ToString();
                SatValue.Text = ((int)Math.Round(_s * 100)).ToString();
                ValValue.Text = ((int)Math.Round(_v * 100)).ToString();

                var (r, g, b) = ColourMath.HsvToRgb(_h, _s, _v);
                PreviewBand.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                HexBox.Text = ColourMath.ToHex(r, g, b);

                // Los fondos de saturacion y brillo se recalculan con el tono: una barra de
                // saturacion que sigue mostrando el rojo mientras el color es azul miente sobre
                // lo que va a pasar al arrastrarla.
                var (pr, pg, pb) = ColourMath.HsvToRgb(_h, 1, 1);
                var puro = Color.FromRgb(pr, pg, pb);
                SatBar.Background = Horizontal(Colors.White, puro);
                ValBar.Background = Horizontal(Colors.Black, puro);
            }
            finally { _internal = false; }
        }

        private static LinearGradientBrush Horizontal(Color from, Color to) =>
            new(from, to, new Point(0, 0.5), new Point(1, 0.5));

        private void Bar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_internal) return;
            _h = HueBar.Value;
            _s = SatBar.Value / 100.0;
            _v = ValBar.Value / 100.0;
            Redraw();
            Emit();
        }

        // El hex se aplica al pulsar Enter o al salir del campo, NO en cada tecla: aplicando por
        // tecla, escribir "F83E64" mandaria al mando los seis colores intermedios.
        private void Hex_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitHex();
            e.Handled = true;
        }

        private void Hex_LostFocus(object sender, RoutedEventArgs e) => CommitHex();

        private void CommitHex()
        {
            if (!ColourMath.TryParseHex(HexBox.Text, out byte r, out byte g, out byte b))
            {
                // Texto invalido: se devuelve el campo al color que SI esta puesto, en vez de
                // dejarlo con algo que no corresponde a nada.
                Redraw();
                return;
            }
            SelectedColor = Color.FromRgb(r, g, b);   // la DependencyProperty recalcula _h/_s/_v y redibuja
            Emit();
        }

        private void CopyHex_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(HexBox.Text); } catch { /* el portapapeles lo puede tener otro proceso */ }
        }
    }
}
