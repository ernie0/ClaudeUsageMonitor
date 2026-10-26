using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ClaudeUsageMonitor;

/// <summary>Remaining-percentage curve for one window plus the list of observed changes (mirrors src/HistoryForm.cs).</summary>
internal sealed class HistoryWindow : Window
{
    readonly MonitorService service;
    readonly StackPanel rangeBar = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly StackPanel windowBar = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(16, 0, 0, 0) };
    readonly WrapPanel chips = new() { Margin = new Thickness(12, 0, 12, 6) };
    readonly HistoryChartView chart = new();
    readonly ListBox list = new() { Background = MacTheme.Solid(MacTheme.Surface) };
    TimeSpan range = TimeSpan.FromDays(7);
    string selectedWindow = WindowIds.SevenDay;
    DateTimeOffset? lastObserved;
    List<UsageSample> rows = [];

    public HistoryWindow(MonitorService service)
    {
        this.service = service;
        Title = "Claude 額度 · 使用紀錄" + (service.Demo ? "（示範資料）" : "");
        Icon = MacShell.AppIcon();
        Width = 960;
        Height = 680;
        MinWidth = 680;
        MinHeight = 520;
        Background = MacTheme.Solid(MacTheme.Canvas);
        FontFamily = MacTheme.UiFont;

        foreach (var (label, span) in new[] { ("24 小時", TimeSpan.FromDays(1)), ("7 天", TimeSpan.FromDays(7)), ("30 天", TimeSpan.FromDays(30)) })
        {
            var button = new Button { Content = label, Tag = span };
            button.Click += (_, _) =>
            {
                range = span;
                Reload();
            };
            rangeBar.Children.Add(button);
        }
        var export = new Button { Content = "匯出 CSV", Margin = new Thickness(16, 0, 0, 0) };
        export.Click += async (_, _) => await ExportCsvAsync();

        list.ItemTemplate = new FuncDataTemplate<ChangeRow>((row, _) => row == null ? new TextBlock() : RowView(row));

        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 12, 12, 8),
            Children = { rangeBar, windowBar, export },
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,3*,2*") };
        grid.Children.Add(top);
        Grid.SetRow(chips, 1);
        grid.Children.Add(chips);
        var chartHost = new Border { Child = chart, Margin = new Thickness(12, 0, 12, 8), CornerRadius = new CornerRadius(10), ClipToBounds = true };
        Grid.SetRow(chartHost, 2);
        grid.Children.Add(chartHost);
        var listHost = new Border { Child = list, Margin = new Thickness(12, 0, 12, 12), CornerRadius = new CornerRadius(10), ClipToBounds = true };
        Grid.SetRow(listHost, 3);
        grid.Children.Add(listHost);
        Content = grid;

        Opened += (_, _) => Reload();
        Action onChanged = () => Dispatcher.UIThread.Post(() =>
        {
            var observed = service.Current?.ObservedAt;
            if (observed == lastObserved)
                return;
            lastObserved = observed;
            Reload();
        });
        service.Changed += onChanged;
        Closed += (_, _) => service.Changed -= onChanged;
    }

    /// <summary>Finishes the chart's draw-in at once; used by --screenshot.</summary>
    internal void Settle() => chart.Settle();

    static string TitleFor(string id, string label) => id switch
    {
        WindowIds.FiveHour => "5 小時額度",
        WindowIds.SevenDay => "本週額度",
        _ => label.Replace("本週·", "").Replace("每週·", "") + " 本週",
    };

    void Reload()
    {
        var now = DateTimeOffset.UtcNow;
        var account = service.Current?.AccountKey;
        rows = service.History.Load(now - range, now).Where(r => account == null || r.Account == account).ToList();
        var choices = rows.GroupBy(r => r.Window)
            .Select(g => (Id: g.Key, Label: g.Last().Label))
            .OrderBy(c => c.Id == WindowIds.FiveHour ? 0 : c.Id == WindowIds.SevenDay ? 1 : 2)
            .ThenBy(c => c.Label)
            .ToList();
        if (choices.Count > 0 && choices.All(c => c.Id != selectedWindow))
            selectedWindow = choices.Any(c => c.Id == WindowIds.SevenDay) ? WindowIds.SevenDay : choices[0].Id;

        windowBar.Children.Clear();
        foreach (var (id, label) in choices)
        {
            var button = new Button { Content = TitleFor(id, label), Tag = id };
            button.Click += (_, _) =>
            {
                selectedWindow = id;
                Highlight();
                Render();
            };
            windowBar.Children.Add(button);
        }
        Highlight();
        Render();
    }

    void Highlight()
    {
        foreach (var button in rangeBar.Children.OfType<Button>().Concat(windowBar.Children.OfType<Button>()))
        {
            bool active = button.Tag is TimeSpan span ? span == range : (string?)button.Tag == selectedWindow;
            button.Classes.Set("accent", active);
        }
    }

    void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var series = rows.Where(r => r.Window == selectedWindow).ToList();
        string title = series.Count > 0 ? TitleFor(selectedWindow, series[^1].Label) : "";
        chart.SetData(series, now - range, now, title, range, service.Settings);

        var changes = Changes(series);
        list.ItemsSource = changes.AsEnumerable().Reverse().Take(3000).ToList();

        double spent = changes.Where(c => c.Delta > 0).Sum(c => c.Delta);
        chips.Children.Clear();
        var items = series.Count == 0
            ? new List<(string, Level)> { ("這段期間沒有紀錄", Level.Unknown) }
            : [
                ($"觀察 {series.Count} 筆", Level.Fair),
                ($"用掉 {spent:0} 個百分點", spent > 0 ? Level.Low : Level.Plenty),
                ($"重置 {changes.Count(c => c.IsReset)} 次", Level.Plenty),
                ($"未監測 {changes.Count(c => c.IsGap)} 段", Level.Unknown),
            ];
        foreach (var (text, level) in items)
        {
            var (start, end) = MacTheme.Gradient(level);
            chips.Children.Add(new Border
            {
                Background = level == Level.Unknown ? MacTheme.Solid(MacTheme.Raised) : MacTheme.Horizontal(start, end),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = text, FontWeight = FontWeight.Bold, Foreground = Brushes.White, FontSize = MacDraw.Pt(9.5) },
            });
        }
    }

    sealed record ChangeRow(DateTimeOffset At, string Label, double Used, double Remaining, double Delta, string Text, bool IsReset, bool IsGap, Color? Highlight);

    Control RowView(ChangeRow row)
    {
        var color = MacTheme.Solid(row.Highlight ?? MacTheme.Text);
        TextBlock Cell(string text, HorizontalAlignment align = HorizontalAlignment.Left) =>
            new() { Text = text, Foreground = color, HorizontalAlignment = align, FontSize = MacDraw.Pt(9.5) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,120,70,70,*") };
        var cells = new Control[]
        {
            Cell(row.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            Cell(TitleFor(selectedWindow, row.Label)),
            Cell(row.Used.ToString("0", CultureInfo.InvariantCulture) + "%", HorizontalAlignment.Right),
            Cell(row.Remaining.ToString("0", CultureInfo.InvariantCulture) + "%", HorizontalAlignment.Right),
            Cell(row.Text),
        };
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].Margin = new Thickness(i == 4 ? 16 : 0, 0, i is 2 or 3 ? 4 : 0, 0);
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    /// <summary>The first sample, every change in usage, monitoring gaps and resets.</summary>
    static List<ChangeRow> Changes(List<UsageSample> series)
    {
        var result = new List<ChangeRow>();
        UsageSample? previous = null;
        foreach (var s in series)
        {
            if (previous == null)
            {
                result.Add(new ChangeRow(s.At, s.Label, s.Used, s.Remaining, 0, "開始觀察", false, false, null));
            }
            else
            {
                double delta = s.Used - previous.Used;
                bool reset = delta < -5 || (!ForecastCalculator.SameReset(previous.Reset, s.Reset) && delta < 0);
                if (reset)
                    result.Add(new ChangeRow(s.At, s.Label, s.Used, s.Remaining, 0, $"↺ 已重置：剩餘 {previous.Remaining:0}% → {s.Remaining:0}%", true, false, MacTheme.Good));
                else if (s.Gap)
                    result.Add(new ChangeRow(s.At, s.Label, s.Used, s.Remaining, Math.Max(0, delta),
                        $"監測缺口後恢復（{Format.Duration(s.At - previous.At)}未觀察）" + (delta > 0 ? $"，期間用掉 {delta:0} 個百分點" : ""), false, true, MacTheme.Muted));
                else if (delta != 0)
                    result.Add(new ChangeRow(s.At, s.Label, s.Used, s.Remaining, delta, $"剩餘 {(delta > 0 ? "−" : "+")}{Math.Abs(delta):0} 個百分點", false, false, null));
            }
            previous = s;
        }
        return result;
    }

    async Task ExportCsvAsync()
    {
        var now = DateTimeOffset.Now;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "匯出 CSV",
            SuggestedFileName = $"claude-usage-{(now - range):yyyyMMdd}-{now:yyyyMMdd}.csv",
            FileTypeChoices = [new FilePickerFileType("CSV 表格") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;
        try
        {
            HistoryStore.ExportCsv(path, rows);
            MacDialog.Show(this, "匯出完成", $"已匯出 {rows.Count} 筆（所有額度）。\n檔案含使用時間，分享前請先檢查。");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MacDialog.Show(this, "匯出失敗", "無法寫入這個位置，請換一個資料夾。");
        }
    }
}

/// <summary>
/// The remaining curve: colour follows the amount left, the area under it uses the same gradient, low and
/// critical zones are tinted, unobserved time is hatched, and the curve draws itself in on load.
/// </summary>
internal sealed class HistoryChartView : Control
{
    readonly DispatcherTimer frame = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly Stopwatch reveal = new();
    List<UsageSample> series = [];
    DateTimeOffset from, to;
    string title = "";
    TimeSpan range;
    int warn = 20, critical = 5;
    int hoverIndex = -1;
    double progress = 1;

    public HistoryChartView()
    {
        ClipToBounds = true;
        frame.Tick += (_, _) =>
        {
            double t = Math.Min(1, reveal.ElapsedMilliseconds / 650.0);
            progress = 1 - Math.Pow(1 - t, 3);
            InvalidateVisual();
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
        if (MacShell.AnimationsEnabled)
        {
            progress = 0;
            reveal.Restart();
            frame.Start();
        }
        InvalidateVisual();
    }

    /// <summary>Finishes the draw-in at once; used by --screenshot.</summary>
    public void Settle()
    {
        frame.Stop();
        progress = 1;
        InvalidateVisual();
    }

    Rect Plot => new(52, 40, Math.Max(0, Bounds.Width - 70), Math.Max(0, Bounds.Height - 70));

    double X(DateTimeOffset t) => Plot.X + (t - from).TotalSeconds / Math.Max(1, (to - from).TotalSeconds) * Plot.Width;

    double Y(double remaining) => Plot.Bottom - remaining / 100 * Plot.Height;

    IBrush LevelBrush(byte alpha)
    {
        double warnPos = 1 - warn / 100.0, criticalPos = 1 - critical / 100.0;
        var plot = Plot;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, plot.Top, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, plot.Bottom, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(MacTheme.Alpha(MacTheme.Good, alpha), 0),
                new GradientStop(MacTheme.Alpha(MacTheme.Bright(Level.Fair), alpha), 0.5),
                new GradientStop(MacTheme.Alpha(MacTheme.Bright(Level.Fair), alpha), Math.Max(0.5, warnPos - 0.02)),
                new GradientStop(MacTheme.Alpha(MacTheme.Warning, alpha), warnPos),
                new GradientStop(MacTheme.Alpha(MacTheme.Danger, alpha), criticalPos),
                new GradientStop(MacTheme.Alpha(MacTheme.Danger, alpha), 1),
            },
        };
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(MacTheme.Solid(MacTheme.Surface), null, new Rect(Bounds.Size));
        var plot = Plot;
        if (plot.Width < 40 || plot.Height < 40)
            return;
        MacDraw.Text(ctx, title.Length > 0 ? title + " · 剩餘曲線" : "剩餘曲線", MacTheme.Ui(FontWeight.Bold), 11, MacTheme.Text, new Rect(12, 9, 400, 22), shadow: false);
        string rangeText = $"{from.ToLocalTime():MM/dd HH:mm} – {to.ToLocalTime():MM/dd HH:mm}";
        MacDraw.Text(ctx, rangeText, MacTheme.Ui(), 9, MacTheme.Muted, new Rect(Bounds.Width - 214, 12, 200, 18), TextAlignment.Right, shadow: false);

        ctx.DrawRectangle(MacTheme.Solid(MacTheme.Alpha(MacTheme.Warning, 22)), null, new Rect(new Point(plot.Left, Y(warn)), new Point(plot.Right, Y(critical))));
        ctx.DrawRectangle(MacTheme.Solid(MacTheme.Alpha(MacTheme.Danger, 30)), null, new Rect(new Point(plot.Left, Y(critical)), new Point(plot.Right, plot.Bottom)));
        var grid = new Pen(MacTheme.Solid(MacTheme.Track), 1);
        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            double y = Y(pct);
            ctx.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            MacDraw.Text(ctx, pct + "%", MacTheme.Ui(), 9, MacTheme.Muted, new Rect(0, y - 9, plot.Left - 6, 18), TextAlignment.Right, shadow: false);
        }
        int ticks = Math.Max(2, Math.Min(7, (int)(plot.Width / 110)));
        string format = range <= TimeSpan.FromDays(1) ? "HH:mm" : range <= TimeSpan.FromDays(7) ? "MM/dd HH:mm" : "MM/dd";
        for (int i = 0; i <= ticks; i++)
        {
            var t = from + (to - from) * i / ticks;
            double x = X(t);
            MacDraw.Text(ctx, t.ToLocalTime().ToString(format, CultureInfo.InvariantCulture), MacTheme.Ui(), 9, MacTheme.Muted,
                new Rect(x - 50, plot.Bottom + 6, 100, 18), TextAlignment.Center, shadow: false);
        }

        if (series.Count == 0)
        {
            MacDraw.Text(ctx, "這段期間沒有紀錄。工具開著時才會累積，關機或休眠期間不會補資料。", MacTheme.Ui(), 10, MacTheme.Muted, plot, TextAlignment.Center, middle: true, shadow: false);
            return;
        }

        var hatch = MacTheme.Solid(Color.FromRgb(0x2A, 0x26, 0x40));
        DateTimeOffset? previousAt = null;
        foreach (var sample in series)
        {
            if (previousAt is { } p && Broken(p, sample))
                ctx.DrawRectangle(hatch, null, new Rect(new Point(X(p), plot.Top), new Point(X(sample.At), plot.Bottom)));
            previousAt = sample.At;
        }
        if (X(series[0].At) - plot.Left > 4)
            ctx.DrawRectangle(hatch, null, new Rect(new Point(plot.Left, plot.Top), new Point(X(series[0].At), plot.Bottom)));

        using (ctx.PushClip(new Rect(new Point(plot.Left - 4, plot.Top - 6), new Point(plot.Left + plot.Width * progress + 4, plot.Bottom + 2))))
        {
            var area = LevelBrush(70);
            var line = new Pen(LevelBrush(255), 2.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var resetPen = new Pen(MacTheme.Solid(MacTheme.Good), 1.4, new DashStyle([4, 3], 0));
            var points = new List<Point>();
            void Flush()
            {
                if (points.Count == 1)
                {
                    ctx.DrawEllipse(LevelBrush(255), null, points[0], 3, 3);
                }
                else if (points.Count > 1)
                {
                    var fill = new StreamGeometry();
                    using (var g = fill.Open())
                    {
                        g.BeginFigure(points[0], true);
                        foreach (var p in points.Skip(1))
                            g.LineTo(p);
                        g.LineTo(new Point(points[^1].X, plot.Bottom));
                        g.LineTo(new Point(points[0].X, plot.Bottom));
                        g.EndFigure(true);
                    }
                    ctx.DrawGeometry(area, null, fill);
                    var stroke = new StreamGeometry();
                    using (var g = stroke.Open())
                    {
                        g.BeginFigure(points[0], false);
                        foreach (var p in points.Skip(1))
                            g.LineTo(p);
                        g.EndFigure(false);
                    }
                    ctx.DrawGeometry(null, line, stroke);
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
                        double x = X(sample.At);
                        ctx.DrawLine(resetPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                        PaintTag(ctx, "↺ 重置", new Point(x + 4, plot.Bottom - 24), Level.Plenty);
                    }
                }
                points.Add(new Point(X(sample.At), Y(sample.Remaining)));
            }
            Flush();
        }

        if (progress >= 1)
        {
            var last = series[^1];
            var at = new Point(X(last.At), Y(last.Remaining));
            ctx.DrawEllipse(MacTheme.Solid(MacTheme.Alpha(MacTheme.Text, 50)), null, at, 7, 7);
            ctx.DrawEllipse(MacTheme.Solid(MacTheme.Text), null, at, 3.5, 3.5);
        }

        PaintTag(ctx, $"偏低 ≤{warn}%", new Point(plot.Left + 6, Y(warn) + 3), Level.Low);
        PaintTag(ctx, $"告急 ≤{critical}%", new Point(plot.Left + 90, Y(warn) + 3), Level.Critical);

        if (hoverIndex >= 0 && hoverIndex < series.Count)
        {
            var sample = series[hoverIndex];
            double x = X(sample.At), y = Y(sample.Remaining);
            ctx.DrawLine(new Pen(MacTheme.Solid(MacTheme.Muted), 1, new DashStyle([1, 2], 0)), new Point(x, plot.Top), new Point(x, plot.Bottom));
            ctx.DrawEllipse(MacTheme.Solid(MacTheme.Text), null, new Point(x, y), 4, 4);
            string text = $"{sample.At.ToLocalTime():MM/dd HH:mm}  剩 {sample.Remaining:0}%（已用 {sample.Used:0}%）";
            var label = MacDraw.Format(text, MacTheme.Ui(), 9, Colors.White);
            var box = new Rect(x + 12, y - label.Height - 18, label.WidthIncludingTrailingWhitespace + 16, label.Height + 10);
            if (box.Right > Bounds.Width - 4)
                box = box.WithX(x - box.Width - 12);
            if (box.Top < plot.Top)
                box = box.WithY(y + 12);
            var level = sample.Remaining <= critical ? Level.Critical : sample.Remaining <= warn ? Level.Low : sample.Remaining <= 50 ? Level.Fair : Level.Plenty;
            var (start, end) = MacTheme.Gradient(level);
            ctx.DrawRectangle(MacTheme.Horizontal(start, end), null, box, 8, 8);
            ctx.DrawText(label, new Point(box.X + 8, box.Y + 5));
        }
    }

    void PaintTag(DrawingContext ctx, string text, Point at, Level level)
    {
        var label = MacDraw.Format(text, MacTheme.Ui(), 9, Colors.White);
        var r = new Rect(at.X, at.Y, label.WidthIncludingTrailingWhitespace + 12, label.Height + 4);
        var (start, end) = MacTheme.Gradient(level);
        ctx.DrawRectangle(MacTheme.Horizontal(start, end), null, r, r.Height / 2, r.Height / 2);
        ctx.DrawText(label, new Point(r.X + 6, r.Y + 2));
    }

    static bool Broken(DateTimeOffset previous, UsageSample s) =>
        s.Gap || (s.At - previous).TotalSeconds > Math.Max(60, s.Interval) * 3;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        int best = -1;
        double bestDistance = double.MaxValue;
        if (series.Count > 0 && Plot.Contains(at))
        {
            for (int i = 0; i < series.Count; i++)
            {
                double d = Math.Abs(X(series[i].At) - at.X);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
        }
        int index = bestDistance <= 24 ? best : -1;
        if (index != hoverIndex)
        {
            hoverIndex = index;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hoverIndex = -1;
        InvalidateVisual();
    }
}
