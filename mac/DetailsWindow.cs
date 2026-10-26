using Avalonia.Controls;
using Avalonia.Threading;

namespace ClaudeUsageMonitor;

/// <summary>The details dashboard (layout in Core/DetailsPage.cs), hosted with scrolling, hover and clicks.</summary>
internal sealed class DetailsWindow : Window
{
    readonly DashboardControl dashboard;

    public DetailsWindow(MonitorService service, MacShell shell)
    {
        dashboard = new DashboardControl(service, shell);
        Title = "Claude 額度 · 詳情" + (service.Demo ? "（示範資料）" : "");
        Icon = MacShell.AppIcon();
        Width = 1320;
        Height = 900;
        MinWidth = 1080;
        MinHeight = 620;
        Background = MacTheme.Solid(MacTheme.Canvas);
        Content = new ScrollViewer { Content = dashboard, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Opened += async (_, _) =>
        {
            dashboard.PlayIntro();
            await service.ScanTokensAsync(8);
            dashboard.InvalidateVisual();
        };
    }

    public DashboardControl Cards => dashboard;
}

/// <summary>Draws the dashboard with Avalonia through the shared layout and animation clock.</summary>
internal sealed class DashboardControl : GlassControl
{
    readonly MonitorService service;
    readonly MacShell shell;
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(30) };

    public DashboardControl(MonitorService service, MacShell shell)
    {
        this.service = service;
        this.shell = shell;
        ticker.Tick += (_, _) => InvalidateVisual();
        ticker.Start();
        Action onChanged = () => Dispatcher.UIThread.Post(() =>
        {
            InvalidateMeasure();
            InvalidateVisual();
        });
        service.Changed += onChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            service.Changed -= onChanged;
            ticker.Stop();
        };
    }

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 1320 : availableSize.Width;
        return new Avalonia.Size(width, DetailsPage.Height(service, State));
    }

    protected override void Draw(IPaint paint, float width) =>
        DetailsPage.Render(paint, width, (float)Math.Max(Bounds.Height, ViewHeight), service, State, DateTimeOffset.UtcNow);

    protected override void Act(string action)
    {
        switch (action)
        {
            case "refresh":
            case "refresh:top":
                if (!service.Refresh(manual: true))
                    MacDialog.Show(null, "請稍候", "10 秒內只能手動更新一次。");
                break;
            case "history":
                shell.OpenHistory();
                break;
            case "analysis":
                shell.OpenAnalysis();
                break;
            case "official":
                MacShell.Open("https://claude.ai/settings/usage");
                break;
            case "folder":
                MacShell.Open(service.Root);
                break;
            case "source":
                State.SourceOpen = !State.SourceOpen;
                InvalidateMeasure();
                InvalidateVisual();
                break;
            default:
                if (action.StartsWith("card:") || action.StartsWith("row:"))
                    shell.OpenHistory(action[(action.IndexOf(':') + 1)..]);
                break;
        }
    }
}
