using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace ClaudeUsageMonitor;

/// <summary>
/// --screenshot: renders every window with demo data to PNG (Avalonia's own renderer, so it works the
/// same on macOS and on a Windows test machine) and captures a few animation frames.
/// </summary>
internal static class MacScreenshots
{
    public static async Task RunAsync(MonitorService service, MacShell shell, string folder, bool live = false)
    {
        Directory.CreateDirectory(folder);
        await service.PollOnceAsync(CancellationToken.None);
        var overlay = shell.Overlay;
        var view = overlay.View;
        view.SimulateHover(null);
        overlay.Show();
        await Task.Delay(500);

        foreach (bool compact in new[] { false, true })
        {
            if (service.Settings.Compact != compact)
                view.ToggleMode();
            view.Settle();
            await Task.Delay(400);
            Save(overlay, Path.Combine(folder, compact ? "overlay-compact.png" : "overlay-standard.png"));
        }
        view.ToggleMode();
        view.Settle();
        await Task.Delay(300);

        view.ReplayIntro();
        await Task.Delay(140);
        Save(overlay, Path.Combine(folder, "overlay-anim.png"));
        view.Settle();
        await Task.Delay(900);

        foreach (var (id, name) in new[] { (WindowIds.FiveHour, "session"), (WindowIds.SevenDay, "weekly"), ("model:fable", "model") })
        {
            view.SimulateHover(id);
            await Task.Delay(500);
            Save(overlay, Path.Combine(folder, $"overlay-hover-{name}.png"));
        }
        view.SimulateHover(null);
        await Task.Delay(800);
        view.PlayDemoEffects();
        await Task.Delay(380);
        Save(overlay, Path.Combine(folder, "overlay-effects.png"));

        foreach (var (name, value, remaining) in new[] { ("full", "100", 100.0), ("ok", "40", 40.0), ("warn", "13", 13.0), ("critical", "3", 3.0), ("unknown", "?", -1.0) })
        {
            var level = remaining < 0 ? Level.Unknown : Levels.Of(remaining, service.Settings);
            MacShell.RenderTrayBitmap(value, level).Save(Path.Combine(folder, $"tray-{name}.png"));
        }

        var history = new HistoryWindow(service);
        history.Show();
        await Task.Delay(900);
        history.Settle();
        await Task.Delay(200);
        Save(history, Path.Combine(folder, "history.png"));
        history.Close();

        var details = new DetailsWindow(service, shell);
        details.Show();
        await Task.Delay(300);
        await Frames(details, details.Cards, Path.Combine(folder, "details"), [400, 1100]);
        await Hover(details, details.Cards, "card:" + WindowIds.FiveHour, Path.Combine(folder, "details-hover-card.png"));
        await Hover(details, details.Cards, null, null);
        Save(details, Path.Combine(folder, "details.png"));
        details.Close();

        var analysisWindow = new AnalysisWindow(service);
        analysisWindow.Show();
        await analysisWindow.PrepareAsync();
        await Frames(analysisWindow, analysisWindow.Page, Path.Combine(folder, "analysis"), [600, 1000]);
        await Hover(analysisWindow, analysisWindow.Page, "see:day:3", Path.Combine(folder, "analysis-hover-day.png"));
        await Hover(analysisWindow, analysisWindow.Page, null, null);
        Save(analysisWindow, Path.Combine(folder, "analysis.png"));
        SaveControl(analysisWindow.Page, Path.Combine(folder, "analysis-full.png"));
        // Sections below the window wait until they scroll into view, then enter.
        var motion = analysisWindow.Page.Motion;
        motion.Time = 50_000;
        analysisWindow.Page.PlayIntro();
        await Task.Delay(150);
        analysisWindow.ScrollTo(560);
        await Task.Delay(150);
        motion.Time = 50_450;
        analysisWindow.Page.InvalidateVisual();
        await Task.Delay(150);
        Save(analysisWindow, Path.Combine(folder, "analysis-scroll.png"));
        motion.Time = null;
        analysisWindow.Close();

        await WorkflowAsync(service, shell, folder, live);

        var settings = new SettingsWindow(service, overlay);
        settings.Show();
        await Task.Delay(600);
        Save(settings, Path.Combine(folder, "settings.png"));
        settings.Close();
        overlay.Hide();
    }

    static double hoverClock = 20_000;

    /// <summary>
    /// The small workflow window ready to send, and the full view. With --workflow-live it also runs one
    /// real conversation (small model, no user settings, no saved history) in a scratch folder.
    /// </summary>
    static async Task WorkflowAsync(MonitorService service, MacShell shell, string folder, bool live)
    {
        string work = Path.Combine(folder, "workflow-work");
        Directory.CreateDirectory(work);
        var mini = shell.OpenWorkflow();
        mini.Position = new PixelPoint(60, 60);
        mini.Prepare(work, "在這個資料夾建立 hello.txt，內容寫「你好」，完成後用一句話回報。");
        await Task.Delay(500);
        Save(mini, Path.Combine(folder, "workflow-mini.png"));
        if (live)
        {
            ClaudeSession.ExtraArguments = ["--setting-sources", "", "--strict-mcp-config", "--no-session-persistence", "--model", "haiku"];
            var session = mini.Submit()!;
            string log = Path.Combine(folder, "workflow-live.txt");
            async Task<bool> Wait(Func<bool> done, string step)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!done() && clock.Elapsed < TimeSpan.FromSeconds(120))
                    await Task.Delay(100);
                File.AppendAllText(log, $"{step}: {(done() ? "ok" : "timeout")} after {clock.Elapsed.TotalSeconds:0.0} s, state {session.State}\n");
                return done();
            }
            if (await Wait(() => session.State == JobState.NeedsPermission, "ask to write"))
            {
                await Task.Delay(400);
                Save(mini, Path.Combine(folder, "workflow-mini-ask.png"));
                mini.Act("allow:0");
            }
            await Wait(() => session.State == JobState.Waiting, "reply after allow");
            File.AppendAllText(log, $"hello.txt: {(File.Exists(Path.Combine(work, "hello.txt")) ? File.ReadAllText(Path.Combine(work, "hello.txt")) : "missing")}\n");
            await Task.Delay(400);
            Save(mini, Path.Combine(folder, "workflow-mini-done.png"));
            mini.Act("reply:0");
            mini.Prepare(work, "把 hello.txt 的內容改成「哈囉」。");
            await Task.Delay(300);
            Save(mini, Path.Combine(folder, "workflow-mini-reply.png"));
            mini.Submit();
            // Claude may try another way after a refusal: refuse every request until it replies.
            int denied = 0;
            while (await Wait(() => session.State is JobState.NeedsPermission or JobState.Waiting, "ask or reply") && session.State == JobState.NeedsPermission)
            {
                mini.Act("deny:0");
                denied++;
                await Task.Delay(200);
            }
            File.AppendAllText(log, $"denied {denied} request(s), state {session.State}\n");
            File.AppendAllText(log, $"hello.txt after deny: {File.ReadAllText(Path.Combine(work, "hello.txt"))}\n");
            await Task.Delay(400);
            Save(mini, Path.Combine(folder, "workflow-mini-deny.png"));
        }
        var detail = shell.OpenWorkflowDetail();
        await Task.Delay(600);
        Save(detail, Path.Combine(folder, live ? "workflow-detail.png" : "workflow.png"));
        detail.Hide();
        if (live)
        {
            mini.Act("close:0");
            await Task.Delay(500);
            Save(mini, Path.Combine(folder, "workflow-mini-closed.png"));
        }
        mini.Hide();
    }

    /// <summary>The page entrance at fixed moments (ms), then its end state.</summary>
    static async Task Frames(Window window, GlassControl page, string prefix, int[] moments)
    {
        page.Motion.Time = 0;
        page.PlayIntro();
        await Task.Delay(150);
        foreach (int ms in moments)
        {
            page.Motion.Time = ms;
            page.InvalidateVisual();
            await Task.Delay(150);
            Save(window, $"{prefix}-anim-{ms}.png");
        }
        page.Motion.Time = null;
        page.Settle();
        await Task.Delay(200);
    }

    /// <summary>Hovers an area and captures the light sweeping across it; with no path it only moves the pointer.</summary>
    static async Task Hover(Window window, GlassControl page, string? action, string? path)
    {
        hoverClock += 10_000;
        page.Motion.Time = hoverClock;
        page.PointAt(action);
        await Task.Delay(150);
        if (path != null)
        {
            page.Motion.Time = hoverClock + 380;
            page.InvalidateVisual();
            await Task.Delay(150);
            Save(window, path);
        }
        page.Motion.Time = null;
        await Task.Delay(100);
    }

    static void SaveControl(Control control, string path)
    {
        var size = control.Bounds.Size;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
        bitmap.Render(control);
        bitmap.Save(path);
    }

    static void Save(Window window, string path)
    {
        double scale = window.RenderScaling;
        var size = window.ClientSize;
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(path);
    }
}
