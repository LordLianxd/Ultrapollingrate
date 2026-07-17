using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HidusbfModernGui
{
    public partial class MainWindow : Window
    {
        private List<UsbDeviceModel> _allDevices = new List<UsbDeviceModel>();
        private DriverState _driverState = new DriverState();
        private bool _isInitializing = true;

        // Mode used to interpret the 31/62 slots. Falls back to NoPatch so the UI
        // shows literal 31Hz/62Hz rather than claiming an overclock we cannot prove.
        private DriverMode ActiveMode => _driverState.EffectiveMode ?? DriverMode.NoPatch;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            BuildHeaderSpectrum();
            BuildLoadingIndicator();
            RefreshStatus();
            RefreshDevicesList();
            _isInitializing = false;
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
            RefreshPlayStationDevices();
        }

        // Collapses a slider drag into one write. Without it, dragging fires a HID write per
        // pixel of travel - hundreds a second at the device.
        private DispatcherTimer? _lightDebounce;

        // Set while code (not the user) moves a control, so programmatic updates do not write.
        private bool _updatingLight;

        // Drives the rainbow effect: each tick steps the walker exactly one colour, so
        // speed is how often the timer fires, never how far a step jumps.
        private DispatcherTimer? _rainbowTimer;
        private RainbowWalker? _rainbowWalker;
        private List<LightProfile> _profiles = new List<LightProfile>();

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
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: byte[] rgb } || rgb.Length != 3) return;

            try
            {
                _updatingLight = true;

                // A preset click is touching a colour too: the last thing you touched
                // wins, same rule as Picker_ColorChanged. Left unticked, Rainbow_Tick
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
        }

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

            UpdateSwatch();
            ApplyLightNow();
            LogStatus("Luz restaurada: azul, Player 1.");
        }

        // Only PlayStation controllers reach this page. The rest of the app is vendor-neutral;
        // this report layout is Sony's alone.
        private void RefreshPlayStationDevices()
        {
            BuildLightControls();
            var ps = _allDevices.Where(DualSenseLight.IsPlayStation).ToList();

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
            UpdateRainbowSpeedText();
        }

        private void Rainbow_Toggled(object sender, RoutedEventArgs e)
        {
            if (RainbowCheck.IsChecked == true)
            {
                _rainbowWalker = new RainbowWalker(CurrentRainbowStyle);

                // Render priority, not the default Background. Background sits below Render
                // and Input, so UI work starved the tick - and the old time-driven colour
                // answered a late tick by jumping (measured: 1 dropped tick doubled the step,
                // 3 made it 7). The walker cannot jump, but a starved tick still costs speed,
                // so the priority still matters.
                //
                // The interval IS the speed: one colour per tick, so firing more often is the
                // only way to go faster. Apply() measures 1.0 ms against the 15.625 ms floor, so
                // even the fastest stop (n=1, 64 colours/s) costs ~6% of one core.
                _rainbowTimer ??= new DispatcherTimer(DispatcherPriority.Render);
                _rainbowTimer.Interval = RainbowWalker.IntervalFor(TicksPerColour);
                _rainbowTimer.Tick -= Rainbow_Tick;
                _rainbowTimer.Tick += Rainbow_Tick;
                _rainbowTimer.Start();
                LogStatus("Rainbow activo. Se detiene al cerrar la app.");
            }
            else
            {
                _rainbowTimer?.Stop();
            }
        }

        private void Rainbow_Tick(object? sender, EventArgs e)
        {
            if (PlayStationList.SelectedItem is not UsbDeviceModel model) return;

            // Rebuilt lazily because a style change drops it: each style has its own ramp.
            _rainbowWalker ??= new RainbowWalker(CurrentRainbowStyle);

            // No clock, no elapsed time, no arithmetic. One tick, one colour.
            var (r, g, b) = _rainbowWalker.Step();

            // The picker follows the effect so the UI shows what the pad is doing. _updatingLight
            // stops that from bouncing back through Picker_ColorChanged and killing the effect on
            // its first tick.
            _updatingLight = true;
            try
            {
                Picker.SelectedColor = Color.FromRgb(r, g, b);
                UpdateSwatch();
            }
            finally { _updatingLight = false; }

            var state = new LightState(r, g, b,
                (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag,
                (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag);
            DualSenseLight.Apply(model.InstanceId, state);
        }

        private int TicksPerColour => (int)RainbowSpeed.Value;

        // Speed is the tick's period, so a drag has to retune the live timer - there is no
        // longer a speed term inside the tick that would pick the change up on its own.
        private void RainbowSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_rainbowTimer != null)
                _rainbowTimer.Interval = RainbowWalker.IntervalFor(TicksPerColour);
            UpdateRainbowSpeedText();
        }

        // Both numbers are what the timer really delivers, not what was requested - the whole
        // point of counting in ticks. A label that overpromises is the defect this fixes.
        private void UpdateRainbowSpeedText()
        {
            if (RainbowSpeedText == null || RainbowSpeed == null) return;

            var walker = new RainbowWalker(CurrentRainbowStyle);
            RainbowSpeedText.Text = $"{RainbowWalker.ColoursPerSecond(TicksPerColour):0.#}/s · vuelta {walker.CycleSeconds(TicksPerColour):0.#} s";
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

        // Shared filter toggle used by the card button and the context menu
        private void ToggleFilter(UsbDeviceModel model)
        {
            bool active = !model.FilterActive;
            var result = SystemManager.SetFilterActive(model.InstanceId, active);
            if (result.Success)
            {
                model.FilterActive = active;
                LogStatus($"Filter {(active ? "enabled" : "disabled")} on device: {model.Name}. Restart required.");
                RefreshDevicesList();
            }
            else
            {
                ShowError("Filter Failed", result.Error!);
            }
        }

        private async Task RestartOne(UsbDeviceModel model)
        {
            LogStatus($"Restarting device connection: {model.Name}...");
            var result = await SystemManager.RestartDevice(model.InstanceId);

            if (result.Success)
            {
                LogStatus($"Successfully restarted device: {model.Name}");
            }
            else
            {
                LogStatus($"Failed to restart {model.Name}: {result.Error}");
                ShowError("Restart Failed", $"Could not restart '{model.Name}'.\n\n{result.Error}");
            }
        }

        // Single place where a rate is written, so the guard rails apply everywhere.
        private bool ApplyRate(UsbDeviceModel model, int rateValue)
        {
            if (model.SelectedRate == rateValue) return true;

            var result = SystemManager.SetDeviceRate(model.InstanceId, model.DriverKey, rateValue, model.BusSpeed);
            if (result.Success)
            {
                model.SelectedRate = rateValue;
                int? shown = rateValue == 0 ? null : PollingCore.ResolveHighRateSlot(rateValue, ActiveMode, model.BusSpeed) ?? rateValue;
                LogStatus($"Polling rate set to {(shown == null ? "Default" : shown + " Hz")} for device: {model.Name}. Restart required.");
                RefreshDevicesList();
                return true;
            }

            ShowError("Rate Not Applied", result.Error!);
            return false;
        }

        // Scan and populate the device list
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
            if (DevicesListBox.SelectedItem is not UsbDeviceModel model) return;
            if (DetailRateCombo.SelectedItem is not ComboBoxItem item) return;

            int rate = (int)item.Tag;
            if (model.SelectedRate == rate) return;
            if (!ApplyRate(model, rate)) PopulateRateCombo(model);
        }

        // Event: Toggle filter active switch on the detail panel
        private void FilterToggle_Click(object sender, RoutedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is UsbDeviceModel model) ToggleFilter(model);
        }

        // Event: Click Restart button on the detail panel
        private async void RestartDevice_Click(object sender, RoutedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is not UsbDeviceModel model) return;
            if (sender is not Button btn) return;

            btn.IsEnabled = false;
            await RestartOne(model);
            btn.IsEnabled = true;
        }

        // Software replug. Deeper than RestartDevice_Click's PnP restart: it removes
        // the device from the tree and re-enumerates it, which is what pulling the
        // cable does, and is what actually makes a new bInterval take effect.
        private async void ReplugDevice_Click(object sender, RoutedEventArgs e)
        {
            if (DevicesListBox.SelectedItem is not UsbDeviceModel model) return;

            ReplugBtn.IsEnabled = false;
            LogStatus($"Reconectando {model.Name}: quitar del arbol PnP, 2 s, re-enumerar, reiniciar...");

            // The meter holds the device open, and CM_Query_And_Remove_SubTree is VETOED
            // when anything does. Left running, the rate detector would break the button
            // that actually applies the overclock. RefreshDevicesList() below restores
            // the selection, which restarts the meter.
            _meter.Stop();
            _meterTimer?.Stop();

            var result = await SystemManager.ReplugDevice(model.InstanceId);

            if (result.Success)
            {
                LogStatus($"{model.Name} reconectado y reiniciado. La tasa deberia estar aplicada ahora.");
            }
            else
            {
                LogStatus($"Fallo al reconectar {model.Name}: {result.Error}");
                ShowError("Reconexion fallida", result.Error!);
            }

            ReplugBtn.IsEnabled = true;
            RefreshDevicesList();
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
