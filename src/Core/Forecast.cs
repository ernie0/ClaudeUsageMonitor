namespace ClaudeUsageMonitor;

/// <param name="RatePerHour">Percentage points used per hour; null when there is not enough data.</param>
/// <param name="BasisMinutes">How many minutes of observations the rate is based on.</param>
/// <param name="ExhaustAt">When the window runs out at the current rate.</param>
/// <param name="BudgetPerHour">The rate that would exactly last until the reset.</param>
internal sealed record Forecast(double? RatePerHour, int BasisMinutes, DateTimeOffset? ExhaustAt, bool ExhaustsBeforeReset, double? BudgetPerHour)
{
    public static readonly Forecast Empty = new(null, 0, null, false, null);
}

internal static class ForecastCalculator
{
    static readonly TimeSpan RecentSpan = TimeSpan.FromHours(2);
    static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Estimates the burn rate from samples of one account and window. Only the latest unbroken run of
    /// observations in the current reset cycle counts: a monitoring gap, a different reset time or a
    /// drop in usage (a reset) ends the run. Utilization is reported in whole percent, so the rate needs
    /// either 2 points of movement or an hour of observation before it is shown.
    /// </summary>
    public static Forecast Calculate(IReadOnlyList<UsageSample> history, QuotaWindow current, DateTimeOffset now)
    {
        double? budget = current.ResetsAt is { } reset && reset > now && (reset - now).TotalHours > 0.05
            ? current.RemainingPercent / (reset - now).TotalHours
            : null;

        var cycle = history
            .Where(s => SameReset(s.Reset, current.ResetsAt) && s.At <= now)
            .OrderBy(s => s.At)
            .ToList();
        if (cycle.Count < 2)
            return Forecast.Empty with { BudgetPerHour = budget };

        int start = cycle.Count - 1;
        while (start > 0)
        {
            var previous = cycle[start - 1];
            var next = cycle[start];
            bool broken = next.Gap
                || (next.At - previous.At).TotalSeconds > Math.Max(60, next.Interval) * 3
                || next.Used < previous.Used;
            if (broken)
                break;
            start--;
        }
        var run = cycle.GetRange(start, cycle.Count - start);
        var last = run[^1];
        var baseSample = run.LastOrDefault(s => s.At <= last.At - RecentSpan) ?? run[0];

        double hours = (last.At - baseSample.At).TotalHours;
        double delta = last.Used - baseSample.Used;
        if (hours <= 0 || (delta < 2 && hours < 1) || hours < 0.25)
            return Forecast.Empty with { BudgetPerHour = budget };

        double rate = delta / hours;
        DateTimeOffset? exhaust = current.RemainingPercent <= 0
            ? last.At
            : rate > 0 ? last.At.AddHours(current.RemainingPercent / rate) : null;
        bool beforeReset = exhaust is { } at && current.ResetsAt is { } r && at < r;
        return new Forecast(rate, (int)Math.Round(hours * 60), exhaust, beforeReset, budget);
    }

    public static bool SameReset(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null || b is null ? a == b : (a.Value - b.Value).Duration() <= ResetTolerance;
}
