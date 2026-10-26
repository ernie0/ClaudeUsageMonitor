using System.Drawing.Imaging;

namespace ClaudeUsageMonitor;

/// <summary>
/// Hosts a page drawn by the shared Core layout: DPI scaling, the kept backdrop, hover and clicks, and the
/// frame timer, which runs only while the page's Motion has something moving and then repaints only what
/// moved. A page at rest never redraws.
/// </summary>
internal abstract class GlassView : Control
{
    readonly System.Windows.Forms.Timer frame = new() { Interval = 15 };
    readonly PaintCache cache = new();
    protected readonly PageState State = new();

    protected GlassView()
    {
        DoubleBuffered = true;
        BackColor = PageKit.PageTop;
        State.Motion.Enabled = Theme.AnimationsEnabled;
        frame.Tick += (_, _) =>
        {
            if (State.Motion.Active)
                Invalidate(Pixels(State.Motion.Dirty));
            else
                frame.Stop();
        };
        Disposed += (_, _) =>
        {
            frame.Dispose();
            cache.Dispose();
        };
    }

    public Motion Motion => State.Motion;

    protected float UiScale => DeviceDpi / 96f;

    /// <summary>Visible height in logical pixels (the scroller's, not the page's).</summary>
    protected float ViewHeight => (Parent?.ClientSize.Height ?? Height) / UiScale;

    /// <summary>Draws the page in logical pixels.</summary>
    protected abstract void Draw(IPaint paint, float width);

    protected abstract void Act(string action);

    /// <summary>Plays the entrance again.</summary>
    public void PlayIntro()
    {
        Motion.Restart();
        Invalidate();
    }

    /// <summary>Shows every entrance at its end at once; used by --screenshot.</summary>
    public void Settle()
    {
        Motion.Settle();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        TilePainter.Prepare(g);
        float s = UiScale;
        // The part of the page the scroller shows; sections below it enter when they scroll in.
        Motion.Viewport = new RectangleF(0, -Top / s, Width / s, ViewHeight);
        using (var paint = new GdiPaint(g, s, cache))
            Draw(paint, Width / s);
        if (Motion.Active)
            frame.Start();
    }

    Rectangle Pixels(RectangleF r)
    {
        float s = UiScale;
        return Rectangle.Ceiling(new RectangleF((r.X - 24) * s, (r.Y - 24) * s, (r.Width + 48) * s, (r.Height + 48) * s));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = new PointF(e.X / UiScale, e.Y / UiScale);
        string? hit = State.HitAt(point);
        Cursor = PageState.Clickable(hit) ? Cursors.Hand : Cursors.Default;
        State.Pointer = point;
        if (hit != State.Hover)
        {
            State.Hover = hit;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        State.Pointer = null;
        if (State.Hover != null)
        {
            State.Hover = null;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && State.HitAt(new PointF(e.X / UiScale, e.Y / UiScale)) is { } action && PageState.Clickable(action))
            Act(action);
    }

    /// <summary>Acts as if the pointer were over <paramref name="action"/>'s area (null: off the page); used by --screenshot.</summary>
    internal void PointAt(string? action)
    {
        State.Hover = action;
        Invalidate();
    }
}

/// <summary>One kept picture (the page backdrop) per view, rebuilt when its key or size changes.</summary>
internal sealed class PaintCache : IDisposable
{
    string key = "";
    Bitmap? bitmap;

    public Bitmap Get(string key, Size size, Action<Bitmap> draw)
    {
        if (bitmap == null || this.key != key || bitmap.Size != size)
        {
            bitmap?.Dispose();
            bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
            this.key = key;
            draw(bitmap);
        }
        return bitmap;
    }

    public void Dispose()
    {
        bitmap?.Dispose();
        bitmap = null;
    }
}
