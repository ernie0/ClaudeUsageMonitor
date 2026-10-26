using System.Text;
using System.Text.Json;

namespace ClaudeUsageMonitor;

internal sealed record TokenCounts(long Input, long Output, long CacheWrite, long CacheRead)
{
    public static readonly TokenCounts Zero = new(0, 0, 0, 0);

    public long Total => Input + Output + CacheWrite + CacheRead;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead);
}

/// <summary>One model response: an opaque id for de-duplication, when, which model, and its token counts.</summary>
internal sealed record TokenEntry(string Key, DateTimeOffset At, string Model, TokenCounts Counts);

/// <summary>
/// Token counts from Claude Code's local transcripts (~/.claude/projects/**/*.jsonl, or under
/// CLAUDE_CONFIG_DIR). Only each response's id, time, model and the four token numbers are read;
/// message content is never kept, shown or written anywhere. Claude Code logs one response several
/// times (once per content block), so entries are de-duplicated by message id + request id.
/// Transcripts only grow, so each file is read once and afterwards only from where it was left off.
/// The numbers cover this computer only — not other devices or claude.ai.
/// </summary>
internal sealed class TokenLedger
{
    sealed class FileState
    {
        public long Offset;
        public DateTime WriteUtc;
        public List<TokenEntry> Entries = [];
    }

    static readonly byte[] UsageMarker = Encoding.UTF8.GetBytes("\"usage\"");

    readonly object gate = new();
    readonly Dictionary<string, FileState> files = new(StringComparer.Ordinal);
    readonly SemaphoreSlim scanGate = new(1, 1);
    readonly List<TokenEntry>? synthetic;

    public TokenLedger(string? root = null)
    {
        Root = root ?? DefaultRoot();
    }

    TokenLedger(List<TokenEntry> entries)
    {
        Root = "（示範資料）";
        synthetic = entries;
        LastScan = DateTimeOffset.UtcNow;
    }

    public string Root { get; }
    public DateTimeOffset? LastScan { get; private set; }
    public bool Scanning { get; private set; }
    public string Error { get; private set; } = "";

    public static string DefaultRoot()
    {
        string? config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string baseDir = !string.IsNullOrWhiteSpace(config)
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(baseDir, "projects");
    }

    /// <summary>Reads new transcript lines; files untouched for longer than <paramref name="horizon"/> are skipped.</summary>
    public async Task ScanAsync(TimeSpan horizon, CancellationToken ct = default)
    {
        if (synthetic != null || !await scanGate.WaitAsync(0, ct))
            return;
        Scanning = true;
        try
        {
            await Task.Run(() => Scan(horizon, ct), ct);
            LastScan = DateTimeOffset.UtcNow;
            Error = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = "讀不到 Claude Code 的對話紀錄資料夾。";
        }
        finally
        {
            Scanning = false;
            scanGate.Release();
        }
    }

    void Scan(TimeSpan horizon, CancellationToken ct)
    {
        if (!Directory.Exists(Root))
        {
            Error = "找不到 Claude Code 的對話紀錄資料夾。";
            return;
        }
        var cutoff = DateTime.UtcNow - horizon;
        foreach (string path in Directory.EnumerateFiles(Root, "*.jsonl", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists || info.LastWriteTimeUtc < cutoff)
                    continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            FileState state;
            lock (gate)
            {
                if (!files.TryGetValue(path, out state!) || info.Length < state.Offset)
                    files[path] = state = new FileState();
            }
            if (info.Length == state.Offset && info.LastWriteTimeUtc == state.WriteUtc)
                continue;
            try
            {
                var (entries, offset) = ReadFrom(path, state.Offset);
                lock (gate)
                {
                    state.Entries.AddRange(entries);
                    state.Offset = offset;
                    state.WriteUtc = info.LastWriteTimeUtc;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file being rotated or locked right now; it is picked up on the next scan.
            }
        }
    }

    /// <summary>Parses complete lines from <paramref name="start"/>; a trailing partial line is left for next time.</summary>
    static (List<TokenEntry> Entries, long Offset) ReadFrom(string path, long start)
    {
        var entries = new List<TokenEntry>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        stream.Seek(start, SeekOrigin.Begin);
        var line = new MemoryStream();
        var buffer = new byte[1 << 16];
        long position = start, committed = start;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            int from = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;
                line.Write(buffer, from, i - from);
                if (Parse(line.GetBuffer().AsSpan(0, (int)line.Length)) is { } entry)
                    entries.Add(entry);
                line.SetLength(0);
                from = i + 1;
                committed = position + i + 1;
            }
            line.Write(buffer, from, read - from);
            position += read;
        }
        return (entries, committed);
    }

    static TokenEntry? Parse(ReadOnlySpan<byte> line)
    {
        if (line.IndexOf(UsageMarker) < 0)
            return null;
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            var message = UsageParser.Get(root, "message");
            var usage = UsageParser.Get(message, "usage");
            if (usage.ValueKind != JsonValueKind.Object)
                return null;
            string model = UsageParser.Get(message, "model").ValueKind == JsonValueKind.String ? UsageParser.Get(message, "model").GetString() ?? "" : "";
            if (model.Length == 0 || model.StartsWith('<'))
                return null;
            if (UsageParser.Get(root, "timestamp").ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(UsageParser.Get(root, "timestamp").GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                return null;
            static long N(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long v) && v > 0 ? v : 0;
            var counts = new TokenCounts(
                N(UsageParser.Get(usage, "input_tokens")),
                N(UsageParser.Get(usage, "output_tokens")),
                N(UsageParser.Get(usage, "cache_creation_input_tokens")),
                N(UsageParser.Get(usage, "cache_read_input_tokens")));
            if (counts.Total == 0)
                return null;
            string id = UsageParser.Get(message, "id").ValueKind == JsonValueKind.String ? UsageParser.Get(message, "id").GetString() ?? "" : "";
            string request = UsageParser.Get(root, "requestId").ValueKind == JsonValueKind.String ? UsageParser.Get(root, "requestId").GetString() ?? "" : "";
            string key = id.Length > 0 || request.Length > 0 ? id + ":" + request : at.UtcTicks + ":" + counts.Total;
            return new TokenEntry(key, at.ToUniversalTime(), model, counts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>De-duplicated entries in [from, to).</summary>
    public List<TokenEntry> Entries(DateTimeOffset from, DateTimeOffset to)
    {
        IEnumerable<TokenEntry> all;
        lock (gate)
            all = synthetic ?? files.Values.SelectMany(f => f.Entries).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return all.Where(e => e.At >= from && e.At < to && seen.Add(e.Key)).OrderBy(e => e.At).ToList();
    }

    public TokenCounts Sum(DateTimeOffset from, DateTimeOffset to, Func<string, bool>? model = null) =>
        Entries(from, to).Where(e => model == null || model(e.Model)).Aggregate(TokenCounts.Zero, (sum, e) => sum + e.Counts);

    /// <summary>Synthetic tokens that follow the demo history (about 2.5 million tokens per hour of use).</summary>
    public static TokenLedger Synthetic(DateTimeOffset now)
    {
        var random = new Random(7);
        var entries = new List<TokenEntry>();
        for (var t = now.AddDays(-35); t < now; t = t.AddMinutes(10))
        {
            int hour = t.ToLocalTime().Hour;
            if (hour < 9 && hour >= 1)
                continue;
            string model = random.NextDouble() < 0.82 ? "claude-opus-5-5" : "claude-fable-5-1";
            long read = 300_000 + random.Next(250_000);
            entries.Add(new TokenEntry(Guid.NewGuid().ToString("N"), t, model,
                new TokenCounts(random.Next(40, 400), 2_000 + random.Next(6_000), 9_000 + random.Next(30_000), read)));
        }
        return new TokenLedger(entries);
    }
}
