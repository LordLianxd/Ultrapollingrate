using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HidusbfModernGui
{
    // An HSV picker: a saturation/value square plus a hue strip. People think in HSV -
    // "the same blue but darker" is one axis here and three in RGB.
    public partial class ColourPicker : UserControl
    {
        private double _h = 240, _s = 1, _v = 1;
        private bool _draggingSv, _draggingHue;

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
            Loaded += (_, _) => Redraw();

            // Mouse capture can be taken away without a button-up ever arriving - Alt+Tab,
            // a system dialog. Without this the drag flag stays set, and the next mouse
            // move over the control with no button held would silently drag the colour.
            LostMouseCapture += (_, _) => { _draggingSv = false; _draggingHue = false; };
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

        private void Redraw()
        {
            if (HueLayer == null) return;

            // The square shows every shade of the current hue, so its base is that hue at
            // full saturation and value.
            var (hr, hg, hb) = ColourMath.HsvToRgb(_h, 1, 1);
            HueLayer.Fill = new SolidColorBrush(Color.FromRgb(hr, hg, hb));

            double w = SvSquare.ActualWidth, h = SvSquare.ActualHeight;
            if (w > 0 && h > 0)
                SvCursor.Margin = new Thickness(_s * w - 6, (1 - _v) * h - 6, 0, 0);

            if (HueStrip.ActualWidth > 0)
                HueCursor.Margin = new Thickness(_h / 360.0 * HueStrip.ActualWidth - 1.5, 0, 0, 0);
        }

        private void SetSvFrom(Point p)
        {
            double w = SvSquare.ActualWidth, h = SvSquare.ActualHeight;
            if (w <= 0 || h <= 0) return;

            _s = Math.Clamp(p.X / w, 0, 1);
            _v = Math.Clamp(1 - p.Y / h, 0, 1);
            Redraw();
            Emit();
        }

        private void SetHueFrom(Point p)
        {
            double w = HueStrip.ActualWidth;
            if (w <= 0) return;

            _h = Math.Clamp(p.X / w, 0, 1) * 360;
            Redraw();
            Emit();
        }

        // Capturing the mouse is what lets a drag continue past the edge of the square.
        // Without it the handle sticks the moment the pointer leaves.
        private void Sv_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = true;
            SvSquare.CaptureMouse();
            SetSvFrom(e.GetPosition(SvSquare));
        }

        private void Sv_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingSv) SetSvFrom(e.GetPosition(SvSquare));
        }

        private void Sv_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = false;
            SvSquare.ReleaseMouseCapture();
        }

        private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = true;
            HueStrip.CaptureMouse();
            SetHueFrom(e.GetPosition(HueStrip));
        }

        private void Hue_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingHue) SetHueFrom(e.GetPosition(HueStrip));
        }

        private void Hue_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = false;
            HueStrip.ReleaseMouseCapture();
        }
    }
}
