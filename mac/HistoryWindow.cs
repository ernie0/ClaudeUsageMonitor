using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SD = System.Drawing;

namespace ClaudeUsageMonitor;

/// <summary>The history page (layout in Core/HistoryPage.cs) with scrolling, hover, a search box and CSV export.</summary>
internal sealed class HistoryWindow : Window
{
    readonly MonitorService service;
    readonly HistoryState state = new();
    readonly HistoryPageControl page;
    readonly Canvas overlay = new();
    readonly TextBox search = new()
    {
        Watermark = "搜尋紀錄…",
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = MacTheme.Solid(MacTheme.Text),
        FontFamily = MacTheme.UiFont,
        FontSize = MacDraw.Pt(10.5),
        Padding = new Thickness(0),
        MinHeight = 0,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };
    DateTimeOffset? lastObserved;

    public HistoryWindow(MonitorService service)
    {
        this.service = service;
        page = new HistoryPageControl(this);
        Title = "Claude 額度 · 使用紀錄" + (service.Demo ? "（示範資料）" : "");
        Icon = MacShell.AppIcon();
        Width = 1240;
        Height = 940;
        MinWidth = 1080;
        MinHeight = 600;
        Background = MacTheme.Solid(MacTheme.Canvas);
        overlay.Children.Add(search);
        search.TextChanged += (_, _) =>
        {
            state.Search = search.Text ?? "";
            page.InvalidateMeasure();
            page.InvalidateVisual();
        };
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Panel { Children = { page, overlay } },
        };
        Opened += (_, _) => Reload(scanTokens: true);
        Action onChanged = () => Dispatcher.UIThread.Post(() =>
        {
            var observed = service.Current?.ObservedAt;
            if (observed == lastObserved)
                return;
            lastObserved = observed;
            Reload(scanTokens: false);
        });
        service.Changed += onChanged;
        Closed += (_, _) => service.Changed -= onChanged;
    }

    internal MonitorService Service => service;
    internal HistoryState State => state;
    internal HistoryData? Data { get; private set; }

    /// <summary>Shows one window's curve (from a card or row on the details dashboard).</summary>
    public void Select(string windowId)
    {
        state.Window = windowId;
        Reload(scanTokens: false);
    }

    async void Reload(bool scanTokens)
    {
        Data = HistoryData.Load(service, state, DateTimeOffset.UtcNow);
        page.InvalidateMeasure();
        page.PlayIntro();
        if (scanTokens)
        {
            await service.ScanTokensAsync((int)Math.Ceiling(state.Range.TotalDays));
            Data = HistoryData.Load(service, state, DateTimeOffset.UtcNow);
            page.InvalidateVisual();
        }
    }

    /// <summary>Keeps the real text box on the search frame the page drew.</summary>
    internal void PlaceSearch(SD.RectangleF box)
    {
        Canvas.SetLeft(search, box.X);
        Canvas.SetTop(search, box.Y);
        search.Width = box.Width;
        search.Height = box.Height;
    }

    internal async void Act(string action)
    {
        if (action.StartsWith("range:"))
        {
            state.Range = TimeSpan.FromDays(int.Parse(action["range:".Length..]));
            Reload(scanTokens: true);
        }
        else if (action.StartsWith("window:"))
        {
            state.Window = action["window:".Length..];
            Reload(scanTokens: false);
        }
        else if (action == "sort")
        {
            state.NewestFirst = !state.NewestFirst;
            page.InvalidateVisual();
        }
        else if (action == "search")
        {
            search.Focus();
        }
        else if (action == "export")
        {
            await ExportCsvAsync();
        }
    }

    async Task ExportCsvAsync()
    {
        if (Data == null)
            return;
        var now = DateTimeOffset.Now;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "匯出 CSV",
            SuggestedFileName = $"claude-usage-{(now - state.Range):yyyyMMdd}-{now:yyyyMMdd}.csv",
            FileTypeChoices = [new FilePickerFileType("CSV 表格") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;
        try
        {
            HistoryStore.ExportCsv(path, Data.Rows);
            MacDialog.Show(this, "匯出完成", $"已匯出 {Data.Rows.Count} 筆（所有額度）。\n檔案含使用時間，分享前請先檢查。");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MacDialog.Show(this, "匯出失敗", "無法寫入這個位置，請換一個資料夾。");
        }
    }

    /// <summary>Finishes the chart's draw-in at once; used by --screenshot.</summary>
    internal void Settle() => page.Settle();
}

/// <summary>Draws the history page with Avalonia.</summary>
internal sealed class HistoryPageControl : Control
{
    readonly HistoryWindow owner;
    readonly DispatcherTimer frame = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly Stopwatch intro = new();

    public HistoryPageControl(HistoryWindow owner)
    {
        this.owner = owner;
        frame.Tick += (_, _) =>
        {
            double t = Math.Min(1, intro.ElapsedMilliseconds / 700.0);
            owner.State.Intro = (float)(1 - Math.Pow(1 - t, 3));
            InvalidateVisual();
            if (t >= 1)
            {
                frame.Stop();
                intro.Reset();
            }
        };
    }

    public void PlayIntro()
    {
        if (!MacShell.AnimationsEnabled)
            return;
        owner.State.Intro = 0;
        intro.Restart();
        frame.Start();
    }

    public void Settle()
    {
        frame.Stop();
        owner.State.Intro = 1;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 1240 : availableSize.Width;
        return new Size(width, owner.Data == null ? 900 : HistoryPage.Height(owner.Data, owner.State));
    }

    public override void Render(DrawingContext ctx)
    {
        if (owner.Data == null)
            return;
        double viewHeight = (this.GetVisualParent()?.GetVisualParent() as Visual)?.Bounds.Height ?? Bounds.Height;
        HistoryPage.Render(new AvaloniaPaint(ctx), (float)Bounds.Width, (float)Math.Max(Bounds.Height, viewHeight), owner.Service, owner.Data, owner.State, DateTimeOffset.UtcNow);
        var box = owner.State.SearchBox;
        Dispatcher.UIThread.Post(() => owner.PlaceSearch(box));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        var point = new SD.PointF((float)at.X, (float)at.Y);
        string? hit = owner.State.HitAt(point);
        Cursor = hit != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        owner.State.Hover = hit;
        owner.State.Pointer = point;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        owner.State.Hover = null;
        owner.State.Pointer = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var at = e.GetPosition(this);
        if (owner.State.HitAt(new SD.PointF((float)at.X, (float)at.Y)) is { } action)
            owner.Act(action);
    }
}
