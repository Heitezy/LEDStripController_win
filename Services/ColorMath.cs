namespace LEDStripController;

public static class ColorMath
{
    static readonly double[] Lut = BuildLut();

    static double[] BuildLut()
    {
        var t = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return t;
    }

    public static double SrgbToLinear(byte v) => Lut[v];

    public static double LinearToSrgb(double l)
    {
        l = Math.Clamp(l, 0, 1);
        return l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
    }

    /// <summary>Inputs 0..1, returns h in 0..360, s and v in 0..1.</summary>
    public static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
            if (h < 0) h += 360;
        }
        return (h, max <= 0 ? 0 : d / max, max);
    }

    /// <summary>Exact port of Android's hsvToRgb(): float maths with truncation. h in degrees, s/v in 0..1.</summary>
    public static Rgb FromHsv(double hue, double sat, double val)
    {
        float h = (float)hue, s = (float)sat, v = (float)val;
        float hh = ((h % 360f + 360f) % 360f) / 60f;
        int i = (int)hh;
        float ff = hh - i;
        static byte C(float x) => (byte)Math.Clamp((int)(x * 255f), 0, 255);
        byte p = C(v * (1f - s));
        byte q = C(v * (1f - s * ff));
        byte t = C(v * (1f - s * (1f - ff)));
        byte vv = C(v);
        return (i % 6) switch
        {
            0 => new Rgb(vv, t, p),
            1 => new Rgb(q, vv, p),
            2 => new Rgb(p, vv, t),
            3 => new Rgb(p, q, vv),
            4 => new Rgb(t, p, vv),
            _ => new Rgb(vv, p, q),
        };
    }

    /// <summary>Port of ScreenAnalyzer.linearToSrgb(): truncating (srgb*255).toInt(), clamped.</summary>
    public static byte LinearToSrgbByte(double l) =>
        (byte)Math.Clamp((int)(LinearToSrgb(l) * 255.0), 0, 255);

    /// <summary>Equivalent of Android's Color.HSVToColor (rounds to nearest). h in degrees, s/v in 0..1.</summary>
    public static Rgb FromHsvRounded(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
