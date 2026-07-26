using System;
using System.Windows;

// Alias explicitos, uno por tipo. NADA de "using System.Windows.Forms" a secas: ese namespace
// trae Application, MessageBox, Color, Brush, Point y ContextMenu, que chocan con los de
// System.Windows y System.Windows.Media que MainWindow.xaml.cs usa por todas partes. Con alias,
// la colision no existe y este archivo es el unico sitio del proyecto que sabe de WinForms.
using WinFormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using WinFormsMenu = System.Windows.Forms.ContextMenuStrip;
using WinFormsMenuItem = System.Windows.Forms.ToolStripMenuItem;
using WinFormsTipIcon = System.Windows.Forms.ToolTipIcon;

namespace HidusbfModernGui
{
    // La app vive en la bandeja: cerrar la ventana la esconde, no mata el proceso, para que el
    // mando virtual y el color del mando sigan aplicandose mientras juegas.
    //
    // Que sigue vivo con la ventana escondida y que no:
    //   SIGUE  el motor del mando virtual (_engineTimer): es el sentido entero de la funcion.
    //   SIGUE  el conductor de luz (_rainbowTimer): mantiene y reafirma el color.
    //   PARA   el medidor: medir contra una ventana que nadie mira es gastar CPU, y su handle
    //          HID abierto es justo lo que puede vetar un CM_Query_And_Remove_SubTree despues.
    //   PARA   el visualizador del mando: son ~60 fps dibujando un arbol visual que no se ve.
    public partial class MainWindow
    {
        private WinFormsNotifyIcon? _tray;

        // Alto solo durante "Salir": es lo que distingue un cierre de verdad de la X, que
        // esconde. Sin esto, OnClosing no puede saber cual de los dos esta ocurriendo.
        private bool _reallyExit;

        private void InitTray()
        {
            var menu = new WinFormsMenu();

            var abrir = new WinFormsMenuItem("Mostrar UltraPolling");
            abrir.Click += (s, e) => ShowFromTray();
            abrir.Font = new System.Drawing.Font(abrir.Font, System.Drawing.FontStyle.Bold);

            var salir = new WinFormsMenuItem("Salir");
            salir.Click += (s, e) => ExitForReal();

            menu.Items.Add(abrir);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(salir);

            _tray = new WinFormsNotifyIcon
            {
                Icon = LoadTrayIcon(),
                ContextMenuStrip = menu,
                Visible = true,
            };
            // Doble clic en el icono = mostrar. Es lo que espera cualquiera que haya usado una
            // app de bandeja, y no obliga a descubrir el menu contextual.
            _tray.DoubleClick += (s, e) => ShowFromTray();

            UpdateTrayTooltip();
        }

        // El mismo app.ico que ya va embebido como Resource para el icono de la ventana. Si por
        // lo que sea no se pudiera leer, se cae al icono de la aplicacion en disco, y si tampoco,
        // a uno del sistema: quedarse SIN icono seria lo peor de todo, porque dejaria la app
        // corriendo sin ninguna forma de volver a ella.
        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var res = Application.GetResourceStream(new Uri("app.ico", UriKind.Relative));
                if (res != null) return new System.Drawing.Icon(res.Stream);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Icono de bandeja: {ex.Message}"); }

            try
            {
                string? exe = Environment.ProcessPath;
                if (exe != null)
                {
                    var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                    if (extracted != null) return extracted;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Icono del exe: {ex.Message}"); }

            return System.Drawing.SystemIcons.Application;
        }

        // El tooltip dice si el mando virtual esta encendido, y no es un adorno: con el motor
        // activo HidHide mantiene el DualSense fisico oculto para TODO el sistema. Si el usuario
        // se olvida de que la app sigue en la bandeja, su mando parece roto en las demas apps, y
        // este texto es la unica pista sin abrir la ventana.
        //
        // El limite de 63 caracteres es de la Shell (NOTIFYICONDATA.szTip): pasarse trunca.
        private void UpdateTrayTooltip()
        {
            if (_tray == null) return;
            _tray.Text = _engineRunning
                ? "UltraPolling - mando virtual ACTIVO (fisico oculto)"
                : "UltraPolling";
        }

        // Esconde la ventana y suelta lo que no tiene sentido mantener sin nadie mirando.
        private void HideToTray()
        {
            Hide();

            // El medidor: para el timer Y suelta el handle HID.
            _meterTimer?.Stop();
            _meter.Stop();

            StopVisualizer();

            UpdateTrayTooltip();

            // Una sola vez en la vida de la instalacion. La primera vez que la X no cierra es
            // una sorpresa y hay que explicarla; a partir de la segunda seria ruido.
            var prefs = UiPrefsStore.Load();
            if (!prefs.TrayHintShown)
            {
                prefs.TrayHintShown = true;
                UiPrefsStore.Save(prefs);
                _tray?.ShowBalloonTip(5000, "UltraPolling sigue funcionando",
                    "Se quedo en la bandeja para mantener tu mando y tu luz. " +
                    "Clic derecho en el icono para volver o salir.", WinFormsTipIcon.Info);
            }
        }

        // Devuelve la ventana. Show() sola no basta si estaba minimizada antes de esconderse:
        // reaparecería minimizada. Y Activate() es lo que la trae al frente por encima del juego.
        private void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;    // gana la carrera contra la ventana que tuviera el foco...
            Topmost = false;   // ...y se quita enseguida, que esta app no es un overlay

            // El visualizador decide por su cuenta si le toca correr (depende de si su panel
            // esta a la vista y de si el motor esta ocupado); aqui solo se le vuelve a preguntar.
            UpdateVisualizerRunState();

            // El medidor se reanuda solo al restaurar la seleccion de la lista, que es quien
            // sabe a que dispositivo apuntar.
            StartMeasuring(DevicesListBox.SelectedItem as UsbDeviceModel);
        }

        // Puerta publica para App: cuando el usuario abre el acceso directo una segunda vez, la
        // instancia que ya corre se muestra en lugar de dejarle creer que no paso nada.
        internal void ShowFromTrayRequest() => ShowFromTray();

        // El unico camino que termina el proceso.
        private void ExitForReal()
        {
            _reallyExit = true;

            // Quitar el icono a mano ANTES de cerrar: la Shell no limpia los iconos de procesos
            // que se van, y quedaria un fantasma en la bandeja hasta que el usuario pasara el
            // raton por encima.
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            // Close() dispara OnClosing, que con _reallyExit puesto hace el revert del motor y
            // deja pasar el cierre. Asi el apagado ordenado vive en UN solo sitio.
            Close();
            Application.Current.Shutdown();
        }
    }
}
