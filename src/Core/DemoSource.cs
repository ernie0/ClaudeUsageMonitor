namespace ClaudeUsageMonitor;

/// <summary>
/// Synthetic data for --demo: a week of plausible history (with overnight gaps and resets) and a live
/// source that continues the same curves. Used for screenshots and for trying the UI without an account.
/// </summary>
internal sealed class DemoSource : IUsageSource
{
    const string Account = "demo";
    static readonly TimeSpan Step = TimeSpan.FromMinutes(5);

    readonly DateTimeOffset anchor;
    readonly DateTimeOffset weeklyReset;
    readonly DateTimeOffset sessionStart;

    public DemoSource(DateTimeOffset now)
    {
        anchor = now;
        weeklyReset = Minute(now.AddDays(1).AddHours(5));
        sessionStart = Minute(now.AddHours(-2).AddMinutes(-47));
    }

    public string ExecutablePath => "（示範資料）";

    public Task<UsageSnapshot> ReadAsync(string salt, CancellationToken ct) => Task.FromResult(At(DateTimeOffset.UtcNow));

    /// <summary>Attribution shaped like a real get_usage answer.</summary>
    public Task<UsageBehaviors?> ReadBehaviorsAsync(CancellationToken ct)
    {
        var day = new BehaviorWindow(1_350, 7,
            [new("long_context", 95, 1_182), new("cron", 41, 1)],
            [], [new("ernie-mode", 5), new("superpowers:test-driven-development", 2)], [new("superpowers", 3)], []);
        var week = new BehaviorWindow(9_360, 15,
            [new("long_context", 94, 7_879), new("subagent_heavy", 62, 4), new("cache_miss", 18, 640)],
            [new("general-purpose", 11), new("Explore", 3)], [new("ernie-mode", 1)], [new("superpowers", 1)], [new("rea", 1)]);
        return Task.FromResult<UsageBehaviors?>(new UsageBehaviors(DateTimeOffset.UtcNow, day, week));
    }

    public void Seed(HistoryStore history)
    {
        var rows = new List<UsageSample>();
        bool pendingGap = true;
        for (var t = anchor.AddDays(-7); t < anchor.AddMinutes(-4); t += Step)
        {
            if (!Active(t))
            {
                pendingGap = true;
                continue;
            }
            rows.AddRange(UsageSample.From(At(t), pendingGap, (int)Step.TotalSeconds));
            pendingGap = false;
        }
        history.Append(rows);
    }

    UsageSnapshot At(DateTimeOffset t)
    {
        var weekStart = weeklyReset.AddDays(-7 * Math.Ceiling((weeklyReset - t).TotalDays / 7));
        double weekly = Math.Min(100, Math.Round(ActiveHours(weekStart, t) * 0.95));
        var sessionCycle = sessionStart.AddHours(5 * Math.Floor((t - sessionStart).TotalHours / 5));
        double session = Math.Min(100, Math.Round(ActiveHours(sessionCycle, t) * 21));
        var windows = new List<QuotaWindow>
        {
            new(WindowIds.FiveHour, "5 小時", 300, session, sessionCycle.AddHours(5)),
            new(WindowIds.SevenDay, "本週", 10080, weekly, weekStart.AddDays(7)),
            new("model:fable", "本週·Fable", 10080, Math.Min(100, Math.Round(weekly * 1.6)), weekStart.AddDays(7)),
        };
        return new UsageSnapshot(t, Account, "max", "Claude Max", windows,
            new ExtraUsage(false, 90m, 90.36m, 100, "USD"), "");
    }

    static bool Active(DateTimeOffset t)
    {
        int hour = t.ToLocalTime().Hour;
        return hour >= 9 || hour < 1;
    }

    static double ActiveHours(DateTimeOffset from, DateTimeOffset to)
    {
        double hours = 0;
        for (var t = from; t < to; t += Step)
        {
            if (Active(t))
                hours += Step.TotalHours;
        }
        return hours;
    }

    static DateTimeOffset Minute(DateTimeOffset t) => new(t.UtcTicks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
}
