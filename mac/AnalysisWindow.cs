using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClaudeUsageMonitor;

/// <summary>Usage analysis (same page as the Windows app, drawn by the shared AnalysisRenderer).</summary>
internal sealed class AnalysisWindow : Window
{
    readonly MonitorService service;
    readonly AnalysisPageView view;
    readonly ScrollViewer scroller;
    int days = 7;

    public AnalysisWindow(MonitorService service)
    {
        this.service = service;
        view = new AnalysisPageView(this);
        Title = "Claude 額度 · 用量分析" + (service.Demo ? "（示範資料）" : "");
        Icon = MacShell.AppIcon();
        Width = 1100;
        Height = 860;
        MinWidth = 900;
        MinHeight = 560;
        Background = MacTheme.Solid(Color.FromRgb(0x0B, 0x10, 0x22));
        FontFamily = MacTheme.UiFont;
        scroller = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = scroller;

        Opened += async (_, _) =>
        {
            view.PlayIntro();
            Reload(scanTokens: true);
            if (service.Behaviors == null || DateTimeOffset.UtcNow - service.Behaviors.ObservedAt > TimeSpan.FromHours(1))
                await service.RefreshBehaviorsAsync();
        };
        Action onChanged = () => Dispatcher.UIThread.Post(() => Reload(scanTokens: false));
        service.Changed += onChanged;
        Closed += (_, _) => service.Changed -= onChanged;
    }

    internal MonitorService Service => service;
    internal int Days => days;
    internal AnalysisPageView Page => view;

    async void Reload(bool scanTokens)
    {
        var scan = scanTokens ? service.ScanTokensAsync(days) : null;
        // Shows "reading" (the sunburst turns) while the transcripts are read.
        view.SetData();
        if (scan == null)
            return;
        await scan;
        view.SetData();
    }

    internal async void Act(string action)
    {
        if (action.StartsWith("range:") && int.Parse(action["range:".Length..]) is int range && range != days)
        {
            days = range;
            // The page plays its entrance again for the new range; the header stays put.
            view.Motion.Restart("top");
            Reload(scanTokens: true);
        }
        else if (action == "reanalyze" && !service.AnalyzingBehaviors)
        {
            var tokens = service.ScanTokensAsync(days);
            await service.RefreshBehaviorsAsync();
            await tokens;
            Reload(scanTokens: false);
        }
    }

    /// <summary>Scrolls the page; used by --screenshot.</summary>
    internal void ScrollTo(double y) => scroller.Offset = new Vector(0, y);

    /// <summary>Waits for the token scan and attribution; used by --screenshot.</summary>
    internal async Task PrepareAsync()
    {
        await service.ScanTokensAsync(days);
        await service.RefreshBehaviorsAsync();
        view.SetData();
    }
}

/// <summary>Hosts the shared analysis layout; its height follows what the layout used.</summary>
internal sealed class AnalysisPageView : GlassControl
{
    readonly AnalysisWindow owner;
    AnalysisData? data;
    double pageHeight = 1200;

    public AnalysisPageView(AnalysisWindow owner)
    {
        this.owner = owner;
    }

    public void SetData()
    {
        var service = owner.Service;
        // A new attribution from Claude Code plays its section's entrance again.
        if (data != null && !ReferenceEquals(data.Behaviors, service.Behaviors))
            Motion.Replay("attr");
        data = new AnalysisData(owner.Days, service.Analyze(owner.Days), TokenReport.Build(service.Tokens, service.Current, owner.Days, DateTimeOffset.UtcNow),
            service.Behaviors, service.AnalyzingBehaviors, service.Settings);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 1100 : availableSize.Width, pageHeight);

    protected override void Draw(IPaint paint, float width)
    {
        if (data == null)
            return;
        double height = AnalysisRenderer.Render(paint, width, State, data);
        if (Math.Abs(height - pageHeight) > 2)
        {
            pageHeight = height;
            Dispatcher.UIThread.Post(InvalidateMeasure);
        }
    }

    protected override void Act(string action) => owner.Act(action);
}
