using System.Diagnostics;

namespace ClaudeUsageMonitor;

/// <summary>The details dashboard (layout in Core/DetailsPage.cs), hosted with scrolling, hover and clicks.</summary>
internal sealed class DetailsForm : Form
{
    static DetailsForm? open;

    readonly MonitorService service;
    readonly DashboardView view;
    readonly Panel scroller = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = PageKit.PageTop };

    public static void Open(MonitorService service)
    {
        if (open is { IsDisposed: false })
        {
            open.Activate();
            return;
        }
        open = new DetailsForm(service);
        open.Show();
    }

    internal DetailsForm(MonitorService service)
    {
        this.service = service;
        view = new DashboardView(service, this) { Location = Point.Empty };
        Text = "Claude 額度 · 詳情" + (service.Demo ? "（示範資料）" : "");
        Icon = AppIcon.Load();
        BackColor = PageKit.PageTop;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1320, 900);
        MinimumSize = new Size(1080, 620);

        scroller.Controls.Add(view);
        scroller.Resize += (_, _) => view.Fit(scroller.ClientSize);
        Controls.Add(scroller);

        Load += async (_, _) =>
        {
            view.Fit(scroller.ClientSize);
            view.PlayIntro();
            await service.ScanTokensAsync(8);
            if (!IsDisposed)
                view.Invalidate();
        };
        HandleCreated += (_, _) => Theme.ApplyWindowChrome(this);
    }

    internal DashboardView View => view;

    internal void Act(string action)
    {
        switch (action)
        {
            case "refresh":
                if (!service.Refresh(manual: true))
                    MessageBox.Show(this, "10 秒內只能手動更新一次。", "請稍候", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case "history":
                HistoryForm.Open(service);
                break;
            case "analysis":
                AnalysisForm.Open(service);
                break;
            case "official":
                OverlayForm.OpenUrl("https://claude.ai/settings/usage");
                break;
            case "folder":
                OverlayForm.OpenUrl(service.Root);
                break;
            case "source":
                view.ToggleSource();
                view.Fit(scroller.ClientSize);
                break;
            default:
                if (action.StartsWith("card:") || action.StartsWith("row:"))
                    HistoryForm.Open(service, action[(action.IndexOf(':') + 1)..]);
                break;
        }
    }
}

/// <summary>Draws the dashboard with GDI+; hover highlights what is clickable, rings sweep in on open.</summary>
internal sealed class DashboardView : Control
{
    readonly MonitorService service;
    readonly DetailsForm owner;
    readonly PageState state = new();
    readonly System.Windows.Forms.Timer frame = new() { Interval = 16 };
    readonly System.Windows.Forms.Timer ticker = new() { Interval = 30_000 };
    readonly Stopwatch intro = new();
    Size viewport;

    public DashboardView(MonitorService service, DetailsForm owner)
    {
        this.service = service;
        this.owner = owner;
        DoubleBuffered = true;
        BackColor = PageKit.PageTop;
        frame.Tick += (_, _) =>
        {
            double t = Math.Min(1, intro.ElapsedMilliseconds / 900.0);
            state.Intro = (float)(1 - Math.Pow(1 - t, 3));
            Invalidate();
            if (t >= 1)
            {
                frame.Stop();
                intro.Reset();
            }
        };
        ticker.Tick += (_, _) => Invalidate();
        ticker.Start();
        Action onChanged = () =>
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => Fit(viewport));
        };
        service.Changed += onChanged;
        Disposed += (_, _) =>
        {
            service.Changed -= onChanged;
            frame.Dispose();
            ticker.Dispose();
        };
    }

    float UiScale => DeviceDpi / 96f;

    public void Fit(Size client)
    {
        viewport = client;
        int height = (int)Math.Ceiling(DetailsPage.Height(service, state) * UiScale);
        Size = new Size(client.Width, Math.Max(client.Height, height));
        Invalidate();
    }

    public void PlayIntro()
    {
        if (!Theme.AnimationsEnabled)
            return;
        state.Intro = 0;
        intro.Restart();
        frame.Start();
    }

    /// <summary>Finishes the intro at once; used by --screenshot.</summary>
    public void Settle()
    {
        frame.Stop();
        state.Intro = 1;
        Invalidate();
    }

    public void ToggleSource() => state.SourceOpen = !state.SourceOpen;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        TilePainter.Prepare(g);
        using var paint = new GdiPaint(g, UiScale);
        DetailsPage.Render(paint, Width / UiScale, Height / UiScale, service, state, DateTimeOffset.UtcNow);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? hit = state.HitAt(new PointF(e.X / UiScale, e.Y / UiScale));
        Cursor = hit != null ? Cursors.Hand : Cursors.Default;
        if (hit != state.Hover)
        {
            state.Hover = hit;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (state.Hover != null)
        {
            state.Hover = null;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && state.HitAt(new PointF(e.X / UiScale, e.Y / UiScale)) is { } action)
            owner.Act(action);
    }
}
