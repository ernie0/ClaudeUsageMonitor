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
            return Screenshots(Arg("--screenshot") ?? Path.Combine(Path.GetTempPath(), "ClaudeUsageMonitor-screens"));

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

    static int Screenshots(string folder)
    {
        Directory.CreateDirectory(folder);
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
        Capture(new HistoryForm(service), Path.Combine(folder, "history.png"));
        Capture(new DetailsForm(service), Path.Combine(folder, "details.png"));
        var analysis = new AnalysisForm(service) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        analysis.Show();
        analysis.PrepareAsync().GetAwaiter().GetResult();
        Grab(analysis, Path.Combine(folder, "analysis.png"));
        analysis.SaveFullPage(Path.Combine(folder, "analysis-full.png"));
        analysis.Close();
        Capture(new SettingsForm(service), Path.Combine(folder, "settings.png"));
        return 0;
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
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < settleMs)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
