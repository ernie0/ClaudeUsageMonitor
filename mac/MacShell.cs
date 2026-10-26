using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace ClaudeUsageMonitor;

internal static class Program
{
    /// <summary>
    /// ClaudeUsageMonitor                       normal start (menu-bar icon + floating card)
    ///   --demo                                 synthetic data in a temporary folder
    ///   --probe &lt;file.json&gt; [--claude &lt;path&gt;]   one real query written to the file, then exit
    ///   --screenshot &lt;folder&gt;                  render every window with demo data to PNG, then exit
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (args.Contains("--probe-tokens"))
            return AppPaths.ProbeTokens(Arg("--probe-tokens") ?? Path.Combine(AppPaths.DataRoot(false), "tokens.json"), int.TryParse(Arg("--days"), out int d) ? d : 7);
        if (args.Contains("--probe"))
            return AppPaths.Probe(Arg("--probe") ?? Path.Combine(AppPaths.DataRoot(false), "probe.json"), Arg("--claude"));

        return AppBuilder.Configure(() => new App(args))
            .UsePlatformDetect()
            // A menu-bar app: no Dock icon, no app menu.
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }
}

internal sealed class App(string[] args) : Application
{
    MonitorService? service;
    MacShell? shell;
    static FileStream? instanceLock;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;

        // Pill buttons like the Windows app; the active/primary one ("accent") carries the brand gradient.
        Styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters = { new Setter(Button.CornerRadiusProperty, new CornerRadius(14)), new Setter(Button.PaddingProperty, new Thickness(14, 5)) },
        });
        foreach (string state in new[] { "", ":pointerover", ":pressed" })
        {
            Styles.Add(new Style(x => (state.Length == 0 ? x.OfType<Button>().Class("accent") : x.OfType<Button>().Class("accent").Class(state))
                .Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty,
                        state == ":pointerover" ? MacTheme.Horizontal(MacTheme.Mix(MacTheme.Coral, Colors.White, 0.15), MacTheme.Mix(MacTheme.Violet, Colors.White, 0.15))
                        : MacTheme.Horizontal(MacTheme.Coral, MacTheme.Violet)),
                    new Setter(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, Brushes.White),
                },
            });
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool screenshot = args.Contains("--screenshot");
            bool demo = args.Contains("--demo") || screenshot;
            string root = AppPaths.DataRoot(demo);
            if (!demo && !AcquireInstance(root))
            {
                desktop.Shutdown();
                return;
            }

            IUsageSource source = demo
                ? AppPaths.NewDemo(root)
                : new ClaudeUsageClient(Path.Combine(root, "neutral"), () => service?.Settings.ClaudePath);
            service = new MonitorService(root, source, demo);
            shell = new MacShell(service, desktop);
            desktop.Exit += (_, _) =>
            {
                shell.Dispose();
                service.Dispose();
            };

            if (screenshot)
            {
                int index = Array.IndexOf(args, "--screenshot");
                string folder = index + 1 < args.Length ? args[index + 1] : Path.Combine(Path.GetTempPath(), "ClaudeUsageMonitor-mac-screens");
                Dispatcher.UIThread.Post(async () =>
                {
                    await MacScreenshots.RunAsync(service, shell, folder);
                    desktop.Shutdown();
                });
            }
            else
            {
                shell.Start();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Second copies exit at once; the lock file is released when the process ends.</summary>
    static bool AcquireInstance(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            instanceLock = new FileStream(Path.Combine(root, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

/// <summary>Menu-bar icon, menus, secondary windows, notifications and auto-start.</summary>
internal sealed class MacShell : IDisposable
{
    const string UsagePage = "https://claude.ai/settings/usage";

    readonly MonitorService service;
    readonly IClassicDesktopStyleApplicationLifetime desktop;
    readonly TrayIcon tray = new() { ToolTipText = "Claude 額度" };
    HistoryWindow? history;
    DetailsWindow? details;
    SettingsWindow? settings;
    AnalysisWindow? analysis;

    public MacShell(MonitorService service, IClassicDesktopStyleApplicationLifetime desktop)
    {
        this.service = service;
        this.desktop = desktop;
        Overlay = new OverlayWindow(service, this);
        tray.Menu = BuildNativeMenu();
        tray.Clicked += (_, _) => Overlay.ToggleVisible();
        if (Application.Current is { } app)
            TrayIcon.SetIcons(app, new TrayIcons { tray });
        service.Changed += () => Dispatcher.UIThread.Post(UpdateTray);
        service.Alert += messages => Notify(messages);
        UpdateTray();
    }

    public OverlayWindow Overlay { get; }

    public void Start()
    {
        if (!service.Settings.StartHidden)
        {
            Overlay.Show();
            Overlay.View.FadeIn();
        }
        service.Start();
    }

    // ---- menus ----------------------------------------------------------------------------------

    IEnumerable<(string Text, Action Action)> MenuItems() =>
    [
        ("顯示／隱藏浮窗", () => Overlay.ToggleVisible()),
        ("立即更新", () =>
        {
            if (!service.Refresh(manual: true))
                MacDialog.Show(null, "請稍候", "10 秒內只能手動更新一次。");
        }),
        ("切換標準／精簡模式", () => Overlay.View.ToggleMode()),
        ("-", () => { }),
        ("使用紀錄…", () => OpenHistory()),
        ("額度詳情…", OpenDetails),
        ("用量分析…", OpenAnalysis),
        ("設定…", OpenSettings),
        ("開啟官方用量頁", () => Open(UsagePage)),
        ("開啟資料夾", () => Open(service.Root)),
        ("-", () => { }),
        ("關於", ShowAbout),
        ("離開", () => desktop.Shutdown()),
    ];

    public ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        foreach (var (text, action) in MenuItems())
        {
            if (text == "-")
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        return menu;
    }

    NativeMenu BuildNativeMenu()
    {
        var menu = new NativeMenu();
        foreach (var (text, action) in MenuItems())
        {
            if (text == "-")
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }
            var item = new NativeMenuItem(text);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        return menu;
    }

    public void OpenHistory(string? windowId = null)
    {
        Bring(ref history, () => new HistoryWindow(service));
        if (windowId != null)
            history!.Select(windowId);
    }

    public void OpenDetails() => Bring(ref details, () => new DetailsWindow(service, this));

    public void OpenSettings() => Bring(ref settings, () => new SettingsWindow(service, Overlay));

    public void OpenAnalysis() => Bring(ref analysis, () => new AnalysisWindow(service));

    static void Bring<T>(ref T? window, Func<T> create) where T : Window
    {
        if (window is { IsVisible: true })
        {
            window.Activate();
            return;
        }
        window = create();
        window.Show();
        window.Activate();
    }

    void ShowAbout()
    {
        string version = typeof(MacShell).Assembly.GetName().Version?.ToString(3) ?? "";
        MacDialog.Show(null, "關於 ClaudeUsageMonitor",
            $"ClaudeUsageMonitor {version}（Mac 版）\n\n" +
            "非官方工具，不隸屬 Anthropic，也未受其背書。\n" +
            "額度由電腦上已登入的官方 Claude Code 查詢（不送出任何提問、不耗用額度），紀錄只存在本機：\n" + service.Root + "\n\n" +
            "設計參考 hayabusasean/CodexUsageMonitor（MIT 授權）。");
    }

    // ---- menu-bar icon --------------------------------------------------------------------------

    void UpdateTray()
    {
        var snapshot = service.Current;
        var window = snapshot?.Tightest;
        string value = window == null ? "?" : Math.Floor(window.RemainingPercent).ToString("0");
        var level = window == null || !service.Fresh ? Level.Unknown : Levels.Of(window.RemainingPercent, service.Settings);
        tray.Icon = new WindowIcon(RenderTrayBitmap(value, level));
        tray.ToolTipText = snapshot == null
            ? "Claude 額度 · " + service.Status
            : $"Claude 額度｜5 小時 {Pct(snapshot.Session)}｜本週 {Pct(snapshot.Weekly)}" + (service.Fresh ? "" : "｜上次資料");
    }

    static string Pct(QuotaWindow? w) => w == null ? "—" : Format.Percent(w.RemainingPercent);

    /// <summary>18-point tile (rendered at 2× for Retina) with the remaining percentage.</summary>
    public static Bitmap RenderTrayBitmap(string value, Level level)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(192, 192));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var r = new Rect(0, 0, 18, 18);
            var (start, end) = MacTheme.Gradient(level);
            ctx.DrawRectangle(MacTheme.Diagonal(start, end), null, r, 4.3, 4.3);
            double size = value.Length >= 3 ? 7.5 : 10.5;
            var text = new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MacTheme.Number(FontWeight.Bold), size, Brushes.White);
            ctx.DrawText(text, new Point((18 - text.Width) / 2, (18 - text.Height) / 2));
        }
        return bitmap;
    }

    // ---- platform services ----------------------------------------------------------------------

    /// <summary>Several alerts from one poll become one notification.</summary>
    void Notify(IReadOnlyList<AlertMessage> messages)
    {
        string title = messages.Count == 1 ? messages[0].Title : $"Claude 額度提醒（{messages.Count} 項）";
        string body = messages.Count == 1
            ? messages[0].Body
            : string.Join("\n", messages.Select(m => m.Title.Replace("Claude ", "") + "：" + m.Body));
        if (!OperatingSystem.IsMacOS())
            return;
        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        Run("/usr/bin/osascript", ["-e", $"display notification {Quote(body)} with title {Quote(title)}"]);
    }

    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    static string Run(string exe, string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process == null)
                return "";
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Trim();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>Follows macOS "Reduce motion" (Accessibility → Display).</summary>
    public static bool AnimationsEnabled { get; } =
        !OperatingSystem.IsMacOS() || Run("/usr/bin/defaults", ["read", "com.apple.universalaccess", "reduceMotion"]) != "1";

    public static WindowIcon? AppIcon()
    {
        using var stream = typeof(MacShell).Assembly.GetManifestResourceStream("ClaudeUsageMonitor.app.ico");
        return stream == null ? null : new WindowIcon(stream);
    }

    public void Dispose()
    {
        tray.IsVisible = false;
        tray.Dispose();
    }
}

/// <summary>Start at login through a per-user LaunchAgent (macOS); not offered elsewhere.</summary>
internal static class Autostart
{
    const string Label = "com.ernie0.claudeusagemonitor";

    static string PlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public static bool Supported => OperatingSystem.IsMacOS();

    public static bool Enabled
    {
        get => Supported && File.Exists(PlistPath);
        set
        {
            if (!Supported)
                return;
            if (!value)
            {
                File.Delete(PlistPath);
                return;
            }
            // Inside a bundle, launch the .app (so macOS treats it as the app); otherwise the program itself.
            string program = Environment.ProcessPath ?? "";
            string? bundle = program.Contains(".app/Contents/MacOS/") ? program[..(program.IndexOf(".app/Contents/MacOS/") + 4)] : null;
            string arguments = bundle != null
                ? $"<string>/usr/bin/open</string><string>-a</string><string>{Escape(bundle)}</string>"
                : $"<string>{Escape(program)}</string>";
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            File.WriteAllText(PlistPath,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\"><dict>\n" +
                $"  <key>Label</key><string>{Label}</string>\n" +
                $"  <key>ProgramArguments</key><array>{arguments}</array>\n" +
                "  <key>RunAtLoad</key><true/>\n" +
                "</dict></plist>\n");
        }
    }

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>A minimal message box (Avalonia has none built in).</summary>
internal static class MacDialog
{
    public static void Show(Window? owner, string title, string message)
    {
        var ok = new Button { Content = "好", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, MinWidth = 80, Classes = { "accent" } };
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = MacTheme.Solid(MacTheme.Canvas),
            Icon = MacShell.AppIcon(),
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontFamily = MacTheme.UiFont, FontSize = MacDraw.Pt(10), Foreground = MacTheme.Solid(MacTheme.Text) },
                    ok,
                },
            },
        };
        ok.Click += (_, _) => window.Close();
        if (owner != null)
            window.ShowDialog(owner);
        else
            window.Show();
    }
}
