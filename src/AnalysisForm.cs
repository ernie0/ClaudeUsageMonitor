using System.Drawing.Drawing2D;

namespace ClaudeUsageMonitor;

/// <summary>Usage analysis: habits from the history, tokens from local transcripts, and Claude Code's attribution.</summary>
internal sealed class AnalysisForm : Form
{
    static AnalysisForm? open;

    readonly MonitorService service;
    readonly AnalysisView view;
    readonly Panel scroller = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = PageKit.PageTop };
    readonly List<PillButton> rangeButtons = [];
    readonly PillButton reanalyze = Theme.Button("重新分析", primary: true);
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
        view = new AnalysisView(service) { Location = Point.Empty };
        Text = "Claude 額度 · 用量分析" + (service.Demo ? "（示範資料）" : "");
        Icon = AppIcon.Load();
        BackColor = PageKit.PageTop;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1080, 820);
        MinimumSize = new Size(820, 560);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 10, 10, 4), BackColor = PageKit.PageTop };
        foreach (int range in new[] { 7, 30 })
        {
            var button = Theme.Button($"近 {range} 天");
            button.Tag = range;
            button.Click += (_, _) =>
            {
                days = range;
                Reload(scanTokens: true);
            };
            rangeButtons.Add(button);
            bar.Controls.Add(button);
        }
        reanalyze.Margin = new Padding(16, 4, 4, 4);
        reanalyze.Click += async (_, _) =>
        {
            var tokens = service.ScanTokensAsync(days);
            await service.RefreshBehaviorsAsync();
            await tokens;
            Reload(scanTokens: false);
        };
        bar.Controls.Add(reanalyze);

        scroller.Controls.Add(view);
        scroller.Resize += (_, _) => view.Width = scroller.ClientSize.Width;
        Controls.Add(scroller);
        Controls.Add(bar);

        Load += async (_, _) =>
        {
            view.Width = scroller.ClientSize.Width;
            Reload(scanTokens: true);
            // The attribution takes a few seconds; refresh it when it is missing or older than an hour.
            if (service.Behaviors == null || DateTimeOffset.UtcNow - service.Behaviors.ObservedAt > TimeSpan.FromHours(1))
                await service.RefreshBehaviorsAsync();
        };
        HandleCreated += (_, _) => Theme.ApplyWindowChrome(this);
        Action onChanged = () =>
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => Reload(scanTokens: false));
        };
        service.Changed += onChanged;
        FormClosed += (_, _) => service.Changed -= onChanged;
    }

    async void Reload(bool scanTokens)
    {
        foreach (var button in rangeButtons)
            button.Active = (int)button.Tag! == days;
        reanalyze.Enabled = !service.AnalyzingBehaviors;
        view.SetData(days);
        if (scanTokens)
        {
            await service.ScanTokensAsync(days);
            if (!IsDisposed)
                view.SetData(days);
        }
    }

    /// <summary>Lets --screenshot wait for the token scan and attribution before capturing.</summary>
    internal async Task PrepareAsync()
    {
        await service.ScanTokensAsync(days);
        await service.RefreshBehaviorsAsync();
        view.SetData(days);
    }

    /// <summary>The whole page, beyond the visible scroll area; used by --screenshot.</summary>
    internal void SaveFullPage(string path)
    {
        Application.DoEvents();
        using var bitmap = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}

/// <summary>Draws the shared analysis layout with GDI+ and grows to its full height inside the scroller.</summary>
internal sealed class AnalysisView : Control
{
    readonly MonitorService service;
    UsageAnalysis? analysis;
    TokenReport? tokens;
    int days = 7;

    public AnalysisView(MonitorService service)
    {
        this.service = service;
        DoubleBuffered = true;
        BackColor = Theme.Canvas;
        Height = 1200;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetData(int range)
    {
        days = range;
        analysis = service.Analyze(range);
        tokens = TokenReport.Build(service.Tokens, service.Current, range, DateTimeOffset.UtcNow);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Canvas);
        TilePainter.Prepare(g);
        if (analysis == null || tokens == null)
            return;
        float scale = DeviceDpi / 96f;
        using var paint = new GdiPaint(g, scale);
        float height = AnalysisRenderer.Render(paint, Width / scale, analysis, tokens, service.Behaviors, service.AnalyzingBehaviors, service.Settings);
        int pixels = (int)Math.Ceiling(height * scale);
        if (Math.Abs(pixels - Height) > 2)
            BeginInvoke(() => Height = pixels);
    }
}

/// <summary>IPaint on GDI+: logical pixels are scaled by the monitor DPI; fonts are cached per size.</summary>
internal sealed class GdiPaint(Graphics g, float scale) : IPaint, IDisposable
{
    readonly Dictionary<(float, bool), Font> fonts = [];

    RectangleF S(RectangleF r) => new(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);

    Font FontFor(float points, bool bold)
    {
        if (!fonts.TryGetValue((points, bold), out var font))
            fonts[(points, bold)] = font = new Font(Theme.UiFamily, points * 96f / 72f * scale, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        return font;
    }

    public void Fill(RectangleF r, Color color, float radius = 0)
    {
        using var brush = new SolidBrush(color);
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
        using var brush = new LinearGradientBrush(brushArea, start, end, vertical ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal);
        using var path = Theme.RoundedRect(area, radius * scale);
        g.FillPath(brush, path);
    }

    public void Stroke(RectangleF r, Color color, float width, float radius = 0)
    {
        using var pen = new Pen(color, width * scale);
        using var path = Theme.RoundedRect(S(r), radius * scale);
        g.DrawPath(pen, path);
    }

    public void Circle(PointF c, float radius, Color? fill, Color? stroke = null, float strokeWidth = 1)
    {
        var r = new RectangleF((c.X - radius) * scale, (c.Y - radius) * scale, radius * 2 * scale, radius * 2 * scale);
        if (fill is { } f)
        {
            using var brush = new SolidBrush(f);
            g.FillEllipse(brush, r);
        }
        if (stroke is { } s)
        {
            using var pen = new Pen(s, strokeWidth * scale);
            g.DrawEllipse(pen, r);
        }
    }

    public void Arc(PointF c, float radius, float startDegrees, float sweepDegrees, Color color, float width)
    {
        if (Math.Abs(sweepDegrees) < 0.1f)
            return;
        using var pen = new Pen(color, width * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
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

    sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    PointF[] Scaled(IReadOnlyList<PointF> points) => points.Select(p => new PointF(p.X * scale, p.Y * scale)).ToArray();

    public void Polyline(IReadOnlyList<PointF> points, Color color, float width)
    {
        if (points.Count < 2)
            return;
        using var pen = new Pen(color, width * scale) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, Scaled(points));
    }

    public void Polygon(IReadOnlyList<PointF> points, Color fill)
    {
        if (points.Count < 3)
            return;
        using var brush = new SolidBrush(fill);
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
        using var brush = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical);
        g.FillPolygon(brush, outline.ToArray());
    }

    public void Block(RectangleF r, Level level, float radius)
    {
        var area = S(r);
        if (area.Width >= 1 && area.Height >= 1)
            TilePainter.Block(g, area, level, radius * scale);
    }

    public void Line(PointF a, PointF b, Color color, float width, bool dashed = false)
    {
        using var pen = new Pen(color, width * scale);
        if (dashed)
            pen.DashStyle = DashStyle.Dash;
        g.DrawLine(pen, a.X * scale, a.Y * scale, b.X * scale, b.Y * scale);
    }

    public void Text(string text, RectangleF r, float points, Color color, bool bold = false, TextAlign align = TextAlign.Left, int maxLines = 1, bool middle = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = align switch { TextAlign.Center => StringAlignment.Center, TextAlign.Right => StringAlignment.Far, _ => StringAlignment.Near },
            LineAlignment = middle ? StringAlignment.Center : StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = maxLines == 1 ? StringFormatFlags.NoWrap : 0,
        };
        g.DrawString(text, FontFor(points, bold), brush, S(r), format);
    }

    public float Measure(string text, float points, bool bold = false) => g.MeasureString(text, FontFor(points, bold)).Width / scale;

    public void Dispose()
    {
        foreach (var font in fonts.Values)
            font.Dispose();
    }
}
