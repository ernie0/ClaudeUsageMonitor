using System.Diagnostics;

namespace ClaudeUsageMonitor;

internal sealed class MonitorService : IDisposable
{
    readonly IUsageSource source;
    readonly AppLog log;
    readonly AlertState alertState;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim wake = new(0, 1);
    readonly SemaphoreSlim pollGate = new(1, 1);
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly object recentGate = new();
    List<UsageSample> recent;
    Task? loop;
    bool forceGap;
    volatile bool locked;
    int idlePolls;
    int plannedInterval;
    FileSystemWatcher? activity;
    long lastPollMs = -600_000;
    long lastManualMs = -60_000;
    DateTime lastCleanup;

    public MonitorService(string root, IUsageSource source, bool demo = false)
    {
        Root = root;
        Demo = demo;
        this.source = source;
        Directory.CreateDirectory(root);
        Settings = AtomicJson.Load(SettingsPath, () => new Settings());
        Settings.Normalize();
        SaveSettings();
        Workflows = new WorkflowHub(() => Settings.ClaudePath);
        plannedInterval = Settings.IntervalSeconds;
        History = new HistoryStore(root);
        log = new AppLog(root);
        alertState = AtomicJson.Load(Path.Combine(root, "alerts.json"), () => new AlertState());
        recent = History.Load(DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow);
        Current = AtomicJson.Load<UsageSnapshot?>(Path.Combine(root, "last_good.json"), () => null);
        if (Current != null)
        {
            Status = "上次的資料，正在重新取得";
            Forecasts = BuildForecasts(Current, DateTimeOffset.UtcNow);
        }
        Behaviors = AtomicJson.Load<UsageBehaviors?>(Path.Combine(root, "behaviors.json"), () => null);
        Tokens = demo ? TokenLedger.Synthetic(DateTimeOffset.UtcNow) : new TokenLedger();
    }

    /// <summary>Token counts from this computer's Claude Code transcripts (read on demand by the analysis).</summary>
    public TokenLedger Tokens { get; }

    /// <summary>The last attribution from Claude Code (kept on disk so the analysis opens with it).</summary>
    public UsageBehaviors? Behaviors { get; private set; }

    public bool AnalyzingBehaviors { get; private set; }

    readonly SemaphoreSlim behaviorGate = new(1, 1);

    /// <summary>Asks Claude Code for its attribution; takes several seconds, so only when the user asks.</summary>
    public async Task RefreshBehaviorsAsync(CancellationToken ct = default)
    {
        if (!await behaviorGate.WaitAsync(0, ct))
            return;
        AnalyzingBehaviors = true;
        Changed?.Invoke();
        try
        {
            if (await source.ReadBehaviorsAsync(ct) is { } behaviors)
            {
                Behaviors = behaviors;
                TrySave(Path.Combine(Root, "behaviors.json"), behaviors);
            }
            log.Write("behaviors", "OK");
        }
        catch (MonitorException e)
        {
            log.Write("behaviors", e.Code);
        }
        finally
        {
            AnalyzingBehaviors = false;
            behaviorGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>The habits analysis over the last <paramref name="days"/> days for the current account.</summary>
    public UsageAnalysis Analyze(int days)
    {
        var now = DateTimeOffset.UtcNow;
        string? account = Current?.AccountKey;
        // Cycle review looks back five weeks whatever the range.
        var samples = History.Load(now.AddDays(-Math.Max(days, 36)), now).Where(s => account == null || s.Account == account).ToList();
        return UsageAnalyzer.Analyze(samples, now, days);
    }

    /// <summary>Re-reads new transcript lines (fast after the first time), then the analysis can build its token report.</summary>
    public Task ScanTokensAsync(int days) => Tokens.ScanAsync(TimeSpan.FromDays(Math.Max(days, 8) + 1));

    public string Root { get; }
    public bool Demo { get; }
    public Settings Settings { get; }

    /// <summary>Conversations started from the workflow window.</summary>
    public WorkflowHub Workflows { get; }
    public HistoryStore History { get; }
    public UsageSnapshot? Current { get; private set; }
    public IReadOnlyDictionary<string, Forecast> Forecasts { get; private set; } = new Dictionary<string, Forecast>();
    public bool Fresh { get; private set; }
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "正在取得額度";
    public string ErrorCode { get; private set; } = "";
    public int Failures { get; private set; }
    public DateTimeOffset? NextPoll { get; private set; }
    public string ClaudePath => source.ExecutablePath;

    /// <summary>True while polling runs slower than the configured interval because nothing changes.</summary>
    public bool SlowedDown => Failures == 0 && plannedInterval > Settings.IntervalSeconds;

    string SettingsPath => Path.Combine(Root, "settings.json");

    public event Action? Changed;
    /// <summary>All alerts from one poll together, so the overlay can show them as one notification.</summary>
    public event Action<IReadOnlyList<AlertMessage>>? Alert;

    public void Start()
    {
        if (loop != null)
            return;
        WatchActivity();
        loop = Task.Run(RunAsync);
    }

    /// <summary>
    /// Watches only whether Claude Code writes under ~/.claude/projects (file contents are never read).
    /// While polling has slowed down, that activity triggers a poll right away instead of waiting.
    /// </summary>
    void WatchActivity()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (Demo || !Directory.Exists(dir))
            return;
        try
        {
            activity = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            activity.Changed += OnActivity;
            activity.Created += OnActivity;
            activity.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            activity?.Dispose();
            activity = null;
        }
    }

    void OnActivity(object sender, FileSystemEventArgs e)
    {
        // Never faster than the configured interval; only matters once polling has slowed down.
        if (!SlowedDown || locked || Busy || clock.ElapsedMilliseconds - lastPollMs < Settings.IntervalSeconds * 1000L)
            return;
        idlePolls = 0;
        log.Write("trigger", "claude-code-activity");
        Refresh();
    }

    /// <summary>Requests a poll now; manual refreshes are limited to one per 10 seconds.</summary>
    public bool Refresh(bool manual = false)
    {
        if (manual)
        {
            if (clock.ElapsedMilliseconds - lastManualMs < 10_000)
                return false;
            lastManualMs = clock.ElapsedMilliseconds;
        }
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        return true;
    }

    /// <summary>After sleep or a network change the next sample must not be joined to the old run.</summary>
    public void Resume()
    {
        forceGap = true;
        Fresh = false;
        idlePolls = 0;
        Refresh();
        Changed?.Invoke();
    }

    /// <summary>Polls slowly while the workstation is locked and catches up as soon as it unlocks.</summary>
    public void SetLocked(bool value)
    {
        locked = value;
        if (!value)
        {
            idlePolls = 0;
            Refresh();
        }
    }

    /// <summary>
    /// The configured interval while usage moves. After every three unchanged polls the wait doubles,
    /// up to 10 minutes (or the configured interval if that is longer); 15 minutes while locked.
    /// Each sample stores the interval it was taken with, so gap detection follows the slower pace.
    /// </summary>
    int NextInterval()
    {
        int interval = Settings.IntervalSeconds;
        if (Failures > 0)
            return Backoff(Failures, interval);
        if (locked)
            return Math.Max(interval, 900);
        int doublings = Math.Min(3, idlePolls / 3);
        return Math.Min(Math.Max(interval, 600), interval << doublings);
    }

    public bool SaveSettings()
    {
        try
        {
            Settings.Normalize();
            AtomicJson.Save(SettingsPath, Settings);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public List<UsageSample> RecentSamples()
    {
        lock (recentGate)
            return recent.ToList();
    }

    async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            int seconds = NextInterval();
            plannedInterval = seconds;
            log.Write("interval", seconds + "s" + (locked ? " (locked)" : ""));
            NextPoll = DateTimeOffset.UtcNow.AddSeconds(seconds);
            Changed?.Invoke();
            try
            {
                await wake.WaitAsync(TimeSpan.FromSeconds(seconds), stop.Token);
                while (wake.Wait(0))
                {
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    static int Backoff(int failures, int interval) => (int)Math.Min(900, Math.Max(30, interval / 2) * Math.Pow(2, Math.Min(4, failures - 1)));

    public async Task PollOnceAsync(CancellationToken ct)
    {
        await pollGate.WaitAsync(ct);
        Busy = true;
        lastPollMs = clock.ElapsedMilliseconds;
        Changed?.Invoke();
        try
        {
            var previous = Current;
            var snapshot = await source.ReadAsync(Settings.Salt, ct);
            var now = DateTimeOffset.UtcNow;

            // A row joins the previous observation of the same account and window only when that one
            // is recent; otherwise the time in between was not watched and is marked as a gap.
            var latest = RecentSamples()
                .Where(s => s.Account == snapshot.AccountKey)
                .GroupBy(s => s.Window)
                .ToDictionary(g => g.Key, g => g.Last().At);
            double maxStep = Math.Max(60, plannedInterval) * 3;
            var rows = UsageSample.From(snapshot, false, plannedInterval)
                .Select(r => r with
                {
                    Gap = forceGap || !latest.TryGetValue(r.Window, out var at) || (r.At - at).TotalSeconds > maxStep,
                })
                .ToList();
            History.Append(rows);
            lock (recentGate)
                recent = recent.Concat(rows).Where(r => r.At >= now.AddDays(-8)).ToList();

            bool moved = previous == null
                || previous.AccountKey != snapshot.AccountKey
                || snapshot.Windows.Any(w => previous.Find(w.Id) is not { } p
                    || p.UsedPercent != w.UsedPercent
                    || !ForecastCalculator.SameReset(p.ResetsAt, w.ResetsAt));
            idlePolls = moved ? 0 : idlePolls + 1;

            Current = snapshot;
            Forecasts = BuildForecasts(snapshot, now);
            forceGap = false;
            Fresh = true;
            Failures = 0;
            ErrorCode = "";
            Status = "已更新";
            log.Write("poll", "OK");

            TrySave(Path.Combine(Root, "last_good.json"), snapshot);
            TrySave(Path.Combine(Root, "status.json"), StatusReport.Create(snapshot, Forecasts, true, "", now));
            if (Settings.Alerts)
            {
                var messages = AlertRules.Evaluate(previous, snapshot, Forecasts, Settings, alertState, now);
                TrySave(Path.Combine(Root, "alerts.json"), alertState);
                if (messages.Count > 0)
                    Alert?.Invoke(messages);
            }
            if (DateTime.UtcNow - lastCleanup > TimeSpan.FromHours(12))
            {
                lastCleanup = DateTime.UtcNow;
                History.Cleanup(Settings.RetentionDays);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Fresh = false;
            Failures++;
            ErrorCode = e is MonitorException me ? me.Code : e is IOException ? "IO_ERROR" : "INTERNAL_ERROR";
            Status = Explain(ErrorCode);
            log.Write("poll", ErrorCode);
            TrySave(Path.Combine(Root, "status.json"), StatusReport.Create(Current, Forecasts, false, ErrorCode, DateTimeOffset.UtcNow));
        }
        finally
        {
            Busy = false;
            pollGate.Release();
            Changed?.Invoke();
        }
    }

    Dictionary<string, Forecast> BuildForecasts(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var samples = RecentSamples().Where(s => s.Account == snapshot.AccountKey).ToLookup(s => s.Window);
        return snapshot.Windows.ToDictionary(
            w => w.Id,
            w => ForecastCalculator.Calculate(samples[w.Id].ToList(), w, now));
    }

    void TrySave<T>(string path, T value)
    {
        try
        {
            AtomicJson.Save(path, value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Write("save:" + Path.GetFileName(path), "WRITE_FAILED");
        }
    }

    public static string Explain(string code) => code switch
    {
        "CLAUDE_NOT_FOUND" => "找不到 Claude Code。請先安裝官方 Claude Code，或在設定指定 claude.exe 位置。",
        "SIGNATURE_INVALID" => "找到的 claude.exe 沒有 Anthropic 有效簽章，已拒絕執行。",
        "START_FAILED" => "無法啟動 Claude Code，可能被防毒或公司政策擋下。",
        "TIMEOUT" => "Claude Code 沒有回應（逾時），稍後自動重試。",
        "EXITED_EARLY" => "Claude Code 啟動後就結束了，可能需要更新或重新登入。",
        "NOT_SUPPORTED" => "這個版本的 Claude Code 不支援用量查詢，請更新 Claude Code。",
        "AUTH_REQUIRED" => "Claude Code 尚未登入。請開啟 Claude Code 執行 /login。",
        "NO_PLAN_LIMITS" => "目前的登入方式沒有方案額度（API 金鑰或第三方平台）。本工具只支援 Pro／Max／Team 訂閱。",
        "USAGE_UNAVAILABLE" => "暫時取不到用量資料（網路或伺服器），稍後自動重試。",
        "PROTOCOL_ERROR" => "Claude Code 回應無法辨識，稍後自動重試。",
        _ => "取得額度時發生錯誤，稍後自動重試。",
    };

    public void Dispose()
    {
        Workflows.Dispose();
        activity?.Dispose();
        stop.Cancel();
        try
        {
            loop?.Wait(2000);
        }
        catch (AggregateException)
        {
        }
        stop.Dispose();
    }
}

/// <summary>status.json: a small machine-readable copy of the overlay for scripts or a status line.</summary>
internal static class StatusReport
{
    public static object Create(UsageSnapshot? snapshot, IReadOnlyDictionary<string, Forecast> forecasts, bool ok, string error, DateTimeOffset now) => new
    {
        schema = 1,
        ok,
        error_code = error,
        generated_at = now,
        observed_at = snapshot?.ObservedAt,
        plan = snapshot?.PlanLabel,
        windows = snapshot?.Windows.Select(w =>
        {
            var f = forecasts.GetValueOrDefault(w.Id) ?? Forecast.Empty;
            return new
            {
                id = w.Id,
                label = w.Label,
                used_percent = w.UsedPercent,
                remaining_percent = w.RemainingPercent,
                resets_at = w.ResetsAt,
                rate_per_hour = f.RatePerHour,
                exhaust_at = f.ExhaustAt,
                exhausts_before_reset = f.ExhaustsBeforeReset,
            };
        }),
    };
}
