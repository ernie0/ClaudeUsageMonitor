using System.Diagnostics;
using System.Drawing.Imaging;

namespace ClaudeUsageMonitor;

internal static class Program
{
    /// <summary>
    /// ClaudeUsageMonitor.exe                 normal start (tray + overlay)
    ///   --demo                               synthetic data in a temporary folder
    ///   --probe &lt;file.json&gt;              one real query, result written to the file, then exit
    ///     [--claude &lt;claude.exe&gt;]        use exactly this executable (still signature-checked)
    ///   --screenshot &lt;folder&gt;            render every window with demo data to PNG, then exit
    /// The data folder can be moved with the CLAUDE_USAGE_MONITOR_HOME environment variable.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        bool demo = args.Contains("--demo") || args.Contains("--screenshot");

        if (args.Contains("--probe-tokens"))
            return AppPaths.ProbeTokens(Arg("--probe-tokens") ?? Path.Combine(AppPaths.DataRoot(false), "tokens.json"), int.TryParse(Arg("--days"), out int d) ? d : 7);
        if (args.Contains("--probe"))
            return AppPaths.Probe(Arg("--probe") ?? Path.Combine(AppPaths.DataRoot(false), "probe.json"), Arg("--claude"));

        ApplicationConfiguration.Initialize();

        if (args.Contains("--screenshot"))
            return Screenshots(Arg("--screenshot") ?? Path.Combine(Path.GetTempPath(), "ClaudeUsageMonitor-screens"), args.Contains("--workflow-live"));

        string instance = demo ? "ClaudeUsageMonitor-demo" : "ClaudeUsageMonitor";
        using var mutex = new Mutex(true, @"Local\" + instance, out bool first);
        using var reveal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + instance + ".reveal");
        if (!first)
        {
            reveal.Set();
            return 0;
        }

        string root = AppPaths.DataRoot(demo);
        MonitorService? service = null;
        IUsageSource source = demo
            ? AppPaths.NewDemo(root)
            : new ClaudeUsageClient(Path.Combine(root, "neutral"), () => service?.Settings.ClaudePath);
        service = new MonitorService(root, source, demo);
        using (service)
            return Run(service, reveal);
    }

    static int Run(MonitorService service, EventWaitHandle reveal)
    {
        var overlay = new OverlayForm(service);
        var listener = ThreadPool.RegisterWaitForSingleObject(reveal, (_, _) =>
        {
            if (overlay.IsHandleCreated && !overlay.IsDisposed)
                overlay.BeginInvoke(overlay.RevealFromSecondInstance);
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        Application.Run(overlay);
        listener.Unregister(null);
        return 0;
    }

    static int Screenshots(string folder, bool live)
    {
        Directory.CreateDirectory(folder);
        screenshotFolder = folder;
        string root = AppPaths.DataRoot(true);
        var demo = AppPaths.NewDemo(root);
        using var service = new MonitorService(root, demo, demo: true);
        service.PollOnceAsync(CancellationToken.None).GetAwaiter().GetResult();

        using (var overlay = new OverlayForm(service))
        {
            overlay.Show();
            foreach (bool compact in new[] { false, true })
            {
                overlay.ShowMode(compact);
                overlay.Location = new Point(40, 40);
                Grab(overlay, Path.Combine(folder, compact ? "overlay-compact.png" : "overlay-standard.png"));
            }
            overlay.ShowMode(false);
            overlay.ReplayIntro();
            Grab(overlay, Path.Combine(folder, "overlay-anim-1.png"), settleMs: 120);
            Grab(overlay, Path.Combine(folder, "overlay-anim-2.png"), settleMs: 180);
            Grab(overlay, Path.Combine(folder, "overlay-settled.png"), settleMs: 1200);
            foreach (var (id, name) in new[] { (WindowIds.FiveHour, "session"), (WindowIds.SevenDay, "weekly"), ("model:fable", "model") })
            {
                overlay.SimulateHover(id);
                Grab(overlay, Path.Combine(folder, $"overlay-hover-{name}-1.png"), settleMs: 450);
                Grab(overlay, Path.Combine(folder, $"overlay-hover-{name}-2.png"), settleMs: 400);
            }
            // Frame cost with the heaviest light (three comets) on: must stay well under 16 ms for 60 fps.
            using (var frame = new Bitmap(overlay.Width, overlay.Height))
            {
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 120; i++)
                    overlay.DrawToBitmap(frame, new Rectangle(Point.Empty, overlay.Size));
                File.WriteAllText(Path.Combine(folder, "paint-timing.txt"), $"hover frame: {timer.Elapsed.TotalMilliseconds / 120:0.00} ms\n");
            }
            overlay.SimulateHover(null);
            Grab(overlay, Path.Combine(folder, "overlay-unhover-fading.png"), settleMs: 90);
            Grab(overlay, Path.Combine(folder, "overlay-unhover.png"), settleMs: 700);
            overlay.PlayDemoEffects();
            Grab(overlay, Path.Combine(folder, "overlay-effects.png"), settleMs: 380);
        }
        foreach (var (name, value, remaining) in new[] { ("full", "100", 100.0), ("ok", "40", 40.0), ("warn", "13", 13.0), ("critical", "3", 3.0), ("unknown", "?", -1.0) })
        {
            var level = remaining < 0 ? Level.Unknown : Levels.Of(remaining, service.Settings);
            using var icon = OverlayForm.RenderTrayBitmap(value, level, 32);
            icon.Save(Path.Combine(folder, $"tray-{name}.png"), ImageFormat.Png);
        }
        using (var history = new HistoryForm(service) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            history.Show();
            Application.DoEvents();
            Frames(history, history.View, "history", [300, 700, 1200]);
            Hover(history, history.View, "see:stat:1", Path.Combine(folder, "history-hover-stat.png"));
            Hover(history, history.View, null, null);
            Grab(history, Path.Combine(folder, "history.png"));
            // The crosshair follows the pointer across the curve without leaving a trail.
            history.View.RecordScreen();
            foreach (float x in new[] { 300f, 420f, 560f })
            {
                history.View.PointerAt(new PointF(x, 520));
                for (int i = 0; i < 12; i++)
                {
                    Pump(15);
                    history.View.PaintRecorded();
                }
            }
            using (var picture = history.View.StopRecording()!)
                picture.Clone(new Rectangle(0, 0, picture.Width, Math.Min(picture.Height, history.ClientSize.Height)), picture.PixelFormat).Save(Path.Combine(folder, "history-pointer-live.png"), ImageFormat.Png);
            history.View.PointerAt(null);
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            Pump(2000);
            var idle = Process.GetCurrentProcess().TotalProcessorTime - cpu;
            File.AppendAllText(Path.Combine(folder, "paint-timing.txt"),
                $"history idle 2 s: {idle.TotalMilliseconds:0} ms CPU, animating: {history.View.Motion.Active} {history.View.Motion.Dirty}\nhistory frame: {FrameCost(history.View):0.0} ms\n");
            history.Close();
        }
        using (var details = new DetailsForm(service) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) })
        {
            details.Show();
            Frames(details, details.View, "details", [150, 400, 700, 1100]);
            Hover(details, details.View, "card:" + WindowIds.FiveHour, Path.Combine(folder, "details-hover-card.png"));
            Hover(details, details.View, "row:" + WindowIds.SevenDay, Path.Combine(folder, "details-hover-row.png"));
            Hover(details, details.View, null, null);
            Grab(details, Path.Combine(folder, "details.png"));
            File.AppendAllText(Path.Combine(folder, "paint-timing.txt"), $"details frame: {FrameCost(details.View):0.0} ms\n");
            // At rest the page must not redraw: CPU used while it sits open for 2 seconds.
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            Pump(2000);
            File.AppendAllText(Path.Combine(folder, "paint-timing.txt"),
                $"details idle 2 s: {(Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds:0} ms CPU, animating: {details.View.Motion.Active} {details.View.Motion.Dirty}\n");
            details.Close();
        }
        var analysis = new AnalysisForm(service) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        analysis.Show();
        analysis.PrepareAsync().GetAwaiter().GetResult();
        Frames(analysis, analysis.View, "analysis", [200, 600, 1000, 1500]);
        Hover(analysis, analysis.View, "see:day:3", Path.Combine(folder, "analysis-hover-day.png"));
        Hover(analysis, analysis.View, "see:heat:2:14", Path.Combine(folder, "analysis-hover-heat.png"));
        Hover(analysis, analysis.View, null, null);
        // With the real clock, frames repaint only what moved, as on screen: the tooltip must still be complete.
        Live(analysis, analysis.View, "see:heat:0:22", Path.Combine(folder, "analysis-hover-live.png"));
        Live(analysis, analysis.View, null, Path.Combine(folder, "analysis-unhover-live.png"));
        Grab(analysis, Path.Combine(folder, "analysis.png"));
        analysis.SaveFullPage(Path.Combine(folder, "analysis-full.png"));
        File.AppendAllText(Path.Combine(folder, "paint-timing.txt"), $"analysis frame: {FrameCost(analysis.View):0.0} ms\n");
        // Sections below the window wait until they scroll into view, then enter.
        var motion = analysis.View.Motion;
        motion.Time = 50_000;
        analysis.View.PlayIntro();
        Grab(analysis, Path.Combine(folder, "analysis-scroll-0.png"), settleMs: 150);
        analysis.ScrollTo(560);
        Grab(analysis, Path.Combine(folder, "analysis-scroll-1.png"), settleMs: 150);
        motion.Time = 50_450;
        analysis.View.Invalidate();
        Grab(analysis, Path.Combine(folder, "analysis-scroll-2.png"), settleMs: 150);
        motion.Time = null;
        analysis.Close();
        Capture(new SettingsForm(service), Path.Combine(folder, "settings.png"));
        Workflow(service, folder, live);
        return 0;
    }

    /// <summary>
    /// The small workflow window ready to send, and the full view. With --workflow-live it also runs one real
    /// conversation (small model, no user settings, no saved history) in a scratch folder: allow a write,
    /// reply, deny an edit, close.
    /// </summary>
    static void Workflow(MonitorService service, string folder, bool live)
    {
        string work = Path.Combine(folder, "workflow-work");
        Directory.CreateDirectory(work);
        var mini = WorkflowMiniForm.Open(service);
        mini.Location = new Point(60, 60);
        mini.Prepare(work, "在這個資料夾建立 hello.txt，內容寫「你好」，完成後用一句話回報。");
        Grab(mini, Path.Combine(folder, "workflow-mini.png"));
        ClaudeSession? session = null;
        if (live)
        {
            ClaudeSession.ExtraArguments = ["--setting-sources", "", "--strict-mcp-config", "--no-session-persistence", "--model", "haiku"];
            session = mini.Submit()!;
            string log = Path.Combine(folder, "workflow-live.txt");
            bool Wait(Func<bool> done, string step)
            {
                var clock = Stopwatch.StartNew();
                while (!done() && clock.Elapsed < TimeSpan.FromSeconds(120))
                    Pump(100);
                File.AppendAllText(log, $"{step}: {(done() ? "ok" : "timeout")} after {clock.Elapsed.TotalSeconds:0.0} s, state {session.State}\n");
                return done();
            }
            Grab(mini, Path.Combine(folder, "workflow-mini-working.png"), settleMs: 400);
            if (Wait(() => session.State == JobState.NeedsPermission, "ask to write"))
            {
                Grab(mini, Path.Combine(folder, "workflow-mini-ask.png"), settleMs: 300);
                mini.Act("allow:0");
            }
            Wait(() => session.State == JobState.Waiting, "reply after allow");
            File.AppendAllText(log, $"hello.txt: {(File.Exists(Path.Combine(work, "hello.txt")) ? File.ReadAllText(Path.Combine(work, "hello.txt")) : "missing")}\n");
            Grab(mini, Path.Combine(folder, "workflow-mini-done.png"), settleMs: 300);
            mini.Act("reply:0");
            mini.Prepare(work, "把 hello.txt 的內容改成「哈囉」。");
            Grab(mini, Path.Combine(folder, "workflow-mini-reply.png"), settleMs: 200);
            mini.Submit();
            // Claude may try another way after a refusal: refuse every request until it replies.
            int denied = 0;
            while (Wait(() => session.State is JobState.NeedsPermission or JobState.Waiting, "ask or reply") && session.State == JobState.NeedsPermission)
            {
                mini.Act("deny:0");
                denied++;
                Pump(200);
            }
            File.AppendAllText(log, $"denied {denied} request(s), state {session.State}\n");
            File.AppendAllText(log, $"hello.txt after deny: {File.ReadAllText(Path.Combine(work, "hello.txt"))}\n");
            Grab(mini, Path.Combine(folder, "workflow-mini-deny.png"), settleMs: 300);
        }
        // Conversations open in VS Code or a terminal (real ones on this computer), and aiming at one.
        mini.RefreshLive();
        Pump(2500);
        Grab(mini, Path.Combine(folder, "workflow-mini-live.png"), settleMs: 300);
        mini.Act("resume:0");
        Grab(mini, Path.Combine(folder, "workflow-mini-resume.png"), settleMs: 300);
        mini.Aim(null);
        // With CLAUDE_USAGE_MONITOR_TEST_RESUME="<session id>|<folder>" (a small saved test conversation), continue a copy of it.
        if (live && Environment.GetEnvironmentVariable("CLAUDE_USAGE_MONITOR_TEST_RESUME")?.Split('|') is [var id, var where])
        {
            string log = Path.Combine(folder, "workflow-live.txt");
            mini.Aim(new LiveSession(id, "測試對話", where, false, DateTimeOffset.UtcNow, "測試"));
            mini.Prepare(work, "我剛剛說我在整理的專案叫什麼名字？只回答名稱。");
            var copy = mini.Submit()!;
            var clock = Stopwatch.StartNew();
            while (copy.State != JobState.Waiting && clock.Elapsed < TimeSpan.FromSeconds(120))
                Pump(100);
            var (lines, _) = copy.Snapshot();
            File.AppendAllText(log, $"resume copy: state {copy.State} after {clock.Elapsed.TotalSeconds:0.0} s, folder {copy.Folder == where}, reply: {lines.LastOrDefault(l => l.Kind == JobLineKind.Claude)?.Text}\n");
            Grab(mini, Path.Combine(folder, "workflow-mini-resumed.png"), settleMs: 300);
            service.Workflows.Close(copy);
            Pump(300);
        }
        var full = WorkflowForm.Open(service);
        full.StartPosition = FormStartPosition.Manual;
        full.Location = new Point(520, 40);
        Grab(full, Path.Combine(folder, live ? "workflow-detail.png" : "workflow.png"));
        full.Dispose();
        if (session != null)
        {
            mini.Act("close:0");
            Pump(400);
            Grab(mini, Path.Combine(folder, "workflow-mini-closed.png"), settleMs: 200);
        }
        mini.Dispose();
    }

    static string screenshotFolder = "";
    static double hoverClock = 20_000;

    /// <summary>The page entrance at fixed moments (ms), then its end state.</summary>
    static void Frames(Form form, GlassView view, string name, int[] moments)
    {
        view.Motion.Time = 0;
        view.PlayIntro();
        Grab(form, Path.Combine(screenshotFolder, $"{name}-anim-0.png"), settleMs: 150);
        foreach (int ms in moments)
        {
            view.Motion.Time = ms;
            view.Invalidate();
            Grab(form, Path.Combine(screenshotFolder, $"{name}-anim-{ms}.png"), settleMs: 150);
        }
        view.Motion.Time = null;
        view.Settle();
    }

    /// <summary>Hovers an area and captures the light sweeping across it; with no path it only moves the pointer.</summary>
    static void Hover(Form form, GlassView view, string? action, string? path)
    {
        hoverClock += 10_000;
        view.Motion.Time = hoverClock;
        view.PointAt(action);
        Pump(150);
        if (path != null)
        {
            view.Motion.Time = hoverClock + 380;
            view.Invalidate();
            Grab(form, path, settleMs: 150);
        }
        view.Motion.Time = null;
    }

    /// <summary>
    /// Points at an area with the real clock and records what the screen would show after 0.8 s, painting
    /// whole once and then only what the view invalidates (PrintWindow would repaint everything).
    /// </summary>
    static void Live(Form form, GlassView view, string? action, string path)
    {
        view.RecordScreen();
        view.PointAt(action);
        for (int i = 0; i < 55; i++)
        {
            Pump(15);
            view.PaintRecorded();
        }
        using var picture = view.StopRecording()!;
        int visible = Math.Min(picture.Height, form.ClientSize.Height);
        using var crop = picture.Clone(new Rectangle(0, -view.Top, picture.Width, Math.Min(visible, picture.Height + view.Top)), picture.PixelFormat);
        crop.Save(path, ImageFormat.Png);
    }

    /// <summary>Average time to paint a control once (DrawToBitmap, 20 frames).</summary>
    static double FrameCost(Control control)
    {
        using var bitmap = new Bitmap(control.Width, Math.Min(control.Height, 1000));
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
            control.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        return timer.Elapsed.TotalMilliseconds / 20;
    }

    static void Capture(Form form, string path)
    {
        using (form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(40, 40);
            form.Show();
            Grab(form, path);
            form.Close();
        }
    }

    /// <summary>
    /// PrintWindow with PW_RENDERFULLCONTENT copies only this window's own pixels (DWM title bar and
    /// themed controls included), even when other windows cover it; the rest of the screen is never read.
    /// </summary>
    static void Grab(Form form, string path, int settleMs = 480)
    {
        Pump(settleMs);
        using var bitmap = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                PrintWindow(form.Handle, hdc, 2);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>Lets pending paints and timers run for a while.</summary>
    static void Pump(int ms)
    {
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
