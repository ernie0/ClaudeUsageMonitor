using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace ClaudeUsageMonitor;

/// <summary>Every window as a coloured card with its reset time, burn rate and forecast spelled out.</summary>
internal sealed class DetailsForm : Form
{
    const string SourceClosed = "▸ 資料來源與紀錄位置";
    const string SourceOpen = "▾ 收起資料來源";

    static DetailsForm? open;

    readonly MonitorService service;
    readonly QuotaCards cards;
    readonly FlowLayoutPanel info = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14, 10, 14, 4), BackColor = Theme.Canvas };
    readonly Label summary = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 6) };
    readonly Label sourceToggle = new() { AutoSize = true, Text = SourceClosed, ForeColor = Theme.Coral, Cursor = Cursors.Hand, Margin = new Padding(0, 2, 0, 4) };
    readonly Label source = new() { AutoSize = true, ForeColor = Theme.Muted, Visible = false, Margin = new Padding(14, 0, 0, 4) };
    readonly PillButton openFolder = Theme.Button("開啟紀錄資料夾");
    readonly System.Windows.Forms.Timer ticker = new() { Interval = 30_000 };
    readonly FlowLayoutPanel buttons;

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
        cards = new QuotaCards(service) { Dock = DockStyle.Top };
        Text = "Claude 額度 · 詳情" + (service.Demo ? "（示範資料）" : "");
        Icon = AppIcon.Load();
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui(10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(880, 560);
        MinimumSize = new Size(720, 420);

        buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = Theme.Canvas };
        var close = Theme.Button("關閉");
        close.Click += (_, _) => Close();
        var refresh = Theme.Button("立即更新", primary: true);
        refresh.Click += (_, _) =>
        {
            if (!service.Refresh(manual: true))
                MessageBox.Show(this, "10 秒內只能手動更新一次。", "請稍候", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        var official = Theme.Button("官方用量頁");
        official.Click += (_, _) => OverlayForm.OpenUrl("https://claude.ai/settings/usage");
        var history = Theme.Button("使用紀錄");
        history.Click += (_, _) => HistoryForm.Open(service);
        buttons.Controls.AddRange([close, refresh, official, history]);

        // Where the data comes from is rarely needed, so it stays folded until asked for.
        sourceToggle.Click += (_, _) => ToggleSource();
        openFolder.Visible = false;
        openFolder.Margin = new Padding(12, 2, 0, 4);
        openFolder.Click += (_, _) => OverlayForm.OpenUrl(service.Root);
        info.Controls.AddRange([summary, sourceToggle, source, openFolder]);
        info.Resize += (_, _) => WrapText();

        Controls.Add(info);
        Controls.Add(cards);
        Controls.Add(buttons);

        Load += (_, _) =>
        {
            Render();
            FitHeight();
            cards.Animate();
        };
        HandleCreated += (_, _) => Theme.ApplyWindowChrome(this);
        ticker.Tick += (_, _) => Render();
        ticker.Start();
        service.Changed += OnServiceChanged;
        FormClosed += (_, _) =>
        {
            service.Changed -= OnServiceChanged;
            ticker.Dispose();
        };
    }

    void OnServiceChanged()
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(Render);
    }

    void ToggleSource()
    {
        bool show = !source.Visible;
        source.Visible = show;
        openFolder.Visible = show;
        sourceToggle.Text = show ? SourceOpen : SourceClosed;
        FitHeight();
    }

    void WrapText()
    {
        var width = new Size(Math.Max(200, info.ClientSize.Width - info.Padding.Horizontal - 20), 0);
        summary.MaximumSize = width;
        source.MaximumSize = width;
    }

    /// <summary>Sizes the window so every card and the visible info fit without scrolling.</summary>
    void FitHeight()
    {
        WrapText();
        int text = info.Controls.Cast<Control>().Where(c => c.Visible)
            .Sum(c => c.GetPreferredSize(new Size(c.MaximumSize.Width, 0)).Height + c.Margin.Vertical);
        int needed = cards.Height + text + info.Padding.Vertical + buttons.Height + 12;
        int limit = Screen.FromControl(this).WorkingArea.Height - 80;
        ClientSize = new Size(ClientSize.Width, Math.Clamp(needed, MinimumSize.Height - (Height - ClientSize.Height), limit));
    }

    void Render()
    {
        var snapshot = service.Current;
        cards.Height = cards.PreferredHeight;
        cards.Invalidate();

        var lines = new List<string>();
        if (snapshot != null)
        {
            lines.Add($"方案：{snapshot.PlanLabel}　資料時間：{snapshot.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" + (service.Fresh ? "" : "（上次資料）"));
            if (snapshot.Extra is { } extra)
                lines.Add($"額外用量：{(extra.Enabled ? "已開啟" : "未開啟")}，本月 {Format.Money(extra.Used, extra.Currency)}／上限 {Format.Money(extra.MonthlyLimit, extra.Currency)}");
        }
        lines.Add("狀態：" + service.Status
            + (service.NextPoll is { } next && !service.Busy ? $"　下次更新 {next.ToLocalTime():HH:mm:ss}" : "")
            + (service.SlowedDown ? "（額度沒有變化，自動放慢）" : ""));
        lines.Add("使用速度是工具開著時的觀察推估，不是官方數字；重置時間由官方回報。");
        summary.Text = string.Join("\n", lines);

        source.Text = "資料來源：" + (service.ClaudePath.Length > 0 ? service.ClaudePath : "尚未找到 Claude Code")
            + (snapshot?.ClaudeVersion is { Length: > 0 } v ? $"（Claude Code {v}）" : "")
            + "\n紀錄位置：" + service.Root;
    }
}

/// <summary>One row per window: the coloured tile on the left, the facts on the right.</summary>
internal sealed class QuotaCards : Control
{
    const float RowH = 104, RowGap = 10, Margin_ = 12, TileWidth = 250;

    readonly MonitorService service;
    readonly System.Windows.Forms.Timer frame = new() { Interval = 16 };
    readonly Stopwatch clock = new();
    FontSet? fonts;
    float progress = 1;

    public QuotaCards(MonitorService service)
    {
        this.service = service;
        DoubleBuffered = true;
        BackColor = Theme.Canvas;
        Font = Theme.Ui(9.5f);
        SetStyle(ControlStyles.ResizeRedraw, true);
        frame.Tick += (_, _) =>
        {
            double t = Math.Min(1, clock.ElapsedMilliseconds / 800.0);
            progress = (float)(1 - Math.Pow(1 - t, 3));
            Invalidate();
            if (t >= 1)
            {
                frame.Stop();
                clock.Reset();
            }
        };
    }

    float UiScale => DeviceDpi / 96f;

    public int PreferredHeight
    {
        get
        {
            int count = Math.Max(1, service.Current?.Windows.Count ?? 1);
            return (int)((Margin_ + count * (RowH + RowGap)) * UiScale);
        }
    }

    /// <summary>Rings sweep up from zero when the window opens.</summary>
    public void Animate()
    {
        if (!Theme.AnimationsEnabled)
            return;
        progress = 0;
        clock.Restart();
        frame.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            frame.Dispose();
            fonts?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Canvas);
        TilePainter.Prepare(g);
        float s = UiScale;
        if (fonts == null || Math.Abs(fonts.Scale - s) > 0.001f)
        {
            fonts?.Dispose();
            fonts = new FontSet(s);
        }
        var now = DateTimeOffset.UtcNow;
        var snapshot = service.Current;
        if (snapshot == null)
        {
            var r = new RectangleF(Margin_ * s, Margin_ * s, Width - Margin_ * 2 * s, RowH * s);
            TilePainter.Pill(g, r with { Height = 34 * s }, TilePainter.BuildInsight(service, now), fonts);
            return;
        }

        float y = Margin_ * s;
        foreach (var w in snapshot.Windows)
        {
            var level = Theme.LevelOf(w.RemainingPercent, service.Settings);
            var forecast = service.Forecasts.GetValueOrDefault(w.Id) ?? Forecast.Empty;
            var tile = new RectangleF(Margin_ * s, y, TileWidth * s, RowH * s);
            TilePainter.Tile(g, tile, new TileModel(TilePainter.TileTitle(w), TilePainter.IconFor(w), w.RemainingPercent * progress, level,
                Theme.Word(level, w.RemainingPercent), Format.ShortCountdown(w.ResetsAt, now), $"已使用 {w.UsedPercent:0}%"), fonts);

            var panel = RectangleF.FromLTRB(tile.Right + 10 * s, y, Width - Margin_ * s, y + RowH * s);
            using (var path = Theme.RoundedRect(panel, 14 * s))
            using (var back = new SolidBrush(Theme.Surface))
            using (var edge = new Pen(Theme.Border))
            {
                g.FillPath(back, path);
                g.DrawPath(edge, path);
            }

            var facts = new List<(string Key, string Value, Color Color)>
            {
                ("重置時間", w.ResetsAt is { } r ? Format.ResetAt(r, now) : "未提供", Theme.Text),
                ("還有", w.ResetsAt is { } r2 && r2 > now ? Format.Duration(r2 - now) : "—", Theme.Text),
                ("使用速度", w.RemainingPercent <= 0 ? "—" : forecast.RatePerHour is { } rate ? $"{Format.Rate(rate)}（{Format.Basis(forecast.BasisMinutes)}）" : "觀察中", Theme.Text),
                ("照目前速度", Outlook(w, forecast, now, out var outlookColor), outlookColor),
                ("每小時可用", forecast.BudgetPerHour is { } budget && w.RemainingPercent > 0 ? $"{Format.Rate(budget)} 以下就撐得到重置" : "—", Theme.Text),
                ("已使用", w.UsedPercent.ToString("0", CultureInfo.InvariantCulture) + "%", Theme.Text),
            };
            float colW = (panel.Width - 28 * s) / 2;
            for (int i = 0; i < facts.Count; i++)
            {
                float fx = panel.X + 16 * s + (i % 2) * colW;
                float fy = panel.Y + 12 * s + (i / 2) * 28 * s;
                using var key = new SolidBrush(Theme.Muted);
                using var value = new SolidBrush(facts[i].Color);
                g.DrawString(facts[i].Key, fonts.Small, key, new PointF(fx, fy + 2 * s));
                g.DrawString(facts[i].Value, fonts.Label, value, new RectangleF(fx + 74 * s, fy, colW - 80 * s, 20 * s),
                    new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            }
            y += (RowH + RowGap) * s;
        }
    }

    static string Outlook(QuotaWindow w, Forecast forecast, DateTimeOffset now, out Color color)
    {
        if (w.RemainingPercent <= 0)
        {
            color = Theme.Danger;
            return "已用完，等待重置";
        }
        if (forecast.ExhaustsBeforeReset && forecast.ExhaustAt is { } at)
        {
            color = Theme.Warning;
            return Format.ResetAt(at, now) + " 用完（早於重置）";
        }
        if (forecast.RatePerHour is not null)
        {
            color = Theme.Good;
            return "撐得到重置";
        }
        color = Theme.Muted;
        return "還在觀察";
    }
}
