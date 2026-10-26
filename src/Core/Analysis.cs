using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeUsageMonitor;

/// <summary>Percentage points of quota used on one local day (Partial: some of it happened while not watched).</summary>
internal sealed record DayUse(DateTime Day, double Weekly, double Session, bool Partial);

/// <summary>One reset cycle of a window: its peak and, if it ran out, when.</summary>
internal sealed record CycleReview(DateTimeOffset Start, DateTimeOffset Reset, double Peak, DateTimeOffset? ExhaustedAt, bool Current);

/// <summary>One of the summary cards at the top of the analysis.</summary>
internal sealed record Highlight(string Title, string Value, string Note, Level Level);

internal sealed record UsageAnalysis(
    int RangeDays,
    IReadOnlyList<DayUse> Days,
    double[,] Heat,
    IReadOnlyList<CycleReview> WeeklyCycles,
    int SessionWindows,
    int SessionExhausted,
    double SessionAveragePeak,
    IReadOnlyList<Highlight> Highlights,
    bool HasData);

/// <summary>
/// Turns the monitor's own history into habits: how many points of the weekly quota go each day, at which
/// hours, how each weekly cycle ended and how often the 5-hour window runs out. Only what was observed
/// while the tool was running counts; usage that happened during a gap is counted on the day it was
/// noticed and that day is marked partial.
/// </summary>
internal static class UsageAnalyzer
{
    /// <summary>Spreading a weekly quota evenly over seven days.</summary>
    public const double DailyBudget = 100.0 / 7;

    public static UsageAnalysis Analyze(IReadOnlyList<UsageSample> samples, DateTimeOffset now, int days)
    {
        var today = now.ToLocalTime().Date;
        var firstDay = today.AddDays(-(days - 1));
        var weekly = samples.Where(s => s.Window == WindowIds.SevenDay).OrderBy(s => s.At).ToList();
        var session = samples.Where(s => s.Window == WindowIds.FiveHour).OrderBy(s => s.At).ToList();

        var perDay = Enumerable.Range(0, days).ToDictionary(i => firstDay.AddDays(i), _ => (Weekly: 0.0, Session: 0.0, Unwatched: 0.0));
        var heat = new double[7, 24];
        foreach (var (at, points, gap) in Increments(weekly))
        {
            var local = at.ToLocalTime();
            if (!perDay.TryGetValue(local.Date, out var day))
                continue;
            perDay[local.Date] = day with { Weekly = day.Weekly + points, Unwatched = day.Unwatched + (gap ? points : 0) };
            // Usage noticed after a gap cannot be placed at an hour, so it stays out of the heat map.
            if (!gap)
                heat[((int)local.DayOfWeek + 6) % 7, local.Hour] += points;
        }
        foreach (var (at, points, gap) in Increments(session))
        {
            var local = at.ToLocalTime();
            if (perDay.TryGetValue(local.Date, out var day))
                perDay[local.Date] = day with { Session = day.Session + points };
        }
        // A day is marked partial only when a noticeable part (2 points or more) was used while not watched.
        var dayList = perDay.OrderBy(p => p.Key).Select(p => new DayUse(p.Key, p.Value.Weekly, p.Value.Session, p.Value.Unwatched >= 2)).ToList();

        var weeklyCycles = Cycles(weekly, now, TimeSpan.FromDays(7)).TakeLast(5).ToList();
        var sessionCycles = Cycles(session, now, TimeSpan.FromHours(5))
            .Where(c => c.Reset.ToLocalTime().Date >= firstDay)
            .ToList();
        int sessionExhausted = sessionCycles.Count(c => c.ExhaustedAt != null);
        double sessionAverage = sessionCycles.Count == 0 ? 0 : sessionCycles.Average(c => c.Peak);

        bool hasData = weekly.Count > 1 || session.Count > 1;
        var highlights = new List<Highlight>
        {
            Pace(weeklyCycles, now),
            DailyAverage(dayList),
            BusiestHours(heat),
            SessionSummary(sessionCycles.Count, sessionExhausted, sessionAverage, days),
        };
        return new UsageAnalysis(days, dayList, heat, weeklyCycles, sessionCycles.Count, sessionExhausted, sessionAverage, highlights, hasData);
    }

    /// <summary>Each observed rise in usage, timed at the later sample; a reset starts counting again from zero.</summary>
    static IEnumerable<(DateTimeOffset At, double Points, bool Gap)> Increments(List<UsageSample> series)
    {
        UsageSample? previous = null;
        foreach (var s in series)
        {
            if (previous != null)
            {
                bool gap = s.Gap || (s.At - previous.At).TotalSeconds > Math.Max(60, s.Interval) * 3;
                if (ForecastCalculator.SameReset(previous.Reset, s.Reset))
                {
                    double delta = s.Used - previous.Used;
                    if (delta > 0)
                        yield return (s.At, delta, gap);
                }
                else if (s.Used > 0 && (s.Reset > previous.Reset || s.Used < previous.Used))
                {
                    // A new cycle began between the two samples: everything used so far is new usage.
                    yield return (s.At, s.Used, true);
                }
            }
            previous = s;
        }
    }

    /// <summary>Groups samples by reset time into cycles.</summary>
    static List<CycleReview> Cycles(List<UsageSample> series, DateTimeOffset now, TimeSpan length)
    {
        var cycles = new List<CycleReview>();
        var group = new List<UsageSample>();
        void Close()
        {
            if (group.Count == 0 || group[^1].Reset is not { } reset)
            {
                group.Clear();
                return;
            }
            var exhausted = group.FirstOrDefault(s => s.Used >= 99.5);
            cycles.Add(new CycleReview(reset - length, reset, group.Max(s => s.Used), exhausted?.At, reset > now));
            group.Clear();
        }
        foreach (var s in series)
        {
            if (group.Count > 0 && !ForecastCalculator.SameReset(group[^1].Reset, s.Reset))
                Close();
            group.Add(s);
        }
        Close();
        return cycles;
    }

    static Highlight Pace(List<CycleReview> cycles, DateTimeOffset now)
    {
        var current = cycles.LastOrDefault(c => c.Current);
        if (current == null)
            return new Highlight("本週進度", "—", "還沒有本週額度的紀錄", Level.Unknown);
        double elapsed = Math.Clamp((now - current.Start).TotalDays / 7, 0, 1);
        double ideal = elapsed * 100;
        double ahead = current.Peak - ideal;
        var level = ahead > 25 ? Level.Critical : ahead > 10 ? Level.Low : ahead > -5 ? Level.Fair : Level.Plenty;
        string note = Math.Abs(ahead) < 3
            ? $"照平均分配此時約 {ideal:0}%，剛好跟上"
            : $"照平均分配此時約 {ideal:0}%，{(ahead > 0 ? "超前" : "落後")} {Math.Abs(ahead):0} 點";
        return new Highlight("本週進度", $"已用 {current.Peak:0}%", note, level);
    }

    static Highlight DailyAverage(List<DayUse> days)
    {
        var observed = days.Where(d => d.Weekly > 0 || d.Session > 0).ToList();
        if (observed.Count == 0)
            return new Highlight("每天平均", "—", "這段期間沒有用量紀錄", Level.Unknown);
        double average = observed.Average(d => d.Weekly);
        var level = average > DailyBudget * 1.5 ? Level.Critical : average > DailyBudget * 1.15 ? Level.Low : Level.Plenty;
        return new Highlight("每天平均", $"{average:0.#} 點／天", $"本週額度平均分配是 {DailyBudget:0.#} 點／天（{observed.Count} 天有用量）", level);
    }

    static Highlight BusiestHours(double[,] heat)
    {
        var hours = new double[24];
        var weekdays = new double[7];
        for (int d = 0; d < 7; d++)
        {
            for (int h = 0; h < 24; h++)
            {
                hours[h] += heat[d, h];
                weekdays[d] += heat[d, h];
            }
        }
        double total = hours.Sum();
        if (total <= 0)
            return new Highlight("最常用時段", "—", "累積更多紀錄後才看得出來", Level.Unknown);
        int best = Enumerable.Range(0, 24).MaxBy(h => hours[h] + hours[(h + 1) % 24] + hours[(h + 2) % 24]);
        int day = Enumerable.Range(0, 7).MaxBy(d => weekdays[d]);
        double share = weekdays[day] / total * 100;
        return new Highlight("最常用時段", $"{best:00}–{(best + 3) % 24:00} 時", $"週{"一二三四五六日"[day]}用得最多（占 {share:0}%）", Level.Fair);
    }

    static Highlight SessionSummary(int windows, int exhausted, double average, int days)
    {
        if (windows == 0)
            return new Highlight("5 小時額度", "—", "這段期間沒有 5 小時額度的紀錄", Level.Unknown);
        var level = exhausted == 0 ? Level.Plenty : exhausted <= 2 ? Level.Low : Level.Critical;
        return new Highlight("5 小時額度", exhausted == 0 ? "沒用完過" : $"用完 {exhausted} 次", $"近 {days} 天 {windows} 個時段，平均用到 {average:0}%", level);
    }
}

internal sealed record BehaviorShare(string Key, double Percent, int Count);

internal sealed record NamedShare(string Name, double Percent);

/// <summary>Claude Code's own attribution of what drives usage, from its scan of local transcripts.</summary>
internal sealed record BehaviorWindow(
    int Requests,
    int Sessions,
    IReadOnlyList<BehaviorShare> Behaviors,
    IReadOnlyList<NamedShare> Agents,
    IReadOnlyList<NamedShare> Skills,
    IReadOnlyList<NamedShare> Plugins,
    IReadOnlyList<NamedShare> McpServers);

internal sealed record UsageBehaviors(DateTimeOffset ObservedAt, BehaviorWindow? Day, BehaviorWindow? Week);

internal static class BehaviorParser
{
    /// <summary>Parses get_usage's "behaviors" (null when Claude Code did not provide it).</summary>
    public static UsageBehaviors? Parse(JsonElement usage, DateTimeOffset now)
    {
        var behaviors = UsageParser.Get(usage, "behaviors");
        if (behaviors.ValueKind != JsonValueKind.Object)
            return null;
        return new UsageBehaviors(now, Window(UsageParser.Get(behaviors, "day")), Window(UsageParser.Get(behaviors, "week")));
    }

    static BehaviorWindow? Window(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        static int Int(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? (int)Math.Max(0, d) : 0;
        static double Pct(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? Math.Clamp(d, 0, 100) : 0;
        var behaviors = new List<BehaviorShare>();
        if (UsageParser.Get(e, "behaviors") is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var item in list.EnumerateArray().Take(20))
            {
                string key = Name(UsageParser.Get(item, "key"));
                if (key.Length > 0)
                    behaviors.Add(new BehaviorShare(key, Pct(UsageParser.Get(item, "pct")), Int(UsageParser.Get(item, "count"))));
            }
        }
        List<NamedShare> Names(string key) =>
            UsageParser.Get(e, key) is { ValueKind: JsonValueKind.Array } array
                ? array.EnumerateArray().Take(20)
                    .Select(i => new NamedShare(Name(UsageParser.Get(i, "name")), Pct(UsageParser.Get(i, "pct"))))
                    .Where(n => n.Name.Length > 0)
                    .OrderByDescending(n => n.Percent)
                    .ToList()
                : [];
        return new BehaviorWindow(Int(UsageParser.Get(e, "request_count")), Int(UsageParser.Get(e, "session_count")),
            behaviors.OrderByDescending(b => b.Percent).ToList(), Names("agents"), Names("skills"), Names("plugins"), Names("mcp_servers"));
    }

    static string Name(JsonElement e)
    {
        string value = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
        return Regex.IsMatch(value, @"^[\p{L}\p{N} _.:\-/@]{1,60}$") ? value : "";
    }

    /// <summary>
    /// What each factor means and what to do about it, in plain words. Claude Code only names the factor;
    /// the explanation and advice are this tool's reading of it.
    /// </summary>
    public static (string Title, string Meaning, string Tip) Describe(string key) => key switch
    {
        "long_context" => ("長對話", "對話越來越長，每次請求都要帶上大量前文", "一段工作做完就 /compact 或開新對話"),
        "cache_miss" => ("快取沒用上", "隔太久才接著同一段對話，提示快取過期，等於整段重送", "同一段工作盡量連續做完，隔很久再開新對話"),
        "subagent_heavy" => ("大量子代理", "派出很多子代理，每個都各自消耗額度", "簡單的查找自己做，不必每件事都開子代理"),
        "high_parallel" => ("同時多工", "同時開很多工作階段或平行請求", "同時進行的對話少一點"),
        "cron" => ("排程任務", "排程或定時工作（/loop、cron、背景代理）自動在跑", "檢查排程頻率，用不到的停掉"),
        _ => (key, "", ""),
    };
}
