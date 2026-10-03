using System.Runtime.InteropServices;

namespace LEDStripController.Screen;

/// <summary>
/// Grabs the primary screen via GDI at 160 px wide / 20 fps, then finds a "dominant" colour:
/// linear-light conversion -> 24-bin hue histogram (circularly smoothed) -> HSV re-grade,
/// with a neutral fallback matched to overall scene brightness.
/// </summary>
public sealed class ScreenAnalyzer : IDisposable
{
    const int TargetWidth = 160;
    const int FrameIntervalMs = 50;               // 20 FPS cap
    const int SampleStride = 4;                   // every 4th pixel in x and y
    const int HueBins = 24;                       // 15 degrees each
    const double MinSaturation = 0.15;            // below this a pixel is "gray" for hue purposes
    const double SaturationBoost = 1.25;          // diffuse strip reads less vivid than an emissive screen
    const double BrightnessBlend = 0.5;           // pull of overall scene brightness on the result's value
    const double MinColorShare = 0.02;            // saturated-weight fraction needed to trust a hue over "gray"

    public event Action<Rgb>? ColorReady;
    CancellationTokenSource? _cts;
    byte[] _pixels = Array.Empty<byte>();

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            int interval = FrameIntervalMs;
            while (!ct.IsCancellationRequested)
            {
                long t0 = Environment.TickCount64;
                try
                {
                    int count = Capture(out int height);
                    if (count > 0 && Analyze(TargetWidth, height) is { } color) ColorReady?.Invoke(color);
                }
                catch { }
                int wait = interval - (int)(Environment.TickCount64 - t0);
                if (wait > 0)
                {
                    try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ---------- analysis (port of ScreenAnalyzer.kt) ----------
    Rgb? Analyze(int w, int h)
    {
        var binWeight = new double[HueBins];
        var binLinR = new double[HueBins];
        var binLinG = new double[HueBins];
        var binLinB = new double[HueBins];

        double overallWeight = 0, overallLinR = 0, overallLinG = 0, overallLinB = 0;
        double overallValueSum = 0, overallValueWeight = 0;

        for (int py = 0; py < h; py += SampleStride)
        {
            for (int px = 0; px < w; px += SampleStride)
            {
                int off = (py * w + px) * 4;                       // BGRA
                byte bv = _pixels[off], gv = _pixels[off + 1], rv = _pixels[off + 2];

                var (hue, sat, v) = ColorMath.ToHsv(rv / 255.0, gv / 255.0, bv / 255.0);
                double linR = ColorMath.SrgbToLinear(rv);
                double linG = ColorMath.SrgbToLinear(gv);
                double linB = ColorMath.SrgbToLinear(bv);

                // Bright pixels dominate perception regardless of colourfulness -> weight by v^2
                double brightnessWeight = v * v + 0.01;
                overallValueSum += v * brightnessWeight;
                overallValueWeight += brightnessWeight;
                overallLinR += linR * brightnessWeight;
                overallLinG += linG * brightnessWeight;
                overallLinB += linB * brightnessWeight;
                overallWeight += brightnessWeight;

                if (sat >= MinSaturation)
                {
                    double weight = sat * v;
                    int bin = Math.Clamp((int)(hue / 360.0 * HueBins), 0, HueBins - 1);
                    binWeight[bin] += weight;
                    binLinR[bin] += linR * weight;
                    binLinG[bin] += linG * weight;
                    binLinB[bin] += linB * weight;
                }
            }
        }

        if (overallWeight <= 0) return null;

        // Circularly smoothed histogram: score = prev*0.25 + bin + next*0.25
        int bestBin = -1;
        double bestScore = 0;
        for (int i = 0; i < HueBins; i++)
        {
            double prev = binWeight[(i - 1 + HueBins) % HueBins];
            double next = binWeight[(i + 1) % HueBins];
            double score = prev * 0.25 + binWeight[i] + next * 0.25;
            if (score > bestScore) { bestScore = score; bestBin = i; }
        }

        double saturatedWeightTotal = binWeight.Sum();
        double overallAvgValue = overallValueSum / overallValueWeight;

        if (bestBin == -1 || saturatedWeightTotal < overallWeight * MinColorShare)
        {
            // Essentially gray / washed out: neutral tone at the scene's real (brightness-weighted) linear-light colour
            return new Rgb(
                ColorMath.LinearToSrgbByte(overallLinR / overallWeight),
                ColorMath.LinearToSrgbByte(overallLinG / overallWeight),
                ColorMath.LinearToSrgbByte(overallLinB / overallWeight));
        }

        int prevBin = (bestBin - 1 + HueBins) % HueBins;
        int nextBin = (bestBin + 1) % HueBins;
        double wsum = binWeight[bestBin] + binWeight[prevBin] * 0.25 + binWeight[nextBin] * 0.25;
        double lr = (binLinR[bestBin] + binLinR[prevBin] * 0.25 + binLinR[nextBin] * 0.25) / wsum;
        double lg = (binLinG[bestBin] + binLinG[prevBin] * 0.25 + binLinG[nextBin] * 0.25) / wsum;
        double lb = (binLinB[bestBin] + binLinB[prevBin] * 0.25 + binLinB[nextBin] * 0.25) / wsum;

        byte dr = ColorMath.LinearToSrgbByte(lr);
        byte dg = ColorMath.LinearToSrgbByte(lg);
        byte db = ColorMath.LinearToSrgbByte(lb);

        // Re-grade in HSV: keep hue, boost saturation, pull value toward overall scene brightness
        var (hh, ss, vv) = ColorMath.ToHsv(dr / 255.0, dg / 255.0, db / 255.0);
        ss = Math.Clamp(ss * SaturationBoost, 0, 1);
        vv = Math.Clamp(vv * (1 - BrightnessBlend) + overallAvgValue * BrightnessBlend, 0, 1);
        return ColorMath.FromHsvRounded(hh, ss, vv);
    }

    // ---------- GDI capture ----------
    int Capture(out int height)
    {
        height = 0;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return 0;
        IntPtr memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            // Physical pixel size of the primary monitor, independent of DPI virtualisation
            int sw = GetDeviceCaps(screenDc, DESKTOPHORZRES);
            int sh = GetDeviceCaps(screenDc, DESKTOPVERTRES);
            if (sw <= 0 || sh <= 0) return 0;

            int w = TargetWidth;
            int h = Math.Max(1, sh * w / sw);

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;       // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;   // BI_RGB

            memDc = CreateCompatibleDC(screenDc);
            bmp = CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) return 0;
            old = SelectObject(memDc, bmp);

            SetStretchBltMode(memDc, HALFTONE);
            if (!StretchBlt(memDc, 0, 0, w, h, screenDc, 0, 0, sw, sh, SRCCOPY | CAPTUREBLT)) return 0;

            int bytes = w * h * 4;
            if (_pixels.Length != bytes) _pixels = new byte[bytes];
            Marshal.Copy(bits, _pixels, 0, bytes);
            height = h;
            return w * h;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    const int DESKTOPVERTRES = 117, DESKTOPHORZRES = 118, HALFTONE = 4;
    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int xs, int ys, int ws, int hs, uint rop);
}
