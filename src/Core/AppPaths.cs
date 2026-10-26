using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>Where data lives, the demo folder, and the --probe query; the same on Windows and macOS.</summary>
internal static class AppPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\ClaudeUsageMonitor on Windows, ~/Library/Application Support/ClaudeUsageMonitor on
    /// macOS; CLAUDE_USAGE_MONITOR_HOME overrides both (used for tests).
    /// </summary>
    public static string DataRoot(bool demo)
    {
        if (demo)
            return Path.Combine(Path.GetTempPath(), "ClaudeUsageMonitor-demo");
        string? custom = Environment.GetEnvironmentVariable("CLAUDE_USAGE_MONITOR_HOME");
        if (!string.IsNullOrWhiteSpace(custom))
            return Path.GetFullPath(custom);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ClaudeUsageMonitor");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageMonitor");
    }

    /// <summary>A fresh demo folder with a week of synthetic history.</summary>
    public static DemoSource NewDemo(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        var demo = new DemoSource(DateTimeOffset.UtcNow);
        demo.Seed(new HistoryStore(root));
        return demo;
    }

    /// <summary>Reads this computer's transcripts and writes the token report for the last <paramref name="days"/> days.</summary>
    public static int ProbeTokens(string output, int days)
    {
        var ledger = new TokenLedger();
        var started = DateTimeOffset.UtcNow;
        ledger.ScanAsync(TimeSpan.FromDays(days + 1)).GetAwaiter().GetResult();
        var firstScan = DateTimeOffset.UtcNow - started;
        started = DateTimeOffset.UtcNow;
        ledger.ScanAsync(TimeSpan.FromDays(days + 1)).GetAwaiter().GetResult();
        var secondScan = DateTimeOffset.UtcNow - started;
        var report = TokenReport.Build(ledger, null, days, DateTimeOffset.UtcNow);
        var result = new
        {
            root = ledger.Root,
            error = ledger.Error,
            first_scan_seconds = firstScan.TotalSeconds,
            rescan_seconds = secondScan.TotalSeconds,
            days,
            range = report.Range,
            range_total = report.Range.Total,
            per_day = report.Days.Select(d => new { day = d.Day.ToString("yyyy-MM-dd"), total = d.Total }),
            models = report.Models.Select(m => new { model = m.Model, total = m.Counts.Total, output = m.Counts.Output }),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, AtomicJson.Options));
        return ledger.Error.Length == 0 ? 0 : 2;
    }

    /// <summary>One real query without touching the history; for troubleshooting and verification.</summary>
    public static int Probe(string output, string? claudePath)
    {
        string root = DataRoot(false);
        var settings = AtomicJson.Load(Path.Combine(root, "settings.json"), () => new Settings());
        settings.Normalize();
        var client = new ClaudeUsageClient(Path.Combine(root, "neutral"), () => claudePath ?? settings.ClaudePath);
        object result;
        int code;
        var started = DateTimeOffset.UtcNow;
        try
        {
            var snapshot = client.ReadAsync(settings.Salt, CancellationToken.None).GetAwaiter().GetResult();
            double seconds = (DateTimeOffset.UtcNow - started).TotalSeconds;
            started = DateTimeOffset.UtcNow;
            var behaviors = client.ReadBehaviorsAsync(CancellationToken.None).GetAwaiter().GetResult();
            result = new
            {
                ok = true,
                seconds,
                behaviors_seconds = (DateTimeOffset.UtcNow - started).TotalSeconds,
                claude = client.ExecutablePath,
                signature_warning = ClaudeLocator.Warning,
                snapshot,
                behaviors,
            };
            code = 0;
        }
        catch (MonitorException e)
        {
            result = new
            {
                ok = false,
                seconds = (DateTimeOffset.UtcNow - started).TotalSeconds,
                claude = client.ExecutablePath,
                error_code = e.Code,
                message = MonitorService.Explain(e.Code),
            };
            code = 2;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, AtomicJson.Options));
        return code;
    }
}
