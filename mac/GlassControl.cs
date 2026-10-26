using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SD = System.Drawing;

namespace ClaudeUsageMonitor;

/// <summary>
/// Hosts a page drawn by the shared Core layout: the kept backdrop, hover and clicks, and the frame timer,
/// which runs only while the page's Motion has something moving. A page at rest never redraws.
/// </summary>
internal abstract class GlassControl : Control
{
    readonly DispatcherTimer frame = new() { Interval = TimeSpan.FromMilliseconds(15) };
    readonly PictureCache cache = new();
    protected readonly PageState State = new();
    ScrollViewer? scroller;

    protected GlassControl()
    {
        State.Motion.Enabled = MacShell.AnimationsEnabled;
        frame.Tick += (_, _) =>
        {
            if (State.Motion.Active)
                InvalidateVisual();
            else
                frame.Stop();
        };
        // Sections below the window enter when they scroll into view.
        AttachedToVisualTree += (_, _) =>
        {
            scroller = this.FindAncestorOfType<ScrollViewer>();
            if (scroller != null)
                scroller.ScrollChanged += OnScrolled;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            frame.Stop();
            if (scroller != null)
                scroller.ScrollChanged -= OnScrolled;
            cache.Dispose();
        };
    }

    void OnScrolled(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    public Motion Motion => State.Motion;

    /// <summary>Visible height (the scroller's, not the page's).</summary>
    protected double ViewHeight => scroller?.Viewport.Height ?? Bounds.Height;

    /// <summary>Draws the page in logical pixels.</summary>
    protected abstract void Draw(IPaint paint, float width);

    protected abstract void Act(string action);

    /// <summary>Plays the entrance again.</summary>
    public void PlayIntro()
    {
        Motion.Restart();
        InvalidateVisual();
    }

    /// <summary>Shows every entrance at its end at once; used by --screenshot.</summary>
    public void Settle()
    {
        Motion.Settle();
        InvalidateVisual();
    }

    /// <summary>Acts as if the pointer were over <paramref name="action"/>'s area (null: off the page); used by --screenshot.</summary>
    public void PointAt(string? action)
    {
        State.Hover = action;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        Motion.Viewport = new SD.RectangleF(0, (float)(scroller?.Offset.Y ?? 0), (float)Bounds.Width, (float)ViewHeight);
        Draw(new AvaloniaPaint(ctx, cache, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1), (float)Bounds.Width);
        if (Motion.Active && !frame.IsEnabled)
            Dispatcher.UIThread.Post(frame.Start);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        var point = new SD.PointF((float)at.X, (float)at.Y);
        string? hit = State.HitAt(point);
        Cursor = PageState.Clickable(hit) ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        // The part that follows the pointer (a chart crosshair) repaints as the pointer moves.
        bool follows = State.PointerArea.Contains(point) || State.Pointer is { } before && State.PointerArea.Contains(before);
        State.Pointer = point;
        if (follows)
            InvalidateVisual();
        if (hit != State.Hover)
        {
            State.Hover = hit;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        State.Pointer = null;
        State.Hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var at = e.GetPosition(this);
        if (State.HitAt(new SD.PointF((float)at.X, (float)at.Y)) is { } action && PageState.Clickable(action))
            Act(action);
    }
}

/// <summary>One kept picture (the page backdrop) per page, rebuilt when its key or size changes.</summary>
internal sealed class PictureCache : IDisposable
{
    string key = "";
    PixelSize size;
    RenderTargetBitmap? bitmap;

    public RenderTargetBitmap Get(string key, PixelSize size, double scaling, Action<DrawingContext> draw)
    {
        if (bitmap == null || this.key != key || this.size != size)
        {
            bitmap?.Dispose();
            bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            using (var ctx = bitmap.CreateDrawingContext())
                draw(ctx);
            this.key = key;
            this.size = size;
        }
        return bitmap;
    }

    public void Dispose()
    {
        bitmap?.Dispose();
        bitmap = null;
    }
}

/// <summary>IPaint on Avalonia's DrawingContext (logical pixels map 1:1 to DIPs).</summary>
internal sealed class AvaloniaPaint(DrawingContext ctx, PictureCache? cache = null, double scaling = 1) : IPaint
{
    static Rect R(SD.RectangleF r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

    static Color C(SD.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public void Fill(SD.RectangleF r, SD.Color color, float radius = 0) =>
        ctx.DrawRectangle(MacTheme.Solid(C(color)), null, R(r), radius, radius);

    public void Gradient(SD.RectangleF r, SD.Color start, SD.Color end, float radius = 0, bool vertical = false) =>
        ctx.DrawRectangle(vertical ? MacTheme.Vertical(C(start), C(end)) : MacTheme.Horizontal(C(start), C(end)), null, R(r), radius, radius);

    public IDisposable Clip(SD.RectangleF r, float radius) => ctx.PushClip(new RoundedRect(R(r), radius));

    public IDisposable Transform(float dx, float dy, float scale = 1, float degrees = 0, SD.PointF origin = default)
    {
        if (dx == 0 && dy == 0 && scale == 1 && degrees == 0)
            return PageKit.Scope.None;
        var matrix = Matrix.CreateTranslation(-origin.X, -origin.Y)
            * Matrix.CreateScale(scale, scale)
            * Matrix.CreateRotation(degrees * Math.PI / 180)
            * Matrix.CreateTranslation(origin.X + dx, origin.Y + dy);
        return ctx.PushTransform(matrix);
    }

    public IDisposable Fade(float opacity) => opacity >= 1 ? PageKit.Scope.None : (IDisposable)ctx.PushOpacity(Math.Max(0, opacity));

    public void Cached(string key, SD.SizeF size, Action<IPaint> draw)
    {
        if (cache == null)
        {
            draw(this);
            return;
        }
        var pixels = new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling));
        var picture = cache.Get(key, pixels, scaling, inner => draw(new AvaloniaPaint(inner)));
        ctx.DrawImage(picture, new Rect(0, 0, size.Width, size.Height));
    }

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
