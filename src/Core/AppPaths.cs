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
            result = new
            {
                ok = true,
                seconds = (DateTimeOffset.UtcNow - started).TotalSeconds,
                claude = client.ExecutablePath,
                signature_warning = ClaudeLocator.Warning,
                snapshot,
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
