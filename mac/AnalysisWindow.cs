using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SD = System.Drawing;

namespace ClaudeUsageMonitor;

/// <summary>Usage analysis (same page as the Windows app, drawn by the shared AnalysisRenderer).</summary>
internal sealed class AnalysisWindow : Window
{
    readonly MonitorService service;
    readonly AnalysisPageView view;
    readonly StackPanel rangeBar = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly Button reanalyze = new() { Content = "重新分析", Classes = { "accent" }, Margin = new Thickness(16, 0, 0, 0) };
    int days = 7;

    public AnalysisWindow(MonitorService service)
    {
        this.service = service;
        view = new AnalysisPageView(service);
        Title = "Claude 額度 · 用量分析" + (service.Demo ? "（示範資料）" : "");
        Icon = MacShell.AppIcon();
        Width = 1000;
        Height = 780;
        MinWidth = 820;
        MinHeight = 560;
        Background = MacTheme.Solid(Color.FromRgb(0x0B, 0x10, 0x22));
        FontFamily = MacTheme.UiFont;

        foreach (int range in new[] { 7, 30 })
        {
            var button = new Button { Content = $"近 {range} 天", Tag = range };
            button.Click += (_, _) =>
            {
                days = range;
                Reload(scanTokens: true);
            };
            rangeBar.Children.Add(button);
        }
        reanalyze.Click += async (_, _) =>
        {
            var tokens = service.ScanTokensAsync(days);
            await service.RefreshBehaviorsAsync();
            await tokens;
            Reload(scanTokens: false);
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 12, 12, 6), Children = { rangeBar, reanalyze } });
        var scroller = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);
        Content = grid;

        Opened += async (_, _) =>
        {
            Reload(scanTokens: true);
            if (service.Behaviors == null || DateTimeOffset.UtcNow - service.Behaviors.ObservedAt > TimeSpan.FromHours(1))
                await service.RefreshBehaviorsAsync();
        };
        Action onChanged = () => Dispatcher.UIThread.Post(() => Reload(scanTokens: false));
        service.Changed += onChanged;
        Closed += (_, _) => service.Changed -= onChanged;
    }

    async void Reload(bool scanTokens)
    {
        foreach (var button in rangeBar.Children.OfType<Button>())
            button.Classes.Set("accent", (int)button.Tag! == days);
        reanalyze.IsEnabled = !service.AnalyzingBehaviors;
        view.SetData(days);
        if (scanTokens)
        {
            await service.ScanTokensAsync(days);
            view.SetData(days);
        }
    }

    /// <summary>Waits for the token scan and attribution; used by --screenshot.</summary>
    internal async Task PrepareAsync()
    {
        await service.ScanTokensAsync(days);
        await service.RefreshBehaviorsAsync();
        view.SetData(days);
    }

    internal AnalysisPageView Page => view;
}

/// <summary>Hosts the shared analysis layout; its height follows what the layout used.</summary>
internal sealed class AnalysisPageView : Control
{
    readonly MonitorService service;
    UsageAnalysis? analysis;
    TokenReport? tokens;
    double pageHeight = 1200;

    public AnalysisPageView(MonitorService service)
    {
        this.service = service;
    }

    public void SetData(int days)
    {
        analysis = service.Analyze(days);
        tokens = TokenReport.Build(service.Tokens, service.Current, days, DateTimeOffset.UtcNow);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width, pageHeight);

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(MacTheme.Solid(MacTheme.Canvas), null, new Rect(Bounds.Size));
        if (analysis == null || tokens == null)
            return;
        double height = AnalysisRenderer.Render(new AvaloniaPaint(ctx), (float)Bounds.Width, analysis, tokens, service.Behaviors, service.AnalyzingBehaviors, service.Settings);
        if (Math.Abs(height - pageHeight) > 2)
        {
            pageHeight = height;
            Dispatcher.UIThread.Post(InvalidateMeasure);
        }
    }
}

/// <summary>IPaint on Avalonia's DrawingContext (logical pixels map 1:1 to DIPs).</summary>
internal sealed class AvaloniaPaint(DrawingContext ctx) : IPaint
{
    static Rect R(SD.RectangleF r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

    static Color C(SD.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public void Fill(SD.RectangleF r, SD.Color color, float radius = 0) =>
        ctx.DrawRectangle(MacTheme.Solid(C(color)), null, R(r), radius, radius);

    public void Gradient(SD.RectangleF r, SD.Color start, SD.Color end, float radius = 0, bool vertical = false) =>
        ctx.DrawRectangle(vertical ? MacTheme.Vertical(C(start), C(end)) : MacTheme.Horizontal(C(start), C(end)), null, R(r), radius, radius);

    public IDisposable Clip(SD.RectangleF r, float radius) => ctx.PushClip(new RoundedRect(R(r), radius));

    public void Stroke(SD.RectangleF r, SD.Color color, float width, float radius = 0) =>
        ctx.DrawRectangle(null, new Pen(MacTheme.Solid(C(color)), width), R(r), radius, radius);

    public void Circle(SD.PointF c, float radius, SD.Color? fill, SD.Color? stroke = null, float strokeWidth = 1) =>
        ctx.DrawEllipse(fill is { } f ? MacTheme.Solid(C(f)) : null, stroke is { } s ? new Pen(MacTheme.Solid(C(s)), strokeWidth) : null,
            new Point(c.X, c.Y), radius, radius);

    public void Arc(SD.PointF c, float radius, float startDegrees, float sweepDegrees, SD.Color color, float width)
    {
        if (Math.Abs(sweepDegrees) < 0.1f)
            return;
        var pen = new Pen(MacTheme.Solid(C(color)), width, lineCap: PenLineCap.Round);
        if (Math.Abs(sweepDegrees) >= 359.9f)
            ctx.DrawEllipse(null, pen, new Point(c.X, c.Y), radius, radius);
        else
            ctx.DrawGeometry(null, pen, MacDraw.Arc(new Point(c.X, c.Y), radius, startDegrees, sweepDegrees));
    }

    static StreamGeometry Path(IReadOnlyList<SD.PointF> points, bool closed, float? baseline = null)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(points[0].X, points[0].Y), closed);
        for (int i = 1; i < points.Count; i++)
            g.LineTo(new Point(points[i].X, points[i].Y));
        if (baseline is { } b)
        {
            g.LineTo(new Point(points[^1].X, b));
            g.LineTo(new Point(points[0].X, b));
        }
        g.EndFigure(closed);
        return geometry;
    }

    public void Polyline(IReadOnlyList<SD.PointF> points, SD.Color color, float width)
    {
        if (points.Count >= 2)
            ctx.DrawGeometry(null, new Pen(MacTheme.Solid(C(color)), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Path(points, false));
    }

    public void Polygon(IReadOnlyList<SD.PointF> points, SD.Color fill)
    {
        if (points.Count >= 3)
            ctx.DrawGeometry(MacTheme.Solid(C(fill)), null, Path(points, true));
    }

    public void Area(IReadOnlyList<SD.PointF> points, float baseline, SD.Color top, SD.Color bottom)
    {
        if (points.Count >= 2)
            ctx.DrawGeometry(MacTheme.Vertical(C(top), C(bottom)), null, Path(points, true, baseline));
    }

    public void Block(SD.RectangleF r, Level level, float radius)
    {
        if (r.Width >= 1 && r.Height >= 1)
            MacDraw.Block(ctx, R(r), level, radius);
    }

    public void Line(SD.PointF a, SD.PointF b, SD.Color color, float width, bool dashed = false) =>
        ctx.DrawLine(new Pen(MacTheme.Solid(C(color)), width, dashed ? new DashStyle([4, 3], 0) : null), new Point(a.X, a.Y), new Point(b.X, b.Y));

    public void Text(string text, SD.RectangleF r, float points, SD.Color color, bool bold = false, TextAlign align = TextAlign.Left, int maxLines = 1, bool middle = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            MacTheme.Ui(bold ? FontWeight.Bold : FontWeight.Normal), MacDraw.Pt(points), MacTheme.Solid(C(color)))
        {
            MaxTextWidth = Math.Max(1, r.Width),
            MaxLineCount = maxLines,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
        };
        double y = middle ? r.Y + (r.Height - formatted.Height) / 2 : r.Y;
        ctx.DrawText(formatted, new Point(r.X, y));
    }

    public float Measure(string text, float points, bool bold = false) =>
        (float)MacDraw.Measure(text, MacTheme.Ui(bold ? FontWeight.Bold : FontWeight.Normal), points);
}
