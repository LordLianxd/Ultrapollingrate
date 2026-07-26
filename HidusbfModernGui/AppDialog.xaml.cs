using System.Windows;
using System.Windows.Input;

namespace HidusbfModernGui
{
    // Dialogo modal con el tema de la app. Sustituye a MessageBox, que traia su marco gris, su
    // icono azul y sus botones en el idioma de Windows -"Yes"/"No" dentro de una app en espanol-.
    public partial class AppDialog : Window
    {
        private AppDialog() => InitializeComponent();

        // Pregunta de si/no. Devuelve true solo si el usuario acepta: cerrar con Escape o con
        // el aspa cuenta como NO, que es lo unico seguro para una accion que no se deshace.
        public static bool Confirm(Window owner, string title, string message,
                                   string aceptar = "ACEPTAR", string cancelar = "CANCELAR")
        {
            var d = Build(owner, title, message);
            d.OkBtn.Content = aceptar;
            d.CancelBtn.Content = cancelar;
            return d.ShowDialog() == true;
        }

        // Aviso de una sola salida. Sin boton de cancelar: no hay nada que cancelar.
        public static void Warn(Window owner, string title, string message)
        {
            var d = Build(owner, title, message);
            d.CancelBtn.Visibility = Visibility.Collapsed;
            d.OkBtn.Content = "ENTENDIDO";
            d.ShowDialog();
        }

        private static AppDialog Build(Window owner, string title, string message)
        {
            var d = new AppDialog
            {
                // Sin Owner, WindowStartupLocation="CenterOwner" cae en el centro de la pantalla
                // y el dialogo puede quedarse DETRAS de la ventana principal.
                Owner = owner,
            };
            d.TitleText.Text = title;
            d.MessageText.Text = message;
            return d;
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Escape cierra como "no". IsCancel en el boton haria lo mismo, pero deja de
            // funcionar cuando el boton esta oculto, que es justo el caso de Warn().
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
            base.OnKeyDown(e);
        }
    }
}
