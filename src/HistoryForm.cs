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
            if (windowId != null)
                open.state.Window = windowId;
            open.Show();
        }
        else if (windowId != null && windowId != open.state.Window)
        {
            open.state.Window = windowId;
            open.Reload(scanTokens: false, replay: true);
        }
        open.Activate();
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

        Load += (_, _) =>
        {
            view.PlayIntro();
            Reload(scanTokens: true, replay: false);
        };
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
            BeginInvoke(() => Reload(scanTokens: false, replay: false));
        };
        service.Changed += onChanged;
        FormClosed += (_, _) => service.Changed -= onChanged;
    }

    internal MonitorService Service => service;
    internal HistoryState State => state;
    internal HistoryData? Data => data;
    internal TextBox Search => search;
    internal HistoryView View => view;

    /// <summary>Loads the rows again; <paramref name="replay"/> plays the entrance for a new range or window (header and toolbar stay).</summary>
    async void Reload(bool scanTokens, bool replay)
    {
        data = HistoryData.Load(service, state, DateTimeOffset.UtcNow);
        if (replay)
            view.Motion.Restart("top", "toolbar");
        view.Fit(scroller.ClientSize);
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
            Reload(scanTokens: true, replay: true);
        }
        else if (action.StartsWith("window:"))
        {
            state.Window = action["window:".Length..];
            Reload(scanTokens: false, replay: true);
        }
        else if (action == "sort")
        {
            state.NewestFirst = !state.NewestFirst;
            view.Motion.Replay("rows");
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

    /// <summary>Shows every entrance at its end at once; used by --screenshot.</summary>
    internal void Settle() => view.Settle();
}

/// <summary>Draws the history page with GDI+ through the shared layout and keeps the search box on its drawn frame.</summary>
internal sealed class HistoryView(HistoryForm owner) : GlassView
{
    public void Fit(Size client)
    {
        if (owner.Data == null)
            return;
        int height = (int)Math.Ceiling(HistoryPage.Height(owner.Data, owner.State) * UiScale);
        Size = new Size(client.Width, Math.Max(client.Height, height));
        Invalidate();
    }

    protected override void Draw(IPaint paint, float width)
    {
        if (owner.Data == null)
            return;
        HistoryPage.Render(paint, width, Math.Max(Height / UiScale, ViewHeight), owner.Service, owner.Data, owner.State, State, DateTimeOffset.UtcNow);
        var box = owner.State.SearchBox;
        var bounds = new Rectangle((int)(box.X * UiScale), (int)(box.Y * UiScale + (box.Height * UiScale - owner.Search.Height) / 2),
            (int)(box.Width * UiScale), owner.Search.Height);
        if (owner.Search.Bounds != bounds)
            BeginInvoke(() => owner.Search.Bounds = bounds);
    }

    protected override void Act(string action) => owner.Act(action);
}
