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
    public static async Task RunAsync(MonitorService service, MacShell shell, string folder)
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
        await Task.Delay(1100);
        details.Cards.Settle();
        await Task.Delay(200);
        Save(details, Path.Combine(folder, "details.png"));
        details.Close();

        var settings = new SettingsWindow(service, overlay);
        settings.Show();
        await Task.Delay(600);
        Save(settings, Path.Combine(folder, "settings.png"));
        settings.Close();
        overlay.Hide();
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
