using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ClaudeUsageMonitor;

/// <summary>Usage analysis: habits from the history, tokens from local transcripts, and Claude Code's attribution.</summary>
internal sealed class AnalysisForm : Form
{
    static AnalysisForm? open;

    readonly MonitorService service;
    readonly AnalysisView view;
    readonly Panel scroller = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = PageKit.PageTop };
    int days = 7;

    public static void Open(MonitorService service)
    {
        if (open is { IsDisposed: false })
        {
            open.Activate();
            return;
        }
        open = new AnalysisForm(service);
        open.Show();
    }

    internal AnalysisForm(MonitorService service)
    {
        this.service = service;
        view = new AnalysisView(this) { Location = Point.Empty };
        Text = "Claude 額度 · 用量分析" + (service.Demo ? "（示範資料）" : "");
        Icon = AppIcon.Load();
        BackColor = PageKit.PageTop;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 860);
        MinimumSize = new Size(900, 560);

        scroller.Controls.Add(view);
        scroller.Resize += (_, _) => view.Width = scroller.ClientSize.Width;
        Controls.Add(scroller);

        Load += async (_, _) =>
        {
            view.Width = scroller.ClientSize.Width;
            view.PlayIntro();
            Reload(scanTokens: true);
            // The attribution takes a few seconds; refresh it when it is missing or older than an hour.
            if (service.Behaviors == null || DateTimeOffset.UtcNow - service.Behaviors.ObservedAt > TimeSpan.FromHours(1))
                await service.RefreshBehaviorsAsync();
        };
        HandleCreated += (_, _) =>
        {
            Theme.ApplyWindowChrome(this);
            Theme.DarkScrollbars(scroller);
        };
        Action onChanged = () =>
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => Reload(scanTokens: false));
        };
        service.Changed += onChanged;
        FormClosed += (_, _) => service.Changed -= onChanged;
    }

    internal MonitorService Service => service;
    internal int Days => days;
    internal AnalysisView View => view;

    async void Reload(bool scanTokens)
    {
        var scan = scanTokens ? service.ScanTokensAsync(days) : null;
        // Shows "reading" (the sunburst turns) while the transcripts are read.
        view.SetData();
        if (scan == null)
            return;
        await scan;
        if (!IsDisposed)
            view.SetData();
    }

    internal async void Act(string action)
    {
        if (action.StartsWith("range:") && int.Parse(action["range:".Length..]) is int range && range != days)
        {
            days = range;
            // The page plays its entrance again for the new range; the header stays put.
            view.Motion.Restart("top");
            Reload(scanTokens: true);
        }
        else if (action == "reanalyze" && !service.AnalyzingBehaviors)
        {
            var tokens = service.ScanTokensAsync(days);
            await service.RefreshBehaviorsAsync();
            await tokens;
            Reload(scanTokens: false);
        }
    }

    /// <summary>Scrolls the page; used by --screenshot.</summary>
    internal void ScrollTo(int y)
    {
        scroller.AutoScrollPosition = new Point(0, y);
        Application.DoEvents();
    }

    /// <summary>Lets --screenshot wait for the token scan and attribution before capturing.</summary>
    internal async Task PrepareAsync()
    {
        await service.ScanTokensAsync(days);
        await service.RefreshBehaviorsAsync();
        view.SetData();
    }

    /// <summary>The whole page, beyond the visible scroll area; used by --screenshot.</summary>
    internal void SaveFullPage(string path)
    {
        Application.DoEvents();
        using var bitmap = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size));
        bitmap.Save(path, ImageFormat.Png);
    }
}

/// <summary>Draws the shared analysis layout with GDI+ and grows to its full height inside the scroller.</summary>
internal sealed class AnalysisView : GlassView
{
    readonly AnalysisForm owner;
    AnalysisData? data;

    public AnalysisView(AnalysisForm owner)
    {
        this.owner = owner;
        Height = 1200;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetData()
    {
        var service = owner.Service;
        // A new attribution from Claude Code plays its section's entrance again.
        if (data != null && !ReferenceEquals(data.Behaviors, service.Behaviors))
            Motion.Replay("attr");
        data = new AnalysisData(owner.Days, service.Analyze(owner.Days), TokenReport.Build(service.Tokens, service.Current, owner.Days, DateTimeOffset.UtcNow),
            service.Behaviors, service.AnalyzingBehaviors, service.Settings);
        Invalidate();
    }

    protected override void Draw(IPaint paint, float width)
    {
        if (data == null)
            return;
        float height = AnalysisRenderer.Render(paint, width, State, data);
        int pixels = (int)Math.Ceiling(height * UiScale);
        if (Math.Abs(pixels - Height) > 2)
            BeginInvoke(() => Height = pixels);
    }

    protected override void Act(string action) => owner.Act(action);
}

/// <summary>IPaint on GDI+: logical pixels are scaled by the monitor DPI; fonts are cached per size.</summary>
internal sealed class GdiPaint(Graphics g, float scale, PaintCache? cache = null) : IPaint, IDisposable
{
    readonly Dictionary<(float, bool), Font> fonts = [];
    float opacity = 1;

    RectangleF S(RectangleF r) => new(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);

    /// <summary>The colour with the current fade applied.</summary>
    Color A(Color c) => opacity >= 1 ? c : Color.FromArgb((int)(c.A * opacity), c.R, c.G, c.B);

    Font FontFor(float points, bool bold)
    {
        if (!fonts.TryGetValue((points, bold), out var font))
            fonts[(points, bold)] = font = new Font(Theme.UiFamily, points * 96f / 72f * scale, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        return font;
    }

    public void Fill(RectangleF r, Color color, float radius = 0)
    {
        using var brush = new SolidBrush(A(color));
        if (radius <= 0)
        {
            g.FillRectangle(brush, S(r));
            return;
        }
        using var path = Theme.RoundedRect(S(r), radius * scale);
        g.FillPath(brush, path);
    }

    public void Gradient(RectangleF r, Color start, Color end, float radius = 0, bool vertical = false)
    {
        var area = S(r);
        if (area.Width < 1 || area.Height < 1)
            return;
        // Inflate the brush rectangle by a pixel so GDI+ does not wrap the gradient at the edges.
        var brushArea = RectangleF.Inflate(area, 1, 1);
        using var brush = new LinearGradientBrush(brushArea, A(start), A(end), vertical ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal);
        using var path = Theme.RoundedRect(area, radius * scale);
        g.FillPath(brush, path);
    }

    public void Stroke(RectangleF r, Color color, float width, float radius = 0)
    {
        using var pen = new Pen(A(color), width * scale);
        using var path = Theme.RoundedRect(S(r), radius * scale);
        g.DrawPath(pen, path);
    }

    public void Circle(PointF c, float radius, Color? fill, Color? stroke = null, float strokeWidth = 1)
    {
        var r = new RectangleF((c.X - radius) * scale, (c.Y - radius) * scale, radius * 2 * scale, radius * 2 * scale);
        if (fill is { } f)
        {
            using var brush = new SolidBrush(A(f));
            g.FillEllipse(brush, r);
        }
        if (stroke is { } s)
        {
            using var pen = new Pen(A(s), strokeWidth * scale);
            g.DrawEllipse(pen, r);
        }
    }

    public void Arc(PointF c, float radius, float startDegrees, float sweepDegrees, Color color, float width)
    {
        if (Math.Abs(sweepDegrees) < 0.1f)
            return;
        using var pen = new Pen(A(color), width * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, (c.X - radius) * scale, (c.Y - radius) * scale, radius * 2 * scale, radius * 2 * scale, startDegrees, sweepDegrees);
    }

    public IDisposable Clip(RectangleF r, float radius)
    {
        var previous = g.Clip;
        using (var path = Theme.RoundedRect(S(r), radius * scale))
            g.SetClip(path, CombineMode.Intersect);
        return new Restore(() =>
        {
            g.Clip = previous;
            previous.Dispose();
        });
    }

    public IDisposable Transform(float dx, float dy, float zoom = 1, float degrees = 0, PointF origin = default)
    {
        if (dx == 0 && dy == 0 && zoom == 1 && degrees == 0)
            return PageKit.Scope.None;
        var saved = g.Save();
        g.TranslateTransform((origin.X + dx) * scale, (origin.Y + dy) * scale);
        if (degrees != 0)
            g.RotateTransform(degrees);
        if (zoom != 1)
            g.ScaleTransform(zoom, zoom);
        g.TranslateTransform(-origin.X * scale, -origin.Y * scale);
        return new Restore(() => g.Restore(saved));
    }

    public IDisposable Fade(float value)
    {
        if (value >= 1)
            return PageKit.Scope.None;
        float previous = opacity;
        opacity *= Math.Max(0, value);
        return new Restore(() => opacity = previous);
    }

    public void Cached(string key, SizeF size, Action<IPaint> draw)
    {
        if (cache == null)
        {
            draw(this);
            return;
        }
        var picture = cache.Get(key, new Size((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)), bitmap =>
        {
            using var inner = Graphics.FromImage(bitmap);
            TilePainter.Prepare(inner);
            using var paint = new GdiPaint(inner, scale);
            draw(paint);
        });
        // Copied pixel for pixel: the backdrop is opaque, so no blending or smoothing is needed.
        var (mode, interpolation, offset) = (g.CompositingMode, g.InterpolationMode, g.PixelOffsetMode);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(picture, new Rectangle(0, 0, picture.Width, picture.Height), 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel);
        (g.CompositingMode, g.InterpolationMode, g.PixelOffsetMode) = (mode, interpolation, offset);
    }

    sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    PointF[] Scaled(IReadOnlyList<PointF> points) => points.Select(p => new PointF(p.X * scale, p.Y * scale)).ToArray();

    public void Polyline(IReadOnlyList<PointF> points, Color color, float width)
    {
        if (points.Count < 2)
            return;
        using var pen = new Pen(A(color), width * scale) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, Scaled(points));
    }

    public void Polygon(IReadOnlyList<PointF> points, Color fill)
    {
        if (points.Count < 3)
            return;
        using var brush = new SolidBrush(A(fill));
        g.FillPolygon(brush, Scaled(points));
    }

    public void Area(IReadOnlyList<PointF> points, float baseline, Color top, Color bottom)
    {
        if (points.Count < 2)
            return;
        var outline = Scaled(points).ToList();
        outline.Add(new PointF(outline[^1].X, baseline * scale));
        outline.Add(new PointF(outline[0].X, baseline * scale));
        float minY = outline.Min(p => p.Y);
        var bounds = RectangleF.FromLTRB(outline.Min(p => p.X), minY - 1, outline.Max(p => p.X) + 1, baseline * scale + 1);
        using var brush = new LinearGradientBrush(bounds, A(top), A(bottom), LinearGradientMode.Vertical);
        g.FillPolygon(brush, outline.ToArray());
    }

    public void Line(PointF a, PointF b, Color color, float width, bool dashed = false)
    {
        using var pen = new Pen(A(color), width * scale);
        if (dashed)
            pen.DashStyle = DashStyle.Dash;
        g.DrawLine(pen, a.X * scale, a.Y * scale, b.X * scale, b.Y * scale);
    }

    public void Text(string text, RectangleF r, float points, Color color, bool bold = false, TextAlign align = TextAlign.Left, int maxLines = 1, bool middle = false)
    {
        using var brush = new SolidBrush(A(color));
        using var format = new StringFormat
        {
            Alignment = align switch { TextAlign.Center => StringAlignment.Center, TextAlign.Right => StringAlignment.Far, _ => StringAlignment.Near },
            LineAlignment = middle ? StringAlignment.Center : StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = maxLines == 1 ? StringFormatFlags.NoWrap : 0,
        };
        // ClearType ignores transparency; while fading in, text is smoothed in grey so it fades with the rest.
        var hint = g.TextRenderingHint;
        if (opacity < 1)
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawString(text, FontFor(points, bold), brush, S(r), format);
        g.TextRenderingHint = hint;
    }

    public float Measure(string text, float points, bool bold = false) => g.MeasureString(text, FontFor(points, bold)).Width / scale;

    public void Dispose()
    {
        foreach (var font in fonts.Values)
            font.Dispose();
    }
}
