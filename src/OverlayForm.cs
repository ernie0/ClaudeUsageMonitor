using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeUsageMonitor;

/// <summary>
/// The always-on-top card plus the tray icon and the shared right-click menu.
/// Each quota is a coloured block (green → violet → amber → red as it runs out) with a ring showing
/// what is left. Animation only runs while something moves: values easing to new numbers, the
/// sparkle spinning during a query, or a critical block breathing. Otherwise nothing repaints.
/// </summary>
internal sealed class OverlayForm : Form
{
    const string UsagePage = "https://claude.ai/settings/usage";

    // Logical layout (96 DPI pixels).
    const float Pad = 10, HeaderH = 28, TileW = 206, TileH = 108, Gap = 8, ScopedW = 120, PillH = 32;
    const float CompactW = 262, CompactH = 120;

    readonly MonitorService service;
    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip menu = new();
    readonly ToolTip tip = new() { InitialDelay = 600, AutoPopDelay = 15000, ReshowDelay = 200 };
    // The clock and the countdowns change once a minute, so one repaint at each minute is enough between animations.
    readonly System.Windows.Forms.Timer ticker = new() { Interval = 60_000 };
    readonly System.Windows.Forms.Timer frame = new() { Interval = 16 };
    readonly Stopwatch fadeClock = new();
    readonly Dictionary<string, double> shown = [];
    readonly List<(RectangleF Bounds, string Id)> tileBounds = [];
    readonly ToolStripMenuItem visibilityItem;
    readonly ToolStripMenuItem modeItem;
    readonly bool animate = Theme.AnimationsEnabled;
    readonly Stopwatch fx = Stopwatch.StartNew();
    readonly List<Effect> effects = [];
    readonly Dictionary<string, float> lift = [];
    readonly Random random = new();
    FontSet? fonts;
    float spin, shimmer = -1, pulse;
    long popStart = -10_000;
    UsageSnapshot? lastSeen;
    string? hoverTile;
    Point dragStart, originStart;
    bool dragging, moved;
    Icon? trayIcon;
    IntPtr trayIconHandle;
    string lastTip = "";

    public OverlayForm(MonitorService service)
    {
        this.service = service;
        Text = service.Demo ? "ClaudeUsageMonitor（示範資料）" : "ClaudeUsageMonitor";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Canvas;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Icon = AppIcon.Load();

        menu.BackColor = Theme.Surface;
        menu.ForeColor = Theme.Text;
        menu.Font = Theme.Ui(9.5f);
        menu.ShowImageMargin = false;
        menu.Renderer = new DarkMenuRenderer();
        ToolStripMenuItem Item(string text, Action action)
        {
            var item = new ToolStripMenuItem(text, null, (_, _) => action()) { Padding = new Padding(6, 3, 6, 3) };
            menu.Items.Add(item);
            return item;
        }
        visibilityItem = Item("隱藏浮窗", ToggleVisible);
        Item("立即更新", () =>
        {
            if (!service.Refresh(manual: true))
                ShowBalloon("請稍候", "10 秒內只能手動更新一次。", ToolTipIcon.Info);
        });
        modeItem = Item("切換為精簡模式", ToggleMode);
        menu.Items.Add(new ToolStripSeparator());
        Item("使用紀錄…", () => HistoryForm.Open(service));
        Item("額度詳情…", () => DetailsForm.Open(service));
        Item("用量分析…", () => AnalysisForm.Open(service));
        Item("設定…", () =>
        {
            if (SettingsForm.Edit(service, this))
                ApplySettings();
        });
        Item("開啟官方用量頁", () => OpenUrl(UsagePage));
        Item("開啟資料夾", () => OpenUrl(service.Root));
        menu.Items.Add(new ToolStripSeparator());
        Item("關於", ShowAbout);
        Item("離開", Close);
        menu.Opening += (_, _) =>
        {
            visibilityItem.Text = Visible ? "隱藏浮窗" : "顯示浮窗";
            modeItem.Text = service.Settings.Compact ? "切換為標準模式" : "切換為精簡模式";
        };
        ContextMenuStrip = menu;

        tray.ContextMenuStrip = menu;
        tray.Text = "Claude 額度";
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleVisible();
        };
        tray.BalloonTipClicked += (_, _) => DetailsForm.Open(service);

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseLeave += (_, _) => SetHover(null);
        MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && !ModeToggleBounds.Contains(e.Location))
                DetailsForm.Open(service);
        };

        service.Changed += OnServiceChanged;
        service.Alert += OnAlert;
        ticker.Tick += (_, _) =>
        {
            ticker.Interval = Format.UntilNextMinute(DateTimeOffset.Now);
            Invalidate();
        };
        frame.Tick += (_, _) => OnFrame();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        Load += (_, _) =>
        {
            ApplySettings();
            RestorePosition();
            UpdateTray();
            tray.Visible = true;
            ticker.Interval = Format.UntilNextMinute(DateTimeOffset.Now);
            ticker.Start();
            service.Start();
            if (service.Settings.StartHidden)
                BeginInvoke(Hide);
            else
                FadeIn();
        };
        HandleCreated += (_, _) => Theme.ApplyWindowChrome(this, rounded: true);
        DpiChanged += (_, _) => ApplySize();
        FormClosing += (_, _) => SavePosition();
        FormClosed += (_, _) =>
        {
            service.Changed -= OnServiceChanged;
            service.Alert -= OnAlert;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            ticker.Dispose();
            frame.Dispose();
            fonts?.Dispose();
            tray.Visible = false;
            tray.Dispose();
            trayIcon?.Dispose();
            if (trayIconHandle != IntPtr.Zero)
                DestroyIcon(trayIconHandle);
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x80 | 0x08000000; // WS_EX_TOOLWINDOW (no Alt+Tab) | WS_EX_NOACTIVATE
            return p;
        }
    }

    float UiScale => DeviceDpi / 96f;
    int S(float logical) => (int)Math.Round(logical * UiScale);
    bool Compact => service.Settings.Compact;
    bool HasScoped => service.Current?.Scoped.Any() == true;
    double TargetOpacity => service.Settings.OpacityPercent / 100.0;

    FontSet Fonts
    {
        get
        {
            if (fonts == null || Math.Abs(fonts.Scale - UiScale) > 0.001f)
            {
                fonts?.Dispose();
                fonts = new FontSet(UiScale);
            }
            return fonts;
        }
    }

    Rectangle ModeToggleBounds => Compact
        ? new Rectangle(Width - S(30), Height - S(28), S(22), S(20))
        : new Rectangle(Width - S(Pad + 22), S(Pad + 3), S(22), S(20));

    public void ApplySettings()
    {
        TopMost = service.Settings.TopMost;
        if (!fadeClock.IsRunning)
            Opacity = TargetOpacity;
        ApplySize();
        UpdateTray();
        Invalidate();
    }

    /// <summary>Resizes for the current mode; a card docked to the right edge stays docked.</summary>
    void ApplySize()
    {
        float width = Compact ? CompactW : Pad * 2 + TileW * 2 + Gap + (HasScoped ? Gap + ScopedW : 0);
        float height = Compact ? CompactH : Pad * 2 + HeaderH + 6 + TileH + Gap + PillH;
        var size = new Size(S(width), S(height));
        if (Size == size)
            return;
        var before = Bounds;
        var area = Screen.FromRectangle(before).WorkingArea;
        bool dockedRight = before.Width > 0 && area.Right - before.Right < S(40);
        Size = size;
        if (dockedRight)
            Left = before.Right - Width;
        Clamp();
    }

    public void RevealFromSecondInstance()
    {
        Show();
        Clamp();
        FadeIn();
    }

    void ToggleVisible()
    {
        if (Visible)
        {
            Hide();
        }
        else
        {
            Show();
            Clamp();
            FadeIn();
        }
    }

    void ToggleMode()
    {
        var before = Bounds;
        var area = Screen.FromControl(this).WorkingArea;
        bool anchoredBottom = area.Bottom - before.Bottom < S(40);
        service.Settings.Compact = !service.Settings.Compact;
        service.SaveSettings();
        ApplySize();
        if (anchoredBottom)
            Top = before.Bottom - Height;
        Clamp();
        SavePosition();
        Kick();
        Invalidate();
    }

    void OnServiceChanged()
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        try
        {
            BeginInvoke(() =>
            {
                ApplySize();
                UpdateTray();
                React();
                Kick();
                Invalidate();
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>A new balloon replaces the previous one, so several alerts are merged into one.</summary>
    void OnAlert(IReadOnlyList<AlertMessage> messages)
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        var icon = messages.Any(m => m.Warning) ? ToolTipIcon.Warning : ToolTipIcon.Info;
        string title = messages.Count == 1 ? messages[0].Title : $"Claude 額度提醒（{messages.Count} 項）";
        string body = messages.Count == 1
            ? messages[0].Body
            : string.Join("\n", messages.Select(m => m.Title.Replace("Claude ", "") + "：" + m.Body));
        try
        {
            BeginInvoke(() => ShowBalloon(title, body, icon));
        }
        catch (InvalidOperationException)
        {
        }
    }

    void ShowBalloon(string title, string body, ToolTipIcon icon) => tray.ShowBalloonTip(8000, title, body, icon);

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            service.Resume();
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            service.SetLocked(true);
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            service.SetLocked(false);
    }

    void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(Clamp);
    }

    // ---- animation ------------------------------------------------------------------------------

    void FadeIn()
    {
        if (!animate)
        {
            Opacity = TargetOpacity;
            return;
        }
        Opacity = 0;
        fadeClock.Restart();
        Kick();
    }

    /// <summary>Starts the frame timer; it stops itself as soon as nothing is moving.</summary>
    void Kick()
    {
        if (animate)
        {
            frame.Start();
            return;
        }
        // Reduced motion: jump straight to the real values.
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
                    // Ease out: fast at first, settling gently on the real number.
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
            spin = (spin + 4f) % 360;
            shimmer = shimmer < 0 ? 0 : (shimmer + 0.016f) % 1f;
        }
        else
        {
            shimmer = -1;
        }

        bool critical = CriticalMain();
        if (critical)
            pulse = (pulse + 0.045f) % (MathF.PI * 2);

        bool fading = fadeClock.IsRunning;
        if (fading)
        {
            double t = Math.Min(1, fadeClock.ElapsedMilliseconds / 320.0);
            Opacity = TargetOpacity * (1 - Math.Pow(1 - t, 3));
            if (t >= 1)
                fadeClock.Reset();
        }

        // Hovered blocks lift towards 1, the others settle back to 0.
        bool lifting = false;
        foreach (var (_, id) in tileBounds)
        {
            float current = lift.GetValueOrDefault(id), target = id == hoverTile ? 1 : 0;
            if (Math.Abs(target - current) > 0.01f)
            {
                // Gentle ease so the lift and its edge light fade in and out over about a quarter second.
                lift[id] = current + (target - current) * 0.14f;
                lifting = true;
            }
            else
            {
                lift[id] = target;
            }
        }

        long now = fx.ElapsedMilliseconds;
        effects.RemoveAll(e => now - e.Start > e.Duration);
        // The edge light keeps running only while the pointer is on a block.
        bool effecting = effects.Count > 0 || now - popStart < PopMs || hoverTile != null;

        Invalidate();
        if (!tweening && !loading && !critical && !fading && !lifting && !effecting)
            frame.Stop();
        else
            frame.Interval = tweening || loading || fading || lifting || effecting ? 16 : 50;
    }

    // ---- little effects -------------------------------------------------------------------------

    const long PopMs = 700;

    enum EffectKind { Float, Ripple, Confetti }

    sealed record Particle(float Vx, float Vy, Color Color, float Size, float Spin);

    sealed record Effect(EffectKind Kind, long Start, long Duration, PointF At, string Text = "", Particle[]? Particles = null, RectangleF? Clip = null);

    /// <summary>
    /// Compares the new observation with the last one shown and plays a matching effect:
    /// a "−1%" that floats up when quota is used, confetti when a window comes back,
    /// and a sparkle pop on every successful refresh.
    /// </summary>
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

        float s = UiScale;
        foreach (var w in snapshot.Windows)
        {
            if (previous.Find(w.Id) is not { } before)
                continue;
            double delta = Math.Round(w.RemainingPercent - before.RemainingPercent);
            if (delta == 0 || TileFor(w.Id) is not { } tile)
                continue;
            var ring = TilePainter.RingCenter(tile, s);
            long start = fx.ElapsedMilliseconds;
            if (delta >= 10)
            {
                effects.Add(new Effect(EffectKind.Confetti, start, 1500, new PointF(tile.X + tile.Width / 2, tile.Y + tile.Height * 0.45f), "", Confetti(s)));
                effects.Add(new Effect(EffectKind.Float, start, 1600, new PointF(ring.X, ring.Y - 20 * s), $"+{delta:0}% 已恢復"));
            }
            else
            {
                effects.Add(new Effect(EffectKind.Float, start, 1300, new PointF(ring.X, ring.Y - 20 * s), (delta > 0 ? "+" : "−") + $"{Math.Abs(delta):0}%"));
            }
        }
    }

    /// <summary>Where a window's block is drawn now; in compact mode only the shown window has one.</summary>
    RectangleF? TileFor(string id)
    {
        string key = !Compact ? id : service.Current?.Tightest?.Id == id ? "compact" : "";
        foreach (var (bounds, tileId) in tileBounds)
        {
            if (tileId == key)
                return bounds;
        }
        return null;
    }

    Particle[] Confetti(float s)
    {
        Color[] colors = [Theme.Coral, Theme.Violet, Theme.Good, Theme.Warning, Color.White, Color.FromArgb(0x38, 0xBD, 0xF8)];
        return Enumerable.Range(0, 28).Select(_ =>
        {
            double angle = random.NextDouble() * Math.PI * 2;
            float speed = (float)(90 + random.NextDouble() * 150) * s;
            return new Particle((float)Math.Cos(angle) * speed, (float)Math.Sin(angle) * speed - 120 * s,
                colors[random.Next(colors.Length)], (3 + (float)random.NextDouble() * 3) * s, (float)(random.NextDouble() * 720 - 360));
        }).ToArray();
    }

    void PaintEffects(Graphics g)
    {
        float s = UiScale;
        long now = fx.ElapsedMilliseconds;
        foreach (var e in effects)
        {
            float t = Math.Clamp((now - e.Start) / (float)e.Duration, 0, 1);
            switch (e.Kind)
            {
                case EffectKind.Float:
                {
                    float rise = 1 - (1 - t) * (1 - t);
                    int alpha = (int)(255 * (1 - t * t));
                    var size = g.MeasureString(e.Text, Fonts.Title);
                    var r = new RectangleF(e.At.X - size.Width / 2 - 8 * s, e.At.Y - 26 * s * rise - size.Height / 2, size.Width + 16 * s, size.Height + 2 * s);
                    using var path = Theme.RoundedRect(r, r.Height / 2);
                    using (var back = new SolidBrush(Color.FromArgb(alpha * 200 / 255, 20, 18, 30)))
                        g.FillPath(back, path);
                    using var text = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
                    using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    var hint = g.TextRenderingHint;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.DrawString(e.Text, Fonts.Title, text, r, format);
                    g.TextRenderingHint = hint;
                    break;
                }
                case EffectKind.Ripple:
                {
                    float radius = (6 + 46 * (1 - (1 - t) * (1 - t))) * s;
                    var clip = g.Clip;
                    if (e.Clip is { } bounds)
                    {
                        using var path = Theme.RoundedRect(bounds, 14 * s);
                        g.SetClip(path, CombineMode.Intersect);
                    }
                    using (var brush = new SolidBrush(Color.FromArgb((int)(80 * (1 - t)), 255, 255, 255)))
                        g.FillEllipse(brush, e.At.X - radius, e.At.Y - radius, radius * 2, radius * 2);
                    g.Clip = clip;
                    break;
                }
                case EffectKind.Confetti:
                {
                    float seconds = (now - e.Start) / 1000f;
                    int alpha = t < 0.6f ? 255 : (int)(255 * (1 - (t - 0.6f) / 0.4f));
                    foreach (var p in e.Particles ?? [])
                    {
                        float x = e.At.X + p.Vx * seconds;
                        float y = e.At.Y + p.Vy * seconds + 0.5f * 420 * s * seconds * seconds;
                        var state = g.Save();
                        g.TranslateTransform(x, y);
                        g.RotateTransform(p.Spin * seconds);
                        using var brush = new SolidBrush(Color.FromArgb(alpha, p.Color));
                        g.FillRectangle(brush, -p.Size / 2, -p.Size * 0.3f, p.Size, p.Size * 0.6f);
                        g.Restore(state);
                    }
                    break;
                }
            }
        }
    }

    /// <summary>0..1 progress of the sparkle pop after a refresh, or 1 when idle.</summary>
    float PopProgress => Math.Clamp((fx.ElapsedMilliseconds - popStart) / (float)PopMs, 0, 1);

    bool CriticalMain() =>
        Visible && service.Current is { } s
        && new[] { s.Session, s.Weekly }.OfType<QuotaWindow>()
            .Any(w => w.RemainingPercent > 0 && w.RemainingPercent <= service.Settings.CriticalRemaining);

    double ShownValue(QuotaWindow w) => animate ? shown.GetValueOrDefault(w.Id, 0) : w.RemainingPercent;

    float GlowFor(QuotaWindow w) =>
        w.RemainingPercent > 0 && w.RemainingPercent <= service.Settings.CriticalRemaining
            ? (MathF.Sin(pulse) + 1) / 2
            : 0;

    // ---- mouse ----------------------------------------------------------------------------------

    void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        if (ModeToggleBounds.Contains(e.Location))
        {
            ToggleMode();
            return;
        }
        foreach (var (bounds, _) in tileBounds)
        {
            if (animate && bounds.Contains(e.Location))
            {
                effects.Add(new Effect(EffectKind.Ripple, fx.ElapsedMilliseconds, 450, e.Location, Clip: bounds));
                Kick();
                break;
            }
        }
        dragging = true;
        moved = false;
        dragStart = Cursor.Position;
        originStart = Location;
        Capture = true;
    }

    void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (dragging && MouseButtons == MouseButtons.Left)
        {
            var delta = new Size(Cursor.Position.X - dragStart.X, Cursor.Position.Y - dragStart.Y);
            if (Math.Abs(delta.Width) + Math.Abs(delta.Height) > 2)
                moved = true;
            Location = originStart + delta;
            return;
        }
        if (!scriptedHover)
            SetHover(tileBounds.FirstOrDefault(t => t.Bounds.Contains(e.Location)).Id);
        // Pointing at the sparkle makes it twinkle.
        if (animate && !Compact && e.Y < S(Pad + HeaderH) && e.X < S(Pad + 120) && fx.ElapsedMilliseconds - popStart > 1200)
        {
            popStart = fx.ElapsedMilliseconds;
            Kick();
        }
        string text = ModeToggleBounds.Contains(e.Location)
            ? (Compact ? "切換為標準模式" : "切換為精簡模式")
            : TooltipText();
        if (text != lastTip)
        {
            lastTip = text;
            tip.SetToolTip(this, text);
        }
    }

    void SetHover(string? id)
    {
        if (id == hoverTile)
            return;
        hoverTile = id;
        Kick();
        Invalidate();
    }

    /// <summary>
    /// The quota's own edge light. Its strength follows the hover lift, so it fades in when the
    /// pointer arrives and only fades away after the pointer has left.
    /// </summary>
    void PaintHoverLight(Graphics g, RectangleF drawn, string id, string windowId, float radius)
    {
        float intensity = animate ? lift.GetValueOrDefault(id) : (hoverTile == id ? 1 : 0);
        if (intensity <= 0.01f)
            return;
        var style = GlowStyle.For(windowId);
        float phase = animate ? fx.ElapsedMilliseconds % style.LapMs / style.LapMs : -1;
        TilePainter.EdgeGlow(g, drawn, radius, phase, UiScale, style, intensity);
    }

    void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (!dragging)
            return;
        dragging = false;
        Capture = false;
        if (moved)
        {
            Clamp();
            SavePosition();
        }
    }

    void RestorePosition()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = service.Settings.WindowX is int x && service.Settings.WindowY is int y
            ? new Point(x, y)
            : new Point(area.Right - Width - S(24), area.Bottom - Height - S(24));
        Clamp();
    }

    void SavePosition()
    {
        service.Settings.WindowX = Left;
        service.Settings.WindowY = Top;
        service.SaveSettings();
    }

    void Clamp()
    {
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    // ---- painting -------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        TilePainter.Prepare(g);
        var client = new RectangleF(0, 0, Width, Height);
        using (var back = new LinearGradientBrush(client, Theme.Canvas, Theme.CanvasEnd, LinearGradientMode.Vertical))
            g.FillRectangle(back, client);
        using (var border = new Pen(Theme.Alpha(Theme.Border, 200)))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        tileBounds.Clear();
        var now = DateTimeOffset.UtcNow;
        if (Compact)
            PaintCompact(g, now);
        else
            PaintStandard(g, now);
        PaintModeToggle(g);
        PaintEffects(g);
    }

    void PaintStandard(Graphics g, DateTimeOffset now)
    {
        var f = Fonts;
        float s = UiScale;
        var snapshot = service.Current;

        // Brand stripe and header: the sparkle says "AI", the title says what this is.
        using (var stripe = new LinearGradientBrush(new RectangleF(0, 0, Width, 3 * s), Theme.Coral, Theme.Violet, LinearGradientMode.Horizontal))
            g.FillRectangle(stripe, 0, 0, Width, 3 * s);
        float headerY = Pad * s;
        // After every refresh the sparkle pops (grows and turns a quarter) — a small "got it".
        float pop = PopProgress;
        float bounce = pop < 1 ? MathF.Sin(pop * MathF.PI) : 0;
        float turn = 90 * (1 - (1 - pop) * (1 - pop));
        using (var brand = new LinearGradientBrush(new RectangleF(Pad * s, headerY, 22 * s, 22 * s), Theme.Coral, Theme.Violet, 45f))
            Glyphs.Sparkle(g, new PointF((Pad + 10) * s, headerY + 13 * s), 10f * s * (1 + 0.5f * bounce), brand, spin + turn);
        g.DrawString("Claude 額度", f.Header, Brushes.White, new PointF((Pad + 24) * s, headerY + 3 * s));

        string meta = snapshot == null ? "" : $"{snapshot.PlanLabel} · {snapshot.ObservedAt.ToLocalTime():HH:mm} 更新";
        var metaSize = g.MeasureString(meta, f.Small);
        float metaX = ModeToggleBounds.Left - 8 * s - metaSize.Width;
        using (var metaBrush = new SolidBrush(Color.FromArgb(0xC9, 0xC5, 0xE0)))
            g.DrawString(meta, f.Small, metaBrush, new PointF(metaX, headerY + 6 * s));
        var dot = new PointF(metaX - 10 * s, headerY + 14 * s);
        PaintClock(g, (Pad + 24) * s + g.MeasureString("Claude 額度", f.Header).Width + 6 * s, headerY + 2 * s, dot.X - 12 * s, now);
        if (pop < 1)
        {
            float ripple = (4 + 12 * (1 - (1 - pop) * (1 - pop))) * s;
            using var ring = new Pen(Theme.Alpha(Theme.Good, (int)(170 * (1 - pop))), 2 * s);
            g.DrawEllipse(ring, dot.X - ripple, dot.Y - ripple, ripple * 2, ripple * 2);
        }
        PaintStatusDot(g, dot, 4.5f * s);

        float tilesY = (Pad + HeaderH + 6) * s;
        var sessionRect = new RectangleF(Pad * s, tilesY, TileW * s, TileH * s);
        var weeklyRect = new RectangleF((Pad + TileW + Gap) * s, tilesY, TileW * s, TileH * s);
        PaintMainTile(g, sessionRect, snapshot?.Session, WindowIds.FiveHour, now);
        PaintMainTile(g, weeklyRect, snapshot?.Weekly, WindowIds.SevenDay, now);

        if (snapshot != null && HasScoped)
        {
            var scoped = snapshot.Scoped.OrderBy(w => w.RemainingPercent).Take(2).ToList();
            float x = (Pad + TileW * 2 + Gap * 2) * s;
            float h = (TileH * s - (scoped.Count - 1) * Gap * s) / scoped.Count;
            for (int i = 0; i < scoped.Count; i++)
            {
                var w = scoped[i];
                var r = new RectangleF(x, tilesY + i * (h + Gap * s), ScopedW * s, h);
                var level = Levels.Of(w.RemainingPercent, service.Settings);
                var drawn = Lifted(g, r, w.Id, 12 * s, out float l);
                TilePainter.Mini(g, drawn, w.Label.Replace("本週·", ""), ShownValue(w), level, Levels.Word(level, w.RemainingPercent), f, 0.25f * l, shimmer);
                PaintHoverLight(g, drawn, w.Id, w.Id, 12 * s);
                tileBounds.Add((r, w.Id));
            }
        }

        var pill = new RectangleF(Pad * s, tilesY + (TileH + Gap) * s, Width - Pad * 2 * s, PillH * s);
        TilePainter.Pill(g, pill, Insights.Build(service, now), f);
    }

    void PaintMainTile(Graphics g, RectangleF r, QuotaWindow? w, string id, DateTimeOffset now)
    {
        TileModel model;
        if (w == null)
        {
            bool waiting = service.Current == null;
            model = new TileModel(id == WindowIds.FiveHour ? "5 小時額度" : "本週額度",
                id == WindowIds.FiveHour ? TileIcon.Clock : TileIcon.Calendar,
                0, Level.Unknown, waiting ? "讀取中" : "無此限制", waiting ? "正在取得…" : "這個方案沒有此限制", "");
        }
        else
        {
            var level = Levels.Of(w.RemainingPercent, service.Settings);
            model = new TileModel(Insights.Title(w), Insights.Icon(w), ShownValue(w), level,
                Levels.Word(level, w.RemainingPercent), Format.ShortCountdown(w.ResetsAt, now), Insights.RateText(service, w));
        }
        var drawn = Lifted(g, r, id, 14 * UiScale, out float l);
        float glow = Math.Max(w == null ? 0 : GlowFor(w), 0.25f * l);
        TilePainter.Tile(g, drawn, model, Fonts, glow, shimmer);
        PaintHoverLight(g, drawn, id, id, 14 * UiScale);
        tileBounds.Add((r, id));
    }

    /// <summary>A hovered block grows a little, rises and casts a shadow; hit-testing keeps the resting rect.</summary>
    RectangleF Lifted(Graphics g, RectangleF r, string id, float radius, out float amount)
    {
        amount = animate ? lift.GetValueOrDefault(id) : (hoverTile == id ? 1 : 0);
        if (amount <= 0)
            return r;
        float s = UiScale;
        var drawn = RectangleF.Inflate(r, 2.5f * s * amount, 2.5f * s * amount);
        drawn.Offset(0, -1.5f * s * amount);
        TilePainter.Shadow(g, drawn, radius, amount, s);
        return drawn;
    }

    void PaintCompact(Graphics g, DateTimeOffset now)
    {
        var f = Fonts;
        float s = UiScale;
        var snapshot = service.Current;
        var r = new RectangleF(6 * s, 6 * s, Width - 12 * s, Height - 12 * s);
        var w = snapshot?.Tightest;
        TileModel model;
        if (snapshot == null || w == null)
        {
            var insight = Insights.Build(service, now);
            model = new TileModel("Claude 額度", TileIcon.Calendar, 0, insight.Level == Level.Critical ? Level.Critical : Level.Unknown,
                insight.Level == Level.Critical ? "錯誤" : "讀取中", insight.Level == Level.Critical ? "查詢失敗" : "正在取得…", "");
        }
        else
        {
            var level = Levels.Of(w.RemainingPercent, service.Settings);
            var other = w.Id == WindowIds.FiveHour ? snapshot.Weekly : snapshot.Session;
            string second = other == null ? Insights.RateText(service, w) : $"{Insights.Title(other)} {Format.Percent(other.RemainingPercent)}";
            model = new TileModel(Insights.Title(w), Insights.Icon(w), ShownValue(w), level,
                Levels.Word(level, w.RemainingPercent), Format.ShortCountdown(w.ResetsAt, now), second);
        }
        var drawn = Lifted(g, r, "compact", 14 * s, out float l);
        float glow = Math.Max(w == null ? 0 : GlowFor(w), 0.25f * l);
        TilePainter.Tile(g, drawn, model, f, glow, shimmer);
        PaintHoverLight(g, drawn, "compact", w?.Id ?? WindowIds.SevenDay, 14 * s);
        tileBounds.Add((r, "compact"));
        var dot = new PointF(drawn.Right - 13 * s, drawn.Y + 13 * s);
        float pop = PopProgress;
        if (pop < 1)
        {
            float ripple = (4 + 11 * (1 - (1 - pop) * (1 - pop))) * s;
            using var ring = new Pen(Color.FromArgb((int)(200 * (1 - pop)), 255, 255, 255), 2 * s);
            g.DrawEllipse(ring, dot.X - ripple, dot.Y - ripple, ripple * 2, ripple * 2);
        }
        PaintStatusDot(g, dot, 4.5f * s);

        // The time of day, on the free right side of the status-word row.
        var (time, _) = Format.Clock(now);
        using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        TilePainter.Text(g, "現在 " + time, f.Small, TilePainter.Secondary, new RectangleF(drawn.Right - 90 * s, drawn.Y + 35 * s, 80 * s, 20 * s), right, s);
    }

    /// <summary>The time of day in a small glass chip beside the title; the weekday only when there is room.</summary>
    void PaintClock(Graphics g, float x, float y, float limit, DateTimeOffset now)
    {
        var f = Fonts;
        float s = UiScale;
        var (time, day) = Format.Clock(now);
        float timeWidth = g.MeasureString(time, f.Header).Width;
        float dayWidth = g.MeasureString(day, f.Small).Width;
        bool withDay = x + timeWidth + dayWidth + 12 * s <= limit;
        var chip = new RectangleF(x, y, timeWidth + (withDay ? dayWidth : 0) + 10 * s, 22 * s);
        if (chip.Right > limit)
            return;
        using (var path = Theme.RoundedRect(chip, chip.Height / 2))
        {
            using var fill = new SolidBrush(Color.FromArgb(22, 255, 255, 255));
            g.FillPath(fill, path);
            using var edge = new Pen(Color.FromArgb(46, 255, 255, 255), 1);
            g.DrawPath(edge, path);
        }
        g.DrawString(time, f.Header, Brushes.White, new PointF(chip.X + 4 * s, chip.Y + 1 * s));
        if (withDay)
        {
            using var muted = new SolidBrush(Color.FromArgb(0xC9, 0xC5, 0xE0));
            g.DrawString(day, f.Small, muted, new PointF(chip.X + 4 * s + timeWidth - 2 * s, chip.Y + 3 * s));
        }
    }

    /// <summary>Green: fresh; amber: stale; red: failing. Spins a halo while querying.</summary>
    void PaintStatusDot(Graphics g, PointF c, float radius)
    {
        Color color = service.ErrorCode.Length > 0 ? Theme.Danger : service.Fresh ? Theme.Good : Theme.Warning;
        if (service.Busy && animate)
        {
            float halo = radius * (1.8f + 0.5f * MathF.Sin(spin * MathF.PI / 90));
            using var ring = new SolidBrush(Theme.Alpha(Theme.Coral, 90));
            g.FillEllipse(ring, c.X - halo, c.Y - halo, halo * 2, halo * 2);
            color = Theme.Coral;
        }
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, c.X - radius, c.Y - radius, radius * 2, radius * 2);
        using var edge = new Pen(Color.FromArgb(120, 255, 255, 255), 1);
        g.DrawEllipse(edge, c.X - radius, c.Y - radius, radius * 2, radius * 2);
    }

    void PaintModeToggle(Graphics g)
    {
        var r = ModeToggleBounds;
        using var pen = new Pen(Compact ? Color.White : Theme.Muted, Math.Max(1.4f, 1.6f * UiScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, w = S(5), h = S(3);
        // Compact shows "expand" (chevron down), standard shows "collapse" (chevron up).
        float dir = Compact ? 1 : -1;
        g.DrawLines(pen, [new PointF(cx - w, cy - h * dir), new PointF(cx, cy + h * dir), new PointF(cx + w, cy - h * dir)]);
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
            string rate = service.Forecasts.GetValueOrDefault(w.Id)?.RatePerHour is { } rph ? $" · 燒速 {Format.Rate(rph)}" : "";
            lines.Add($"{Insights.Title(w)}：剩 {Format.Percent(w.RemainingPercent)}（已用 {w.UsedPercent:0}%）· {reset}{rate}");
        }
        lines.Add(service.Status);
        lines.Add("右鍵開選單 · 雙擊看詳情 · 可拖曳");
        return string.Join("\n", lines);
    }

    // ---- tray -----------------------------------------------------------------------------------

    void UpdateTray()
    {
        var snapshot = service.Current;
        var window = snapshot?.Tightest;
        string value = window == null ? "?" : Math.Floor(window.RemainingPercent).ToString("0");
        var level = window == null || !service.Fresh ? Level.Unknown : Levels.Of(window.RemainingPercent, service.Settings);

        using var bitmap = RenderTrayBitmap(value, level, SystemInformation.SmallIconSize.Width * 2);
        IntPtr handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);
        tray.Icon = icon;
        trayIcon?.Dispose();
        if (trayIconHandle != IntPtr.Zero)
            DestroyIcon(trayIconHandle);
        trayIcon = icon;
        trayIconHandle = handle;

        string tipText = snapshot == null
            ? "Claude 額度 · " + service.Status
            : $"Claude 額度｜5 小時 {Pct(snapshot.Session)}｜本週 {Pct(snapshot.Weekly)}" + (service.Fresh ? "" : "｜上次資料");
        tray.Text = tipText.Length > 63 ? tipText[..63] : tipText;
    }

    static string Pct(QuotaWindow? w) => w == null ? "—" : Format.Percent(w.RemainingPercent);

    /// <summary>The remaining percentage as a white number on a tile in the level's gradient.</summary>
    internal static Bitmap RenderTrayBitmap(string value, Level level, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var r = new RectangleF(0, 0, size, size);
        var (start, end) = Theme.Gradient(level);
        using var brush = new LinearGradientBrush(r, start, end, 45f);
        using var path = Theme.RoundedRect(r, size * 0.24f);
        g.FillPath(brush, path);
        float em = value.Length >= 3 ? size * 0.44f : size * 0.62f;
        using var font = new Font(Theme.NumberFamily, em, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(value, font, Brushes.White, new RectangleF(0, size * 0.03f, size, size), format);
        return bitmap;
    }

    void ShowAbout()
    {
        string version = Application.ProductVersion.Split('+')[0];
        MessageBox.Show(this,
            $"ClaudeUsageMonitor {version}\n\n" +
            "非官方工具，不隸屬 Anthropic，也未受其背書。\n" +
            "額度由電腦上已登入的官方 Claude Code 查詢（不送出任何提問、不耗用額度），\n" +
            "紀錄只存在本機：" + service.Root + "\n\n" +
            "設計參考 hayabusasean/CodexUsageMonitor（MIT 授權）。",
            "關於 ClaudeUsageMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    internal static void OpenUrl(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>Points the hover at a block (or none) as if the mouse were there; used by --screenshot.</summary>
    internal void SimulateHover(string? id)
    {
        scriptedHover = true;
        SetHover(id);
    }

    /// <summary>Set by --screenshot so a real pointer resting over the window cannot override the scripted hover.</summary>
    bool scriptedHover;

    /// <summary>Plays the "quota used", "reset confetti" and hover effects at once; used by --screenshot.</summary>
    internal void PlayDemoEffects()
    {
        float s = UiScale;
        long start = fx.ElapsedMilliseconds;
        popStart = start;
        if (TileFor(WindowIds.SevenDay) is { } weekly)
        {
            var ring = TilePainter.RingCenter(weekly, s);
            effects.Add(new Effect(EffectKind.Float, start, 1300, new PointF(ring.X, ring.Y - 20 * s), "−1%"));
        }
        if (TileFor(WindowIds.FiveHour) is { } session)
        {
            var ring = TilePainter.RingCenter(session, s);
            effects.Add(new Effect(EffectKind.Confetti, start, 1500, new PointF(session.X + session.Width / 2, session.Y + session.Height * 0.45f), "", Confetti(s)));
            effects.Add(new Effect(EffectKind.Float, start, 1600, new PointF(ring.X, ring.Y - 20 * s), "+60% 已恢復"));
        }
        hoverTile = service.Current?.Scoped.FirstOrDefault()?.Id;
        Kick();
    }

    /// <summary>Restarts the rings from zero; used by --screenshot to capture mid-animation frames.</summary>
    internal void ReplayIntro()
    {
        shown.Clear();
        Kick();
    }

    /// <summary>Switches the layout without saving it, with animation settled; used by --screenshot.</summary>
    internal void ShowMode(bool compact)
    {
        service.Settings.Compact = compact;
        ApplySize();
        if (service.Current is { } snapshot)
        {
            foreach (var w in snapshot.Windows)
                shown[w.Id] = w.RemainingPercent;
        }
        Opacity = TargetOpacity;
        fadeClock.Reset();
        Invalidate();
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.Coral;
        public override Color MenuItemSelected => Theme.Raised;
        public override Color MenuItemSelectedGradientBegin => Theme.Raised;
        public override Color MenuItemSelectedGradientEnd => Theme.Raised;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
    }
}

internal static class AppIcon
{
    public static Icon? Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("ClaudeUsageMonitor.app.ico");
        return stream == null ? null : new Icon(stream);
    }
}
