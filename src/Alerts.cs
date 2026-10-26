namespace ClaudeUsageMonitor;

internal sealed class AlertState
{
    /// <summary>Alert keys already shown, so a restart does not repeat them.</summary>
    public Dictionary<string, DateTimeOffset> Sent { get; set; } = [];
}

internal sealed record AlertMessage(string Title, string Body, bool Warning);

internal static class AlertRules
{
    public static List<AlertMessage> Evaluate(
        UsageSnapshot? previous,
        UsageSnapshot current,
        IReadOnlyDictionary<string, Forecast> forecasts,
        Settings settings,
        AlertState state,
        DateTimeOffset now)
    {
        var messages = new List<AlertMessage>();
        foreach (var window in current.Windows)
        {
            string cycle = $"{current.AccountKey}|{window.Id}|{window.ResetsAt?.ToUnixTimeSeconds()}";
            double remaining = window.RemainingPercent;
            string resetText = window.ResetsAt is { } reset ? Format.ResetAt(reset, now) + " 重置" : "重置時間未提供";

            string? level = remaining <= 0 ? "empty"
                : remaining <= settings.CriticalRemaining ? "critical"
                : remaining <= settings.WarnRemaining ? "warn"
                : null;
            if (level != null && !state.Sent.ContainsKey(cycle + "|" + level))
            {
                // Reaching a deeper level also counts the shallower ones as shown.
                foreach (string shown in LevelsUpTo(level))
                    state.Sent[cycle + "|" + shown] = now;

                string body = level == "empty"
                    ? $"已用完，{resetText}。"
                    : $"剩 {Format.Percent(remaining)}，{resetText}。" + ForecastHint(forecasts.GetValueOrDefault(window.Id));
                messages.Add(new AlertMessage($"Claude {window.Label}額度" + (level == "empty" ? "已用完" : "偏低"), body, true));
            }

            var before = previous?.AccountKey == current.AccountKey ? previous.Find(window.Id) : null;
            if (settings.NotifyReset && before != null
                && window.UsedPercent < before.UsedPercent - 10
                && before.RemainingPercent <= settings.WarnRemaining
                && !state.Sent.ContainsKey(cycle + "|reset"))
            {
                state.Sent[cycle + "|reset"] = now;
                messages.Add(new AlertMessage($"Claude {window.Label}額度已恢復",
                    $"剩餘 {Format.Percent(before.RemainingPercent)} → {Format.Percent(remaining)}。", false));
            }
        }

        foreach (string key in state.Sent.Where(p => now - p.Value > TimeSpan.FromDays(14)).Select(p => p.Key).ToList())
            state.Sent.Remove(key);
        return messages;
    }

    static IEnumerable<string> LevelsUpTo(string level) => level switch
    {
        "empty" => ["warn", "critical", "empty"],
        "critical" => ["warn", "critical"],
        _ => ["warn"],
    };

    static string ForecastHint(Forecast? forecast) =>
        forecast is { ExhaustsBeforeReset: true, ExhaustAt: { } at }
            ? $"照目前速度約 {at.ToLocalTime():HH:mm} 用完。"
            : "";
}
