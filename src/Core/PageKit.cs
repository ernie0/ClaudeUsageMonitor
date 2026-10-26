using System.Drawing;

namespace ClaudeUsageMonitor;

internal enum IconKind { Clock, Calendar, Chip, Refresh, List, Document, External, Chart, Check, Clipboard, Download, Monitor, Search, ChevronRight, ChevronDown, Info, Sparkle, Sun, Folder }

/// <summary>A clickable area the page drew, and what clicking it means.</summary>
internal sealed record Hit(RectangleF Bounds, string Action);

/// <summary>
/// The dark-glass dashboard look (see docs/screenshots): page background with soft light, tinted glass
/// cards with a glow, pills, icon buttons, gradient rings, sparklines and line icons. Drawn through IPaint
/// so the Windows and Mac windows are identical.
/// </summary>
internal static class PageKit
{
    public static Color Hex(int rgb, int alpha = 255) => Color.FromArgb(alpha, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);

    public static readonly Color PageTop = Hex(0x0B1022);
    public static readonly Color PageBottom = Hex(0x15122E);
    public static readonly Color PanelTop = Hex(0x1A1E3C);
    public static readonly Color PanelBottom = Hex(0x141731);
    public static readonly Color PanelBorder = Hex(0x343A66);
    public static readonly Color Text = Hex(0xF4F2FF);
    public static readonly Color Muted = Hex(0xA9A8C9);
    public static readonly Color Faint = Hex(0x6E6F99);
    public static readonly Color Orange = Hex(0xF59E6B);
    public static readonly Color Violet = Hex(0x8B5CF6);
    public static readonly Color Indigo = Hex(0x6366F1);
    public static readonly Color Green = Hex(0x34D399);

    /// <summary>Glass tint per level: card top, card bottom, and the light colour for rings, borders and glow.</summary>
    public static (Color Top, Color Bottom, Color Light) Tint(Level level) => level switch
    {
        Level.Plenty => (Hex(0x0F766E), Hex(0x113F45), Hex(0x2DD4BF)),
        Level.Fair => (Hex(0x5B21B6), Hex(0x2E1B66), Hex(0xA78BFA)),
        Level.Low => (Hex(0xB4530F), Hex(0x6B2A12), Hex(0xFB923C)),
        Level.Critical => (Hex(0xBE123C), Hex(0x6E1032), Hex(0xFB7185)),
        _ => (Hex(0x334155), Hex(0x1E293B), Hex(0x94A3B8)),
    };

    public static void Background(IPaint p, float width, float height)
    {
        p.Gradient(new RectangleF(0, 0, width, height), PageTop, PageBottom, vertical: true);
        // Soft coloured light behind the glass: it shows through the translucent cards.
        (float X, float Y, float Size, Color Color, int Alpha)[] lights =
        [
            (width * 0.8f, -40, 190, Violet, 7),
            (-60, height * 0.32f, 150, Indigo, 6),
            (width * 0.18f, 210, 140, Hex(0x14B8A6), 5),
            (width * 0.52f, 230, 130, Hex(0xF97316), 5),
            (width * 0.86f, 260, 130, Hex(0xF43F5E), 5),
            (width * 0.5f, height * 0.75f, 220, Hex(0x4F46E5), 4),
        ];
        foreach (var (x, y, size, color, alpha) in lights)
        {
            for (int i = 6; i >= 1; i--)
                p.Circle(new PointF(x, y), size * 0.45f + i * size * 0.16f, WithAlpha(color, alpha + i));
        }
    }

    /// <summary>
    /// The glass itself, on top of a fill: a frosted haze, a diagonal reflection band, a bright top edge,
    /// a faint inner rim and a darker bottom, all kept inside the rounded shape.
    /// </summary>
    public static void GlassSheen(IPaint p, RectangleF r, float radius, float strength = 1)
    {
        using (p.Clip(r, radius))
        {
            p.Fill(r, Color.FromArgb((int)(10 * strength), 255, 255, 255));
            p.Gradient(new RectangleF(r.X, r.Y, r.Width, r.Height * 0.5f), Color.FromArgb((int)(30 * strength), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0, vertical: true);
            float band = Math.Min(r.Width * 0.22f, 120);
            float start = r.X + r.Width * 0.08f;
            p.Polygon([new(start, r.Y), new(start + band, r.Y), new(start + band - r.Height * 0.55f, r.Bottom), new(start - r.Height * 0.55f, r.Bottom)],
                Color.FromArgb((int)(13 * strength), 255, 255, 255));
            p.Polygon([new(start + band * 1.25f, r.Y), new(start + band * 1.55f, r.Y), new(start + band * 1.55f - r.Height * 0.55f, r.Bottom), new(start + band * 1.25f - r.Height * 0.55f, r.Bottom)],
                Color.FromArgb((int)(8 * strength), 255, 255, 255));
            p.Gradient(new RectangleF(r.X, r.Bottom - r.Height * 0.3f, r.Width, r.Height * 0.3f), Color.FromArgb(0, 0, 0, 0), Color.FromArgb((int)(45 * strength), 0, 0, 0), 0, vertical: true);
        }
        p.Line(new PointF(r.X + radius, r.Y + 1.2f), new PointF(r.Right - radius, r.Y + 1.2f), Color.FromArgb((int)(110 * strength), 255, 255, 255), 1.2f);
        p.Stroke(RectangleF.Inflate(r, -1.6f, -1.6f), Color.FromArgb((int)(22 * strength), 255, 255, 255), 1, Math.Max(0, radius - 1.6f));
    }

    public static void Glow(IPaint p, RectangleF r, float radius, Color color, int strength)
    {
        for (int i = 4; i >= 1; i--)
            p.Stroke(RectangleF.Inflate(r, i * 1.6f, i * 1.6f), WithAlpha(color, strength / (i + 1)), 2.2f, radius + i * 1.6f);
    }

    /// <summary>Neutral glass panel.</summary>
    /// <summary>Neutral frosted-glass panel.</summary>
    public static void Panel(IPaint p, RectangleF r, float radius = 16, bool hover = false)
    {
        p.Gradient(r, WithAlpha(PanelTop, 175), WithAlpha(PanelBottom, 200), radius, vertical: true);
        GlassSheen(p, r, radius, 0.8f);
        p.Stroke(r, hover ? WithAlpha(Violet, 210) : WithAlpha(Hex(0x5A63A8), 120), hover ? 1.6f : 1.1f, radius);
    }

    /// <summary>Tinted glass card: translucent colour over the page light, glass sheen, and a glow in the level's light colour.</summary>
    public static void Card(IPaint p, RectangleF r, Level level, float radius, bool hover, float strength = 1)
    {
        var (top, bottom, light) = Tint(level);
        Glow(p, r, radius, light, (int)((hover ? 160 : 90) * strength));
        p.Gradient(r, WithAlpha(top, 200), WithAlpha(bottom, 185), radius, vertical: true);
        using (p.Clip(r, radius))
        {
            // Light from the card's colour pooling in the upper corner.
            p.Circle(new PointF(r.Right - r.Height * 0.15f, r.Top - r.Height * 0.1f), r.Height * 0.65f, WithAlpha(light, 26));
            p.Circle(new PointF(r.X + r.Width * 0.1f, r.Bottom + r.Height * 0.2f), r.Height * 0.55f, Color.FromArgb(18, 0, 0, 0));
        }
        GlassSheen(p, r, radius, 1.1f);
        p.Stroke(r, WithAlpha(light, hover ? 235 : 160), hover ? 1.8f : 1.2f, radius);
    }

    /// <summary>A rounded label; returns its width.</summary>
    public static float Pill(IPaint p, PointF at, string text, Color fill, Color textColor, float points = 9.5f, float height = 24, Color? border = null)
    {
        float width = p.Measure(text, points, bold: true) + height * 0.9f;
        var r = new RectangleF(at.X, at.Y, width, height);
        p.Fill(r, fill, height / 2);
        if (border is { } b)
            p.Stroke(r, b, 1.1f, height / 2);
        p.Text(text, r, points, textColor, bold: true, align: TextAlign.Center, middle: true);
        return width;
    }

    public static float ButtonWidth(IPaint p, string text, IconKind? icon, float points = 10.5f) =>
        p.Measure(text, points, bold: true) + (icon == null ? 34 : 56);

    /// <summary>Glass button (or the violet gradient primary one) with an optional icon; registers its hit area.</summary>
    public static void Button(IPaint p, List<Hit> hits, string action, RectangleF r, string text, IconKind? icon, bool primary, string? hover, float points = 10.5f)
    {
        bool over = hover == action;
        float radius = r.Height / 2;
        if (primary)
        {
            Glow(p, r, radius, Violet, over ? 140 : 70);
            p.Gradient(r, over ? Hex(0xA06BFF) : Hex(0x8B5CF6), over ? Hex(0x6E7BFF) : Hex(0x5B67F0), radius);
        }
        else
        {
            p.Fill(r, over ? Hex(0x2A2F58, 240) : Hex(0x1C2042, 230), radius);
            p.Stroke(r, over ? WithAlpha(Violet, 220) : Hex(0x3C4378), 1.2f, radius);
        }
        float textWidth = p.Measure(text, points, bold: true);
        float contentWidth = textWidth + (icon == null ? 0 : 24);
        float x = r.X + (r.Width - contentWidth) / 2;
        if (icon is { } kind)
        {
            Icon(p, kind, new RectangleF(x, r.Y + r.Height / 2 - 8, 16, 16), Text, 1.7f);
            x += 24;
        }
        p.Text(text, new RectangleF(x, r.Y, textWidth + 4, r.Height), points, Text, bold: true, middle: true);
        hits.Add(new Hit(r, action));
    }

    /// <summary>Glowing ring: the arc is what is left, fading from the light colour to white at its end.</summary>
    public static void Ring(IPaint p, PointF c, float radius, float thickness, double remaining, Color light, float progress = 1)
    {
        p.Arc(c, radius, 0, 360, Color.FromArgb(40, 0, 0, 0), thickness + 2);
        p.Arc(c, radius, 0, 360, WithAlpha(light, 45), thickness);
        float sweep = (float)(360 * Math.Clamp(remaining, 0, 100) / 100 * progress);
        if (sweep < 0.5f)
            return;
        p.Arc(c, radius, -90, sweep, WithAlpha(light, 55), thickness * 2.1f);
        const int Segments = 40;
        for (int i = 0; i < Segments; i++)
        {
            float t = i / (float)Segments;
            var color = Mix(light, Color.White, t * 0.55f);
            p.Arc(c, radius, -90 + sweep * t, sweep / Segments + 0.6f, color, thickness);
        }
        // A bright dot at the end of the arc.
        double end = (-90 + sweep) * Math.PI / 180;
        p.Circle(new PointF(c.X + radius * (float)Math.Cos(end), c.Y + radius * (float)Math.Sin(end)), thickness * 0.55f, Color.White);
    }

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>A small area chart of remaining percentage (0..100), faded so text can sit on it.</summary>
    public static void Sparkline(IPaint p, RectangleF r, IReadOnlyList<double> values, Color color, int alpha = 255)
    {
        if (values.Count < 2)
            return;
        var points = values.Select((v, i) => new PointF(r.X + r.Width * i / (values.Count - 1), r.Bottom - (float)(Math.Clamp(v, 0, 100) / 100) * r.Height)).ToList();
        p.Area(points, r.Bottom, WithAlpha(color, alpha * 90 / 255), WithAlpha(color, 0));
        p.Polyline(points, WithAlpha(color, alpha), 2.2f);
        p.Circle(points[^1], 4, Color.White, WithAlpha(color, alpha), 2);
    }

    public static void Progress(IPaint p, RectangleF r, double fraction, Color light)
    {
        p.Fill(r, Color.FromArgb(70, 0, 0, 0), r.Height / 2);
        float width = (float)(r.Width * Math.Clamp(fraction, 0, 1));
        if (width >= r.Height)
            p.Gradient(r with { Width = width }, Mix(light, Color.White, 0.25f), light, r.Height / 2);
        else if (width > 0)
            p.Circle(new PointF(r.X + r.Height / 2, r.Y + r.Height / 2), r.Height / 2, light);
    }

    public static void StatusPill(IPaint p, RectangleF r, string text, Color dot)
    {
        p.Fill(r, Hex(0x161A36, 230), r.Height / 2);
        p.Stroke(r, Hex(0x2F3563), 1.1f, r.Height / 2);
        p.Circle(new PointF(r.X + 20, r.Y + r.Height / 2), 6.5f, dot);
        p.Circle(new PointF(r.X + 20, r.Y + r.Height / 2), 10, WithAlpha(dot, 40));
        p.Text(text, new RectangleF(r.X + 36, r.Y, r.Width - 44, r.Height), 10.5f, Text, middle: true);
    }

    /// <summary>Page title with the Claude-style sunburst, an optional plan pill, a subtitle and a status pill.</summary>
    public static void Header(IPaint p, float width, string title, string subtitle, string? plan, string? status, Color statusDot, float rightReserve)
    {
        Icon(p, IconKind.Sun, new RectangleF(22, 18, 42, 42), Orange, 0);
        p.Text(title, new RectangleF(76, 14, 600, 34), 19, Text, bold: true);
        if (plan != null)
        {
            float titleWidth = p.Measure(title, 19, bold: true);
            Pill(p, new PointF(76 + titleWidth + 14, 21), plan, WithAlpha(Violet, 60), Hex(0xD9CCFF), 9.5f, 26, WithAlpha(Violet, 170));
        }
        p.Text(subtitle, new RectangleF(78, 52, 700, 20), 10.5f, Muted);
        if (status != null)
        {
            float pillWidth = p.Measure(status, 10.5f) + 56;
            StatusPill(p, new RectangleF(width - rightReserve - pillWidth - 24, 24, pillWidth, 40), status, statusDot);
        }
    }

    /// <summary>Line icons drawn from primitives, so both platforms look the same.</summary>
    public static void Icon(IPaint p, IconKind kind, RectangleF r, Color c, float w)
    {
        float x = r.X, y = r.Y, s = Math.Min(r.Width, r.Height);
        var center = new PointF(x + s / 2, y + s / 2);
        PointF P(float fx, float fy) => new(x + s * fx, y + s * fy);
        void L(float x1, float y1, float x2, float y2) => p.Line(P(x1, y1), P(x2, y2), c, w);
        switch (kind)
        {
            case IconKind.Clock:
                p.Circle(center, s * 0.45f, null, c, w);
                L(0.5f, 0.5f, 0.5f, 0.24f);
                L(0.5f, 0.5f, 0.7f, 0.6f);
                break;
            case IconKind.Calendar:
                p.Stroke(new RectangleF(x + s * 0.06f, y + s * 0.16f, s * 0.88f, s * 0.78f), c, w, s * 0.14f);
                L(0.06f, 0.4f, 0.94f, 0.4f);
                L(0.3f, 0.04f, 0.3f, 0.26f);
                L(0.7f, 0.04f, 0.7f, 0.26f);
                break;
            case IconKind.Chip:
                p.Stroke(new RectangleF(x + s * 0.2f, y + s * 0.2f, s * 0.6f, s * 0.6f), c, w, s * 0.1f);
                foreach (float t in new[] { 0.36f, 0.5f, 0.64f })
                {
                    L(t, 0.04f, t, 0.2f);
                    L(t, 0.8f, t, 0.96f);
                    L(0.04f, t, 0.2f, t);
                    L(0.8f, t, 0.96f, t);
                }
                break;
            case IconKind.Refresh:
                p.Arc(center, s * 0.38f, -60, 300, c, w);
                L(0.78f, 0.1f, 0.74f, 0.32f);
                L(0.74f, 0.32f, 0.52f, 0.28f);
                break;
            case IconKind.List:
                foreach (float t in new[] { 0.22f, 0.5f, 0.78f })
                {
                    p.Circle(P(0.1f, t), w * 0.9f, c);
                    L(0.3f, t, 0.95f, t);
                }
                break;
            case IconKind.Document:
                p.Stroke(new RectangleF(x + s * 0.18f, y + s * 0.06f, s * 0.64f, s * 0.88f), c, w, s * 0.1f);
                L(0.32f, 0.38f, 0.68f, 0.38f);
                L(0.32f, 0.56f, 0.68f, 0.56f);
                L(0.32f, 0.74f, 0.56f, 0.74f);
                break;
            case IconKind.External:
                p.Stroke(new RectangleF(x + s * 0.08f, y + s * 0.2f, s * 0.72f, s * 0.72f), c, w, s * 0.1f);
                L(0.45f, 0.55f, 0.92f, 0.08f);
                L(0.6f, 0.08f, 0.92f, 0.08f);
                L(0.92f, 0.08f, 0.92f, 0.4f);
                break;
            case IconKind.Chart:
                p.Fill(new RectangleF(x + s * 0.12f, y + s * 0.5f, s * 0.18f, s * 0.42f), c, 2);
                p.Fill(new RectangleF(x + s * 0.41f, y + s * 0.3f, s * 0.18f, s * 0.62f), c, 2);
                p.Fill(new RectangleF(x + s * 0.7f, y + s * 0.1f, s * 0.18f, s * 0.82f), c, 2);
                break;
            case IconKind.Check:
                p.Polyline([P(0.22f, 0.52f), P(0.42f, 0.72f), P(0.8f, 0.32f)], c, w * 1.3f);
                break;
            case IconKind.Clipboard:
                p.Stroke(new RectangleF(x + s * 0.16f, y + s * 0.12f, s * 0.68f, s * 0.82f), c, w, s * 0.1f);
                p.Fill(new RectangleF(x + s * 0.34f, y + s * 0.04f, s * 0.32f, s * 0.16f), c, 2);
                L(0.32f, 0.45f, 0.68f, 0.45f);
                L(0.32f, 0.64f, 0.68f, 0.64f);
                break;
            case IconKind.Download:
                L(0.5f, 0.08f, 0.5f, 0.64f);
                p.Polyline([P(0.28f, 0.44f), P(0.5f, 0.66f), P(0.72f, 0.44f)], c, w);
                p.Polyline([P(0.1f, 0.7f), P(0.1f, 0.92f), P(0.9f, 0.92f), P(0.9f, 0.7f)], c, w);
                break;
            case IconKind.Monitor:
                p.Stroke(new RectangleF(x + s * 0.06f, y + s * 0.12f, s * 0.88f, s * 0.6f), c, w, s * 0.08f);
                L(0.5f, 0.72f, 0.5f, 0.88f);
                L(0.3f, 0.9f, 0.7f, 0.9f);
                break;
            case IconKind.Search:
                p.Circle(P(0.42f, 0.42f), s * 0.3f, null, c, w);
                L(0.64f, 0.64f, 0.92f, 0.92f);
                break;
            case IconKind.ChevronRight:
                p.Polyline([P(0.36f, 0.18f), P(0.68f, 0.5f), P(0.36f, 0.82f)], c, w);
                break;
            case IconKind.ChevronDown:
                p.Polyline([P(0.18f, 0.36f), P(0.5f, 0.68f), P(0.82f, 0.36f)], c, w);
                break;
            case IconKind.Info:
                p.Circle(center, s * 0.44f, null, c, w);
                L(0.5f, 0.44f, 0.5f, 0.74f);
                p.Circle(P(0.5f, 0.28f), w * 0.8f, c);
                break;
            case IconKind.Folder:
                p.Polygon([P(0.06f, 0.24f), P(0.4f, 0.24f), P(0.5f, 0.36f), P(0.94f, 0.36f), P(0.94f, 0.86f), P(0.06f, 0.86f)], WithAlpha(c, 60));
                p.Polyline([P(0.06f, 0.24f), P(0.4f, 0.24f), P(0.5f, 0.36f), P(0.94f, 0.36f), P(0.94f, 0.86f), P(0.06f, 0.86f), P(0.06f, 0.24f)], c, w);
                break;
            case IconKind.Sparkle:
                p.Polygon(Star(center, s * 0.5f, s * 0.13f, 4), c);
                break;
            case IconKind.Sun:
                // Claude-style sunburst: many thin rays around a small core.
                p.Polygon(Star(center, s * 0.5f, s * 0.12f, 12), c);
                p.Circle(center, s * 0.12f, c);
                break;
        }
    }

    static List<PointF> Star(PointF c, float outer, float inner, int rays)
    {
        var points = new List<PointF>();
        for (int i = 0; i < rays * 2; i++)
        {
            double angle = Math.PI * i / rays - Math.PI / 2;
            float radius = i % 2 == 0 ? outer : inner;
            points.Add(new PointF(c.X + radius * (float)Math.Cos(angle), c.Y + radius * (float)Math.Sin(angle)));
        }
        return points;
    }
}
