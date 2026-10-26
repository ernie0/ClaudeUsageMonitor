using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ClaudeUsageMonitor;

internal static class Theme
{
    // Deep indigo canvas so the coloured quota blocks stand out.
    public static readonly Color Canvas = Color.FromArgb(0x14, 0x12, 0x1E);
    public static readonly Color CanvasEnd = Color.FromArgb(0x1E, 0x1A, 0x33);
    public static readonly Color Surface = Color.FromArgb(0x1E, 0x1B, 0x2E);
    public static readonly Color Raised = Color.FromArgb(0x2B, 0x27, 0x42);
    public static readonly Color Border = Color.FromArgb(0x3D, 0x38, 0x5C);
    public static readonly Color Text = Color.FromArgb(0xF4, 0xF2, 0xFF);
    public static readonly Color Muted = Color.FromArgb(0xA9, 0xA4, 0xC4);
    public static readonly Color Track = Color.FromArgb(0x33, 0x2F, 0x4D);

    // Brand: Claude coral into an "AI" violet.
    public static readonly Color Coral = Color.FromArgb(0xE8, 0x82, 0x5F);
    public static readonly Color Violet = Color.FromArgb(0x9F, 0x7A, 0xEA);
    public static readonly Color Accent = Coral;

    public static readonly Color Warning = Color.FromArgb(0xFB, 0xBF, 0x24);
    public static readonly Color Danger = Color.FromArgb(0xFB, 0x71, 0x85);
    public static readonly Color Good = Color.FromArgb(0x34, 0xD3, 0x99);

    public const string UiFamily = "Microsoft JhengHei UI";
    public const string NumberFamily = "Segoe UI";

    public static Font Ui(float points = 9f, FontStyle style = FontStyle.Regular) => new(UiFamily, points, style);

    /// <summary>Gradient stops; every pair keeps white text above 5:1 contrast.</summary>
    public static (Color Start, Color End) Gradient(Level level) => level switch
    {
        Level.Plenty => (Color.FromArgb(0x04, 0x78, 0x57), Color.FromArgb(0x0E, 0x74, 0x90)),
        Level.Fair => (Color.FromArgb(0x4F, 0x46, 0xE5), Color.FromArgb(0x7C, 0x3A, 0xED)),
        Level.Low => (Color.FromArgb(0xB4, 0x53, 0x09), Color.FromArgb(0xC2, 0x41, 0x0C)),
        Level.Critical => (Color.FromArgb(0xBE, 0x12, 0x3C), Color.FromArgb(0x9F, 0x12, 0x39)),
        _ => (Color.FromArgb(0x47, 0x55, 0x69), Color.FromArgb(0x33, 0x41, 0x55)),
    };

    /// <summary>A bright version of the level colour for lines and text on the dark canvas.</summary>
    public static Color Bright(Level level) => level switch
    {
        Level.Plenty => Good,
        Level.Fair => Color.FromArgb(0xA5, 0xB4, 0xFC),
        Level.Low => Warning,
        Level.Critical => Danger,
        _ => Muted,
    };

    public static Color ForRemaining(double remaining, Settings settings) => Bright(Levels.Of(remaining, settings));

    /// <summary>Follows Windows' "Animation effects" switch (Settings → Accessibility → Visual effects).</summary>
    public static bool AnimationsEnabled
    {
        get
        {
            try
            {
                return !SystemParametersInfo(0x1042, 0, out bool on, 0) || on; // SPI_GETCLIENTAREAANIMATION
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return true;
            }
        }
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Alpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);

    /// <summary>Dark title bar and rounded corners on Windows 11; silently ignored elsewhere.</summary>
    public static void ApplyWindowChrome(Form form, bool rounded = false)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int));
            if (rounded)
            {
                int round = 2;
                DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    public static PillButton Button(string text, bool primary = false) => new() { Text = text, Primary = primary };

    /// <summary>Dark header, dark scrollbars, and a last column that fills the remaining width.</summary>
    public static void StyleList(ListView list)
    {
        list.BackColor = Surface;
        list.ForeColor = Text;
        list.OwnerDraw = true;
        list.DrawColumnHeader += (_, e) =>
        {
            using var back = new SolidBrush(Raised);
            e.Graphics.FillRectangle(back, e.Bounds);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding
                | (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, list.Font, e.Bounds, Muted, flags);
        };
        list.DrawItem += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) => e.DrawDefault = true;
        list.HandleCreated += (_, _) => DarkScrollbars(list);
        list.Resize += (_, _) => FitLastColumn(list);
    }

    public static void FitLastColumn(ListView list)
    {
        if (list.Columns.Count == 0)
            return;
        // The list adds its vertical scrollbar after the items arrive, so measure once it has done so;
        // ClientSize then excludes the scrollbar and the columns never force a horizontal one.
        void Fit()
        {
            int others = list.Columns.Cast<ColumnHeader>().Take(list.Columns.Count - 1).Sum(c => c.Width);
            list.Columns[^1].Width = Math.Max(80, list.ClientSize.Width - others);
        }
        if (list.IsHandleCreated)
            list.BeginInvoke(Fit);
        else
            Fit();
    }

    /// <summary>"DarkMode_Explorer" for lists, "DarkMode_CFD" for combo boxes (Windows 10 1809+).</summary>
    public static void DarkScrollbars(Control control, string theme = "DarkMode_Explorer")
    {
        try
        {
            SetWindowTheme(control.Handle, theme, null);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
}

/// <summary>Rounded pill button; the active or primary one carries the coral-to-violet brand gradient.</summary>
internal sealed class PillButton : Button
{
    bool active, hover;

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = true;
        Padding = new Padding(12, 4, 12, 4);
        Margin = new Padding(4);
        Cursor = Cursors.Hand;
        ForeColor = Theme.Text;
        BackColor = Color.Transparent;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Ui(9.5f);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => active;
        set
        {
            active = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundedRect(r, r.Height / 2);
        if (active || Primary)
        {
            using var brush = new LinearGradientBrush(r, Theme.Coral, Theme.Violet, LinearGradientMode.Horizontal);
            g.FillPath(brush, path);
            if (hover)
            {
                using var glow = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                g.FillPath(glow, path);
            }
        }
        else
        {
            using var brush = new SolidBrush(hover ? Theme.Border : Theme.Raised);
            g.FillPath(brush, path);
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), Enabled ? Theme.Text : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>Small vector icons, so meaning does not depend on reading the text.</summary>
internal static class Glyphs
{
    /// <summary>The four-pointed "AI" sparkle.</summary>
    public static void Sparkle(Graphics g, PointF c, float size, Brush brush, float rotation = 0)
    {
        using var path = new GraphicsPath();
        var points = new PointF[8];
        for (int i = 0; i < 8; i++)
        {
            double angle = (rotation + i * 45 - 90) * Math.PI / 180;
            float radius = i % 2 == 0 ? size : size * 0.36f;
            points[i] = new PointF(c.X + (float)Math.Cos(angle) * radius, c.Y + (float)Math.Sin(angle) * radius);
        }
        path.AddClosedCurve(points, 0.12f);
        g.FillPath(brush, path);
    }

    public static void Clock(Graphics g, RectangleF r, Color color, float width)
    {
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, r);
        var c = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
        g.DrawLine(pen, c, new PointF(c.X, r.Y + r.Height * 0.24f));
        g.DrawLine(pen, c, new PointF(r.X + r.Width * 0.72f, c.Y));
    }

    public static void Calendar(Graphics g, RectangleF r, Color color, float width)
    {
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var body = new RectangleF(r.X, r.Y + r.Height * 0.14f, r.Width, r.Height * 0.86f);
        using (var path = Theme.RoundedRect(body, r.Width * 0.18f))
            g.DrawPath(pen, path);
        g.DrawLine(pen, body.X, body.Y + body.Height * 0.32f, body.Right, body.Y + body.Height * 0.32f);
        g.DrawLine(pen, r.X + r.Width * 0.3f, r.Y, r.X + r.Width * 0.3f, r.Y + r.Height * 0.26f);
        g.DrawLine(pen, r.X + r.Width * 0.7f, r.Y, r.X + r.Width * 0.7f, r.Y + r.Height * 0.26f);
    }

    public static void Chip(Graphics g, RectangleF r, Color color, float width)
    {
        using var pen = new Pen(color, width);
        using (var path = Theme.RoundedRect(RectangleF.Inflate(r, -r.Width * 0.18f, -r.Height * 0.18f), r.Width * 0.12f))
            g.DrawPath(pen, path);
        for (int i = 0; i < 3; i++)
        {
            float t = r.X + r.Width * (0.32f + i * 0.18f);
            g.DrawLine(pen, t, r.Y, t, r.Y + r.Height * 0.18f);
            g.DrawLine(pen, t, r.Bottom - r.Height * 0.18f, t, r.Bottom);
        }
    }

    public static void Warning(Graphics g, RectangleF r, Color color, float width)
    {
        using var pen = new Pen(color, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawPolygon(pen, [new PointF(r.X + r.Width / 2, r.Y), new PointF(r.Right, r.Bottom), new PointF(r.X, r.Bottom)]);
        g.DrawLine(pen, r.X + r.Width / 2, r.Y + r.Height * 0.38f, r.X + r.Width / 2, r.Y + r.Height * 0.66f);
        g.DrawLine(pen, r.X + r.Width / 2, r.Y + r.Height * 0.84f, r.X + r.Width / 2, r.Y + r.Height * 0.86f);
    }

    public static void Check(Graphics g, RectangleF r, Color color, float width)
    {
        using var pen = new Pen(color, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, r);
        g.DrawLines(pen, [
            new PointF(r.X + r.Width * 0.28f, r.Y + r.Height * 0.52f),
            new PointF(r.X + r.Width * 0.45f, r.Y + r.Height * 0.68f),
            new PointF(r.X + r.Width * 0.73f, r.Y + r.Height * 0.36f)]);
    }
}
