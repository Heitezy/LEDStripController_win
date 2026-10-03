using System.Collections.ObjectModel;
using LEDStripController.Audio;
using LEDStripController.Ble;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;

namespace LEDStripController;

public sealed partial class MainWindow : Window
{
    readonly LightEngine _engine = new();
    readonly AppSettings _settings = AppSettings.Load();
    readonly ObservableCollection<DiscoveredDevice> _devices = new();
    readonly DispatcherQueueTimer _colorTimer;
    readonly DispatcherQueueTimer _brightnessTimer;

    Color _pickedColor = Colors.White;
    bool _suppress = true;     // true while the UI is being built / synced programmatically
    bool _connecting;

    public MainWindow()
    {
        InitializeComponent();
        Title = "ELK-BLEDOM & BJ_LED Controller";
        AppWindow.Resize(new SizeInt32(720, 980));

        // Throttled senders: fire at most every 80 ms while dragging, always send the final value.
        _colorTimer = DispatcherQueue.CreateTimer();
        _colorTimer.Interval = TimeSpan.FromMilliseconds(80);
        _colorTimer.IsRepeating = false;
        _colorTimer.Tick += (_, _) => _engine.SetSolid(ToRgb(_pickedColor), dropIfBusy: false);

        _brightnessTimer = DispatcherQueue.CreateTimer();
        _brightnessTimer.Interval = TimeSpan.FromMilliseconds(80);
        _brightnessTimer.IsRepeating = false;
        _brightnessTimer.Tick += (_, _) => _ = _engine.SetBrightnessAsync((int)BrightnessSlider.Value);

        DevicesList.ItemsSource = _devices;
        PopulatePatterns();
        PopulatePresets();
        PopulateBandCombo(BassCombo, 1);    // Red
        PopulateBandCombo(MidCombo, 4);     // Green
        PopulateBandCombo(HighCombo, 6);    // Blue
        ShowColor(Colors.White);

        SmoothSwitch.IsOn = _settings.AmbilightSmooth;
        _engine.AmbilightSmooth = _settings.AmbilightSmooth;

        _engine.Error += msg => RunUI(() => { ShowError(msg); SyncTogglesFromEngine(); });
        _engine.LevelsUpdated += l => RunUI(() =>
        {
            BassBar.Value = l.Bass * 100;
            MidBar.Value = l.Mid * 100;
            HighBar.Value = l.High * 100;
        });

        var ble = _engine.Led.Ble;
        ble.DeviceFound += d => RunUI(() => OnDeviceFound(d));
        ble.ScanError += msg => RunUI(() => { ShowError(msg); ScanButton.Content = "Scan"; });
        ble.ConnectionChanged += _ => RunUI(UpdateStatus);

        // "onResume": whenever the window is activated, reconnect to the saved strip without scanning
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                _ = TryAutoReconnectAsync();
        };
        Closed += (_, _) => _engine.Dispose();

        _suppress = false;
        UpdateStatus();
    }

    // ---------- helpers ----------
    void RunUI(Action a) => DispatcherQueue.TryEnqueue(() => a());

    static Rgb ToRgb(Color c) => new(c.R, c.G, c.B);
    static Color ToColor(Rgb c) => Color.FromArgb(255, c.R, c.G, c.B);

    void ShowError(string msg)
    {
        ErrorBar.Message = msg;
        ErrorBar.IsOpen = true;
    }

    void ShowColor(Color c)
    {
        ColorSwatch.Background = new SolidColorBrush(c);
        HexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        HighlightPreset(c);
    }

    void SyncTogglesFromEngine()
    {
        _suppress = true;
        MusicSwitch.IsOn = _engine.Sync == SyncMode.Music;
        ScreenSwitch.IsOn = _engine.Sync == SyncMode.Screen;
        _suppress = false;
        if (_engine.Sync != SyncMode.Music) BassBar.Value = MidBar.Value = HighBar.Value = 0;
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null) yield break;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    void SetControlsEnabled(bool enabled)
    {
        foreach (var control in FindVisualChildren<Control>(ControlsPanel))
            control.IsEnabled = enabled;
    }

    void UpdateStatus()
    {
        var ble = _engine.Led.Ble;
        bool connected = ble.IsConnected;
        StatusDot.Fill = new SolidColorBrush(connected ? Colors.LimeGreen : Colors.IndianRed);
        StatusText.Text = _connecting ? "Connecting…"
            : connected ? $"Connected — {ble.DeviceName} ({(ble.Type == StripType.BjLed ? "BJ_LED" : "ELK-BLEDOM")})"
            : "Not connected";
        DisconnectButton.IsEnabled = connected;
        SetControlsEnabled(connected);
    }

    // ---------- population ----------
    void PopulatePatterns()
    {
        foreach (var p in Enum.GetValues<LedPattern>()) PatternCombo.Items.Add(p.DisplayName());
        PatternCombo.SelectedIndex = 0;
    }

    readonly List<(Button Button, float Hue, float Sat)> _presetButtons = new();

    void PopulatePresets()
    {
        foreach (var (name, hue, sat) in Palette.Presets)
        {
            var color = ToColor(ColorMath.FromHsv(hue, sat, 1f));
            var btn = new Button
            {
                Width = 40,
                Height = 40,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(20),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(btn, name);
            float h = hue, s = sat;
            btn.Click += (_, _) =>
            {
                // Like Android: a preset sets hue + saturation and keeps the current colour value.
                var (_, _, v) = ColorMath.ToHsv(_pickedColor.R / 255.0, _pickedColor.G / 255.0, _pickedColor.B / 255.0);
                var rgb = ColorMath.FromHsv(h, s, (float)v);
                var c = ToColor(rgb);
                _suppress = true;
                Picker.Color = c;
                _suppress = false;
                ChooseColor(c);
            };
            _presetButtons.Add((btn, hue, sat));
            PresetGrid.Items.Add(btn);
        }
    }

    void HighlightPreset(Color c)
    {
        var (h, s, _) = ColorMath.ToHsv(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        foreach (var (btn, ph, ps) in _presetButtons)
        {
            bool hueMatch = ps == 0f || Math.Abs(h - ph) < 1.0 || Math.Abs(h - ph) > 359.0;
            btn.BorderThickness = new Thickness(Math.Abs(s - ps) < 0.02 && hueMatch ? 2 : 0);
        }
    }

    void PopulateBandCombo(ComboBox box, int selectedIndex)
    {
        box.Items.Add(new BandOption("Off", null));
        foreach (var (name, rgb) in Palette.BandColors)
            box.Items.Add(new BandOption(name, new SolidColorBrush(ToColor(rgb))));
        box.SelectedIndex = selectedIndex;
        ApplyBandSelection(box);
    }

    void ApplyBandSelection(ComboBox box)
    {
        int i = box.SelectedIndex;
        Rgb? color = i <= 0 ? null : Palette.BandColors[i - 1].Color;
        if (box == BassCombo) _engine.Bass = color;
        else if (box == MidCombo) _engine.Mid = color;
        else if (box == HighCombo) _engine.High = color;
    }

    // ---------- connection ----------
    void OnDeviceFound(DiscoveredDevice d)
    {
        int idx = -1;
        for (int i = 0; i < _devices.Count; i++)
            if (_devices[i].Address == d.Address) { idx = i; break; }

        if (idx >= 0) { _devices[idx] = d; return; }   // existing: update in place (no re-sort)

        // New device: LED-strip names first, then strongest signal (same ordering as the Android app)
        var sorted = _devices.Append(d)
            .OrderByDescending(x => x.IsLikelyLedStrip)
            .ThenByDescending(x => x.Rssi)
            .ToList();
        _devices.Clear();
        foreach (var x in sorted) _devices.Add(x);
    }

    void Scan_Click(object sender, RoutedEventArgs e)
    {
        var ble = _engine.Led.Ble;
        if (ble.IsScanning)
        {
            ble.StopScan();
            ScanButton.Content = "Scan";
            return;
        }
        ErrorBar.IsOpen = false;
        _devices.Clear();
        ble.StartScan();
        ScanButton.Content = "Stop";
    }

    async void Devices_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DiscoveredDevice d) await ConnectAsync(d.Address, userInitiated: true);
    }

    async Task TryAutoReconnectAsync()
    {
        if (_connecting || _engine.Led.Ble.IsConnected) return;
        if (_settings.LastAddress is { } addr) await ConnectAsync(addr, userInitiated: false);
    }

    async Task ConnectAsync(ulong address, bool userInitiated)
    {
        if (_connecting) return;
        _connecting = true;
        _engine.Led.Ble.StopScan();
        ScanButton.Content = "Scan";
        UpdateStatus();

        bool ok = await _engine.Led.Ble.ConnectAsync(address);
        _connecting = false;

        if (ok)
        {
            _settings.LastAddress = address;
            _settings.Save();
            ErrorBar.IsOpen = false;
            await _engine.OnConnectedAsync();
        }
        else if (userInitiated)
        {
            ShowError("Could not connect. Make sure the strip is powered on, in range, and not connected to another device (e.g. your phone).");
        }
        UpdateStatus();
    }

    void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _engine.Disconnect();
        _settings.LastAddress = null;      // explicit disconnect => no auto-reconnect
        _settings.Save();
        _suppress = true;
        MusicSwitch.IsOn = false;
        ScreenSwitch.IsOn = false;
        _suppress = false;
        UpdateStatus();
    }

    // ---------- power / brightness ----------
    async void Power_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        await _engine.SetPowerAsync(PowerSwitch.IsOn);
    }

    void Brightness_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;
        if (!_brightnessTimer.IsRunning) _brightnessTimer.Start();
    }

    // ---------- colour ----------
    void Picker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppress) return;
        ChooseColor(args.NewColor);
    }

    /// <summary>
    /// Same as Android: the colour is always stored, but only shown while the pattern is Solid
    /// and no sync is running.
    /// </summary>
    void ChooseColor(Color c)
    {
        _pickedColor = c;
        ShowColor(c);
        _engine.SetSolid(ToRgb(c), dropIfBusy: true);
        if (!_colorTimer.IsRunning) _colorTimer.Start();   // guarantees the final colour is delivered
    }

    // ---------- pattern ----------
    void Pattern_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || PatternCombo.SelectedIndex < 0) return;
        var p = (LedPattern)PatternCombo.SelectedIndex;
        DelayBox.Visibility = p == LedPattern.Solid ? Visibility.Collapsed : Visibility.Visible;
        _engine.SetPattern(p);      // also stops any sync
        SyncTogglesFromEngine();
    }

    void Delay_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) return;
        _engine.DelayMs = (int)args.NewValue;
    }

    // ---------- music sync ----------
    void Music_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (MusicSwitch.IsOn)
        {
            _suppress = true;
            ScreenSwitch.IsOn = false;
            _suppress = false;
            _engine.SetSync(SyncMode.Music);
        }
        else
        {
            _engine.SetSync(SyncMode.None);
            BassBar.Value = MidBar.Value = HighBar.Value = 0;
        }
        SyncTogglesFromEngine();
    }

    void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _engine.SetSource(SourceCombo.SelectedIndex == 1 ? AudioSource.SystemAudio : AudioSource.Microphone);
        SyncTogglesFromEngine();
    }

    void Band_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box) ApplyBandSelection(box);
    }

    // ---------- screen sync ----------
    void Screen_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (ScreenSwitch.IsOn)
        {
            _suppress = true;
            MusicSwitch.IsOn = false;
            _suppress = false;
            BassBar.Value = MidBar.Value = HighBar.Value = 0;
            _engine.SetSync(SyncMode.Screen);
        }
        else
        {
            _engine.SetSync(SyncMode.None);
        }
        SyncTogglesFromEngine();
    }

    // ---------- settings ----------
    void Smooth_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _engine.AmbilightSmooth = SmoothSwitch.IsOn;
        _settings.AmbilightSmooth = SmoothSwitch.IsOn;
        _settings.Save();
    }
}

public sealed record BandOption(string Name, Brush? Swatch)
{
    public Visibility SwatchVisibility => Swatch == null ? Visibility.Collapsed : Visibility.Visible;
}
