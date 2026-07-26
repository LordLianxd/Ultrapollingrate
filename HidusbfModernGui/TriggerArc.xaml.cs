using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HidusbfModernGui
{
    // El arco de guiones que rodea cada gatillo. Ensena DOS cosas a la vez, que es lo que hace
    // util la pantalla: cuanto esta apretado el gatillo AHORA (los guiones encendidos) y donde
    // cae el punto de disparo configurado (el guion largo). Asi se ajusta el umbral apretando
    // el gatillo, sin tener que adivinar a que porcentaje corresponde el recorrido del dedo.
    //
    // Toda la geometria sale de TriggerGauge, que es logica pura y esta bajo test. Aqui solo
    // hay dibujo: si algun dia el arco se ve raro, el fallo esta en uno de los dos sitios, y
    // el primero ya se puede descartar sin tocar el hardware.
    public partial class TriggerArc : UserControl
    {
        private const double TickLength = 11;
        private const double TickWidth = 2;
        private const double MarkerLength = 20;

        // Apagado pero visible: el recorrido completo tiene que leerse siempre, o no se sabe
        // cuanto queda por apretar.
        private const double RestOpacity = 0.22;

        private readonly List<Rectangle> _ticks = new();
        private Rectangle? _marker;
        private double _value;
        private double _threshold;

        public TriggerArc()
        {
            InitializeComponent();
            Loaded += (s, e) => Build();
        }

        // Recorrido actual del gatillo, 0..1. Lo empuja el temporizador de la pagina desde el
        // snapshot del mando.
        public double Value
        {
            get => _value;
            set { _value = value; Paint(); }
        }

        // Punto de disparo configurado, 0..1. En 0 no se dibuja marca: 0% significa "hair
        // trigger apagado", no "umbral en el cero".
        public double Threshold
        {
            get => _threshold;
            set { _threshold = value; PlaceMarker(); }
        }

        // El gatillo derecho es el espejo del izquierdo. Se hace con un transform sobre el
        // canvas entero en vez de con otra tanda de cuentas, para que las dos mitades no
        // puedan separarse por un signo mal puesto.
        public bool Mirrored
        {
            get => (bool)GetValue(MirroredProperty);
            set => SetValue(MirroredProperty, value);
        }

        public static readonly DependencyProperty MirroredProperty =
            DependencyProperty.Register(nameof(Mirrored), typeof(bool), typeof(TriggerArc),
                new PropertyMetadata(false, (d, e) => ((TriggerArc)d).ApplyMirror()));

        private void ApplyMirror()
        {
            if (ArcCanvas == null) return;

            // UN solo mecanismo para el centro del espejo. Antes se fijaba
            // RenderTransformOrigin (relativo, 0.5/0.5) Y ADEMAS se le pasaban CenterX/CenterY
            // al ScaleTransform: los dos desplazan lo mismo, se sumaban, y el arco derecho
            // salia corrido medio ancho a la derecha y cortado por el borde de su tarjeta.
            // Un Canvas no tiene tamano propio, asi que el origen relativo no le sirve: el
            // centro se da explicito y el RenderTransformOrigin se deja en su valor por defecto.
            ArcCanvas.RenderTransform = Mirrored
                ? new ScaleTransform(-1, 1, Width / 2, Height / 2)
                : Transform.Identity;
        }

        private void Build()
        {
            if (ArcCanvas.Children.Count > 0) return;   // idempotente: Loaded puede repetirse

            var brush = (Brush)FindResource("TextDataBrush");
            double cx = Width / 2, cy = Height / 2;
            double radius = Math.Min(cx, cy) - TickLength;

            for (int i = 0; i < TriggerGauge.TickCount; i++)
            {
                var (x, y) = TriggerGauge.TickCentre(i, cx, cy, radius);

                var tick = new Rectangle
                {
                    Width = TickWidth,
                    Height = TickLength,
                    Fill = brush,
                    Opacity = RestOpacity,
                    RadiusX = 1,
                    RadiusY = 1,
                    // Cada guion apunta hacia fuera: se gira sobre su propio centro por el
                    // angulo del punto, que es el mismo que lo coloco.
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(TriggerGauge.AngleOfTick(i)),
                };
                Canvas.SetLeft(tick, x - TickWidth / 2);
                Canvas.SetTop(tick, y - TickLength / 2);
                ArcCanvas.Children.Add(tick);
                _ticks.Add(tick);
            }

            _marker = new Rectangle
            {
                Width = TickWidth + 1,
                Height = MarkerLength,
                Fill = brush,
                Visibility = Visibility.Collapsed,
                RadiusX = 1,
                RadiusY = 1,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            ArcCanvas.Children.Add(_marker);

            ApplyMirror();
            Paint();
            PlaceMarker();
        }

        // Enciende los guiones que toca. No se anima: el gatillo ya se mueve a 60 fps y una
        // transicion encima lo dejaria por detras del dedo, que es justo lo que no puede pasar
        // en un indicador que sirve para calibrar.
        private void Paint()
        {
            if (_ticks.Count == 0) return;
            for (int i = 0; i < _ticks.Count; i++)
                _ticks[i].Opacity = TriggerGauge.IsTickLit(i, _value) ? 1.0 : RestOpacity;
        }

        private void PlaceMarker()
        {
            if (_marker == null) return;

            int? t = TriggerGauge.ThresholdTick(_threshold);
            if (t == null) { _marker.Visibility = Visibility.Collapsed; return; }

            double cx = Width / 2, cy = Height / 2;
            double radius = Math.Min(cx, cy) - TickLength;
            var (x, y) = TriggerGauge.TickCentre(t.Value, cx, cy, radius);

            Canvas.SetLeft(_marker, x - _marker.Width / 2);
            Canvas.SetTop(_marker, y - MarkerLength / 2);
            _marker.RenderTransform = new RotateTransform(TriggerGauge.AngleOfTick(t.Value));
            _marker.Visibility = Visibility.Visible;
        }
    }
}
