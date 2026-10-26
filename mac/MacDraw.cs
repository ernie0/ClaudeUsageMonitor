using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace ClaudeUsageMonitor;

/// <summary>What one coloured quota block shows (Remaining is the animated value being drawn).</summary>
internal sealed record MacTile(string Title, TileIcon Icon, double Remaining, Level Level, string Badge, string Countdown, string Rate);

/// <summary>
/// Drawing for the Mac interface, mirroring src/TilePainter.cs: vivid gradient blocks, a white ring for
/// what is left, status badges, the one-line conclusion and each quota's hover light.
/// </summary>
internal static class MacDraw
{
    public static readonly Color Primary = Colors.White;
    public static readonly Color Secondary = Color.FromRgb(0xF3, 0xF1, 0xFF);
    public static readonly Color Tertiary = Color.FromRgb(0xE6, 0xE2, 0xF8);

    /// <summary>Points to device-independent pixels (the Windows app sizes text in points).</summary>
    public static double Pt(double points) => points * 96 / 72;

    public static FormattedText Format(string text, Typeface typeface, double points, Color color, double maxWidth = double.PositiveInfinity, TextAlignment alignment = TextAlignment.Left) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, Pt(points), new SolidColorBrush(color))
        {
            MaxTextWidth = double.IsInfinity(maxWidth) ? 10_000 : Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };

    public static double Measure(string text, Typeface typeface, double points) =>
        Format(text, typeface, points, Colors.White).WidthIncludingTrailingWhitespace;

    /// <summary>Text with a soft one-pixel shadow so it reads on any part of a gradient.</summary>
    public static void Text(DrawingContext ctx, string text, Typeface typeface, double points, Color color, Rect area,
        TextAlignment alignment = TextAlignment.Left, bool middle = false, bool shadow = true)
    {
        var main = Format(text, typeface, points, color, area.Width, alignment);
        double y = middle ? area.Y + (area.Height - main.Height) / 2 : area.Y;
        if (shadow)
            ctx.DrawText(Format(text, typeface, points, Color.FromArgb(80, 0, 0, 0), area.Width, alignment), new Point(area.X, y + 1));
        ctx.DrawText(main, new Point(area.X, y));
    }

    /// <summary>
    /// A gradient block with soft decorative circles and a top sheen. glow brightens it (hover, breathing
    /// alert), shimmer (0..1, &lt; 0 for none) sweeps a light band across while loading, lift casts a shadow.
    /// </summary>
    public static void Block(DrawingContext ctx, Rect r, Level level, double radius, double glow = 0, double shimmer = -1, double lift = 0)
    {
        var (start, end) = MacTheme.Gradient(level);
        var shadows = lift > 0
            ? new BoxShadows(new BoxShadow { OffsetY = 4 * lift, Blur = 14 * lift, Color = Color.FromArgb((byte)(110 * lift), 0, 0, 0) })
            : default;
        ctx.DrawRectangle(MacTheme.Diagonal(start, end), null, r, radius, radius, shadows);

        using (ctx.PushClip(new RoundedRect(r, radius)))
        {
            double d = r.Height * 1.25;
            ctx.DrawEllipse(MacTheme.Solid(Color.FromArgb(22, 255, 255, 255)), null,
                new Point(r.Right - d * 0.62 + d / 2, r.Top - d * 0.48 + d / 2), d / 2, d / 2);
            double small = r.Height * 0.7;
            ctx.DrawEllipse(MacTheme.Solid(Color.FromArgb(14, 255, 255, 255)), null,
                new Point(r.Left - small * 0.35 + small / 2, r.Bottom - small * 0.55 + small / 2), small / 2, small / 2);
            ctx.DrawRectangle(MacTheme.Vertical(Color.FromArgb(26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)), null,
                new Rect(r.X, r.Y, r.Width, r.Height * 0.5));
            if (glow > 0)
                ctx.DrawRectangle(MacTheme.Solid(Color.FromArgb((byte)(40 * glow), 255, 255, 255)), null, r);
            if (shimmer >= 0)
            {
                double band = r.Width * 0.45;
                double x = r.Left - band + (r.Width + band * 2) * shimmer;
                var light = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                        new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.5),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                    },
                };
                ctx.DrawRectangle(light, null, new Rect(x, r.Top, band, r.Height));
            }
        }
    }

    /// <summary>White progress ring: the filled arc is what is left.</summary>
    public static void Ring(DrawingContext ctx, Point c, double radius, double thickness, double remaining, string number)
    {
        ctx.DrawEllipse(null, new Pen(MacTheme.Solid(Color.FromArgb(64, 255, 255, 255)), thickness), c, radius, radius);
        double sweep = 360 * Math.Clamp(remaining, 0, 100) / 100;
        if (sweep >= 359.9)
        {
            ctx.DrawEllipse(null, new Pen(Brushes.White, thickness), c, radius, radius);
        }
        else if (sweep > 0.5)
        {
            ctx.DrawGeometry(null, new Pen(Brushes.White, thickness, lineCap: PenLineCap.Round), Arc(c, radius, -90, sweep));
        }
        var numberText = Format(number, MacTheme.Number(), 14, Primary);
        double numberY = c.Y - numberText.Height * 0.7;
        Text(ctx, number, MacTheme.Number(), 14, Primary, new Rect(c.X - radius - 6, numberY, radius * 2 + 12, numberText.Height), TextAlignment.Center);
        Text(ctx, "剩餘", MacTheme.Ui(FontWeight.Bold), 8.5, Secondary, new Rect(c.X - radius, c.Y + numberText.Height * 0.24, radius * 2, 16), TextAlignment.Center);
    }

    public static Geometry Arc(Point c, double radius, double startDegrees, double sweepDegrees)
    {
        static Point On(Point c, double r, double degrees) =>
            new(c.X + r * Math.Cos(degrees * Math.PI / 180), c.Y + r * Math.Sin(degrees * Math.PI / 180));
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(On(c, radius, startDegrees), false);
        g.ArcTo(On(c, radius, startDegrees + sweepDegrees), new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise);
        g.EndFigure(false);
        return geometry;
    }

    /// <summary>A small translucent pill with a status word; returns its width.</summary>
    public static double Badge(DrawingContext ctx, Point at, string text)
    {
        var label = Format(text, MacTheme.Ui(FontWeight.Bold), 9, Primary);
        var r = new Rect(at.X, at.Y, label.WidthIncludingTrailingWhitespace + 12, label.Height + 3);
        ctx.DrawRectangle(MacTheme.Solid(Color.FromArgb(70, 0, 0, 0)), new Pen(MacTheme.Solid(Color.FromArgb(90, 255, 255, 255)), 1), r, r.Height / 2, r.Height / 2);
        ctx.DrawText(label, new Point(r.X + 6, r.Y + 1.5));
        return r.Width;
    }

    /// <summary>The four-pointed "AI" sparkle, with concave sides.</summary>
    public static void Sparkle(DrawingContext ctx, Point c, double size, IBrush brush, double rotation = 0)
    {
        Point At(double degrees, double r) => new(c.X + Math.Cos((degrees + rotation - 90) * Math.PI / 180) * r, c.Y + Math.Sin((degrees + rotation - 90) * Math.PI / 180) * r);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(0, size), true);
            for (int i = 1; i <= 4; i++)
                g.QuadraticBezierTo(At(i * 90 - 45, size * 0.18), At(i * 90 % 360, size));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geometry);
    }

    public static void Icon(DrawingContext ctx, TileIcon icon, Rect r, Color color, double width)
    {
        var pen = new Pen(MacTheme.Solid(color), width, lineCap: PenLineCap.Round);
        var c = r.Center;
        switch (icon)
        {
            case TileIcon.Clock:
                ctx.DrawEllipse(null, pen, c, r.Width / 2, r.Height / 2);
                ctx.DrawLine(pen, c, new Point(c.X, r.Y + r.Height * 0.24));
                ctx.DrawLine(pen, c, new Point(r.X + r.Width * 0.72, c.Y));
                break;
            case TileIcon.Calendar:
                var body = new Rect(r.X, r.Y + r.Height * 0.14, r.Width, r.Height * 0.86);
                ctx.DrawRectangle(null, pen, body, r.Width * 0.18, r.Width * 0.18);
                ctx.DrawLine(pen, new Point(body.X, body.Y + body.Height * 0.32), new Point(body.Right, body.Y + body.Height * 0.32));
                ctx.DrawLine(pen, new Point(r.X + r.Width * 0.3, r.Y), new Point(r.X + r.Width * 0.3, r.Y + r.Height * 0.26));
                ctx.DrawLine(pen, new Point(r.X + r.Width * 0.7, r.Y), new Point(r.X + r.Width * 0.7, r.Y + r.Height * 0.26));
                break;
            default:
                ctx.DrawRectangle(null, pen, r.Deflate(r.Width * 0.18), r.Width * 0.12, r.Width * 0.12);
                for (int i = 0; i < 3; i++)
                {
                    double t = r.X + r.Width * (0.32 + i * 0.18);
                    ctx.DrawLine(pen, new Point(t, r.Y), new Point(t, r.Y + r.Height * 0.18));
                    ctx.DrawLine(pen, new Point(t, r.Bottom - r.Height * 0.18), new Point(t, r.Bottom));
                }
                break;
        }
    }

    /// <summary>The main quota block: ring on the left, title, status word, countdown and burn rate on the right.</summary>
    public static void Tile(DrawingContext ctx, Rect r, MacTile m, double glow = 0, double shimmer = -1, double lift = 0)
    {
        Block(ctx, r, m.Level, 14, glow, shimmer, lift);
        double radius = RingRadius(r);
        var center = RingCenter(r);
        Ring(ctx, center, radius, 6.5, m.Remaining, m.Level == Level.Unknown ? "—" : ClaudeUsageMonitor.Format.Percent(m.Remaining));

        double x = center.X + radius + 12;
        double width = r.Right - x - 6;
        double y = r.Y + 11;
        Icon(ctx, m.Icon, new Rect(x, y + 3, 13, 13), Colors.White, 1.6);
        Text(ctx, m.Title, MacTheme.Ui(FontWeight.Bold), 10.5, Primary, new Rect(x + 18, y, width - 18, 22));
        y += 24;
        Badge(ctx, new Point(x, y), m.Badge);
        y += 24;
        Text(ctx, m.Countdown, MacTheme.Ui(FontWeight.Bold), 10, Secondary, new Rect(x, y, width, 20));
        y += 21;
        Text(ctx, m.Rate, MacTheme.Ui(), 9.5, Tertiary, new Rect(x, y, width, 19));
    }

    public static double RingRadius(Rect r) => Math.Min(r.Height * 0.32, 33);

    public static Point RingCenter(Rect r) => new(r.X + 12 + RingRadius(r), r.Y + r.Height / 2);

    /// <summary>A narrow block for a per-model weekly window (e.g. Fable).</summary>
    public static void Mini(DrawingContext ctx, Rect r, string name, double remaining, Level level, string word, double glow = 0, double shimmer = -1, double lift = 0)
    {
        Block(ctx, r, level, 12, glow, shimmer, lift);
        double x = r.X + 10, width = r.Width - 20;
        bool tall = r.Height >= 70;
        Icon(ctx, TileIcon.Chip, new Rect(x, r.Y + 11, 13, 13), Colors.White, 1.4);
        Text(ctx, name, MacTheme.Ui(FontWeight.Bold), 10.5, Primary, new Rect(x + 18, r.Y + 8, width - 18, 22));
        if (tall)
        {
            Text(ctx, "本週剩", MacTheme.Ui(), 9.5, Secondary, new Rect(x, r.Y + 34, width, 18));
            Text(ctx, ClaudeUsageMonitor.Format.Percent(remaining), MacTheme.Number(), 17, Primary, new Rect(x, r.Y + 50, width, 30));
            double badgeWidth = Measure(word, MacTheme.Ui(FontWeight.Bold), 9) + 12;
            Badge(ctx, new Point(r.Right - 10 - badgeWidth, r.Y + 57), word);
        }
        else
        {
            Text(ctx, $"{ClaudeUsageMonitor.Format.Percent(remaining)} · {word}", MacTheme.Ui(), 9.5, Secondary, new Rect(x, r.Y + 28, width, 18));
        }
        var bar = new Rect(x, r.Bottom - 11, width, 4);
        ctx.DrawRectangle(MacTheme.Solid(Color.FromArgb(64, 255, 255, 255)), null, bar);
        ctx.DrawRectangle(Brushes.White, null, bar.WithWidth(bar.Width * Math.Clamp(remaining, 0, 100) / 100));
    }

    /// <summary>The plain-language conclusion strip.</summary>
    public static void Pill(DrawingContext ctx, Rect r, Insight insight)
    {
        if (insight.Level == Level.Unknown)
        {
            ctx.DrawRectangle(MacTheme.Solid(MacTheme.Raised), null, r, r.Height / 2, r.Height / 2);
        }
        else
        {
            var (start, end) = MacTheme.Gradient(insight.Level);
            ctx.DrawRectangle(MacTheme.Horizontal(start, end), null, r, r.Height / 2, r.Height / 2);
        }
        double icon = r.Height * 0.52;
        var iconRect = new Rect(r.X + r.Height * 0.42, r.Y + (r.Height - icon) / 2, icon, icon);
        var pen = new Pen(Brushes.White, 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        switch (insight.Icon)
        {
            case InsightIcon.Warning:
                var triangle = new StreamGeometry();
                using (var g = triangle.Open())
                {
                    g.BeginFigure(new Point(iconRect.Center.X, iconRect.Y), false);
                    g.LineTo(iconRect.BottomRight);
                    g.LineTo(iconRect.BottomLeft);
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(null, pen, triangle);
                ctx.DrawLine(pen, new Point(iconRect.Center.X, iconRect.Y + icon * 0.38), new Point(iconRect.Center.X, iconRect.Y + icon * 0.66));
                ctx.DrawLine(pen, new Point(iconRect.Center.X, iconRect.Y + icon * 0.84), new Point(iconRect.Center.X, iconRect.Y + icon * 0.86));
                break;
            case InsightIcon.Check:
                ctx.DrawEllipse(null, pen, iconRect.Center, icon / 2, icon / 2);
                ctx.DrawLine(pen, new Point(iconRect.X + icon * 0.28, iconRect.Y + icon * 0.52), new Point(iconRect.X + icon * 0.45, iconRect.Y + icon * 0.68));
                ctx.DrawLine(pen, new Point(iconRect.X + icon * 0.45, iconRect.Y + icon * 0.68), new Point(iconRect.X + icon * 0.73, iconRect.Y + icon * 0.36));
                break;
            default:
                Sparkle(ctx, iconRect.Center, icon / 2, MacTheme.Solid(MacTheme.Coral));
                break;
        }
        double x = iconRect.Right + 8;
        Text(ctx, insight.Text, MacTheme.Ui(FontWeight.Bold), 10, Primary, new Rect(x, r.Y, r.Right - x - r.Height * 0.4, r.Height), middle: true);
    }

    /// <summary>
    /// Hover light: comets of light run around the block's rounded edge over a softly lit rim, in the
    /// quota's own colour, number and speed. phase 0..1 is one lap; intensity fades it in and out.
    /// </summary>
    public static void EdgeGlow(DrawingContext ctx, Rect r, double radius, double phase, MacGlow style, double intensity)
    {
        if (intensity <= 0.01)
            return;
        double breathe = phase < 0 ? 1 : 0.75 + 0.25 * Math.Sin(phase * Math.PI * 2 * 3);
        double level = breathe * intensity;
        var tint = style.Color;
        ctx.DrawRectangle(null, new Pen(MacTheme.Solid(MacTheme.Alpha(tint, 70 * level)), 7), r, radius, radius);
        ctx.DrawRectangle(null, new Pen(MacTheme.Solid(MacTheme.Alpha(MacTheme.Mix(tint, Colors.White, 0.55), 190 * level)), 1.6), r, radius, radius);
        if (phase < 0)
            return;

        var path = new RoundedPerimeter(r, radius);
        const int Steps = 48;
        double tail = path.Length * style.Tail;
        var white = MacTheme.Solid(Color.FromArgb((byte)(255 * intensity), 255, 255, 255));
        for (int c = 0; c < style.Comets; c++)
        {
            double offset = c / (double)style.Comets;
            double head = (phase + offset) % 1 * path.Length;
            var previous = path.At(head - tail);
            for (int k = 1; k <= Steps; k++)
            {
                double t = k / (double)Steps;
                var point = path.At(head - tail + tail * t);
                ctx.DrawLine(new Pen(MacTheme.Solid(MacTheme.Alpha(tint, 160 * t * t * level)), 2 + 10 * t, lineCap: PenLineCap.Round), previous, point);
                ctx.DrawLine(new Pen(MacTheme.Solid(MacTheme.Alpha(MacTheme.Mix(tint, Colors.White, 0.25 + 0.75 * t * t), 255 * t * level)), 1 + 2.4 * t, lineCap: PenLineCap.Round), previous, point);
                previous = point;
            }

            var at = path.At(head);
            double twinkle = 0.72 + 0.28 * Math.Sin((phase * 11 + offset) * Math.PI * 2);
            double burst = 14 * twinkle;
            for (int ring = 4; ring >= 1; ring--)
            {
                double size = burst * ring / 4;
                ctx.DrawEllipse(MacTheme.Solid(MacTheme.Alpha(MacTheme.Mix(tint, Colors.White, 0.6), 55 * level * (5 - ring) / 4)), null, at, size, size);
            }
            Sparkle(ctx, at, 7 * twinkle, white, phase * 720);

            for (int j = 1; j <= 4; j++)
            {
                var star = path.At(head - tail * 0.16 * j);
                double beat = 0.5 + 0.5 * Math.Sin((phase * 14 + j * 0.37 + offset) * Math.PI * 2);
                double alpha = 230 * (1 - j / 5.0) * beat * intensity;
                if (alpha < 8)
                    continue;
                double wobble = Math.Sin((phase * 6 + j) * Math.PI * 2) * 3.5;
                Sparkle(ctx, new Point(star.X + wobble, star.Y - wobble * 0.6), 3.6 - j * 0.5,
                    MacTheme.Solid(MacTheme.Alpha(MacTheme.Mix(tint, Colors.White, 0.7), alpha)), j * 40);
            }
        }
    }

    /// <summary>Points along a rounded rectangle's outline by distance, starting at the top-left corner's end.</summary>
    sealed class RoundedPerimeter(Rect r, double radius)
    {
        readonly double straightX = Math.Max(0, r.Width - 2 * radius);
        readonly double straightY = Math.Max(0, r.Height - 2 * radius);
        readonly double arc = Math.PI / 2 * radius;

        public double Length => 2 * straightX + 2 * straightY + 4 * arc;

        public Point At(double d)
        {
            d = ((d % Length) + Length) % Length;
            (double Length, Func<double, Point> Point)[] parts =
            [
                (straightX, t => new Point(r.X + radius + t, r.Y)),
                (arc, t => Corner(r.Right - radius, r.Y + radius, -90 + 90 * t / arc)),
                (straightY, t => new Point(r.Right, r.Y + radius + t)),
                (arc, t => Corner(r.Right - radius, r.Bottom - radius, 90 * t / arc)),
                (straightX, t => new Point(r.Right - radius - t, r.Bottom)),
                (arc, t => Corner(r.X + radius, r.Bottom - radius, 90 + 90 * t / arc)),
                (straightY, t => new Point(r.X, r.Bottom - radius - t)),
                (arc, t => Corner(r.X + radius, r.Y + radius, 180 + 90 * t / arc)),
            ];
            foreach (var (length, point) in parts)
            {
                if (d <= length)
                    return point(d);
                d -= length;
            }
            return new Point(r.X + radius, r.Y);
        }

        Point Corner(double cx, double cy, double degrees) =>
            new(cx + radius * Math.Cos(degrees * Math.PI / 180), cy + radius * Math.Sin(degrees * Math.PI / 180));
    }
}
