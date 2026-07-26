using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HidusbfModernGui
{
    // Monitor 2D en vivo de un stick: circulo exterior, anillo de zona muerta, circulo de
    // alcance, cruz de ejes, rastro y punto vivo. El control es dueno de su propio
    // StickTelemetry (Task 1, ya bajo test): quien lo consume solo empuja muestras con
    // Push y lee Drift, sin manejar un segundo objeto por stick.
    //
    // La posicion de cada punto (rastro y punto vivo) sale de PadVisualMath.StickOffset,
    // la MISMA cuenta que ya usa PadVisual para el pulgar del mando en vivo. Dos dibujos
    // del mismo stick no pueden convertir x/y a pixeles con dos formulas distintas.
    //
    // Push corre hasta 60 veces por segundo, asi que el arbol visual (circulos, ejes) se
    // construye UNA sola vez en Loaded (ver Build). Por muestra solo se mueve el punto
    // vivo y se reasignan los puntos de los segmentos del rastro - nunca se crea o destruye
    // una forma.
    public partial class StickMonitor : UserControl
    {
        private const double CanvasSize = 240;
        private const double Center = CanvasSize / 2;

        // Deja aire para que el punto vivo (7 px) y el circulo exterior no toquen el borde
        // del canvas cuando el stick esta a fondo de recorrido.
        private const double MaxRadius = Center - 14;

        private const double DotSize = 7;

        // El rastro se dibuja en varios segmentos de opacidad creciente (el mas viejo, mas
        // tenue) en vez de un unico Polyline: WPF no tiene opacidad por punto dentro de una
        // sola forma, asi que "la opacidad cae del punto actual hacia atras" (el pedido del
        // brief) solo se puede aproximar partiendo el rastro. Los segmentos se crean una vez
        // en Build y solo se les reasignan los puntos en cada Push - la forma en si no cambia.
        private const int TrailSegments = 8;

        private readonly StickTelemetry _telemetry = new();
        private readonly Polyline?[] _trailParts = new Polyline?[TrailSegments];

        private Ellipse? _deadzoneRing;
        private Ellipse? _reachRing;
        private Ellipse? _liveDot;
        private bool _built;

        private double _innerDeadzone;
        private double _outerReach = 1.0;

        public StickMonitor()
        {
            InitializeComponent();
            Loaded += (s, e) => Build();
        }

        // Radio normalizado (0..1) donde empieza a leerse la zona muerta interior. Task 4 lo
        // fija desde RemapSettings.LeftInnerDeadzone/RightInnerDeadzone.
        public double InnerDeadzone
        {
            get => _innerDeadzone;
            set { _innerDeadzone = value; PlaceRings(); }
        }

        // Radio normalizado (0..1) del alcance maximo configurado (mas alla de esto la
        // salida ya esta a fondo). Task 4 lo fija desde RemapSettings.LeftOuterDeadzone/
        // RightOuterDeadzone - el mismo campo normalizado, sin conversion.
        public double OuterReach
        {
            get => _outerReach;
            set { _outerReach = value; PlaceRings(); }
        }

        // Espejo de StickTelemetry.Drift. Solo lectura: la deriva la calcula el telemetro
        // propio a partir de las muestras que le llegan por Push, nadie de fuera la fija.
        public DriftLevel Drift => _telemetry.Drift;

        // Espejo de StickTelemetry.NewValuesPerSecond (Task 4, metrica VALORES NUEVOS): la
        // tasa de reportes la conoce quien llama (PollingMeter), no este control.
        public double NewValuesPerSecond(double reportHz) => _telemetry.NewValuesPerSecond(reportHz);

        // Empuja una muestra nueva (x, y en -1..1, convencion ControllerState: Y arriba
        // positivo). Antes de que Loaded corra no hay nada que mover todavia; el telemetro
        // sigue acumulando igual, asi que el primer Build ya arranca con rastro si Push
        // se llamo antes de que el control se mostrara.
        public void Push(double x, double y)
        {
            _telemetry.Push(x, y);
            if (!_built) return;
            PlaceDot(x, y);
            RedrawTrail();
        }

        public void Reset()
        {
            _telemetry.Reset();
            if (!_built) return;
            _liveDot!.Visibility = Visibility.Collapsed;
            RedrawTrail();
        }

        private void Build()
        {
            if (_built) return;   // Loaded puede repetirse (idempotente, como TriggerArc)

            var borderBrush = (Brush)FindResource("BorderBrush");
            var surfaceAltBrush = (Brush)FindResource("SurfaceAltBrush");
            var mutedBrush = (Brush)FindResource("TextMutedBrush");
            var dataBrush = (Brush)FindResource("TextDataBrush");

            var outerCircle = new Ellipse
            {
                Width = MaxRadius * 2,
                Height = MaxRadius * 2,
                Stroke = borderBrush,
                StrokeThickness = 1,
                Fill = surfaceAltBrush,
            };
            Canvas.SetLeft(outerCircle, Center - MaxRadius);
            Canvas.SetTop(outerCircle, Center - MaxRadius);
            MonitorCanvas.Children.Add(outerCircle);

            // Cruz de ejes en un gris casi negro, para leer el centro sin competir con el
            // rastro blanco. Fijo (no de la paleta de 7 marcas): es la misma excepcion que
            // ya usa el resto de esta pantalla para lineas de referencia muy tenues.
            var axisBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
            if (axisBrush.CanFreeze) axisBrush.Freeze();
            var hAxis = new Line
            {
                X1 = Center - MaxRadius, Y1 = Center, X2 = Center + MaxRadius, Y2 = Center,
                Stroke = axisBrush, StrokeThickness = 1,
            };
            var vAxis = new Line
            {
                X1 = Center, Y1 = Center - MaxRadius, X2 = Center, Y2 = Center + MaxRadius,
                Stroke = axisBrush, StrokeThickness = 1,
            };
            MonitorCanvas.Children.Add(hAxis);
            MonitorCanvas.Children.Add(vAxis);

            _deadzoneRing = new Ellipse
            {
                Stroke = mutedBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Fill = Brushes.Transparent,
            };
            MonitorCanvas.Children.Add(_deadzoneRing);

            _reachRing = new Ellipse
            {
                Stroke = mutedBrush,
                StrokeThickness = 1,
                Fill = Brushes.Transparent,
            };
            MonitorCanvas.Children.Add(_reachRing);

            for (int i = 0; i < TrailSegments; i++)
            {
                var part = new Polyline
                {
                    Stroke = dataBrush,
                    StrokeThickness = 1.5,
                    // El segmento mas viejo (i=0) queda mas tenue; el mas nuevo, a opacidad
                    // plena - "la opacidad cae del punto actual hacia atras".
                    Opacity = (i + 1) / (double)TrailSegments,
                };
                _trailParts[i] = part;
                MonitorCanvas.Children.Add(part);
            }

            _liveDot = new Ellipse
            {
                Width = DotSize,
                Height = DotSize,
                Fill = dataBrush,
                Visibility = Visibility.Collapsed,
            };
            MonitorCanvas.Children.Add(_liveDot);

            _built = true;
            PlaceRings();
            RedrawTrail();
        }

        // Mueve los dos anillos segun InnerDeadzone/OuterReach. Solo lo llaman esos dos
        // setters (cambios de configuracion) y Build; Push nunca lo toca, porque el radio
        // de zona muerta/alcance no cambia muestra a muestra.
        private void PlaceRings()
        {
            if (!_built) return;
            SizeRing(_deadzoneRing!, Math.Clamp(_innerDeadzone, 0.0, 1.0) * MaxRadius);
            SizeRing(_reachRing!, Math.Clamp(_outerReach, 0.0, 1.0) * MaxRadius);
        }

        private static void SizeRing(Ellipse ring, double radius)
        {
            ring.Width = radius * 2;
            ring.Height = radius * 2;
            Canvas.SetLeft(ring, Center - radius);
            Canvas.SetTop(ring, Center - radius);
        }

        private void PlaceDot(double x, double y)
        {
            var (dx, dy) = PadVisualMath.StickOffset(x, y, MaxRadius);
            Canvas.SetLeft(_liveDot!, Center + dx - DotSize / 2);
            Canvas.SetTop(_liveDot!, Center + dy - DotSize / 2);
            _liveDot!.Visibility = Visibility.Visible;
        }

        // Reparte StickTelemetry.Trail (mas viejo -> mas nuevo) entre los TrailSegments
        // polylines, con un punto de solape en cada frontera para que no quede un hueco
        // entre un segmento y el siguiente. Se recalcula entero en cada Push: con
        // TrailLength=120 puntos totales es barato (mismo argumento que TriggerArc usa
        // para no reconstruir su arco entero).
        private void RedrawTrail()
        {
            var trail = _telemetry.Trail;
            int n = trail.Count;

            if (n < 2)
            {
                foreach (var part in _trailParts) part!.Points = new PointCollection();
                return;
            }

            for (int i = 0; i < TrailSegments; i++)
            {
                int start = (int)Math.Round(i * (n - 1) / (double)TrailSegments);
                int end = (int)Math.Round((i + 1) * (n - 1) / (double)TrailSegments);
                var points = new PointCollection();
                for (int j = start; j <= end; j++)
                {
                    var (dx, dy) = PadVisualMath.StickOffset(trail[j].X, trail[j].Y, MaxRadius);
                    points.Add(new Point(Center + dx, Center + dy));
                }
                _trailParts[i]!.Points = points;
            }
        }
    }
}
