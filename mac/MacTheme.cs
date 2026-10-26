using Avalonia;
using Avalonia.Media;

namespace ClaudeUsageMonitor;

/// <summary>The same palette as the Windows app (see src/Theme.cs), as Avalonia brushes and fonts.</summary>
internal static class MacTheme
{
    public static readonly Color Canvas = Color.FromRgb(0x14, 0x12, 0x1E);
    public static readonly Color CanvasEnd = Color.FromRgb(0x1E, 0x1A, 0x33);
    public static readonly Color Surface = Color.FromRgb(0x1E, 0x1B, 0x2E);
    public static readonly Color Raised = Color.FromRgb(0x2B, 0x27, 0x42);
    public static readonly Color Border = Color.FromRgb(0x3D, 0x38, 0x5C);
    public static readonly Color Text = Color.FromRgb(0xF4, 0xF2, 0xFF);
    public static readonly Color Muted = Color.FromRgb(0xA9, 0xA4, 0xC4);
    public static readonly Color Track = Color.FromRgb(0x33, 0x2F, 0x4D);
    public static readonly Color Coral = Color.FromRgb(0xE8, 0x82, 0x5F);
    public static readonly Color Violet = Color.FromRgb(0x9F, 0x7A, 0xEA);
    public static readonly Color Warning = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color Danger = Color.FromRgb(0xFB, 0x71, 0x85);
    public static readonly Color Good = Color.FromRgb(0x34, 0xD3, 0x99);

    /// <summary>PingFang on macOS, JhengHei on Windows test runs.</summary>
    public static readonly FontFamily UiFont = new("PingFang TC, Microsoft JhengHei UI, Noto Sans CJK TC, sans-serif");
    public static readonly FontFamily NumberFont = new("SF Pro Display, Segoe UI, Helvetica Neue, sans-serif");

    public static Typeface Ui(FontWeight weight = FontWeight.Normal) => new(UiFont, FontStyle.Normal, weight);
    public static Typeface Number(FontWeight weight = FontWeight.Bold) => new(NumberFont, FontStyle.Normal, weight);

    public static (Color Start, Color End) Gradient(Level level) => level switch
    {
        Level.Plenty => (Color.FromRgb(0x04, 0x78, 0x57), Color.FromRgb(0x0E, 0x74, 0x90)),
        Level.Fair => (Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x7C, 0x3A, 0xED)),
        Level.Low => (Color.FromRgb(0xB4, 0x53, 0x09), Color.FromRgb(0xC2, 0x41, 0x0C)),
        Level.Critical => (Color.FromRgb(0xBE, 0x12, 0x3C), Color.FromRgb(0x9F, 0x12, 0x39)),
        _ => (Color.FromRgb(0x47, 0x55, 0x69), Color.FromRgb(0x33, 0x41, 0x55)),
    };

    public static Color Bright(Level level) => level switch
    {
        Level.Plenty => Good,
        Level.Fair => Color.FromRgb(0xA5, 0xB4, 0xFC),
        Level.Low => Warning,
        Level.Critical => Danger,
        _ => Muted,
    };

    public static Color Alpha(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static IBrush Solid(Color c) => new SolidColorBrush(c);

    /// <summary>A diagonal gradient across whatever it fills.</summary>
    public static IBrush Diagonal(Color start, Color end) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(start, 0), new GradientStop(end, 1) },
    };

    public static IBrush Horizontal(Color start, Color end) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(start, 0), new GradientStop(end, 1) },
    };

    public static IBrush Vertical(Color top, Color bottom) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
    };
}

/// <summary>Each quota's own hover light (same as the Windows app's GlowStyle).</summary>
internal sealed record MacGlow(Color Color, int Comets, double Tail, double LapMs)
{
    public static readonly MacGlow Session = new(Color.FromRgb(0x5E, 0xEA, 0xF4), 2, 0.30, 1500);
    public static readonly MacGlow Weekly = new(Color.FromRgb(0xFF, 0xC9, 0x4D), 1, 0.48, 2200);
    public static readonly MacGlow Model = new(Color.FromRgb(0xF4, 0x8F, 0xD8), 3, 0.18, 2600);

    public static MacGlow For(string windowId) => windowId switch
    {
        WindowIds.FiveHour => Session,
        WindowIds.SevenDay => Weekly,
        _ => Model,
    };
}
