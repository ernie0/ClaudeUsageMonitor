using System.Drawing;

namespace ClaudeUsageMonitor;

internal enum TextAlign { Left, Center, Right }

/// <summary>
/// The few drawing operations the analysis page needs, in logical (96-DPI) pixels. Windows implements it
/// with GDI+, the Mac app with Avalonia, so both draw the same page from one layout.
/// </summary>
internal interface IPaint
{
    void Fill(RectangleF r, Color color, float radius = 0);
    void Gradient(RectangleF r, Color start, Color end, float radius = 0, bool vertical = false);
    void Stroke(RectangleF r, Color color, float width, float radius = 0);
    void Line(PointF a, PointF b, Color color, float width, bool dashed = false);
    void Circle(PointF center, float radius, Color? fill, Color? stroke = null, float strokeWidth = 1);
    /// <summary>A round-capped arc; degrees clockwise from 3 o'clock.</summary>
    void Arc(PointF center, float radius, float startDegrees, float sweepDegrees, Color color, float width);
    void Polyline(IReadOnlyList<PointF> points, Color color, float width);
    void Polygon(IReadOnlyList<PointF> points, Color fill);
    /// <summary>Fills under a line down to <paramref name="baseline"/> with a top-to-bottom gradient.</summary>
    void Area(IReadOnlyList<PointF> points, float baseline, Color top, Color bottom);
    void Text(string text, RectangleF r, float points, Color color, bool bold = false, TextAlign align = TextAlign.Left, int maxLines = 1, bool middle = false);
    float Measure(string text, float points, bool bold = false);
    /// <summary>Limits drawing to a rounded rectangle until the returned scope is disposed.</summary>
    IDisposable Clip(RectangleF r, float radius);
    /// <summary>
    /// Scales and turns (clockwise degrees) everything drawn around <paramref name="origin"/>, then moves it
    /// by (<paramref name="dx"/>, <paramref name="dy"/>), until the returned scope is disposed.
    /// </summary>
    IDisposable Transform(float dx, float dy, float scale = 1, float degrees = 0, PointF origin = default);
    /// <summary>Multiplies the opacity of everything drawn until the returned scope is disposed.</summary>
    IDisposable Fade(float opacity);
    /// <summary>Draws something that only depends on its size (the page backdrop); the host may keep it as a picture.</summary>
    void Cached(string key, SizeF size, Action<IPaint> draw);
}

/// <summary>Tokens for the analysis: the selected range, per day, per model, and the current quota periods.</summary>
internal sealed record TokenReport(
    TokenCounts Range,
    IReadOnlyList<(DateTime Day, long Total)> Days,
    IReadOnlyList<(string Model, TokenCounts Counts)> Models,
    TokenCounts? Session,
    TokenCounts? Week,
    string Status,
    bool Scanning)
{
    public static TokenReport Build(TokenLedger ledger, UsageSnapshot? snapshot, int days, DateTimeOffset now)
    {
        var firstDay = now.ToLocalTime().Date.AddDays(-(days - 1));
        var from = new DateTimeOffset(firstDay, TimeZoneInfo.Local.GetUtcOffset(firstDay));
        var entries = ledger.Entries(from, now.AddMinutes(1));
        var range = entries.Aggregate(TokenCounts.Zero, (sum, e) => sum + e.Counts);
        var perDay = Enumerable.Range(0, days)
            .Select(i => firstDay.AddDays(i))
            .Select(day => (day, entries.Where(e => e.At.ToLocalTime().Date == day).Sum(e => e.Counts.Total)))
            .ToList();
        var models = entries.GroupBy(e => Format.ModelName(e.Model))
            .Select(g => (g.Key, g.Aggregate(TokenCounts.Zero, (sum, e) => sum + e.Counts)))
            .OrderByDescending(m => m.Item2.Total)
            .ToList();
        TokenCounts? Period(QuotaWindow? w, TimeSpan length) =>
            w?.ResetsAt is { } reset ? ledger.Sum(reset - length, now.AddMinutes(1)) : null;
        string status = ledger.Scanning ? "讀取對話紀錄中…"
            : ledger.Error.Length > 0 ? ledger.Error
            : ledger.LastScan is { } at ? $"{at.ToLocalTime():HH:mm} 讀取"
            : "尚未讀取";
        return new TokenReport(range, perDay, models, Period(snapshot?.Session, TimeSpan.FromHours(5)), Period(snapshot?.Weekly, TimeSpan.FromDays(7)), status, ledger.Scanning);
    }
}

/// <summary>Colours of the analysis page (same values as the app themes).</summary>
internal static class AnalysisPalette
{
    public static readonly Color Canvas = Color.FromArgb(0x14, 0x12, 0x1E);
    public static readonly Color Surface = Color.FromArgb(0x1E, 0x1B, 0x2E);
    public static readonly Color Raised = Color.FromArgb(0x2B, 0x27, 0x42);
    public static readonly Color Border = Color.FromArgb(0x3D, 0x38, 0x5C);
    public static readonly Color Text = Color.FromArgb(0xF4, 0xF2, 0xFF);
    public static readonly Color Muted = Color.FromArgb(0xA9, 0xA4, 0xC4);
    public static readonly Color Track = Color.FromArgb(0x33, 0x2F, 0x4D);
    public static readonly Color Coral = Color.FromArgb(0xE8, 0x82, 0x5F);
    public static readonly Color Violet = Color.FromArgb(0x9F, 0x7A, 0xEA);
    public static readonly Color Warning = Color.FromArgb(0xFB, 0xBF, 0x24);
    public static readonly Color Danger = Color.FromArgb(0xFB, 0x71, 0x85);
    public static readonly Color Good = Color.FromArgb(0x34, 0xD3, 0x99);
    public static readonly Color Sky = Color.FromArgb(0x38, 0xBD, 0xF8);

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static Color Bright(Level level) => level switch
    {
        Level.Plenty => Good,
        Level.Fair => Color.FromArgb(0xA5, 0xB4, 0xFC),
        Level.Low => Warning,
        Level.Critical => Danger,
        _ => Muted,
    };
}

/// <summary>Everything the analysis page shows.</summary>
internal sealed record AnalysisData(int Days, UsageAnalysis Analysis, TokenReport Tokens, UsageBehaviors? Behaviors, bool Analyzing, Settings Settings);

/// <summary>
/// Lays out and draws the whole analysis page: header with the range choice and "re-analyse", summary
/// cards, daily usage (with tokens per day), the hour-of-week heat map, weekly cycle review, token detail,
/// and Claude Code's own attribution. Sections enter as they scroll into view (cards pop in, bars grow,
/// the heat map ripples in, numbers count up); bars and heat cells show a glass tooltip under the pointer.
/// Returns the height it used so the host can scroll.
/// Actions: range:{days}, reanalyze; hover only: see:card:{i}, see:day:{i}, see:heat:{day}:{hour}.
/// </summary>
internal static class AnalysisRenderer
{
    const float M = 22, Gap = 14;
    const float BackdropHeight = 1800;

    public static float Render(IPaint p, float width, PageState state, AnalysisData d)
    {
        state.BeginFrame();
        var m = state.Motion;
        PageKit.Backdrop(p, width, BackdropHeight);
        var tips = new List<Action>();
        float w = width - M * 2;
        float y = Header(p, state, d, width);
        y = Cards(p, state, d, M, y, w) + Gap * 1.5f;
        var page = new RectangleF(0, 0, width, 100_000);
        y = Daily(p, state, d, M, y, w, tips, page) + Gap * 1.5f;
        float left = (w - Gap) * 0.56f;
        y = Math.Max(Heat(p, state, d.Analysis, M, y, left, tips, page), Cycles(p, state, d, M + left + Gap, y, w - left - Gap)) + Gap * 1.5f;
        y = Tokens(p, state, d.Tokens, M, y, w) + Gap * 1.5f;
        y = Attribution(p, state, d.Behaviors, d.Analyzing, M, y, w) + Gap;
        var note = new RectangleF(M, y, w, 36);
        using (PageKit.Enter(p, Motion.Out(m.In("note", note, 900, 600)), 0, 8))
            p.Text("每日用量、時段與週期回顧來自本工具開著時的紀錄；Token 數來自這台電腦上 Claude Code 的對話紀錄（只讀數字，不讀內容），不含其他裝置與 claude.ai。",
                note, 8.5f, AnalysisPalette.Muted, maxLines: 2);
        // Tooltips last, above everything else.
        foreach (var tip in tips)
            tip();
        return y + 40;
    }

    static float Header(IPaint p, PageState state, AnalysisData d, float width)
    {
        var m = state.Motion;
        var top = new RectangleF(0, 0, width, 90);
        float enter = Motion.Out(m.In("top", top, 0, 560));
        // The sunburst turns while Claude Code analyses or the transcripts are being read.
        float busy = m.Spin(d.Analyzing || d.Tokens.Scanning, top);
        using (PageKit.Enter(p, enter, 0, -14))
        {
            PageKit.Header(p, width, "Claude 額度 · 用量分析", "看懂使用習慣、Token 去向，以及是什麼在吃額度", null, null, PageKit.Green, 0, 140 * (1 - enter) + busy * 0.5f);
            float x = width - M;
            string label = d.Analyzing ? "分析中…" : "重新分析";
            float bw = PageKit.ButtonWidth(p, label, IconKind.Refresh);
            x -= bw;
            var r = new RectangleF(x, 24, bw, 40);
            var (hover, sweep) = state.Over("reanalyze", r);
            PageKit.Button(p, state.Hits, "reanalyze", r, label, IconKind.Refresh, true, hover, sweep, m.Spin(d.Analyzing, r), enabled: !d.Analyzing);
            x -= 18;
            foreach (int range in new[] { 30, 7 })
            {
                string text = $"近 {range} 天";
                float cw = PageKit.ButtonWidth(p, text, IconKind.Calendar);
                x -= cw;
                var c = new RectangleF(x, 24, cw, 40);
                var (h, s) = state.Over("range:" + range, c);
                PageKit.Choice(p, state.Hits, "range:" + range, c, text, IconKind.Calendar, d.Days == range, h, PageKit.Hex(0xF59E6B), PageKit.Hex(0xEC4899), s);
                x -= 10;
            }
        }
        return 92;
    }

    static float Section(IPaint p, string title, string note, float x, float y, float w)
    {
        p.Text(title, new RectangleF(x, y, w, 22), 11.5f, AnalysisPalette.Text, bold: true);
        if (note.Length > 0)
        {
            float titleWidth = p.Measure(title, 11.5f, bold: true);
            p.Text(note, new RectangleF(x + titleWidth + 10, y + 3, w - titleWidth - 10, 18), 9f, AnalysisPalette.Muted);
        }
        return y + 28;
    }

    static float Cards(IPaint p, PageState state, AnalysisData d, float x, float y, float w)
    {
        var m = state.Motion;
        var tokens = d.Tokens;
        var cards = d.Analysis.Highlights.ToList();
        cards.Add(new Highlight($"近 {d.Days} 天 Token", Format.Tokens(tokens.Range.Total),
            $"輸出 {Format.Tokens(tokens.Range.Output)} · 快取讀取 {Format.Tokens(tokens.Range.CacheRead)}", tokens.Range.Total > 0 ? Level.Fair : Level.Unknown));
        float cardW = (w - Gap * (cards.Count - 1)) / cards.Count;
        const float H = 104;
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            var r = new RectangleF(x + i * (cardW + Gap), y, cardW, H);
            string action = "see:card:" + i;
            state.Hits.Add(new Hit(r, action));
            var area = RectangleF.Inflate(r, 14, 30);
            float enter = m.In("cards", area, 100 + i * 90, 650);
            if (enter <= 0)
                continue;
            float hover = m.HoverOf(action, area);
            using var entering = PageKit.Enter(p, Motion.Out(enter), 0, 26, 0.9f + 0.1f * Motion.Back(enter), PageKit.Center(r));
            using var lifted = p.Transform(0, -4 * hover);
            PageKit.Card(p, r, c.Level, 16, hover);
            PageKit.Shine(p, r, 16, m.In("cards", area, 560 + i * 90, 900));
            PageKit.Shine(p, r, 16, m.Sweep(action, area));
            p.Text(c.Title, new RectangleF(r.X + 14, r.Y + 11, r.Width - 28, 18), 9.5f, Color.White, bold: true);
            p.Text(c.Value, new RectangleF(r.X + 14, r.Y + 31, r.Width - 28, 28), 15f, Color.White, bold: true);
            p.Text(c.Note, new RectangleF(r.X + 14, r.Y + 64, r.Width - 28, 34), 8.5f, Color.FromArgb(0xF3, 0xF1, 0xFF), maxLines: 2);
        }
        return y + H;
    }

    static float Daily(IPaint p, PageState state, AnalysisData d, float x, float y, float w, List<Action> tips, RectangleF bounds)
    {
        var m = state.Motion;
        var a = d.Analysis;
        const float H = 228;
        var area = new RectangleF(x, y, w, 28 + H);
        float enter = Motion.Out(m.In("daily", area, 260, 600));
        if (enter <= 0)
            return area.Bottom;
        using var entering = PageKit.Enter(p, enter, 0, 22);
        y = Section(p, "每日用量", $"每天用掉本週額度幾個百分點；虛線是平均分配（{UsageAnalyzer.DailyBudget:0.#} 點／天），下方是當天 Token；* 表示有一部分發生在工具沒開時", x, y, w);
        var panel = new RectangleF(x, y, w, H);
        PageKit.Panel(p, panel, 16);
        var plot = RectangleF.FromLTRB(panel.Left + 44, panel.Top + 26, panel.Right - 14, panel.Bottom - 46);
        double max = Math.Max(UsageAnalyzer.DailyBudget * 1.6, a.Days.Count == 0 ? 0 : a.Days.Max(day => day.Weekly) * 1.15);
        float Y(double v) => plot.Bottom - (float)(v / max) * plot.Height;
        foreach (double v in new[] { 0, max / 2, max })
        {
            p.Line(new PointF(plot.Left, Y(v)), new PointF(plot.Right, Y(v)), AnalysisPalette.Track, 1);
            p.Text(v.ToString("0"), new RectangleF(panel.Left, Y(v) - 8, 38, 16), 8.5f, AnalysisPalette.Muted, align: TextAlign.Right);
        }
        // The even-share line draws itself across.
        float line = Motion.Out(m.In("daily", area, 420, 900));
        p.Line(new PointF(plot.Left, Y(UsageAnalyzer.DailyBudget)), new PointF(plot.Left + plot.Width * line, Y(UsageAnalyzer.DailyBudget)), AnalysisPalette.Warning, 1.2f, dashed: true);

        int n = Math.Max(1, a.Days.Count);
        float slot = plot.Width / n;
        float bar = Math.Max(3, Math.Min(34, slot * 0.62f));
        float step = Math.Min(70f, 900f / n);
        int labelEvery = slot >= 46 ? 1 : slot >= 24 ? 2 : 4;
        for (int i = 0; i < a.Days.Count; i++)
        {
            var day = a.Days[i];
            float cx = plot.Left + slot * (i + 0.5f);
            var column = RectangleF.FromLTRB(cx - slot / 2, plot.Top - 6, cx + slot / 2, plot.Bottom + 40);
            string action = "see:day:" + i;
            state.Hits.Add(new Hit(column, action));
            float hover = m.HoverOf(action, RectangleF.Inflate(column, 8, 30));
            if (hover > 0)
                p.Fill(column, Color.FromArgb((int)(16 * hover), 255, 255, 255), 10);
            long dayTokens = d.Tokens.Days.FirstOrDefault(t => t.Day == day.Day).Total;
            var level = day.Weekly > UsageAnalyzer.DailyBudget * 1.5 ? Level.Critical : day.Weekly > UsageAnalyzer.DailyBudget ? Level.Low : Level.Plenty;
            var (deep, _, light) = PageKit.Tint(level);
            float top = plot.Bottom;
            if (day.Weekly > 0)
            {
                // Bars grow from the baseline one after another, overshooting a touch before they settle.
                float grow = m.In("daily", area, 480 + i * step, 760);
                float height = (plot.Bottom - Y(day.Weekly)) * Motion.Back(grow);
                float widen = 2 * hover;
                var r = RectangleF.FromLTRB(cx - bar / 2 - widen, plot.Bottom - height, cx + bar / 2 + widen, plot.Bottom);
                top = r.Top;
                PageKit.Glow(p, r, Math.Min(6, bar / 2), light, (int)(130 * hover));
                PageKit.GlassBar(p, r, light, deep, Math.Min(6, bar / 2), vertical: true);
                if (slot >= 22 && grow > 0.5f)
                    p.Text(day.Weekly.ToString("0") + (day.Partial ? "*" : ""), new RectangleF(cx - 24, r.Top - 17, 48, 16), 8.5f,
                        PageKit.WithAlpha(AnalysisPalette.Text, (int)(255 * Math.Min(1, (grow - 0.5f) * 2))), bold: true, align: TextAlign.Center);
            }
            if (i % labelEvery == 0 || i == a.Days.Count - 1)
            {
                string date = day.Day.ToString("MM/dd") + (slot >= 46 ? "（" + "日一二三四五六"[(int)day.Day.DayOfWeek] + "）" : "");
                p.Text(date, new RectangleF(cx - 40, plot.Bottom + 5, 80, 16), 8.5f, PageKit.Mix(AnalysisPalette.Muted, Color.White, hover), align: TextAlign.Center);
                if (dayTokens > 0)
                    p.Text(Format.Tokens(dayTokens), new RectangleF(cx - 40, plot.Bottom + 21, 80, 16), 8f, AnalysisPalette.Sky, align: TextAlign.Center);
            }
            if (hover > 0)
            {
                var lines = new List<(string, Color, bool)> { (day.Day.ToString("MM/dd") + "（" + "日一二三四五六"[(int)day.Day.DayOfWeek] + "）", PageKit.Text, true) };
                if (day.Weekly > 0)
                {
                    lines.Add(($"用掉本週額度 {day.Weekly:0.#} 點", AnalysisPalette.Bright(level), true));
                    lines.Add((day.Weekly > UsageAnalyzer.DailyBudget ? $"比平均分配多 {day.Weekly - UsageAnalyzer.DailyBudget:0.#} 點" : $"在平均分配（{UsageAnalyzer.DailyBudget:0.#} 點）以內", AnalysisPalette.Muted, false));
                }
                else
                {
                    lines.Add(("沒有用到本週額度", AnalysisPalette.Muted, false));
                }
                if (dayTokens > 0)
                    lines.Add(($"Token {Format.Tokens(dayTokens)}", AnalysisPalette.Sky, false));
                if (day.Partial)
                    lines.Add(("* 有一部分發生在工具沒開時", AnalysisPalette.Muted, false));
                var anchor = new PointF(cx, top - 4);
                tips.Add(() => m.Follow(PageKit.Tooltip(p, anchor, lines, light, bounds, hover)));
            }
        }
        if (!a.HasData)
            p.Text("還沒有足夠的紀錄。工具開著時才會累積。", plot, 10, AnalysisPalette.Muted, align: TextAlign.Center, middle: true);
        return panel.Bottom;
    }

    static float Heat(IPaint p, PageState state, UsageAnalysis a, float x, float y, float w, List<Action> tips, RectangleF bounds)
    {
        var m = state.Motion;
        const float H = 214;
        var area = new RectangleF(x, y, w, 28 + H);
        float enter = Motion.Out(m.In("panels", area, 400, 600));
        if (enter <= 0)
            return area.Bottom;
        using var entering = PageKit.Enter(p, enter, 0, 22);
        y = Section(p, "常用時段", "顏色越亮代表那個時段用掉越多本週額度；滑鼠移到格子上看數字", x, y, w);
        var panel = new RectangleF(x, y, w, H);
        PageKit.Panel(p, panel, 16);
        var grid = RectangleF.FromLTRB(panel.Left + 30, panel.Top + 14, panel.Right - 12, panel.Bottom - 26);
        float cw = grid.Width / 24, ch = grid.Height / 7;
        double max = 0;
        foreach (double v in a.Heat)
            max = Math.Max(max, v);
        const string Days = "一二三四五六日";
        // A little bead of glass: lighter on top, a bright rim along the top edge.
        void Bead(RectangleF r, Color color, bool used, int alpha)
        {
            if (!used)
            {
                p.Fill(r, PageKit.WithAlpha(color, alpha), 3);
                return;
            }
            p.Gradient(r, PageKit.WithAlpha(PageKit.Mix(color, Color.White, 0.3f), alpha), PageKit.WithAlpha(color, alpha), 3, vertical: true);
            p.Line(new PointF(r.X + 3, r.Y + 1), new PointF(r.Right - 3, r.Y + 1), Color.FromArgb(alpha * 110 / 255, 255, 255, 255), 1);
        }
        var raised = new List<Action>();
        for (int d = 0; d < 7; d++)
        {
            p.Text(Days[d].ToString(), new RectangleF(panel.Left + 6, grid.Top + d * ch, 20, ch), 9f, AnalysisPalette.Muted, align: TextAlign.Center, middle: true);
            for (int h = 0; h < 24; h++)
            {
                var cell = new RectangleF(grid.Left + h * cw + 1, grid.Top + d * ch + 1, cw - 2, ch - 2);
                string action = $"see:heat:{d}:{h}";
                state.Hits.Add(new Hit(cell, action));
                // The cells ripple in diagonally from the top-left corner.
                float pop = m.In("panels", area, 560 + (h + d * 2) * 18, 420);
                if (pop <= 0)
                    continue;
                double t = max <= 0 ? 0 : Math.Sqrt(a.Heat[d, h] / max);
                var color = t <= 0 ? AnalysisPalette.Track
                    : t < 0.6 ? AnalysisPalette.Mix(AnalysisPalette.Violet, AnalysisPalette.Coral, t / 0.6)
                    : AnalysisPalette.Mix(AnalysisPalette.Coral, AnalysisPalette.Warning, (t - 0.6) / 0.4);
                float hover = m.HoverOf(action, RectangleF.Inflate(cell, 6, 6));
                float size = Motion.Back(pop) + 0.25f * hover;
                var drawn = RectangleF.Inflate(cell, (size - 1) * cell.Width / 2, (size - 1) * cell.Height / 2);
                int alpha = (int)(255 * Motion.Out(pop));
                if (hover <= 0)
                {
                    Bead(drawn, color, t > 0, alpha);
                }
                else
                {
                    // The hovered cell is drawn after the others so its neighbours do not cover it.
                    raised.Add(() =>
                    {
                        Bead(drawn, color, t > 0, alpha);
                        p.Stroke(drawn, Color.FromArgb((int)(230 * hover), 255, 255, 255), 1.6f, 4);
                    });
                    double points = a.Heat[d, h];
                    var lines = new List<(string, Color, bool)>
                    {
                        ($"星期{Days[d]} {h:00}:00–{(h + 1) % 24:00}:00", PageKit.Text, true),
                        (points > 0 ? $"用掉本週額度 {points:0.#} 點" : "這個時段沒有用量", points > 0 ? color : AnalysisPalette.Muted, points > 0),
                    };
                    var anchor = new PointF(drawn.X + drawn.Width / 2, drawn.Top - 2);
                    tips.Add(() => m.Follow(PageKit.Tooltip(p, anchor, lines, t > 0 ? color : PageKit.Violet, bounds, hover)));
                }
            }
        }
        foreach (var cell in raised)
            cell();
        foreach (int h in new[] { 0, 6, 12, 18, 23 })
            p.Text(h.ToString(), new RectangleF(grid.Left + h * cw - 6, grid.Bottom + 4, cw + 12, 16), 8.5f, AnalysisPalette.Muted, align: TextAlign.Center);
        return panel.Bottom;
    }

    static float Cycles(IPaint p, PageState state, AnalysisData d, float x, float y, float w)
    {
        var m = state.Motion;
        var a = d.Analysis;
        const float H = 214;
        var area = new RectangleF(x, y, w, 28 + H);
        float enter = Motion.Out(m.In("panels", area, 480, 600));
        if (enter <= 0)
            return area.Bottom;
        using var entering = PageKit.Enter(p, enter, 0, 22);
        y = Section(p, "本週額度週期回顧", "", x, y, w);
        var panel = new RectangleF(x, y, w, H);
        PageKit.Panel(p, panel, 16);
        if (a.WeeklyCycles.Count == 0)
        {
            p.Text("還沒有完整的週期紀錄", panel, 10, AnalysisPalette.Muted, align: TextAlign.Center, middle: true);
            return panel.Bottom;
        }
        float rowH = Math.Min(40, (H - 20) / a.WeeklyCycles.Count);
        float ry = panel.Top + 12;
        int i = 0;
        foreach (var c in a.WeeklyCycles.Reverse())
        {
            string label = c.Current ? "本週（進行中）" : $"{c.Start.ToLocalTime():MM/dd}–{c.Reset.ToLocalTime():MM/dd}";
            p.Text(label, new RectangleF(panel.Left + 14, ry, 110, 18), 9f, c.Current ? AnalysisPalette.Text : AnalysisPalette.Muted, bold: c.Current);
            var track = new RectangleF(panel.Left + 14, ry + 20, w - 28, 8);
            p.Fill(track, AnalysisPalette.Track, 4);
            var level = Levels.Of(100 - c.Peak, d.Settings);
            var (deep, _, light) = PageKit.Tint(level);
            float grow = Motion.Out(m.In("panels", area, 700 + i * 120, 800));
            PageKit.GlassBar(p, track with { Width = Math.Max(8, track.Width * (float)Math.Clamp(c.Peak / 100, 0, 1)) * grow }, light, deep, 4, vertical: false);
            string result = c.ExhaustedAt is { } at
                ? $"用完 · 比重置早 {Format.Duration(c.Reset - at)}"
                : $"用到 {c.Peak * grow:0}%";
            p.Text(result, new RectangleF(panel.Left + 120, ry, w - 134, 18), 9f, c.ExhaustedAt != null ? AnalysisPalette.Danger : AnalysisPalette.Text, align: TextAlign.Right);
            ry += rowH;
            i++;
        }
        return panel.Bottom;
    }

    static float Tokens(IPaint p, PageState state, TokenReport t, float x, float y, float w)
    {
        var m = state.Motion;
        int modelRows = Math.Max(1, Math.Min(5, t.Models.Count));
        float height = 28 + 14 + 62 + 14 + 26 + modelRows * 24 + 26 + 4;
        var area = new RectangleF(x, y, w, height);
        float enter = Motion.Out(m.In("tokens", area, 640, 600));
        if (enter <= 0)
            return area.Bottom;
        using var entering = PageKit.Enter(p, enter, 0, 22);
        y = Section(p, "Token 用量", $"這台電腦上 Claude Code 的紀錄 · {t.Status}", x, y, w);
        var panel = new RectangleF(x, y, w, area.Bottom - y);
        PageKit.Panel(p, panel, 16);
        float inner = y + 14;

        // The four kinds, as tinted glass chips whose numbers count up.
        (string Name, long Value, Color Color)[] kinds =
        [
            ("輸入", t.Range.Input, AnalysisPalette.Sky),
            ("輸出", t.Range.Output, AnalysisPalette.Good),
            ("快取寫入", t.Range.CacheWrite, AnalysisPalette.Violet),
            ("快取讀取", t.Range.CacheRead, AnalysisPalette.Coral),
        ];
        long total = Math.Max(1, t.Range.Total);
        float chipW = (w - 28 - Gap * 3) / 4;
        for (int i = 0; i < kinds.Length; i++)
        {
            var r = new RectangleF(x + 14 + i * (chipW + Gap), inner, chipW, 62);
            float rise = Motion.Out(m.In("tokens", area, 760 + i * 80, 600));
            using var chip = PageKit.Enter(p, rise, 0, 14);
            var color = kinds[i].Color;
            p.Fill(r, PageKit.WithAlpha(AnalysisPalette.Raised, 190), 12);
            p.Gradient(r, PageKit.WithAlpha(color, 64), PageKit.WithAlpha(color, 14), 12, vertical: true);
            PageKit.GlassSheen(p, r, 12, 0.85f);
            p.Stroke(r, PageKit.WithAlpha(color, 140), 1.1f, 12);
            p.Fill(new RectangleF(r.X + 7, r.Y + 12, 4, r.Height - 24), color, 2);
            double value = m.Value("token:" + kinds[i].Name, kinds[i].Value, area, 1100);
            p.Text(kinds[i].Name, new RectangleF(r.X + 20, r.Y + 8, r.Width - 26, 16), 9f, PageKit.Hex(0xD8D4F0));
            p.Text(Format.Tokens((long)value), new RectangleF(r.X + 20, r.Y + 27, r.Width - 26, 24), 12.5f, AnalysisPalette.Text, bold: true);
            p.Text($"{value * 100.0 / total:0.#}%", new RectangleF(r.X + 20, r.Y + 27, r.Width - 32, 24), 9f, PageKit.Mix(color, Color.White, 0.2f), bold: true, align: TextAlign.Right, middle: true);
        }
        inner += 62 + 14;

        string Period(string name, TokenCounts? c) => c is { } counts
            ? $"{name}：{Format.Tokens(counts.Total)}（輸出 {Format.Tokens(counts.Output)}）"
            : $"{name}：—";
        p.Text(Period("目前 5 小時時段", t.Session) + "　　" + Period("本週期（從上次重置起）", t.Week),
            new RectangleF(x + 14, inner, w - 28, 18), 9.5f, AnalysisPalette.Text);
        inner += 26;

        long modelMax = t.Models.Count == 0 ? 1 : Math.Max(1, t.Models.Max(model => model.Counts.Total));
        int row = 0;
        foreach (var (model, counts) in t.Models.Take(5))
        {
            p.Text(model, new RectangleF(x + 14, inner, 120, 18), 9.5f, AnalysisPalette.Text, bold: true);
            var track = new RectangleF(x + 140, inner + 5, w - 140 - 190, 8);
            p.Fill(track, AnalysisPalette.Track, 4);
            float grow = Motion.Out(m.In("tokens", area, 1000 + row * 90, 800));
            PageKit.GlassBar(p, track with { Width = Math.Max(6, track.Width * counts.Total / modelMax) * grow }, AnalysisPalette.Violet, AnalysisPalette.Coral, 4, vertical: false);
            double shown = m.Value("model:" + model, counts.Total, area, 1100);
            p.Text($"{Format.Tokens((long)shown)}（{shown * 100.0 / total:0.#}%）", new RectangleF(track.Right + 8, inner, 180, 18), 9f, AnalysisPalette.Muted);
            inner += 24;
            row++;
        }
        if (t.Models.Count == 0)
        {
            p.Text(t.Scanning || t.Status == "尚未讀取" ? "讀取中…" : "這段期間沒有 Claude Code 的 Token 紀錄", new RectangleF(x + 14, inner, w - 28, 18), 9.5f, AnalysisPalette.Muted);
            inner += 24;
        }
        p.Text("快取讀取通常佔大宗（重送之前的對話內容）；API 計價約為一般輸入的十分之一。", new RectangleF(x + 14, inner + 2, w - 28, 18), 8.5f, AnalysisPalette.Muted);
        return area.Bottom;
    }

    static float Attribution(IPaint p, PageState state, UsageBehaviors? b, bool analyzing, float x, float y, float w)
    {
        var m = state.Motion;
        var area = new RectangleF(x, y, w, 320);
        float enter = Motion.Out(m.In("attr", area, 780, 600));
        using var entering = PageKit.Enter(p, enter, 0, 22);
        string note = analyzing ? "Claude Code 正在分析本機近 7 天的對話紀錄…"
            : b == null ? "按上方「重新分析」讓 Claude Code 分析（約 5～10 秒）"
            : $"Claude Code {b.ObservedAt.ToLocalTime():MM/dd HH:mm} 的分析，同 /usage";
        y = Section(p, "是什麼在吃額度", note, x, y, w);
        if (b == null)
            return y;
        float colW = (w - Gap) / 2;
        float bottom = y;
        foreach (var (title, window, col) in new[] { ("近 24 小時", b.Day, 0), ("近 7 天", b.Week, 1) })
        {
            float cx = x + col * (colW + Gap);
            float cy = y;
            var header = new RectangleF(cx, cy, colW, 32);
            PageKit.Panel(p, header, 12);
            p.Text(window == null ? $"{title}：沒有資料" : $"{title}：{window.Requests:#,0} 次請求 · {window.Sessions} 個工作階段",
                new RectangleF(cx + 14, cy, colW - 28, 32), 10f, AnalysisPalette.Text, bold: true, middle: true);
            cy += 42;
            if (window != null)
            {
                int i = 0;
                foreach (var behavior in window.Behaviors.Take(5))
                {
                    var (name, meaning, tip) = BehaviorParser.Describe(behavior.Key);
                    var level = behavior.Percent >= 70 ? Level.Critical : behavior.Percent >= 40 ? Level.Low : Level.Fair;
                    var (deep, _, light) = PageKit.Tint(level);
                    p.Text(name, new RectangleF(cx, cy, 100, 18), 10f, AnalysisPalette.Text, bold: true);
                    var track = new RectangleF(cx + 104, cy + 5, colW - 104 - 56, 9);
                    p.Fill(track, AnalysisPalette.Track, 4.5f);
                    float grow = Motion.Out(m.In("attr", area, 960 + (col * 5 + i) * 70, 800));
                    PageKit.GlassBar(p, track with { Width = Math.Max(8, track.Width * (float)(behavior.Percent / 100)) * grow }, light, deep, 4.5f, vertical: false);
                    p.Text($"{behavior.Percent * grow:0}%", new RectangleF(track.Right + 6, cy, 50, 18), 9.5f, AnalysisPalette.Bright(level), bold: true);
                    cy += 20;
                    if (meaning.Length > 0)
                    {
                        p.Text(meaning + "。建議：" + tip + "。", new RectangleF(cx + 104, cy, colW - 104, 32), 8.5f, AnalysisPalette.Muted, maxLines: 2);
                        cy += 34;
                    }
                    i++;
                }
                if (window.Behaviors.Count == 0)
                {
                    p.Text("沒有特別突出的因素", new RectangleF(cx, cy, colW, 18), 9.5f, AnalysisPalette.Muted);
                    cy += 24;
                }
                foreach (var (label, items) in new[] { ("技能", window.Skills), ("代理", window.Agents), ("外掛", window.Plugins), ("MCP", window.McpServers) })
                {
                    if (items.Count == 0)
                        continue;
                    p.Text(label, new RectangleF(cx, cy, 48, 18), 9f, AnalysisPalette.Muted);
                    p.Text(string.Join("、", items.Take(4).Select(item => $"{item.Name} {item.Percent:0}%")), new RectangleF(cx + 52, cy, colW - 52, 18), 9f, AnalysisPalette.Text);
                    cy += 22;
                }
            }
            bottom = Math.Max(bottom, cy);
        }
        p.Text("比例是 Claude Code 依本機紀錄算的加權占比，各因素會重疊，加起來不是 100%；說明與建議是本工具的解讀。",
            new RectangleF(x, bottom + 4, w, 18), 8.5f, AnalysisPalette.Muted);
        return bottom + 26;
    }
}
