using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeUsageMonitor;

internal enum TileIcon { Clock, Calendar, Chip }

internal enum InsightIcon { Warning, Check, Sparkle }

/// <summary>What one coloured quota block shows. Remaining is the animated value being drawn.</summary>
internal sealed record TileModel(string Title, TileIcon Icon, double Remaining, Level Level, string Badge, string Countdown, string Rate);

/// <summary>The one-sentence conclusion under the tiles.</summary>
internal sealed record Insight(string Text, Level Level, InsightIcon Icon);

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

    /// <summary>Turns the service state into the one sentence a user needs.</summary>
    public static Insight BuildInsight(MonitorService service, DateTimeOffset now)
    {
        var snapshot = service.Current;
        if (snapshot == null)
            return service.ErrorCode.Length > 0
                ? new Insight(service.Status, Level.Critical, InsightIcon.Warning)
                : new Insight("正在向 Claude Code 取得額度…", Level.Unknown, InsightIcon.Sparkle);
        if (!service.Fresh && service.ErrorCode.Length > 0)
            return new Insight(service.Status, Level.Low, InsightIcon.Warning);

        var main = new[] { snapshot.Session, snapshot.Weekly }.OfType<QuotaWindow>().ToList();
        if (main.FirstOrDefault(w => w.RemainingPercent <= 0) is { } empty)
        {
            string reset = empty.ResetsAt is { } at ? Format.ResetAt(at, now) + " 重置" : "等待重置";
            return new Insight($"{TileTitle(empty)}已用完，{reset}", Level.Critical, InsightIcon.Warning);
        }
        var risky = main
            .Select(w => (Window: w, Forecast: service.Forecasts.GetValueOrDefault(w.Id)))
            .Where(x => x.Forecast is { ExhaustsBeforeReset: true, ExhaustAt: not null })
            .OrderBy(x => x.Forecast!.ExhaustAt)
            .FirstOrDefault();
        if (risky.Window != null)
            return new Insight($"照目前速度，{TileTitle(risky.Window)} {Format.ResetAt(risky.Forecast!.ExhaustAt!.Value, now)} 用完，早於重置",
                Level.Low, InsightIcon.Warning);
        if (main.Any(w => service.Forecasts.GetValueOrDefault(w.Id)?.RatePerHour is not null))
            return new Insight("照目前速度，額度撐得到重置", Level.Plenty, InsightIcon.Check);
        return new Insight("正在觀察使用速度，約 15 分鐘後能預估會不會用完", Level.Unknown, InsightIcon.Sparkle);
    }

    public static string TileTitle(QuotaWindow w) => w.Id switch
    {
        WindowIds.FiveHour => "5 小時額度",
        WindowIds.SevenDay => "本週額度",
        _ => w.Label.Replace("本週·", "") + " 本週",
    };

    public static TileIcon IconFor(QuotaWindow w) => w.Id switch
    {
        WindowIds.FiveHour => TileIcon.Clock,
        WindowIds.SevenDay => TileIcon.Calendar,
        _ => TileIcon.Chip,
    };

    public static string RateText(MonitorService service, QuotaWindow w)
    {
        if (w.RemainingPercent <= 0)
            return "等待重置";
        var forecast = service.Forecasts.GetValueOrDefault(w.Id);
        if (forecast?.RatePerHour is not { } rate)
            return "速度觀察中…";
        if (rate < 0.05)
            return "目前沒有在用";
        return "每小時 −" + Format.Rate(rate).Replace("%/h", "%");
    }
}
