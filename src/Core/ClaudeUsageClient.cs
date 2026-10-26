using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageMonitor;

internal interface IUsageSource
{
    Task<UsageSnapshot> ReadAsync(string salt, CancellationToken ct);
    string ExecutablePath { get; }
}

/// <summary>
/// Starts the official claude.exe in SDK stream-json mode and asks it, through control requests only,
/// for the account (initialize) and the plan limits (get_usage). No prompt is ever sent, so no model
/// call is made. Each poll runs its own short-lived process so nothing stays resident between polls.
/// </summary>
internal sealed class ClaudeUsageClient(string workDir, Func<string?> configuredPath) : IUsageSource
{
    const int TimeoutSeconds = 30;

    static readonly string[] Arguments =
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        // No user/project settings: no hooks, plugins or MCP servers start for a usage query.
        "--setting-sources", "",
        "--strict-mcp-config",
        "--disable-slash-commands",
        // Do not write a transcript for these sessions.
        "--no-session-persistence",
    ];

    public string ExecutablePath { get; private set; } = "";

    public async Task<UsageSnapshot> ReadAsync(string salt, CancellationToken ct)
    {
        string exe = ClaudeLocator.FindVerified(configuredPath());
        ExecutablePath = exe;
        Directory.CreateDirectory(workDir);

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
            WorkingDirectory = workDir,
        };
        foreach (string argument in Arguments)
            info.ArgumentList.Add(argument);
        // CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC is deliberately not set: without feature flags the
        // per-model weekly windows (model_scoped) are missing from get_usage.

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new MonitorException("START_FAILED");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new MonitorException("START_FAILED");
        }

        using (process)
        {
            // stderr is drained and discarded: it may contain account details and is never logged.
            _ = Task.Run(() => DrainAsync(process.StandardError));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            try
            {
                await SendAsync(process, "init", new { subtype = "initialize" }, timeout.Token);
                await SendAsync(process, "version", new { subtype = "get_binary_version" }, timeout.Token);
                await SendAsync(process, "usage", new { subtype = "get_usage", skip_behaviors = true }, timeout.Token);
                var responses = await ReadResponsesAsync(process, ["init", "version", "usage"], timeout.Token);

                var account = UsageParser.Get(Body(responses["init"]), "account");
                var usage = Body(responses["usage"]);
                string version = responses.TryGetValue("version", out var v) ? VersionOf(v) : "";
                return UsageParser.Parse(account, usage, salt, version, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new MonitorException("TIMEOUT");
            }
            catch (IOException)
            {
                throw new MonitorException("EXITED_EARLY");
            }
            finally
            {
                Shutdown(process);
            }
        }
    }

    static async Task SendAsync(Process process, string id, object request, CancellationToken ct)
    {
        string line = JsonSerializer.Serialize(new { type = "control_request", request_id = id, request });
        await process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
    }

    /// <summary>Collects control responses by request id; refuses anything the CLI asks of us.</summary>
    static async Task<Dictionary<string, JsonElement>> ReadResponsesAsync(Process process, string[] wanted, CancellationToken ct)
    {
        var found = new Dictionary<string, JsonElement>();
        while (wanted.Any(id => !found.ContainsKey(id)))
        {
            string? line = await process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
            {
                // get_binary_version is optional; the other two are not.
                if (found.ContainsKey("init") && found.ContainsKey("usage"))
                    break;
                throw new MonitorException("EXITED_EARLY");
            }
            if (line.Length == 0 || line[0] != '{')
                continue;

            JsonElement message;
            try
            {
                using var doc = JsonDocument.Parse(line);
                message = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            string type = UsageParser.Get(message, "type").GetString() ?? "";
            if (type == "control_request")
            {
                await RefuseAsync(process, UsageParser.Get(message, "request_id"), ct);
                continue;
            }
            if (type != "control_response")
                continue;

            var response = UsageParser.Get(message, "response");
            string id = UsageParser.Get(response, "request_id").ValueKind == JsonValueKind.String
                ? UsageParser.Get(response, "request_id").GetString()!
                : "";
            if (!wanted.Contains(id))
                continue;
            if (UsageParser.Get(response, "subtype").GetString() == "error")
            {
                if (id == "version")
                {
                    found[id] = default;
                    continue;
                }
                // The error text is inspected in memory only and never persisted.
                string error = UsageParser.Get(response, "error").ValueKind == JsonValueKind.String
                    ? UsageParser.Get(response, "error").GetString()!
                    : "";
                bool unsupported = error.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("not supported", StringComparison.OrdinalIgnoreCase);
                throw new MonitorException(unsupported ? "NOT_SUPPORTED" : "PROTOCOL_ERROR");
            }
            found[id] = response;
        }
        return found;
    }

    static async Task RefuseAsync(Process process, JsonElement requestId, CancellationToken ct)
    {
        if (requestId.ValueKind != JsonValueKind.String)
            return;
        string line = JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new { subtype = "error", request_id = requestId.GetString(), error = "Read-only usage monitor" },
        });
        await process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
    }

    static JsonElement Body(JsonElement response) => UsageParser.Get(response, "response");

    static string VersionOf(JsonElement response)
    {
        var body = Body(response);
        foreach (string key in new[] { "version", "binary_version" })
        {
            if (UsageParser.Get(body, key) is { ValueKind: JsonValueKind.String } value)
                return value.GetString() ?? "";
        }
        return "";
    }

    static async Task DrainAsync(StreamReader reader)
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

    static void Shutdown(Process process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(5000))
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
    }
}
