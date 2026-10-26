using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace ClaudeUsageMonitor;

/// <summary>Remaining-percentage curve for one window plus a table of the observed changes.</summary>
internal sealed class HistoryForm : Form
{
    static HistoryForm? open;

    readonly MonitorService service;
    readonly FlowLayoutPanel windowButtons = new() { AutoSize = true, WrapContents = false, Margin = new Padding(16, 0, 0, 0), BackColor = Theme.Canvas };
    readonly HistoryChart chart = new() { Dock = DockStyle.Fill };
    readonly StatChips stats = new() { Dock = DockStyle.Top, Height = 40 };
    readonly ListView table = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None };
    readonly List<PillButton> rangeButtons = [];
    TimeSpan range = TimeSpan.FromDays(7);
    string selectedWindow = WindowIds.SevenDay;
    DateTimeOffset? lastObserved;
    List<UsageSample> rows = [];

    public static void Open(MonitorService service)
    {
        if (open is { IsDisposed: false })
        {
            open.Activate();
            open.Reload();
            return;
        }
        open = new HistoryForm(service);
        open.Show();
    }

    internal HistoryForm(MonitorService service)
    {
        this.service = service;
        Text = "Claude 額度 · 使用紀錄" + (service.Demo ? "（示範資料）" : "");
        Icon = AppIcon.Load();
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(960, 680);
        MinimumSize = new Size(680, 520);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 10, 10, 2), BackColor = Theme.Canvas, WrapContents = true };
        foreach (var (label, span) in new[] { ("24 小時", TimeSpan.FromDays(1)), ("7 天", TimeSpan.FromDays(7)), ("30 天", TimeSpan.FromDays(30)) })
        {
            var button = Theme.Button(label);
            button.Tag = span;
            button.Click += (_, _) =>
            {
                range = span;
                Reload();
            };
            rangeButtons.Add(button);
            bar.Controls.Add(button);
        }
        bar.Controls.Add(windowButtons);
        var export = Theme.Button("匯出 CSV");
        export.Margin = new Padding(16, 4, 4, 4);
        export.Click += (_, _) => ExportCsv();
        bar.Controls.Add(export);

        table.Columns.Add("時間", 170);
        table.Columns.Add("額度", 120);
        table.Columns.Add("已使用", 80, HorizontalAlignment.Right);
        table.Columns.Add("剩餘", 80, HorizontalAlignment.Right);
        table.Columns.Add("變化", 360);
        Theme.StyleList(table);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = Theme.Canvas,
            SplitterWidth = 6,
        };
        split.Panel1.BackColor = Theme.Canvas;
        split.Panel2.BackColor = Theme.Canvas;
        split.Panel1.Padding = new Padding(10, 4, 10, 4);
        split.Panel2.Padding = new Padding(10, 4, 10, 10);
        split.Panel1.Controls.Add(chart);
        split.Panel2.Controls.Add(table);

        Controls.Add(split);
        Controls.Add(stats);
        Controls.Add(bar);
        Load += (_, _) =>
        {
            split.SplitterDistance = (int)(split.Height * 0.6);
            Reload();
        };
        HandleCreated += (_, _) => Theme.ApplyWindowChrome(this);
        service.Changed += OnServiceChanged;
        FormClosed += (_, _) => service.Changed -= OnServiceChanged;
    }

    void OnServiceChanged()
    {
        // Reread the files only when a new observation has been stored.
        var observed = service.Current?.ObservedAt;
        if (!IsHandleCreated || IsDisposed || observed == lastObserved)
            return;
        lastObserved = observed;
        BeginInvoke(Reload);
    }

    async void ScanTokens()
    {
        await service.ScanTokensAsync((int)Math.Ceiling(range.TotalDays));
        if (IsHandleCreated && !IsDisposed)
            Render();
    }

    void Reload()
    {
        ScanTokens();
        var now = DateTimeOffset.UtcNow;
        var account = service.Current?.AccountKey;
        // Only the current account's history is shown; another login would mix two quotas.
        rows = service.History.Load(now - range, now).Where(r => account == null || r.Account == account).ToList();
        var choices = rows.GroupBy(r => r.Window)
            .Select(g => (Id: g.Key, Label: g.Last().Label))
            .OrderBy(c => c.Id == WindowIds.FiveHour ? 0 : c.Id == WindowIds.SevenDay ? 1 : 2)
            .ThenBy(c => c.Label)
            .ToList();
        if (choices.Count > 0 && choices.All(c => c.Id != selectedWindow))
            selectedWindow = choices.Any(c => c.Id == WindowIds.SevenDay) ? WindowIds.SevenDay : choices[0].Id;

        windowButtons.SuspendLayout();
        foreach (Control old in windowButtons.Controls.Cast<Control>().ToList())
            old.Dispose();
        foreach (var (id, label) in choices)
        {
            var button = Theme.Button(Title(id, label));
            button.Tag = id;
            button.Click += (_, _) =>
            {
                selectedWindow = id;
                Highlight();
                Render();
            };
            windowButtons.Controls.Add(button);
        }
        windowButtons.ResumeLayout();
        Highlight();
        Render();
    }

    static string Title(string id, string label) => id switch
    {
        WindowIds.FiveHour => "5 小時額度",
        WindowIds.SevenDay => "本週額度",
        _ => label.Replace("本週·", "").Replace("每週·", "") + " 本週",
    };

    void Highlight()
    {
        foreach (var button in rangeButtons.Concat(windowButtons.Controls.OfType<PillButton>()))
            button.Active = button.Tag is TimeSpan span ? span == range : (string?)button.Tag == selectedWindow;
    }

    void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var series = rows.Where(r => r.Window == selectedWindow).ToList();
        string title = series.Count > 0 ? Title(selectedWindow, series[^1].Label) : "";
        chart.SetData(series, now - range, now, title, range, service.Settings);

        var changes = Changes(series);
        table.BeginUpdate();
        table.Items.Clear();
        foreach (var change in changes.AsEnumerable().Reverse().Take(3000))
        {
            var item = new ListViewItem(change.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            item.SubItems.Add(Title(selectedWindow, change.Label));
            item.SubItems.Add(change.Used.ToString("0", CultureInfo.InvariantCulture) + "%");
            item.SubItems.Add(change.Remaining.ToString("0", CultureInfo.InvariantCulture) + "%");
            item.SubItems.Add(change.Text);
            if (change.Highlight is { } color)
                item.ForeColor = color;
            table.Items.Add(item);
        }
        table.EndUpdate();
        Theme.FitLastColumn(table);

        double spent = changes.Where(c => c.Delta > 0).Sum(c => c.Delta);
        int resets = changes.Count(c => c.IsReset);
        int gaps = changes.Count(c => c.IsGap);
        stats.SetChips(series.Count == 0
            ? [("這段期間沒有紀錄", Level.Unknown)]
            : [
                ($"觀察 {series.Count} 筆", Level.Fair),
                ($"用掉 {spent:0} 個百分點", spent > 0 ? Level.Low : Level.Plenty),
                ($"重置 {resets} 次", Level.Plenty),
                ($"未監測 {gaps} 段", Level.Unknown),
                ($"區間 Token {(service.Tokens.LastScan == null ? "讀取中…" : Format.Tokens(service.Tokens.Sum(now - range, now).Total))}", Level.Fair),
            ]);
    }

    sealed record Change(DateTimeOffset At, string Label, double Used, double Remaining, double Delta, string Text, bool IsReset, bool IsGap, Color? Highlight);

    /// <summary>The first sample, every change in usage, monitoring gaps and resets.</summary>
    static List<Change> Changes(List<UsageSample> series)
    {
        var list = new List<Change>();
        UsageSample? previous = null;
        foreach (var s in series)
        {
            if (previous == null)
            {
                list.Add(new Change(s.At, s.Label, s.Used, s.Remaining, 0, "開始觀察", false, false, null));
            }
            else
            {
                double delta = s.Used - previous.Used;
                bool reset = delta < -5 || (!ForecastCalculator.SameReset(previous.Reset, s.Reset) && delta < 0);
                if (reset)
                    list.Add(new Change(s.At, s.Label, s.Used, s.Remaining, 0, $"↺ 已重置：剩餘 {previous.Remaining:0}% → {s.Remaining:0}%", true, false, Theme.Good));
                else if (s.Gap)
                    list.Add(new Change(s.At, s.Label, s.Used, s.Remaining, Math.Max(0, delta),
                        $"監測缺口後恢復（{Format.Duration(s.At - previous.At)}未觀察）" + (delta > 0 ? $"，期間用掉 {delta:0} 個百分點" : ""), false, true, Theme.Muted));
                else if (delta != 0)
                    list.Add(new Change(s.At, s.Label, s.Used, s.Remaining, delta, $"剩餘 {(delta > 0 ? "−" : "+")}{Math.Abs(delta):0} 個百分點", false, false, null));
            }
            previous = s;
        }
        return list;
    }

    void ExportCsv()
    {
        var now = DateTimeOffset.Now;
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV 表格 (*.csv)|*.csv",
            FileName = $"claude-usage-{(now - range):yyyyMMdd}-{now:yyyyMMdd}.csv",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            HistoryStore.ExportCsv(dialog.FileName, rows);
            MessageBox.Show(this, $"已匯出 {rows.Count} 筆（所有額度）。\n檔案含使用時間，分享前請先檢查。", "匯出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "無法寫入這個位置，請換一個資料夾。", "匯出失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>A row of small coloured summary chips.</summary>
internal sealed class StatChips : Control
{
    List<(string Text, Level Level)> chips = [];

    public StatChips()
    {
        DoubleBuffered = true;
        BackColor = Theme.Canvas;
        Font = Theme.Ui(9.5f, FontStyle.Bold);
        Padding = new Padding(14, 6, 14, 6);
    }

    public void SetChips(List<(string Text, Level Level)> items)
    {
        chips = items;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        TilePainter.Prepare(g);
        g.Clear(BackColor);
        float s = DeviceDpi / 96f;
        float x = Padding.Left * s;
        float h = Height - Padding.Vertical * s;
        foreach (var (text, level) in chips)
        {
            var size = g.MeasureString(text, Font);
            var r = new RectangleF(x, Padding.Top * s, size.Width + 22 * s, h);
            using var path = Theme.RoundedRect(r, h / 2);
            if (level == Level.Unknown)
            {
                using var back = new SolidBrush(Theme.Raised);
                g.FillPath(back, path);
            }
            else
            {
                var (start, end) = Theme.Gradient(level);
                using var back = new LinearGradientBrush(r, start, end, LinearGradientMode.Horizontal);
                g.FillPath(back, path);
            }
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, Font, Brushes.White, r, format);
            x = r.Right + 8 * s;
        }
    }
}

/// <summary>
/// The remaining-percentage curve. Colour follows the amount left (green at the top through violet and
/// amber to red at the bottom), the area under it is filled with the same gradient, the "low" and
/// "critical" zones are tinted, unobserved time is hatched, and the curve draws itself in on load.
/// </summary>
internal sealed class HistoryChart : Control
{
    readonly System.Windows.Forms.Timer frame = new() { Interval = 16 };
    readonly Stopwatch reveal = new();
    List<UsageSample> series = [];
    DateTimeOffset from, to;
    string title = "";
    TimeSpan range;
    int warn = 20, critical = 5;
    int hoverIndex = -1;
    float progress = 1;

    public HistoryChart()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9f);
        SetStyle(ControlStyles.ResizeRedraw, true);
        frame.Tick += (_, _) =>
        {
            double t = Math.Min(1, reveal.ElapsedMilliseconds / 650.0);
            progress = (float)(1 - Math.Pow(1 - t, 3));
            Invalidate();
            if (t >= 1)
            {
                frame.Stop();
                reveal.Reset();
            }
        };
    }

    public void SetData(List<UsageSample> data, DateTimeOffset start, DateTimeOffset end, string label, TimeSpan span, Settings settings)
    {
        series = data;
        from = start;
        to = end;
        title = label;
        range = span;
        warn = settings.WarnRemaining;
        critical = settings.CriticalRemaining;
        hoverIndex = -1;
        if (Theme.AnimationsEnabled)
        {
            progress = 0;
            reveal.Restart();
            frame.Start();
        }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            frame.Dispose();
        base.Dispose(disposing);
    }

    float UiScale => DeviceDpi / 96f;

    Rectangle Plot => Rectangle.FromLTRB((int)(52 * UiScale), (int)(40 * UiScale), Width - (int)(18 * UiScale), Height - (int)(30 * UiScale));

    float X(DateTimeOffset t) => Plot.Left + (float)((t - from).TotalSeconds / Math.Max(1, (to - from).TotalSeconds)) * Plot.Width;

    float Y(double remaining) => Plot.Bottom - (float)(remaining / 100) * Plot.Height;

    /// <summary>Vertical gradient over the plot: the colour of a height is the level of that much left.</summary>
    LinearGradientBrush LevelBrush(Rectangle plot, int alpha)
    {
        var brush = new LinearGradientBrush(new Rectangle(plot.X, plot.Y - 1, plot.Width, plot.Height + 2), Color.Black, Color.Black, LinearGradientMode.Vertical);
        float warnPos = 1 - warn / 100f, criticalPos = 1 - critical / 100f;
        brush.InterpolationColors = new ColorBlend
        {
            Colors =
            [
                Theme.Alpha(Theme.Good, alpha),
                Theme.Alpha(Theme.Bright(Level.Fair), alpha),
                Theme.Alpha(Theme.Bright(Level.Fair), alpha),
                Theme.Alpha(Theme.Warning, alpha),
                Theme.Alpha(Theme.Danger, alpha),
                Theme.Alpha(Theme.Danger, alpha),
            ],
            Positions = [0f, 0.5f, Math.Max(0.5f, warnPos - 0.02f), warnPos, criticalPos, 1f],
        };
        return brush;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        TilePainter.Prepare(g);
        var plot = Plot;
        if (plot.Width < 40 || plot.Height < 40)
            return;
        float s = UiScale;

        using (var titleFont = new Font(Font.FontFamily, Font.Size + 2f, FontStyle.Bold))
        using (var titleBrush = new SolidBrush(Theme.Text))
            g.DrawString(title.Length > 0 ? title + " · 剩餘曲線" : "剩餘曲線", titleFont, titleBrush, new PointF(12 * s, 9 * s));
        string rangeText = $"{from.ToLocalTime():MM/dd HH:mm} – {to.ToLocalTime():MM/dd HH:mm}";
        var rangeSize = TextRenderer.MeasureText(rangeText, Font);
        TextRenderer.DrawText(g, rangeText, Font, new Point(Width - rangeSize.Width - (int)(14 * s), (int)(12 * s)), Theme.Muted);

        // Tinted zones so "low" and "critical" are visible without reading numbers.
        using (var lowZone = new SolidBrush(Theme.Alpha(Theme.Warning, 22)))
            g.FillRectangle(lowZone, RectangleF.FromLTRB(plot.Left, Y(warn), plot.Right, Y(critical)));
        using (var criticalZone = new SolidBrush(Theme.Alpha(Theme.Danger, 30)))
            g.FillRectangle(criticalZone, RectangleF.FromLTRB(plot.Left, Y(critical), plot.Right, plot.Bottom));

        using var grid = new Pen(Theme.Track);
        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            float y = Y(pct);
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            var text = pct + "%";
            var size = TextRenderer.MeasureText(text, Font);
            TextRenderer.DrawText(g, text, Font, new Point(plot.Left - size.Width - (int)(4 * s), (int)(y - size.Height / 2f)), Theme.Muted);
        }
        PaintTimeAxis(g, plot);

        if (series.Count == 0)
        {
            TextRenderer.DrawText(g, "這段期間沒有紀錄。工具開著時才會累積，關機或休眠期間不會補資料。", Font, plot, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        // Unobserved stretches are hatched instead of being bridged by a line.
        using var gapBrush = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(0x34, 0x30, 0x4E), Theme.Surface);
        DateTimeOffset? previousAt = null;
        foreach (var sample in series)
        {
            if (previousAt is { } p && Broken(p, sample))
                g.FillRectangle(gapBrush, RectangleF.FromLTRB(X(p), plot.Top, X(sample.At), plot.Bottom));
            previousAt = sample.At;
        }
        if (X(series[0].At) - plot.Left > 4 * s)
            g.FillRectangle(gapBrush, RectangleF.FromLTRB(plot.Left, plot.Top, X(series[0].At), plot.Bottom));

        // Draw-in: everything right of the reveal edge is clipped away until the animation finishes.
        var clip = g.Clip;
        g.SetClip(RectangleF.FromLTRB(plot.Left - 4 * s, plot.Top - 6 * s, plot.Left + plot.Width * progress + 4 * s, plot.Bottom + 2));

        using var area = LevelBrush(plot, 70);
        using var lineBrush = LevelBrush(plot, 255);
        using var line = new Pen(lineBrush, 2.6f * s) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var resetPen = new Pen(Theme.Good, 1.4f * s) { DashStyle = DashStyle.Dash };
        var points = new List<PointF>();
        void Flush()
        {
            if (points.Count == 1)
            {
                float r = 3f * s;
                g.FillEllipse(lineBrush, points[0].X - r, points[0].Y - r, r * 2, r * 2);
            }
            else if (points.Count > 1)
            {
                using var fill = new GraphicsPath();
                fill.AddLines(points.ToArray());
                fill.AddLine(points[^1], new PointF(points[^1].X, plot.Bottom));
                fill.AddLine(new PointF(points[^1].X, plot.Bottom), new PointF(points[0].X, plot.Bottom));
                fill.CloseFigure();
                g.FillPath(area, fill);
                g.DrawLines(line, points.ToArray());
            }
            points.Clear();
        }
        for (int i = 0; i < series.Count; i++)
        {
            var sample = series[i];
            if (i > 0)
            {
                var previous = series[i - 1];
                bool reset = sample.Used < previous.Used - 5;
                if (reset || Broken(previous.At, sample))
                    Flush();
                if (reset)
                {
                    float x = X(sample.At);
                    g.DrawLine(resetPen, x, plot.Top, x, plot.Bottom);
                    PaintTag(g, "↺ 重置", new PointF(x + 4 * s, plot.Bottom - 24 * s), Level.Plenty);
                }
            }
            points.Add(new PointF(X(sample.At), Y(sample.Remaining)));
        }
        Flush();
        g.Clip = clip;

        // The latest value, as a glowing dot with its number.
        if (progress >= 1)
        {
            var last = series[^1];
            var at = new PointF(X(last.At), Y(last.Remaining));
            using (var halo = new SolidBrush(Theme.Alpha(Theme.Text, 50)))
                g.FillEllipse(halo, at.X - 7 * s, at.Y - 7 * s, 14 * s, 14 * s);
            using (var dot = new SolidBrush(Theme.Text))
                g.FillEllipse(dot, at.X - 3.5f * s, at.Y - 3.5f * s, 7 * s, 7 * s);
        }

        // Zone names last, so neither the curve nor the hatching covers them.
        PaintTag(g, $"偏低 ≤{warn}%", new PointF(plot.Left + 6 * s, Y(warn) + 3 * s), Level.Low);
        PaintTag(g, $"告急 ≤{critical}%", new PointF(plot.Left + 90 * s, Y(warn) + 3 * s), Level.Critical);

        if (hoverIndex >= 0 && hoverIndex < series.Count)
            PaintHover(g, plot, series[hoverIndex]);
    }

    void PaintTag(Graphics g, string text, PointF at, Level level)
    {
        float s = UiScale;
        var size = g.MeasureString(text, Font);
        var r = new RectangleF(at.X, at.Y, size.Width + 12 * s, size.Height + 4 * s);
        using var path = Theme.RoundedRect(r, r.Height / 2);
        var (start, end) = Theme.Gradient(level);
        using var back = new LinearGradientBrush(r, start, end, LinearGradientMode.Horizontal);
        g.FillPath(back, path);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, Font, Brushes.White, r, format);
    }

    static bool Broken(DateTimeOffset previous, UsageSample s) =>
        s.Gap || (s.At - previous).TotalSeconds > Math.Max(60, s.Interval) * 3;

    void PaintTimeAxis(Graphics g, Rectangle plot)
    {
        int ticks = Math.Max(2, Math.Min(7, plot.Width / (int)(110 * UiScale)));
        string format = range <= TimeSpan.FromDays(1) ? "HH:mm" : range <= TimeSpan.FromDays(7) ? "MM/dd HH:mm" : "MM/dd";
        for (int i = 0; i <= ticks; i++)
        {
            var t = from + (to - from) * i / ticks;
            string text = t.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
            var size = TextRenderer.MeasureText(text, Font);
            float x = X(t) - size.Width / 2f;
            x = Math.Clamp(x, plot.Left - size.Width / 2f, Width - size.Width - 2);
            TextRenderer.DrawText(g, text, Font, new Point((int)x, plot.Bottom + (int)(6 * UiScale)), Theme.Muted);
        }
    }

    void PaintHover(Graphics g, Rectangle plot, UsageSample sample)
    {
        float s = UiScale;
        float x = X(sample.At), y = Y(sample.Remaining);
        using var pen = new Pen(Theme.Muted) { DashStyle = DashStyle.Dot };
        g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
        using var dot = new SolidBrush(Theme.Text);
        float r = 4f * s;
        g.FillEllipse(dot, x - r, y - r, r * 2, r * 2);

        string text = $"{sample.At.ToLocalTime():MM/dd HH:mm}  剩 {sample.Remaining:0}%（已用 {sample.Used:0}%）";
        var size = g.MeasureString(text, Font);
        var box = new RectangleF(x + 12 * s, y - size.Height - 18 * s, size.Width + 16 * s, size.Height + 10 * s);
        if (box.Right > Width - 4)
            box.X = x - box.Width - 12 * s;
        if (box.Top < plot.Top)
            box.Y = y + 12 * s;
        var level = sample.Remaining <= critical ? Level.Critical : sample.Remaining <= warn ? Level.Low : sample.Remaining <= 50 ? Level.Fair : Level.Plenty;
        using var path = Theme.RoundedRect(box, 8 * s);
        var (start, end) = Theme.Gradient(level);
        using var back = new LinearGradientBrush(box, start, end, LinearGradientMode.Horizontal);
        g.FillPath(back, path);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, Font, Brushes.White, box, format);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (series.Count == 0 || !Plot.Contains(e.Location))
        {
            SetHover(-1);
            return;
        }
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < series.Count; i++)
        {
            float d = Math.Abs(X(series[i].At) - e.X);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        SetHover(bestDistance <= 24 * UiScale ? best : -1);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    void SetHover(int index)
    {
        if (index == hoverIndex)
            return;
        hoverIndex = index;
        Invalidate();
    }
}
