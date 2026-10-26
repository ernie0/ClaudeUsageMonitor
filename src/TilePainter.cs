using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeUsageMonitor;

/// <summary>What one coloured quota block shows. Remaining is the animated value being drawn.</summary>
internal sealed record TileModel(string Title, TileIcon Icon, double Remaining, Level Level, string Badge, string Countdown, string Rate);

/// <summary>Each quota's own hover light, so the three blocks never glow alike.</summary>
internal sealed record GlowStyle(Color Color, int Comets, float Tail, float LapMs)
{
    /// <summary>5-hour: electric cyan, two quick comets.</summary>
    public static readonly GlowStyle Session = new(Color.FromArgb(0x5E, 0xEA, 0xF4), 2, 0.30f, 1500);

    /// <summary>Weekly: one long golden stream.</summary>
    public static readonly GlowStyle Weekly = new(Color.FromArgb(0xFF, 0xC9, 0x4D), 1, 0.48f, 2200);

    /// <summary>Per-model windows: three short pink-violet starlights.</summary>
    public static readonly GlowStyle Model = new(Color.FromArgb(0xF4, 0x8F, 0xD8), 3, 0.18f, 2600);

    public static GlowStyle For(string windowId) => windowId switch
    {
        WindowIds.FiveHour => Session,
        WindowIds.SevenDay => Weekly,
        _ => Model,
    };
}

/// <summary>
/// Pixel fonts for one DPI scale, cached so animation frames do not allocate fonts. Sizes are on the
/// generous side and most labels are bold: JhengHei's regular strokes get thin and blurry when small.
/// </summary>
internal sealed class FontSet : IDisposable
{
    public FontSet(float scale)
    {
        Scale = scale;
        Header = Px(Theme.UiFamily, 11f, FontStyle.Bold);
        Title = Px(Theme.UiFamily, 10.5f, FontStyle.Bold);
        Label = Px(Theme.UiFamily, 10f, FontStyle.Bold);
        Small = Px(Theme.UiFamily, 9.5f);
        Badge = Px(Theme.UiFamily, 9f, FontStyle.Bold);
        RingNumber = Px(Theme.NumberFamily, 14f, FontStyle.Bold);
        RingNumberLarge = Px(Theme.NumberFamily, 17f, FontStyle.Bold);
        RingSub = Px(Theme.UiFamily, 8.5f, FontStyle.Bold);
    }

    public float Scale { get; }
    public Font Header { get; }
    public Font Title { get; }
    public Font Label { get; }
    public Font Small { get; }
    public Font Badge { get; }
    public Font RingNumber { get; }
    public Font RingNumberLarge { get; }
    public Font RingSub { get; }

    Font Px(string family, float points, FontStyle style = FontStyle.Regular) =>
        new(family, points * 96f / 72f * Scale, style, GraphicsUnit.Pixel);

    public void Dispose()
    {
        foreach (var font in new[] { Header, Title, Label, Small, Badge, RingNumber, RingNumberLarge, RingSub })
            font.Dispose();
    }
}

internal static class TilePainter
{
    // Text on the blocks is opaque (ClearType ignores alpha); hierarchy comes from tint, not transparency.
    public static readonly Color Primary = Color.White;
    public static readonly Color Secondary = Color.FromArgb(0xF3, 0xF1, 0xFF);
    public static readonly Color Tertiary = Color.FromArgb(0xE6, 0xE2, 0xF8);

    static readonly StringFormat Centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    static readonly StringFormat Left = new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    static readonly StringFormat LeftMiddle = new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    /// <summary>Crisp ClearType text with a soft one-pixel shadow so it reads on any part of a gradient.</summary>
    public static void Text(Graphics g, string text, Font font, Color color, RectangleF r, StringFormat? format = null, float scale = 1)
    {
        format ??= Left;
        var hint = g.TextRenderingHint;
        // Grid-fitted like the ClearType pass, so both use the same advances and the shadow never drifts.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
            g.DrawString(text, font, shadow, new RectangleF(r.X, r.Y + Math.Max(1, scale), r.Width, r.Height), format);
        g.TextRenderingHint = hint;
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, r, format);
    }

    /// <summary>
    /// A vivid gradient block with soft decorative circles and a top sheen.
    /// glow (0..1) brightens it for hover and the "breathing" alert; shimmer (0..1, or &lt; 0 for none)
    /// sweeps a light band across while data is loading.
    /// </summary>
    public static void Block(Graphics g, RectangleF r, Level level, float radius, float glow = 0, float shimmer = -1)
    {
        var (start, end) = Theme.Gradient(level);
        using var path = Theme.RoundedRect(r, radius);
        using (var fill = new LinearGradientBrush(r, start, end, 35f))
            g.FillPath(fill, path);

        var clip = g.Clip;
        g.SetClip(path, CombineMode.Intersect);
        using (var big = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
        {
            float d = r.Height * 1.25f;
            g.FillEllipse(big, r.Right - d * 0.62f, r.Top - d * 0.48f, d, d);
        }
        using (var small = new SolidBrush(Color.FromArgb(14, 255, 255, 255)))
        {
            float d = r.Height * 0.7f;
            g.FillEllipse(small, r.Left - d * 0.35f, r.Bottom - d * 0.55f, d, d);
        }
        var sheenRect = new RectangleF(r.X, r.Y, r.Width, r.Height * 0.5f + 1);
        using (var sheen = new LinearGradientBrush(sheenRect, Color.FromArgb(26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
            g.FillRectangle(sheen, sheenRect);
        if (glow > 0)
        {
            using var pulse = new SolidBrush(Color.FromArgb((int)(40 * glow), 255, 255, 255));
            g.FillRectangle(pulse, r);
        }
        if (shimmer >= 0)
        {
            float band = r.Width * 0.45f;
            float x = r.Left - band + (r.Width + band * 2) * shimmer;
            var bandRect = new RectangleF(x, r.Top, band, r.Height);
            using var light = new LinearGradientBrush(bandRect, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = [Color.FromArgb(0, 255, 255, 255), Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)],
                    Positions = [0f, 0.5f, 1f],
                },
            };
            g.FillRectangle(light, bandRect);
        }
        g.Clip = clip;
    }

    /// <summary>
    /// Hover light: comets of light run around the block's rounded edge over a softly lit rim.
    /// Each quota has its own light (colour, number of comets, tail length, speed); the tail fades
    /// from that colour into a white head with a light burst, a sparkle and a few trailing twinkles.
    /// phase 0..1 is one lap; intensity 0..1 fades the whole effect in and out. With phase &lt; 0
    /// (animations off) only the lit rim is drawn.
    /// </summary>
    public static void EdgeGlow(Graphics g, RectangleF r, float radius, float phase, float scale, GlowStyle style, float intensity = 1)
    {
        if (intensity <= 0.01f)
            return;
        using var path = Theme.RoundedRect(r, radius);
        float breathe = phase < 0 ? 1 : 0.75f + 0.25f * MathF.Sin(phase * MathF.PI * 2 * 3);
        float level = breathe * intensity;
        var tint = style.Color;
        // The whole edge glows softly outward in the quota's colour, with a crisp lit rim on top.
        using (var aura = new Pen(Color.FromArgb((int)(70 * level), tint), 7f * scale))
            g.DrawPath(aura, path);
        using (var rim = new Pen(Color.FromArgb((int)(190 * level), Mix(tint, Color.White, 0.55f)), 1.6f * scale))
            g.DrawPath(rim, path);
        if (phase < 0)
            return;

        path.Flatten(null, 0.3f);
        var points = path.PathPoints;
        int n = points.Length;
        if (n < 3)
            return;
        var lengths = new float[n + 1];
        for (int i = 0; i < n; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % n];
            lengths[i + 1] = lengths[i] + MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        }
        float total = lengths[n];
        PointF At(float distance)
        {
            distance = ((distance % total) + total) % total;
            int i = Array.BinarySearch(lengths, distance);
            if (i < 0)
                i = ~i - 1;
            i = Math.Clamp(i, 0, n - 1);
            float span = lengths[i + 1] - lengths[i];
            float t = span <= 0 ? 0 : (distance - lengths[i]) / span;
            var a = points[i];
            var b = points[(i + 1) % n];
            return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        const int Steps = 48;
        float tail = total * style.Tail;
        using var halo = new Pen(Color.White, 8f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var core = new Pen(Color.White, 3f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var white = new SolidBrush(Color.FromArgb((int)(255 * intensity), 255, 255, 255));
        for (int c = 0; c < style.Comets; c++)
        {
            float offset = c / (float)style.Comets;
            float head = ((phase + offset) % 1f) * total;
            var previous = At(head - tail);
            for (int k = 1; k <= Steps; k++)
            {
                float t = k / (float)Steps;
                var point = At(head - tail + tail * t);
                // Coloured and thin at the tail, white and wide at the head: the light reads as moving forward.
                halo.Color = Color.FromArgb((int)(160 * t * t * level), tint);
                halo.Width = (2 + 10 * t) * scale;
                g.DrawLine(halo, previous, point);
                core.Color = Color.FromArgb((int)(255 * t * level), Mix(tint, Color.White, 0.25f + 0.75f * t * t));
                core.Width = (1f + 2.4f * t) * scale;
                g.DrawLine(core, previous, point);
                previous = point;
            }

            // A soft burst of coloured light around the head, with a turning sparkle in it.
            var at = At(head);
            float twinkle = 0.72f + 0.28f * MathF.Sin((phase * 11 + offset) * MathF.PI * 2);
            float burst = 14f * scale * twinkle;
            using (var glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(at.X - burst, at.Y - burst, burst * 2, burst * 2);
                using var glow = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb((int)(220 * level), Mix(tint, Color.White, 0.6f)),
                    SurroundColors = [Color.FromArgb(0, tint)],
                };
                g.FillPath(glow, glowPath);
            }
            Glyphs.Sparkle(g, at, 7f * scale * twinkle, white, phase * 360 * 2);

            // Little stars left behind the head, each twinkling on its own beat.
            for (int j = 1; j <= 4; j++)
            {
                float back = head - tail * 0.16f * j;
                var star = At(back);
                float beat = 0.5f + 0.5f * MathF.Sin((phase * 14 + j * 0.37f + offset) * MathF.PI * 2);
                int alpha = (int)(230 * (1 - j / 5f) * beat * intensity);
                if (alpha < 8)
                    continue;
                float wobble = MathF.Sin((phase * 6 + j) * MathF.PI * 2) * 3.5f * scale;
                using var starBrush = new SolidBrush(Color.FromArgb(alpha, Mix(tint, Color.White, 0.7f)));
                Glyphs.Sparkle(g, new PointF(star.X + wobble, star.Y - wobble * 0.6f), (3.6f - j * 0.5f) * scale, starBrush, j * 40);
            }
        }
    }

    static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>A soft drop shadow under a lifted block (lift 0..1).</summary>
    public static void Shadow(Graphics g, RectangleF r, float radius, float lift, float scale)
    {
        if (lift <= 0)
            return;
        for (int i = 3; i >= 1; i--)
        {
            var spread = RectangleF.Inflate(r, i * 1.5f * scale, i * 1.5f * scale);
            spread.Offset(0, 4 * scale * lift);
            using var path = Theme.RoundedRect(spread, radius + i * scale);
            using var brush = new SolidBrush(Color.FromArgb((int)(26 * lift), 0, 0, 0));
            g.FillPath(brush, path);
        }
    }

    /// <summary>White progress ring: the filled arc is what is left.</summary>
    public static void Ring(Graphics g, PointF c, float radius, float thickness, double remaining, string number, FontSet f)
    {
        var rect = new RectangleF(c.X - radius, c.Y - radius, radius * 2, radius * 2);
        using (var track = new Pen(Color.FromArgb(64, 255, 255, 255), thickness))
            g.DrawEllipse(track, rect);
        float sweep = (float)(360 * Math.Clamp(remaining, 0, 100) / 100);
        if (sweep > 0.5f)
        {
            using var arc = new Pen(Color.White, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, rect, -90, sweep);
        }
        float numberHeight = f.RingNumber.GetHeight(g);
        float subHeight = f.RingSub.GetHeight(g);
        Text(g, number, f.RingNumber, Primary, new RectangleF(rect.X - 6, c.Y - numberHeight * 0.7f, rect.Width + 12, numberHeight), Centered, f.Scale);
        Text(g, "剩餘", f.RingSub, Secondary, new RectangleF(rect.X, c.Y + numberHeight * 0.24f, rect.Width, subHeight), Centered, f.Scale);
    }

    /// <summary>A small translucent pill with a status word; returns its width.</summary>
    public static float Badge(Graphics g, PointF at, string text, Font font, float scale)
    {
        var size = g.MeasureString(text, font);
        var r = new RectangleF(at.X, at.Y, size.Width + 10 * scale, size.Height + 2 * scale);
        using var path = Theme.RoundedRect(r, r.Height / 2);
        using (var back = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillPath(back, path);
        using (var edge = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1, scale)))
            g.DrawPath(edge, path);
        using var white = new SolidBrush(Primary);
        g.DrawString(text, font, white, r, Centered);
        return r.Width;
    }

    public static void Icon(Graphics g, TileIcon icon, RectangleF r, Color color, float width)
    {
        switch (icon)
        {
            case TileIcon.Clock: Glyphs.Clock(g, r, color, width); break;
            case TileIcon.Calendar: Glyphs.Calendar(g, r, color, width); break;
            default: Glyphs.Chip(g, r, color, width); break;
        }
    }

    /// <summary>The main quota block: ring on the left, title, status word, countdown and burn rate on the right.</summary>
    public static void Tile(Graphics g, RectangleF r, TileModel m, FontSet f, float glow = 0, float shimmer = -1)
    {
        float s = f.Scale;
        Block(g, r, m.Level, 14 * s, glow, shimmer);

        var center = RingCenter(r, s);
        float radius = RingRadius(r, s);
        Ring(g, center, radius, 6.5f * s, m.Remaining, m.Level == Level.Unknown ? "—" : Format.Percent(m.Remaining), f);

        float x = center.X + radius + 12 * s;
        float width = r.Right - x - 6 * s;
        float y = r.Y + 11 * s;
        Icon(g, m.Icon, new RectangleF(x, y + 3 * s, 13 * s, 13 * s), Color.White, 1.6f * s);
        Text(g, m.Title, f.Title, Primary, new RectangleF(x + 18 * s, y, width - 18 * s, 22 * s), Left, s);
        y += 24 * s;
        Badge(g, new PointF(x, y), m.Badge, f.Badge, s);
        y += 24 * s;
        Text(g, m.Countdown, f.Label, Secondary, new RectangleF(x, y, width, 20 * s), Left, s);
        y += 21 * s;
        Text(g, m.Rate, f.Small, Tertiary, new RectangleF(x, y, width, 19 * s), Left, s);
    }

    public static float RingRadius(RectangleF r, float s) => Math.Min(r.Height * 0.32f, 33 * s);

    public static PointF RingCenter(RectangleF r, float s) => new(r.X + 12 * s + RingRadius(r, s), r.Y + r.Height / 2);

    /// <summary>A narrow block for a per-model weekly window (e.g. Fable).</summary>
    public static void Mini(Graphics g, RectangleF r, string name, double remaining, Level level, string word, FontSet f, float glow = 0, float shimmer = -1)
    {
        float s = f.Scale;
        Block(g, r, level, 12 * s, glow, shimmer);
        float x = r.X + 10 * s, width = r.Width - 20 * s;
        bool tall = r.Height >= 70 * s;
        Glyphs.Chip(g, new RectangleF(x, r.Y + 11 * s, 13 * s, 13 * s), Color.White, 1.4f * s);
        Text(g, name, f.Title, Primary, new RectangleF(x + 18 * s, r.Y + 8 * s, width - 18 * s, 22 * s), Left, s);
        if (tall)
        {
            Text(g, "本週剩", f.Small, Secondary, new RectangleF(x, r.Y + 34 * s, width, 18 * s), Left, s);
            Text(g, Format.Percent(remaining), f.RingNumberLarge, Primary, new RectangleF(x, r.Y + 50 * s, width, 30 * s), Left, s);
            Badge(g, new PointF(r.Right - 10 * s - g.MeasureString(word, f.Badge).Width - 10 * s, r.Y + 57 * s), word, f.Badge, s);
        }
        else
        {
            Text(g, $"{Format.Percent(remaining)} · {word}", f.Small, Secondary, new RectangleF(x, r.Y + 28 * s, width, 18 * s), Left, s);
        }
        var bar = new RectangleF(x, r.Bottom - 11 * s, width, 4 * s);
        using (var track = new SolidBrush(Color.FromArgb(64, 255, 255, 255)))
            g.FillRectangle(track, bar);
        using (var fill = new SolidBrush(Color.White))
            g.FillRectangle(fill, bar with { Width = (float)(bar.Width * Math.Clamp(remaining, 0, 100) / 100) });
    }

    /// <summary>The plain-language conclusion strip.</summary>
    public static void Pill(Graphics g, RectangleF r, Insight insight, FontSet f)
    {
        float s = f.Scale;
        using var path = Theme.RoundedRect(r, r.Height / 2);
        if (insight.Level == Level.Unknown)
        {
            using var back = new SolidBrush(Theme.Raised);
            g.FillPath(back, path);
        }
        else
        {
            var (start, end) = Theme.Gradient(insight.Level);
            using var back = new LinearGradientBrush(r, start, end, LinearGradientMode.Horizontal);
            g.FillPath(back, path);
        }
        float icon = r.Height * 0.52f;
        var iconRect = new RectangleF(r.X + r.Height * 0.42f, r.Y + (r.Height - icon) / 2, icon, icon);
        switch (insight.Icon)
        {
            case InsightIcon.Warning: Glyphs.Warning(g, iconRect, Color.White, 1.7f * s); break;
            case InsightIcon.Check: Glyphs.Check(g, iconRect, Color.White, 1.7f * s); break;
            default:
                using (var coral = new SolidBrush(Theme.Coral))
                    Glyphs.Sparkle(g, new PointF(iconRect.X + icon / 2, iconRect.Y + icon / 2), icon / 2, coral);
                break;
        }
        float x = iconRect.Right + 8 * s;
        Text(g, insight.Text, f.Label, Primary, new RectangleF(x, r.Y, r.Right - x - r.Height * 0.4f, r.Height), LeftMiddle, s);
    }

}
