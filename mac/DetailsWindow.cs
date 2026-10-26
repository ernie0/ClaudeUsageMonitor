using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClaudeUsageMonitor;

/// <summary>Every window as a coloured card with its facts; the data source stays folded until asked for.</summary>
internal sealed class DetailsWindow : Window
{
    const string SourceClosed = "▸ 資料來源與紀錄位置";
    const string SourceOpen = "▾ 收起資料來源";

    readonly MonitorService service;
    readonly QuotaCardsView cards;
    readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Foreground = MacTheme.Solid(MacTheme.Muted), FontSize = MacDraw.Pt(10) };
    readonly Button sourceToggle = new() { Content = SourceClosed, Background = Brushes.Transparent, Foreground = MacTheme.Solid(MacTheme.Coral), Padding = new Thickness(0, 2) };
    readonly TextBlock source = new() { TextWrapping = TextWrapping.Wrap, Foreground = MacTheme.Solid(MacTheme.Muted), FontSize = MacDraw.Pt(10), IsVisible = false, Margin = new Thickness(14, 0, 0, 0) };
    readonly Button openFolder = new() { Content = "開啟紀錄資料夾", IsVisible = false, Margin = new Thickness(12, 4, 0, 0) };
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(30) };

    public DetailsWindow(MonitorService service, MacShell shell)
    {
        this.service = service;
        cards = new QuotaCardsView(service);
        Title = "Claude 額度 · 詳情" + (service.Demo ? "（示範資料）" : "");
        Icon = MacShell.AppIcon();
        Width = 880;
        SizeToContent = SizeToContent.Height;
        MinWidth = 720;
        Background = MacTheme.Solid(MacTheme.Canvas);
        FontFamily = MacTheme.UiFont;

        sourceToggle.Click += (_, _) =>
        {
            source.IsVisible = !source.IsVisible;
            openFolder.IsVisible = source.IsVisible;
            sourceToggle.Content = source.IsVisible ? SourceOpen : SourceClosed;
        };
        openFolder.Click += (_, _) => MacShell.Open(service.Root);

        var refresh = new Button { Content = "立即更新", Classes = { "accent" } };
        refresh.Click += (_, _) =>
        {
            if (!service.Refresh(manual: true))
                MacDialog.Show(this, "請稍候", "10 秒內只能手動更新一次。");
        };
        var official = new Button { Content = "官方用量頁" };
        official.Click += (_, _) => MacShell.Open("https://claude.ai/settings/usage");
        var history = new Button { Content = "使用紀錄" };
        history.Click += (_, _) => shell.OpenHistory();
        var close = new Button { Content = "關閉" };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Children =
            {
                cards,
                new StackPanel { Margin = new Thickness(14, 8, 14, 0), Spacing = 4, Children = { summary, sourceToggle, source, openFolder } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(12),
                    Children = { history, official, refresh, close },
                },
            },
        };

        Opened += (_, _) =>
        {
            Render();
            cards.Animate();
        };
        ticker.Tick += (_, _) => Render();
        ticker.Start();
        Action onChanged = () => Dispatcher.UIThread.Post(Render);
        service.Changed += onChanged;
        Closed += (_, _) =>
        {
            service.Changed -= onChanged;
            ticker.Stop();
        };
    }

    public QuotaCardsView Cards => cards;

    void Render()
    {
        var snapshot = service.Current;
        cards.Height = cards.PreferredHeight;
        cards.InvalidateVisual();

        var lines = new List<string>();
        if (snapshot != null)
        {
            lines.Add($"方案：{snapshot.PlanLabel}　資料時間：{snapshot.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" + (service.Fresh ? "" : "（上次資料）"));
            if (snapshot.Extra is { } extra)
                lines.Add($"額外用量：{(extra.Enabled ? "已開啟" : "未開啟")}，本月 {Format.Money(extra.Used, extra.Currency)}／上限 {Format.Money(extra.MonthlyLimit, extra.Currency)}");
        }
        lines.Add("狀態：" + service.Status
            + (service.NextPoll is { } next && !service.Busy ? $"　下次更新 {next.ToLocalTime():HH:mm:ss}" : "")
            + (service.SlowedDown ? "（額度沒有變化，自動放慢）" : ""));
        if (ClaudeLocator.Warning.Length > 0)
            lines.Add("⚠ " + ClaudeLocator.Warning);
        lines.Add("使用速度是工具開著時的觀察推估，不是官方數字；重置時間由官方回報。");
        summary.Text = string.Join("\n", lines);

        source.Text = "資料來源：" + (service.ClaudePath.Length > 0 ? service.ClaudePath : "尚未找到 Claude Code")
            + (snapshot?.ClaudeVersion is { Length: > 0 } v ? $"（Claude Code {v}）" : "")
            + "\n紀錄位置：" + service.Root;
    }
}

/// <summary>One row per window: the coloured tile on the left, the facts on the right; rings sweep in on open.</summary>
internal sealed class QuotaCardsView : Control
{
    const double RowH = 104, RowGap = 10, Margin_ = 12, TileWidth = 250;

    readonly MonitorService service;
    readonly DispatcherTimer frame = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly Stopwatch clock = new();
    double progress = 1;

    public QuotaCardsView(MonitorService service)
    {
        this.service = service;
        frame.Tick += (_, _) =>
        {
            double t = Math.Min(1, clock.ElapsedMilliseconds / 800.0);
            progress = 1 - Math.Pow(1 - t, 3);
            InvalidateVisual();
            if (t >= 1)
            {
                frame.Stop();
                clock.Reset();
            }
        };
        Height = PreferredHeight;
    }

    public double PreferredHeight => Margin_ + Math.Max(1, service.Current?.Windows.Count ?? 1) * (RowH + RowGap);

    public void Animate()
    {
        if (!MacShell.AnimationsEnabled)
            return;
        progress = 0;
        clock.Restart();
        frame.Start();
    }

    public void Settle()
    {
        frame.Stop();
        progress = 1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = service.Current;
        if (snapshot == null)
        {
            MacDraw.Pill(ctx, new Rect(Margin_, Margin_, Bounds.Width - Margin_ * 2, 34), Insights.Build(service, now));
            return;
        }
        double y = Margin_;
        foreach (var w in snapshot.Windows)
        {
            var level = Levels.Of(w.RemainingPercent, service.Settings);
            var forecast = service.Forecasts.GetValueOrDefault(w.Id) ?? Forecast.Empty;
            var tile = new Rect(Margin_, y, TileWidth, RowH);
            MacDraw.Tile(ctx, tile, new MacTile(Insights.Title(w), Insights.Icon(w), w.RemainingPercent * progress, level,
                Levels.Word(level, w.RemainingPercent), Format.ShortCountdown(w.ResetsAt, now), $"已使用 {w.UsedPercent:0}%"));

            var panel = new Rect(new Point(tile.Right + 10, y), new Point(Bounds.Width - Margin_, y + RowH));
            ctx.DrawRectangle(MacTheme.Solid(MacTheme.Surface), new Pen(MacTheme.Solid(MacTheme.Border), 1), panel, 14, 14);

            var facts = new List<(string Key, string Value, Color Color)>
            {
                ("重置時間", w.ResetsAt is { } r ? Format.ResetAt(r, now) : "未提供", MacTheme.Text),
                ("還有", w.ResetsAt is { } r2 && r2 > now ? Format.Duration(r2 - now) : "—", MacTheme.Text),
                ("使用速度", w.RemainingPercent <= 0 ? "—" : forecast.RatePerHour is { } rate ? $"{Format.Rate(rate)}（{Format.Basis(forecast.BasisMinutes)}）" : "觀察中", MacTheme.Text),
                ("照目前速度", Outlook(w, forecast, now, out var outlookColor), outlookColor),
                ("每小時可用", forecast.BudgetPerHour is { } budget && w.RemainingPercent > 0 ? $"{Format.Rate(budget)} 以下就撐得到重置" : "—", MacTheme.Text),
                ("已使用", w.UsedPercent.ToString("0", CultureInfo.InvariantCulture) + "%", MacTheme.Text),
            };
            double colW = (panel.Width - 28) / 2;
            for (int i = 0; i < facts.Count; i++)
            {
                double fx = panel.X + 16 + (i % 2) * colW;
                double fy = panel.Y + 12 + (i / 2) * 28;
                MacDraw.Text(ctx, facts[i].Key, MacTheme.Ui(), 9.5, MacTheme.Muted, new Rect(fx, fy + 2, 74, 18), shadow: false);
                MacDraw.Text(ctx, facts[i].Value, MacTheme.Ui(FontWeight.Bold), 10, facts[i].Color, new Rect(fx + 74, fy, colW - 80, 20), shadow: false);
            }
            y += RowH + RowGap;
        }
    }

    static string Outlook(QuotaWindow w, Forecast forecast, DateTimeOffset now, out Color color)
    {
        if (w.RemainingPercent <= 0)
        {
            color = MacTheme.Danger;
            return "已用完，等待重置";
        }
        if (forecast.ExhaustsBeforeReset && forecast.ExhaustAt is { } at)
        {
            color = MacTheme.Warning;
            return Format.ResetAt(at, now) + " 用完（早於重置）";
        }
        if (forecast.RatePerHour is not null)
        {
            color = MacTheme.Good;
            return "撐得到重置";
        }
        color = MacTheme.Muted;
        return "還在觀察";
    }
}
