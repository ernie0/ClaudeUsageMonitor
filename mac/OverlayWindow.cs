using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClaudeUsageMonitor;

/// <summary>Borderless, always-on-top card window; the drawing lives in <see cref="OverlayView"/>.</summary>
internal sealed class OverlayWindow : Window
{
    readonly MonitorService service;
    readonly DispatcherTimer saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public OverlayWindow(MonitorService service, MacShell shell)
    {
        this.service = service;
        Title = service.Demo ? "ClaudeUsageMonitor（示範資料）" : "ClaudeUsageMonitor";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Icon = MacShell.AppIcon();
        View = new OverlayView(service, shell, this);
        Content = View;
        ApplySettings();

        Opened += (_, _) => RestorePosition();
        PositionChanged += (_, _) =>
        {
            saveDebounce.Stop();
            saveDebounce.Start();
        };
        saveDebounce.Tick += (_, _) =>
        {
            saveDebounce.Stop();
            service.Settings.WindowX = Position.X;
            service.Settings.WindowY = Position.Y;
            service.SaveSettings();
        };
    }

    public OverlayView View { get; }

    public void ApplySettings()
    {
        Topmost = service.Settings.TopMost;
        Opacity = service.Settings.OpacityPercent / 100.0;
        Resize();
    }

    public void Resize()
    {
        var size = View.DesiredLayoutSize;
        if (Math.Abs(Width - size.Width) < 0.5 && Math.Abs(Height - size.Height) < 0.5)
            return;
        // Keep a card docked to the right edge docked when it grows or shrinks.
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        double scale = screen?.Scaling ?? 1;
        bool dockedRight = screen != null && !double.IsNaN(Width) && screen.WorkingArea.Right - (Position.X + Width * scale) < 40 * scale;
        int right = Position.X + (int)(Width * scale);
        Width = size.Width;
        Height = size.Height;
        if (dockedRight)
            Position = new PixelPoint(right - (int)(Width * scale), Position.Y);
    }

    void RestorePosition()
    {
        var screen = Screens.Primary;
        if (screen == null)
            return;
        double scale = screen.Scaling;
        var area = screen.WorkingArea;
        int width = (int)(Width * scale), height = (int)(Height * scale);
        var position = service.Settings.WindowX is int x && service.Settings.WindowY is int y
            ? new PixelPoint(x, y)
            // macOS: top-right under the menu bar; elsewhere bottom-right, like the Windows app.
            : OperatingSystem.IsMacOS()
                ? new PixelPoint(area.Right - width - (int)(16 * scale), area.Y + (int)(12 * scale))
                : new PixelPoint(area.Right - width - (int)(24 * scale), area.Bottom - height - (int)(24 * scale));
        Position = new PixelPoint(
            Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    public void ToggleVisible()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            View.FadeIn();
        }
    }
}

/// <summary>
/// The card itself: coloured blocks with rings, the conclusion strip, and the small animations
/// (rings easing in, sparkle pop after refresh, "−1%" floats, reset confetti, hover lift with each
/// quota's edge light, click ripples). The frame timer only runs while something moves.
/// </summary>
internal sealed class OverlayView : Control
{
    const double Pad = 10, HeaderH = 28, TileW = 206, TileH = 108, Gap = 8, ScopedW = 120, PillH = 32;
    const double CompactW = 262, CompactH = 120;
    const long PopMs = 700;

    readonly MonitorService service;
    readonly MacShell shell;
    readonly OverlayWindow window;
    readonly DispatcherTimer frame = new() { Interval = TimeSpan.FromMilliseconds(16) };
    // The clock and the countdowns change once a minute: one repaint at each minute between animations.
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(Format.UntilNextMinute(DateTimeOffset.Now)) };
    readonly Stopwatch fx = Stopwatch.StartNew();
    readonly Stopwatch fade = new();
    readonly Dictionary<string, double> shown = [];
    readonly Dictionary<string, double> lift = [];
    readonly List<(Rect Bounds, string Id)> tiles = [];
    readonly List<FxEffect> effects = [];
    readonly Random random = new();
    readonly bool animate = MacShell.AnimationsEnabled;
    double spin, shimmer = -1, pulse;
    long popStart = -10_000;
    string? hover;
    bool scriptedHover;
    UsageSnapshot? lastSeen;

    public OverlayView(MonitorService service, MacShell shell, OverlayWindow window)
    {
        this.service = service;
        this.shell = shell;
        this.window = window;
        ClipToBounds = false;
        ContextMenu = shell.BuildContextMenu();
        frame.Tick += (_, _) => OnFrame();
        ticker.Tick += (_, _) =>
        {
            ticker.Interval = TimeSpan.FromMilliseconds(Format.UntilNextMinute(DateTimeOffset.Now));
            InvalidateVisual();
        };
        ticker.Start();
        service.Changed += () => Dispatcher.UIThread.Post(OnServiceChanged);
        service.Workflows.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            InvalidateVisual();
            // Claude waits for an answer: the small window comes back, without taking the keyboard.
            if (service.Workflows.NeedsAnswer)
                shell.RevealWorkflow();
        });
        PointerExited += (_, _) =>
        {
            if (!scriptedHover)
                SetHover(null);
        };
    }

    bool Compact => service.Settings.Compact;
    bool HasScoped => service.Current?.Scoped.Any() == true;

    public Size DesiredLayoutSize => Compact
        ? new Size(CompactW, CompactH)
        : new Size(Pad * 2 + TileW * 2 + Gap + (HasScoped ? Gap + ScopedW : 0), Pad * 2 + HeaderH + 6 + TileH + Gap + PillH);

    protected override Size MeasureOverride(Size availableSize) => DesiredLayoutSize;

    Rect ModeToggle => Compact
        ? new Rect(Bounds.Width - 30, Bounds.Height - 28, 22, 20)
        : new Rect(Bounds.Width - Pad - 22, Pad + 3, 22, 20);

    /// <summary>The ">_" button that opens the workflow window, left of the mode toggle.</summary>
    Rect WorkflowButton => Compact
        ? new Rect(Bounds.Width - 58, Bounds.Height - 28, 24, 20)
        : new Rect(Bounds.Width - Pad - 50, Pad + 3, 24, 20);

    void OnServiceChanged()
    {
        window.Resize();
        InvalidateMeasure();
        React();
        Kick();
        InvalidateVisual();
    }

    public void ToggleMode()
    {
        service.Settings.Compact = !service.Settings.Compact;
        service.SaveSettings();
        window.Resize();
        InvalidateMeasure();
        Kick();
        InvalidateVisual();
    }

    // ---- animation ------------------------------------------------------------------------------

    public void FadeIn()
    {
        if (!animate)
            return;
        window.Opacity = 0;
        fade.Restart();
        Kick();
    }

    void Kick()
    {
        if (animate)
        {
            frame.Start();
            return;
        }
        if (service.Current is { } snapshot)
        {
            foreach (var w in snapshot.Windows)
                shown[w.Id] = w.RemainingPercent;
        }
    }

    void OnFrame()
    {
        bool tweening = false;
        if (service.Current is { } snapshot)
        {
            foreach (var w in snapshot.Windows)
            {
                double current = shown.GetValueOrDefault(w.Id, 0);
                double delta = w.RemainingPercent - current;
                if (Math.Abs(delta) > 0.05)
                {
                    current += delta * 0.12 + Math.Sign(delta) * 0.05;
                    shown[w.Id] = Math.Abs(w.RemainingPercent - current) < 0.05 || Math.Sign(w.RemainingPercent - current) != Math.Sign(delta)
                        ? w.RemainingPercent
                        : current;
                    tweening = true;
                }
                else
                {
                    shown[w.Id] = w.RemainingPercent;
                }
            }
        }

        bool loading = service.Busy || service.Current == null;
        if (loading)
        {
            spin = (spin + 4) % 360;
            shimmer = shimmer < 0 ? 0 : (shimmer + 0.016) % 1;
        }
        else
        {
            shimmer = -1;
        }

        bool critical = CriticalMain();
        if (critical)
            pulse = (pulse + 0.045) % (Math.PI * 2);

        bool fading = fade.IsRunning;
        if (fading)
        {
            double t = Math.Min(1, fade.ElapsedMilliseconds / 320.0);
            window.Opacity = service.Settings.OpacityPercent / 100.0 * (1 - Math.Pow(1 - t, 3));
            if (t >= 1)
                fade.Reset();
        }

        bool lifting = false;
        foreach (var (_, id) in tiles)
        {
            double current = lift.GetValueOrDefault(id), target = id == hover ? 1 : 0;
            if (Math.Abs(target - current) > 0.01)
            {
                lift[id] = current + (target - current) * 0.14;
                lifting = true;
            }
            else
            {
                lift[id] = target;
            }
        }

        long now = fx.ElapsedMilliseconds;
        effects.RemoveAll(e => now - e.Start > e.Duration);
        bool effecting = effects.Count > 0 || now - popStart < PopMs || hover != null;

        InvalidateVisual();
        if (!tweening && !loading && !critical && !fading && !lifting && !effecting)
            frame.Stop();
        else
            frame.Interval = TimeSpan.FromMilliseconds(tweening || loading || fading || lifting || effecting ? 16 : 50);
    }

    bool CriticalMain() =>
        IsVisible && service.Current is { } s
        && new[] { s.Session, s.Weekly }.OfType<QuotaWindow>()
            .Any(w => w.RemainingPercent > 0 && w.RemainingPercent <= service.Settings.CriticalRemaining);

    double ShownValue(QuotaWindow w) => animate ? shown.GetValueOrDefault(w.Id, 0) : w.RemainingPercent;

    double GlowFor(QuotaWindow w) =>
        w.RemainingPercent > 0 && w.RemainingPercent <= service.Settings.CriticalRemaining ? (Math.Sin(pulse) + 1) / 2 : 0;

    double PopProgress => Math.Clamp((fx.ElapsedMilliseconds - popStart) / (double)PopMs, 0, 1);

    enum EffectKind { Float, Ripple, Confetti }

    sealed record Particle(double Vx, double Vy, Color Color, double Size, double Spin);

    sealed record FxEffect(EffectKind Kind, long Start, long Duration, Point At, string Text = "", Particle[]? Particles = null, Rect? Clip = null);

    /// <summary>A "−1%" float when quota is used, confetti when a window comes back, a sparkle pop on every refresh.</summary>
    void React()
    {
        var snapshot = service.Current;
        if (snapshot == null || snapshot.ObservedAt == lastSeen?.ObservedAt)
            return;
        var previous = lastSeen;
        lastSeen = snapshot;
        if (!animate)
            return;
        popStart = fx.ElapsedMilliseconds;
        if (previous == null || previous.AccountKey != snapshot.AccountKey)
            return;
        foreach (var w in snapshot.Windows)
        {
            if (previous.Find(w.Id) is not { } before)
                continue;
            double delta = Math.Round(w.RemainingPercent - before.RemainingPercent);
            if (delta == 0 || TileFor(w.Id) is not { } tile)
                continue;
            var ring = MacDraw.RingCenter(tile);
            long start = fx.ElapsedMilliseconds;
            if (delta >= 10)
            {
                effects.Add(new FxEffect(EffectKind.Confetti, start, 1500, new Point(tile.Center.X, tile.Y + tile.Height * 0.45), "", Confetti()));
                effects.Add(new FxEffect(EffectKind.Float, start, 1600, new Point(ring.X, ring.Y - 20), $"+{delta:0}% 已恢復"));
            }
            else
            {
                effects.Add(new FxEffect(EffectKind.Float, start, 1300, new Point(ring.X, ring.Y - 20), (delta > 0 ? "+" : "−") + $"{Math.Abs(delta):0}%"));
            }
        }
    }

    Rect? TileFor(string id)
    {
        string key = !Compact ? id : service.Current?.Tightest?.Id == id ? "compact" : "";
        foreach (var (bounds, tileId) in tiles)
        {
            if (tileId == key)
                return bounds;
        }
        return null;
    }

    Particle[] Confetti()
    {
        Color[] colors = [MacTheme.Coral, MacTheme.Violet, MacTheme.Good, MacTheme.Warning, Colors.White, Color.FromRgb(0x38, 0xBD, 0xF8)];
        return Enumerable.Range(0, 28).Select(_ =>
        {
            double angle = random.NextDouble() * Math.PI * 2;
            double speed = 90 + random.NextDouble() * 150;
            return new Particle(Math.Cos(angle) * speed, Math.Sin(angle) * speed - 120, colors[random.Next(colors.Length)],
                3 + random.NextDouble() * 3, random.NextDouble() * 720 - 360);
        }).ToArray();
    }

    // ---- input ----------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        var at = point.Position;
        if (ModeToggle.Contains(at))
        {
            ToggleMode();
            e.Handled = true;
            return;
        }
        if (WorkflowButton.Contains(at))
        {
            shell.ToggleWorkflow();
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            shell.OpenDetails();
            e.Handled = true;
            return;
        }
        foreach (var (bounds, _) in tiles)
        {
            if (animate && bounds.Contains(at))
            {
                effects.Add(new FxEffect(EffectKind.Ripple, fx.ElapsedMilliseconds, 450, at, Clip: bounds));
                Kick();
                break;
            }
        }
        window.BeginMoveDrag(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (!scriptedHover)
            SetHover(tiles.FirstOrDefault(t => t.Bounds.Contains(at)).Id);
        if (animate && !Compact && at.Y < Pad + HeaderH && at.X < Pad + 120 && fx.ElapsedMilliseconds - popStart > 1200)
        {
            popStart = fx.ElapsedMilliseconds;
            Kick();
        }
        ToolTip.SetTip(this, ModeToggle.Contains(at) ? (Compact ? "切換為標準模式" : "切換為精簡模式")
            : WorkflowButton.Contains(at) ? "Claude 工作流：不用開 VS Code 就能下指令" + (service.Workflows.Running is int n and > 0 ? $"（{n} 個工作進行中）" : "")
            : TooltipText());
    }

    void SetHover(string? id)
    {
        if (id == hover)
            return;
        hover = id;
        Kick();
        InvalidateVisual();
    }

    string TooltipText()
    {
        var snapshot = service.Current;
        if (snapshot == null)
            return service.Status;
        var now = DateTimeOffset.UtcNow;
        var lines = new List<string> { $"Claude 額度 · {snapshot.PlanLabel}" };
        foreach (var w in snapshot.Windows)
        {
            string reset = w.ResetsAt is { } r ? Format.ResetAt(r, now) + " 重置" : "重置時間未提供";
            lines.Add($"{Insights.Title(w)}：剩 {Format.Percent(w.RemainingPercent)} · {reset}");
        }
        lines.Add(service.Status);
        lines.Add("右鍵（或按住 Control 點一下）開選單 · 連點兩下看詳情 · 可拖曳");
        return string.Join("\n", lines);
    }

    // ---- painting -------------------------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        double radius = OperatingSystem.IsMacOS() ? 14 : 10;
        ctx.DrawRectangle(MacTheme.Vertical(MacTheme.Canvas, MacTheme.CanvasEnd), new Pen(MacTheme.Solid(MacTheme.Alpha(MacTheme.Border, 200)), 1),
            bounds.Deflate(0.5), radius, radius);

        tiles.Clear();
        var now = DateTimeOffset.UtcNow;
        using (ctx.PushClip(new RoundedRect(bounds, radius)))
        {
            if (Compact)
                PaintCompact(ctx, now);
            else
                PaintStandard(ctx, now);
            PaintModeToggle(ctx);
            PaintWorkflowButton(ctx);
            PaintEffects(ctx);
        }
    }

    void PaintStandard(DrawingContext ctx, DateTimeOffset now)
    {
        var snapshot = service.Current;
        ctx.DrawRectangle(MacTheme.Horizontal(MacTheme.Coral, MacTheme.Violet), null, new Rect(0, 0, Bounds.Width, 3));

        double pop = PopProgress;
        double bounce = pop < 1 ? Math.Sin(pop * Math.PI) : 0;
        double turn = 90 * (1 - (1 - pop) * (1 - pop));
        MacDraw.Sparkle(ctx, new Point(Pad + 10, Pad + 13), 10 * (1 + 0.5 * bounce), MacTheme.Diagonal(MacTheme.Coral, MacTheme.Violet), spin + turn);
        MacDraw.Text(ctx, "Claude 額度", MacTheme.Ui(FontWeight.Bold), 11, Colors.White, new Rect(Pad + 24, Pad + 3, 180, 22), shadow: false);

        string meta = snapshot == null ? "" : $"{snapshot.PlanLabel} · {snapshot.ObservedAt.ToLocalTime():HH:mm} 更新";
        double metaWidth = MacDraw.Measure(meta, MacTheme.Ui(), 9.5);
        double metaX = WorkflowButton.Left - 6 - metaWidth;
        MacDraw.Text(ctx, meta, MacTheme.Ui(), 9.5, Color.FromRgb(0xC9, 0xC5, 0xE0), new Rect(metaX, Pad + 6, metaWidth + 4, 18), shadow: false);
        var dot = new Point(metaX - 10, Pad + 14);
        PaintClock(ctx, Pad + 24 + MacDraw.Measure("Claude 額度", MacTheme.Ui(FontWeight.Bold), 11) + 6, Pad + 2, dot.X - 12, now);
        if (pop < 1)
        {
            double ripple = 4 + 12 * (1 - (1 - pop) * (1 - pop));
            ctx.DrawEllipse(null, new Pen(MacTheme.Solid(MacTheme.Alpha(MacTheme.Good, 170 * (1 - pop))), 2), dot, ripple, ripple);
        }
        PaintStatusDot(ctx, dot, 4.5);

        double tilesY = Pad + HeaderH + 6;
        PaintMainTile(ctx, new Rect(Pad, tilesY, TileW, TileH), snapshot?.Session, WindowIds.FiveHour, now);
        PaintMainTile(ctx, new Rect(Pad + TileW + Gap, tilesY, TileW, TileH), snapshot?.Weekly, WindowIds.SevenDay, now);

        if (snapshot != null && HasScoped)
        {
            var scoped = snapshot.Scoped.OrderBy(w => w.RemainingPercent).Take(2).ToList();
            double x = Pad + TileW * 2 + Gap * 2;
            double h = (TileH - (scoped.Count - 1) * Gap) / scoped.Count;
            for (int i = 0; i < scoped.Count; i++)
            {
                var w = scoped[i];
                var r = new Rect(x, tilesY + i * (h + Gap), ScopedW, h);
                var level = Levels.Of(w.RemainingPercent, service.Settings);
                var drawn = Lifted(r, w.Id, out double l);
                MacDraw.Mini(ctx, drawn, w.Label.Replace("本週·", ""), ShownValue(w), level, Levels.Word(level, w.RemainingPercent), 0.25 * l, shimmer, l);
                PaintHoverLight(ctx, drawn, w.Id, w.Id, 12);
                tiles.Add((r, w.Id));
            }
        }

        MacDraw.Pill(ctx, new Rect(Pad, tilesY + TileH + Gap, Bounds.Width - Pad * 2, PillH), Insights.Build(service, now));
    }

    void PaintMainTile(DrawingContext ctx, Rect r, QuotaWindow? w, string id, DateTimeOffset now)
    {
        MacTile model;
        if (w == null)
        {
            bool waiting = service.Current == null;
            model = new MacTile(id == WindowIds.FiveHour ? "5 小時額度" : "本週額度", id == WindowIds.FiveHour ? TileIcon.Clock : TileIcon.Calendar,
                0, Level.Unknown, waiting ? "讀取中" : "無此限制", waiting ? "正在取得…" : "這個方案沒有此限制", "");
        }
        else
        {
            var level = Levels.Of(w.RemainingPercent, service.Settings);
            model = new MacTile(Insights.Title(w), Insights.Icon(w), ShownValue(w), level,
                Levels.Word(level, w.RemainingPercent), Format.ShortCountdown(w.ResetsAt, now), Insights.RateText(service, w));
        }
        var drawn = Lifted(r, id, out double l);
        double glow = Math.Max(w == null ? 0 : GlowFor(w), 0.25 * l);
        MacDraw.Tile(ctx, drawn, model, glow, shimmer, l);
        PaintHoverLight(ctx, drawn, id, id, 14);
        tiles.Add((r, id));
    }

    void PaintCompact(DrawingContext ctx, DateTimeOffset now)
    {
        var snapshot = service.Current;
        var r = new Rect(6, 6, Bounds.Width - 12, Bounds.Height - 12);
        var w = snapshot?.Tightest;
        MacTile model;
        if (snapshot == null || w == null)
        {
            var insight = Insights.Build(service, now);
            bool failed = insight.Level == Level.Critical;
            model = new MacTile("Claude 額度", TileIcon.Calendar, 0, failed ? Level.Critical : Level.Unknown,
                failed ? "錯誤" : "讀取中", failed ? "查詢失敗" : "正在取得…", "");
        }
        else
        {
            var level = Levels.Of(w.RemainingPercent, service.Settings);
            var other = w.Id == WindowIds.FiveHour ? snapshot.Weekly : snapshot.Session;
            string second = other == null ? Insights.RateText(service, w) : $"{Insights.Title(other)} {Format.Percent(other.RemainingPercent)}";
            model = new MacTile(Insights.Title(w), Insights.Icon(w), ShownValue(w), level, Levels.Word(level, w.RemainingPercent), Format.ShortCountdown(w.ResetsAt, now), second);
        }
        var drawn = Lifted(r, "compact", out double l);
        double glow = Math.Max(w == null ? 0 : GlowFor(w), 0.25 * l);
        MacDraw.Tile(ctx, drawn, model, glow, shimmer, l);
        PaintHoverLight(ctx, drawn, "compact", w?.Id ?? WindowIds.SevenDay, 14);
        tiles.Add((r, "compact"));
        var dot = new Point(drawn.Right - 13, drawn.Y + 13);
        double pop = PopProgress;
        if (pop < 1)
        {
            double ripple = 4 + 11 * (1 - (1 - pop) * (1 - pop));
            ctx.DrawEllipse(null, new Pen(MacTheme.Solid(Color.FromArgb((byte)(200 * (1 - pop)), 255, 255, 255)), 2), dot, ripple, ripple);
        }
        PaintStatusDot(ctx, dot, 4.5);

        // The time of day, on the free right side of the status-word row.
        MacDraw.Text(ctx, "現在 " + Format.Clock(now).Time, MacTheme.Ui(), 9.5, MacDraw.Secondary, new Rect(drawn.Right - 90, drawn.Y + 35, 80, 20), TextAlignment.Right, middle: true);
    }

    /// <summary>The time of day in a small glass chip beside the title; the weekday only when there is room.</summary>
    void PaintClock(DrawingContext ctx, double x, double y, double limit, DateTimeOffset now)
    {
        var (time, day) = Format.Clock(now);
        double timeWidth = MacDraw.Measure(time, MacTheme.Ui(FontWeight.Bold), 11);
        double dayWidth = MacDraw.Measure(day, MacTheme.Ui(), 9.5);
        bool withDay = x + timeWidth + dayWidth + 20 <= limit;
        var chip = new Rect(x, y, timeWidth + (withDay ? dayWidth + 4 : 0) + 14, 22);
        if (chip.Right > limit)
            return;
        ctx.DrawRectangle(MacTheme.Solid(Color.FromArgb(22, 255, 255, 255)), new Pen(MacTheme.Solid(Color.FromArgb(46, 255, 255, 255)), 1), chip, 11, 11);
        MacDraw.Text(ctx, time, MacTheme.Ui(FontWeight.Bold), 11, Colors.White, new Rect(chip.X + 7, chip.Y, timeWidth + 2, chip.Height), middle: true, shadow: false);
        if (withDay)
            MacDraw.Text(ctx, day, MacTheme.Ui(), 9.5, Color.FromRgb(0xC9, 0xC5, 0xE0), new Rect(chip.X + 11 + timeWidth, chip.Y, dayWidth + 2, chip.Height), middle: true, shadow: false);
    }

    Rect Lifted(Rect r, string id, out double amount)
    {
        amount = animate ? lift.GetValueOrDefault(id) : (hover == id ? 1 : 0);
        if (amount <= 0)
            return r;
        return r.Inflate(2.5 * amount).Translate(new Vector(0, -1.5 * amount));
    }

    void PaintHoverLight(DrawingContext ctx, Rect drawn, string id, string windowId, double radius)
    {
        double intensity = animate ? lift.GetValueOrDefault(id) : (hover == id ? 1 : 0);
        if (intensity <= 0.01)
            return;
        var style = MacGlow.For(windowId);
        double phase = animate ? fx.ElapsedMilliseconds % style.LapMs / style.LapMs : -1;
        MacDraw.EdgeGlow(ctx, drawn, radius, phase, style, intensity);
    }

    void PaintStatusDot(DrawingContext ctx, Point c, double radius)
    {
        Color color = service.ErrorCode.Length > 0 ? MacTheme.Danger : service.Fresh ? MacTheme.Good : MacTheme.Warning;
        if (service.Busy && animate)
        {
            double halo = radius * (1.8 + 0.5 * Math.Sin(spin * Math.PI / 90));
            ctx.DrawEllipse(MacTheme.Solid(MacTheme.Alpha(MacTheme.Coral, 90)), null, c, halo, halo);
            color = MacTheme.Coral;
        }
        ctx.DrawEllipse(MacTheme.Solid(color), new Pen(MacTheme.Solid(Color.FromArgb(120, 255, 255, 255)), 1), c, radius, radius);
    }

    /// <summary>
    /// The ">_" workflow button: a quiet outline at rest; while Claude works it fills in violet with the
    /// number of jobs, and turns amber when Claude waits for an answer.
    /// </summary>
    void PaintWorkflowButton(DrawingContext ctx)
    {
        var r = WorkflowButton;
        int running = service.Workflows.Running;
        bool asking = service.Workflows.NeedsAnswer;
        var ink = Compact ? Colors.White : MacTheme.Muted;
        if (running > 0)
        {
            ctx.DrawRectangle(MacTheme.Solid(asking ? Color.FromArgb(220, 0xFB, 0xBF, 0x24) : MacTheme.Alpha(MacTheme.Violet, 200)), null, r, 6, 6);
            ink = asking ? Color.FromRgb(0x3A, 0x2C, 0x14) : Colors.White;
        }
        var pen = new Pen(MacTheme.Solid(ink), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        double cx = r.X + r.Width * 0.3, cy = r.Center.Y;
        ctx.DrawLine(pen, new Point(cx - 3, cy - 4), new Point(cx + 1.5, cy));
        ctx.DrawLine(pen, new Point(cx + 1.5, cy), new Point(cx - 3, cy + 4));
        ctx.DrawLine(pen, new Point(cx + 4, cy + 4.5), new Point(cx + 10, cy + 4.5));
        if (running > 1)
        {
            var badge = new Rect(r.Right - 7, r.Y - 5, 12, 12);
            ctx.DrawEllipse(MacTheme.Solid(MacTheme.Coral), null, badge.Center, 6, 6);
            MacDraw.Text(ctx, running > 9 ? "9+" : running.ToString(), MacTheme.Ui(FontWeight.Bold), 6.5, Colors.White, badge, TextAlignment.Center, middle: true, shadow: false);
        }
    }

    void PaintModeToggle(DrawingContext ctx)
    {
        var r = ModeToggle;
        var pen = new Pen(MacTheme.Solid(Compact ? Colors.White : MacTheme.Muted), 1.6, lineCap: PenLineCap.Round);
        double cx = r.Center.X, cy = r.Center.Y, w = 5, h = 3, dir = Compact ? 1 : -1;
        ctx.DrawLine(pen, new Point(cx - w, cy - h * dir), new Point(cx, cy + h * dir));
        ctx.DrawLine(pen, new Point(cx, cy + h * dir), new Point(cx + w, cy - h * dir));
    }

    void PaintEffects(DrawingContext ctx)
    {
        long now = fx.ElapsedMilliseconds;
        foreach (var e in effects)
        {
            double t = Math.Clamp((now - e.Start) / (double)e.Duration, 0, 1);
            switch (e.Kind)
            {
                case EffectKind.Float:
                {
                    double rise = 1 - (1 - t) * (1 - t);
                    double alpha = 255 * (1 - t * t);
                    var text = MacDraw.Format(e.Text, MacTheme.Ui(FontWeight.Bold), 10.5, MacTheme.Alpha(Colors.White, alpha));
                    var r = new Rect(e.At.X - text.WidthIncludingTrailingWhitespace / 2 - 8, e.At.Y - 26 * rise - text.Height / 2,
                        text.WidthIncludingTrailingWhitespace + 16, text.Height + 2);
                    ctx.DrawRectangle(MacTheme.Solid(Color.FromArgb((byte)(alpha * 200 / 255), 20, 18, 30)), null, r, r.Height / 2, r.Height / 2);
                    ctx.DrawText(text, new Point(r.X + 8, r.Y + 1));
                    break;
                }
                case EffectKind.Ripple:
                {
                    double radius = 6 + 46 * (1 - (1 - t) * (1 - t));
                    var brush = MacTheme.Solid(Color.FromArgb((byte)(80 * (1 - t)), 255, 255, 255));
                    if (e.Clip is { } clip)
                    {
                        using (ctx.PushClip(new RoundedRect(clip, 14)))
                            ctx.DrawEllipse(brush, null, e.At, radius, radius);
                    }
                    else
                    {
                        ctx.DrawEllipse(brush, null, e.At, radius, radius);
                    }
                    break;
                }
                case EffectKind.Confetti:
                {
                    double seconds = (now - e.Start) / 1000.0;
                    double alpha = t < 0.6 ? 255 : 255 * (1 - (t - 0.6) / 0.4);
                    foreach (var p in e.Particles ?? [])
                    {
                        double x = e.At.X + p.Vx * seconds;
                        double y = e.At.Y + p.Vy * seconds + 0.5 * 420 * seconds * seconds;
                        var matrix = Matrix.CreateRotation(p.Spin * seconds * Math.PI / 180) * Matrix.CreateTranslation(x, y);
                        using (ctx.PushTransform(matrix))
                            ctx.DrawRectangle(MacTheme.Solid(MacTheme.Alpha(p.Color, alpha)), null, new Rect(-p.Size / 2, -p.Size * 0.3, p.Size, p.Size * 0.6));
                    }
                    break;
                }
            }
        }
    }

    // ---- --screenshot hooks ---------------------------------------------------------------------

    internal void Settle()
    {
        if (service.Current is { } snapshot)
        {
            foreach (var w in snapshot.Windows)
                shown[w.Id] = w.RemainingPercent;
        }
        window.Opacity = service.Settings.OpacityPercent / 100.0;
        fade.Reset();
        InvalidateVisual();
    }

    internal void ReplayIntro()
    {
        shown.Clear();
        Kick();
    }

    internal void SimulateHover(string? id)
    {
        scriptedHover = true;
        SetHover(id);
    }

    internal void PlayDemoEffects()
    {
        long start = fx.ElapsedMilliseconds;
        popStart = start;
        if (TileFor(WindowIds.SevenDay) is { } weekly)
        {
            var ring = MacDraw.RingCenter(weekly);
            effects.Add(new FxEffect(EffectKind.Float, start, 1300, new Point(ring.X, ring.Y - 20), "−1%"));
        }
        if (TileFor(WindowIds.FiveHour) is { } session)
        {
            var ring = MacDraw.RingCenter(session);
            effects.Add(new FxEffect(EffectKind.Confetti, start, 1500, new Point(session.Center.X, session.Y + session.Height * 0.45), "", Confetti()));
            effects.Add(new FxEffect(EffectKind.Float, start, 1600, new Point(ring.X, ring.Y - 20), "+60% 已恢復"));
        }
        Kick();
    }
}
