using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HidusbfModernGui
{
    public partial class MainWindow : Window
    {
        private List<UsbDeviceModel> _allDevices = new List<UsbDeviceModel>();
        private DriverState _driverState = new DriverState();
        private bool _isInitializing = true;
        private bool _overclockBusy;
        private StreamerWindow? _streamerWindow;

        // El mando de las luces se resuelve solo. Antes habia un desplegable, pero con un unico
        // mando conectado era una lista de un elemento que el usuario tenia que confirmar.
        //
        // El orden importa: primero el escaneo (nombre bonito, y coincide con lo que se ve en
        // Dispositivos) y si no, el resolutor en-proceso, que es el que sigue encontrando el mando
        // cuando HidHide lo oculta y el escaneo por PowerShell ya no lo ve.
        private string? _lightPadId;

        // Navegacion del configurador (Task PS3): hub de tarjetas <-> 4 sub-paginas.
        // Logica pura en ConfigNav (probada aparte); este campo es el unico estado de
        // navegacion, y UpdateConfigPages() el unico sitio que traduce Current a Visibility.
        private readonly ConfigNav _configNav = new();

        // Mode used to interpret the 31/62 slots. Falls back to NoPatch so the UI
        // shows literal 31Hz/62Hz rather than claiming an overclock we cannot prove.
        private DriverMode ActiveMode => _driverState.EffectiveMode ?? DriverMode.NoPatch;

        public MainWindow()
        {
            InitializeComponent();

            // El feed vive mientras el pad del configurador este visible O la ventana
            // streamer siga abierta (ver UpdateVisualizerRunState): arranca/para segun ese
            // estado combinado cada vez que ConfigPadVisual cambia de visibilidad, sin
            // importar por que camino de navegacion llego el usuario.
            ConfigPadVisual.IsVisibleChanged += (s, e) => UpdateVisualizerRunState();

            // Los arcos de los gatillos solo leen el mando mientras su pagina esta a la vista.
            // Se engancha a la visibilidad y no a los botones de navegacion para que valga
            // cualquier camino de entrada y de salida, incluido volver con la flecha.
            PageGatillos.IsVisibleChanged += (s, e) => UpdateTriggerArcRunState();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Guard de arranque (Task 6/14): si un run anterior murio con el DualSense
            // oculto por HidHide, re-mostrarlo ahora para no dejar el mando "desaparecido".
            // Best-effort y silencioso: nunca debe impedir que la app abra.
            try { new HidHideControl().ShowAllDualSense(); } catch { }

            BuildHeaderSpectrum();
            BuildLoadingIndicator();
            BuildEngineSpinner();
            InitTray();
            ApplySpecsExpanded(UiPrefsStore.Load().SpecsExpanded);
            RefreshPrivilegeState();
            RefreshStatus();

            // La luz guardada se aplica AQUI, antes del escaneo: el usuario ve su color a la
            // vez que la ventana. Antes colgaba del final de RefreshDevicesList(), que es una
            // llamada a PowerShell de ~1 s - un escaneo de dispositivos que encender una luz
            // no necesita para nada.
            ApplySavedLightNow();

            RefreshDevicesList();
            BuildRemapControls();

            // El segmento inicial se marca AQUI y no en el XAML: alli el Checked llega durante
            // el parseo, con los paneles hermanos aun sin crear.
            ConfigTabBtn.IsChecked = true;

            _isInitializing = false;
        }

        // Nunca cerrar la app con el fisico todavia oculto: si el mando virtual sigue activo,
        // detenerlo (muestra el fisico, quita el virtual) antes de salir. Se hace en el
        // hilo de UI, en linea (no via StopEngine()/Task.Run): la app ya se esta cerrando,
        // asi que un revert sincrono de ~1-2s aqui no es el freeze que se reporto (ese
        // ocurria al hacer clic en PROBAR/DETENER durante uso normal); esperar aqui a un
        // Task.Run en cambio arriesgaria deadlock, porque StopEngine() toca controles de
        // UI despues de su await y esta llamada no puede hacer await (OnClosing no es async).
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Todo cierre que NO venga de "Salir" es en realidad un esconder: la X, Alt+F4 y
            // el menu de la barra de titulo pasan todos por aqui. Cancelar y esconder los cubre
            // los tres de una vez, en lugar de tener que interceptar cada uno por separado.
            if (!_reallyExit)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            StopVisualizer();
            _streamerWindow?.Close();

            // Un guardado con debounce pendiente se escribe YA: salir dentro de la ventana de
            // 750 ms no debe perder el ultimo color o player que eligio el usuario.
            if (_intentSave != null && _intentSave.IsEnabled && _lastIntent != null)
            {
                _intentSave.Stop();
                IntentStore.Save(_lastIntent);
            }

            _meterTimer?.Stop();
            _rainbowTimer?.Stop();
            // Suelta el handle HID antes de que se vaya el proceso: dejarlo abierto de salida
            // podria vetar un CM_Query_And_Remove_SubTree posterior sobre ese dispositivo.
            _meter.Dispose();

            if (_engineRunning)
            {
                if (_engineTimer != null)
                {
                    _engineTimer.Stop();
                    _engineTimer.Tick -= EngineTick;
                    _engineTimer = null;
                }
                _engineRunning = false;
                try { RevertEngineDevices(); } catch { }
                CleanupEngine();
            }
            base.OnClosing(e);
        }

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;
        private DispatcherTimer? _deviceChangeDebounce;

        // Cuantas veces se puede APLAZAR un refresco por estar el motor o un replug ocupados,
        // antes de rendirse. A 500 ms por intento son 15 s, de sobra para el reinicio de
        // devnode mas lento; el tope existe solo para que una bandera que se quedara colgada
        // no deje un temporizador latiendo el resto de la sesion.
        private const int DeviceChangeMaxDeferrals = 30;
        private int _deviceChangeDeferrals;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var src = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            src?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE && wParam.ToInt32() == DBT_DEVNODES_CHANGED)
            {
                // Los cambios en el arbol de dispositivos llegan en rafaga; agrupa antes de
                // reaccionar. Reusa RefreshDevicesList (un escaneo con debounce) para repoblar
                // la lista y reaplicar la luz al mando que reaparecio.
                if (_deviceChangeDebounce == null)
                {
                    _deviceChangeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    _deviceChangeDebounce.Tick += (s, ev) =>
                    {
                        _deviceChangeDebounce!.Stop();

                        // Ocupados = APLAZAR, nunca descartar. Antes aqui habia dos "return"
                        // secos, y como el temporizador ya se habia parado en la linea de
                        // arriba, el aviso se perdia para siempre.
                        //
                        // No era un caso raro: al apagar el mando virtual, HidHide devuelve el
                        // fisico reiniciando su devnode, y ese revert tarda MUCHO mas que estos
                        // 500 ms, asi que el WM_DEVICECHANGE de nuestro propio replug llegaba
                        // SIEMPRE con _engineBusy en true. Resultado: la lista se quedaba con la
                        // foto anterior al motor, la luz no se reaplicaba (se reaplica justo
                        // aqui) y el medidor seguia muerto -su handle se lo lleva el reinicio
                        // del devnode-, con lo que MEDIDA no volvia a dar señal y parecia que el
                        // overclock se hubiera perdido.
                        if (_overclockBusy || _engineBusy)
                        {
                            if (++_deviceChangeDeferrals <= DeviceChangeMaxDeferrals)
                                _deviceChangeDebounce.Start();
                            return;
                        }

                        _deviceChangeDeferrals = 0;
                        _intentReapplied = false;   // permitir reaplicar al mando reaparecido
                        RefreshDevicesList();        // repuebla _allDevices y reaplica la luz
                    };
                }
                _deviceChangeDebounce.Stop();
                _deviceChangeDeferrals = 0;   // aviso nuevo: el presupuesto de aplazamientos vuelve a empezar
                _deviceChangeDebounce.Start();
            }
            else if (msg == WM_GETMINMAXINFO)
            {
                LimitMaximizeToWorkArea(hwnd, lParam);
            }
            return IntPtr.Zero;
        }

        // ===== Maximizar sin tapar la barra de tareas =====
        //
        // Una ventana sin chrome (WindowStyle=None + AllowsTransparency) se maximiza al
        // MONITOR entero, tapando la barra de tareas: Windows solo respeta el area de trabajo
        // en ventanas con marco estandar. La solucion es responder a WM_GETMINMAXINFO con el
        // tamano y la posicion del area de trabajo del monitor donde esta la ventana - asi
        // tambien funciona bien en multi-monitor y con la barra en cualquier borde.
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int MONITOR_DEFAULTTONEAREST = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private static void LimitMaximizeToWorkArea(IntPtr hwnd, IntPtr lParam)
        {
            try
            {
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero) return;

                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref mi)) return;

                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                // Coordenadas relativas al monitor, no al escritorio virtual.
                mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
            catch
            {
                // Si algo falla aqui, Windows usa su calculo por defecto (tapa la barra de
                // tareas): feo, pero jamas debe impedir que la ventana se maximice.
            }
        }

        private readonly PollingMeter _meter = new PollingMeter();
        private DispatcherTimer? _meterTimer;
        private readonly List<System.Windows.Shapes.Rectangle> _bars = new List<System.Windows.Shapes.Rectangle>();

        // The header's spectrum. Every bar is one real inter-report gap measured off the
        // selected device - it used to be Random(20260715). Bars are built in code
        // because there are dozens of them and their count follows the window width.
        private void BuildHeaderSpectrum()
        {
            const double barWidth = 3, gap = 4;
            HeaderSpectrum.Children.Clear();
            _bars.Clear();

            HeaderSpectrum.SizeChanged -= HeaderSpectrum_SizeChanged;
            HeaderSpectrum.SizeChanged += HeaderSpectrum_SizeChanged;

            double width = HeaderSpectrum.ActualWidth > 0 ? HeaderSpectrum.ActualWidth : 1000;
            int count = Math.Max(1, (int)(width / (barWidth + gap)));
            var muted = (Brush)FindResource("TextMutedBrush");

            for (int i = 0; i < count; i++)
            {
                var bar = new System.Windows.Shapes.Rectangle
                {
                    Width = barWidth,
                    Height = 2,
                    Fill = muted,
                    RadiusX = 1,
                    RadiusY = 1
                };
                Canvas.SetLeft(bar, i * (barWidth + gap));
                Canvas.SetBottom(bar, 6);
                HeaderSpectrum.Children.Add(bar);
                _bars.Add(bar);
            }
        }

        // Pulls the meter's snapshot and paints it. Runs on the UI thread; the reader
        // task never touches a control.
        private void MeterTick(object? sender, EventArgs e)
        {
            var sample = _meter.Snapshot();
            var gaps = _meter.RecentGaps(_bars.Count);
            DrawSpectrum(gaps, sample);
            UpdateMeasuredReadout(sample);
        }

        // Height carries the quality of the polling; colour carries whether there is any
        // polling at all. Both are needed: a flawless steady rate normalises to flat
        // bars, and so does silence. Without the colour those opposite states would look
        // identical.
        private void DrawSpectrum(double[] gaps, RateSample? sample)
        {
            bool live = sample.HasValue && gaps.Length > 0;
            var brush = (Brush)FindResource(live ? "TextDataBrush" : "TextMutedBrush");

            for (int i = 0; i < _bars.Count; i++)
            {
                var bar = _bars[i];
                bar.Fill = brush;

                if (!live)
                {
                    // Flat and grey. No motion is invented when there is no signal -
                    // that is precisely what the fake graph we deleted used to do.
                    bar.Height = 2;
                    bar.Opacity = 0.5;
                    continue;
                }

                // Newest gaps land on the right, so the strip reads left-to-right in time.
                int g = gaps.Length - _bars.Count + i;
                if (g < 0) { bar.Height = 2; bar.Opacity = 0.35; continue; }

                // Normalised against the median: 1.0 means "exactly the typical
                // interval". A dropped poll is a long gap and so a short bar.
                double ratio = sample!.Value.MedianGapMs / Math.Max(gaps[g], 0.0001);
                bar.Height = Math.Max(2, Math.Min(34, ratio * 16));
                bar.Opacity = 0.85;
            }
        }

        // The canvas has no width until layout runs, so the first build guesses. Rebuild
        // once the real width is known, and whenever the window is resized.
        private void HeaderSpectrum_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged) BuildHeaderSpectrum();
        }

        // Three dots pulsing in sequence. Shown only while a scan is genuinely running.
        // Anillo de 12 puntos que decrecen y se apagan segun se alejan de la cabeza, girando a
        // saltos de 30 grados. Los saltos son a proposito: un giro continuo a esta escala se
        // ve como un borron, y el escalonado es lo que hace que se lea el sentido de giro.
        //
        // Un solo Storyboard sobre el contenedor en vez de 12 animaciones sueltas: es una sola
        // cosa que gira, no doce que parpadean cada una a su ritmo.
        private const int LoadingDots = 12;

        // El anillo grande, centrado sobre la lista de dispositivos mientras se escanea.
        private void BuildLoadingIndicator()
            => BuildDotRing(LoadingIndicator, LoadingSpin, LoadingDots, centre: 26, ring: 19, headSize: 8.0);

        // El anillo pequeno de la fila MANDO VIRTUAL. Menos puntos que el grande a proposito:
        // a 18 px, doce se tocarian entre si y el anillo se leeria como un circulo solido, que
        // es justo lo contrario de lo que tiene que comunicar.
        private const int EngineDots = 8;

        private void BuildEngineSpinner()
            => BuildDotRing(EngineSpinner, EngineSpin, EngineDots, centre: 9, ring: 6.5, headSize: 4.0);

        // Dibuja el anillo y le engancha el giro. Los saltos discretos son a proposito: un giro
        // continuo a estas escalas se ve como un borron, y el escalonado es lo que hace que se
        // lea el sentido de giro.
        private void BuildDotRing(Canvas host, RotateTransform spin, int dots,
                                  double centre, double ring, double headSize)
        {
            var white = (Brush)FindResource("TextDataBrush");
            host.Children.Clear();

            for (int i = 0; i < dots; i++)
            {
                // i = 0 es la cabeza. El tamano cae al 31% y la opacidad al 18% dando la
                // vuelta, que es lo que dibuja la "estela".
                double t = (double)i / dots;
                double size = headSize * (1.0 - 0.69 * t);
                double angle = -Math.PI / 2 + i * (2 * Math.PI / dots);

                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = white,
                    Opacity = 1.0 - 0.82 * t,
                };
                Canvas.SetLeft(dot, centre + ring * Math.Cos(angle) - size / 2);
                Canvas.SetTop(dot, centre + ring * Math.Sin(angle) - size / 2);
                host.Children.Add(dot);
            }

            var turn = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            for (int i = 1; i <= dots; i++)
            {
                turn.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                    i * (360.0 / dots),
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * (900.0 / dots)))));
            }
            spin.BeginAnimation(RotateTransform.AngleProperty, turn);
        }

        // Enciende o apaga el anillo de la fila MANDO VIRTUAL. Se llama en los dos sentidos
        // -al arrancar el motor y al pararlo-, porque las dos operaciones reinician el devnode
        // y las dos tardan lo mismo en volver.
        // ===== ESPECIFICACIONES TECNICAS: plegar / desplegar =====
        //
        // Campos de diagnostico (velocidad de bus, bInterval, filtro, modo de intervalo,
        // instance id): hacen falta para entender por que una tasa NO se aplico, no para el uso
        // normal. Plegados de partida, y la eleccion se recuerda.
        private bool _specsExpanded;

        private void SpecsToggle_Click(object sender, RoutedEventArgs e)
        {
            ApplySpecsExpanded(!_specsExpanded);

            // Se guarda al pulsar y no con debounce: es un clic aislado del usuario, no una
            // rafaga como el arrastre del selector de color.
            var prefs = UiPrefsStore.Load();
            prefs.SpecsExpanded = _specsExpanded;
            UiPrefsStore.Save(prefs);
        }

        // Deja el panel y el icono coherentes. Un solo sitio los toca, para que no pueda
        // quedar un ojo abierto sobre una tarjeta plegada.
        private void ApplySpecsExpanded(bool expanded)
        {
            _specsExpanded = expanded;
            if (SpecsCard == null || SpecsToggleIcon == null) return;

            SpecsCard.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            SpecsToggleIcon.Data = (Geometry)FindResource(expanded ? "EyeIconPath" : "EyeOffIconPath");
            SpecsToggleBtn.ToolTip = expanded
                ? "Ocultar las especificaciones tecnicas"
                : "Ver las especificaciones tecnicas";
        }

        private void SetEngineBusyVisual(bool busy)
        {
            EngineSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            MasterToggleBtn.IsEnabled = !busy;

            // Los tres finales del motor -arranque OK, arranque fallido y parada- pasan por
            // aqui, asi que es el sitio que garantiza que el tooltip de la bandeja nunca miente
            // sobre si el fisico esta oculto.
            UpdateTrayTooltip();
        }

        // Difumina (o deja nitida) la lista mientras se escanea. El desenfoque entra y sale
        // animado: aparecer de golpe se lee como un fallo de dibujado, no como un estado.
        private void SetDevicesBusy(bool busy)
        {
            LoadingIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

            if (DevicesScroll.Effect is not System.Windows.Media.Effects.BlurEffect blur)
            {
                blur = new System.Windows.Media.Effects.BlurEffect
                {
                    Radius = 0,
                    KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                };
                DevicesScroll.Effect = blur;
            }

            var fade = new DoubleAnimation
            {
                To = busy ? 9.0 : 0.0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            };
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, fade);
        }

        // Window controls
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;

            // Doble clic en la barra = maximizar/restaurar, como en cualquier ventana.
            if (e.ClickCount == 2) { ToggleMaximize(); return; }

            // Arrastrar una ventana maximizada la restaura y la deja "colgando" del cursor en
            // la misma posicion relativa, que es lo que hace Windows.
            if (WindowState == WindowState.Maximized)
            {
                double ratio = ActualWidth > 0 ? e.GetPosition(this).X / ActualWidth : 0.5;
                var screen = PointToScreen(e.GetPosition(this));
                WindowState = WindowState.Normal;
                Left = screen.X - RestoreBounds.Width * ratio;
                Top = screen.Y - 26;   // el cursor queda dentro de la barra de titulo
            }

            // DragMove lanza si el boton ya se solto entre el evento y esta llamada (puede
            // pasar justo al restaurar); no es motivo para tumbar la app.
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        // Un texto vacio no debe dejar un hueco en la tarjeta: si no hay nada que decir, la
        // linea desaparece. Unico punto por el que pasa el estado del interruptor maestro.
        private void SetMasterStatus(string text)
        {
            if (MasterStatusText == null) return;
            MasterStatusText.Text = text;
            MasterStatusText.Visibility = string.IsNullOrWhiteSpace(text)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void ToggleMaximize()
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        // El chrome es propio (WindowStyle=None), asi que el aspecto de "maximizada" hay que
        // pintarlo a mano: sin margen ni esquinas redondeadas, o quedan franjas de escritorio
        // a los lados y el redondeo flotando en mitad de la pantalla.
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (RootBorder == null) return;   // aun no se ha parseado el XAML

            bool max = WindowState == WindowState.Maximized;
            RootBorder.CornerRadius = new CornerRadius(max ? 0 : 28);
            SurfaceBorder.CornerRadius = new CornerRadius(max ? 0 : 24);
            SurfaceBorder.Margin = new Thickness(max ? 0 : 16);

            MaxIcon.Data = (Geometry)FindResource(max ? "RestoreIconPath" : "MaxIconPath");
            MaximizeBtn.ToolTip = max ? "Restaurar" : "Maximizar";
        }

        // La X NO cierra: esconde a la bandeja, para que el mando virtual y la luz sigan
        // aplicandose mientras juegas. Se sale por el menu del icono. La primera vez que pasa,
        // un globo lo explica (ver MainWindow.Tray.cs).
        private void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

        // Reads the real system state: the hash of the installed .sys plus the
        // registry, never what this app wrote earlier.
        private void RefreshStatus()
        {
            _driverState = SystemManager.GetDriverState();

            // The header is the spectrum animation now, so the System view is the only
            // place driver and service state are reported. They keep their status dots:
            // the colour still encodes a fact, it just moved.
            DriverModeText.Text = _driverState.ModeText;
            HeaderModeDot.Fill = StatusBrush(_driverState.HeaderStatus);

            ServiceStatusText.Text = _driverState.ServiceStatus;
            ServiceStatusDot.Fill = StatusBrush(_driverState.ServiceStatusLevel);

            int selectedIndex = _driverState.EffectiveMode switch
            {
                DriverMode.NoPatch => 0,
                DriverMode.Rate1k => 1,
                DriverMode.Rate2k4k => 2,
                DriverMode.Rate4k8k => 3,
                _ => -1
            };

            GlobalModeComboBox.SelectionChanged -= GlobalModeComboBox_SelectionChanged;
            GlobalModeComboBox.SelectedIndex = selectedIndex;
            GlobalModeComboBox.SelectionChanged += GlobalModeComboBox_SelectionChanged;

            UpdateWarningBanner();
        }

        // El aviso del driver es lo unico que explica un punto de estado en Warn/Error, asi
        // que vive en Ajustes > ESTADO DEL SISTEMA y no en la barra inferior, que ahora es
        // muda y se retira sola: un aviso que se borra a los seis segundos no es un aviso.
        // RefreshStatus() es el UNICO que escribe aqui; la cháchara del escaneo va a
        // StatusLogText y no puede pisarlo.
        // El "ADMIN" de la barra vieja era una etiqueta escrita a mano. Aqui, bajo el rotulo
        // PERMISOS, se lee como el resultado de una comprobacion - asi que se comprueba. El
        // manifiesto pide requireAdministrator, o sea que deberia ser siempre cierto; si algun
        // dia deja de serlo, esto lo dira en vez de seguir afirmandolo.
        private void RefreshPrivilegeState()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                bool admin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                PrivilegeText.Text = admin ? "Administrador" : "Usuario estandar";
                PrivilegeText.Foreground = StatusBrush(admin ? StatusLevel.Ok : StatusLevel.Warn);
            }
            catch (Exception ex)
            {
                PrivilegeText.Text = "Desconocido";
                System.Diagnostics.Debug.WriteLine($"RefreshPrivilegeState fallo: {ex.Message}");
            }
        }

        private void UpdateWarningBanner()
        {
            string? warning = _driverState.Warning;
            if (string.IsNullOrEmpty(warning))
            {
                WarningText.Text = "";
                WarningText.Visibility = Visibility.Collapsed;
                return;
            }

            WarningText.Text = warning;
            WarningText.Foreground = StatusBrush(_driverState.HeaderStatus);
            WarningText.Visibility = Visibility.Visible;
        }

        // Delegates to the converter's static mapping. Do NOT re-implement the
        // StatusLevel switch here: one colour rule, one place.
        private static Brush StatusBrush(StatusLevel level)
            => StatusLevelToBrushConverter.BrushFor(level);

        // Navigation button click event handlers
        private void DashboardNavBtn_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
        }

        private void SettingsNavBtn_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 1;
        }

        private void LightNavBtn_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 2;
            // Marcar el segmento dispara Checked -> ShowConfigPanel, pero solo si CAMBIA: si
            // ya estaba marcado no salta nada, asi que la llamada directa va detras. Las dos
            // juntas son idempotentes (solo alternan visibilidades) y dejan el segmento y el
            // panel siempre de acuerdo.
            ConfigTabBtn.IsChecked = true;
            ShowConfigPanel(this, null!);
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al abrir el enlace: {ex.Message}");
            }
        }

        // Sub-nav del hub "Mando": Configurar el mando (por defecto) | Luces del mando | Perfiles.
        //
        // Los tres empiezan comprobando que los paneles existen. Son handlers de Checked de un
        // RadioButton, y ese evento puede llegar durante el parseo del XAML - antes de que los
        // paneles hermanos esten creados. Ya tumbo la app una vez.
        private bool PanelsReady => ConfigPanel != null && LucesPanel != null && PerfilesPanel != null;

        private void ShowConfigPanel(object sender, RoutedEventArgs e)
        {
            if (!PanelsReady) return;
            ConfigPanel.Visibility = Visibility.Visible;
            LucesPanel.Visibility = Visibility.Collapsed;
            PerfilesPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowLucesPanel(object sender, RoutedEventArgs e)
        {
            if (!PanelsReady) return;
            ConfigPanel.Visibility = Visibility.Collapsed;
            LucesPanel.Visibility = Visibility.Visible;
            PerfilesPanel.Visibility = Visibility.Collapsed;
            RefreshPlayStationDevices();   // igual que hoy al entrar a la luz
        }

        private void ShowPerfilesPanel(object sender, RoutedEventArgs e)
        {
            if (!PanelsReady) return;
            ConfigPanel.Visibility = Visibility.Collapsed;
            LucesPanel.Visibility = Visibility.Collapsed;
            PerfilesPanel.Visibility = Visibility.Visible;

            // Aplicar un perfil escribe la luz, y eso necesita _lightPadId resuelto. Sin esta
            // llamada, entrar directo a PERFILES sin pasar por LUCES dejaria la mitad de luz
            // del perfil sin efecto y en silencio.
            RefreshPlayStationDevices();
            LoadGameProfiles();
        }

        // ===== Hub del configurador (Task PS3): navegacion + animaciones =====
        //
        // Las 4 tarjetas del hub entran a su pagina; ConfigBack_Click vuelve. Un unico
        // sitio (UpdateConfigPages) decide que Grid se ve, asi que no hay dos caminos que
        // puedan dejar dos paginas visibles a la vez.
        private void GoToBotones_Click(object sender, RoutedEventArgs e) => GoToPage(ConfigPage.Botones);
        private void GoToSticks_Click(object sender, RoutedEventArgs e) => GoToPage(ConfigPage.Sticks);
        private void GoToGatillos_Click(object sender, RoutedEventArgs e) => GoToPage(ConfigPage.Gatillos);
        private void GoToTouchpad_Click(object sender, RoutedEventArgs e) => GoToPage(ConfigPage.Touchpad);

        private void GoToPage(ConfigPage page)
        {
            _configNav.Go(page);
            UpdateConfigPages();
        }

        private void ConfigBack_Click(object sender, RoutedEventArgs e)
        {
            _configNav.Back();
            UpdateConfigPages();
        }

        private void UpdateConfigPages()
        {
            var target = _configNav.Current;

            // El diagrama (Task BT2) se construye la primera vez que se entra a la pagina,
            // no en Window_Loaded: 16 grupos de Shape/Button de mas no cuestan nada mientras
            // el usuario no visite BOTONES, y BuildButtonDiagram() es idempotente.
            if (target == ConfigPage.Botones) BuildButtonDiagram();

            ConfigHub.Visibility      = Vis(ConfigPage.Hub);
            PageBotones.Visibility    = Vis(ConfigPage.Botones);
            PageSticks.Visibility     = Vis(ConfigPage.Sticks);
            PageGatillos.Visibility   = Vis(ConfigPage.Gatillos);
            PageTouchpad.Visibility   = Vis(ConfigPage.Touchpad);

            Visibility Vis(ConfigPage p) => target == p ? Visibility.Visible : Visibility.Collapsed;

            // Navegacion de un solo nivel (hub <-> pagina, ver ConfigNav): la direccion
            // sale sola de si el destino es el hub (volviendo, desde la izquierda) o una
            // pagina (entrando, desde la derecha).
            Grid entering = target switch
            {
                ConfigPage.Botones  => PageBotones,
                ConfigPage.Sticks   => PageSticks,
                ConfigPage.Gatillos => PageGatillos,
                ConfigPage.Touchpad => PageTouchpad,
                _                   => ConfigHub,
            };
            AnimatePageEnter(entering, fromRight: target != ConfigPage.Hub);

            // El mando es el protagonista (plan de animacion): al volver al hub, ademas
            // del deslizamiento generico de la pagina, tiene su propio fade+escala.
            if (target == ConfigPage.Hub) AnimateLivePadEnter();
        }

        private static readonly Duration PageTransitionDuration = new Duration(TimeSpan.FromMilliseconds(160));
        private static readonly Duration LivePadEnterDuration = new Duration(TimeSpan.FromMilliseconds(220));
        private static readonly Duration PopupOpenDuration = new Duration(TimeSpan.FromMilliseconds(120));

        // Fade 0->1 + deslizamiento de 18px (desde la derecha al entrar a una pagina,
        // desde la izquierda al volver al hub), 160ms CubicEase EaseOut - la capa de
        // animacion del plan. Solo la pagina ENTRANTE se anima; la saliente ya esta
        // Collapsed en el mismo UpdateConfigPages(), sin transicion de salida.
        private void AnimatePageEnter(Grid grid, bool fromRight)
        {
            if (grid.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                grid.RenderTransform = tt;
            }
            double startX = fromRight ? 18 : -18;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(startX, 0, PageTransitionDuration) { EasingFunction = ease });
            grid.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, PageTransitionDuration) { EasingFunction = ease });
        }

        // "El mando es el protagonista": al entrar al hub, el mando en vivo tiene su
        // propio fade + escala 0.98->1.0 en 220ms, encima del deslizamiento generico
        // de ConfigHub. Nunca se aplica a Update()/VisualizerTick - eso es entrada en
        // vivo del mando, y esa regla no se toca.
        private void AnimateLivePadEnter()
        {
            if (ConfigPadVisual.RenderTransform is not ScaleTransform st)
            {
                st = new ScaleTransform(1, 1);
                ConfigPadVisual.RenderTransform = st;
                ConfigPadVisual.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.98, 1.0, LivePadEnterDuration) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.98, 1.0, LivePadEnterDuration) { EasingFunction = ease });
            ConfigPadVisual.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, LivePadEnterDuration) { EasingFunction = ease });
        }

        // Fade + escala 0.96->1.0 en 120ms para cualquier Popup de ayuda/opciones del
        // configurador (la "?" del hub y de cada pagina, la tuerca del mando en vivo).
        // Un unico handler, cableado por XAML a Popup.Opened en los seis popups: cada
        // uno anima su propio Child, asi que no hace falta un metodo por popup.
        private void AnimatePopupOpen(object sender, EventArgs e)
        {
            if (sender is not Popup popup || popup.Child is not FrameworkElement content) return;

            if (content.RenderTransform is not ScaleTransform scale)
            {
                scale = new ScaleTransform(0.96, 0.96);
                content.RenderTransform = scale;
                content.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            content.Opacity = 0;
            scale.ScaleX = 0.96;
            scale.ScaleY = 0.96;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, PopupOpenDuration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1.0, PopupOpenDuration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1.0, PopupOpenDuration) { EasingFunction = ease });
        }

        // Toggles de los popups "?"/tuerca: un Popup no tiene un Click propio, asi que
        // cada boton redondo alterna IsOpen a mano. StaysOpen="False" en el XAML cierra
        // solo al perder foco/clic fuera; este toggle es lo que lo ABRE.
        private void MasterHelp_Click(object sender, RoutedEventArgs e) => MasterHelpPopup.IsOpen = !MasterHelpPopup.IsOpen;
        private void LiveOptions_Click(object sender, RoutedEventArgs e) => LiveOptionsPopup.IsOpen = !LiveOptionsPopup.IsOpen;
        private void BotonesHelp_Click(object sender, RoutedEventArgs e) => BotonesHelpPopup.IsOpen = !BotonesHelpPopup.IsOpen;
        private void SticksHelp_Click(object sender, RoutedEventArgs e) => SticksHelpPopup.IsOpen = !SticksHelpPopup.IsOpen;
        private void GatillosHelp_Click(object sender, RoutedEventArgs e) => GatillosHelpPopup.IsOpen = !GatillosHelpPopup.IsOpen;
        private void TouchpadHelp_Click(object sender, RoutedEventArgs e) => TouchpadHelpPopup.IsOpen = !TouchpadHelpPopup.IsOpen;

        // ===== MOTOR DEL MANDO VIRTUAL (interruptor maestro, Tasks 6/7/14) =====
        //
        // El lazo completo del remapeador: leer el DualSense fisico (DualSenseReader),
        // transformarlo con los ajustes del configurador (RemapEngine + _remap, en vivo) y
        // empujarlo a un DS4 virtual (VirtualPad/ViGEm), con el fisico oculto para las demas
        // apps (HidHideControl). Apagado por defecto: sin activar, el juego ve el DualSense
        // nativo y esta app no toca nada. Crecio del spike de la Fase 2, ya validado en
        // hardware real (lectura ~8kHz, ocultado y revert comprobados en joy.cpl).
        private DualSenseReader? _padReader;
        private VirtualPad? _padVirtual;
        private HidHideControl? _padHidHide;
        private DispatcherTimer? _engineTimer;
        private bool _engineRunning;
        private int _engineTick;
        private string? _hideError;

        // True while Start/StopEngine's background thread is doing the heavy device work
        // (HidHide hide/revert, which includes a PnP devnode remove+re-enumerate). That
        // restart raises our own WM_DEVICECHANGE, which the debounced handler above would
        // otherwise answer with a ~1s RefreshDevicesList() PowerShell scan; this guard
        // (mirrors _overclockBusy) skips that self-inflicted rescan.
        private volatile bool _engineBusy;

        // Alto mientras el codigo (y no el usuario) mueve el interruptor maestro: si arrancar
        // falla hay que devolverlo a apagado, y eso no puede volver a entrar aqui e intentar
        // pararlo.
        private bool _updatingMasterSwitch;

        private void SetMasterSwitch(bool on)
        {
            _updatingMasterSwitch = true;
            try { MasterToggleBtn.IsChecked = on; }
            finally { _updatingMasterSwitch = false; }
        }

        private async void MasterToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingMasterSwitch) return;
            if (MasterToggleBtn.IsChecked == true) await StartEngine();
            else await StopEngine();
        }

        // Everything that can block for a while - ViGEm connect, opening the HID device,
        // and HidHide's hide (which best-effort restarts the DualSense's devnode, a slow
        // PnP remove+re-enumerate) - runs on a background thread via StartEngineDevices() so
        // the UI thread never stalls for it. Object construction and the exe path are read
        // here on the UI thread (cheap, no device I/O); everything after 'await' resumes on
        // the UI thread automatically (WPF's SynchronizationContext), which is why the
        // DispatcherTimer and every *.Text/*.Content assignment below are safe as written.
        private async Task StartEngine()
        {
            // Sigue sin "Aplicando...": un texto que aparece y se va no se llega a leer, solo
            // parpadea, y la linea de estado esta reservada para lo que SI hay que contar (un
            // error de arranque o un revert parcial). La espera la dice el anillo: el
            // interruptor atenuado por si solo se lee como "deshabilitado", no como
            // "trabajando", y esto tarda varios segundos porque reinicia el devnode del mando.
            SetEngineBusyVisual(true);

            _padVirtual = new VirtualPad();
            _padReader = new DualSenseReader();
            _padHidHide = new HidHideControl();
            // Environment.ProcessPath es la ruta real del exe incluso en el publish de un
            // solo archivo (Assembly.Location ahi devuelve cadena vacia - IL3000 -, y una
            // whitelist vacia en HidHide nos ocultaria el mando a nosotros mismos).
            string exe = Environment.ProcessPath
                         ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                         ?? "";

            // Cierra el lector propio del visualizador ANTES de que el motor oculte/reinicie
            // el devnode del fisico: si quedara abierto, seria el handle que HidHide expulsa
            // (leccion L1). El feed pasara a usar _padReader en cuanto _engineRunning sea true.
            _visualFeed.StopOwnReader();

            _engineBusy = true;
            var result = await Task.Run(() => StartEngineDevices(exe));
            _engineBusy = false;

            if (!result.Success)
            {
                SetMasterStatus(result.FailedStage == "virtual"
                    ? "Error creando el DS4 virtual: " + result.Error
                    : "Error leyendo el DualSense: " + result.Error);
                CleanupEngine();
                // El arranque fallo (p. ej. sin ViGEmBus/HidHide): el fisico sigue visible, asi que
                // reabrimos el lector propio del visualizador (que StartEngine cerro por la leccion L1),
                // o el mando en vivo se queda congelado. Espeja la recuperacion de StopEngine.
                if (ConfigPadVisual.IsVisible) _visualFeed.StartOwnReader();
                // El interruptor se movio al pulsarlo, pero el motor NO arranco: devolverlo a
                // apagado. Dejarlo encendido seria la peor version de este control - decir que
                // el juego ve tu configuracion cuando sigue viendo el mando nativo.
                SetMasterSwitch(false);
                SetEngineBusyVisual(false);
                return;
            }

            _hideError = result.HideError;
            _engineRunning = true;
            _engineTick = 0;

            // El DispatcherTimer se crea/arranca en el hilo de UI (no es seguro entre
            // hilos; construirlo desde el hilo de fondo lo asociaria a un dispatcher ad-hoc
            // de ese hilo, que nunca bombea, y el passthrough nunca avanzaria).
            _engineTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(8)
            };
            _engineTimer.Tick += EngineTick;
            _engineTimer.Start();
            UpdateEngineStatus();
            SetEngineBusyVisual(false);
        }

        // Trabajo puro de dispositivo (ViGEm/HID/HidHide), sin tocar ningun control de UI,
        // para que sea seguro ejecutarlo en el hilo de fondo que arma StartEngine().
        private (bool Success, string? FailedStage, string? Error, string? HideError) StartEngineDevices(string exe)
        {
            // Orden al arrancar (importa mucho):
            //  1. Virtual PRIMERO, para que ningun juego vea cero mandos durante el cambio.
            //  2. OCULTAR el fisico ANTES de abrir nuestro lector. HidHide reinicia el
            //     devnode del mando (RemoveAndSetup), lo que fuerza el cierre de TODO handle
            //     abierto al mando. Si nuestro lector ya estuviera abierto, seria justo ese
            //     el handle que se expulsa -> el lector moria y el passthrough se congelaba
            //     (los reportes se quedaban clavados). Ocultar primero evita la expulsion.
            //     HidHide resuelve el ID de instancia del fisico por su cuenta, asi que no
            //     necesita el DevicePath del lector para esto.
            //  3. RECIEN AHORA abrir el lector, contra el devnode ya re-enumerado (y oculto
            //     para las demas apps). Nuestro exe esta en la whitelist, asi que nosotros
            //     si podemos abrirlo; Start() reintenta unos segundos porque el devnode
            //     necesita un momento para volver tras el reinicio.
            var v = _padVirtual!.Connect();
            if (!v.Success) return (false, "virtual", v.Error, null);

            // Ocultar es best-effort: si falla, el fisico queda VISIBLE (el estado seguro) y
            // el motor igual corre lector + virtual. El error se muestra pero no aborta.
            var h = _padHidHide!.HideDualSense(exe, "");
            string? hideError = h.Success ? null : h.Error;

            var r = _padReader!.Start();
            if (!r.Success)
            {
                // El lector no pudo abrir tras el reinicio del devnode: revertir el ocultado
                // (nunca dejar el fisico oculto sin nada que lo lea) y soltar el virtual.
                try { _padHidHide.Revert(); } catch { }
                _padVirtual.Disconnect();
                return (false, "reader", r.Error, null);
            }

            return (true, null, null, hideError);
        }

        private void EngineTick(object? sender, EventArgs e)
        {
            var reader = _padReader;
            var virt = _padVirtual;
            if (!_engineRunning || reader == null || virt == null) return;
            // Aplica los ajustes de la UI (deadzone/curvas/gatillos/remapeo/touchpad) en vivo:
            // _remap es el MISMO objeto que editan los controles del configurador, y tanto la
            // edicion como este tick corren en el hilo de UI, asi que leerlo aqui es seguro y
            // cualquier cambio de slider se refleja en el mando virtual en el acto.
            virt.Push(RemapEngine.Transform(reader.Snapshot(), _remap));
            if (++_engineTick % 15 == 0) UpdateEngineStatus();
        }

        // Con el motor CORRIENDO BIEN esto se calla: el interruptor ya dice que esta
        // encendido, y repetirlo con un contador que sube era ruido.
        //
        // Pero solo se calla si todo esta bien. Esta linea es la unica superficie que
        // distingue "encendido" de "encendido a medias", y ese es justo el estado que hay que
        // poder ver: el interruptor puesto mientras el fisico sigue visible para el juego, o
        // el virtual conectado sin que llegue un solo reporte (el lector congelado que ya
        // aparecio una vez). Un interruptor que dice ON sobre un motor a medias es una
        // mentira; enumerar las piezas cuando alguna falla es lo que lo impide.
        private void UpdateEngineStatus()
        {
            if (_padReader == null || _padVirtual == null || _padHidHide == null) return;

            bool oculto = _padHidHide.IsHiding;
            bool conectado = _padVirtual.Connected;
            long reportes = _padReader.ReportsRead;

            // Los reportes tardan unos cuadros en arrancar; solo se considera un fallo tras
            // ~1 s corriendo, para no acusar de congelado a un motor que acaba de encenderse.
            bool sinReportes = reportes == 0 && _engineTick > 60;

            if (oculto && conectado && !sinReportes && _hideError == null)
            {
                SetMasterStatus("");
                return;
            }

            string fisico = oculto ? "fisico OCULTO" : "fisico VISIBLE (el juego sigue viendo tu DualSense)";
            string virt = conectado ? "virtual ACTIVO" : "virtual INACTIVO";
            string flujo = sinReportes ? " / SIN reportes: el mando no esta enviando nada" : "";
            string extra = _hideError == null ? "" : $"  (HidHide no oculto: {_hideError})";
            SetMasterStatus($"A medias - {fisico} / {virt}{flujo}{extra}");
        }

        // Trabajo puro de dispositivo (sin tocar ningun control de UI): revierte HidHide
        // (incluye su propio restart de devnode, lento), para el lector (Join sobre el
        // hilo lector) y desconecta el virtual. Orden de seguridad: MOSTRAR el fisico
        // primero, luego parar el lector y desconectar el virtual, para que nunca haya una
        // ventana sin ningun mando. Seguro de llamar desde el hilo de UI (OnClosing, donde
        // la app ya se esta cerrando y bloquear brevemente no es el freeze reportado) o
        // desde un hilo de fondo (StopEngine, via Task.Run, para no bloquear la UI en uso
        // normal).
        private string? RevertEngineDevices()
        {
            string? revertErr = null;
            try { revertErr = _padHidHide?.Revert().Error; }
            catch (Exception ex) { revertErr = ex.Message; }
            try { _padReader?.Stop(); } catch { }
            try { _padVirtual?.Disconnect(); } catch { }
            return revertErr;
        }

        private async Task StopEngine()
        {
            // Igual que al arrancar, y por el mismo motivo: quitar el mando virtual devuelve el
            // fisico reiniciando su devnode, que tarda lo mismo que ocultarlo. El anillo cubre
            // las dos operaciones porque las dos hacen esperar.
            SetEngineBusyVisual(true);

            // El timer del passthrough vive y muere en el hilo de UI; pararlo aqui, antes
            // del trabajo pesado de fondo, deja de empujar reportes al virtual de inmediato.
            if (_engineTimer != null)
            {
                _engineTimer.Stop();
                _engineTimer.Tick -= EngineTick;
                _engineTimer = null;
            }
            _engineRunning = false;

            _engineBusy = true;
            string? revertErr = await Task.Run(() => RevertEngineDevices());
            _engineBusy = false;

            // Estado apagado = sin texto (el interruptor ya lo dice). Un revert PARCIAL si se
            // cuenta: ahi el mando pudo quedar en un estado raro y el usuario debe saberlo.
            SetMasterStatus(revertErr == null
                ? ""
                : $"Revert parcial: {revertErr}. Revisa joy.cpl.");
            CleanupEngine();
            // El fisico volvio a estar visible: si el visualizador sigue en pantalla,
            // recupera su propia fuente (antes usaba _padReader, que ya no existe).
            if (ConfigPadVisual.IsVisible) _visualFeed.StartOwnReader();
            SetEngineBusyVisual(false);
        }

        private void CleanupEngine()
        {
            _padReader = null;
            _padVirtual = null;
            _padHidHide = null;
            _hideError = null;
            _engineRunning = false;
        }

        // ===== Mando en vivo (Task VZ3): PadVisual del configurador alimentado por la salida
        // TRANSFORMADA del DualSense fisico, a ~60fps =====
        //
        // VisualizerFeed decide la fuente cuando el motor esta apagado (lector propio, de solo
        // lectura sobre el fisico visible); cuando el motor esta encendido usamos el snapshot
        // del propio _padReader del motor para no competir por un segundo handle contra el
        // reinicio de devnode del arranque (leccion L1).
        private readonly VisualizerFeed _visualFeed = new();
        private DispatcherTimer? _visualTimer;

        // Arranca el visualizador: timer ~60fps + (si el motor esta apagado) lector propio.
        // Idempotente.
        private void StartVisualizer()
        {
            // _engineBusy tambien: durante el arranque/parada del motor (que reinicia el devnode) no
            // debemos abrir un lector propio, o su handle muere en el restart (leccion L1). _engineRunning
            // solo pasa a true/false al terminar el trabajo de fondo, asi que se necesita el guard de busy.
            if (!_engineRunning && !_engineBusy) _visualFeed.StartOwnReader();
            if (_visualTimer == null)
            {
                _visualTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
                _visualTimer.Tick += VisualizerTick;
            }
            _visualTimer.Start();
        }

        private void StopVisualizer()
        {
            _visualTimer?.Stop();
            _visualFeed.StopOwnReader();
        }

        // El feed corre si ALGUNA pagina del configurador esta visible (Task PS3: el mando
        // en vivo vive solo en el hub, asi que ConfigPadVisual.IsVisible ya no basta - se
        // apagaria al entrar a una sub-pagina como Botones o Gatillos) O si la ventana
        // streamer sigue abierta (el overlay debe seguir vivo aunque el usuario navegue a
        // otra pestana). ConfigPanel es el padre comun del hub y las 4 sub-paginas, asi
        // que su IsVisible ya capta "estamos en Mando > Configurar", sea cual sea la
        // pagina interna. ConfigPadVisual.Update() seguir corriendo con el pad Collapsed
        // es inofensivo (actualizar un control invisible es barato y no dibuja nada).
        private void UpdateVisualizerRunState()
        {
            if (ConfigPanel.IsVisible || _streamerWindow != null) StartVisualizer();
            else StopVisualizer();
        }

        private void VisualizerTick(object? sender, EventArgs e)
        {
            // Fuente: el lector del motor si esta activo (no abrimos segundo handle), si no el propio.
            ControllerState? raw = _engineRunning ? _padReader?.Snapshot() : _visualFeed.PhysicalSnapshot();
            if (raw == null) return;
            var outState = RemapEngine.Transform(raw, _remap);
            ConfigPadVisual.Update(outState);
            _streamerWindow?.Pad.Update(outState);
            UpdateCurveLiveDot(raw, outState);
        }

        // Alto mientras el codigo (y no el usuario) mueve los interruptores del panel: el
        // overlay se puede cerrar desde su propia barra, y al apagar el interruptor desde
        // ahi no puede volver a entrar aqui y cerrarlo otra vez.
        private bool _updatingStreamerSwitches;

        // MODO STREAMER: ventana overlay transparente/siempre-encima con solo el mando, para
        // capturar en OBS. Es un ESTADO (abierto o cerrado), asi que es un interruptor y no un
        // boton: antes habia que leer el texto ("MODO STREAMER" / "CERRAR STREAMER") para
        // saber en cual de los dos estabas.
        private void StreamerToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingStreamerSwitches) return;

            if (StreamerToggle.IsChecked == true)
            {
                if (_streamerWindow != null) return;   // ya abierto

                _streamerWindow = new StreamerWindow { Owner = this };
                _streamerWindow.Closed += (_, _) =>
                {
                    _streamerWindow = null;
                    // El overlay se puede cerrar desde su propia barra, no solo desde aqui:
                    // los dos interruptores tienen que volver solos a su sitio.
                    _updatingStreamerSwitches = true;
                    try
                    {
                        StreamerToggle.IsChecked = false;
                        StreamerClickThrough.IsChecked = false;
                        StreamerClickThrough.IsEnabled = false;
                    }
                    finally { _updatingStreamerSwitches = false; }
                    UpdateVisualizerRunState();   // para el feed si el pad tampoco esta visible
                };
                _streamerWindow.Show();
                StreamerClickThrough.IsEnabled = true;
                UpdateVisualizerRunState();   // asegura el feed vivo aunque el foco cambie despues
            }
            else
            {
                _streamerWindow?.Close();   // el handler Closed deja los dos interruptores en su sitio
            }
        }

        // Apaga (o prende) el pasa-clic del overlay desde la ventana principal, que nunca es
        // ella misma click-through: es la unica via no destructiva de recuperar el toolbar del
        // overlay una vez que el pasa-clic lo vuelve inalcanzable (ver StreamerWindow.SetClickThrough).
        private void StreamerClickThrough_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingStreamerSwitches) return;
            _streamerWindow?.SetClickThrough(StreamerClickThrough.IsChecked == true);
        }

        // Mueve el punto vivo de cada canvas a (entrada, salida) segun el stick actual. La entrada
        // es la MAGNITUD cruda del stick (0..1); la salida, la misma curva que dibuja DrawCurve
        // (via InputTransform.ApplyStick), asi el punto cae exactamente sobre la polilinea. Solo se
        // muestra si el stick esta fuera de la zona muerta (si no, no hay nada que ver en el centro).
        private void UpdateCurveLiveDot(ControllerState raw, ControllerState outState)
        {
            PlaceLiveDot(LeftCurveCanvas, LeftCurveLiveDot, raw.Left, outState.Left,
                _remap.LeftInnerDeadzone, _remap.LeftOuterDeadzone);
            PlaceLiveDot(RightCurveCanvas, RightCurveLiveDot, raw.Right, outState.Right,
                _remap.RightInnerDeadzone, _remap.RightOuterDeadzone);
        }

        // La curva mapea magnitud de entrada -> magnitud de salida. X del canvas = entrada cruda
        // (0..1), Y = salida (0..1, invertida). Coincide con DrawCurve, que muestrea ApplyStick
        // sobre un stick horizontal. Se oculta dentro de la zona muerta (magnitud de salida 0).
        private void PlaceLiveDot(Canvas canvas, System.Windows.Shapes.Ellipse dot,
            StickInput inRaw, StickInput outT, double inner, double outer)
        {
            double inMag = Math.Min(1.0, Math.Sqrt(inRaw.X * inRaw.X + inRaw.Y * inRaw.Y));
            double outMag = Math.Min(1.0, Math.Sqrt(outT.X * outT.X + outT.Y * outT.Y));
            if (outMag <= 0.0001)
            {
                dot.Visibility = Visibility.Collapsed;
                return;
            }
            Canvas.SetLeft(dot, inMag * canvas.Width - dot.Width / 2);
            Canvas.SetTop(dot, (1 - outMag) * canvas.Height - dot.Height / 2);
            dot.Visibility = Visibility.Visible;
        }

        // ===== Configurador del mando: edita _remap y persiste via perfiles (Task 4) =====
        //
        // _remap es el objeto vivo: EngineTick lo lee en cada frame cuando el mando virtual
        // esta activo, asi que cualquier cambio aqui (slider, combo, CARGAR un perfil) se
        // aplica al juego en el acto, sin boton de "aplicar".

        // El estado que edita toda la pestana STICKS/GATILLOS/BOTONES/TOUCHPAD.
        private RemapSettings _remap = new();

        // Igual que _updatingLight: true mientras el codigo (CARGAR, o el build inicial)
        // mueve los controles, para que esos cambios programaticos no se interpreten como
        // una edicion del usuario ni disparen el guardado debounced.
        private bool _updatingRemap;

        private bool _remapControlsBuilt;
        private List<RemapProfile> _remapProfiles = new();
        private List<SavedCurve> _savedCurves = new();
        private readonly List<(TouchZone Zone, ComboBox Combo)> _touchZoneRows = new();

        // Diagrama de botones (Task BT2): construido una sola vez (_diagramBuilt) al entrar
        // por primera vez a PageBotones. _pills mapea cada boton de origen a su pildora, para
        // que RefreshButtonPills() pueda actualizar texto/resalte sin reconstruir nada.
        // _pickerSource recuerda que pildora abrio el picker mientras el Popup esta abierto.
        private readonly Dictionary<PadButton, Button> _pills = new();
        private bool _diagramBuilt;

        // true = hay imagen y el panel se pinta claro (tinta negra); false = respaldo
        // vectorial sobre panel oscuro. Lo fija BuildButtonDiagram y lo leen todos los
        // colores del diagrama, para que no haya dos criterios distintos.
        private bool _diagramIsLight;

        // Modo del diagrama, leido de las preferencias la primera vez que se construye.
        private AppTheme _diagramMode = AppTheme.Noche;
        private bool _diagramModeLoaded;

        // Alto mientras el codigo (no el usuario) mueve DiagramDayCheck.
        private bool _updatingDiagramMode;

        private PadButton _pickerSource;

        // Nombre reservado bajo el que se autoguarda el ultimo estado (distinto de los
        // perfiles con nombre que el usuario ve en RemapProfileList), para que cerrar y
        // reabrir la app mantenga el ajuste activo sin que el usuario tenga que pulsar
        // GUARDAR primero. Vive en el mismo remap-profiles.json que RemapProfileStore ya
        // maneja - no hace falta un almacen nuevo para esto.
        private const string LastUsedProfileName = "__ultimo_usado__";

        // Botones de destino disponibles en todo remapeo (botones + zonas de touchpad).
        // Ninguno primero: es la opcion neutra para una zona de touchpad sin asignar.
        private static readonly (string Label, PadButton Value)[] RemapTargets =
        {
            ("Ninguno", PadButton.None),
            ("Cruz", PadButton.Cross),
            ("Circulo", PadButton.Circle),
            ("Cuadrado", PadButton.Square),
            ("Triangulo", PadButton.Triangle),
            ("Cruceta arriba", PadButton.DpadUp),
            ("Cruceta abajo", PadButton.DpadDown),
            ("Cruceta izquierda", PadButton.DpadLeft),
            ("Cruceta derecha", PadButton.DpadRight),
            ("L1", PadButton.L1),
            ("R1", PadButton.R1),
            ("L2", PadButton.L2),
            ("R2", PadButton.R2),
            ("L3", PadButton.L3),
            ("R3", PadButton.R3),
            ("Compartir", PadButton.Share),
            ("Opciones", PadButton.Options),
            ("PS", PadButton.PS),
            ("Touchpad (click)", PadButton.TouchpadClick),
        };

        // Los 16 botones de origen remapeables (excluye PS y el click del touchpad) ya no
        // necesitan su propia lista aqui: son exactamente PadDiagram.Anchors, que
        // BuildButtonDiagram() recorre directamente.

        private static readonly (string Label, TouchZone Value)[] TouchZones =
        {
            ("Arriba izquierda", TouchZone.ArribaIzq),
            ("Arriba derecha", TouchZone.ArribaDer),
            ("Abajo izquierda", TouchZone.AbajoIzq),
            ("Abajo derecha", TouchZone.AbajoDer),
        };

        // Construye las filas dinamicas (Botones/Touchpad), carga el ultimo estado
        // guardado (si hay) y refleja todo en los controles. Se llama una vez desde
        // Window_Loaded; idempotente por si algo mas la vuelve a llamar.
        private void BuildRemapControls()
        {
            if (_remapControlsBuilt) return;
            _remapControlsBuilt = true;

            BuildTouchZoneCombos();
            BuildCurveLists();
            RefreshCurveLibraryLists();

            _remapProfiles = RemapProfileStore.Load();
            var last = _remapProfiles.FirstOrDefault(p => p.Name == LastUsedProfileName);
            if (last != null) _remap = CloneRemapSettings(last.Settings);
            _remap.Sanitize();   // perfiles viejos con presets retirados -> Lineal (ver RemapSettings)

            try
            {
                _updatingRemap = true;
                ApplyRemapSettingsToControls();
            }
            finally
            {
                _updatingRemap = false;
            }

            CheckEngineDrivers();

            RefreshSkinStatus();
        }

        // Task SK3: recarga el skin instalado (por si se instalo/actualizo con la app
        // abierta) tanto en el pad del configurador como en el del streamer si esta abierto.
        private void ReloadSkin_Click(object sender, RoutedEventArgs e)
        {
            ConfigPadVisual.ReloadSkin();
            _streamerWindow?.Pad.ReloadSkin();
            RefreshSkinStatus();
        }

        // Que se esta dibujando (el skin instalado o el mando vectorial) ya no ocupa una linea
        // de texto en el panel: vive en el ToolTip del boton que lo recarga, que es el unico
        // sitio donde hace falta saberlo. La informacion no se pierde, deja de estorbar.
        private void RefreshSkinStatus() => ReloadSkinBtn.ToolTip = ConfigPadVisual.StatusText;

        // Checked/Unchecked y no Click, aqui y en los dos de abajo: Click solo cubre el clic
        // de raton y cualquier otro camino que cambie el estado se lo salta.
        private void Calibration_Changed(object sender, RoutedEventArgs e)
            => ConfigPadVisual.ShowCalibration = CalibrationCheck.IsChecked == true;

        // Si falta ViGEmBus o HidHide, el interruptor maestro se desactiva y el estado dice
        // exactamente que instalar; sin drivers el mando virtual no puede existir y el juego
        // sigue viendo el DualSense nativo (el estado seguro). La deteccion consulta el SCM
        // (DriverCheck, sin efectos secundarios) en un hilo de fondo para no tocar la UI.
        private async void CheckEngineDrivers()
        {
            var (vigem, hidhide) = await Task.Run(DriverCheck.Detect);
            if (vigem && hidhide) return;   // ambos instalados: el interruptor queda operativo

            MasterToggleBtn.IsEnabled = false;
            string faltan = (!vigem && !hidhide) ? "ViGEmBus y HidHide"
                          : !vigem ? "ViGEmBus" : "HidHide";
            SetMasterStatus($"Falta instalar {faltan} (drivers de Nefarius). Sin eso no hay " +
                            "mando virtual; el juego sigue viendo tu DualSense nativo.");
        }

        // ===== Diagrama de botones (Task BT2) =====
        //
        // El diagrama se construye una sola vez (_diagramBuilt), asi que cambiar de modo obliga a
        // rehacerlo entero: la lamina, las 16 lineas y las 16 pildoras se crearon con los pinceles
        // del modo anterior y no hay forma de retintarlas sin recorrerlas una a una.
        private void RebuildButtonDiagram()
        {
            DiagramCanvas.Children.Clear();
            _pills.Clear();
            _diagramBuilt = false;
            BuildButtonDiagram();
        }

        // Se construye una sola vez: la imagen y las 16 lineas no cambian, solo el texto de
        // las etiquetas (RefreshButtonPills) y su resalte. La llama UpdateConfigPages() al
        // entrar a PageBotones por primera vez; _diagramBuilt la hace idempotente.
        private void BuildButtonDiagram()
        {
            if (_diagramBuilt) return;
            _diagramBuilt = true;

            if (!_diagramModeLoaded)
            {
                _diagramMode = UiPrefsStore.Load().Theme;
                _diagramModeLoaded = true;
                _updatingDiagramMode = true;
                try { DiagramDayCheck.IsChecked = _diagramMode == AppTheme.Dia; }
                finally { _updatingDiagramMode = false; }
            }

            DiagramCanvas.Width = PadDiagram.CanvasWidth;
            DiagramCanvas.Height = PadDiagram.CanvasHeight;

            // Fondo: la imagen del mando si esta disponible; si no, el vectorial en estatico.
            // UNA bandera gobierna las tres decisiones de color (papel del panel, estilo de
            // pildora, tinta de las lineas), para que no exista la combinacion imposible de
            // un panel blanco con un mando dibujado para fondo negro.
            var bg = TryLoadDiagramImage(_diagramMode);
            // Claro SOLO en modo dia y con su lamina cargada. En noche el panel es oscuro, igual que
            // con el respaldo vectorial, asi que las tres decisiones de color de abajo no distinguen
            // entre "noche con imagen" y "sin imagen": el papel de la lamina de noche es exactamente
            // el SurfaceBrush del panel.
            _diagramIsLight = bg != null && _diagramMode == AppTheme.Dia;

            DiagramPanel.Background = (Brush)FindResource(_diagramIsLight ? "DiagramPaperBrush" : "SurfaceBrush");

            if (bg != null)
            {
                var img = new Image { Source = bg, Width = PadDiagram.DiagramWidth, Height = PadDiagram.DiagramHeight };
                // La imagen va CENTRADA en el lienzo ancho; todo lo medido sobre ella se
                // desplaza por PadDiagram.AnchorX.
                Canvas.SetLeft(img, PadDiagram.ImageOffsetX); Canvas.SetTop(img, 0);
                DiagramCanvas.Children.Add(img);
            }
            else
            {
                // Sin imagen no se deja la pagina en blanco: se dibuja el mando vectorial,
                // escalado al hueco de la imagen (2400x1792) dentro del lienzo. PadVisual
                // escala su Canvas interno de 360x260 con Stretch=Uniform y lo centra, asi
                // que al forzar Width/Height a 2400x1792 el resultado llena ese hueco casi
                // entero (el limitante es el ancho: escala x6.667, alto resultante ~1733 de
                // 1792, un letterbox de ~30px arriba/abajo) - no queda encogido en una esquina.
                var fallback = new PadVisual { Width = PadDiagram.DiagramWidth, Height = PadDiagram.DiagramHeight };
                Canvas.SetLeft(fallback, PadDiagram.ImageOffsetX); Canvas.SetTop(fallback, 0);
                DiagramCanvas.Children.Add(fallback);
            }

            foreach (var side in new[] { true, false })
            {
                var anchors = PadDiagram.Anchors.Where(a => a.Left == side).ToList();
                // minGap 110 y no 70: la etiqueta pasa de 26 a 44 px de cuerpo y ademas lleva
                // iconos, asi que es bastante mas alta y con el hueco de antes se pisarian.
                var placed = PadDiagram.LayoutLabels(anchors, 110);

                foreach (var (button, lx, ly) in placed)
                {
                    var a = anchors.First(z => z.Button == button);

                    // Linea guia: del ancla (en coordenadas del lienzo) al borde interior de
                    // la columna, a la altura repartida.
                    var line = new Line
                    {
                        X1 = PadDiagram.AnchorX(a), Y1 = a.Y, X2 = lx, Y2 = ly,
                        // Sobre el panel claro la guia va en tinta; sobre el oscuro, en
                        // TextLabelBrush - no en BorderBrush, que a #1F1F1F era practicamente
                        // invisible. Una guia que no se ve no guia.
                        Stroke = _diagramIsLight ? (Brush)FindResource("DiagramInkBrush") : (Brush)FindResource("TextLabelBrush"),
                        StrokeThickness = _diagramIsLight ? 4 : 3,
                        IsHitTestVisible = false,
                    };
                    DiagramCanvas.Children.Add(line);

                    var pill = new Button
                    {
                        Style = (Style)FindResource(_diagramIsLight ? "PillButtonInk" : "PillButton"),
                        Tag = button,
                        FontSize = 44,          // el lienzo mide 3600 px: la tipografia va a esa escala
                    };
                    pill.Click += ButtonPill_Click;
                    DiagramCanvas.Children.Add(pill);
                    _pills[button] = pill;

                    // La etiqueta se ancla por su borde interior: la columna izquierda crece
                    // hacia la izquierda, la derecha hacia la derecha. Se centra en vertical
                    // sobre su Y. OJO: se reposiciona en SizeChanged, no en Loaded - Loaded
                    // solo dispara una vez, pero RefreshButtonPills() cambia el texto (y por
                    // tanto el ancho) cada vez que el remapeo cambia, y una pildora mas ancha
                    // reposicionada solo en Loaded quedaria descuadrada de su columna.
                    // SizeChanged cubre tambien el primer layout (0 -> tamano real), asi que
                    // no hace falta Loaded ademas.
                    pill.SizeChanged += (_, _) =>
                    {
                        Canvas.SetLeft(pill, side ? lx - pill.ActualWidth : lx);
                        Canvas.SetTop(pill, ly - pill.ActualHeight / 2);
                    };
                }
            }

            RefreshButtonPills();
        }

        // Checked/Unchecked y no Click: el estado de este CheckBox gobierna que lamina se dibuja,
        // y Click solo cubre el clic de raton - cualquier otro camino que cambie IsChecked se lo
        // saltaba y el diagrama se quedaba en el modo anterior sin decir nada.
        //
        // El guard es necesario JUSTO por eso: BuildButtonDiagram pone IsChecked al leer la
        // preferencia guardada, y sin el eso dispararia una reconstruccion dentro de la propia
        // construccion.
        private void DiagramDay_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingDiagramMode) return;

            _diagramMode = DiagramDayCheck.IsChecked == true ? AppTheme.Dia : AppTheme.Noche;
            RebuildButtonDiagram();

            var prefs = UiPrefsStore.Load();
            prefs.Theme = _diagramMode;
            UiPrefsStore.Save(prefs);

            // Si la lamina del modo elegido no esta instalada se cae al mando vectorial, y eso hay
            // que decirlo: el usuario acaba de pulsar algo y veria un dibujo distinto sin motivo.
            if (!_diagramIsLight && _diagramMode == AppTheme.Dia)
                LogStatus("Falta diagram.png en la carpeta del skin: se dibuja el mando vectorial.");
            else
                LogStatus(_diagramMode == AppTheme.Dia ? "Diagrama en modo dia." : "Diagrama en modo noche.");
        }

        // Fondo del diagrama: la lamina del modo elegido, de la carpeta del skin instalado.
        //   Dia   -> diagram.png        (tinta oscura sobre papel blanco)
        //   Noche -> diagram_noche.png  (tinta clara sobre papel #0A0A0A)
        // Vive FUERA del repo por la misma razon que el resto del arte del skin (ver
        // DOCUMENTACION.md), asi que faltar es un caso normal, no un error: entonces se dibuja el
        // mando vectorial. Nunca lanza - una imagen rota jamas puede dejar la pagina en blanco.
        private ImageSource? TryLoadDiagramImage(AppTheme mode)
        {
            try
            {
                var dir = PadSkinLoader.FindFirstSkinDir(PadSkinLoader.DefaultSkinsRoot);
                if (dir == null) return null;

                string file = mode == AppTheme.Dia ? "diagram.png" : "diagram_noche.png";
                string path = System.IO.Path.Combine(dir, file);
                if (!System.IO.File.Exists(path)) return null;

                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                // OnLoad: no dejar bloqueado el archivo del usuario ni depender de que siga
                // ahi despues (puede mover o borrar el skin con la app abierta).
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        // Cada etiqueta dice A QUE envia su boton: solo su propio icono si no esta remapeado,
        // "origen -> destino" si lo esta (y entonces se resalta). Asi se ve el mapa entero de
        // un vistazo, sin pasar el raton por encima ni abrir nada - esa es la ventaja del
        // diagrama sobre la lista de combos que reemplaza.
        private void RefreshButtonPills()
        {
            foreach (var (button, pill) in _pills)
            {
                bool remapped = _remap.ButtonRemap.TryGetValue(button, out var target) && target != button;

                // Sin remapeo se muestra SOLO el origen: repetir "R2 -> R2" era pedirle al
                // usuario que comparase dos cadenas para deducir que no pasa nada.
                pill.Content = BuildPillContent(button, remapped ? target : (PadButton?)null);

                pill.Foreground = Ink;
                // Remapeado = borde de tinta; sin remapear = borde apagado. Sobre el panel
                // claro no vale jugar con el blanco, asi que la diferencia es el contraste
                // del borde, no el color del texto.
                pill.BorderBrush = _diagramIsLight
                    ? (Brush)FindResource(remapped ? "DiagramInkBrush" : "TextLabelBrush")
                    : (Brush)FindResource(remapped ? "TextLabelBrush" : "BorderBrush");
                pill.BorderThickness = new Thickness(remapped ? 4 : 2);
                pill.ToolTip = remapped
                    ? $"{FriendlyName(button)} envia {FriendlyName(target)}"
                    : $"{FriendlyName(button)} sin cambios";
            }
        }

        // Los dos pinceles del diagrama. Con imagen, el panel es claro y FIJO: su papel y su
        // tinta no siguen el tema, porque el PNG del mando es tinta oscura sobre papel blanco y
        // en modo dia se invertirian dejando papel negro bajo un dibujo blanco. Sin imagen, el
        // respaldo es el mando VECTORIAL, que si se dibuja con los pinceles del tema, asi que
        // ahi papel y tinta son los del tema y acompanan a dia/noche.
        private Brush Ink => _diagramIsLight
            ? (Brush)FindResource("DiagramInkBrush")
            : (Brush)FindResource("TextDataBrush");

        private Brush Paper => _diagramIsLight
            ? (Brush)FindResource("DiagramPaperBrush")
            : (Brush)FindResource("BgBrush");

        // Contenido de una etiqueta: [icono origen]  (->  [icono destino])
        private UIElement BuildPillContent(PadButton source, PadButton? target)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(BuildIcon(source));
            if (target != null)
            {
                row.Children.Add(new TextBlock
                {
                    Text = "→",
                    Margin = new Thickness(10, 0, 10, 0),
                    FontSize = 32,
                    Foreground = Ink,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(BuildIcon(target.Value));
            }
            return row;
        }

        // Un icono: la forma vectorial, o el texto serigrafiado si ese boton no tiene forma.
        // Las cuatro caras van CALADAS sobre un circulo relleno, como en el mando: dentro del
        // circulo el simbolo se dibuja con el color del panel, no con el de la tinta.
        private UIElement BuildIcon(PadButton b)
        {
            string? path = PadIcons.PathOf(b);
            if (path == null)
            {
                // "Keycap": L1/R2/L3 no son texto corrido, son la serigrafia de un boton.
                // Mono + Black + recuadro para que pesen lo mismo que las insignias redondas
                // de las caras; con la tipografia de la interfaz se leerian como una palabra
                // mas de la pantalla.
                var cap = new TextBlock
                {
                    Text = PadIcons.TextOf(b) ?? "-",
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    FontSize = 30,
                    FontWeight = FontWeights.Black,
                    Foreground = Ink,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                return new Border
                {
                    Child = cap,
                    BorderBrush = Ink,
                    BorderThickness = new Thickness(2.5),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 5, 12, 5),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            var shape = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(path),
                Stretch = Stretch.Uniform,
                Width = 34, Height = 34,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };

            if (!PadIcons.IsFilledBadge(b))
            {
                // Cruceta, Share/Options y el click del touchpad: trazo de tinta suelto sobre
                // el panel. Share y Options son lineas abiertas, asi que van a trazo y no a
                // relleno - rellenarlas las convertiria en manchas.
                shape.Stroke = Ink;
                shape.StrokeThickness = 2;
                if (b is PadButton.DpadUp or PadButton.DpadDown or PadButton.DpadLeft or PadButton.DpadRight)
                    shape.Fill = Ink;
                return shape;
            }

            // Cara: circulo relleno + simbolo calado encima, con el color del papel.
            shape.Stroke = Paper;
            shape.StrokeThickness = 2;
            if (b is PadButton.Square or PadButton.Triangle) shape.Fill = Paper;

            var grid = new Grid { Width = 48, Height = 48 };
            grid.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Ink });
            grid.Children.Add(shape);
            return grid;
        }

        // Reutiliza las etiquetas amigables de RemapTargets (Cruz, Circulo, Cuadrado...) en
        // vez de inventar un segundo juego de nombres para el diagrama.
        private static string FriendlyName(PadButton button)
        {
            foreach (var (label, value) in RemapTargets)
                if (value == button) return label;
            return button.ToString();
        }

        // Picker de destino (Step 5): un Popup unico reutilizado para las 16 pildoras -
        // ButtonPickerList se rellena la primera vez que se abre y luego solo se reancla.
        // _pickerSource recuerda que pildora lo abrio (el Popup no tiene un DataContext
        // propio del origen).
        private void ButtonPill_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button pill || pill.Tag is not PadButton source) return;
            _pickerSource = source;

            if (ButtonPickerList.Children.Count == 0)
            {
                foreach (var (label, value) in RemapTargets)
                {
                    var item = new Button
                    {
                        Content = label,
                        Tag = value,
                        Style = (Style)FindResource("InstrumentButton"),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 0, 0, 2),
                    };
                    item.Click += ButtonPickerItem_Click;
                    ButtonPickerList.Children.Add(item);
                }
            }

            ButtonPickerPopup.PlacementTarget = pill;
            ButtonPickerPopup.IsOpen = true;
        }

        private void ButtonPickerItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button item || item.Tag is not PadButton target) return;
            ButtonPickerPopup.IsOpen = false;

            var source = _pickerSource;
            if (target == PadButton.None || target == source) _remap.ButtonRemap.Remove(source);   // identidad: no se guarda
            else _remap.ButtonRemap[source] = target;

            RememberRemap();
            RefreshButtonPills();
        }

        private void ResetButtonRemap_Click(object sender, RoutedEventArgs e)
        {
            _remap.ButtonRemap.Clear();
            RememberRemap();
            RefreshButtonPills();
        }

        private void BuildTouchZoneCombos()
        {
            TouchZoneGrid.Children.Clear();
            _touchZoneRows.Clear();

            for (int i = 0; i < TouchZones.Length; i++)
            {
                var (label, zone) = TouchZones[i];
                var cell = new StackPanel { Margin = new Thickness(10) };
                cell.Children.Add(new TextBlock
                {
                    Text = label,
                    Style = (Style)FindResource("FieldLabel"),
                    Margin = new Thickness(0, 0, 0, 6)
                });

                var combo = new ComboBox { Width = 160, Tag = zone };
                foreach (var (targetLabel, targetValue) in RemapTargets)
                    combo.Items.Add(new ComboBoxItem { Content = targetLabel, Tag = targetValue });
                combo.SelectionChanged += TouchZoneCombo_Changed;
                cell.Children.Add(combo);

                Grid.SetRow(cell, i / 2);
                Grid.SetColumn(cell, i % 2);
                TouchZoneGrid.Children.Add(cell);
                _touchZoneRows.Add((zone, combo));
            }
        }

        // Preajustes de RESPUESTA en el orden que ve el usuario en cada ComboBox. "Lineal" es
        // la etiqueta amigable de ResponseCurve.Normal (el nombre del enum no cambia: lo usan
        // los perfiles guardados).
        private static readonly (string Label, ResponseCurve Curve)[] CurvePresets =
        {
            ("Lineal", ResponseCurve.Normal),
            ("Editor", ResponseCurve.Propia),
        };

        // El mini-icono de "Editor" no puede muestrear _remap.Left/RightCurvePoints (el combo
        // es compartido y AddCurveItem no sabe de que stick es): usa una forma de ejemplo fija
        // solo para que el icono no salga identico al de "Lineal" (que confundiria al usuario).
        private static readonly CurvePoint[] IconPropiaPoints =
            { new(0, 0), new(0.3, 0.55), new(0.7, 0.6), new(1, 1) };

        // Puntos del mini-icono de cada curva. Menos muestras que el CURVA grande (CurveSamples):
        // a 48x24 la diferencia no se nota y son 6 curvas x 2 sticks por reconstruir cada vez
        // que se abre la pestana.
        private const int CurveIconSamples = 12;

        private void BuildCurveLists()
        {
            LeftCurveList.Items.Clear();
            RightCurveList.Items.Clear();
            foreach (var (label, curve) in CurvePresets)
            {
                AddCurveItem(LeftCurveList, label, curve);
                AddCurveItem(RightCurveList, label, curve);
            }
        }

        // Un ComboBoxItem con el mini-icono de la curva (Canvas+Polyline muestreando Shape a
        // curvatura neutra 50 - el icono no cambia con el slider de Curvatura) + su nombre.
        // Tag = el ResponseCurve, que es lo que leen LeftCurve_Changed/RightCurve_Changed y
        // SelectComboByTag (ya usado por los combos de remapeo de botones/touchpad).
        private void AddCurveItem(ComboBox combo, string label, ResponseCurve curve)
        {
            const double w = 48, h = 24;
            var points = new PointCollection();
            for (int i = 0; i < CurveIconSamples; i++)
            {
                double t = i / (double)(CurveIconSamples - 1);
                double y = curve == ResponseCurve.Propia
                    ? InputTransform.ShapeCustom(t, IconPropiaPoints)
                    : InputTransform.Shape(t, curve, 50);
                points.Add(new Point(t * w, h - y * h));
            }

            var canvas = new Canvas { Width = w, Height = h };
            canvas.Children.Add(new Polyline
            {
                Points = points,
                Stroke = (Brush)FindResource("TextDataBrush"),
                StrokeThickness = 1.25,
            });

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(canvas);
            panel.Children.Add(new TextBlock
            {
                Text = label,
                Style = (Style)FindResource("FieldLabel"),
                Foreground = (Brush)FindResource("TextDataBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });

            combo.Items.Add(new ComboBoxItem { Content = panel, Tag = curve });
        }

        // Refleja _remap entero en los controles. Usado al construir la UI y tras CARGAR.
        // Bajo _updatingRemap para que ninguno de los ValueChanged/SelectionChanged que
        // dispara escriba de vuelta en _remap ni programe un guardado.
        private void ApplyRemapSettingsToControls()
        {
            LeftDeadzoneSlider.Value = _remap.LeftDeadzonePct;
            LeftReachSlider.Value = _remap.LeftReachPct;
            SelectComboByTag(LeftCurveList, _remap.LeftCurve);

            RightDeadzoneSlider.Value = _remap.RightDeadzonePct;
            RightReachSlider.Value = _remap.RightReachPct;
            SelectComboByTag(RightCurveList, _remap.RightCurve);

            L2PointSlider.Value = _remap.L2PointPct;
            R2PointSlider.Value = _remap.R2PointPct;

            RefreshButtonPills();   // no-op si el diagrama aun no se construyo (_pills vacio)

            foreach (var (zone, combo) in _touchZoneRows)
                SelectComboByTag(combo, _remap.TouchZoneMap.TryGetValue(zone, out var target) ? target : PadButton.None);

            // Slider.ValueChanged y ComboBox.SelectionChanged no se disparan cuando el valor
            // asignado es igual al que ya tenian (p.ej. cargar un perfil identico al actual),
            // asi que el texto y la curva se refrescan aqui explicitamente en vez de confiar
            // solo en los handlers de arriba.
            UpdateDeadzoneReachText();
            UpdateTriggerText();
            RedrawLeftCurve();
            RedrawRightCurve();
        }

        // Null-guarded like UpdateRainbowHint/UpdatePlayerSpeedText: a Slider
        // whose XAML Value differs from the RangeBase default (0) raises ValueChanged the
        // moment InitializeComponent assigns its Minimum/Maximum/Value, while later-declared
        // siblings in the same XAML tree (e.g. the "STICK DERECHO" fields, from a change on
        // the left stick's slider) do not exist yet. Without this guard that is a
        // NullReferenceException at startup, not just a theoretical race - LeftReachSlider's
        // Value="100" hit it on first launch.
        private void UpdateDeadzoneReachText()
        {
            if (LeftDeadzoneText == null || LeftReachText == null ||
                RightDeadzoneText == null || RightReachText == null) return;

            LeftDeadzoneText.Text = $"{LeftDeadzoneSlider.Value:0}%";
            LeftReachText.Text = $"{LeftReachSlider.Value:0}%";
            RightDeadzoneText.Text = $"{RightDeadzoneSlider.Value:0}%";
            RightReachText.Text = $"{RightReachSlider.Value:0}%";
        }



        private void UpdateTriggerText()
        {
            if (L2PointText == null || R2PointText == null ||
                L2PointBar == null || R2PointBar == null) return;

            L2PointText.Text = $"{L2PointSlider.Value:0}%";
            R2PointText.Text = $"{R2PointSlider.Value:0}%";
            L2PointBar.Width = 220 * (L2PointSlider.Value / 100.0);
            R2PointBar.Width = 220 * (R2PointSlider.Value / 100.0);
        }

        private void LeftDeadzone_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateDeadzoneReachText();
            if (_updatingRemap) return;
            _remap.LeftDeadzonePct = (int)Math.Round(LeftDeadzoneSlider.Value);
            RedrawLeftCurve();
            RememberRemap();
        }

        private void LeftReach_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateDeadzoneReachText();
            if (_updatingRemap) return;
            _remap.LeftReachPct = (int)Math.Round(LeftReachSlider.Value);
            RedrawLeftCurve();
            RememberRemap();
        }

        private void RightDeadzone_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateDeadzoneReachText();
            if (_updatingRemap) return;
            _remap.RightDeadzonePct = (int)Math.Round(RightDeadzoneSlider.Value);
            RedrawRightCurve();
            RememberRemap();
        }

        private void RightReach_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateDeadzoneReachText();
            if (_updatingRemap) return;
            _remap.RightReachPct = (int)Math.Round(RightReachSlider.Value);
            RedrawRightCurve();
            RememberRemap();
        }

        // ===== Arcos de los gatillos =====
        //
        // Un temporizador propio, vivo SOLO mientras la pagina esta a la vista. El lector del
        // DualSense se abre al entrar y se cierra al salir: un handle HID abierto que nadie
        // mira no solo gasta, es ademas lo que puede vetar un CM_Query_And_Remove_SubTree
        // cuando el usuario aplique una tasa (leccion L1).
        private DispatcherTimer? _triggerTimer;

        private void UpdateTriggerArcRunState()
        {
            bool visible = PageGatillos != null && PageGatillos.IsVisible;

            if (!visible)
            {
                _triggerTimer?.Stop();
                _triggerTimer = null;
                // Solo se cierra el lector si lo abrimos NOSOTROS. Con el motor del mando
                // virtual encendido el lector es suyo, y cerrarlo aqui congelaria el juego.
                if (!_engineRunning && !_engineBusy) _visualFeed.StopOwnReader();
                return;
            }

            if (!_engineRunning && !_engineBusy) _visualFeed.StartOwnReader();
            LoadTriggerArtwork();

            if (_triggerTimer != null) return;
            // 60 fps: es un indicador para calibrar con el dedo, y por debajo de eso se nota
            // que el arco va detras del gatillo.
            _triggerTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _triggerTimer.Tick += TriggerArcTick;
            _triggerTimer.Start();
        }

        private void TriggerArcTick(object? sender, EventArgs e)
        {
            // Con el motor encendido el mando fisico esta oculto y quien lo lee es el motor;
            // sin motor, el lector propio del visualizador. Se prueban los dos en ese orden.
            var snap = _engineRunning ? _padReader?.Snapshot() : _visualFeed.PhysicalSnapshot();
            if (snap == null) return;

            L2Arc.Value = snap.L2;
            R2Arc.Value = snap.R2;
        }

        // La lamina es opcional, igual que las skins del mando: si el PNG no esta, los arcos se
        // dibujan solos y no se avisa de nada. Un dibujo de fondo que falta no es un error.
        private void LoadTriggerArtwork()
        {
            if (TriggerArtwork == null || TriggerArtwork.Source != null) return;
            try
            {
                string ruta = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "UltraPolling", "skins", "ps5", "triggers.png");
                if (!System.IO.File.Exists(ruta)) return;

                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(ruta);
                // Sin esto el archivo se queda bloqueado y el usuario no puede reemplazarlo
                // sin cerrar la app - que es exactamente el problema que ya nos costo hoy.
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();

                TriggerArtwork.Source = bmp;
                TriggerArtwork.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lamina de gatillos: {ex.Message}");
            }
        }

        private void L2Point_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (L2Arc != null) L2Arc.Threshold = L2PointSlider.Value / 100.0;
            UpdateTriggerText();
            if (_updatingRemap) return;
            _remap.L2PointPct = (int)Math.Round(L2PointSlider.Value);
            RememberRemap();
        }

        private void R2Point_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (R2Arc != null) R2Arc.Threshold = R2PointSlider.Value / 100.0;
            UpdateTriggerText();
            if (_updatingRemap) return;
            _remap.R2PointPct = (int)Math.Round(R2PointSlider.Value);
            RememberRemap();
        }

        private void LeftCurve_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (LeftCurveList.SelectedItem is not ComboBoxItem { Tag: ResponseCurve curve }) return;
            if (_updatingRemap) return;
            _remap.LeftCurve = curve;
            RedrawLeftCurve();
            RememberRemap();
        }

        private void RightCurve_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (RightCurveList.SelectedItem is not ComboBoxItem { Tag: ResponseCurve curve }) return;
            if (_updatingRemap) return;
            _remap.RightCurve = curve;
            RedrawRightCurve();
            RememberRemap();
        }



        private void ToggleLeftAdvanced(object sender, RoutedEventArgs e)
        {
            LeftAdvancedPanel.Visibility = LeftAdvancedPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ToggleRightAdvanced(object sender, RoutedEventArgs e)
        {
            RightAdvancedPanel.Visibility = RightAdvancedPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ToggleHelpPanel_Click(object sender, RoutedEventArgs e)
        {
            HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void TouchZoneCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingRemap) return;
            if (sender is not ComboBox { Tag: TouchZone zone, SelectedItem: ComboBoxItem item }) return;

            var target = (PadButton)item.Tag;
            if (target == PadButton.None) _remap.TouchZoneMap.Remove(zone);   // sin asignar
            else _remap.TouchZoneMap[zone] = target;

            RememberRemap();
        }

        // Dibuja la curva de respuesta muestreando InputTransform.ApplyStick sobre un stick
        // puramente horizontal (Y=0): la salida en X para cada entrada t en 0..1 es
        // exactamente lo que el usuario siente al empujar el stick en una direccion. Sin
        // hardware ni mock: es la misma funcion pura que usara el motor. Pasa curve+curvaturePct
        // directo al overload de ApplyStick que usa Shape, asi que cubre las 6 curvas (incluidas
        // la S de Dinamica y el escalon de Digital), no solo las de exponente fijo.
        private const int CurveSamples = 41;

        private static void DrawCurve(System.Windows.Shapes.Polyline line, double innerDeadzone,
            double outerDeadzone, ResponseCurve curve, int curvaturePct, double width, double height,
            IReadOnlyList<CurvePoint>? points)
        {
            var samples = new PointCollection();
            for (int i = 0; i < CurveSamples; i++)
            {
                double t = i / (double)(CurveSamples - 1);
                var (x, _) = InputTransform.ApplyStick(new StickInput(t, 0), innerDeadzone, outerDeadzone, curve, curvaturePct, points);
                samples.Add(new Point(t * width, height - (x * height)));
            }
            line.Points = samples;
        }

        // Same XAML-parse-time hazard as UpdateDeadzoneReachText: LeftCurveCanvas is declared
        // after LeftDeadzoneSlider in the tree, so a ValueChanged raised while parsing the
        // deadzone slider would otherwise hit it before it exists.
        private void RedrawLeftCurve()
        {
            if (LeftCurveLine == null || LeftCurveCanvas == null) return;
            DrawCurve(LeftCurveLine, _remap.LeftInnerDeadzone, _remap.LeftOuterDeadzone,
                _remap.LeftCurve, _remap.LeftCurvaturePct, LeftCurveCanvas.Width, LeftCurveCanvas.Height,
                _remap.LeftCurvePoints);
            RefreshCurveDots(LeftCurveCanvas, _leftCurveDots, _remap.LeftCurvePoints, _remap.LeftCurve,
                _remap.LeftInnerDeadzone, _remap.LeftOuterDeadzone);
        }

        private void RedrawRightCurve()
        {
            if (RightCurveLine == null || RightCurveCanvas == null) return;
            DrawCurve(RightCurveLine, _remap.RightInnerDeadzone, _remap.RightOuterDeadzone,
                _remap.RightCurve, _remap.RightCurvaturePct, RightCurveCanvas.Width, RightCurveCanvas.Height,
                _remap.RightCurvePoints);
            RefreshCurveDots(RightCurveCanvas, _rightCurveDots, _remap.RightCurvePoints, _remap.RightCurve,
                _remap.RightInnerDeadzone, _remap.RightOuterDeadzone);
        }

        // ===== Editor de curva (ResponseCurve.Propia): 3 puntos interiores arrastrables =====
        // Los extremos (0,0)/(1,1) son fijos: la zona muerta y el alcance ya los gobiernan los
        // sliders. Solo se arrastran los indices 1..3 de la lista de 5.

        // El eje X del canvas es la entrada CRUDA del stick (0..1), pero CurvePoint.X vive en el
        // dominio post-deadzone (0..1 entre inner y outer). Estos dos convierten entre ambos para
        // que los marcadores caigan exactamente sobre la polilinea dibujada por DrawCurve y el
        // arrastre aterrice donde el usuario apunta, con cualquier zona muerta/alcance.
        private static double DomainToRaw(double x, double inner, double outer)
            => inner + x * (Math.Max(outer, inner + 1e-6) - inner);
        private static double RawToDomain(double x, double inner, double outer)
        {
            double o = Math.Max(outer, inner + 1e-6);
            return Math.Clamp((x - inner) / (o - inner), 0.0, 1.0);
        }

        // Colores fijos de los 3 puntos del editor - la UNICA excepcion de color del tema
        // monocromo, pedida explicitamente: cada punto tiene identidad propia y la ayuda
        // ("¿COMO FUNCIONA?") los nombra por color. El indice es el contrato:
        //   0 = VERDE  zona baja  (movimientos finos, punteria)
        //   1 = AMBAR  zona media (transicion apuntar<->girar)
        //   2 = ROJO   zona alta  (giros rapidos, tope)
        private static readonly Color[] CurveDotColors =
        {
            Color.FromRgb(0x66, 0xBB, 0x6A),
            Color.FromRgb(0xFF, 0xCA, 0x28),
            Color.FromRgb(0xEF, 0x53, 0x50),
        };

        private readonly List<System.Windows.Shapes.Ellipse> _leftCurveDots = new();
        private readonly List<System.Windows.Shapes.Ellipse> _rightCurveDots = new();
        private int _dragIndex = -1;
        private bool _dragIsLeft;

        private void EnsureCurveDots(Canvas canvas, List<System.Windows.Shapes.Ellipse> dots)
        {
            if (dots.Count > 0) return;
            for (int i = 0; i < 3; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 9, Height = 9,
                    Fill = new SolidColorBrush(CurveDotColors[i]),
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    Visibility = Visibility.Collapsed,
                };
                dots.Add(dot);
                canvas.Children.Add(dot);
            }
        }

        // Coloca los 3 marcadores segun los puntos 1..3 y los muestra solo si la curva es Propia.
        // p.X vive en el dominio post-deadzone; DomainToRaw lo lleva al eje crudo del canvas
        // (el mismo que usa DrawCurve), asi el marcador cae exactamente sobre la polilinea.
        private void RefreshCurveDots(Canvas canvas, List<System.Windows.Shapes.Ellipse> dots,
                                      List<CurvePoint> pts, ResponseCurve curve, double inner, double outer)
        {
            EnsureCurveDots(canvas, dots);
            bool show = curve == ResponseCurve.Propia;
            for (int i = 0; i < 3; i++)
            {
                var p = pts[i + 1];
                Canvas.SetLeft(dots[i], DomainToRaw(p.X, inner, outer) * canvas.Width - dots[i].Width / 2);
                Canvas.SetTop(dots[i], (1 - p.Y) * canvas.Height - dots[i].Height / 2);
                dots[i].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void CurveCanvas_Down(Canvas canvas, List<CurvePoint> pts, ResponseCurve curve,
                                      bool isLeft, double inner, double outer, MouseButtonEventArgs e)
        {
            if (curve != ResponseCurve.Propia) return;
            var pos = e.GetPosition(canvas);

            // Prueba en espacio de PIXELES (no en el 0..1 normalizado): un radio fijo en pixeles
            // da un area de captura circular real sobre el canvas 220x100 (una normalizada seria
            // muy anisotropica, ancha en X y angosta en Y). Las posiciones de los puntos se
            // convierten al eje crudo del canvas con DomainToRaw, igual que RefreshCurveDots.
            int best = -1; double bestDist = 14.0;
            for (int i = 1; i <= 3; i++)
            {
                double px = DomainToRaw(pts[i].X, inner, outer) * canvas.Width;
                double py = (1 - pts[i].Y) * canvas.Height;
                double d = Math.Sqrt(Math.Pow(px - pos.X, 2) + Math.Pow(py - pos.Y, 2));
                if (d < bestDist) { bestDist = d; best = i; }
            }
            if (best < 0) return;
            _dragIndex = best;
            _dragIsLeft = isLeft;
            canvas.CaptureMouse();
            e.Handled = true;
        }

        private void CurveCanvas_Move(Canvas canvas, List<CurvePoint> pts, double inner, double outer, MouseEventArgs e)
        {
            if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(canvas);
            // X acotada entre los vecinos (con margen, en el dominio post-deadzone) para que la
            // curva siga siendo una funcion; Y libre en 0..1. La posicion cruda del mouse se
            // convierte al dominio con RawToDomain antes de acotar/guardar.
            double minX = pts[_dragIndex - 1].X + 0.03, maxX = pts[_dragIndex + 1].X - 0.03;
            double x = Math.Clamp(RawToDomain(pos.X / canvas.Width, inner, outer), minX, maxX);
            double y = Math.Clamp(1 - pos.Y / canvas.Height, 0.0, 1.0);
            pts[_dragIndex] = new CurvePoint(x, y);
            if (_dragIsLeft) RedrawLeftCurve(); else RedrawRightCurve();
        }

        private void CurveCanvas_Up(Canvas canvas)
        {
            if (_dragIndex < 0) return;
            _dragIndex = -1;
            canvas.ReleaseMouseCapture();
            RememberRemap();   // persiste el dibujo (debounced, como todo _remap)
        }

        // Wrappers por stick (los que referencia el XAML). Pasan inner/outer leidos de _remap en
        // cada llamada (no cacheados): si el usuario mueve el slider de zona muerta/alcance a
        // mitad de un arrastre, la conversion sigue consistente en el siguiente evento.
        private void LeftCurveCanvas_MouseDown(object sender, MouseButtonEventArgs e)
            => CurveCanvas_Down(LeftCurveCanvas, _remap.LeftCurvePoints, _remap.LeftCurve, true,
                _remap.LeftInnerDeadzone, _remap.LeftOuterDeadzone, e);
        private void LeftCurveCanvas_MouseMove(object sender, MouseEventArgs e)
            => CurveCanvas_Move(LeftCurveCanvas, _remap.LeftCurvePoints, _remap.LeftInnerDeadzone, _remap.LeftOuterDeadzone, e);
        private void LeftCurveCanvas_MouseUp(object sender, MouseButtonEventArgs e)
            => CurveCanvas_Up(LeftCurveCanvas);
        private void RightCurveCanvas_MouseDown(object sender, MouseButtonEventArgs e)
            => CurveCanvas_Down(RightCurveCanvas, _remap.RightCurvePoints, _remap.RightCurve, false,
                _remap.RightInnerDeadzone, _remap.RightOuterDeadzone, e);
        private void RightCurveCanvas_MouseMove(object sender, MouseEventArgs e)
            => CurveCanvas_Move(RightCurveCanvas, _remap.RightCurvePoints, _remap.RightInnerDeadzone, _remap.RightOuterDeadzone, e);
        private void RightCurveCanvas_MouseUp(object sender, MouseButtonEventArgs e)
            => CurveCanvas_Up(RightCurveCanvas);

        // Copia profunda: RemapProfile.Settings no debe compartir instancia con _remap, o
        // seguir editando despues de GUARDAR reescribiria en silencio el perfil ya guardado
        // (y CARGAR luego mutaria el propio perfil guardado al editar).
        private static RemapSettings CloneRemapSettings(RemapSettings s) => new RemapSettings
        {
            LeftDeadzonePct = s.LeftDeadzonePct,
            LeftReachPct = s.LeftReachPct,
            LeftCurve = s.LeftCurve,
            LeftCurvaturePct = s.LeftCurvaturePct,
            RightDeadzonePct = s.RightDeadzonePct,
            RightReachPct = s.RightReachPct,
            RightCurve = s.RightCurve,
            RightCurvaturePct = s.RightCurvaturePct,
            L2PointPct = s.L2PointPct,
            R2PointPct = s.R2PointPct,
            ButtonRemap = new Dictionary<PadButton, PadButton>(s.ButtonRemap),
            TouchZoneMap = new Dictionary<TouchZone, PadButton>(s.TouchZoneMap),
            LeftCurvePoints = new List<CurvePoint>(s.LeftCurvePoints),
            RightCurvePoints = new List<CurvePoint>(s.RightCurvePoints),
        };

        // Guarda el estado activo bajo el nombre reservado, agrupando rafagas de arrastre
        // (igual que RememberLight/_intentSave para la luz) en una sola escritura a disco.
        private DispatcherTimer? _remapSave;

        private void RememberRemap()
        {
            if (_updatingRemap) return;

            _remapSave ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _remapSave.Stop();
            _remapSave.Tick -= RemapSave_Tick;
            _remapSave.Tick += RemapSave_Tick;
            _remapSave.Start();
        }

        private void RemapSave_Tick(object? sender, EventArgs e)
        {
            _remapSave!.Stop();
            PersistLastUsedRemap();
        }

        // Guardado silencioso: si falla (disco lleno, permisos) no interrumpe con un
        // MessageBox cada 750 ms - igual que el autoguardado de LightIntent.
        private void PersistLastUsedRemap()
        {
            _remapProfiles.RemoveAll(x => x.Name == LastUsedProfileName);
            _remapProfiles.Add(new RemapProfile { Name = LastUsedProfileName, Settings = CloneRemapSettings(_remap) });
            RemapProfileStore.Save(_remapProfiles);
        }

        // Aqui vivian GUARDAR/CARGAR/BORRAR del remapeo. Los perfiles con nombre son ahora
        // una seccion propia que guarda luz y mando juntos (ver la region PERFILES abajo);
        // de RemapProfileStore solo queda el pseudo-perfil "__ultimo_usado__" de arriba, que
        // es el estado en vivo del configurador y no un perfil que el usuario elija.

        // Collapses a slider drag into one write. Without it, dragging fires a HID write per
        // pixel of travel - hundreds a second at the device.
        private DispatcherTimer? _lightDebounce;

        // Set while code (not the user) moves a control, so programmatic updates do not write.
        private bool _updatingLight;

        // Drives the rainbow effect. Speed is in colours/second: at or below 64/s the timer
        // fires once per colour; above 64/s it fires at the timer floor and each tick advances
        // a fractional number of colours (still smooth, since ramp colours differ by <=1).
        private DispatcherTimer? _rainbowTimer;
        private RainbowWalker? _rainbowWalker;

        private PlayerLedWalker? _playerWalker;
        private double _playerFrameAccumMs;   // acumula ms para avanzar el frame del efecto de LED
        private int _playerFrameIndex;

        private bool RainbowOn => RainbowCheck.IsChecked == true;

        private PlayerLedEffect CurrentPlayerEffect =>
            PlayerEffectList?.SelectedItem is ComboBoxItem it ? (PlayerLedEffect)it.Tag : PlayerLedEffect.None;

        private bool PlayerEffectOn => CurrentPlayerEffect != PlayerLedEffect.None;

        // Velocidad del efecto de LED en frames/segundo (la barra VELOCIDAD del apartado del mando).
        private double PlayerEffectFps => PlayerSpeed?.Value ?? 6;

        private void UpdatePlayerSpeedText()
        {
            if (PlayerSpeedText == null || PlayerSpeed == null) return;
            PlayerSpeedText.Text = $"{PlayerSpeed.Value:0}/s";
        }

        // Guarda la intencion de luz en disco, agrupando rafagas (arrastrar el picker, girar
        // el rainbow) en una sola escritura. NUNCA se llama por-tick del rainbow.
        private DispatcherTimer? _intentSave;

        private List<string> _palette = new();

        // Populated once. Tag carries the value so handlers read a real value rather than
        // parsing a label back into meaning.
        private void BuildLightControls()
        {
            if (PlayerLedRow.Children.Count > 0) return;

            // El guard cubre el bloque ENTERO, no solo alrededor de las asignaciones de abajo:
            // marcar un segmento dispara PlayerLed_Checked/Brightness_Checked, que llaman a
            // ApplyLightNow() en cuanto _updatingLight es false. Sin el guard, reflejar la
            // intencion guardada escribiria al mando en vez de limitarse a restaurar los
            // controles.
            try
            {
                _updatingLight = true;

                // Seis segmentos, no cinco: PlayerLeds trae Off, cuatro jugadores y All. El ultimo
                // lleva el icono de las cinco barras y no un "5", porque un jugador 5 no existe en
                // el mando.
                foreach (var (contenido, valor, nombre) in new (object, PlayerLeds, string)[]
                         {
                             (Icono("ProfileIconPath"), PlayerLeds.Off,    "Ninguna"),
                             ("1", PlayerLeds.Player1, "Jugador 1"),
                             ("2", PlayerLeds.Player2, "Jugador 2"),
                             ("3", PlayerLeds.Player3, "Jugador 3"),
                             ("4", PlayerLeds.Player4, "Jugador 4"),
                             (Icono("AllLedsIconPath"), PlayerLeds.All, "Todas encendidas"),
                         })
                {
                    var seg = new RadioButton
                    {
                        Style = (Style)FindResource("MiniSegment"),
                        GroupName = "PlayerLed",
                        Content = contenido,
                        Tag = valor,
                        IsChecked = valor == PlayerLeds.Player1,   // Player 1, lo que Windows muestra
                    };
                    System.Windows.Automation.AutomationProperties.SetName(seg, nombre);
                    seg.ToolTip = nombre;
                    seg.Checked += PlayerLed_Checked;
                    PlayerLedRow.Children.Add(seg);
                }

                // Tres niveles, no cuatro: LedBrightness es High/Medium/Low. El sol crece con el nivel.
                foreach (var (tamano, valor, nombre) in new (double, LedBrightness, string)[]
                         {
                             (11, LedBrightness.Low,    "Brillo bajo"),
                             (14, LedBrightness.Medium, "Brillo medio"),
                             (17, LedBrightness.High,   "Brillo alto"),
                         })
                {
                    var seg = new RadioButton
                    {
                        Style = (Style)FindResource("MiniSegment"),
                        GroupName = "LedBrightness",
                        Content = Icono("SunIconPath", tamano),
                        Tag = valor,
                        IsChecked = valor == LedBrightness.High,   // Alto, la seleccion de antes
                    };
                    System.Windows.Automation.AutomationProperties.SetName(seg, nombre);
                    seg.ToolTip = nombre;
                    seg.Checked += Brightness_Checked;
                    BrightnessRow.Children.Add(seg);
                }

                foreach (var (label, value) in new (string, RainbowStyle)[]
                         {
                             ("Suave", RainbowStyle.Smooth),
                             ("Equilibrado", RainbowStyle.Balanced),
                             ("Vivo", RainbowStyle.Vivid),
                         })
                    RainbowStyleList.Items.Add(new ComboBoxItem { Content = label, Tag = value });

                // Smooth by default: the reported complaint is that the cycle jumps, not that it is
                // dull. Vivid is the old behaviour, kept for anyone who wants saturation over
                // smoothness.
                RainbowStyleList.SelectedIndex = 0;
                UpdateRainbowHint();

                foreach (var (label, value) in new (string, PlayerLedEffect)[]
                         {
                             ("Ninguno", PlayerLedEffect.None),
                             ("Carga", PlayerLedEffect.Charge),
                             ("Estrellas", PlayerLedEffect.Twinkle),
                             ("Respiracion", PlayerLedEffect.Breathe),
                         })
                    PlayerEffectList.Items.Add(new ComboBoxItem { Content = label, Tag = value });
                PlayerEffectList.SelectedIndex = 0;

                // Reflejar en la UI lo que se restauro al mando (la intencion guardada), para que
                // no aparezca Player 1/azul cuando el mando ya tiene otro estado. Bajo _updatingLight
                // para no disparar escrituras; el rainbow se arranca despues, fuera del guard.
                var saved = IntentStore.Load();
                if (saved != null)
                {
                    Picker.SelectedColor = Color.FromRgb(saved.R, saved.G, saved.B);
                    SelectSegmentByTag(PlayerLedRow, saved.Player);
                    SelectSegmentByTag(BrightnessRow, saved.Brightness);
                    SelectComboByTag(RainbowStyleList, saved.Style);
                    RainbowSpeed.Value = Math.Clamp(saved.RainbowColoursPerSecond,
                        (int)RainbowWalker.MinColoursPerSecond, (int)RainbowWalker.MaxColoursPerSecond);
                    SelectComboByTag(PlayerEffectList, saved.PlayerEffect);
                    PlayerSpeed.Value = Math.Clamp(saved.PlayerEffectFps, 2, 20);
                }
                // Con o sin intencion guardada (p.ej. primer arranque), la barra de velocidad y la
                // seleccion fija de Player dependen solo de si hay un efecto de LED activo.
                PlayerSpeed.IsEnabled = PlayerEffectOn;
                PlayerLedRow.IsEnabled = !PlayerEffectOn;

                // Presets cover the common case in one click. "Apagado" belongs here: turning the
                // light off is a preference, not an error state.
                foreach (var (name, r, g, b) in new (string, byte, byte, byte)[]
                         {
                             ("Azul", 0, 0, 255),
                             ("Rojo", 255, 0, 0),
                             ("Verde", 0, 255, 0),
                             ("Cian", 0, 255, 255),
                             ("Magenta", 255, 0, 255),
                             ("Naranja", 255, 100, 0),
                             ("Blanco", 255, 255, 255),
                             ("Apagado", 0, 0, 0),
                         })
                {
                    // A Button, styled as just the coloured rectangle (PresetSwatchButton in
                    // Theme.xaml), so Tab reaches it and Enter/Space activates it - the old
                    // Border+MouseLeftButtonUp had no tab stop and no keyboard activation.
                    var swatch = new Button
                    {
                        Style = (Style)FindResource("PresetSwatchButton"),
                        Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                        ToolTip = name,
                        Tag = new byte[] { r, g, b }
                    };
                    swatch.Click += Preset_Click;
                    PresetRow.Children.Add(swatch);
                }

                // La paleta del usuario, debajo de los presets de fabrica.
                _palette = PaletteStore.Load();
                RefreshPalette();

                UpdateRainbowSpeedText();
            }
            finally
            {
                _updatingLight = false;
            }

            // Si lo guardado era un rainbow, reanudar la animacion ahora que los controles existen.
            // Marcar el check dispara Rainbow_Toggled, que arranca el timer con el estilo/velocidad
            // que acabamos de fijar arriba.
            var savedIntent = IntentStore.Load();
            if (savedIntent?.Kind == LightIntentKind.Rainbow && RainbowCheck.IsChecked != true)
            {
                RainbowCheck.IsChecked = true;
            }

            if (savedIntent != null && savedIntent.PlayerEffect != PlayerLedEffect.None)
            {
                _playerWalker = new PlayerLedWalker(savedIntent.PlayerEffect);
                _playerFrameIndex = 0; _playerFrameAccumMs = 0;
                UpdateEffectDriver();
            }

            UpdatePlayerSpeedText();
        }

        // El Fill se ata al Foreground del segmento: un Path no lo hereda, y sin esto el icono del
        // segmento activo se quedaria gris sobre la pastilla clara (la leccion L7, otra vez).
        private System.Windows.Shapes.Path Icono(string clave, double tamano = 15)
        {
            var p = new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource(clave),
                Stretch = Stretch.Uniform,
                Width = tamano,
                Height = tamano,
            };
            p.SetBinding(System.Windows.Shapes.Shape.FillProperty,
                new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) });
            return p;
        }

        private void PlayerLed_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingLight) return;
            ApplyLightNow();
        }

        private void Brightness_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingLight) return;
            ApplyLightNow();
        }

        // null si aun no se ha construido la fila (el arranque llama a esto antes de tiempo por
        // varios caminos); los llamadores ya saben salirse cuando no hay valor.
        private PlayerLeds? CurrentPlayerLed()
            => PlayerLedRow.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as PlayerLeds?;

        private LedBrightness? CurrentBrightness()
            => BrightnessRow.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as LedBrightness?;

        // Selecciona en un ComboBox el item cuyo Tag es igual a value (los items se construyen
        // con Tag = el enum). Sin match, deja la seleccion actual.
        private static void SelectComboByTag(ComboBox combo, object value)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (Equals(item.Tag, value)) { combo.SelectedItem = item; return; }
            }
        }

        // Equivalente de SelectComboByTag para las filas de segmentos (PlayerLedRow,
        // BrightnessRow): marca el RadioButton cuyo Tag es igual a value. Sin match, deja la
        // seleccion actual. Los llamadores lo envuelven en _updatingLight para que restaurar la
        // intencion guardada no escriba al mando.
        private static void SelectSegmentByTag(System.Windows.Controls.Panel row, object value)
        {
            foreach (var seg in row.Children.OfType<RadioButton>())
            {
                if (Equals(seg.Tag, value)) { seg.IsChecked = true; return; }
            }
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: byte[] rgb } || rgb.Length != 3) return;

            try
            {
                _updatingLight = true;

                // A preset click is touching a colour too: the last thing you touched
                // wins, same rule as Picker_ColorChanged. Left unticked, Effect_Tick
                // would overwrite this within one tick (15.6-187.5 ms) and the click
                // would do nothing.
                // RainbowCheck is not guarded by _updatingLight (its own handler does not
                // check it), so this still reaches Rainbow_Toggled and actually stops
                // the timer via the Unchecked event it is wired to.
                if (RainbowCheck.IsChecked == true) RainbowCheck.IsChecked = false;

                Picker.SelectedColor = Color.FromRgb(rgb[0], rgb[1], rgb[2]);
            }
            finally
            {
                _updatingLight = false;
            }

            ApplyLightNow();   // A preset click is a decision, not a drag - no need to debounce.
        }

        // La paleta del usuario: sus colores guardados, mas el "+" que anade el actual. Se
        // reconstruye entera en cada cambio - son 13 elementos como mucho y asi no hay dos caminos
        // para dejarla desincronizada del archivo.
        private void RefreshPalette()
        {
            PaletteRow.Children.Clear();

            foreach (string hex in _palette)
            {
                if (!ColourMath.TryParseHex(hex, out byte r, out byte g, out byte b)) continue;

                var swatch = new Button
                {
                    Style = (Style)FindResource("PresetSwatchButton"),
                    Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    Tag = new byte[] { r, g, b },
                    ToolTip = $"#{hex}  (clic derecho para quitarlo)",
                };
                swatch.Click += Preset_Click;                 // el mismo handler que los de fabrica
                swatch.MouseRightButtonUp += PaletteSwatch_Remove;
                PaletteRow.Children.Add(swatch);
            }

            if (_palette.Count < PaletteStore.MaxColours)
            {
                var add = new Button
                {
                    Style = (Style)FindResource("PresetSwatchButton"),
                    Background = (Brush)FindResource("SurfaceAltBrush"),
                    Content = "+",
                    Foreground = (Brush)FindResource("TextDataBrush"),
                    ToolTip = "Guardar el color actual en tu paleta",
                };
                add.Click += PaletteAdd_Click;
                PaletteRow.Children.Add(add);
            }
        }

        // Guardar y solo entonces adoptar. Las dos operaciones de la paleta mutaban la lista viva
        // antes de escribir a disco, asi que un fallo al guardar dejaba la pantalla diciendo una
        // cosa y el archivo otra: el color quitado reaparecia al reabrir, y el anadido fantasma se
        // colaba en el siguiente guardado con exito. Se trabaja sobre una copia y se adopta solo
        // si el disco la acepto.
        private bool TrySavePalette(List<string> propuesta)
        {
            var saved = PaletteStore.Save(propuesta);
            if (!saved.Success)
            {
                LogStatus(saved.Error!);
                return false;
            }
            _palette = propuesta;
            RefreshPalette();
            return true;
        }

        private void PaletteAdd_Click(object sender, RoutedEventArgs e)
        {
            var c = Picker.SelectedColor;
            var propuesta = new List<string>(_palette);
            if (!PaletteStore.Add(propuesta, ColourMath.ToHex(c.R, c.G, c.B)))
            {
                LogStatus("Ese color ya esta en tu paleta.");
                return;
            }
            TrySavePalette(propuesta);
        }

        // Clic derecho para quitar: un aspa sobre cada muestra ensuciaria una fila cuyo contenido
        // ES el color, y quitar uno no destruye nada que no se pueda volver a guardar con el "+".
        private void PaletteSwatch_Remove(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Button b || b.Tag is not byte[] rgb) return;

            string hex = ColourMath.ToHex(rgb[0], rgb[1], rgb[2]);
            var propuesta = new List<string>(_palette);
            if (propuesta.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase)) == 0) return;

            TrySavePalette(propuesta);
        }

        private LightState CurrentLight()
        {
            var c = Picker.SelectedColor;
            return new LightState(c.R, c.G, c.B,
                CurrentPlayerLed() ?? PlayerLeds.Off,
                CurrentBrightness() ?? LedBrightness.Low);
        }

        private void LightDebounce_Tick(object? sender, EventArgs e)
        {
            _lightDebounce?.Stop();
            ApplyLightNow();
        }

        private void ApplyLightNow()
        {
            // Any direct apply cancels a pending debounced one. Without this, dragging a
            // slider (which starts the 50 ms timer) and then clicking a preset or combo
            // within that window applies immediately (correct) and then again ~50 ms later
            // when the stale timer fires - a redundant duplicate write.
            _lightDebounce?.Stop();

            if (_lightPadId == null) return;
            if (CurrentPlayerLed() == null || CurrentBrightness() == null) return;

            var result = DualSenseLight.Apply(_lightPadId, CurrentLight());
            if (!result.Success) LogStatus($"No se pudo cambiar la luz: {result.Error}");
            RememberLight();
        }

        // Lee de los controles la intencion de luz que hay puesta ahora mismo (color fijo o
        // rainbow, mas el efecto de LED). null si los controles aun no existen. La usan el
        // autoguardado de abajo y GUARDAR de la seccion PERFILES: la misma foto de la luz.
        private LightIntent? BuildCurrentIntent()
        {
            if (CurrentPlayerLed() is not { } player || CurrentBrightness() is not { } brightness) return null;

            LightIntent intent;
            if (RainbowOn)
            {
                if (RainbowStyleList.SelectedItem == null) return null;
                var style = (RainbowStyle)((ComboBoxItem)RainbowStyleList.SelectedItem).Tag;
                var lit = CurrentLight();
                intent = LightIntent.FromRainbow(style, (int)Math.Round(TargetColoursPerSecond), player, brightness);
                intent.R = lit.R; intent.G = lit.G; intent.B = lit.B;
            }
            else
            {
                intent = LightIntent.FromStatic(CurrentLight());
            }

            intent.PlayerEffect = CurrentPlayerEffect;
            intent.PlayerEffectFps = (int)Math.Round(PlayerEffectFps);
            return intent;
        }

        // Construye la intencion actual (color fijo o rainbow) y agenda su guardado.
        private void RememberLight()
        {
            if (_updatingLight) return;                 // no persistir cambios programaticos

            var intent = BuildCurrentIntent();
            if (intent == null) return;

            if (_intentSave == null)
            {
                _intentSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
                _intentSave.Tick += (s, e) => { _intentSave!.Stop(); IntentStore.Save(_lastIntent!); };
            }
            _lastIntent = intent;
            _intentSave.Stop();
            _intentSave.Start();
        }

        private LightIntent? _lastIntent;

        private void RestoreLight_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _updatingLight = true;

                // Restoring is touching a colour too - same "last thing you touched wins"
                // rule as Picker_ColorChanged and Preset_Click. Without this, a running
                // rainbow overwrites the restored blue within one tick (15.6-187.5 ms).
                if (RainbowCheck.IsChecked == true) RainbowCheck.IsChecked = false;

                Picker.SelectedColor = Color.FromRgb(0, 0, 255);
                SelectSegmentByTag(PlayerLedRow, PlayerLeds.Player1);
                SelectSegmentByTag(BrightnessRow, LedBrightness.High);
            }
            finally
            {
                _updatingLight = false;
            }

            // Outside the _updatingLight guard so PlayerEffect_Changed actually runs: it stops
            // the effect timer (via UpdateEffectDriver), re-enables PlayerLedRow, and - now
            // that nothing is animating - re-applies the restored static colour/player itself.
            PlayerEffectList.SelectedIndex = 0;   // Ninguno

            ApplyLightNow();
            LogStatus("Luz restaurada: azul, Player 1.");
            RememberLight();
        }

        // Only PlayStation controllers reach this page. The rest of the app is vendor-neutral;
        // this report layout is Sony's alone.
        private void RefreshPlayStationDevices()
        {
            BuildLightControls();

            ResolveLightPad();
        }

        // Resolver el mando de las luces es barato y hay que rehacerlo cada vez que cambia el
        // hardware, no solo al entrar a la pagina: con el campo fijado al entrar, desenchufar el
        // mando o cambiarlo de puerto dejaba un identificador muerto y las luces escribian al vacio
        // sin decir nada.
        //
        // El orden NO es intercambiable: primero el escaneo (nombre bonito, coincide con la lista de
        // Dispositivos) y si no, el resolutor en-proceso, que es el unico que encuentra el mando
        // cuando HidHide lo oculta con el mando virtual encendido.
        //
        // Resolver el mando y reflejarlo en pantalla son la MISMA operacion: separarlas dejaba la
        // pagina mintiendo al enchufar o desenchufar - controles muertos sin decir nada, o la luz
        // encendida mientras la pantalla seguia diciendo que no hay mando.
        //
        // El escaneo llama aqui aunque la pagina de luces no se haya construido todavia, asi que la
        // parte de UI se salta si sus controles aun no existen; la proxima entrada a la pagina la
        // pone al dia.
        private void ResolveLightPad()
        {
            _lightPadId = _allDevices.FirstOrDefault(DualSenseLight.IsPlayStation)?.InstanceId
                          ?? HidHideControl.FindPhysicalGamepadInstanceId();

            if (LightEmptyState == null || LightPanel == null) return;

            bool hayMando = _lightPadId != null;
            LightEmptyState.Visibility = hayMando ? Visibility.Collapsed : Visibility.Visible;
            LightPanel.Visibility = hayMando ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Picker_ColorChanged(object? sender, EventArgs e)
        {
            if (_updatingLight) return;

            // Touching a colour ends the effect: while the rainbow owns the colour, a picked one
            // would be overwritten within one tick (15.6-187.5 ms). The last thing you touched wins.
            if (RainbowCheck.IsChecked == true) RainbowCheck.IsChecked = false;

            _lightDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _lightDebounce.Tick -= LightDebounce_Tick;
            _lightDebounce.Tick += LightDebounce_Tick;
            _lightDebounce.Stop();
            _lightDebounce.Start();
        }

        private RainbowStyle CurrentRainbowStyle =>
            RainbowStyleList?.SelectedItem is ComboBoxItem { Tag: RainbowStyle s } ? s : RainbowStyle.Smooth;

        // The trade-off stated where the choice is made. Every one of these numbers is measured,
        // not estimated - see docs/superpowers/plans/2026-07-16-perceptual-rainbow.md.
        // Va al ToolTip del propio desplegable y ya no a un parrafo bajo la fila: es un texto
        // que se lee una vez, al elegir, y despues solo ocupaba sitio permanentemente.
        private void UpdateRainbowHint()
        {
            if (RainbowStyleList == null) return;
            RainbowStyleList.ToolTip = CurrentRainbowStyle switch
            {
                RainbowStyle.Smooth => "Suave: transicion perfectamente pareja, brillo constante. Los colores salen menos saturados - un azul vivo es oscuro, y no se puede tener las dos cosas.",
                RainbowStyle.Balanced => "Equilibrado: cada tono coge todo el color que puede sin variar el brillo. Mas vivo que Suave, casi tan parejo.",
                _ => "Vivo: maxima saturacion. El brillo cambia fuerte entre tonos - el azul se ve 13 veces mas oscuro que el amarillo."
            };
        }

        private void RainbowStyle_Changed(object sender, SelectionChangedEventArgs e)
        {
            UpdateRainbowHint();

            // Each style has its own ramp, so the walker is dropped and the tick rebuilds it.
            // No write here: the tick picks it up on its own, and writing too would race it.
            _rainbowWalker = null;
            UpdateEffectDriver();
            UpdateRainbowSpeedText();
            RememberLight();
        }

        // Un solo motor: corre mientras haya rainbow y/o efecto de LED. El intervalo va al ritmo
        // del rainbow si esta activo (hasta 64/s); si solo hay efecto de LED, al ritmo del efecto.
        // El frame del efecto de LED avanza por acumulador de ms, asi su cadencia es independiente
        // de un rainbow mas rapido.
        private void UpdateEffectDriver()
        {
            bool any = RainbowOn || PlayerEffectOn;
            if (!any) { _rainbowTimer?.Stop(); return; }

            _rainbowTimer ??= new DispatcherTimer(DispatcherPriority.Render);
            _rainbowTimer.Tick -= Effect_Tick;
            _rainbowTimer.Tick += Effect_Tick;
            _rainbowTimer.Interval = RainbowOn
                ? RainbowWalker.IntervalFor(TargetColoursPerSecond)
                : TimeSpan.FromMilliseconds(1000.0 / PlayerEffectFps);

            if (RainbowOn) _rainbowWalker ??= new RainbowWalker(CurrentRainbowStyle);
            if (PlayerEffectOn) _playerWalker ??= new PlayerLedWalker(CurrentPlayerEffect);
            _rainbowTimer.Start();
        }

        private void Effect_Tick(object? sender, EventArgs e)
        {
            if (_lightPadId == null) return;
            if (CurrentPlayerLed() == null || CurrentBrightness() == null) return;

            byte r, g, b;
            if (RainbowOn)
            {
                _rainbowWalker ??= new RainbowWalker(CurrentRainbowStyle);
                (r, g, b) = _rainbowWalker.Advance(RainbowWalker.SpeedPlan(TargetColoursPerSecond).coloursPerTick);
                _updatingLight = true;
                try { Picker.SelectedColor = System.Windows.Media.Color.FromRgb(r, g, b); }
                finally { _updatingLight = false; }
            }
            else
            {
                var c = Picker.SelectedColor; r = c.R; g = c.G; b = c.B;
            }

            PlayerLeds player;
            if (PlayerEffectOn)
            {
                _playerWalker ??= new PlayerLedWalker(CurrentPlayerEffect);
                double frameMs = 1000.0 / PlayerEffectFps;
                _playerFrameAccumMs += _rainbowTimer!.Interval.TotalMilliseconds;
                // Recuperar TODOS los frames vencidos, no solo uno: si el tick del rainbow es mas
                // lento que 1/fps, un solo paso acumularia deuda sin fin (camara lenta + rafaga al
                // reajustar). Avanzar el indice por los frames enteros vencidos y guardar el resto.
                if (_playerFrameAccumMs >= frameMs)
                {
                    _playerFrameIndex += (int)(_playerFrameAccumMs / frameMs);
                    _playerFrameAccumMs %= frameMs;
                }
                player = (PlayerLeds)_playerWalker.MaskAt(_playerFrameIndex);
            }
            else
            {
                player = CurrentPlayerLed()!.Value;
            }

            var brightness = CurrentBrightness()!.Value;
            DualSenseLight.Apply(_lightPadId, new LightState(r, g, b, player, brightness));
        }

        private void Rainbow_Toggled(object sender, RoutedEventArgs e)
        {
            if (RainbowOn) _rainbowWalker = new RainbowWalker(CurrentRainbowStyle);
            // Con el rainbow activo el color lo maneja el efecto: deshabilitar el apartado COLOR.
            if (ColorSection != null) ColorSection.IsEnabled = !RainbowOn;
            UpdateEffectDriver();
            LogStatus(RainbowOn ? "Rainbow activo." : "Rainbow desactivado.");
            RememberLight();
        }

        private void PlayerSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_rainbowTimer != null && !RainbowOn && PlayerEffectOn)
                _rainbowTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / PlayerEffectFps);
            UpdatePlayerSpeedText();
            if (!_updatingLight) RememberLight();
        }

        private void PlayerEffect_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingLight) return;
            if (PlayerEffectOn) { _playerWalker = new PlayerLedWalker(CurrentPlayerEffect); _playerFrameIndex = 0; _playerFrameAccumMs = 0; }
            // Con un efecto de LED activo, la seleccion fija de Player la maneja el efecto.
            if (PlayerLedRow != null) PlayerLedRow.IsEnabled = !PlayerEffectOn;
            if (PlayerSpeed != null) PlayerSpeed.IsEnabled = PlayerEffectOn;
            UpdateEffectDriver();
            if (!PlayerEffectOn && !RainbowOn)
                ApplyLightNow();   // vuelve a la seleccion Player fija y persiste
            else
                RememberLight();
        }

        private double TargetColoursPerSecond => RainbowSpeed.Value;

        // Speed is the tick's period, so a drag has to retune the live timer - there is no
        // longer a speed term inside the tick that would pick the change up on its own.
        private void RainbowSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateEffectDriver();
            UpdateRainbowSpeedText();
            RememberLight();
        }

        // Las cifras son lo que el timer ENTREGA de verdad, no lo que se le pidio - para eso se
        // cuenta en ticks. Una etiqueta que promete de mas es justo el defecto que esto arregla.
        //
        // A la vista queda solo la vuelta, que es la unica que se percibe mirando el mando; los
        // colores por segundo y el aviso de que se saltan colores viven en el ToolTip. Se
        // muestran las tres, pero no todas a la vez.
        private void UpdateRainbowSpeedText()
        {
            if (RainbowSpeedText == null || RainbowSpeed == null) return;

            var walker = new RainbowWalker(CurrentRainbowStyle);
            double actual = RainbowWalker.ActualColoursPerSecond(TargetColoursPerSecond);
            double vuelta = walker.CycleSeconds(TargetColoursPerSecond);

            RainbowSpeedText.Text = $"{vuelta:0.#} s";

            string extra = RainbowWalker.ShowsEveryColour(TargetColoursPerSecond)
                ? ""
                : "\nA esta velocidad el mando salta varios colores por cuadro: se ve fluido, pero no pasa por todos.";
            RainbowSpeedText.ToolTip =
                $"Una vuelta completa cada {vuelta:0.#} s, a {actual:0.#} colores por segundo.{extra}";
        }

        // ===== PERFILES (seccion propia) =====
        //
        // Un perfil es UNO: luz + configuracion del mando + tasa. Antes eran dos listas en
        // dos paginas distintas (LightProfile en Luces, RemapProfile en el configurador),
        // y en la practica el usuario queria las dos mitades a la vez. Los dos archivos
        // viejos siguen en disco intactos: la migracion los lee, no los toca.

        private List<GameProfile> _gameProfiles = new();

        // Alto mientras el codigo (no el usuario) repuebla la lista: sin esto, reasignar
        // ItemsSource dispara SelectionChanged y aplicaria un perfil que nadie eligio.
        private bool _updatingProfiles;

        // Fila de la lista: el perfil mas el texto que se ve de el. Aparte de GameProfile
        // para que el modelo que va a JSON no cargue con cadenas de UI.
        private sealed class GameProfileRow
        {
            public GameProfileRow(GameProfile profile)
            {
                Profile = profile;
                Contents = (profile.Light != null, profile.Remap != null) switch
                {
                    (true, true) => "Luz y configuracion del mando",
                    (true, false) => "Solo luz",
                    (false, true) => "Solo configuracion del mando",
                    _ => "Vacio",
                };
                RateLabel = profile.Rate switch
                {
                    null => "",
                    0 => "Default",
                    _ => $"{profile.Rate} Hz",
                };
            }

            public GameProfile Profile { get; }
            public string Name => Profile.Name;
            public string Contents { get; }
            public string RateLabel { get; }
        }

        private void LoadGameProfiles()
        {
            _gameProfiles = GameProfileStore.Load();

            // Migracion, una sola vez: si no hay archivo nuevo pero si perfiles viejos, se
            // funden por nombre y se escriben al nuevo. Los viejos se quedan donde estan.
            if (_gameProfiles.Count == 0 && !System.IO.File.Exists(GameProfileStore.Path))
            {
                var migrated = GameProfileStore.Migrate(ProfileStore.Load(), RemapProfileStore.Load());
                if (migrated.Count > 0)
                {
                    var saved = GameProfileStore.Save(migrated);
                    _gameProfiles = migrated;
                    LogStatus(saved.Success
                        ? $"{migrated.Count} perfil(es) de antes migrados al formato unico."
                        : $"Perfiles migrados en memoria, pero no se pudieron guardar: {saved.Error}");
                }
            }

            RefreshGameProfileList();
        }

        private void RefreshGameProfileList()
        {
            _updatingProfiles = true;
            try
            {
                var selected = (GameProfileList.SelectedItem as GameProfileRow)?.Name;
                GameProfileList.ItemsSource = null;
                GameProfileList.ItemsSource = _gameProfiles
                    .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(p => new GameProfileRow(p))
                    .ToList();

                if (selected != null)
                {
                    foreach (GameProfileRow row in GameProfileList.Items)
                        if (string.Equals(row.Name, selected, StringComparison.OrdinalIgnoreCase))
                        {
                            GameProfileList.SelectedItem = row;
                            break;
                        }
                }
            }
            finally { _updatingProfiles = false; }

            GameProfilesEmpty.Visibility = _gameProfiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (GameProfileRate.Items.Count == 0) BuildProfileRateCombo();
        }

        // Las mismas ranuras que el combo de la pagina de dispositivos, con etiquetas planas:
        // aqui no hay un dispositivo elegido contra el que calcular alcanzabilidad. Si la tasa
        // no le sirve al dispositivo, SetDeviceRate lo dice al aplicar el perfil.
        private void BuildProfileRateCombo()
        {
            GameProfileRate.Items.Add(new ComboBoxItem { Content = "Sin tasa", Tag = null });
            GameProfileRate.Items.Add(new ComboBoxItem { Content = "Default", Tag = 0 });
            foreach (int slot in new[] { 8000, 4000, 2000, 1000, 500, 250, 125, 62, 31 })
                GameProfileRate.Items.Add(new ComboBoxItem { Content = $"{slot} Hz", Tag = slot });
            GameProfileRate.SelectedIndex = 0;
        }

        // Seleccionar un perfil lo APLICA. No hay boton "Cargar" ni "Aplicar": un perfil que
        // esta seleccionado pero no aplicado es un estado que solo sirve para confundir - la
        // lista muestra lo que esta puesto ahora mismo, no una intencion pendiente.
        private void GameProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingProfiles) return;
            if (GameProfileList.SelectedItem is not GameProfileRow row) return;

            ApplyGameProfile(row.Profile);

            // La caja de nombre y la tasa siguen a la seleccion, para que GUARDAR encima del
            // mismo perfil sea escribir el nombre cero veces.
            GameProfileName.Text = row.Profile.Name;
            SelectProfileRate(row.Profile.Rate);
        }

        private void SelectProfileRate(int? rate)
        {
            foreach (ComboBoxItem item in GameProfileRate.Items)
            {
                if ((int?)item.Tag == rate) { GameProfileRate.SelectedItem = item; return; }
            }
            GameProfileRate.SelectedIndex = 0;
        }

        private int? SelectedProfileRate =>
            (GameProfileRate.SelectedItem as ComboBoxItem)?.Tag as int?;

        // Aplica SOLO las mitades que el perfil traiga. Un perfil sin luz no cambia la luz.
        private void ApplyGameProfile(GameProfile p)
        {
            var notes = new List<string>();

            if (p.Light != null)
            {
                if (_lightPadId != null)
                {
                    ApplyProfileLight(p.Light);
                    notes.Add("luz");
                }
                else
                {
                    notes.Add("luz omitida (sin mando)");
                }
            }

            if (p.Remap != null)
            {
                try
                {
                    _updatingRemap = true;
                    _remap = CloneRemapSettings(p.Remap);
                    _remap.Sanitize();   // perfiles viejos con presets retirados -> Lineal
                    ApplyRemapSettingsToControls();
                }
                finally { _updatingRemap = false; }

                PersistLastUsedRemap();   // el recien aplicado pasa a ser el "ultimo usado"
                notes.Add("mando");
            }

            if (p.Rate != null)
            {
                // La tasa va al dispositivo elegido en DISPOSITIVOS, que es donde viven las
                // tasas; aplicarla a otro seria escribir en el equivocado sin avisar.
                if (DevicesListBox.SelectedItem is UsbDeviceModel target)
                {
                    var r = SystemManager.SetDeviceRate(target.InstanceId, target.DriverKey, p.Rate.Value, target.BusSpeed);
                    // A proposito sin replug automatico: arrancarle el mando del bus al usuario
                    // por elegir un perfil seria una sorpresa hostil.
                    notes.Add(r.Success
                        ? $"tasa {p.Rate} Hz escrita (pulsa RECONECTAR)"
                        : $"tasa fallida: {r.Error}");
                }
                else
                {
                    notes.Add("tasa omitida (elige un dispositivo en Dispositivos)");
                }
            }

            LogStatus(notes.Count == 0
                ? $"Perfil '{p.Name}' no lleva nada que aplicar."
                : $"Perfil '{p.Name}' aplicado: {string.Join(", ", notes)}.");
        }

        // Empuja una intencion de luz a los controles y la aplica al mando. Mismo camino que
        // usa el arranque para reflejar lo guardado, aqui reutilizado por los perfiles.
        private void ApplyProfileLight(LightIntent li)
        {
            _updatingLight = true;
            try
            {
                Picker.SelectedColor = Color.FromRgb(li.R, li.G, li.B);
                SelectSegmentByTag(PlayerLedRow, li.Player);
                SelectSegmentByTag(BrightnessRow, li.Brightness);
                SelectComboByTag(RainbowStyleList, li.Style);
                RainbowSpeed.Value = Math.Clamp(li.RainbowColoursPerSecond,
                    (int)RainbowWalker.MinColoursPerSecond, (int)RainbowWalker.MaxColoursPerSecond);
                SelectComboByTag(PlayerEffectList, li.PlayerEffect);
                PlayerSpeed.Value = Math.Clamp(li.PlayerEffectFps, 2, 20);
            }
            finally { _updatingLight = false; }

            // PlayerEffect_Changed se salta el trabajo bajo _updatingLight, asi que el walker
            // del efecto se rearma aqui, igual que hace el arranque con la intencion guardada.
            if (li.PlayerEffect != PlayerLedEffect.None)
            {
                _playerWalker = new PlayerLedWalker(li.PlayerEffect);
                _playerFrameIndex = 0;
                _playerFrameAccumMs = 0;
            }
            PlayerSpeed.IsEnabled = PlayerEffectOn;
            PlayerLedRow.IsEnabled = !PlayerEffectOn;

            // Fuera del guard: marcar el check dispara Rainbow_Toggled, que arranca o para el
            // motor de efectos con todo lo de arriba ya puesto.
            bool wantsRainbow = li.Kind == LightIntentKind.Rainbow;
            if (RainbowCheck.IsChecked != wantsRainbow) RainbowCheck.IsChecked = wantsRainbow;
            else UpdateEffectDriver();

            if (!RainbowOn) ApplyLightNow();   // ya persiste via RememberLight
            else RememberLight();
        }

        private void SaveGameProfile_Click(object sender, RoutedEventArgs e)
        {
            string name = GameProfileName.Text.Trim();
            if (string.IsNullOrEmpty(name) && GameProfileList.SelectedItem is GameProfileRow sel)
                name = sel.Name;

            if (string.IsNullOrEmpty(name)) { LogStatus("Ponle un nombre al perfil primero."); return; }
            if (string.Equals(name, GameProfileStore.LastUsedPseudoProfile, StringComparison.OrdinalIgnoreCase))
            {
                LogStatus("Ese nombre esta reservado. Elige otro.");
                return;
            }

            bool overwrote = _gameProfiles.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            _gameProfiles.Add(new GameProfile
            {
                Name = name,
                Rate = SelectedProfileRate,
                Light = BuildCurrentIntent(),
                Remap = CloneRemapSettings(_remap),
            });

            var result = GameProfileStore.Save(_gameProfiles);
            if (!result.Success) { ShowError("Perfil no guardado", result.Error!); return; }

            RefreshGameProfileList();
            // Sobrescribir se avisa en la barra de estado, no con un dialogo: el usuario acaba
            // de escribir un nombre que ya estaba en la lista que tiene delante.
            LogStatus(overwrote ? $"Perfil '{name}' sobrescrito." : $"Perfil '{name}' guardado.");
        }

        private void DeleteGameProfile_Click(object sender, RoutedEventArgs e)
        {
            if (GameProfileList.SelectedItem is not GameProfileRow row) { LogStatus("Selecciona un perfil."); return; }

            _gameProfiles.RemoveAll(x => string.Equals(x.Name, row.Name, StringComparison.OrdinalIgnoreCase));
            var result = GameProfileStore.Save(_gameProfiles);
            if (!result.Success) { ShowError("Perfil no borrado", result.Error!); return; }

            RefreshGameProfileList();
            LogStatus($"Perfil '{row.Name}' borrado. Hay una copia en {GameProfileStore.Path}.backup");
        }

        // Scan and populate the device list
        private bool _intentReapplied;

        // Camino RAPIDO, el del arranque: aplica la luz guardada sin esperar a nada. Resuelve
        // el mando enumerando HID en-proceso (milisegundos) en vez de leer _allDevices, que
        // no existe hasta que termina el escaneo de PowerShell.
        private void ApplySavedLightNow()
        {
            try
            {
                var intent = IntentStore.Load();
                if (intent == null) { _intentReapplied = true; return; }

                string? id = HidHideControl.FindPhysicalGamepadInstanceId();
                if (id == null) return;   // sin mando aun: lo recoge ReapplyIntent o el replug

                DualSenseLight.Apply(id, intent.ToLightState());
                _intentReapplied = true;
                LogStatus("Color del mando restaurado de la ultima sesion.");
            }
            catch
            {
                // Encender una luz jamas puede impedir que la app abra; si falla, el camino
                // lento de ReapplyIntent lo intenta de nuevo al terminar el escaneo.
            }
        }

        // Camino LENTO, la RED de reconexion: reaplica la intencion cuando aparece un mando
        // que no estaba al arrancar (replug, o encendido despues de abrir la app). Lo llama
        // el final del escaneo; _intentReapplied evita que pise al camino rapido de arriba.
        // Los dos existen a proposito: no fusionarlos.
        //
        // El mando ya se resolvio unas lineas antes, en el mismo Dispatcher.Invoke, via
        // ResolveLightPad() -> _lightPadId. Buscarlo otra vez aqui (incluia un UsbDeviceModel
        // sintetico desechable solo para leer su InstanceId) era trabajo duplicado; _lightPadId
        // es ahora el unico camino para saber que mando hay.
        private void ReapplyIntent()
        {
            if (_intentReapplied) return;
            var intent = IntentStore.Load();
            if (intent == null) { _intentReapplied = true; return; }

            if (_lightPadId == null) return;   // sin mando aun; se reintenta al reconectar (Task B4)
            _intentReapplied = true;

            if (intent.Kind == LightIntentKind.Static)
            {
                DualSenseLight.Apply(_lightPadId, intent.ToLightState());
            }
            else
            {
                DualSenseLight.Apply(_lightPadId, intent.ToLightState()); // color base + LEDs
                // El arranque real del rainbow (walker + timer) se hace cuando el usuario
                // abre la pestana; aqui se deja el mando en un color valido con los LEDs
                // correctos para no arrancar animacion en segundo plano en el Dashboard.
            }
            LogStatus("Color del mando restaurado de la ultima sesion.");
        }

        private void RefreshDevicesList()
        {
            LogStatus("Scanning USB devices...");
            var mode = ActiveMode;

            // ScanDevices builds brand-new UsbDeviceModel instances every call (no
            // Equals override, no identity beyond InstanceId), and ApplyFilters()
            // below reassigns ItemsSource, which clears the ListBox selection. Both
            // together used to collapse the detail panel on every action that
            // triggers a rescan. Capture the stable identity now so it can be
            // restored once the new models exist.
            string? selectedInstanceId = (DevicesListBox.SelectedItem as UsbDeviceModel)?.InstanceId;

            // Se muestra durante una espera REAL (el escaneo de dispositivos), no como adorno.
            SetDevicesBusy(true);

            // Scan in background so UI doesn't stutter
            Task.Run(() =>
            {
                List<UsbDeviceModel> devices;
                try
                {
                    devices = SystemManager.ScanDevices(mode);
                }
                catch (Exception ex)
                {
                    // A scan that throws must not leave the dots pulsing forever over an
                    // empty list, telling the user something is still loading when nothing is.
                    Dispatcher.Invoke(() =>
                    {
                        SetDevicesBusy(false);
                        LogStatus($"Scan failed: {ex.Message}");
                    });
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    SetDevicesBusy(false);
                    _allDevices = devices;
                    ResolveLightPad();   // el mando de las luces puede haber cambiado de puerto o haberse ido
                    ApplyFilters();

                    // Re-select the same device by InstanceId among the freshly
                    // filtered items. If it was filtered out (or unplugged), leave
                    // the selection cleared rather than guessing. This only changes
                    // DevicesListBox.SelectedItem, which drives PopulateRateCombo
                    // through the normal SelectionChanged path (DetailRateCombo's
                    // handler is detached/reattached there), so no rate write fires.
                    if (selectedInstanceId != null &&
                        DevicesListBox.ItemsSource is IEnumerable<UsbDeviceModel> currentItems)
                    {
                        var restored = currentItems.FirstOrDefault(d => d.InstanceId == selectedInstanceId);
                        if (restored != null) DevicesListBox.SelectedItem = restored;
                    }

                    int unknown = _allDevices.Count(d => !d.SpeedKnown);
                    string suffix = unknown > 0 ? $" ({unknown} with unknown bus speed)" : "";
                    LogStatus($"Scan completed. Found {_allDevices.Count} devices{suffix}.");

                    ReapplyIntent();
                });
            });
        }

        // Apply search and status filters to the listbox
        private void ApplyFilters()
        {
            bool onlyControllers = OnlyControllersCheck.IsChecked == true;
            bool onlyFiltered = OnlyFilteredCheck.IsChecked == true;

            var filtered = _allDevices.Where(d =>
                (!onlyControllers || d.IconKind == "Gamepad") &&
                (!onlyFiltered || d.FilterActive)).ToList();

            DevicesListBox.ItemsSource = filtered;
            // "(n de m)": n es lo que pasa el filtro, m es el total conectado. Mostrar
            // solo n bajo el titulo CONECTADOS mentiria con el filtro activo - por
            // ejemplo "CONECTADOS (1)" con 1 de 12 dispositivos coincidiendo.
            DeviceCountText.Text = filtered.Count.ToString();
            DeviceCountTotalText.Text = _allDevices.Count.ToString();
        }

        // Event: Checkbox filter changed
        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitializing) ApplyFilters();
        }

        // The list selection drives the detail panel, so the rate options are
        // rebuilt whenever the selected device changes.
        private void DevicesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var model = DevicesListBox.SelectedItem as UsbDeviceModel;
            PopulateRateCombo(model);
            StartMeasuring(model);
        }

        // One open HID handle at most, and only while a device is selected.
        private void StartMeasuring(UsbDeviceModel? model)
        {
            _meter.Stop();
            _meterTimer?.Stop();
            // Parar el timer no borra lo que hay en pantalla: sin esto, el primer tick
            // (100 ms) del nuevo dispositivo se dibuja sobre el Hz y la pastilla del
            // dispositivo anterior.
            UpdateMeasuredReadout(null);

            if (model == null)
            {
                DrawSpectrum(Array.Empty<double>(), null);
                UpdateMeasuredReadout(null);
                return;
            }

            if (!_meter.Start(model.InstanceId))
            {
                // Not measurable is a different claim from 0 Hz, and the user is owed
                // the reason rather than an empty number.
                MeasuredText.Text = $"no medible ({_meter.Unavailable})";
                MeasuredText.Foreground = (Brush)FindResource("TextLabelBrush");
                MeasuredDot.Fill = (Brush)FindResource("TextMutedBrush");
                MeasuredGapText.Text = "--";
                // Cleared, or a mismatch warning from the previously selected device
                // would sit here talking about a device that is no longer on screen.
                MatchHintText.Text = "";
                // Sin muestra no hay nada que clasificar: un distintivo colgado con el
                // valor del dispositivo anterior seria peor que no tener distintivo.
                SteadinessChip.Visibility = Visibility.Collapsed;
                DrawSpectrum(Array.Empty<double>(), null);
                return;
            }

            _meterTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _meterTimer.Tick -= MeterTick;
            _meterTimer.Tick += MeterTick;
            _meterTimer.Start();
        }

        private void UpdateMeasuredReadout(RateSample? sample)
        {
            if (DevicesListBox.SelectedItem is not UsbDeviceModel model)
            {
                MeasuredText.Text = "--";
                MeasuredGapText.Text = "--";
                MeasuredDot.Fill = (Brush)FindResource("TextMutedBrush");
                SteadinessChip.Visibility = Visibility.Collapsed;
                return;
            }

            if (sample == null)
            {
                // Reports stopped arriving, or none ever did. Keeping the last reading
                // on screen would claim a rate the device is no longer achieving.
                MeasuredText.Text = "sin datos";
                MeasuredText.Foreground = (Brush)FindResource("TextLabelBrush");
                MeasuredGapText.Text = "--";
                MeasuredDot.Fill = (Brush)FindResource("TextMutedBrush");
                MatchHintText.Text = "";
                // Snapshot() vuelve null tanto si nunca llego nada como si el flujo
                // lleva 2 s callado: en los dos casos no hay muestra que clasificar.
                SteadinessChip.Visibility = Visibility.Collapsed;
                return;
            }

            double hz = sample.Value.MedianHz;
            MeasuredText.Text = $"{hz:0.#} Hz";
            MeasuredText.Foreground = (Brush)FindResource("TextDataBrush");
            MeasuredGapText.Text = $"{sample.Value.MedianGapMs:0.###} ms";

            // El distintivo dice como de REGULAR llega el flujo, no si la tasa es la
            // pedida - eso lo dice el punto de PEDIDA, justo debajo. Son dos preguntas
            // distintas: un mando puede ir a la tasa correcta con tirones, y uno a tasa
            // equivocada puede ir finisimo.
            var firmeza = RateStability.Classify(
                sample.Value.MedianGapMs, sample.Value.P95GapMs, sample.Value.P99GapMs, sample.Value.Count);

            (SteadinessChipText.Text, var puntoFirmeza) = firmeza switch
            {
                RateSteadiness.Regular   => ("REGULAR",   StatusLevel.Ok),
                RateSteadiness.Irregular => ("IRREGULAR", StatusLevel.Warn),
                _                        => ("SIN DATOS", StatusLevel.Idle),
            };
            SteadinessChipDot.Fill = StatusBrush(puntoFirmeza);
            SteadinessChip.Visibility = Visibility.Visible;

            // Green when the device is doing what was asked; amber when it is not. This
            // one dot is the answer to the question the app could never answer before.
            int? want = model.ResolvedRate;
            if (want == null)
            {
                MeasuredDot.Fill = (Brush)FindResource("TextMutedBrush");
                MatchHintText.Text = "";
                return;
            }

            bool matches = PollingCore.RateMatches(hz, want.Value);
            MeasuredDot.Fill = StatusBrush(matches ? StatusLevel.Ok : StatusLevel.Warn);

            // An amber dot alone would leave the user guessing. Writing bInterval does
            // not reconfigure the device - it only takes effect on re-enumeration - so a
            // mismatch almost always means the rate was written and never applied. Say
            // that, and say what fixes it.
            MatchHintText.Text = matches
                ? ""
                : $"Escrita pero no aplicada: el mando sigue a {hz:0.#} Hz. Pulsa RECONECTAR.";
        }

        private void PopulateRateCombo(UsbDeviceModel? model)
        {
            DetailRateCombo.SelectionChanged -= DetailRateCombo_SelectionChanged;
            DetailRateCombo.Items.Clear();

            if (model == null)
            {
                DetailRateCombo.IsEnabled = false;
                DetailRateCombo.SelectionChanged += DetailRateCombo_SelectionChanged;
                return;
            }

            // Highest first, matching Setup.exe's ordering.
            //
            // Two mechanisms feed this list and they can collide. On High/Super Speed
            // 8000/4000/2000 are native (bInterval 1/2/3 = 125/250/500us) and slots
            // 31/62 are literally 31 and 62 Hz. On Low/Full Speed the native high
            // rates are unreachable, and a patched driver smuggles them through the
            // 31/62 slots instead - so under 2k-4k / 4k-8k a dead native entry and a
            // live slot entry resolve to the same label. One label, one entry: the
            // one that actually works wins. Offering the user two identical "8000 Hz"
            // rows, only one of which does anything, is the kind of lie this UI exists
            // to stop telling.
            var candidates = new[] { 0, 8000, 4000, 2000, 1000, 500, 250, 125, 62, 31 }
                .Select(tag => new
                {
                    Tag = tag,
                    Label = tag == 0
                        ? "Default"
                        : $"{PollingCore.ResolveHighRateSlot(tag, ActiveMode, model.BusSpeed) ?? tag} Hz",
                    Reachable = tag == 0 ||
                                PollingCore.TryMapRateToBInterval(tag, model.BusSpeed) != null
                })
                .GroupBy(c => c.Label)
                .Select(g => g.OrderByDescending(c => c.Reachable).First());

            foreach (var c in candidates)
            {
                DetailRateCombo.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Tag, IsEnabled = c.Reachable });
            }

            foreach (ComboBoxItem item in DetailRateCombo.Items)
            {
                if ((int)item.Tag == (model.SelectedRate ?? 0)) { DetailRateCombo.SelectedItem = item; break; }
            }

            DetailRateCombo.IsEnabled = model.SpeedKnown;
            DetailRateCombo.ToolTip = model.SpeedKnown
                ? null
                : "Velocidad de bus desconocida: el intervalo no se puede calcular con seguridad.";

            DetailRateCombo.SelectionChanged += DetailRateCombo_SelectionChanged;
        }

        private void DetailRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // La tasa se aplica al pulsar APLICAR CAMBIOS, no al elegirla en la lista.
            // Este handler se deja vacio a proposito; el valor se lee del combo en
            // ApplyOverclock_Click.
        }

        // Un clic = todo el overclock. Encadena lo que antes eran tres botones: activa el
        // filtro, escribe la tasa y hace el replug (lo unico que la aplica de verdad).
        // Para el medidor antes del replug por la misma razon que el replug de SystemManager:
        // CM_Query_And_Remove_SubTree se veta si algo tiene el dispositivo abierto.
        private async void ApplyOverclock_Click(object sender, RoutedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is not UsbDeviceModel model)
            {
                LogStatus("Selecciona un dispositivo primero.");
                return;
            }
            if (DetailRateCombo.SelectedItem is not ComboBoxItem item)
            {
                LogStatus("Elige una tasa en CONFIGURACIÓN DE TASA.");
                return;
            }
            int rate = (int)item.Tag;
            string original = (string)ApplyOverclockBtn.Content;
            try
            {
                _overclockBusy = true;
                ApplyOverclockBtn.IsEnabled = false;
                ResetOverclockBtn.IsEnabled = false;
                ApplyOverclockBtn.Content = "APLICANDO...";
                _meter.Stop();
                _meterTimer?.Stop();
                // Parar el timer no borra lo que hay en pantalla: sin esto, la pastilla
                // REGULAR y el Hz del dispositivo se quedan durante el replug, mintiendo
                // sobre un dispositivo que esta fuera del bus.
                UpdateMeasuredReadout(null);

                var filter = SystemManager.SetFilterActive(model.InstanceId, true);
                if (!filter.Success) { LogStatus($"No se pudo activar el filtro: {filter.Error}"); return; }

                var rateRes = SystemManager.SetDeviceRate(model.InstanceId, model.DriverKey, rate, model.BusSpeed);
                if (!rateRes.Success) { LogStatus($"No se pudo escribir la tasa: {rateRes.Error}"); return; }

                var replug = await SystemManager.ReplugDevice(model.InstanceId);
                if (!replug.Success)
                {
                    LogStatus($"Reconexion fallida: {replug.Error}");
                    ShowError("Reconexion fallida", replug.Error!);
                    return;
                }
                LogStatus($"Overclock aplicado: {rate} Hz. Mueve el dispositivo para ver la tasa medida.");
            }
            finally
            {
                ApplyOverclockBtn.Content = original;
                ApplyOverclockBtn.IsEnabled = true;
                ResetOverclockBtn.IsEnabled = true;
                RefreshDevicesList();   // restaura la seleccion, lo que reinicia el medidor
                _overclockBusy = false;
            }
        }

        // Emergencia: quita el filtro y reconecta, dejando el dispositivo en su estado por
        // defecto. Sustituye a la vieja funcion de REINICIAR/quitar filtro manual.
        private async void ResetOverclock_Click(object sender, RoutedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is not UsbDeviceModel model)
            {
                LogStatus("Selecciona un dispositivo primero.");
                return;
            }
            try
            {
                _overclockBusy = true;
                ApplyOverclockBtn.IsEnabled = false;
                ResetOverclockBtn.IsEnabled = false;
                _meter.Stop();
                _meterTimer?.Stop();
                // Parar el timer no borra lo que hay en pantalla: sin esto, la pastilla
                // REGULAR y el Hz del dispositivo se quedan durante el replug, mintiendo
                // sobre un dispositivo que esta fuera del bus.
                UpdateMeasuredReadout(null);

                var filter = SystemManager.SetFilterActive(model.InstanceId, false);
                if (!filter.Success) { LogStatus($"No se pudo quitar el filtro: {filter.Error}"); return; }

                // Borrar tambien la tasa escrita (rate 0 = DeleteValue("bInterval")). Quitar
                // solo el filtro deja el dispositivo funcionando por defecto HOY, pero el
                // valor sigue en el registro: si el filtro se reactiva por cualquier via
                // -esta app, la GUI original de hidusbf, otra herramienta- la tasa vieja
                // vuelve sola. "Restablecer valores" tiene que dejar limpio lo que
                // APLICAR CAMBIOS escribio, que son las dos cosas.
                var rate = SystemManager.SetDeviceRate(model.InstanceId, model.DriverKey, 0, model.BusSpeed);
                if (!rate.Success) { LogStatus($"No se pudo borrar la tasa guardada: {rate.Error}"); return; }

                var replug = await SystemManager.ReplugDevice(model.InstanceId);
                if (!replug.Success) { LogStatus($"Reconexion fallida: {replug.Error}"); return; }
                LogStatus($"{model.Name} restablecido: filtro quitado y tasa borrada.");
            }
            finally
            {
                ApplyOverclockBtn.IsEnabled = true;
                ResetOverclockBtn.IsEnabled = true;
                RefreshDevicesList();
                _overclockBusy = false;
            }
        }

        // Event: Global driver mode selection changes
        private void GlobalModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GlobalModeComboBox.SelectedItem is not ComboBoxItem item) return;

            string modeText = item.Content.ToString() ?? "";
            var mode = PollingCore.ParseMode(modeText.Split(' ')[0]);
            if (mode == null)
            {
                LogStatus($"Unrecognised driver mode '{modeText}'.");
                return;
            }

            LogStatus($"Changing global driver mode to {PollingCore.DescribeMode(mode.Value)}...");

            Task.Run(() =>
            {
                var result = SystemManager.ChangeDriverMode(mode.Value);

                Dispatcher.Invoke(() =>
                {
                    if (result.Success)
                    {
                        LogStatus($"Driver mode changed to {PollingCore.DescribeMode(mode.Value)}. Restart filtered devices to apply.");
                    }
                    else
                    {
                        LogStatus($"Failed to change driver mode: {result.Error}");
                        ShowError("Mode Change Failed", result.Error!);
                    }

                    // Re-read either way: the UI must show what the system really is,
                    // including a partial change.
                    RefreshStatus();
                    RefreshDevicesList();
                });
            });
        }

        // Event: Click Install Service button
        private void InstallServiceBtn_Click(object sender, RoutedEventArgs e)
        {
            LogStatus("Installing hidusbf filter driver service...");
            var result = SystemManager.InstallService(DriverMode.Rate1k);
            if (result.Success)
            {
                LogStatus("Filter service installed successfully!");
            }
            else
            {
                LogStatus($"Failed to install filter service: {result.Error}");
                ShowError("Install Failed", result.Error!);
            }
            RefreshStatus();
            RefreshDevicesList();
        }

        // Event: Click Uninstall Service button
        private void UninstallServiceBtn_Click(object sender, RoutedEventArgs e)
        {
            LogStatus("Uninstalling hidusbf filter driver service...");
            var result = SystemManager.UninstallService();
            if (result.Success)
            {
                LogStatus("Filter service uninstalled and removed.");
            }
            else
            {
                LogStatus($"Failed to uninstall filter service: {result.Error}");
                ShowError("Uninstall Failed", result.Error!);
            }
            RefreshStatus();
            RefreshDevicesList();
        }

        // Event: Click Refresh Devices button
        private void RefreshDevicesBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshStatus();
            RefreshDevicesList();
        }

        // Event: Click Restart All Devices button
        private async void RestartAllBtn_Click(object sender, RoutedEventArgs e)
        {
            var filteredDevices = _allDevices.Where(d => d.FilterActive).ToList();
            if (filteredDevices.Count == 0)
            {
                LogStatus("No active filtered devices found to restart.");
                return;
            }

            RestartAllBtn.IsEnabled = false;
            LogStatus($"Restarting {filteredDevices.Count} filtered devices...");

            int successCount = 0;
            foreach (var dev in filteredDevices)
            {
                LogStatus($"Restarting {dev.Name}...");
                var result = await SystemManager.RestartDevice(dev.InstanceId);
                if (result.Success) successCount++;
            }

            LogStatus($"Restart complete. Successfully restarted {successCount}/{filteredDevices.Count} devices.");
            RestartAllBtn.IsEnabled = true;
        }

        // ===== BIBLIOTECA "MIS CURVAS" (Task 4) =====
        private void RefreshCurveLibraryLists()
        {
            _savedCurves = CurveLibraryStore.Load();

            // Refresca combo izquierdo
            var leftSel = LeftSavedCurveList.SelectedItem as SavedCurve;
            LeftSavedCurveList.ItemsSource = null;
            LeftSavedCurveList.ItemsSource = _savedCurves;
            if (leftSel != null)
                LeftSavedCurveList.SelectedItem = _savedCurves.FirstOrDefault(c => c.Name == leftSel.Name);

            // Refresca combo derecho
            var rightSel = RightSavedCurveList.SelectedItem as SavedCurve;
            RightSavedCurveList.ItemsSource = null;
            RightSavedCurveList.ItemsSource = _savedCurves;
            if (rightSel != null)
                RightSavedCurveList.SelectedItem = _savedCurves.FirstOrDefault(c => c.Name == rightSel.Name);
        }

        private void SaveCurveGeneric(bool isLeft, string name, List<CurvePoint> points)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                LogStatus("Introduce un nombre para la curva.");
                return;
            }
            name = name.Trim();
            if (name.Equals("Lineal", StringComparison.OrdinalIgnoreCase) || name.Equals("Editor", StringComparison.OrdinalIgnoreCase))
            {
                LogStatus("No puedes usar nombres reservados ('Lineal', 'Editor').");
                return;
            }

            var existing = _savedCurves.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Points = new List<CurvePoint>(points);
            }
            else
            {
                _savedCurves.Add(new SavedCurve { Name = name, Points = new List<CurvePoint>(points) });
            }

            var res = CurveLibraryStore.Save(_savedCurves);
            if (!res.Success)
            {
                ShowError("Error al guardar curva", res.Error!);
                return;
            }

            RefreshCurveLibraryLists();
            if (isLeft)
            {
                LeftCurveName.Text = "";
                LeftSavedCurveList.SelectedItem = _savedCurves.FirstOrDefault(c => c.Name == name);
            }
            else
            {
                RightCurveName.Text = "";
                RightSavedCurveList.SelectedItem = _savedCurves.FirstOrDefault(c => c.Name == name);
            }
            LogStatus($"Curva '{name}' guardada en la biblioteca.");
        }

        private void LoadCurveGeneric(bool isLeft, SavedCurve? curve)
        {
            if (curve == null)
            {
                LogStatus("Selecciona una curva de la biblioteca.");
                return;
            }

            try
            {
                _updatingRemap = true;
                if (isLeft)
                {
                    _remap.LeftCurve = ResponseCurve.Propia;
                    _remap.LeftCurvePoints = new List<CurvePoint>(curve.Points);
                    SelectComboByTag(LeftCurveList, ResponseCurve.Propia);
                    RedrawLeftCurve();
                }
                else
                {
                    _remap.RightCurve = ResponseCurve.Propia;
                    _remap.RightCurvePoints = new List<CurvePoint>(curve.Points);
                    SelectComboByTag(RightCurveList, ResponseCurve.Propia);
                    RedrawRightCurve();
                }
            }
            finally
            {
                _updatingRemap = false;
            }

            RememberRemap();
            LogStatus($"Curva '{curve.Name}' aplicada al stick {(isLeft ? "izquierdo" : "derecho")}.");
        }

        private void DeleteCurveGeneric(bool isLeft, SavedCurve? curve)
        {
            if (curve == null)
            {
                LogStatus("Selecciona una curva para borrar.");
                return;
            }

            _savedCurves.RemoveAll(c => c.Name == curve.Name);
            var res = CurveLibraryStore.Save(_savedCurves);
            if (!res.Success)
            {
                ShowError("Error al borrar curva", res.Error!);
                return;
            }

            RefreshCurveLibraryLists();
            LogStatus($"Curva '{curve.Name}' eliminada de la biblioteca.");
        }

        private void LoadLeftCurve_Click(object sender, RoutedEventArgs e)
            => LoadCurveGeneric(true, LeftSavedCurveList.SelectedItem as SavedCurve);

        private void LoadRightCurve_Click(object sender, RoutedEventArgs e)
            => LoadCurveGeneric(false, RightSavedCurveList.SelectedItem as SavedCurve);

        private void DeleteLeftCurve_Click(object sender, RoutedEventArgs e)
            => DeleteCurveGeneric(true, LeftSavedCurveList.SelectedItem as SavedCurve);

        private void DeleteRightCurve_Click(object sender, RoutedEventArgs e)
            => DeleteCurveGeneric(false, RightSavedCurveList.SelectedItem as SavedCurve);

        private void SaveLeftCurve_Click(object sender, RoutedEventArgs e)
            => SaveCurveGeneric(true, LeftCurveName.Text, _remap.LeftCurvePoints);

        private void SaveRightCurve_Click(object sender, RoutedEventArgs e)
            => SaveCurveGeneric(false, RightCurveName.Text, _remap.RightCurvePoints);

        private static void ShowError(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Cuanto se queda un mensaje antes de que la barra se vuelva a callar. 6 s: lo
        // suficiente para leer una frase sin volver a mirar, poco para que estorbe.
        private static readonly TimeSpan StatusLinger = TimeSpan.FromSeconds(6);
        private DispatcherTimer? _statusHide;

        // La barra es MUDA: aparece al tener algo que decir y se retira sola. Cada mensaje
        // nuevo reinicia la cuenta, asi que una rafaga (escaneo -> resultado) no parpadea.
        private void LogStatus(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            StatusLogText.Text = $"[{timestamp}] {message}";
            StatusBar.Visibility = Visibility.Visible;

            _statusHide ??= new DispatcherTimer { Interval = StatusLinger };
            _statusHide.Tick -= StatusHide_Tick;
            _statusHide.Tick += StatusHide_Tick;
            _statusHide.Stop();
            _statusHide.Start();
        }

        private void StatusHide_Tick(object? sender, EventArgs e)
        {
            _statusHide!.Stop();
            StatusBar.Visibility = Visibility.Collapsed;
            StatusLogText.Text = "";
        }
    }
}

