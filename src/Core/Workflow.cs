using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageMonitor;

internal enum JobState { Starting, Working, NeedsPermission, Waiting, Stopped, Failed }

internal enum JobLineKind { User, Claude, Tool, Note, Error }

internal sealed record JobLine(JobLineKind Kind, string Text);

/// <summary>Claude Code's permission modes as the workflow window offers them ("dontAsk" is for unattended scripts and left out).</summary>
internal static class PermissionModes
{
    public const string Ask = "manual";
    public const string Everything = "bypassPermissions";

    public static readonly (string Id, string Name, string Note)[] All =
    [
        (Ask, "每次詢問", "改檔或執行指令前都先問你"),
        ("acceptEdits", "自動允許改檔", "改檔不問，執行指令仍會問"),
        ("auto", "自動判斷", "Claude Code 判斷安全的動作自動放行，有風險的才問"),
        ("plan", "只規劃", "只讀檔、提出計畫，不改檔也不執行指令"),
        (Everything, "全部允許", "不問就改檔、執行任何指令（有風險）"),
    ];

    public static (string Id, string Name, string Note) Of(string id) => All.FirstOrDefault(m => m.Id == id) is { Id: not null } mode ? mode : All[0];
}

/// <summary>Claude asks before using a tool: what it wants to do, in words, and the exact command or file.</summary>
internal sealed record PermissionAsk(string RequestId, string Summary, string Detail, JsonElement Input);

/// <summary>
/// One Claude Code conversation run through the official claude.exe in SDK stream-json mode, in a folder
/// the user picked. It loads the user's own settings (CLAUDE.md, hooks, plugins) like a session in VS Code
/// and is saved to Claude Code's history, but runs in the permission mode chosen in the window (not the
/// one in the user's settings): whatever Claude Code would ask about comes back here as a question
/// (can_use_tool). The reader runs on a background thread; <see cref="Changed"/> fires there.
/// </summary>
internal sealed class ClaudeSession : IDisposable
{
    static readonly string[] Arguments =
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        // Permission questions come to this window instead of a terminal prompt.
        "--permission-prompt-tool", "stdio",
    ];

    readonly object gate = new();
    readonly List<JobLine> lines = [];
    readonly Process process;
    readonly StringBuilder live = new();
    int requests;

    public ClaudeSession(string exe, string folder, string mode, string prompt)
    {
        Folder = folder;
        Mode = PermissionModes.Of(mode).Id;
        Title = Summarize(prompt, 40);
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = folder,
        };
        foreach (string argument in Arguments)
            info.ArgumentList.Add(argument);
        // The mode chosen in the window, not the one in the user's settings.
        info.ArgumentList.Add("--permission-mode");
        info.ArgumentList.Add(Mode);
        foreach (string argument in ExtraArguments)
            info.ArgumentList.Add(argument);
        process = Process.Start(info) ?? throw new MonitorException("START_FAILED");
        _ = Task.Run(() => Drain(process.StandardError));
        _ = Task.Run(ReadLoop);
        Write(new { type = "control_request", request_id = "init", request = new { subtype = "initialize" } });
        Send(prompt);
    }

    /// <summary>--screenshot runs its live check without the user's settings and history, on a small model.</summary>
    public static string[] ExtraArguments { get; set; } = [];

    public string Folder { get; }
    public string Mode { get; }
    public string Title { get; }
    public JobState State { get; private set; } = JobState.Starting;
    public PermissionAsk? Ask { get; private set; }
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;

    /// <summary>True while Claude is working on something (including waiting for a permission answer).</summary>
    public bool Running => State is JobState.Starting or JobState.Working or JobState.NeedsPermission;

    public event Action? Changed;

    /// <summary>A copy of the conversation so far, plus the reply being written right now.</summary>
    public (List<JobLine> Lines, string Live) Snapshot()
    {
        lock (gate)
            return (lines.ToList(), live.ToString());
    }

    /// <summary>Sends the next instruction (the first one, or a follow-up after Claude replied).</summary>
    public void Send(string text)
    {
        if (State is JobState.Stopped or JobState.Failed || text.Trim().Length == 0)
            return;
        Add(JobLineKind.User, text.Trim());
        State = JobState.Working;
        Write(new { type = "user", session_id = "", parent_tool_use_id = (string?)null, message = new { role = "user", content = text.Trim() } });
        Changed?.Invoke();
    }

    /// <summary>Answers Claude's permission question.</summary>
    public void Answer(bool allow)
    {
        var ask = Ask;
        if (ask == null)
            return;
        Ask = null;
        State = JobState.Working;
        Add(JobLineKind.Note, (allow ? "已允許：" : "已拒絕：") + ask.Summary);
        object response = allow
            ? new { behavior = "allow", updatedInput = ask.Input }
            : new { behavior = "deny", message = "使用者在工作流視窗拒絕了這個動作。" };
        Write(new { type = "control_response", response = new { subtype = "success", request_id = ask.RequestId, response } });
        Changed?.Invoke();
    }

    /// <summary>Stops what Claude is doing now; the conversation stays open for another instruction.</summary>
    public void Interrupt()
    {
        if (!Running)
            return;
        if (Ask is { } ask)
        {
            Ask = null;
            Write(new { type = "control_response", response = new { subtype = "success", request_id = ask.RequestId, response = new { behavior = "deny", message = "使用者停止了這項工作。", interrupt = true } } });
        }
        Write(new { type = "control_request", request_id = "stop-" + Interlocked.Increment(ref requests), request = new { subtype = "interrupt" } });
        Add(JobLineKind.Note, "已要求停止");
        Changed?.Invoke();
    }

    void Write(object message)
    {
        string line = JsonSerializer.Serialize(message);
        lock (gate)
        {
            try
            {
                process.StandardInput.WriteLine(line);
                process.StandardInput.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    void Add(JobLineKind kind, string text)
    {
        lock (gate)
            lines.Add(new JobLine(kind, text));
    }

    async Task ReadLoop()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0 || line[0] != '{')
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    Handle(doc.RootElement);
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        if (State != JobState.Stopped)
        {
            Add(JobLineKind.Error, "Claude Code 已結束這段對話。");
            State = JobState.Failed;
        }
        Ask = null;
        Changed?.Invoke();
    }

    void Handle(JsonElement message)
    {
        switch (Str(message, "type"))
        {
            case "stream_event":
                // The reply appears as it is written.
                var ev = UsageParser.Get(message, "event");
                if (Str(ev, "type") == "content_block_start" && Str(UsageParser.Get(ev, "content_block"), "type") == "text")
                {
                    lock (gate)
                        live.Clear();
                }
                else if (Str(ev, "type") == "content_block_delta" && Str(UsageParser.Get(ev, "delta"), "type") == "text_delta")
                {
                    lock (gate)
                        live.Append(Str(UsageParser.Get(ev, "delta"), "text"));
                    Changed?.Invoke();
                }
                return;
            case "assistant":
                // Sub-agents' own steps are left out; their result comes back as the Task tool's.
                if (UsageParser.Get(message, "parent_tool_use_id").ValueKind == JsonValueKind.String)
                    return;
                foreach (var block in Items(UsageParser.Get(UsageParser.Get(message, "message"), "content")))
                {
                    switch (Str(block, "type"))
                    {
                        case "text" when Str(block, "text").Trim().Length > 0:
                            lock (gate)
                            {
                                live.Clear();
                                lines.Add(new JobLine(JobLineKind.Claude, Str(block, "text").Trim()));
                            }
                            break;
                        case "tool_use":
                            Add(JobLineKind.Tool, Describe(Str(block, "name"), UsageParser.Get(block, "input")).Summary);
                            break;
                    }
                }
                if (State == JobState.Starting)
                    State = JobState.Working;
                Changed?.Invoke();
                return;
            case "control_request":
                var request = UsageParser.Get(message, "request");
                string id = Str(message, "request_id");
                if (Str(request, "subtype") == "can_use_tool")
                {
                    var input = UsageParser.Get(request, "input").Clone();
                    var (summary, detail) = Describe(Str(request, "tool_name"), input);
                    Ask = new PermissionAsk(id, summary, detail, input);
                    State = JobState.NeedsPermission;
                }
                else
                {
                    Write(new { type = "control_response", response = new { subtype = "error", request_id = id, error = "Not supported by the workflow window" } });
                }
                Changed?.Invoke();
                return;
            case "result":
                string subtype = Str(message, "subtype");
                if (subtype != "success")
                    Add(JobLineKind.Error, subtype == "error_during_execution" ? "這一輪被中斷了。" : "這一輪沒有完成（" + subtype + "）。");
                lock (gate)
                    live.Clear();
                Ask = null;
                State = JobState.Waiting;
                Changed?.Invoke();
                return;
        }
    }

    /// <summary>What a tool call does, in words (for the log) and in full (for a permission question).</summary>
    public static (string Summary, string Detail) Describe(string tool, JsonElement input)
    {
        string Field(string name) => Str(input, name);
        string FileName(string name) => Field(name) is { Length: > 0 } path ? Path.GetFileName(path.TrimEnd('\\', '/')) : "";
        string command = Field("command");
        return tool switch
        {
            "Read" => ("讀取 " + FileName("file_path"), Field("file_path")),
            "Write" => ("建立或覆寫 " + FileName("file_path"), Field("file_path")),
            "Edit" or "MultiEdit" => ("修改 " + FileName("file_path"), Field("file_path")),
            "NotebookEdit" => ("修改筆記本 " + FileName("notebook_path"), Field("notebook_path")),
            "Bash" or "PowerShell" => ("執行指令：" + Summarize(command, 60), command),
            "Glob" => ("找檔案：" + Field("pattern"), Field("pattern")),
            "Grep" => ("搜尋內容：" + Summarize(Field("pattern"), 50), Field("pattern")),
            "WebFetch" => ("讀取網頁：" + Field("url"), Field("url")),
            "WebSearch" => ("搜尋網路：" + Field("query"), Field("query")),
            "Task" or "Agent" => ("派出子代理：" + Field("description"), Field("prompt")),
            "TodoWrite" => ("更新待辦清單", ""),
            "Skill" => ("使用技能：" + Field("skill"), Field("skill")),
            _ => ("使用工具：" + tool, input.ValueKind == JsonValueKind.Object ? Summarize(input.GetRawText(), 300) : ""),
        };
    }

    static string Summarize(string text, int max)
    {
        string first = text.Trim().Split('\n')[0].Trim();
        return first.Length <= max ? first : first[..max] + "…";
    }

    static string Str(JsonElement e, string name) =>
        UsageParser.Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";

    static IEnumerable<JsonElement> Items(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    static async Task Drain(StreamReader reader)
    {
        try
        {
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Ends the conversation and its claude.exe.</summary>
    public void Dispose()
    {
        if (State != JobState.Failed)
            State = JobState.Stopped;
        try
        {
            lock (gate)
                process.StandardInput.Close();
            if (!process.WaitForExit(3000))
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }
        process.Dispose();
    }
}

/// <summary>The conversations the workflow window shows; only open ones are kept.</summary>
internal sealed class WorkflowHub(Func<string?> configuredPath) : IDisposable
{
    readonly List<ClaudeSession> sessions = [];

    public event Action? Changed;

    public IReadOnlyList<ClaudeSession> Sessions
    {
        get
        {
            lock (sessions)
                return sessions.ToList();
        }
    }

    public int Running => Sessions.Count(s => s.Running);

    public bool NeedsAnswer => Sessions.Any(s => s.State == JobState.NeedsPermission);

    /// <summary>Starts a new conversation; the claude executable is signature-checked like the usage queries.</summary>
    public ClaudeSession Start(string folder, string mode, string prompt)
    {
        var session = new ClaudeSession(ClaudeLocator.FindVerified(configuredPath()), folder, mode, prompt);
        session.Changed += () => Changed?.Invoke();
        lock (sessions)
            sessions.Add(session);
        Changed?.Invoke();
        return session;
    }

    public void Close(ClaudeSession session)
    {
        lock (sessions)
            sessions.Remove(session);
        Task.Run(session.Dispose);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        foreach (var session in Sessions)
            session.Dispose();
        lock (sessions)
            sessions.Clear();
    }
}

/// <summary>Folders Claude Code worked in recently, newest first.</summary>
internal static class RecentProjects
{
    /// <summary>
    /// Reads only the "cwd" field of each project's newest transcript under ~/.claude/projects (the
    /// folder names there are lossy), keeps folders that still exist.
    /// </summary>
    public static List<string> Load(int max = 12)
    {
        var found = new List<(string Path, DateTime At)>();
        string root = TokenLedger.DefaultRoot();
        if (!Directory.Exists(root))
            return [];
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
        {
            var newest = dir.EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null || CwdOf(newest.FullName) is not { } cwd)
                continue;
            found.Add((cwd, newest.LastWriteTimeUtc));
        }
        return found.Where(f => Directory.Exists(f.Path))
            .GroupBy(f => Path.TrimEndingDirectorySeparator(f.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Path: g.Key, At: g.Max(f => f.At)))
            .OrderByDescending(f => f.At)
            .Take(max)
            .Select(f => f.Path)
            .ToList();
    }

    static string? CwdOf(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            for (int i = 0; i < 40 && reader.ReadLine() is { } line; i++)
            {
                if (!line.Contains("\"cwd\""))
                    continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
                    return cwd.GetString();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return null;
    }
}
