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
