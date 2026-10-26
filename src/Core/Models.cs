using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClaudeUsageMonitor;

internal static class WindowIds
{
    public const string FiveHour = "five_hour";
    public const string SevenDay = "seven_day";
}

/// <summary>One plan rate-limit window as reported by Claude Code's get_usage.</summary>
internal sealed record QuotaWindow(string Id, string Label, int DurationMinutes, double UsedPercent, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed record ExtraUsage(bool Enabled, decimal? MonthlyLimit, decimal? Used, double? Utilization, string Currency);

internal sealed record UsageSnapshot(
    DateTimeOffset ObservedAt,
    string AccountKey,
    string PlanType,
    string PlanLabel,
    IReadOnlyList<QuotaWindow> Windows,
    ExtraUsage? Extra,
    string ClaudeVersion)
{
    public QuotaWindow? Find(string id) => Windows.FirstOrDefault(w => w.Id == id);

    [JsonIgnore]
    public QuotaWindow? Session => Find(WindowIds.FiveHour);

    [JsonIgnore]
    public QuotaWindow? Weekly => Find(WindowIds.SevenDay);

    /// <summary>Per-model or per-product weekly windows, shown on the overlay's footer.</summary>
    [JsonIgnore]
    public IEnumerable<QuotaWindow> Scoped => Windows.Where(w => w.Id is not (WindowIds.FiveHour or WindowIds.SevenDay));

    /// <summary>The tighter of the two plan-wide windows; drives compact mode and the tray icon.</summary>
    [JsonIgnore]
    public QuotaWindow? Tightest => new[] { Session, Weekly }.OfType<QuotaWindow>().MinBy(w => w.RemainingPercent);
}

internal sealed class MonitorException(string code) : Exception(code)
{
    public string Code => Message;
}

/// <summary>Turns the raw initialize / get_usage control responses into a snapshot.</summary>
internal static class UsageParser
{
    static readonly (string Key, string Label, int Minutes)[] NamedWindows =
    [
        (WindowIds.FiveHour, "5 小時", 300),
        (WindowIds.SevenDay, "本週", 10080),
        ("seven_day_opus", "本週·Opus", 10080),
        ("seven_day_sonnet", "本週·Sonnet", 10080),
        ("seven_day_oauth_apps", "本週·外部應用", 10080),
    ];

    public static UsageSnapshot Parse(JsonElement account, JsonElement usage, string salt, string version, DateTimeOffset observedAt)
    {
        string email = Str(Get(account, "email")).Trim();
        string organization = Str(Get(account, "organization")).Trim();
        string provider = Str(Get(account, "apiProvider"));
        if (provider.Length > 0 && provider != "firstParty")
            throw new MonitorException("NO_PLAN_LIMITS");

        if (Get(usage, "rate_limits_available").ValueKind == JsonValueKind.False)
            throw new MonitorException(email.Length == 0 ? "AUTH_REQUIRED" : "NO_PLAN_LIMITS");

        var limits = Get(usage, "rate_limits");
        if (limits.ValueKind != JsonValueKind.Object)
            throw new MonitorException("USAGE_UNAVAILABLE");

        var windows = new List<QuotaWindow>();
        foreach (var (key, label, minutes) in NamedWindows)
        {
            if (ParseWindow(Get(limits, key), key, label, minutes) is { } window)
                windows.Add(window);
        }

        var scoped = Get(limits, "model_scoped");
        if (scoped.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in scoped.EnumerateArray().Take(20))
            {
                string name = SafeLabel(Str(Get(item, "display_name")));
                if (name.Length == 0)
                    continue;
                string id = "model:" + name.ToLowerInvariant().Replace(' ', '_');
                if (windows.Any(w => w.Id == id))
                    continue;
                if (ParseWindow(item, id, "本週·" + name, 10080) is { } window)
                    windows.Add(window);
            }
        }

        if (windows.Count == 0)
            throw new MonitorException("USAGE_UNAVAILABLE");

        string planType = SafeLabel(Str(Get(usage, "subscription_type")));
        string planLabel = SafeLabel(Str(Get(account, "subscriptionType")));
        return new UsageSnapshot(
            observedAt,
            AccountKey(salt, email, organization),
            planType,
            planLabel.Length > 0 ? planLabel : planType,
            windows,
            ParseExtra(Get(limits, "extra_usage")),
            SafeLabel(version));
    }

    static QuotaWindow? ParseWindow(JsonElement element, string id, string label, int minutes)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        var utilization = Get(element, "utilization");
        if (utilization.ValueKind != JsonValueKind.Number || !utilization.TryGetDouble(out double used) || double.IsNaN(used))
            return null;
        return new QuotaWindow(id, label, minutes, Math.Clamp(used, 0, 100), ParseTime(Get(element, "resets_at")));
    }

    static ExtraUsage? ParseExtra(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        int places = Get(element, "decimal_places") is { ValueKind: JsonValueKind.Number } p && p.TryGetInt32(out int dp) ? Math.Clamp(dp, 0, 6) : 2;
        decimal scale = (decimal)Math.Pow(10, places);
        decimal? Amount(string key) => Get(element, key) is { ValueKind: JsonValueKind.Number } v && v.TryGetDecimal(out decimal d) ? d / scale : null;
        double? utilization = Get(element, "utilization") is { ValueKind: JsonValueKind.Number } u && u.TryGetDouble(out double x) ? x : null;
        string currency = SafeLabel(Str(Get(element, "currency")));
        return new ExtraUsage(
            Get(element, "is_enabled").ValueKind == JsonValueKind.True,
            Amount("monthly_limit"),
            Amount("used_credits"),
            utilization,
            currency.Length > 0 ? currency : "USD");
    }

    static DateTimeOffset? ParseTime(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            return null;
        if (!DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
            return null;
        // The server recomputes resets_at on every call, so it jitters by milliseconds; keep whole minutes.
        long minute = TimeSpan.TicksPerMinute;
        return new DateTimeOffset((value.UtcTicks + minute / 2) / minute * minute, TimeSpan.Zero);
    }

    /// <summary>A salted hash that separates accounts in the history without storing the e-mail.</summary>
    static string AccountKey(string salt, string email, string organization)
    {
        if (email.Length == 0 && organization.Length == 0)
            return "unknown";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(salt));
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(email.ToLowerInvariant() + "|" + organization));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    internal static JsonElement Get(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;

    static string Str(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";

    static string SafeLabel(string value) =>
        Regex.IsMatch(value, @"^[\p{L}\p{N} _.\-()]{1,40}$") ? value : "";
}
