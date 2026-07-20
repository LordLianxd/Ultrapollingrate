using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HidusbfModernGui
{
    // Dibuja un DualSense monocromo y refleja un ControllerState sobre el. La
    // geometria vive en PadVisual.xaml (Canvas de base fija 360x260, escalado por
    // un Viewbox); las constantes de aqui abajo son la lectura en codigo de esa
    // misma geometria - deben coincidir con donde se dibujaron los pozos de stick
    // y los marcos de gatillo, o los pulgares/barras quedan descentrados.
    public partial class PadVisual : UserControl
    {
        // Radio de desplazamiento del pulgar dentro del pozo (StickOffset). El pozo
        // dibujado (LeftStickWell/RightStickWell) tiene 80x80 -> radio 40, y el
        // pulgar (LeftThumb/RightThumb) 28x28 -> radio 14; 26+14=40 == radio del
        // pozo, asi que a fondo de recorrido el pulgar toca el borde del pozo sin
        // salirse.
        private const double StickRadius = 26;

        private static Brush Idle = Brushes.Transparent;
        private static readonly Brush Active = Brushes.White;

        public PadVisual()
        {
            InitializeComponent();
            Idle = (Brush)Application.Current.FindResource("PadIdleBrush");
            if (Idle.CanFreeze && !Idle.IsFrozen)
                Idle.Freeze();
        }

        private bool _streamerBackground;
        public bool StreamerBackground
        {
            get => _streamerBackground;
            set
            {
                _streamerBackground = value;
                // En modo streamer el cuerpo del mando tambien debe volverse
                // transparente (no solo el fondo del Border), o queda un rectangulo
                // solido casi negro tapando el video del juego.
                RootSurface.Background = value ? Brushes.Transparent
                    : (Brush)FindResource("SurfaceBrush");
                PadBody.Fill = value ? Brushes.Transparent
                    : (Brush)FindResource("SurfaceBrush");
            }
        }

        // Refleja un ControllerState (ya transformado) en el dibujo. Llamado por el feed a ~60fps.
        public void Update(ControllerState s)
        {
            var (lx, ly) = PadVisualMath.StickOffset(s.Left.X, s.Left.Y, StickRadius);
            Canvas.SetLeft(LeftThumb, LeftStickCenterX + lx - LeftThumb.Width / 2);
            Canvas.SetTop(LeftThumb, LeftStickCenterY + ly - LeftThumb.Height / 2);
            var (rx, ry) = PadVisualMath.StickOffset(s.Right.X, s.Right.Y, StickRadius);
            Canvas.SetLeft(RightThumb, RightStickCenterX + rx - RightThumb.Width / 2);
            Canvas.SetTop(RightThumb, RightStickCenterY + ry - RightThumb.Height / 2);

            var p = s.Pressed;
            Set(BtnCross, p.Contains(PadButton.Cross));
            Set(BtnCircle, p.Contains(PadButton.Circle));
            Set(BtnSquare, p.Contains(PadButton.Square));
            Set(BtnTriangle, p.Contains(PadButton.Triangle));
            Set(DpadUp, p.Contains(PadButton.DpadUp));
            Set(DpadDown, p.Contains(PadButton.DpadDown));
            Set(DpadLeft, p.Contains(PadButton.DpadLeft));
            Set(DpadRight, p.Contains(PadButton.DpadRight));
            Set(BtnL1, p.Contains(PadButton.L1));
            Set(BtnR1, p.Contains(PadButton.R1));
            Set(BtnShare, p.Contains(PadButton.Share));
            Set(BtnOptions, p.Contains(PadButton.Options));
            Set(BtnPS, p.Contains(PadButton.PS));
            Set(TouchpadArea, p.Contains(PadButton.TouchpadClick));

            // Gatillos: la barra crece con el valor analogico, desde ABAJO hacia
            // arriba (metafora de medidor/gauge convencional). Se fija Height y
            // luego se recalcula Canvas.Top para que el borde inferior de la barra
            // quede siempre pegado al borde inferior del marco (TriggerFrameBottom).
            L2Fill.Height = TriggerMaxHeight * PadVisualMath.Fill01(s.L2);
            Canvas.SetTop(L2Fill, TriggerFrameBottom - L2Fill.Height);
            R2Fill.Height = TriggerMaxHeight * PadVisualMath.Fill01(s.R2);
            Canvas.SetTop(R2Fill, TriggerFrameBottom - R2Fill.Height);
        }

        private static void Set(System.Windows.Shapes.Shape shape, bool on)
            => shape.Fill = on ? Active : Idle;

        // Centros de los pozos (coinciden con LeftStickWell/RightStickWell en el
        // XAML: Canvas.Left=92/188, Canvas.Top=110, 80x80 -> centro Left=132,
        // Right=228, ambos Top=150) y alto maximo de gatillo (coincide con la
        // Height=30 del marco de L2Fill/R2Fill en el XAML).
        private const double LeftStickCenterX = 132, LeftStickCenterY = 150;
        private const double RightStickCenterX = 228, RightStickCenterY = 150;
        private const double TriggerMaxHeight = 30;

        // Borde inferior del marco de gatillo (coincide con Canvas.Top="5" +
        // Height="30" del marco fijo de L2/R2 en el XAML). Las barras de relleno
        // crecen hacia arriba desde este Y, no hacia abajo desde el Top del marco.
        private const double TriggerFrameBottom = 5 + TriggerMaxHeight;
    }
}
