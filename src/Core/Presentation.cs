using System.Globalization;

namespace ClaudeUsageMonitor;

/// <summary>How much of a window is left, as a word and a colour anyone can read at a glance.</summary>
internal enum Level { Plenty, Fair, Low, Critical, Unknown }

internal enum TileIcon { Clock, Calendar, Chip }

internal enum InsightIcon { Warning, Check, Sparkle }

/// <summary>The one-sentence conclusion under the tiles.</summary>
internal sealed record Insight(string Text, Level Level, InsightIcon Icon);

/// <summary>Remaining percentage as a level and a word; shared by the Windows and Mac interfaces.</summary>
internal static class Levels
{
    public static Level Of(double remaining, Settings settings) =>
        remaining <= settings.CriticalRemaining ? Level.Critical
        : remaining <= settings.WarnRemaining ? Level.Low
        : remaining <= 50 ? Level.Fair
        : Level.Plenty;

    public static string Word(Level level, double remaining) =>
        remaining <= 0 ? "用完" : level switch
        {
            Level.Plenty => "充足",
            Level.Fair => "尚可",
            Level.Low => "偏低",
            Level.Critical => "告急",
            _ => "未知",
        };
}

/// <summary>The words shown on the blocks and the one-sentence conclusion.</summary>
internal static class Insights
{
    /// <summary>Turns the service state into the one sentence a user needs.</summary>
    public static Insight Build(MonitorService service, DateTimeOffset now)
    {
        var snapshot = service.Current;
        if (snapshot == null)
            return service.ErrorCode.Length > 0
                ? new Insight(service.Status, Level.Critical, InsightIcon.Warning)
                : new Insight("正在向 Claude Code 取得額度…", Level.Unknown, InsightIcon.Sparkle);
        if (!service.Fresh && service.ErrorCode.Length > 0)
            return new Insight(service.Status, Level.Low, InsightIcon.Warning);

        var main = new[] { snapshot.Session, snapshot.Weekly }.OfType<QuotaWindow>().ToList();
        if (main.FirstOrDefault(w => w.RemainingPercent <= 0) is { } empty)
        {
            string reset = empty.ResetsAt is { } at ? Format.ResetAt(at, now) + " 重置" : "等待重置";
            return new Insight($"{Title(empty)}已用完，{reset}", Level.Critical, InsightIcon.Warning);
        }
        var risky = main
            .Select(w => (Window: w, Forecast: service.Forecasts.GetValueOrDefault(w.Id)))
            .Where(x => x.Forecast is { ExhaustsBeforeReset: true, ExhaustAt: not null })
            .OrderBy(x => x.Forecast!.ExhaustAt)
            .FirstOrDefault();
        if (risky.Window != null)
            return new Insight($"照目前速度，{Title(risky.Window)} {Format.ResetAt(risky.Forecast!.ExhaustAt!.Value, now)} 用完，早於重置",
                Level.Low, InsightIcon.Warning);
        if (main.Any(w => service.Forecasts.GetValueOrDefault(w.Id)?.RatePerHour is not null))
            return new Insight("照目前速度，額度撐得到重置", Level.Plenty, InsightIcon.Check);
        return new Insight("正在觀察使用速度，約 15 分鐘後能預估會不會用完", Level.Unknown, InsightIcon.Sparkle);
    }

    public static string Title(QuotaWindow w) => w.Id switch
    {
        WindowIds.FiveHour => "5 小時額度",
        WindowIds.SevenDay => "本週額度",
        _ => w.Label.Replace("本週·", "") + " 本週",
    };

    public static TileIcon Icon(QuotaWindow w) => w.Id switch
    {
        WindowIds.FiveHour => TileIcon.Clock,
        WindowIds.SevenDay => TileIcon.Calendar,
        _ => TileIcon.Chip,
    };

    public static string RateText(MonitorService service, QuotaWindow w)
    {
        if (w.RemainingPercent <= 0)
            return "等待重置";
        var forecast = service.Forecasts.GetValueOrDefault(w.Id);
        if (forecast?.RatePerHour is not { } rate)
            return "速度觀察中…";
        if (rate < 0.05)
            return "目前沒有在用";
        return "每小時 −" + Format.Rate(rate).Replace("%/h", "%");
    }
}

internal static class Format
{
    static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Percent(double value) =>
        value > 0 && value < 1 ? "<1%" : Math.Floor(value).ToString("0", Culture) + "%";

    public static string Rate(double perHour) =>
        perHour < 0.05 ? "0%/h" : perHour.ToString(perHour < 10 ? "0.0" : "0", Culture) + "%/h";

    /// <summary>How much observation a rate is based on: "近 2 小時", "近 35 分".</summary>
    public static string Basis(int minutes) =>
        minutes >= 60 ? $"近 {minutes / 60.0:0.#} 小時" : $"近 {minutes} 分";

    /// <summary>"2 天 5 小時", "4 小時 56 分", "12 分鐘".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
            return "0 分鐘";
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} 天 {span.Hours} 小時";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} 小時 {span.Minutes} 分";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))} 分鐘";
    }

    /// <summary>Tight form for the tiles: "1天12時", "4時28分", "12分".</summary>
    public static string ShortDuration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
            return "0分";
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}天{span.Hours}時";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}時{span.Minutes}分";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}分";
    }

    public static string Countdown(DateTimeOffset? reset, DateTimeOffset now) =>
        reset is not { } at ? "重置時間未提供"
        : at <= now ? "已到重置時間，等待更新"
        : Duration(at - now) + "後重置";

    public static string ShortCountdown(DateTimeOffset? reset, DateTimeOffset now) =>
        reset is not { } at ? "重置時間未知"
        : at <= now ? "等待重置"
        : ShortDuration(at - now) + "後重置";

    /// <summary>"今天 21:00", "明天 09:00", "10/28（三）09:00".</summary>
    public static string ResetAt(DateTimeOffset reset, DateTimeOffset now)
    {
        var local = reset.ToLocalTime();
        var today = now.ToLocalTime().Date;
        string time = local.ToString("HH:mm", Culture);
        if (local.Date == today)
            return "今天 " + time;
        if (local.Date == today.AddDays(1))
            return "明天 " + time;
        return local.ToString("MM/dd", Culture) + "（" + "日一二三四五六"[(int)local.DayOfWeek] + "）" + time;
    }

    public static string Money(decimal? amount, string currency) =>
        amount is { } a ? (currency == "USD" ? "$" + a.ToString("0.00", Culture) : a.ToString("0.00", Culture) + " " + currency) : "—";
}
