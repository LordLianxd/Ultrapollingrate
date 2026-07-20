using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HidusbfModernGui
{
    // Ventana overlay sin borde, transparente y siempre-encima para capturar el
    // mando en OBS. Reusa el mismo PadVisual del configurador (VisualizerTick lo
    // alimenta ademas del de la ventana principal, ver MainWindow.xaml.cs).
    public partial class StreamerWindow : Window
    {
        public PadVisual Pad => PadControl;   // expuesto para que el feed lo alimente

        public StreamerWindow()
        {
            InitializeComponent();
            PadControl.StreamerBackground = true;   // fondo transparente del mando
        }

        private void Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
        private void Scale_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
            => PadControl.LayoutTransform = new ScaleTransform(ScaleSlider.Value, ScaleSlider.Value);
        private void Topmost_Changed(object s, RoutedEventArgs e) => Topmost = TopmostToggle.IsChecked == true;
        private void CloseClick(object s, RoutedEventArgs e) => Close();

        // Click-through: la ventana deja pasar el raton al juego/OBS de abajo (WS_EX_TRANSPARENT).
        private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        private void ClickThrough_Changed(object s, RoutedEventArgs e)
            => SetClickThrough(ClickThroughToggle.IsChecked == true);

        // Expuesto para que la ventana principal (que nunca es click-through) pueda
        // apagar el pasa-clic desde afuera: una vez ON desde la barra del propio overlay,
        // el raton la atraviesa entera y su ToggleButton deja de ser alcanzable, asi que
        // este es el unico camino no destructivo para volver a OFF.
        public void SetClickThrough(bool on)
        {
            var h = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if (on) SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
            else SetWindowLong(h, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
            ClickThroughToggle.IsChecked = on;
        }
    }
}
