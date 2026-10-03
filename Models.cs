namespace LEDStripController;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb Red = new(255, 0, 0);
    public static readonly Rgb Green = new(0, 255, 0);
    public static readonly Rgb Blue = new(0, 0, 255);
    public static readonly Rgb Yellow = new(255, 255, 0);
    public static readonly Rgb Cyan = new(0, 255, 255);
    public static readonly Rgb Magenta = new(255, 0, 255);
    public static readonly Rgb White = new(255, 255, 255);

    /// <summary>Truncating scale (matches Kotlin's Float.toInt()).</summary>
    public Rgb Scale(double f)
    {
        f = Math.Clamp(f, 0, 1);
        return new((byte)(R * f), (byte)(G * f), (byte)(B * f));
    }

    public int Pack() => (R << 16) | (G << 8) | B;
    public static Rgb Unpack(int v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v);
}

public enum StripType { ElkBledom, BjLed }

public enum SyncMode { None, Music, Screen }

public enum LedPattern
{
    Solid, JumpRgb, JumpAll, FadeRgb, FadeAll,
    CrossfadeRed, CrossfadeGreenBlue, CrossfadeBlueOrange, CrossfadeBlue, CrossfadeWhite,
    FlashRgb, FlashAll, StrobeWhite
}

public static class PatternExtensions
{
    public static string DisplayName(this LedPattern p) => p switch
    {
        LedPattern.Solid => "Solid",
        LedPattern.JumpRgb => "Jump RGB",
        LedPattern.JumpAll => "Jump All",
        LedPattern.FadeRgb => "Fade RGB",
        LedPattern.FadeAll => "Fade All",
        LedPattern.CrossfadeRed => "Crossfade Red",
        LedPattern.CrossfadeGreenBlue => "Crossfade Green Blue",
        LedPattern.CrossfadeBlueOrange => "Crossfade Blue Orange",
        LedPattern.CrossfadeBlue => "Crossfade Blue",
        LedPattern.CrossfadeWhite => "Crossfade White",
        LedPattern.FlashRgb => "Flash RGB",
        LedPattern.FlashAll => "Flash All",
        LedPattern.StrobeWhite => "Strobe White",
        _ => p.ToString()
    };
}

public static class Palette
{
    // The 10 preset swatches from ColorPicker.kt: (name, hue, saturation). Value is kept from the picker.
    public static readonly (string Name, float Hue, float Sat)[] Presets =
    {
        ("Red", 0f, 1f), ("Orange", 30f, 1f), ("Yellow", 60f, 1f), ("Green", 120f, 1f),
        ("Cyan", 180f, 1f), ("Sky blue", 210f, 1f), ("Blue", 240f, 1f), ("Violet", 270f, 1f),
        ("Magenta", 300f, 1f), ("White", 0f, 0f),
    };

    // Exact values of Android's SyncColor enum (OFF is handled by the UI as "no colour").
    public static readonly (string Name, Rgb Color)[] BandColors =
    {
        ("Red", new(255, 0, 0)), ("Orange", new(255, 90, 0)), ("Yellow", new(255, 210, 0)),
        ("Green", new(0, 255, 0)), ("Cyan", new(0, 255, 220)), ("Blue", new(0, 0, 255)),
        ("Violet", new(120, 0, 255)), ("Magenta", new(255, 0, 180)), ("Pink", new(255, 60, 120)),
        ("White", new(255, 255, 255)),
    };
}

/// <summary>
/// Software-animated patterns (set-colour command only), ported 1:1 from MainViewModel.kt:
/// 50 steps per fade/pulse, inclusive endpoints, truncating interpolation.
/// The caller waits the user-configured delay after every frame.
/// </summary>
public static class PatternEngine
{
    const int Steps = 50;

    static readonly Rgb[] Rgb3 = { Rgb.Red, Rgb.Green, Rgb.Blue };
    static readonly Rgb[] Rgb7 =
    {
        new(255, 0, 0), new(255, 128, 0), new(255, 255, 0), new(0, 255, 0),
        new(0, 255, 255), new(0, 0, 255), new(255, 0, 255),
    };
    static readonly Rgb[] Gb2 = { new(0, 255, 0), new(0, 0, 255) };
    static readonly Rgb[] Bo2 = { new(0, 0, 255), new(255, 165, 0) };

    public static IEnumerable<Rgb> Frames(LedPattern p) => p switch
    {
        LedPattern.JumpRgb => Jump(Rgb3),
        LedPattern.JumpAll => Jump(Rgb7),
        LedPattern.FadeRgb => Fade(Rgb3),
        LedPattern.FadeAll => Fade(Rgb7),
        LedPattern.CrossfadeRed => Pulse(new Rgb(255, 0, 0)),
        LedPattern.CrossfadeGreenBlue => Fade(Gb2),
        LedPattern.CrossfadeBlueOrange => Fade(Bo2),
        LedPattern.CrossfadeBlue => Pulse(new Rgb(0, 0, 255)),
        LedPattern.CrossfadeWhite => Pulse(new Rgb(255, 255, 255)),
        LedPattern.FlashRgb => Jump(WithBlackGaps(Rgb3)),
        LedPattern.FlashAll => Jump(WithBlackGaps(Rgb7)),
        LedPattern.StrobeWhite => Jump(new[] { Rgb.White, Rgb.Black }),
        _ => Enumerable.Empty<Rgb>()
    };

    static Rgb[] WithBlackGaps(Rgb[] set) => set.SelectMany(c => new[] { c, Rgb.Black }).ToArray();

    static Rgb Mix(Rgb a, Rgb b, float t) => new(
        (byte)(a.R + t * (b.R - a.R)),
        (byte)(a.G + t * (b.G - a.G)),
        (byte)(a.B + t * (b.B - a.B)));

    static IEnumerable<Rgb> Jump(Rgb[] set)
    {
        while (true)
            foreach (var c in set) yield return c;
    }

    static IEnumerable<Rgb> Fade(Rgb[] set)
    {
        int from = 0;
        while (true)
        {
            int to = (from + 1) % set.Length;
            for (int step = 0; step <= Steps; step++)
                yield return Mix(set[from], set[to], step / (float)Steps);
            from = to;
        }
    }

    static IEnumerable<Rgb> Pulse(Rgb c)
    {
        while (true)
        {
            for (int step = 0; step <= Steps; step++) yield return Mix(Rgb.Black, c, step / (float)Steps);
            for (int step = Steps; step >= 0; step--) yield return Mix(Rgb.Black, c, step / (float)Steps);
        }
    }
}
