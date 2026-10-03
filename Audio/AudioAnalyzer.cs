using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LEDStripController.Audio;

public enum AudioSource { Microphone, SystemAudio }

/// <summary>Band energies normalised to 0..1 (like Android's FrequencyData).</summary>
public readonly record struct BandLevels(double Bass, double Mid, double High, bool Beat);

/// <summary>
/// Line-for-line port of AudioAnalyzer.kt: non-overlapping 4096-sample blocks, Hann window,
/// un-normalised radix-2 FFT, bass 60-250 / mid 250-4000 / high 4000-16000 Hz band averages,
/// slow-decay per-band peaks (0.995, start 0.001), beat = (bass*3 + mid*0.5) > 1.4 x rolling average.
/// Microphone = default capture device; SystemAudio = WASAPI loopback (replaces Android playback capture).
/// </summary>
public sealed class AudioAnalyzer : IDisposable
{
    const int FftSize = 4096;
    const float Decay = 0.995f;

    public event Action<BandLevels>? Levels;

    readonly float[] _ring = new float[FftSize];
    readonly float[] _window = new float[FftSize];
    readonly float[] _re = new float[FftSize];
    readonly float[] _im = new float[FftSize];
    readonly Queue<float> _energyHistory = new();
    readonly float[] _peaks = { 0.001f, 0.001f, 0.001f };
    readonly object _lock = new();

    int _write, _pending;
    long _lastData, _lastFrame;
    int _sampleRate = 44100;
    int _bassLo, _bassHi, _midLo, _midHi, _highLo, _highHi;

    IWaveIn? _capture;
    Timer? _watchdog;

    public AudioAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
            _window[i] = 0.5f * (1f - (float)Math.Cos(2f * Math.PI * i / (FftSize - 1)));
    }

    int HzToBin(int hz) => Math.Clamp((int)((long)hz * FftSize / _sampleRate), 1, FftSize / 2 - 1);

    public void Start(AudioSource source)
    {
        _capture = source == AudioSource.SystemAudio ? new WasapiLoopbackCapture() : new WasapiCapture();
        _sampleRate = _capture.WaveFormat.SampleRate;
        _bassLo = HzToBin(60);   _bassHi = HzToBin(250);
        _midLo = HzToBin(250);   _midHi = HzToBin(4000);
        _highLo = HzToBin(4000); _highHi = HzToBin(16000);

        _lastData = _lastFrame = Environment.TickCount64;
        _capture.DataAvailable += OnData;
        _capture.StartRecording();

        // Loopback delivers no packets during silence (Android's AudioRecord returns zeros),
        // so feed silent blocks to let the colours fall back to black.
        _watchdog = new Timer(_ => SilenceTick(), null, 50, 50);
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var wf = _capture!.WaveFormat;
        int channels = wf.Channels, block = wf.BlockAlign;
        int frames = e.BytesRecorded / block;
        bool isFloat = wf.BitsPerSample == 32, is16 = wf.BitsPerSample == 16;
        if (!isFloat && !is16) return;

        BandLevels? result = null;
        lock (_lock)
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int o = f * block + c * (isFloat ? 4 : 2);
                    sum += isFloat ? BitConverter.ToSingle(e.Buffer, o) : BitConverter.ToInt16(e.Buffer, o) / 32768f;
                }
                _ring[_write] = sum / channels;
                _write = (_write + 1) % FftSize;

                if (++_pending >= FftSize)
                {
                    _pending = 0;
                    result = Process(silent: false);   // one analysis per full 4096-sample block
                }
            }
            _lastData = Environment.TickCount64;
        }
        if (result is { } r) { _lastFrame = Environment.TickCount64; Levels?.Invoke(r); }
    }

    void SilenceTick()
    {
        long now = Environment.TickCount64;
        if (now - _lastData < 250 || now - _lastFrame < 90) return;
        BandLevels r;
        lock (_lock) { Array.Clear(_ring); _pending = 0; r = Process(silent: true); }
        _lastFrame = now;
        Levels?.Invoke(r);
    }

    BandLevels Process(bool silent)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _re[i] = silent ? 0f : _ring[(_write + i) % FftSize] * _window[i];
            _im[i] = 0f;
        }
        Fft(_re, _im);

        int half = FftSize / 2;
        var mag = new float[half];
        for (int i = 0; i < half; i++) mag[i] = (float)Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);

        float bassRaw = BandAvg(mag, _bassLo, _bassHi);
        float midRaw = BandAvg(mag, _midLo, _midHi);
        float highRaw = BandAvg(mag, _highLo, _highHi);

        float beatEnergy = bassRaw * 3f + midRaw * 0.5f;
        _energyHistory.Enqueue(beatEnergy);
        if (_energyHistory.Count > 43) _energyHistory.Dequeue();
        float avg = _energyHistory.Average();
        bool isBeat = beatEnergy > avg * 1.4f && beatEnergy > 0.005f;

        _peaks[0] = Math.Max(_peaks[0] * Decay, bassRaw);
        _peaks[1] = Math.Max(_peaks[1] * Decay, midRaw);
        _peaks[2] = Math.Max(_peaks[2] * Decay, highRaw);

        static double Norm(float raw, float peak) => peak > 0 ? Math.Clamp(raw / peak, 0f, 1f) : 0;
        return new BandLevels(Norm(bassRaw, _peaks[0]), Norm(midRaw, _peaks[1]), Norm(highRaw, _peaks[2]), isBeat);
    }

    static float BandAvg(float[] mag, int lo, int hi)
    {
        float sum = 0;
        int end = Math.Min(hi, mag.Length - 1);
        for (int i = lo; i <= end; i++) sum += mag[i];
        return sum / (hi - lo + 1);
    }

    // Cooley-Tukey in-place radix-2 FFT — identical to the Kotlin version (no 1/N scaling).
    static void Fft(float[] real, float[] imag)
    {
        int n = real.Length;
        int j = 0;
        for (int i = 1; i < n; i++)
        {
            int bit = n >>> 1;
            while ((j & bit) != 0) { j ^= bit; bit >>>= 1; }
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >>> 1;
            double ang = -2.0 * Math.PI / len;
            float wR = (float)Math.Cos(ang), wI = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float curR = 1f, curI = 0f;
                for (int k = 0; k < half; k++)
                {
                    float eR = real[i + k], eI = imag[i + k];
                    float oR = real[i + k + half] * curR - imag[i + k + half] * curI;
                    float oI = real[i + k + half] * curI + imag[i + k + half] * curR;
                    real[i + k] = eR + oR; imag[i + k] = eI + oI;
                    real[i + k + half] = eR - oR; imag[i + k + half] = eI - oI;
                    float nr = curR * wR - curI * wI;
                    curI = curR * wI + curI * wR;
                    curR = nr;
                }
            }
        }
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        if (_capture != null)
        {
            _capture.DataAvailable -= OnData;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
    }
}
