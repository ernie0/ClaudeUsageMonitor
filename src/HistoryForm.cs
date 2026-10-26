using System.Diagnostics;

namespace ClaudeUsageMonitor;

/// <summary>The history page (layout in Core/HistoryPage.cs) with scrolling, hover, a search box and CSV export.</summary>
internal sealed class HistoryForm : Form
{
    static HistoryForm? open;

    readonly MonitorService service;
    readonly HistoryState state = new();
    readonly Panel scroller = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = PageKit.PageTop };
    readonly HistoryView view;
    readonly TextBox search = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = PageKit.Hex(0x161A36),
        ForeColor = PageKit.Text,
        PlaceholderText = "搜尋紀錄…",
    };
    HistoryData? data;
    DateTimeOffset? lastObserved;

    /// <summary>Opens (or brings forward) the history, optionally showing one window's curve.</summary>
    public static void Open(MonitorService service, string? windowId = null)
    {
        if (open is not { IsDisposed: false })
        {
            open = new HistoryForm(service);
            open.Show();
        }
        if (windowId != null)
            open.state.Window = windowId;
        open.Activate();
        open.Reload(scanTokens: false);
    }

    internal HistoryForm(MonitorService service)
    {
        this.service = service;
        view = new HistoryView(this) { Location = Point.Empty };
        Text = "Claude 額度 · 使用紀錄" + (service.Demo ? "（示範資料）" : "");
        Icon = AppIcon.Load();
        BackColor = PageKit.PageTop;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 940);
        MinimumSize = new Size(1080, 600);
        search.Font = Theme.Ui(10.5f);
        search.TextChanged += (_, _) =>
        {
            state.Search = search.Text;
            view.Fit(scroller.ClientSize);
        };
        view.Controls.Add(search);
        scroller.Controls.Add(view);
        scroller.Resize += (_, _) => view.Fit(scroller.ClientSize);
        Controls.Add(scroller);

        Load += (_, _) => Reload(scanTokens: true);
        HandleCreated += (_, _) =>
        {
            Theme.ApplyWindowChrome(this);
            Theme.DarkScrollbars(scroller);
        };
        Action onChanged = () =>
        {
            var observed = service.Current?.ObservedAt;
            if (!IsHandleCreated || IsDisposed || observed == lastObserved)
                return;
            lastObserved = observed;
            BeginInvoke(() => Reload(scanTokens: false));
        };
        service.Changed += onChanged;
        FormClosed += (_, _) => service.Changed -= onChanged;
    }

    internal MonitorService Service => service;
    internal HistoryState State => state;
    internal HistoryData? Data => data;
    internal TextBox Search => search;

    async void Reload(bool scanTokens)
    {
        data = HistoryData.Load(service, state, DateTimeOffset.UtcNow);
        view.Fit(scroller.ClientSize);
        view.PlayIntro();
        if (scanTokens)
        {
            await service.ScanTokensAsync((int)Math.Ceiling(state.Range.TotalDays));
            if (IsDisposed)
                return;
            data = HistoryData.Load(service, state, DateTimeOffset.UtcNow);
            view.Invalidate();
        }
    }

    internal void Act(string action)
    {
        if (action.StartsWith("range:"))
        {
            state.Range = TimeSpan.FromDays(int.Parse(action["range:".Length..]));
            Reload(scanTokens: true);
        }
        else if (action.StartsWith("window:"))
        {
            state.Window = action["window:".Length..];
            Reload(scanTokens: false);
        }
        else if (action == "sort")
        {
            state.NewestFirst = !state.NewestFirst;
            view.Invalidate();
        }
        else if (action == "search")
        {
            search.Focus();
        }
        else if (action == "export")
        {
            ExportCsv();
        }
    }

    void ExportCsv()
    {
        if (data == null)
            return;
        var now = DateTimeOffset.Now;
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV 表格 (*.csv)|*.csv",
            FileName = $"claude-usage-{(now - state.Range):yyyyMMdd}-{now:yyyyMMdd}.csv",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            HistoryStore.ExportCsv(dialog.FileName, data.Rows);
            MessageBox.Show(this, $"已匯出 {data.Rows.Count} 筆（所有額度）。\n檔案含使用時間，分享前請先檢查。", "匯出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "無法寫入這個位置，請換一個資料夾。", "匯出失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Finishes the chart's draw-in at once; used by --screenshot.</summary>
    internal void Settle() => view.Settle();
}

/// <summary>Draws the history page with GDI+ and keeps the search box on its drawn frame.</summary>
internal sealed class HistoryView : Control
{
    readonly HistoryForm owner;
    readonly System.Windows.Forms.Timer frame = new() { Interval = 16 };
    readonly Stopwatch intro = new();
    readonly PaintCache cache = new();

    public HistoryView(HistoryForm owner)
    {
        this.owner = owner;
        DoubleBuffered = true;
        BackColor = PageKit.PageTop;
        frame.Tick += (_, _) =>
        {
            double t = Math.Min(1, intro.ElapsedMilliseconds / 700.0);
            owner.State.Intro = (float)(1 - Math.Pow(1 - t, 3));
            Invalidate();
            if (t >= 1)
            {
                frame.Stop();
                intro.Reset();
            }
        };
        Disposed += (_, _) =>
        {
            frame.Dispose();
            cache.Dispose();
        };
    }

    float UiScale => DeviceDpi / 96f;

    public void Fit(Size client)
    {
        if (owner.Data == null)
            return;
        int height = (int)Math.Ceiling(HistoryPage.Height(owner.Data, owner.State) * UiScale);
        Size = new Size(client.Width, Math.Max(client.Height, height));
        Invalidate();
    }

    public void PlayIntro()
    {
        if (!Theme.AnimationsEnabled)
            return;
        owner.State.Intro = 0;
        intro.Restart();
        frame.Start();
    }

    public void Settle()
    {
        frame.Stop();
        owner.State.Intro = 1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (owner.Data == null)
            return;
        var g = e.Graphics;
        TilePainter.Prepare(g);
        using (var paint = new GdiPaint(g, UiScale, cache))
            HistoryPage.Render(paint, Width / UiScale, Height / UiScale, owner.Service, owner.Data, owner.State, DateTimeOffset.UtcNow);
        var box = owner.State.SearchBox;
        var bounds = new Rectangle((int)(box.X * UiScale), (int)(box.Y * UiScale + (box.Height * UiScale - owner.Search.Height) / 2),
            (int)(box.Width * UiScale), owner.Search.Height);
        if (owner.Search.Bounds != bounds)
            owner.Search.Bounds = bounds;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = new PointF(e.X / UiScale, e.Y / UiScale);
        string? hit = owner.State.HitAt(point);
        Cursor = hit != null ? Cursors.Hand : Cursors.Default;
        owner.State.Hover = hit;
        owner.State.Pointer = point;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        owner.State.Hover = null;
        owner.State.Pointer = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && owner.State.HitAt(new PointF(e.X / UiScale, e.Y / UiScale)) is { } action)
            owner.Act(action);
    }
}
