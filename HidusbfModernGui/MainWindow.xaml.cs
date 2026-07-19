using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

        // Mode used to interpret the 31/62 slots. Falls back to NoPatch so the UI
        // shows literal 31Hz/62Hz rather than claiming an overclock we cannot prove.
        private DriverMode ActiveMode => _driverState.EffectiveMode ?? DriverMode.NoPatch;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Guard de arranque (Task 6/14): si un run anterior murio con el DualSense
            // oculto por HidHide, re-mostrarlo ahora para no dejar el mando "desaparecido".
            // Best-effort y silencioso: nunca debe impedir que la app abra.
            try { new HidHideControl().ShowAllDualSense(); } catch { }

            BuildHeaderSpectrum();
            BuildLoadingIndicator();
            RefreshStatus();
            RefreshDevicesList();
            BuildRemapControls();
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
                        if (_overclockBusy) return;  // no competir con un replug en curso
                        // El propio motor provoca este WM_DEVICECHANGE (HidHide reinicia el
                        // devnode del DualSense al ocultarlo/mostrarlo). Sin este guard, ese
                        // replug propio dispara un RefreshDevicesList() (~1s de PowerShell) que
                        // compite por el hilo de UI con el propio Start/StopEngine.
                        if (_engineBusy) return;
                        _intentReapplied = false;   // permitir reaplicar al mando reaparecido
                        RefreshDevicesList();        // repuebla _allDevices y reaplica la luz
                    };
                }
                _deviceChangeDebounce.Stop();
                _deviceChangeDebounce.Start();
            }
            return IntPtr.Zero;
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
        private void BuildLoadingIndicator()
        {
            var white = (Brush)FindResource("TextDataBrush");
            LoadingIndicator.Children.Clear();

            for (int i = 0; i < 3; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = white,
                    Opacity = 0.2,
                    Margin = new Thickness(5, 0, 5, 0)
                };
                LoadingIndicator.Children.Add(dot);

                var pulse = new DoubleAnimation
                {
                    From = 0.15,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(520),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 170),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                dot.BeginAnimation(UIElement.OpacityProperty, pulse);
            }
        }

        // Window controls
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Release the HID handle before the process goes. Leaving it open on the way
            // out could veto a later CM_Query_And_Remove_SubTree on that device.
            _meterTimer?.Stop();
            _rainbowTimer?.Stop();

            // Si hay un guardado con debounce pendiente, escribirlo YA: cerrar dentro de la
            // ventana de 750 ms no debe perder el ultimo color/player que eligio el usuario.
            if (_intentSave != null && _intentSave.IsEnabled && _lastIntent != null)
            {
                _intentSave.Stop();
                IntentStore.Save(_lastIntent);
            }

            _meter.Dispose();
            Application.Current.Shutdown();
        }

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

        // The driver warning is the only surface that explains a Warn/Error header
        // dot, so it lives in its own status-bar slot instead of the scrolling
        // StatusLogText line. RefreshStatus() is the only writer of this element;
        // RefreshDevicesList()'s "Scanning..." / "Scan completed..." chatter only
        // ever touches StatusLogText, so it can never clobber an unacknowledged
        // warning off the screen the way the old single-line log did.
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

        // Sub-nav del hub "Mando": Configurar el mando (por defecto) | Luces del mando.
        private void ShowConfigPanel(object sender, RoutedEventArgs e)
        {
            ConfigPanel.Visibility = Visibility.Visible;
            LucesPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowLucesPanel(object sender, RoutedEventArgs e)
        {
            ConfigPanel.Visibility = Visibility.Collapsed;
            LucesPanel.Visibility = Visibility.Visible;
            RefreshPlayStationDevices();   // igual que hoy al entrar a la luz
        }

        // Pestanas propias del configurador (Sticks/Gatillos/Touchpad/Botones), mismo
        // patron de visibilidad que ShowConfigPanel/ShowLucesPanel. Los controles reales
        // de cada contenedor (Task 4) editan _remap; esto solo alterna cual se ve.
        private void ShowStickTab(object sender, RoutedEventArgs e)
        {
            TabSticks.Visibility = Visibility.Visible;
            TabGatillos.Visibility = Visibility.Collapsed;
            TabTouchpad.Visibility = Visibility.Collapsed;
            TabBotones.Visibility = Visibility.Collapsed;
        }

        private void ShowGatilloTab(object sender, RoutedEventArgs e)
        {
            TabSticks.Visibility = Visibility.Collapsed;
            TabGatillos.Visibility = Visibility.Visible;
            TabTouchpad.Visibility = Visibility.Collapsed;
            TabBotones.Visibility = Visibility.Collapsed;
        }

        private void ShowTouchpadTab(object sender, RoutedEventArgs e)
        {
            TabSticks.Visibility = Visibility.Collapsed;
            TabGatillos.Visibility = Visibility.Collapsed;
            TabTouchpad.Visibility = Visibility.Visible;
            TabBotones.Visibility = Visibility.Collapsed;
        }

        private void ShowBotonTab(object sender, RoutedEventArgs e)
        {
            TabSticks.Visibility = Visibility.Collapsed;
            TabGatillos.Visibility = Visibility.Collapsed;
            TabTouchpad.Visibility = Visibility.Collapsed;
            TabBotones.Visibility = Visibility.Visible;
        }

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

        private async void MasterToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_engineRunning) await StopEngine();
            else await StartEngine();
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
            MasterToggleBtn.IsEnabled = false;
            MasterStatusText.Text = "Aplicando...";

            _padVirtual = new VirtualPad();
            _padReader = new DualSenseReader();
            _padHidHide = new HidHideControl();
            // Environment.ProcessPath es la ruta real del exe incluso en el publish de un
            // solo archivo (Assembly.Location ahi devuelve cadena vacia - IL3000 -, y una
            // whitelist vacia en HidHide nos ocultaria el mando a nosotros mismos).
            string exe = Environment.ProcessPath
                         ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                         ?? "";

            _engineBusy = true;
            var result = await Task.Run(() => StartEngineDevices(exe));
            _engineBusy = false;

            if (!result.Success)
            {
                MasterStatusText.Text = result.FailedStage == "virtual"
                    ? "Error creando el DS4 virtual: " + result.Error
                    : "Error leyendo el DualSense: " + result.Error;
                CleanupEngine();
                MasterToggleBtn.IsEnabled = true;
                return;
            }

            _hideError = result.HideError;
            _engineRunning = true;
            _engineTick = 0;
            MasterToggleBtn.Content = "VOLVER AL MANDO NATIVO";

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
            MasterToggleBtn.IsEnabled = true;
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

        private void UpdateEngineStatus()
        {
            if (_padReader == null || _padVirtual == null || _padHidHide == null) return;
            string fisico = _padHidHide.IsHiding ? "fisico OCULTO" : "fisico visible";
            string virt = _padVirtual.Connected ? "virtual ACTIVO" : "virtual inactivo";
            string reportes = $"{_padReader.ReportsRead} reportes leidos";
            string extra = _hideError == null ? "" : $"  (HidHide no oculto: {_hideError})";
            MasterStatusText.Text = $"MANDO VIRTUAL ACTIVO - {fisico} / {virt} / {reportes}{extra}";
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
            MasterToggleBtn.IsEnabled = false;
            MasterStatusText.Text = "Deteniendo...";

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

            MasterToggleBtn.Content = "ACTIVAR MANDO VIRTUAL";
            MasterStatusText.Text = revertErr == null
                ? "MANDO NATIVO - el juego ve tu DualSense fisico, sin transformar."
                : $"MANDO NATIVO (revert parcial: {revertErr}). Revisa joy.cpl.";
            CleanupEngine();
            MasterToggleBtn.IsEnabled = true;
        }

        private void CleanupEngine()
        {
            _padReader = null;
            _padVirtual = null;
            _padHidHide = null;
            _hideError = null;
            _engineRunning = false;
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
        private readonly List<(PadButton Source, ComboBox Combo)> _buttonRemapRows = new();
        private readonly List<(TouchZone Zone, ComboBox Combo)> _touchZoneRows = new();

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

        // Botones de origen remapeables (excluye PS y el click del touchpad, que no son
        // fuente de remapeo aqui).
        private static readonly (string Label, PadButton Value)[] RemappableButtons =
        {
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
        };

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

            BuildButtonRemapRows();
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

            RefreshRemapProfileList();
            CheckEngineDrivers();
        }

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
            MasterStatusText.Text = $"Falta instalar {faltan} (drivers de Nefarius). Sin eso no hay " +
                                    "mando virtual; el juego sigue viendo tu DualSense nativo.";
        }

        private void BuildButtonRemapRows()
        {
            BotonRows.Children.Clear();
            _buttonRemapRows.Clear();

            foreach (var (label, source) in RemappableButtons)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    Style = (Style)FindResource("FieldLabel"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 140
                });

                var combo = new ComboBox { Width = 170, Tag = source };
                foreach (var (targetLabel, targetValue) in RemapTargets)
                    combo.Items.Add(new ComboBoxItem { Content = targetLabel, Tag = targetValue });
                combo.SelectionChanged += ButtonRemapCombo_Changed;
                row.Children.Add(combo);

                BotonRows.Children.Add(row);
                _buttonRemapRows.Add((source, combo));
            }
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

            foreach (var (source, combo) in _buttonRemapRows)
                SelectComboByTag(combo, _remap.ButtonRemap.TryGetValue(source, out var target) ? target : source);

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

        // Null-guarded like UpdateSwatch/UpdateRainbowHint/UpdatePlayerSpeedText: a Slider
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

        private void L2Point_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateTriggerText();
            if (_updatingRemap) return;
            _remap.L2PointPct = (int)Math.Round(L2PointSlider.Value);
            RememberRemap();
        }

        private void R2Point_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
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

        private void ButtonRemapCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingRemap) return;
            if (sender is not ComboBox { Tag: PadButton source, SelectedItem: ComboBoxItem item }) return;

            var target = (PadButton)item.Tag;
            if (target == source) _remap.ButtonRemap.Remove(source);   // identidad: no se guarda
            else _remap.ButtonRemap[source] = target;

            RememberRemap();
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

        private void RefreshRemapProfileList()
        {
            RemapProfileList.ItemsSource = null;
            var visible = _remapProfiles.Where(p => p.Name != LastUsedProfileName).ToList();
            RemapProfileList.ItemsSource = visible;
            // Igual que LoadProfiles() (luz): sin esto, CARGAR/BORRAR justo despues de GUARDAR
            // no encuentran nada seleccionado y no hacen nada, en silencio.
            if (visible.Count > 0) RemapProfileList.SelectedIndex = 0;
        }

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

        private void SaveRemapProfile_Click(object sender, RoutedEventArgs e)
        {
            string name = RemapProfileName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                LogStatus("Ponle un nombre al perfil del remapeo primero.");
                return;
            }
            if (string.Equals(name, LastUsedProfileName, StringComparison.OrdinalIgnoreCase))
            {
                LogStatus("Ese nombre esta reservado. Elige otro.");
                return;
            }

            _remapProfiles.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            _remapProfiles.Add(new RemapProfile { Name = name, Settings = CloneRemapSettings(_remap) });

            var result = RemapProfileStore.Save(_remapProfiles);
            if (!result.Success) { ShowError("Perfil no guardado", result.Error!); return; }

            RefreshRemapProfileList();
            LogStatus($"Perfil de remapeo '{name}' guardado.");

            // Tambien al instante como "ultimo usado": si el guardado llega a menos de 750 ms
            // de la ultima edicion, no depende de que el debounce alcance a disparar solo.
            PersistLastUsedRemap();
        }

        private void LoadRemapProfile_Click(object sender, RoutedEventArgs e)
        {
            if (RemapProfileList.SelectedItem is not RemapProfile p)
            {
                LogStatus("Selecciona un perfil de remapeo.");
                return;
            }

            try
            {
                _updatingRemap = true;
                _remap = CloneRemapSettings(p.Settings);
                _remap.Sanitize();   // perfiles viejos con presets retirados -> Lineal (ver RemapSettings)
                ApplyRemapSettingsToControls();
            }
            finally
            {
                _updatingRemap = false;
            }

            LogStatus($"Perfil de remapeo '{p.Name}' cargado.");
            PersistLastUsedRemap();   // el recien cargado pasa a ser el "ultimo usado"
        }

        private void DeleteRemapProfile_Click(object sender, RoutedEventArgs e)
        {
            if (RemapProfileList.SelectedItem is not RemapProfile p)
            {
                LogStatus("Selecciona un perfil de remapeo.");
                return;
            }

            _remapProfiles.RemoveAll(x => x.Name == p.Name);
            var result = RemapProfileStore.Save(_remapProfiles);
            if (!result.Success) { ShowError("Perfil no borrado", result.Error!); return; }

            RefreshRemapProfileList();
            LogStatus($"Perfil de remapeo '{p.Name}' borrado. Hay una copia en {RemapProfileStore.Path}.backup");
        }

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
        private List<LightProfile> _profiles = new List<LightProfile>();

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

        // Populated once. Tag carries the value so handlers read a real value rather than
        // parsing a label back into meaning.
        private void BuildLightControls()
        {
            if (PlayerLedList.Items.Count > 0) return;

            // Guarded end to end, not just around the two SelectedIndex assignments below:
            // both of them fire LightCombo_Changed, which calls ApplyLightNow() the moment
            // _updatingLight is false. Relying on RefreshPlayStationDevices() happening to
            // call this before PlayStationList gets its ItemsSource (so ApplyLightNow()'s
            // null-model check bails out) is an accident of call order, not a guarantee.
            try
            {
                _updatingLight = true;

                foreach (var (label, value) in new (string, PlayerLeds)[]
                         {
                             ("Apagados", PlayerLeds.Off),
                             ("Player 1", PlayerLeds.Player1),
                             ("Player 2", PlayerLeds.Player2),
                             ("Player 3", PlayerLeds.Player3),
                             ("Player 4", PlayerLeds.Player4),
                             ("Todas", PlayerLeds.All),
                         })
                    PlayerLedList.Items.Add(new ComboBoxItem { Content = label, Tag = value });
                PlayerLedList.SelectedIndex = 1;   // Player 1, what Windows shows

                foreach (var (label, value) in new (string, LedBrightness)[]
                         {
                             ("Alto", LedBrightness.High),
                             ("Medio", LedBrightness.Medium),
                             ("Bajo", LedBrightness.Low),
                         })
                    BrightnessList.Items.Add(new ComboBoxItem { Content = label, Tag = value });
                BrightnessList.SelectedIndex = 0;

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
                    UpdateSwatch();
                    SelectComboByTag(PlayerLedList, saved.Player);
                    SelectComboByTag(BrightnessList, saved.Brightness);
                    SelectComboByTag(RainbowStyleList, saved.Style);
                    RainbowSpeed.Value = Math.Clamp(saved.RainbowColoursPerSecond,
                        (int)RainbowWalker.MinColoursPerSecond, (int)RainbowWalker.MaxColoursPerSecond);
                    SelectComboByTag(PlayerEffectList, saved.PlayerEffect);
                    PlayerSpeed.Value = Math.Clamp(saved.PlayerEffectFps, 2, 20);
                }
                // Con o sin intencion guardada (p.ej. primer arranque), la barra de velocidad y la
                // seleccion fija de Player dependen solo de si hay un efecto de LED activo.
                PlayerSpeed.IsEnabled = PlayerEffectOn;
                PlayerLedList.IsEnabled = !PlayerEffectOn;

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

        // Selecciona en un ComboBox el item cuyo Tag es igual a value (los items se construyen
        // con Tag = el enum). Sin match, deja la seleccion actual.
        private static void SelectComboByTag(ComboBox combo, object value)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (Equals(item.Tag, value)) { combo.SelectedItem = item; return; }
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

            UpdateSwatch();
            ApplyLightNow();   // A preset click is a decision, not a drag - no need to debounce.
        }

        private LightState CurrentLight()
        {
            var c = Picker.SelectedColor;
            return new LightState(c.R, c.G, c.B,
                (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag,
                (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag);
        }

        // A combo box is a discrete choice, not a drag: apply it immediately.
        private void LightCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingLight || ColourSwatch == null) return;
            ApplyLightNow();
        }

        private void LightDebounce_Tick(object? sender, EventArgs e)
        {
            _lightDebounce?.Stop();
            ApplyLightNow();
        }

        private void UpdateSwatch()
        {
            if (ColourSwatch == null) return;
            var c = Picker.SelectedColor;
            ColourSwatch.Background = new SolidColorBrush(c);
            HexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private void ApplyLightNow()
        {
            // Any direct apply cancels a pending debounced one. Without this, dragging a
            // slider (which starts the 50 ms timer) and then clicking a preset or combo
            // within that window applies immediately (correct) and then again ~50 ms later
            // when the stale timer fires - a redundant duplicate write.
            _lightDebounce?.Stop();

            if (PlayStationList.SelectedItem is not UsbDeviceModel model) return;
            if (PlayerLedList.SelectedItem == null || BrightnessList.SelectedItem == null) return;

            var result = DualSenseLight.Apply(model.InstanceId, CurrentLight());
            if (!result.Success) LogStatus($"No se pudo cambiar la luz: {result.Error}");
            RememberLight();
        }

        // Construye la intencion actual (color fijo o rainbow) y agenda su guardado.
        private void RememberLight()
        {
            if (_updatingLight) return;                 // no persistir cambios programaticos
            if (PlayerLedList.SelectedItem == null || BrightnessList.SelectedItem == null) return;

            var player = (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag;
            var brightness = (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag;

            LightIntent intent;
            if (RainbowOn)
            {
                if (RainbowStyleList.SelectedItem == null) return;
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
                PlayerLedList.SelectedIndex = 1;   // Player 1
                BrightnessList.SelectedIndex = 0;  // High
            }
            finally
            {
                _updatingLight = false;
            }

            // Outside the _updatingLight guard so PlayerEffect_Changed actually runs: it stops
            // the effect timer (via UpdateEffectDriver), re-enables PlayerLedList, and - now
            // that nothing is animating - re-applies the restored static colour/player itself.
            PlayerEffectList.SelectedIndex = 0;   // Ninguno

            UpdateSwatch();
            ApplyLightNow();
            LogStatus("Luz restaurada: azul, Player 1.");
            RememberLight();
        }

        // Only PlayStation controllers reach this page. The rest of the app is vendor-neutral;
        // this report layout is Sony's alone.
        private void RefreshPlayStationDevices()
        {
            BuildLightControls();
            var ps = _allDevices.Where(DualSenseLight.IsPlayStation).ToList();

            // Con HidHide ocultando el fisico, el escaneo (PowerShell, proceso externo sin
            // whitelist) no lo ve, pero NOSOTROS si: se resuelve en-proceso y se inyecta una
            // entrada sintetica para que la pagina de luces siga funcionando con el motor activo.
            if (ps.Count == 0)
            {
                var hiddenId = HidHideControl.FindPhysicalGamepadInstanceId();
                if (hiddenId != null)
                    ps.Add(new UsbDeviceModel
                    {
                        Name = "DualSense (oculto por HidHide)",
                        InstanceId = hiddenId,
                        Status = "OK",
                        Class = "HIDClass",
                    });
            }

            PlayStationList.ItemsSource = ps;
            if (ps.Count > 0 && PlayStationList.SelectedItem == null) PlayStationList.SelectedIndex = 0;

            LightEmptyState.Visibility = ps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LightPanel.Visibility = ps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateSwatch();
            LoadProfiles();
        }

        // Selecting a different controller must not write to it. The user has not asked for a
        // colour on this device yet.
        private void PlayStationList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSwatch();

        private void Picker_ColorChanged(object? sender, EventArgs e)
        {
            if (_updatingLight) return;

            // Touching a colour ends the effect: while the rainbow owns the colour, a picked one
            // would be overwritten within one tick (15.6-187.5 ms). The last thing you touched wins.
            if (RainbowCheck.IsChecked == true) RainbowCheck.IsChecked = false;

            UpdateSwatch();

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
        private void UpdateRainbowHint()
        {
            if (RainbowStyleHint == null) return;
            RainbowStyleHint.Text = CurrentRainbowStyle switch
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
            if (PlayStationList.SelectedItem is not UsbDeviceModel model) return;
            if (PlayerLedList.SelectedItem == null || BrightnessList.SelectedItem == null) return;

            byte r, g, b;
            if (RainbowOn)
            {
                _rainbowWalker ??= new RainbowWalker(CurrentRainbowStyle);
                (r, g, b) = _rainbowWalker.Advance(RainbowWalker.SpeedPlan(TargetColoursPerSecond).coloursPerTick);
                _updatingLight = true;
                try { Picker.SelectedColor = System.Windows.Media.Color.FromRgb(r, g, b); UpdateSwatch(); }
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
                player = (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag;
            }

            var brightness = (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag;
            DualSenseLight.Apply(model.InstanceId, new LightState(r, g, b, player, brightness));
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
            if (PlayerLedList != null) PlayerLedList.IsEnabled = !PlayerEffectOn;
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

        // Both numbers are what the timer really delivers, not what was requested - the whole
        // point of counting in ticks. A label that overpromises is the defect this fixes.
        private void UpdateRainbowSpeedText()
        {
            if (RainbowSpeedText == null || RainbowSpeed == null) return;

            var walker = new RainbowWalker(CurrentRainbowStyle);
            double actual = RainbowWalker.ActualColoursPerSecond(TargetColoursPerSecond);
            string suffix = RainbowWalker.ShowsEveryColour(TargetColoursPerSecond) ? "" : " · varios colores/cuadro";
            RainbowSpeedText.Text = $"{actual:0.#}/s · vuelta {walker.CycleSeconds(TargetColoursPerSecond):0.#} s{suffix}";
        }

        private void LoadProfiles()
        {
            _profiles = ProfileStore.Load();
            ProfileList.ItemsSource = null;
            ProfileList.ItemsSource = _profiles;
            if (_profiles.Count > 0) ProfileList.SelectedIndex = 0;
        }

        private void SaveProfile_Click(object sender, RoutedEventArgs e)
        {
            string name = ProfileName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                LogStatus("Ponle un nombre al perfil primero.");
                return;
            }

            // SetDeviceRate (via ApplyProfile_Click) expects the raw slot that the rate
            // combo's Tag carries - the same value ApplyRate writes - not ResolvedRate's
            // display value. On a Low/Full Speed device under a patched driver those
            // differ: slot 31 resolves to 2000 Hz for the user's eyes, but
            // TryMapRateToBInterval(2000, Full) is null, because 2000 only exists via
            // the smuggled slot. Saving ResolvedRate would make the profile unappliable.
            int? capturedRate = null;
            bool wantsRate = ProfileIncludesRate.IsChecked == true;
            if (wantsRate)
            {
                var rateSource = DevicesListBox.SelectedItem as UsbDeviceModel;
                if (rateSource?.SelectedRate == null)
                {
                    LogStatus("No se pudo incluir la tasa: selecciona un dispositivo en Dispositivos con una tasa establecida. Perfil guardado sin tasa.");
                }
                else
                {
                    capturedRate = rateSource.SelectedRate;
                }
            }

            var c = Picker.SelectedColor;
            var p = new LightProfile
            {
                Name = name,
                R = c.R, G = c.G, B = c.B,
                Player = (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag,
                Brightness = (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag,
                Rainbow = RainbowCheck.IsChecked == true,
                Rate = capturedRate
            };

            // Same name replaces, rather than silently accumulating duplicates the user cannot
            // tell apart in the list.
            _profiles.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            _profiles.Add(p);

            var result = ProfileStore.Save(_profiles);
            if (!result.Success) { ShowError("Perfil no guardado", result.Error!); return; }

            LoadProfiles();
            LogStatus($"Perfil '{name}' guardado.");
        }

        private void ApplyProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileList.SelectedItem is not LightProfile p) { LogStatus("Selecciona un perfil."); return; }
            if (PlayStationList.SelectedItem is not UsbDeviceModel model) { LogStatus("Selecciona un mando."); return; }

            _updatingLight = true;
            try
            {
                Picker.SelectedColor = Color.FromRgb(p.R, p.G, p.B);
                foreach (ComboBoxItem i in PlayerLedList.Items)
                    if ((PlayerLeds)i.Tag == p.Player) { PlayerLedList.SelectedItem = i; break; }
                foreach (ComboBoxItem i in BrightnessList.Items)
                    if ((LedBrightness)i.Tag == p.Brightness) { BrightnessList.SelectedItem = i; break; }
                RainbowCheck.IsChecked = p.Rainbow;
            }
            finally { _updatingLight = false; }

            UpdateSwatch();
            if (!p.Rainbow) ApplyLightNow();
            else Rainbow_Toggled(sender, e);

            if (p.Rate == null) { LogStatus($"Perfil '{p.Name}' aplicado."); return; }

            // The rate goes to the device selected on the Dashboard, which is where rates live.
            // Applying it here would otherwise silently target the wrong device.
            if (DevicesListBox.SelectedItem is not UsbDeviceModel target)
            {
                LogStatus($"Perfil '{p.Name}' aplicado (luz). Selecciona un dispositivo en Dispositivos para su tasa.");
                return;
            }

            var rateResult = SystemManager.SetDeviceRate(target.InstanceId, target.DriverKey, p.Rate.Value, target.BusSpeed);
            LogStatus(rateResult.Success
                // Deliberately not auto-replugging: yanking the user's controller off the bus
                // because they clicked a profile would be a hostile surprise. The detail panel
                // already shows measured-vs-requested and says to press RECONECTAR.
                ? $"Perfil '{p.Name}' aplicado. Tasa {p.Rate} Hz escrita: pulsa RECONECTAR para que surta efecto."
                : $"Perfil '{p.Name}': luz aplicada, pero la tasa fallo: {rateResult.Error}");
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileList.SelectedItem is not LightProfile p) { LogStatus("Selecciona un perfil."); return; }

            _profiles.RemoveAll(x => x.Name == p.Name);
            var result = ProfileStore.Save(_profiles);
            if (!result.Success) { ShowError("Perfil no borrado", result.Error!); return; }

            LoadProfiles();
            LogStatus($"Perfil '{p.Name}' borrado. Hay una copia en {ProfileStore.Path}.backup");
        }

        // Scan and populate the device list
        private bool _intentReapplied;

        // Reaplica la ultima intencion de luz al primer DualSense presente. Se llama una
        // vez, desde el fin del escaneo (cuando _allDevices ya esta poblada).
        private void ReapplyIntent()
        {
            if (_intentReapplied) return;
            var intent = IntentStore.Load();
            if (intent == null) { _intentReapplied = true; return; }

            var pad = _allDevices.FirstOrDefault(DualSenseLight.IsPlayStation);
            if (pad == null)
            {
                var hiddenId = HidHideControl.FindPhysicalGamepadInstanceId();
                if (hiddenId != null)
                    pad = new UsbDeviceModel { Name = "DualSense (oculto)", InstanceId = hiddenId, Status = "OK", Class = "HIDClass" };
            }
            if (pad == null) return;   // sin mando aun; se reintenta al reconectar (Task B4)
            _intentReapplied = true;

            if (intent.Kind == LightIntentKind.Static)
            {
                DualSenseLight.Apply(pad.InstanceId, intent.ToLightState());
            }
            else
            {
                DualSenseLight.Apply(pad.InstanceId, intent.ToLightState()); // color base + LEDs
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

            // Shown for the duration of a real wait: ScanDevices is a PowerShell round
            // trip of roughly a second. Not staged busywork.
            LoadingIndicator.Visibility = Visibility.Visible;

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
                        LoadingIndicator.Visibility = Visibility.Collapsed;
                        LogStatus($"Scan failed: {ex.Message}");
                    });
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    LoadingIndicator.Visibility = Visibility.Collapsed;
                    _allDevices = devices;
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
            DeviceCountText.Text = filtered.Count.ToString();
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
                return;
            }

            double hz = sample.Value.MedianHz;
            MeasuredText.Text = $"{hz:0.#} Hz";
            MeasuredText.Foreground = (Brush)FindResource("TextDataBrush");
            MeasuredGapText.Text = $"{sample.Value.MedianGapMs:0.###} ms";

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
                LogStatus("Elige una TASA OBJETIVO.");
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

                var filter = SystemManager.SetFilterActive(model.InstanceId, false);
                if (!filter.Success) { LogStatus($"No se pudo quitar el filtro: {filter.Error}"); return; }

                var replug = await SystemManager.ReplugDevice(model.InstanceId);
                if (!replug.Success) { LogStatus($"Reconexion fallida: {replug.Error}"); return; }
                LogStatus($"{model.Name} restablecido a su estado por defecto.");
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

        // Helper to output to the console log at the bottom
        private void LogStatus(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            StatusLogText.Text = $"[{timestamp}] {message}";
        }
    }
}
