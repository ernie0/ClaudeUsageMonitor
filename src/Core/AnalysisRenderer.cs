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
    void Gradient(RectangleF r, Color start, Color end, float radius = 0);
    /// <summary>The vivid quota block used everywhere else in the app.</summary>
    void Block(RectangleF r, Level level, float radius);
    void Line(PointF a, PointF b, Color color, float width, bool dashed = false);
    void Text(string text, RectangleF r, float points, Color color, bool bold = false, TextAlign align = TextAlign.Left, int maxLines = 1, bool middle = false);
    float Measure(string text, float points, bool bold = false);
}

/// <summary>Tokens for the analysis: the selected range, per day, per model, and the current quota periods.</summary>
internal sealed record TokenReport(
    TokenCounts Range,
    IReadOnlyList<(DateTime Day, long Total)> Days,
    IReadOnlyList<(string Model, TokenCounts Counts)> Models,
    TokenCounts? Session,
    TokenCounts? Week,
    string Status)
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
        return new TokenReport(range, perDay, models, Period(snapshot?.Session, TimeSpan.FromHours(5)), Period(snapshot?.Weekly, TimeSpan.FromDays(7)), status);
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

/// <summary>
/// Lays out and draws the whole analysis page: summary cards, daily usage (with tokens per day), the
/// hour-of-week heat map, weekly cycle review, token detail, and Claude Code's own attribution.
/// Returns the height it used so the host can scroll.
/// </summary>
internal static class AnalysisRenderer
{
    const float M = 14, Gap = 12;

    public static float Render(IPaint p, float width, UsageAnalysis a, TokenReport tokens, UsageBehaviors? behaviors, bool analyzing, Settings settings)
    {
        float w = width - M * 2;
        float y = M;
        y = Cards(p, a, tokens, M, y, w) + Gap * 1.5f;
        y = Daily(p, a, tokens, M, y, w) + Gap * 1.5f;
        float left = (w - Gap) * 0.56f;
        float panels = Math.Max(Heat(p, a, M, y, left), Cycles(p, a, settings, M + left + Gap, y, w - left - Gap));
        y = panels + Gap * 1.5f;
        y = Tokens(p, tokens, M, y, w) + Gap * 1.5f;
        y = Attribution(p, behaviors, analyzing, M, y, w) + Gap;
        p.Text("每日用量、時段與週期回顧來自本工具開著時的紀錄；Token 數來自這台電腦上 Claude Code 的對話紀錄（只讀數字，不讀內容），不含其他裝置與 claude.ai。",
            new RectangleF(M, y, w, 36), 8.5f, AnalysisPalette.Muted, maxLines: 2);
        return y + 40;
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

    static float Cards(IPaint p, UsageAnalysis a, TokenReport tokens, float x, float y, float w)
    {
        var cards = a.Highlights.ToList();
        cards.Add(new Highlight($"近 {a.RangeDays} 天 Token", Format.Tokens(tokens.Range.Total),
            $"輸出 {Format.Tokens(tokens.Range.Output)} · 快取讀取 {Format.Tokens(tokens.Range.CacheRead)}", tokens.Range.Total > 0 ? Level.Fair : Level.Unknown));
        float cardW = (w - Gap * (cards.Count - 1)) / cards.Count;
        const float H = 100;
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            var r = new RectangleF(x + i * (cardW + Gap), y, cardW, H);
            p.Block(r, c.Level, 14);
            p.Text(c.Title, new RectangleF(r.X + 12, r.Y + 10, r.Width - 24, 18), 9.5f, Color.White, bold: true);
            p.Text(c.Value, new RectangleF(r.X + 12, r.Y + 30, r.Width - 24, 28), 15f, Color.White, bold: true);
            p.Text(c.Note, new RectangleF(r.X + 12, r.Y + 62, r.Width - 24, 34), 8.5f, Color.FromArgb(0xF3, 0xF1, 0xFF), maxLines: 2);
        }
        return y + H;
    }

    static float Daily(IPaint p, UsageAnalysis a, TokenReport tokens, float x, float y, float w)
    {
        y = Section(p, "每日用量", $"每天用掉本週額度幾個百分點；虛線是平均分配（{UsageAnalyzer.DailyBudget:0.#} 點／天），下方是當天 Token；* 表示有一部分發生在工具沒開時", x, y, w);
        const float H = 220;
        var area = new RectangleF(x, y, w, H);
        p.Fill(area, AnalysisPalette.Surface, 12);
        var plot = RectangleF.FromLTRB(area.Left + 44, area.Top + 22, area.Right - 14, area.Bottom - 44);
        double max = Math.Max(UsageAnalyzer.DailyBudget * 1.6, a.Days.Count == 0 ? 0 : a.Days.Max(d => d.Weekly) * 1.1);
        float Y(double v) => plot.Bottom - (float)(v / max) * plot.Height;
        foreach (double v in new[] { 0, max / 2, max })
        {
            p.Line(new PointF(plot.Left, Y(v)), new PointF(plot.Right, Y(v)), AnalysisPalette.Track, 1);
            p.Text(v.ToString("0"), new RectangleF(area.Left, Y(v) - 8, 38, 16), 8.5f, AnalysisPalette.Muted, align: TextAlign.Right);
        }
        p.Line(new PointF(plot.Left, Y(UsageAnalyzer.DailyBudget)), new PointF(plot.Right, Y(UsageAnalyzer.DailyBudget)), AnalysisPalette.Warning, 1.2f, dashed: true);

        int n = Math.Max(1, a.Days.Count);
        float slot = plot.Width / n;
        float bar = Math.Max(3, Math.Min(34, slot * 0.62f));
        int labelEvery = slot >= 46 ? 1 : slot >= 24 ? 2 : 4;
        for (int i = 0; i < a.Days.Count; i++)
        {
            var d = a.Days[i];
            float cx = plot.Left + slot * (i + 0.5f);
            if (d.Weekly > 0)
            {
                var level = d.Weekly > UsageAnalyzer.DailyBudget * 1.5 ? Level.Critical : d.Weekly > UsageAnalyzer.DailyBudget ? Level.Low : Level.Plenty;
                var r = RectangleF.FromLTRB(cx - bar / 2, Y(d.Weekly), cx + bar / 2, plot.Bottom);
                p.Block(r, level, Math.Min(6, bar / 2));
                if (slot >= 22)
                    p.Text(d.Weekly.ToString("0") + (d.Partial ? "*" : ""), new RectangleF(cx - 24, r.Top - 17, 48, 16), 8.5f, AnalysisPalette.Text, bold: true, align: TextAlign.Center);
            }
            if (i % labelEvery == 0 || i == a.Days.Count - 1)
            {
                string date = d.Day.ToString("MM/dd") + (slot >= 46 ? "（" + "日一二三四五六"[(int)d.Day.DayOfWeek] + "）" : "");
                p.Text(date, new RectangleF(cx - 40, plot.Bottom + 5, 80, 16), 8.5f, AnalysisPalette.Muted, align: TextAlign.Center);
                long dayTokens = tokens.Days.FirstOrDefault(t => t.Day == d.Day).Total;
                if (dayTokens > 0)
                    p.Text(Format.Tokens(dayTokens), new RectangleF(cx - 40, plot.Bottom + 21, 80, 16), 8f, AnalysisPalette.Sky, align: TextAlign.Center);
            }
        }
        if (!a.HasData)
            p.Text("還沒有足夠的紀錄。工具開著時才會累積。", plot, 10, AnalysisPalette.Muted, align: TextAlign.Center, middle: true);
        return area.Bottom;
    }

    static float Heat(IPaint p, UsageAnalysis a, float x, float y, float w)
    {
        y = Section(p, "常用時段", "顏色越亮代表那個時段用掉越多本週額度", x, y, w);
        const float H = 214;
        var area = new RectangleF(x, y, w, H);
        p.Fill(area, AnalysisPalette.Surface, 12);
        var grid = RectangleF.FromLTRB(area.Left + 30, area.Top + 14, area.Right - 12, area.Bottom - 26);
        float cw = grid.Width / 24, ch = grid.Height / 7;
        double max = 0;
        foreach (double v in a.Heat)
            max = Math.Max(max, v);
        for (int d = 0; d < 7; d++)
        {
            p.Text("一二三四五六日"[d].ToString(), new RectangleF(area.Left + 6, grid.Top + d * ch, 20, ch), 9f, AnalysisPalette.Muted, align: TextAlign.Center, middle: true);
            for (int h = 0; h < 24; h++)
            {
                double t = max <= 0 ? 0 : Math.Sqrt(a.Heat[d, h] / max);
                var color = t <= 0 ? AnalysisPalette.Track
                    : t < 0.6 ? AnalysisPalette.Mix(AnalysisPalette.Violet, AnalysisPalette.Coral, t / 0.6)
                    : AnalysisPalette.Mix(AnalysisPalette.Coral, AnalysisPalette.Warning, (t - 0.6) / 0.4);
                p.Fill(new RectangleF(grid.Left + h * cw + 1, grid.Top + d * ch + 1, cw - 2, ch - 2), color, 3);
            }
        }
        foreach (int h in new[] { 0, 6, 12, 18, 23 })
            p.Text(h.ToString(), new RectangleF(grid.Left + h * cw - 6, grid.Bottom + 4, cw + 12, 16), 8.5f, AnalysisPalette.Muted, align: TextAlign.Center);
        return area.Bottom;
    }

    static float Cycles(IPaint p, UsageAnalysis a, Settings settings, float x, float y, float w)
    {
        y = Section(p, "本週額度週期回顧", "", x, y, w);
        const float H = 214;
        var area = new RectangleF(x, y, w, H);
        p.Fill(area, AnalysisPalette.Surface, 12);
        if (a.WeeklyCycles.Count == 0)
        {
            p.Text("還沒有完整的週期紀錄", area, 10, AnalysisPalette.Muted, align: TextAlign.Center, middle: true);
            return area.Bottom;
        }
        float rowH = Math.Min(40, (H - 20) / a.WeeklyCycles.Count);
        float ry = area.Top + 10;
        foreach (var c in a.WeeklyCycles.Reverse())
        {
            string label = c.Current ? "本週（進行中）" : $"{c.Start.ToLocalTime():MM/dd}–{c.Reset.ToLocalTime():MM/dd}";
            p.Text(label, new RectangleF(area.Left + 12, ry, 110, 18), 9f, c.Current ? AnalysisPalette.Text : AnalysisPalette.Muted, bold: c.Current);
            var track = new RectangleF(area.Left + 12, ry + 20, w - 24, 8);
            p.Fill(track, AnalysisPalette.Track, 4);
            var level = Levels.Of(100 - c.Peak, settings);
            p.Block(track with { Width = Math.Max(8, track.Width * (float)Math.Clamp(c.Peak / 100, 0, 1)) }, level, 4);
            string result = c.ExhaustedAt is { } at
                ? $"用完 · 比重置早 {Format.Duration(c.Reset - at)}"
                : $"用到 {c.Peak:0}%";
            p.Text(result, new RectangleF(area.Left + 120, ry, w - 132, 18), 9f, c.ExhaustedAt != null ? AnalysisPalette.Danger : AnalysisPalette.Text, align: TextAlign.Right);
            ry += rowH;
        }
        return area.Bottom;
    }

    static float Tokens(IPaint p, TokenReport t, float x, float y, float w)
    {
        y = Section(p, "Token 用量", $"這台電腦上 Claude Code 的紀錄 · {t.Status}", x, y, w);
        int modelRows = Math.Max(1, Math.Min(5, t.Models.Count));
        var area = new RectangleF(x, y, w, 12 + 58 + 12 + 26 + modelRows * 24 + 26 + 4);
        p.Fill(area, AnalysisPalette.Surface, 12);
        float inner = y + 12;

        // The four kinds, as coloured chips with their share of the range.
        (string Name, long Value, Color Color)[] kinds =
        [
            ("輸入", t.Range.Input, AnalysisPalette.Sky),
            ("輸出", t.Range.Output, AnalysisPalette.Good),
            ("快取寫入", t.Range.CacheWrite, AnalysisPalette.Violet),
            ("快取讀取", t.Range.CacheRead, AnalysisPalette.Coral),
        ];
        long total = Math.Max(1, t.Range.Total);
        float chipW = (w - 24 - Gap * 3) / 4;
        for (int i = 0; i < kinds.Length; i++)
        {
            var r = new RectangleF(x + 12 + i * (chipW + Gap), inner, chipW, 58);
            p.Fill(r, AnalysisPalette.Raised, 10);
            p.Fill(new RectangleF(r.X, r.Y, 5, r.Height), kinds[i].Color, 2);
            p.Text(kinds[i].Name, new RectangleF(r.X + 14, r.Y + 7, r.Width - 20, 16), 9f, AnalysisPalette.Muted);
            p.Text(Format.Tokens(kinds[i].Value), new RectangleF(r.X + 14, r.Y + 24, r.Width - 20, 22), 12.5f, AnalysisPalette.Text, bold: true);
            p.Text($"{kinds[i].Value * 100.0 / total:0.#}%", new RectangleF(r.X + 14, r.Y + 24, r.Width - 24, 22), 9f, kinds[i].Color, align: TextAlign.Right, middle: true);
        }
        inner += 58 + 12;

        string Period(string name, TokenCounts? c) => c is { } counts
            ? $"{name}：{Format.Tokens(counts.Total)}（輸出 {Format.Tokens(counts.Output)}）"
            : $"{name}：—";
        p.Text(Period("目前 5 小時時段", t.Session) + "　　" + Period("本週期（從上次重置起）", t.Week),
            new RectangleF(x + 12, inner, w - 24, 18), 9.5f, AnalysisPalette.Text);
        inner += 26;

        long modelMax = t.Models.Count == 0 ? 1 : Math.Max(1, t.Models.Max(m => m.Counts.Total));
        foreach (var (model, counts) in t.Models.Take(5))
        {
            p.Text(model, new RectangleF(x + 12, inner, 120, 18), 9.5f, AnalysisPalette.Text, bold: true);
            var track = new RectangleF(x + 140, inner + 5, w - 140 - 190, 8);
            p.Fill(track, AnalysisPalette.Track, 4);
            p.Gradient(track with { Width = Math.Max(6, track.Width * counts.Total / modelMax) }, AnalysisPalette.Coral, AnalysisPalette.Violet, 4);
            p.Text($"{Format.Tokens(counts.Total)}（{counts.Total * 100.0 / total:0.#}%）", new RectangleF(track.Right + 8, inner, 180, 18), 9f, AnalysisPalette.Muted);
            inner += 24;
        }
        if (t.Models.Count == 0)
        {
            p.Text(t.Status == "尚未讀取" || t.Status.StartsWith("讀取") ? "讀取中…" : "這段期間沒有 Claude Code 的 Token 紀錄", new RectangleF(x + 12, inner, w - 24, 18), 9.5f, AnalysisPalette.Muted);
            inner += 24;
        }
        p.Text("快取讀取通常佔大宗（重送之前的對話內容）；API 計價約為一般輸入的十分之一。", new RectangleF(x + 12, inner + 2, w - 24, 18), 8.5f, AnalysisPalette.Muted);
        return area.Bottom;
    }

    static float Attribution(IPaint p, UsageBehaviors? b, bool analyzing, float x, float y, float w)
    {
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
            p.Fill(new RectangleF(cx, cy, colW, 30), AnalysisPalette.Raised, 10);
            p.Text(window == null ? $"{title}：沒有資料" : $"{title}：{window.Requests:#,0} 次請求 · {window.Sessions} 個工作階段",
                new RectangleF(cx + 12, cy, colW - 24, 30), 10f, AnalysisPalette.Text, bold: true, middle: true);
            cy += 38;
            if (window != null)
            {
                foreach (var behavior in window.Behaviors.Take(5))
                {
                    var (name, meaning, tip) = BehaviorParser.Describe(behavior.Key);
                    var level = behavior.Percent >= 70 ? Level.Critical : behavior.Percent >= 40 ? Level.Low : Level.Fair;
                    p.Text(name, new RectangleF(cx, cy, 100, 18), 10f, AnalysisPalette.Text, bold: true);
                    var track = new RectangleF(cx + 104, cy + 5, colW - 104 - 56, 9);
                    p.Fill(track, AnalysisPalette.Track, 4);
                    p.Block(track with { Width = Math.Max(8, track.Width * (float)(behavior.Percent / 100)) }, level, 4);
                    p.Text($"{behavior.Percent:0}%", new RectangleF(track.Right + 6, cy, 50, 18), 9.5f, AnalysisPalette.Bright(level), bold: true);
                    cy += 20;
                    if (meaning.Length > 0)
                    {
                        p.Text(meaning + "。建議：" + tip + "。", new RectangleF(cx + 104, cy, colW - 104, 32), 8.5f, AnalysisPalette.Muted, maxLines: 2);
                        cy += 34;
                    }
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
                    p.Text(string.Join("、", items.Take(4).Select(i => $"{i.Name} {i.Percent:0}%")), new RectangleF(cx + 52, cy, colW - 52, 18), 9f, AnalysisPalette.Text);
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
