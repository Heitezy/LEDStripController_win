using LEDStripController.Audio;
using LEDStripController.Ble;
using LEDStripController.Screen;

namespace LEDStripController;

/// <summary>
/// Owns what the strip is doing: static colour, software-animated pattern, music sync or screen sync.
/// Ported from MainViewModel.kt — smoothing is applied per incoming analyzer frame, exactly as there.
/// </summary>
public sealed class LightEngine : IDisposable
{
    readonly object _gate = new();
    CancellationTokenSource? _cts;
    AudioAnalyzer? _audio;
    ScreenAnalyzer? _screen;
    int _delayMs = 200;
    bool _power = true;

    // smoothing state
    float _mr, _mg, _mb;      // music
    float _sr, _sg, _sb;      // screen

    public LedController Led { get; } = new();

    public event Action<string>? Error;
    public event Action<BandLevels>? LevelsUpdated;

    public SyncMode Sync { get; private set; } = SyncMode.None;
    public LedPattern Pattern { get; private set; } = LedPattern.Solid;
    public Rgb Solid { get; private set; } = Rgb.White;
    public AudioSource Source { get; private set; } = AudioSource.Microphone;

    public volatile bool AmbilightSmooth = false;     // Android default: off
    public Rgb? Bass = new Rgb(255, 0, 0);
    public Rgb? Mid = new Rgb(0, 255, 0);
    public Rgb? High = new Rgb(0, 0, 255);

    public int DelayMs
    {
        get => Volatile.Read(ref _delayMs);
        set => Volatile.Write(ref _delayMs, Math.Clamp(value, 10, 5000));
    }

    public LightEngine()
    {
        Led.Ble.ConnectionChanged += connected =>
        {
            if (!connected) Cancel();
            else _ = Task.Delay(300).ContinueWith(_ => Apply());   // strip came back by itself
        };
    }

    // ---------- public commands ----------
    public async Task OnConnectedAsync()
    {
        await Led.SetPowerAsync(_power);
        await Task.Delay(150);
        if (Led.Ble.Type == StripType.ElkBledom) await Led.SetBrightnessAsync(Led.Brightness);
        Apply();
    }

    public async Task SetPowerAsync(bool on)
    {
        _power = on;
        Cancel();
        await Led.SetPowerAsync(on);
        if (on)
        {
            await Task.Delay(120);
            Apply();
        }
    }

    public Task SetBrightnessAsync(int percent) => Led.SetBrightnessAsync(percent);

    /// <summary>Stores the colour; it is only shown in Solid mode with no sync active (same as Android).</summary>
    public void SetSolid(Rgb c, bool dropIfBusy)
    {
        Solid = c;
        if (Sync == SyncMode.None && Pattern == LedPattern.Solid && _power && Led.Ble.IsConnected)
            _ = Led.SetColorAsync(c, dropIfBusy);
    }

    public void SetPattern(LedPattern p) { Pattern = p; Sync = SyncMode.None; Apply(); }

    public void SetSync(SyncMode mode) { Sync = mode; Apply(); }

    public void SetSource(AudioSource s)
    {
        Source = s;
        if (Sync == SyncMode.Music) Apply();
    }

    public void Disconnect()
    {
        Cancel();
        Led.Ble.Disconnect();
    }

    // ---------- internals ----------
    void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _audio?.Dispose(); _audio = null;
            _screen?.Dispose(); _screen = null;
        }
    }

    void Apply()
    {
        lock (_gate)
        {
            Cancel();
            if (!_power || !Led.Ble.IsConnected) return;

            try
            {
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                switch (Sync)
                {
                    case SyncMode.None when Pattern == LedPattern.Solid:
                        _ = Led.SetColorAsync(Solid, false);
                        break;

                    case SyncMode.None:
                        var pattern = Pattern;
                        _ = Task.Run(() => PatternLoop(pattern, ct), ct);
                        break;

                    case SyncMode.Music:
                        _mr = _mg = _mb = 0;
                        _audio = new AudioAnalyzer();
                        _audio.Levels += OnLevels;
                        _audio.Start(Source);
                        break;

                    case SyncMode.Screen:
                        _sr = _sg = _sb = 0;
                        _screen = new ScreenAnalyzer();
                        _screen.ColorReady += OnScreenColor;
                        _screen.Start();
                        break;
                }
            }
            catch (Exception ex)
            {
                var failed = Sync;
                Cancel();
                Sync = SyncMode.None;
                Error?.Invoke(failed == SyncMode.Music
                    ? $"Could not start audio capture: {ex.Message}"
                    : $"Could not start sync: {ex.Message}");
            }
        }
    }

    async Task PatternLoop(LedPattern pattern, CancellationToken ct)
    {
        try
        {
            foreach (var frame in PatternEngine.Frames(pattern))
            {
                ct.ThrowIfCancellationRequested();
                await Led.SetColorAsync(frame, false);
                await Task.Delay(DelayMs, ct);   // read every frame -> speed changes apply immediately
            }
        }
        catch (OperationCanceledException) { }
    }

    static (int R, int G, int B) Contrib(double level, Rgb? c) =>
        c is { } col ? ((int)(col.R * level), (int)(col.G * level), (int)(col.B * level)) : (0, 0, 0);

    void OnLevels(BandLevels l)
    {
        LevelsUpdated?.Invoke(l);

        var (br, bg, bb) = Contrib(l.Bass, Bass);
        var (mr, mg, mb) = Contrib(l.Mid, Mid);
        var (hr, hg, hb) = Contrib(l.High, High);
        float tr = Math.Clamp(br + mr + hr, 0, 255);
        float tg = Math.Clamp(bg + mg + hg, 0, 255);
        float tb = Math.Clamp(bb + mb + hb, 0, 255);

        float alpha = AmbilightSmooth ? 0.2f : 1f;
        _mr += alpha * (tr - _mr);
        _mg += alpha * (tg - _mg);
        _mb += alpha * (tb - _mb);
        _ = Led.SetColorAsync(new Rgb((byte)_mr, (byte)_mg, (byte)_mb), true);
    }

    void OnScreenColor(Rgb c)
    {
        float alpha = AmbilightSmooth ? 0.07f : 0.25f;
        _sr += alpha * (c.R - _sr);
        _sg += alpha * (c.G - _sg);
        _sb += alpha * (c.B - _sb);
        _ = Led.SetColorAsync(new Rgb((byte)Math.Round(_sr), (byte)Math.Round(_sg), (byte)Math.Round(_sb)), true);
    }

    public void Dispose() => Cancel();
}
