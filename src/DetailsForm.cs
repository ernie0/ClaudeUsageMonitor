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
        HandleCreated += (_, _) =>
        {
            Theme.ApplyWindowChrome(this);
            Theme.DarkScrollbars(scroller);
        };
    }

    internal DashboardView View => view;

    internal void Act(string action)
    {
        switch (action)
        {
            case "refresh":
            case "refresh:top":
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

/// <summary>Draws the dashboard with GDI+ through the shared layout and animation clock.</summary>
internal sealed class DashboardView : GlassView
{
    readonly MonitorService service;
    readonly DetailsForm owner;
    readonly System.Windows.Forms.Timer ticker = new() { Interval = 30_000 };
    Size viewport;

    public DashboardView(MonitorService service, DetailsForm owner)
    {
        this.service = service;
        this.owner = owner;
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
            ticker.Dispose();
        };
    }

    public void Fit(Size client)
    {
        viewport = client;
        int height = (int)Math.Ceiling(DetailsPage.Height(service, State) * UiScale);
        Size = new Size(client.Width, Math.Max(client.Height, height));
        Invalidate();
    }

    public void ToggleSource() => State.SourceOpen = !State.SourceOpen;

    protected override void Draw(IPaint paint, float width) =>
        DetailsPage.Render(paint, width, Math.Max(Height / UiScale, ViewHeight), service, State, DateTimeOffset.UtcNow);

    protected override void Act(string action) => owner.Act(action);
}
