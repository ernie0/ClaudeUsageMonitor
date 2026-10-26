using System.Drawing;

namespace ClaudeUsageMonitor;

/// <summary>What a drawn page needs between frames: its animation clock, the pointer, the folded data source and what can be clicked.</summary>
internal sealed class PageState
{
    public readonly Motion Motion = new();
    public PointF? Pointer;
    public bool SourceOpen;
    public readonly List<Hit> Hits = [];

    public string? Hover
    {
        get => Motion.Hover;
        set => Motion.Hover = value;
    }

    public string? HitAt(PointF point)
    {
        for (int i = Hits.Count - 1; i >= 0; i--)
        {
            if (Hits[i].Bounds.Contains(point))
                return Hits[i].Action;
        }
        return null;
    }

    /// <summary>Areas starting with "see:" only show a tooltip: no hand cursor, nothing happens on click.</summary>
    public static bool Clickable(string? action) => action != null && !action.StartsWith("see:");

    /// <summary>Eased hover of a button, and the light sweeping across it right after the pointer arrives.</summary>
    public (float Hover, float Sweep) Over(string action, RectangleF r)
    {
        var area = RectangleF.Inflate(r, 12, 12);
        return (Motion.HoverOf(action, area), Motion.Sweep(action, area));
    }
}

/// <summary>
/// The details dashboard: header with plan and status, a glass card per main quota (ring, status,
/// countdown, burn rate, sparkline, used/remaining bar), the one-line conclusion, a row per window with
/// every fact, and the footer with the folded data source and the action buttons. Everything enters with
/// a short cascade (cards rise, rings sweep and numbers count up, rows slide in), cards and rows lift and
/// catch a sweep of light under the pointer, and the refresh icon and sunburst turn while querying.
/// Actions: refresh (and refresh:top, the same button in the header), history, analysis, official, source,
/// folder, card:{id}, row:{id}.
/// </summary>
internal static class DetailsPage
{
    const float M = 24;

    public static float Height(MonitorService service, PageState state)
    {
        int rows = Math.Max(1, service.Current?.Windows.Count ?? 1);
        return 92 + 238 + 22 + 56 + 22 + 34 + rows * 96 + 12 + (state.SourceOpen ? 150 : 110) + (ClaudeLocator.Warning.Length > 0 ? 22 : 0);
    }

    public static void Render(IPaint p, float width, float viewHeight, MonitorService service, PageState state, DateTimeOffset now)
    {
        state.Hits.Clear();
        var m = state.Motion;
        m.BeginFrame();
        var snapshot = service.Current;
        float height = Math.Max(viewHeight, Height(service, state));
        p.Cached("page", new SizeF(width, height), b => PageKit.Background(b, width, height));

        // Header: drops into place while the sunburst turns in; it keeps turning while querying.
        var top = new RectangleF(0, 0, width, 90);
        float enter = Motion.Out(m.In("top", top, 0, duration: 560));
        string? status = snapshot == null ? null : $"{snapshot.PlanLabel} · {snapshot.ObservedAt.ToLocalTime():HH:mm} 更新";
        var dot = service.ErrorCode.Length > 0 ? PageKit.Hex(0xFB7185) : service.Fresh ? PageKit.Green : PageKit.Hex(0xFBBF24);
        float refreshWidth = PageKit.ButtonWidth(p, "立即更新", IconKind.Refresh);
        float busy = m.Spin(service.Busy, top);
        using (PageKit.Enter(p, enter, 0, -14))
        {
            PageKit.Header(p, width, "Claude 額度", "即時查看使用額度與重置時間，掌握使用狀態", snapshot?.PlanLabel, status, dot, refreshWidth + 12, 140 * (1 - enter) + busy * 0.5f);
            var refresh = new RectangleF(width - M - refreshWidth, 24, refreshWidth, 40);
            var (hover, sweep) = state.Over("refresh:top", refresh);
            PageKit.Button(p, state.Hits, "refresh:top", refresh, "立即更新", IconKind.Refresh, false, hover, sweep, busy);
        }

        float y = 92;
        y = Cards(p, service, state, now, y, width) + 22;
        y = Banner(p, service, state, now, y, width) + 22;

        var title = new RectangleF(M, y, width - M * 2, 30);
        float t = Motion.Out(m.In("rows", title, 520, duration: 520));
        using (PageKit.Enter(p, t, -16, 0))
        {
            PageKit.Icon(p, IconKind.List, new RectangleF(M + 2, y + 6, 20, 20), PageKit.Text, 2f);
            p.Text("詳細資訊", new RectangleF(M + 34, y, 200, 30), 14, PageKit.Text, bold: true, middle: true);
            p.Line(new PointF(M + 140, y + 16), new PointF(M + 140 + (width - M * 2 - 140) * t, y + 16), PageKit.Hex(0x2E3358), 1);
        }
        y += 42;

        int index = 0;
        foreach (var w in snapshot?.Windows ?? [])
            y = Row(p, service, state, w, now, y, width, index++) + 12;
        if (snapshot == null)
        {
            p.Text(service.ErrorCode.Length > 0 ? service.Status : "正在向 Claude Code 取得額度…", new RectangleF(M, y, width - M * 2, 40), 11, PageKit.Muted, middle: true);
            y += 52;
        }
        Footer(p, service, state, now, y + 4, width);
    }

    static List<QuotaWindow> CardWindows(UsageSnapshot? snapshot)
    {
        if (snapshot == null)
            return [];
        var list = new List<QuotaWindow>();
        if (snapshot.Session is { } s)
            list.Add(s);
        if (snapshot.Weekly is { } w)
            list.Add(w);
        list.AddRange(snapshot.Scoped.OrderBy(x => x.RemainingPercent).Take(3 - list.Count));
        return list;
    }

    static float Cards(IPaint p, MonitorService service, PageState state, DateTimeOffset now, float y, float width)
    {
        var m = state.Motion;
        var windows = CardWindows(service.Current);
        const float H = 238, Gap = 20;
        if (windows.Count == 0)
        {
            PageKit.Panel(p, new RectangleF(M, y, width - M * 2, H), 20);
            p.Text(service.Status, new RectangleF(M, y, width - M * 2, H), 12, PageKit.Muted, align: TextAlign.Center, middle: true);
            return y + H;
        }
        float cardW = (width - M * 2 - Gap * (windows.Count - 1)) / windows.Count;
        var samples = service.RecentSamples();
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var r = new RectangleF(M + i * (cardW + Gap), y, cardW, H);
            string action = "card:" + w.Id;
            state.Hits.Add(new Hit(r, action));
            var area = RectangleF.Inflate(r, 16, 34);
            // Each card rises and pops in a little after the one before.
            float enter = m.In("cards", area, 120 + i * 110, 700);
            if (enter <= 0)
                continue;
            var level = Levels.Of(w.RemainingPercent, service.Settings);
            var (_, _, light) = PageKit.Tint(level);
            float hover = m.HoverOf(action, area);
            using var entering = PageKit.Enter(p, Motion.Out(enter), 0, 30, 0.9f + 0.1f * Motion.Back(enter), PageKit.Center(r));
            using var lifted = p.Transform(0, -5 * hover);
            PageKit.Card(p, r, level, 20, hover);
            // A glint crosses the glass once the card has landed, and again whenever the pointer arrives.
            PageKit.Shine(p, r, 20, m.In("cards", area, 620 + i * 110, 900));
            PageKit.Shine(p, r, 20, m.Sweep(action, area));

            // Trend of what is left, drawn in from the left, in the free space right of the status pill.
            var trend = Trend(samples, service.Current!.AccountKey, w, now);
            var spark = new RectangleF(r.Right - Math.Min(150, r.Width * 0.34f) - 22, r.Y + 62, Math.Min(150, r.Width * 0.34f), 42);
            float draw = Motion.Out(m.In("cards", area, 380 + i * 110, 900));
            using (p.Clip(RectangleF.FromLTRB(spark.X - 6, spark.Y - 8, spark.X + (spark.Width + 12) * draw, spark.Bottom + 6), 0))
                PageKit.Sparkline(p, spark, trend, light, 190);

            float radius = Math.Min(64, r.Height * 0.27f);
            var ring = new PointF(r.X + 28 + radius, r.Y + 26 + radius);
            double remaining = m.Value("ring:" + w.Id, w.RemainingPercent, area, 1100);
            PageKit.Ring(p, ring, radius, 12, remaining, light);
            p.Text(Format.Percent(remaining), new RectangleF(ring.X - radius, ring.Y - 26, radius * 2, 34), 22, Color.White, bold: true, align: TextAlign.Center);
            p.Text("剩餘", new RectangleF(ring.X - radius, ring.Y + 8, radius * 2, 20), 10.5f, PageKit.Hex(0xE6E2F8), align: TextAlign.Center);

            float x = ring.X + radius + 26;
            float textWidth = r.Right - x - 18;
            PageKit.Icon(p, Insights.Icon(w) switch { TileIcon.Clock => IconKind.Clock, TileIcon.Calendar => IconKind.Calendar, _ => IconKind.Chip },
                new RectangleF(x, r.Y + 28, 22, 22), Color.White, 2f);
            p.Text(Insights.Title(w), new RectangleF(x + 32, r.Y + 22, textWidth - 56, 32), 14, Color.White, bold: true, middle: true);
            PageKit.Icon(p, IconKind.ChevronRight, new RectangleF(r.Right - 34 + 4 * hover, r.Y + 30, 16, 16), PageKit.WithAlpha(Color.White, (int)(200 + 55 * hover)), 2f);
            PageKit.Pill(p, new PointF(x, r.Y + 66), Levels.Word(level, w.RemainingPercent), PageKit.WithAlpha(light, 120), Color.White, 10f, 28);
            p.Text(Format.ShortCountdown(w.ResetsAt, now), new RectangleF(x, r.Y + 110, textWidth, 26), 13, Color.White, bold: true);
            p.Text(Insights.RateText(service, w), new RectangleF(x, r.Y + 142, textWidth, 22), 10.5f, PageKit.Hex(0xE6E2F8));

            var bar = new RectangleF(r.X + 24, r.Bottom - 48, r.Width - 48, 8);
            double used = m.Value("used:" + w.Id, w.UsedPercent, area, 1100);
            PageKit.Progress(p, bar, used / 100, light);
            p.Text($"已使用 {used:0}%", new RectangleF(bar.X, bar.Bottom + 8, bar.Width / 2, 20), 10f, PageKit.Hex(0xE6E2F8));
            p.Text($"剩餘 {Format.Percent(remaining)}", new RectangleF(bar.X + bar.Width / 2, bar.Bottom + 8, bar.Width / 2, 20), 10f, PageKit.Hex(0xE6E2F8), align: TextAlign.Right);
        }
        return y + H;
    }

    /// <summary>The one-line conclusion: slides in from the left, its sparkle pops and turns as it lands.</summary>
    static float Banner(IPaint p, MonitorService service, PageState state, DateTimeOffset now, float y, float width)
    {
        var m = state.Motion;
        var insight = Insights.Build(service, now);
        var banner = new RectangleF(M, y, width - M * 2, 56);
        var area = RectangleF.Inflate(banner, 40, 8);
        float enter = Motion.Out(m.In("banner", area, 430, 620));
        if (enter <= 0)
            return banner.Bottom;
        using var entering = PageKit.Enter(p, enter, -36, 0);
        PageKit.Panel(p, banner, 16);
        PageKit.Shine(p, banner, 16, m.In("banner", area, 900, 1000), 48);
        float pop = m.In("banner", area, 620, 700);
        var star = new RectangleF(banner.X + 22, banner.Y + 16, 24, 24);
        using (p.Transform(0, 0, Math.Max(0.01f, Motion.Back(pop)), 180 * (1 - Motion.Out(pop)), PageKit.Center(star)))
            PageKit.Icon(p, IconKind.Sparkle, star, insight.Level is Level.Low or Level.Critical ? PageKit.Hex(0xFBBF24) : PageKit.Orange, 0);
        p.Text(insight.Text, new RectangleF(banner.X + 62, banner.Y, banner.Width - 120, banner.Height), 12f, PageKit.Text, middle: true);
        float textWidth = Math.Min(banner.Width - 120, p.Measure(insight.Text, 12f));
        PageKit.Icon(p, IconKind.Info, new RectangleF(banner.X + 70 + textWidth, banner.Y + 19, 18, 18), PageKit.Muted, 1.5f);
        return banner.Bottom;
    }

    /// <summary>Remaining percentage over the last day (5-hour window) or week, down-sampled for a sparkline.</summary>
    static List<double> Trend(List<UsageSample> samples, string account, QuotaWindow w, DateTimeOffset now)
    {
        var from = now - (w.Id == WindowIds.FiveHour ? TimeSpan.FromHours(24) : TimeSpan.FromDays(7));
        var series = samples.Where(s => s.Account == account && s.Window == w.Id && s.At >= from).Select(s => s.Remaining).ToList();
        if (series.Count <= 40)
            return series;
        return Enumerable.Range(0, 40).Select(i => series[i * (series.Count - 1) / 39]).ToList();
    }

    static float Row(IPaint p, MonitorService service, PageState state, QuotaWindow w, DateTimeOffset now, float y, float width, int index)
    {
        const float H = 84;
        var m = state.Motion;
        var r = new RectangleF(M, y, width - M * 2, H);
        string action = "row:" + w.Id;
        state.Hits.Add(new Hit(r, action));
        var area = RectangleF.Inflate(r, 14, 10);
        area.Width += 50;
        // Rows slide in from the right, one after another.
        float enter = m.In("rows", area, 600 + index * 100, 650);
        if (enter <= 0)
            return y + H;
        var level = Levels.Of(w.RemainingPercent, service.Settings);
        var (top, _, light) = PageKit.Tint(level);
        float hover = m.HoverOf(action, area);
        using var entering = PageKit.Enter(p, Motion.Out(enter), 48, 0);
        using var nudged = p.Transform(3 * hover, 0);
        PageKit.Glow(p, r, 16, light, (int)(90 * hover));
        p.Gradient(r, PageKit.WithAlpha(top, (int)(95 + 30 * hover)), PageKit.WithAlpha(PageKit.PanelBottom, 200), 16);
        PageKit.GlassSheen(p, r, 16, 0.75f + 0.2f * hover);
        PageKit.Shine(p, r, 16, m.Sweep(action, area), 44);
        p.Stroke(r, PageKit.WithAlpha(light, (int)(100 + 120 * hover)), 1.1f + 0.5f * hover, 16);

        var icon = new PointF(r.X + 46, r.Y + H / 2);
        p.Circle(icon, 25 + 2 * hover, PageKit.WithAlpha(light, (int)(50 + 30 * hover)), PageKit.WithAlpha(light, 170), 1.5f);
        PageKit.Icon(p, Insights.Icon(w) switch { TileIcon.Clock => IconKind.Clock, TileIcon.Calendar => IconKind.Calendar, _ => IconKind.Chip },
            new RectangleF(icon.X - 11, icon.Y - 11, 22, 22), Color.White, 2f);
        p.Text(Insights.Title(w), new RectangleF(r.X + 86, r.Y + 14, 150, 26), 12.5f, PageKit.Text, bold: true);
        PageKit.Pill(p, new PointF(r.X + 86, r.Y + 46), Levels.Word(level, w.RemainingPercent), PageKit.WithAlpha(light, 110), Color.White, 9.5f, 24);

        var forecast = service.Forecasts.GetValueOrDefault(w.Id) ?? Forecast.Empty;
        (string Outlook, Color Color) outlook = w.RemainingPercent <= 0 ? ("已用完，等待重置", PageKit.Hex(0xFB7185))
            : forecast.ExhaustsBeforeReset && forecast.ExhaustAt is { } at ? (Format.ResetAt(at, now) + " 用完", PageKit.Hex(0xFBBF24))
            : forecast.RatePerHour is not null ? ("撐得到重置", PageKit.Green)
            : ("還在觀察", PageKit.Text);
        var columns = new (string Label, string Value, Color Color, float Weight)[]
        {
            ("重置時間", w.ResetsAt is { } reset ? Format.ResetAt(reset, now) : "未提供", PageKit.Text, 1.25f),
            ("使用速度", w.RemainingPercent <= 0 ? "—" : forecast.RatePerHour is { } rate ? Format.Rate(rate) : "觀察中", PageKit.Text, 0.9f),
            ("每小時可用", forecast.BudgetPerHour is { } budget && w.RemainingPercent > 0 ? $"{Format.Rate(budget)} 以下" : "—", PageKit.Text, 1.05f),
            ("還有", w.ResetsAt is { } r2 && r2 > now ? Format.Duration(r2 - now) : "—", PageKit.Text, 1.05f),
            ("照目前速度", outlook.Outlook, outlook.Color, 1.2f),
            ("本期 Token", Insights.PeriodTokens(service, w, now), PageKit.Hex(0x7DD3FC), 0.95f),
        };
        float colStart = r.X + 250, usedWidth = 150, colEnd = r.Right - usedWidth - 44;
        float totalWeight = columns.Sum(c => c.Weight);
        float cx = colStart;
        p.Line(new PointF(colStart - 18, r.Y + 18), new PointF(colStart - 18, r.Bottom - 18), PageKit.WithAlpha(light, 60), 1);
        foreach (var (label, value, color, weight) in columns)
        {
            float cw = (colEnd - colStart) * weight / totalWeight;
            p.Text(label, new RectangleF(cx, r.Y + 18, cw - 10, 20), 9.5f, PageKit.Muted);
            p.Text(value, new RectangleF(cx, r.Y + 42, cw - 10, 24), 11.5f, color, bold: color != PageKit.Text);
            cx += cw;
        }
        double used = m.Value("rowused:" + w.Id, w.UsedPercent, area, 1000);
        p.Text("已使用", new RectangleF(colEnd, r.Y + 18, usedWidth, 20), 9.5f, PageKit.Muted);
        p.Text($"{used:0}%", new RectangleF(colEnd, r.Y + 42, 52, 24), 11.5f, PageKit.Text, bold: true);
        PageKit.Progress(p, new RectangleF(colEnd + 54, r.Y + 50, usedWidth - 62, 8), used / 100, light);
        PageKit.Icon(p, IconKind.ChevronRight, new RectangleF(r.Right - 32 + 4 * hover, r.Y + H / 2 - 8, 16, 16), PageKit.Mix(PageKit.Muted, Color.White, hover), 2f);
        return y + H;
    }

    static void Footer(IPaint p, MonitorService service, PageState state, DateTimeOffset now, float y, float width)
    {
        var m = state.Motion;
        var snapshot = service.Current;
        var area = new RectangleF(0, y - 130, width, 290);
        float enter = Motion.Out(m.In("footer", area, 820, 600));
        using var entering = PageKit.Enter(p, enter, 0, 14);
        PageKit.Icon(p, IconKind.Monitor, new RectangleF(M + 2, y + 4, 20, 20), PageKit.Muted, 1.6f);
        float x = M + 34, lineH = 22;
        var lines = new List<(string Text, Color Color)>();
        if (snapshot != null)
        {
            lines.Add(($"方案：{snapshot.PlanLabel}　　資料時間：{snapshot.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" + (service.Fresh ? "" : "（上次資料）"), PageKit.Muted));
            string extra = snapshot.Extra is { } e ? $"額外用量：{(e.Enabled ? "已開啟" : "未開啟")}，本月 {Format.Money(e.Used, e.Currency)}／上限 {Format.Money(e.MonthlyLimit, e.Currency)}　　" : "";
            lines.Add((extra + "狀態：" + service.Status
                + (service.NextPoll is { } next && !service.Busy ? $"　　下次更新 {next.ToLocalTime():HH:mm:ss}" : "")
                + (service.SlowedDown ? "（沒有變化，自動放慢）" : ""), PageKit.Muted));
        }
        if (ClaudeLocator.Warning.Length > 0)
            lines.Add(("⚠ " + ClaudeLocator.Warning, PageKit.Hex(0xFBBF24)));
        foreach (var (text, color) in lines)
        {
            p.Text(text, new RectangleF(x, y, width * 0.56f, lineH), 9.5f, color);
            y += lineH;
        }

        // The data source stays folded until asked for.
        string toggle = state.SourceOpen ? "▾ 收起資料來源" : "▸ 資料來源與紀錄位置";
        var toggleRect = new RectangleF(x, y + 2, p.Measure(toggle, 9.5f, bold: true) + 8, lineH);
        p.Text(toggle, toggleRect, 9.5f, PageKit.Mix(PageKit.Orange, PageKit.Hex(0xFDBA8C), m.HoverOf("source", toggleRect)), bold: true);
        state.Hits.Add(new Hit(toggleRect, "source"));
        y += lineH + 4;
        if (state.SourceOpen)
        {
            p.Text("資料來源：" + (service.ClaudePath.Length > 0 ? service.ClaudePath : "尚未找到 Claude Code")
                + (snapshot?.ClaudeVersion is { Length: > 0 } v ? $"（Claude Code {v}）" : ""), new RectangleF(x + 14, y, width * 0.56f, lineH), 9f, PageKit.Faint);
            y += lineH;
            p.Text("紀錄位置：" + service.Root, new RectangleF(x + 14, y, width * 0.56f, lineH), 9f, PageKit.Faint);
            y += lineH;
            var folder = new RectangleF(x + 14, y + 2, p.Measure("開啟紀錄資料夾", 9.5f, bold: true) + 30, lineH);
            PageKit.Icon(p, IconKind.Folder, new RectangleF(folder.X, folder.Y + 3, 16, 16), PageKit.Orange, 1.4f);
            p.Text("開啟紀錄資料夾", new RectangleF(folder.X + 22, folder.Y, folder.Width, lineH), 9.5f, PageKit.Mix(PageKit.Orange, PageKit.Hex(0xFDBA8C), m.HoverOf("folder", folder)), bold: true);
            state.Hits.Add(new Hit(folder, "folder"));
        }

        // Action buttons on the right.
        (string Action, string Text, IconKind Icon, bool Primary)[] buttons =
        [
            ("analysis", "用量分析", IconKind.Chart, false),
            ("history", "使用紀錄", IconKind.Document, false),
            ("official", "官方用量頁", IconKind.External, false),
            ("refresh", "立即更新", IconKind.Refresh, true),
        ];
        float right = width - M;
        float by = y - (state.SourceOpen ? 3 * lineH + 26 : lineH + 4) - lines.Count * lineH + 4;
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            var (action, text, icon, primary) = buttons[i];
            float bw = PageKit.ButtonWidth(p, text, icon);
            right -= bw;
            var r = new RectangleF(right, Math.Max(by, y - 120), bw, 44);
            var (hover, sweep) = state.Over(action, r);
            PageKit.Button(p, state.Hits, action, r, text, icon, primary, hover, sweep, action == "refresh" ? m.Spin(service.Busy, r) : 0);
            right -= 12;
        }
    }
}
