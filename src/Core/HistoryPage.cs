using System.Drawing;
using System.Globalization;

namespace ClaudeUsageMonitor;

internal sealed record ChangeRow(DateTimeOffset At, string Label, double Used, double Remaining, double Delta, string Text, bool IsReset, bool IsGap);

/// <summary>What the history page is showing and the pointer over it.</summary>
internal sealed class HistoryState
{
    public TimeSpan Range = TimeSpan.FromDays(7);
    public string Window = WindowIds.SevenDay;
    public string Search = "";
    public bool NewestFirst = true;
    public string? Hover;
    public PointF? Pointer;
    public float Intro = 1;
    public RectangleF SearchBox;
    public readonly List<Hit> Hits = [];

    public string? HitAt(PointF point)
    {
        for (int i = Hits.Count - 1; i >= 0; i--)
        {
            if (Hits[i].Bounds.Contains(point))
                return Hits[i].Action;
        }
        return null;
    }
}

/// <summary>The rows and numbers the history page shows, computed once per reload.</summary>
internal sealed class HistoryData
{
    public required List<UsageSample> Rows { get; init; }
    public required List<UsageSample> Series { get; init; }
    public required List<(string Id, string Title)> Windows { get; init; }
    public required List<ChangeRow> Changes { get; init; }
    public required TokenCounts Tokens { get; init; }
    public required bool TokensRead { get; init; }

    public static HistoryData Load(MonitorService service, HistoryState state, DateTimeOffset now)
    {
        string? account = service.Current?.AccountKey;
        var rows = service.History.Load(now - state.Range, now).Where(r => account == null || r.Account == account).ToList();
        var windows = rows.GroupBy(r => r.Window)
            .Select(g => (Id: g.Key, Title: HistoryPage.Title(g.Key, g.Last().Label)))
            .OrderBy(c => c.Id == WindowIds.FiveHour ? 0 : c.Id == WindowIds.SevenDay ? 1 : 2)
            .ThenBy(c => c.Title)
            .ToList();
        if (windows.Count > 0 && windows.All(c => c.Id != state.Window))
            state.Window = windows.Any(c => c.Id == WindowIds.SevenDay) ? WindowIds.SevenDay : windows[0].Id;
        var series = rows.Where(r => r.Window == state.Window).ToList();
        return new HistoryData
        {
            Rows = rows,
            Series = series,
            Windows = windows,
            Changes = ChangesOf(series),
            Tokens = service.Tokens.Sum(now - state.Range, now),
            TokensRead = service.Tokens.LastScan != null,
        };
    }

    /// <summary>The first sample, every change in usage, monitoring gaps and resets.</summary>
    public static List<ChangeRow> ChangesOf(List<UsageSample> series)
    {
        var list = new List<ChangeRow>();
        UsageSample? previous = null;
        foreach (var s in series)
        {
            if (previous == null)
            {
                list.Add(new ChangeRow(s.At, s.Label, s.Used, s.Remaining, 0, "開始觀察", false, false));
            }
            else
            {
                double delta = s.Used - previous.Used;
                bool reset = delta < -5 || (!ForecastCalculator.SameReset(previous.Reset, s.Reset) && delta < 0);
                if (reset)
                    list.Add(new ChangeRow(s.At, s.Label, s.Used, s.Remaining, 0, $"已重置：剩餘 {previous.Remaining:0}% → {s.Remaining:0}%", true, false));
                else if (s.Gap)
                    list.Add(new ChangeRow(s.At, s.Label, s.Used, s.Remaining, Math.Max(0, delta),
                        $"監測缺口後恢復（{Format.Duration(s.At - previous.At)}未觀察）" + (delta > 0 ? $"，期間用掉 {delta:0} 個百分點" : ""), false, true));
                else if (delta != 0)
                    list.Add(new ChangeRow(s.At, s.Label, s.Used, s.Remaining, delta, $"剩餘 {(delta > 0 ? "−" : "+")}{Math.Abs(delta):0} 個百分點", false, false));
            }
            previous = s;
        }
        return list;
    }
}

/// <summary>
/// The history page after the reference design: header, a toolbar of icon pills (range, window, export),
/// four stat cards, the remaining curve with a summary card, and the searchable, sortable change list.
/// Actions: range:{days}, window:{id}, export, sort.
/// </summary>
internal static class HistoryPage
{
    const float M = 24, RowH = 44;
    public const int MaxRows = 300;

    public static string Title(string id, string label) => id switch
    {
        WindowIds.FiveHour => "5 小時額度",
        WindowIds.SevenDay => "本週額度",
        _ => label.Replace("本週·", "").Replace("每週·", "") + " 本週",
    };

    static IconKind IconFor(string id) => id switch
    {
        WindowIds.FiveHour => IconKind.Clock,
        WindowIds.SevenDay => IconKind.Calendar,
        _ => IconKind.Chip,
    };

    public static List<ChangeRow> VisibleRows(HistoryData data, HistoryState state)
    {
        IEnumerable<ChangeRow> rows = state.NewestFirst ? data.Changes.AsEnumerable().Reverse() : data.Changes;
        string query = state.Search.Trim();
        if (query.Length > 0)
        {
            rows = rows.Where(r => r.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal)
                || Title(state.Window, r.Label).Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        return rows.Take(MaxRows).ToList();
    }

    public static float Height(HistoryData data, HistoryState state) =>
        92 + 88 + 20 + 108 + 20 + 430 + 20 + 74 + 40 + Math.Max(1, VisibleRows(data, state).Count) * RowH + 56;

    public static void Render(IPaint p, float width, float viewHeight, MonitorService service, HistoryData data, HistoryState state, DateTimeOffset now)
    {
        state.Hits.Clear();
        float height = Math.Max(viewHeight, Height(data, state));
        PageKit.Background(p, width, height);
        var snapshot = service.Current;
        string? status = snapshot == null ? null : $"{snapshot.PlanLabel} · {snapshot.ObservedAt.ToLocalTime():HH:mm} 更新";
        var dot = service.ErrorCode.Length > 0 ? PageKit.Hex(0xFB7185) : service.Fresh ? PageKit.Green : PageKit.Hex(0xFBBF24);
        PageKit.Header(p, width, "Claude 額度 · 使用紀錄", "查看使用趨勢與重置時間，合理規劃您的使用量", null, status, dot, 0);

        float y = Toolbar(p, data, state, 92, width) + 20;
        y = Stats(p, data, state, y, width) + 20;
        y = Chart(p, service, data, state, now, y, width) + 20;
        Table(p, data, state, y, width);
    }

    static float Toolbar(IPaint p, HistoryData data, HistoryState state, float y, float width)
    {
        var bar = new RectangleF(M, y, width - M * 2, 76);
        PageKit.Panel(p, bar, 20);
        float x = bar.X + 16, by = bar.Y + 16, bh = 44;
        foreach (int days in new[] { 1, 7, 30 })
        {
            string text = days == 1 ? "24 小時" : $"{days} 天";
            string action = "range:" + days;
            float bw = PageKit.ButtonWidth(p, text, IconKind.Calendar);
            Pill(p, state, new RectangleF(x, by, bw, bh), action, text, IconKind.Calendar, (int)state.Range.TotalDays == days, PageKit.Hex(0xF59E6B), PageKit.Hex(0xEC4899));
            x += bw + 10;
        }
        x += 14;
        foreach (var (id, title) in data.Windows)
        {
            float bw = PageKit.ButtonWidth(p, title, IconFor(id));
            if (x + bw > bar.Right - 160)
                break;
            Pill(p, state, new RectangleF(x, by, bw, bh), "window:" + id, title, IconFor(id), state.Window == id, PageKit.Violet, PageKit.Indigo);
            x += bw + 10;
        }
        float ew = PageKit.ButtonWidth(p, "匯出 CSV", IconKind.Download);
        Pill(p, state, new RectangleF(bar.Right - 16 - ew, by, ew, bh), "export", "匯出 CSV", IconKind.Download, false, PageKit.Violet, PageKit.Indigo);
        return bar.Bottom;
    }

    static void Pill(IPaint p, HistoryState state, RectangleF r, string action, string text, IconKind icon, bool active, Color from, Color to)
    {
        bool hover = state.Hover == action;
        float radius = r.Height / 2;
        if (active)
        {
            PageKit.Glow(p, r, radius, from, 110);
            p.Gradient(r, from, to, radius);
        }
        else
        {
            p.Fill(r, hover ? PageKit.Hex(0x2A2F58, 235) : PageKit.Hex(0x1C2042, 210), radius);
            p.Stroke(r, hover ? PageKit.WithAlpha(PageKit.Violet, 220) : PageKit.Hex(0x3C4378), 1.2f, radius);
        }
        float textWidth = p.Measure(text, 10.5f, bold: true);
        float x = r.X + (r.Width - textWidth - 24) / 2;
        PageKit.Icon(p, icon, new RectangleF(x, r.Y + r.Height / 2 - 8, 16, 16), PageKit.Text, 1.7f);
        p.Text(text, new RectangleF(x + 24, r.Y, textWidth + 4, r.Height), 10.5f, PageKit.Text, bold: true, middle: true);
        state.Hits.Add(new Hit(r, action));
    }

    static float Stats(IPaint p, HistoryData data, HistoryState state, float y, float width)
    {
        var series = data.Series;
        int empties = 0;
        for (int i = 0; i < series.Count; i++)
        {
            if (series[i].Remaining <= 0 && (i == 0 || series[i - 1].Remaining > 0))
                empties++;
        }
        double emptyShare = series.Count == 0 ? 0 : series.Count(s => s.Remaining <= 0) * 100.0 / series.Count;
        (string Label, string Value, string Unit, string Note, IconKind Icon, Color From, Color To)[] cards =
        [
            ("觀察筆數", series.Count.ToString("#,0"), "筆", "", IconKind.Chart, PageKit.Hex(0x6D28D9), PageKit.Hex(0x4338CA)),
            ("用完", empties.ToString(), "次", $"（占 {emptyShare:0}%）", IconKind.Check, PageKit.Hex(0x0F766E), PageKit.Hex(0x065F46)),
            ("重置", data.Changes.Count(c => c.IsReset).ToString(), "次", "", IconKind.Refresh, PageKit.Hex(0xC2410C), PageKit.Hex(0x9A3412)),
            ("未監測", data.Changes.Count(c => c.IsGap).ToString(), "段", "", IconKind.Clipboard, PageKit.Hex(0x1D4ED8), PageKit.Hex(0x1E3A8A)),
        ];
        const float H = 108, Gap = 18;
        float cw = (width - M * 2 - Gap * 3) / 4;
        for (int i = 0; i < cards.Length; i++)
        {
            var (label, value, unit, note, icon, from, to) = cards[i];
            var r = new RectangleF(M + i * (cw + Gap), y, cw, H);
            PageKit.Glow(p, r, 18, from, 60);
            p.Gradient(r, PageKit.WithAlpha(from, 150), PageKit.WithAlpha(to, 110), 18, vertical: true);
            PageKit.GlassSheen(p, r, 18, 0.9f);
            p.Stroke(r, PageKit.WithAlpha(PageKit.Mix(from, Color.White, 0.4f), 170), 1.2f, 18);
            var tile = new RectangleF(r.X + 22, r.Y + 24, 60, 60);
            p.Gradient(tile, PageKit.Mix(from, Color.White, 0.15f), to, 16, vertical: true);
            PageKit.GlassSheen(p, tile, 16, 0.8f);
            PageKit.Icon(p, icon, new RectangleF(tile.X + 16, tile.Y + 16, 28, 28), Color.White, 2.6f);
            p.Text(label, new RectangleF(r.X + 100, r.Y + 22, cw - 140, 22), 10.5f, PageKit.Hex(0xE4E1F7));
            p.Text(value, new RectangleF(r.X + 100, r.Y + 46, cw - 140, 40), 21, Color.White, bold: true);
            float valueWidth = p.Measure(value, 21, bold: true);
            p.Text(unit + note, new RectangleF(r.X + 106 + valueWidth, r.Y + 56, cw - 150 - valueWidth, 26), 11, PageKit.Hex(0xE4E1F7));
            PageKit.Icon(p, IconKind.ChevronRight, new RectangleF(r.Right - 34, r.Y + H / 2 - 8, 16, 16), PageKit.WithAlpha(Color.White, 200), 2f);
        }
        return y + H;
    }

    static float Chart(IPaint p, MonitorService service, HistoryData data, HistoryState state, DateTimeOffset now, float y, float width)
    {
        const float H = 430;
        var panel = new RectangleF(M, y, width - M * 2, H);
        PageKit.Panel(p, panel, 20);
        var current = service.Current?.Find(state.Window);
        var level = current == null ? Level.Fair : Levels.Of(current.RemainingPercent, service.Settings);
        var (_, _, light) = PageKit.Tint(level);
        string title = data.Series.Count > 0 ? Title(state.Window, data.Series[^1].Label) : "剩餘曲線";
        PageKit.Icon(p, IconFor(state.Window), new RectangleF(panel.X + 22, panel.Y + 22, 26, 26), light, 2.2f);
        p.Text(title + " · 剩餘曲線", new RectangleF(panel.X + 60, panel.Y + 16, 420, 38), 14, PageKit.Text, bold: true, middle: true);

        var from = now - state.Range;
        string rangeText = $"{from.ToLocalTime():yyyy/MM/dd HH:mm} – {now.ToLocalTime():yyyy/MM/dd HH:mm}";
        float summaryWidth = Math.Min(420, panel.Width * 0.34f);
        float rw = p.Measure(rangeText, 10f) + 58;
        var rangeBox = new RectangleF(panel.Right - 20 - rw, panel.Y + 16, rw, 40);
        p.Fill(rangeBox, PageKit.Hex(0x161A36, 230), 12);
        p.Stroke(rangeBox, PageKit.Hex(0x343A66), 1.1f, 12);
        PageKit.Icon(p, IconKind.Calendar, new RectangleF(rangeBox.X + 14, rangeBox.Y + 12, 16, 16), PageKit.Muted, 1.6f);
        p.Text(rangeText, new RectangleF(rangeBox.X + 40, rangeBox.Y, rw - 44, 40), 10f, PageKit.Muted, middle: true);

        var plot = RectangleF.FromLTRB(panel.X + 70, panel.Y + 84, panel.Right - summaryWidth - 44, panel.Bottom - 48);
        PlotCurve(p, data, state, light, plot, from, now);
        Summary(p, service, data, state, current, level, light, new RectangleF(panel.Right - summaryWidth - 20, panel.Y + 74, summaryWidth, H - 94), now);
        return panel.Bottom;
    }

    static void PlotCurve(IPaint p, HistoryData data, HistoryState state, Color light, RectangleF plot, DateTimeOffset from, DateTimeOffset to)
    {
        float X(DateTimeOffset t) => plot.Left + (float)((t - from).TotalSeconds / Math.Max(1, (to - from).TotalSeconds)) * plot.Width;
        float Y(double v) => plot.Bottom - (float)(v / 100) * plot.Height;
        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            p.Line(new PointF(plot.Left, Y(pct)), new PointF(plot.Right, Y(pct)), PageKit.Hex(0x272C52), 1);
            p.Text(pct + "%", new RectangleF(plot.Left - 60, Y(pct) - 10, 50, 20), 9.5f, PageKit.Muted, align: TextAlign.Right);
        }
        int ticks = Math.Max(2, Math.Min(8, (int)(plot.Width / 100)));
        string format = state.Range <= TimeSpan.FromDays(1) ? "HH:mm" : "MM/dd";
        for (int i = 0; i <= ticks; i++)
        {
            var t = from + (to - from) * i / ticks;
            p.Line(new PointF(X(t), plot.Top), new PointF(X(t), plot.Bottom), PageKit.Hex(0x1F2446), 1);
            p.Text(t.ToLocalTime().ToString(format, CultureInfo.InvariantCulture), new RectangleF(X(t) - 40, plot.Bottom + 10, 80, 18), 9.5f, PageKit.Muted, align: TextAlign.Center);
        }
        var series = data.Series;
        if (series.Count == 0)
        {
            p.Text("這段期間沒有紀錄。工具開著時才會累積，關機或休眠期間不會補資料。", plot, 11, PageKit.Muted, align: TextAlign.Center, middle: true);
            return;
        }

        static bool Broken(UsageSample previous, UsageSample s) =>
            s.Gap || (s.At - previous.At).TotalSeconds > Math.Max(60, s.Interval) * 3 || s.Used < previous.Used - 5;
        for (int i = 1; i < series.Count; i++)
        {
            if (series[i].Gap || (series[i].At - series[i - 1].At).TotalSeconds > Math.Max(60, series[i].Interval) * 3)
                p.Fill(RectangleF.FromLTRB(X(series[i - 1].At), plot.Top, X(series[i].At), plot.Bottom), Color.FromArgb(22, 255, 255, 255));
        }

        float reveal = plot.Left + plot.Width * state.Intro;
        using (p.Clip(RectangleF.FromLTRB(plot.Left - 8, plot.Top - 10, reveal + 8, plot.Bottom + 2), 0))
        {
            var run = new List<PointF>();
            void Flush()
            {
                if (run.Count > 1)
                {
                    p.Area(run, plot.Bottom, PageKit.WithAlpha(light, 110), PageKit.WithAlpha(light, 8));
                    p.Polyline(run, PageKit.WithAlpha(light, 45), 5);
                    p.Polyline(run, light, 2.6f);
                }
                else if (run.Count == 1)
                {
                    p.Circle(run[0], 3.5f, light);
                }
                run.Clear();
            }
            for (int i = 0; i < series.Count; i++)
            {
                if (i > 0 && Broken(series[i - 1], series[i]))
                {
                    Flush();
                    if (series[i].Used < series[i - 1].Used - 5)
                    {
                        float rx = X(series[i].At);
                        p.Line(new PointF(rx, plot.Top), new PointF(rx, plot.Bottom), PageKit.WithAlpha(PageKit.Green, 200), 1.4f, dashed: true);
                        PageKit.Pill(p, new PointF(rx + 6, plot.Bottom - 30), "↺ 重置", PageKit.WithAlpha(PageKit.Hex(0x0F766E), 230), Color.White, 9f, 22);
                    }
                }
                run.Add(new PointF(X(series[i].At), Y(series[i].Remaining)));
            }
            Flush();
            // A few markers along the curve, and a bright one at the latest value.
            int step = Math.Max(1, series.Count / 10);
            for (int i = 0; i < series.Count; i += step)
                p.Circle(new PointF(X(series[i].At), Y(series[i].Remaining)), 4, Color.White, light, 2);
            p.Circle(new PointF(X(series[^1].At), Y(series[^1].Remaining)), 9, PageKit.WithAlpha(light, 60));
            p.Circle(new PointF(X(series[^1].At), Y(series[^1].Remaining)), 5, Color.White, light, 2.4f);
        }

        if (state.Pointer is { } pointer && plot.Contains(pointer))
        {
            var nearest = series.MinBy(s => Math.Abs(X(s.At) - pointer.X))!;
            float x = X(nearest.At), y = Y(nearest.Remaining);
            p.Line(new PointF(x, plot.Top), new PointF(x, plot.Bottom), PageKit.WithAlpha(PageKit.Muted, 160), 1, dashed: true);
            p.Circle(new PointF(x, y), 6, Color.White, light, 2.5f);
            string text = $"{nearest.At.ToLocalTime():MM/dd HH:mm}　剩 {nearest.Remaining:0}%（已用 {nearest.Used:0}%）";
            float tw = p.Measure(text, 10f) + 28;
            var box = new RectangleF(x + 14, y - 46, tw, 34);
            if (box.Right > plot.Right)
                box.X = x - tw - 14;
            if (box.Top < plot.Top)
                box.Y = y + 14;
            p.Gradient(box, PageKit.Hex(0x2B2F5E, 245), PageKit.Hex(0x1C1F42, 245), 12, vertical: true);
            p.Stroke(box, PageKit.WithAlpha(light, 200), 1.2f, 12);
            p.Text(text, box, 10f, PageKit.Text, align: TextAlign.Center, middle: true);
        }
    }

    static void Summary(IPaint p, MonitorService service, HistoryData data, HistoryState state, QuotaWindow? current, Level level, Color light, RectangleF r, DateTimeOffset now)
    {
        PageKit.Card(p, r, level, 18, false, 0.7f);
        if (current == null)
        {
            p.Text("這個額度目前沒有資料", r, 11, PageKit.Muted, align: TextAlign.Center, middle: true);
            return;
        }
        float radius = Math.Min(70, r.Height * 0.2f);
        var ring = new PointF(r.X + 26 + radius, r.Y + 26 + radius);
        PageKit.Ring(p, ring, radius, 13, current.RemainingPercent, light, state.Intro);
        p.Text(Format.Percent(current.RemainingPercent), new RectangleF(ring.X - radius, ring.Y - 26, radius * 2, 34), 22, Color.White, bold: true, align: TextAlign.Center);
        p.Text("剩餘", new RectangleF(ring.X - radius, ring.Y + 8, radius * 2, 20), 10.5f, PageKit.Hex(0xE6E2F8), align: TextAlign.Center);

        float x = ring.X + radius + 24, tw = r.Right - x - 16;
        PageKit.Icon(p, IconFor(current.Id), new RectangleF(x, r.Y + 30, 22, 22), Color.White, 2f);
        p.Text(Insights.Title(current), new RectangleF(x + 30, r.Y + 24, tw - 30, 32), 13, Color.White, bold: true, middle: true);
        PageKit.Pill(p, new PointF(x, r.Y + 64), Levels.Word(level, current.RemainingPercent), PageKit.WithAlpha(light, 130), Color.White, 10f, 28);
        p.Text($"{current.UsedPercent:0}%", new RectangleF(x, r.Y + 100, tw, 36), 19, Color.White, bold: true);
        p.Text("已使用", new RectangleF(x, r.Y + 136, tw, 20), 10f, PageKit.Hex(0xE6E2F8));

        var bar = new RectangleF(r.X + 24, r.Y + 2 * radius + 52, r.Width - 48, 8);
        PageKit.Progress(p, bar, current.UsedPercent / 100 * state.Intro, light);
        p.Text($"已使用 {current.UsedPercent:0}%", new RectangleF(bar.X, bar.Bottom + 8, bar.Width / 2, 18), 9.5f, PageKit.Hex(0xE6E2F8));
        p.Text($"剩餘 {Format.Percent(current.RemainingPercent)}", new RectangleF(bar.X + bar.Width / 2, bar.Bottom + 8, bar.Width / 2, 18), 9.5f, PageKit.Hex(0xE6E2F8), align: TextAlign.Right);

        float sy = bar.Bottom + 40;
        p.Line(new PointF(r.X + 20, sy - 10), new PointF(r.Right - 20, sy - 10), PageKit.WithAlpha(Color.White, 40), 1);
        (IconKind Icon, string Label, string Value)[] facts =
        [
            (IconKind.Document, "觀察筆數", $"{data.Series.Count:#,0} 筆"),
            (IconKind.Clock, "重置時間", current.ResetsAt is { } reset && reset > now ? Format.ShortDuration(reset - now) : "—"),
            (IconKind.Chart, "區間 Token", data.TokensRead ? Format.Tokens(data.Tokens.Total) : "讀取中…"),
        ];
        float fw = (r.Width - 40) / facts.Length;
        for (int i = 0; i < facts.Length; i++)
        {
            float fx = r.X + 20 + i * fw;
            p.Circle(new PointF(fx + 18, sy + 22), 18, PageKit.WithAlpha(Color.White, 26), PageKit.WithAlpha(Color.White, 60), 1);
            PageKit.Icon(p, facts[i].Icon, new RectangleF(fx + 9, sy + 13, 18, 18), Color.White, 1.7f);
            p.Text(facts[i].Label, new RectangleF(fx + 44, sy + 2, fw - 48, 18), 9.5f, PageKit.Hex(0xE4E1F7));
            p.Text(facts[i].Value, new RectangleF(fx + 44, sy + 20, fw - 46, 24), 11f, Color.White, bold: true);
        }
    }

    static void Table(IPaint p, HistoryData data, HistoryState state, float y, float width)
    {
        var rows = VisibleRows(data, state);
        var panel = new RectangleF(M, y, width - M * 2, 74 + 40 + Math.Max(1, rows.Count) * RowH + 22);
        PageKit.Panel(p, panel, 20);
        PageKit.Icon(p, IconKind.Clipboard, new RectangleF(panel.X + 22, panel.Y + 24, 24, 24), PageKit.Violet, 2f);
        p.Text("使用紀錄明細", new RectangleF(panel.X + 58, panel.Y + 18, 300, 36), 14, PageKit.Text, bold: true, middle: true);

        string sort = state.NewestFirst ? "最新在上" : "最舊在上";
        float sw = p.Measure(sort, 10.5f) + 52;
        var sortBox = new RectangleF(panel.Right - 20 - sw, panel.Y + 16, sw, 40);
        p.Fill(sortBox, state.Hover == "sort" ? PageKit.Hex(0x2A2F58, 235) : PageKit.Hex(0x161A36, 230), 12);
        p.Stroke(sortBox, PageKit.Hex(0x343A66), 1.1f, 12);
        p.Text(sort, new RectangleF(sortBox.X + 16, sortBox.Y, sw - 40, 40), 10.5f, PageKit.Text, middle: true);
        PageKit.Icon(p, IconKind.ChevronDown, new RectangleF(sortBox.Right - 28, sortBox.Y + 13, 14, 14), PageKit.Muted, 1.8f);
        state.Hits.Add(new Hit(sortBox, "sort"));

        var search = new RectangleF(sortBox.X - 14 - 280, panel.Y + 16, 280, 40);
        p.Fill(search, PageKit.Hex(0x161A36, 230), 12);
        p.Stroke(search, state.Hover == "search" ? PageKit.WithAlpha(PageKit.Violet, 200) : PageKit.Hex(0x343A66), 1.1f, 12);
        PageKit.Icon(p, IconKind.Search, new RectangleF(search.X + 14, search.Y + 12, 16, 16), PageKit.Muted, 1.7f);
        // The host puts a real text box here; the page only draws the frame and icon.
        state.SearchBox = new RectangleF(search.X + 40, search.Y + 8, search.Width - 50, search.Height - 16);
        state.Hits.Add(new Hit(search, "search"));

        var header = new RectangleF(panel.X + 16, panel.Y + 74, panel.Width - 32, 40);
        p.Fill(header, PageKit.Hex(0x1E2347, 240), 10);
        float[] widths = [0.2f, 0.17f, 0.1f, 0.1f, 0.1f, 0.33f];
        string[] titles = ["時間", "額度", "已使用", "剩餘", "變化", "備註"];
        float cx = header.X + 18;
        for (int i = 0; i < titles.Length; i++)
        {
            p.Text(titles[i], new RectangleF(cx, header.Y, header.Width * widths[i], header.Height), 10f, PageKit.Muted, bold: true, middle: true);
            cx += header.Width * widths[i];
        }

        float ry = header.Bottom;
        if (rows.Count == 0)
            p.Text(state.Search.Trim().Length > 0 ? "沒有符合搜尋的紀錄" : "這段期間沒有紀錄", new RectangleF(header.X, ry, header.Width, RowH), 10.5f, PageKit.Muted, align: TextAlign.Center, middle: true);
        var light = PageKit.Tint(Level.Fair).Light;
        foreach (var row in rows)
        {
            var r = new RectangleF(header.X, ry, header.Width, RowH);
            p.Line(new PointF(r.X + 8, r.Bottom), new PointF(r.Right - 8, r.Bottom), PageKit.Hex(0x232848), 1);
            cx = r.X + 18;
            Color rowColor = row.IsReset ? PageKit.Green : row.IsGap ? PageKit.Muted : PageKit.Text;
            p.Text(row.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), new RectangleF(cx, r.Y, header.Width * widths[0], RowH), 10.5f, rowColor, middle: true);
            cx += header.Width * widths[0];
            string title = Title(state.Window, row.Label);
            float chipWidth = p.Measure(title, 9.5f, bold: true) + 44;
            var chip = new RectangleF(cx, r.Y + 8, chipWidth, RowH - 16);
            p.Fill(chip, PageKit.WithAlpha(light, 40), chip.Height / 2);
            p.Stroke(chip, PageKit.WithAlpha(light, 110), 1, chip.Height / 2);
            PageKit.Icon(p, IconFor(state.Window), new RectangleF(chip.X + 10, chip.Y + chip.Height / 2 - 7, 14, 14), light, 1.6f);
            p.Text(title, new RectangleF(chip.X + 30, chip.Y, chipWidth - 34, chip.Height), 9.5f, PageKit.Text, bold: true, middle: true);
            cx += header.Width * widths[1];
            p.Text(row.Used.ToString("0", CultureInfo.InvariantCulture) + "%", new RectangleF(cx, r.Y, header.Width * widths[2], RowH), 10.5f, rowColor, middle: true);
            cx += header.Width * widths[2];
            p.Text(row.Remaining.ToString("0", CultureInfo.InvariantCulture) + "%", new RectangleF(cx, r.Y, header.Width * widths[3], RowH), 10.5f, rowColor, middle: true);
            cx += header.Width * widths[3];
            (string arrow, Color color) = row.IsReset ? ("↺", PageKit.Green) : row.Delta > 0 ? ($"↓ {row.Delta:0}", PageKit.Hex(0xFB923C)) : ("→", PageKit.Muted);
            p.Text(arrow, new RectangleF(cx, r.Y, header.Width * widths[4], RowH), 10.5f, color, bold: true, middle: true);
            cx += header.Width * widths[4];
            p.Text(row.Text, new RectangleF(cx, r.Y, header.Width * widths[5] - 18, RowH), 10.5f, rowColor, middle: true);
            ry += RowH;
        }
        if (data.Changes.Count > MaxRows && state.Search.Trim().Length == 0)
            p.Text($"只顯示 {MaxRows} 筆；要看全部請用搜尋或「匯出 CSV」。", new RectangleF(header.X, ry + 2, header.Width, 20), 9.5f, PageKit.Muted);
    }
}
