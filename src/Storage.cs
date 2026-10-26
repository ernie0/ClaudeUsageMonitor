using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClaudeUsageMonitor;

internal static class AtomicJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback() : fallback();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return fallback();
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}

internal sealed class Settings
{
    public int IntervalSeconds { get; set; } = 120;
    public bool Alerts { get; set; } = true;
    public int WarnRemaining { get; set; } = 20;
    public int CriticalRemaining { get; set; } = 5;
    public bool NotifyReset { get; set; } = true;
    public bool Compact { get; set; }
    public bool TopMost { get; set; } = true;
    public int OpacityPercent { get; set; } = 95;
    public bool StartHidden { get; set; }
    public int RetentionDays { get; set; } = 90;
    public string ClaudePath { get; set; } = "";
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public string Salt { get; set; } = "";

    public void Normalize()
    {
        IntervalSeconds = Math.Clamp(IntervalSeconds, 60, 900);
        WarnRemaining = Math.Clamp(WarnRemaining, 1, 90);
        CriticalRemaining = Math.Clamp(CriticalRemaining, 0, WarnRemaining - 1);
        OpacityPercent = Math.Clamp(OpacityPercent, 40, 100);
        RetentionDays = Math.Clamp(RetentionDays, 7, 365);
        ClaudePath ??= "";
        if (string.IsNullOrEmpty(Salt))
            Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ClaudeUsageMonitor";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}

/// <summary>One observed window in one poll; the unit of the history files and the CSV export.</summary>
internal sealed record UsageSample(
    DateTimeOffset At,
    string Account,
    string Plan,
    string Window,
    string Label,
    double Used,
    DateTimeOffset? Reset,
    bool Gap,
    int Interval)
{
    [JsonIgnore]
    public double Remaining => Math.Clamp(100 - Used, 0, 100);

    public static IEnumerable<UsageSample> From(UsageSnapshot snapshot, bool gap, int interval) =>
        snapshot.Windows.Select(w => new UsageSample(
            snapshot.ObservedAt, snapshot.AccountKey, snapshot.PlanType, w.Id, w.Label, w.UsedPercent, w.ResetsAt, gap, interval));
}

/// <summary>Append-only JSON lines, one file per UTC day under history/.</summary>
internal sealed class HistoryStore(string root)
{
    readonly object gate = new();
    static readonly JsonSerializerOptions LineOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    string Dir => Path.Combine(root, "history");

    string FileFor(DateTimeOffset day) => Path.Combine(Dir, day.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

    public void Append(IEnumerable<UsageSample> rows)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Dir);
            foreach (var group in rows.GroupBy(r => FileFor(r.At)))
            {
                var text = new StringBuilder();
                foreach (var row in group)
                    text.Append(JsonSerializer.Serialize(row, LineOptions)).Append('\n');
                File.AppendAllText(group.Key, text.ToString(), new UTF8Encoding(false));
            }
        }
    }

    public List<UsageSample> Load(DateTimeOffset from, DateTimeOffset to)
    {
        var rows = new List<UsageSample>();
        lock (gate)
        {
            for (var day = from.UtcDateTime.Date; day <= to.UtcDateTime.Date; day = day.AddDays(1))
            {
                string path = FileFor(new DateTimeOffset(day, TimeSpan.Zero));
                if (!File.Exists(path))
                    continue;
                foreach (string line in File.ReadLines(path))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<UsageSample>(line, LineOptions) is { } row && row.At >= from && row.At <= to)
                            rows.Add(row);
                    }
                    catch (JsonException)
                    {
                        // A torn last line after a crash; skip it.
                    }
                }
            }
        }
        rows.Sort((a, b) => a.At.CompareTo(b.At));
        return rows;
    }

    public void Cleanup(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-retentionDays);
        lock (gate)
        {
            if (!Directory.Exists(Dir))
                return;
            foreach (string path in Directory.GetFiles(Dir, "*.jsonl"))
            {
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day) && day < cutoff)
                    File.Delete(path);
            }
        }
    }

    public static void ExportCsv(string path, IEnumerable<UsageSample> rows)
    {
        var text = new StringBuilder("時間,視窗,已使用%,剩餘%,重置時間,方案,監測缺口\r\n");
        foreach (var r in rows)
        {
            text.Append(r.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(r.Label)).Append(',')
                .Append(r.Used.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(r.Remaining.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(r.Reset?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(Csv(r.Plan)).Append(',')
                .Append(r.Gap ? "是" : "").Append("\r\n");
        }
        // BOM so Excel opens the Chinese headers correctly.
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    static string Csv(string value)
    {
        // Neutralise spreadsheet formulas as well as quoting.
        if (value.Length > 0 && "=+-@".Contains(value[0]))
            value = "'" + value;
        return value.Contains(',') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

/// <summary>Writes an error code only when an operation's state changes; never raw output.</summary>
internal sealed class AppLog(string root)
{
    readonly object gate = new();
    readonly Dictionary<string, string> last = [];

    public void Write(string operation, string code)
    {
        lock (gate)
        {
            if (last.TryGetValue(operation, out string? prior) && prior == code)
                return;
            last[operation] = code;
            try
            {
                string dir = Path.Combine(root, "logs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "app-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\t{operation}\t{code}\n", new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
