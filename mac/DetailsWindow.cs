using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SD = System.Drawing;

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

/// <summary>Draws the dashboard with Avalonia; hover highlights what is clickable, rings sweep in on open.</summary>
internal sealed class DashboardControl : Control
{
    readonly MonitorService service;
    readonly MacShell shell;
    readonly PageState state = new();
    readonly DispatcherTimer frame = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly Stopwatch intro = new();

    public DashboardControl(MonitorService service, MacShell shell)
    {
        this.service = service;
        this.shell = shell;
        frame.Tick += (_, _) =>
        {
            double t = Math.Min(1, intro.ElapsedMilliseconds / 900.0);
            state.Intro = (float)(1 - Math.Pow(1 - t, 3));
            InvalidateVisual();
            if (t >= 1)
            {
                frame.Stop();
                intro.Reset();
            }
        };
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

    public void PlayIntro()
    {
        if (!MacShell.AnimationsEnabled)
            return;
        state.Intro = 0;
        intro.Restart();
        frame.Start();
    }

    public void Settle()
    {
        frame.Stop();
        state.Intro = 1;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 1320 : availableSize.Width;
        return new Size(width, DetailsPage.Height(service, state));
    }

    public override void Render(DrawingContext ctx)
    {
        double viewHeight = (this.GetVisualParent() as Visual)?.Bounds.Height ?? Bounds.Height;
        DetailsPage.Render(new AvaloniaPaint(ctx), (float)Bounds.Width, (float)Math.Max(Bounds.Height, viewHeight), service, state, DateTimeOffset.UtcNow);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        string? hit = state.HitAt(new SD.PointF((float)at.X, (float)at.Y));
        Cursor = hit != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        if (hit != state.Hover)
        {
            state.Hover = hit;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        state.Hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var at = e.GetPosition(this);
        if (state.HitAt(new SD.PointF((float)at.X, (float)at.Y)) is not { } action)
            return;
        switch (action)
        {
            case "refresh":
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
                state.SourceOpen = !state.SourceOpen;
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
