using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using SD = System.Drawing;

namespace ClaudeUsageMonitor;

/// <summary>
/// The small workflow window (layout in Core/WorkflowMini.cs), the same as on Windows: it floats on top,
/// takes an instruction on one line and grows a short row for each open conversation with the buttons
/// that matter. It comes back by itself, without taking the keyboard, when Claude waits for an answer.
/// </summary>
internal sealed class WorkflowMiniWindow : Window
{
    readonly MonitorService service;
    readonly MacShell shell;
    readonly MiniControl page;
    readonly Canvas overlay = new();
    readonly TextBox input = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = MacTheme.Solid(MacTheme.Text),
        FontFamily = MacTheme.UiFont,
        FontSize = MacDraw.Pt(10.5),
        Padding = new Thickness(0),
        MinHeight = 0,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    List<ClaudeSession> sessions = [];
    ClaudeSession? replyTo;
    string folder, mode;

    public WorkflowMiniWindow(MonitorService service, MacShell shell)
    {
        this.service = service;
        this.shell = shell;
        folder = Directory.Exists(service.Settings.WorkflowFolder) ? service.Settings.WorkflowFolder
            : RecentProjects.Load().FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        mode = service.Settings.WorkflowMode;
        Title = "Claude 工作流";
        Icon = MacShell.AppIcon();
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = service.Settings.TopMost;
        Background = MacTheme.Solid(Color.FromRgb(0x0B, 0x10, 0x22));
        Width = WorkflowMini.Width;
        Height = 92;
        page = new MiniControl(this);
        ShowPlaceholder();
        // The page draws the field; the text box keeps no frame or fill of its own, focused or not.
        foreach (string state in new[] { ":focus", ":pointerover" })
        {
            input.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<TextBox>().Class(state).Template().OfType<Border>().Name("PART_BorderElement"))
            {
                Setters =
                {
                    new Avalonia.Styling.Setter(Border.BorderThicknessProperty, new Thickness(0)),
                    new Avalonia.Styling.Setter(Border.BackgroundProperty, Brushes.Transparent),
                },
            });
        }
        input.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        overlay.Children.Add(input);
        Content = new Panel { Children = { page, overlay } };
        Action onChanged = () => Dispatcher.UIThread.Post(page.InvalidateVisual);
        service.Workflows.Changed += onChanged;
        Closed += (_, _) => service.Workflows.Changed -= onChanged;
        PositionChanged += (_, _) =>
        {
            service.Settings.WorkflowX = Position.X;
            service.Settings.WorkflowY = Position.Y;
        };
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Submit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (replyTo != null)
                Target(null);
            else
                Hide();
        }
    }

    /// <summary>Below the overlay (it sits under the menu bar on a Mac), or above it when there is no room; a dragged position wins.</summary>
    public void Place(Window near)
    {
        var screen = Screens.ScreenFromWindow(near) ?? Screens.Primary;
        if (screen == null)
            return;
        double scale = screen.Scaling;
        var area = screen.WorkingArea;
        int width = (int)(Width * scale), height = (int)(Height * scale);
        var position = service.Settings.WorkflowX is int x && service.Settings.WorkflowY is int y
            ? new PixelPoint(x, y)
            : near.Position.Y + (int)(near.Bounds.Height * scale) + height + 10 < area.Bottom
                ? new PixelPoint(near.Position.X + (int)(near.Bounds.Width * scale) - width, near.Position.Y + (int)(near.Bounds.Height * scale) + 10)
                : new PixelPoint(near.Position.X + (int)(near.Bounds.Width * scale) - width, near.Position.Y - height - 10);
        Position = new PixelPoint(Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - width)), Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    public void FocusInput()
    {
        Activate();
        input.Focus();
    }

    /// <summary>Draws through the shared layout, keeps the text box on its field and the window as tall as the content.</summary>
    internal void Draw(IPaint paint, PageState state)
    {
        sessions = service.Workflows.Sessions.ToList();
        if (replyTo != null && !sessions.Contains(replyTo))
            Target(null);
        float height = WorkflowMini.Render(paint, state, new WorkflowMiniData(folder, mode, sessions, replyTo), out var box);
        Dispatcher.UIThread.Post(() =>
        {
            Canvas.SetLeft(input, box.X);
            Canvas.SetTop(input, box.Y);
            input.Width = box.Width;
            input.Height = box.Height;
            if (Math.Abs(Height - height) > 0.5)
                Height = height;
        });
    }

    /// <summary>Sets the folder and the instruction; used by --screenshot.</summary>
    internal void Prepare(string path, string text)
    {
        folder = path;
        input.Text = text;
        page.InvalidateVisual();
    }

    internal ClaudeSession? Submit()
    {
        string text = (input.Text ?? "").Trim();
        if (text.Length == 0)
            return null;
        if (replyTo is { State: JobState.Waiting } target)
        {
            target.Send(text);
            input.Text = "";
            Target(null);
            return target;
        }
        if (!Directory.Exists(folder))
        {
            MacDialog.Show(this, "Claude 工作流", "找不到這個資料夾，請重新選擇。");
            return null;
        }
        try
        {
            var session = service.Workflows.Start(folder, mode, text);
            input.Text = "";
            service.Settings.WorkflowFolder = folder;
            service.SaveSettings();
            return session;
        }
        catch (MonitorException e)
        {
            MacDialog.Show(this, "無法啟動 Claude Code", MonitorService.Explain(e.Code));
            return null;
        }
    }

    void Target(ClaudeSession? session)
    {
        replyTo = session;
        ShowPlaceholder();
        page.InvalidateVisual();
    }

    void ShowPlaceholder() => input.Watermark = replyTo is { } s
        ? $"回覆「{s.Title}」…（Esc 取消）"
        : "要 Claude 做什麼？（Enter 送出）";

    internal void Act(string action)
    {
        int colon = action.IndexOf(':');
        var session = colon > 0 && int.TryParse(action[(colon + 1)..], out int i) && i >= 0 && i < sessions.Count ? sessions[i] : null;
        switch (colon > 0 ? action[..colon] : action)
        {
            case "input":
                FocusInput();
                break;
            case "folder":
                ShowFolders();
                break;
            case "mode":
                ShowModes();
                break;
            case "detail":
            case "open":
                shell.OpenWorkflowDetail();
                break;
            case "hide":
                Hide();
                break;
            case "allow":
                session?.Answer(true);
                break;
            case "deny":
                session?.Answer(false);
                break;
            case "stop":
                session?.Interrupt();
                break;
            case "reply":
                Target(session);
                FocusInput();
                break;
            case "close" when session != null:
                service.Workflows.Close(session);
                break;
        }
    }

    void ShowMenu(IEnumerable<(string Text, bool Checked, Action Action)> items)
    {
        var menu = new ContextMenu();
        foreach (var (text, check, action) in items)
        {
            if (text == "-")
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = (check ? "✓ " : "　 ") + text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        menu.Open(page);
    }

    void ShowFolders()
    {
        var items = RecentProjects.Load()
            .Select(path => (Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) + "　" + path, string.Equals(path, folder, StringComparison.OrdinalIgnoreCase), (Action)(() => SetFolder(path))))
            .ToList();
        if (items.Count > 0)
            items.Add(("-", false, () => { }));
        items.Add(("其他資料夾…", false, () => _ = PickFolderAsync()));
        ShowMenu(items);
    }

    async Task PickFolderAsync()
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(folder);
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Claude 要在哪個資料夾工作？", AllowMultiple = false, SuggestedStartLocation = start });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            SetFolder(path);
    }

    void SetFolder(string path)
    {
        folder = path;
        page.InvalidateVisual();
    }

    void ShowModes() => ShowMenu(PermissionModes.All.Select(m => ($"{m.Name}　{m.Note}", m.Id == mode, (Action)(() => _ = SetModeAsync(m.Id)))));

    async Task SetModeAsync(string id)
    {
        if (id == PermissionModes.Everything && mode != id
            && !await MacDialog.Confirm(this, "Claude 工作流", "「全部允許」會讓 Claude 不經詢問就改檔、執行任何指令（包括刪除檔案）。\n確定之後的新工作都用這個模式嗎？", "確定", "取消"))
            return;
        mode = id;
        service.Settings.WorkflowMode = id;
        service.SaveSettings();
        page.InvalidateVisual();
    }

    internal MiniControl Page => page;
}

/// <summary>Draws the small window through the shared host; dragging an empty spot moves the window.</summary>
internal sealed class MiniControl(WorkflowMiniWindow owner) : GlassControl
{
    protected override void Draw(IPaint paint, float width) => owner.Draw(paint, State);

    protected override void Act(string action) => owner.Act(action);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var at = e.GetPosition(this);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && State.HitAt(new SD.PointF((float)at.X, (float)at.Y)) == null)
        {
            owner.BeginMoveDrag(e);
            return;
        }
        base.OnPointerPressed(e);
    }
}

/// <summary>
/// The full view of the workflow (「詳細」): every open conversation with its whole log, Claude's question
/// with the exact command or file, and a box to reply. Closing it only hides it.
/// </summary>
internal sealed class WorkflowDetailWindow : Window
{
    readonly MonitorService service;
    readonly StackPanel list = new() { Spacing = 12, Margin = new Thickness(14) };
    readonly TextBlock empty = Label("目前沒有進行中的工作。從精簡小視窗輸入要 Claude 做的事。", MacTheme.Muted, 10);
    readonly Dictionary<ClaudeSession, JobView> views = [];
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public WorkflowDetailWindow(MonitorService service)
    {
        this.service = service;
        Title = "Claude 工作流 · 詳細";
        Icon = MacShell.AppIcon();
        Width = 620;
        Height = 720;
        MinWidth = 480;
        MinHeight = 420;
        Background = MacTheme.Vertical(Color.FromRgb(0x0B, 0x10, 0x22), Color.FromRgb(0x15, 0x12, 0x2E));
        FontFamily = MacTheme.UiFont;
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Margin = new Thickness(0, 24);
        list.Children.Add(empty);
        Content = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        refresh.Tick += (_, _) =>
        {
            refresh.Stop();
            Sync();
        };
        Action onChanged = () => Dispatcher.UIThread.Post(() => refresh.Start());
        service.Workflows.Changed += onChanged;
        Closed += (_, _) => service.Workflows.Changed -= onChanged;
        Sync();
    }

    internal static TextBlock Label(string text, Color color, double points, bool bold = false) => new()
    {
        Text = text,
        Foreground = MacTheme.Solid(color),
        FontSize = MacDraw.Pt(points),
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap,
    };

    void Sync()
    {
        var sessions = service.Workflows.Sessions;
        foreach (var gone in views.Keys.Except(sessions).ToList())
        {
            list.Children.Remove(views[gone]);
            views.Remove(gone);
        }
        foreach (var session in sessions)
        {
            if (!views.TryGetValue(session, out var view))
            {
                view = new JobView(session, () => service.Workflows.Close(session));
                views[session] = view;
                // Newest first, under the empty note.
                list.Children.Insert(1, view);
            }
            view.Refresh();
        }
        empty.IsVisible = views.Count == 0;
    }
}

/// <summary>One conversation in the full view.</summary>
internal sealed class JobView : Border
{
    readonly ClaudeSession session;
    readonly TextBlock status = WorkflowDetailWindow.Label("", MacTheme.Text, 9.5, bold: true);
    readonly TextBlock title = WorkflowDetailWindow.Label("", MacTheme.Text, 10, bold: true);
    readonly TextBlock where = WorkflowDetailWindow.Label("", MacTheme.Muted, 8.5);
    readonly SelectableTextBlock log = new() { TextWrapping = TextWrapping.Wrap, FontSize = MacDraw.Pt(9.5), Inlines = [] };
    readonly ScrollViewer logScroll;
    readonly Button stop = new() { Content = "停止" };
    readonly Button close = new() { Content = "關閉" };
    readonly Border ask;
    readonly TextBlock askTitle = WorkflowDetailWindow.Label("", Color.FromRgb(0xFB, 0xBF, 0x24), 10, bold: true);
    readonly TextBox askDetail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Menlo, Consolas, monospace"), MaxHeight = 90, Background = MacTheme.Solid(Color.FromRgb(0x2A, 0x20, 0x10)), BorderThickness = new Thickness(0) };
    readonly Button allow = new() { Content = "允許", Classes = { "accent" } };
    readonly Button deny = new() { Content = "拒絕" };
    readonly TextBox reply = new() { Watermark = "接著跟 Claude 說…（Enter 送出）", MinWidth = 200 };
    readonly Button send = new() { Content = "送出", Classes = { "accent" } };
    readonly Grid replyRow;
    int rendered = -1;
    string liveShown = "";

    public JobView(ClaudeSession session, Action onClose)
    {
        this.session = session;
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(14);
        Background = MacTheme.Vertical(Color.FromArgb(220, 0x1A, 0x1E, 0x3C), Color.FromArgb(230, 0x14, 0x17, 0x31));
        BorderThickness = new Thickness(1.2);
        logScroll = new ScrollViewer { Content = log, Height = 220, Background = MacTheme.Solid(Color.FromRgb(0x16, 0x1A, 0x36)), Padding = new Thickness(10) };
        ask = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Background = MacTheme.Solid(Color.FromRgb(0x3A, 0x2C, 0x14)),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { askTitle, askDetail, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { deny, allow } } },
            },
        };
        replyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { reply, send } };
        Grid.SetColumn(send, 1);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10, Children = { status, title, stop, close } };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(stop, 2);
        Grid.SetColumn(close, 3);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        Child = new StackPanel { Spacing = 8, Children = { head, where, logScroll, ask, replyRow } };
        stop.Click += (_, _) => session.Interrupt();
        close.Click += (_, _) => onClose();
        allow.Click += (_, _) => session.Answer(true);
        deny.Click += (_, _) => session.Answer(false);
        send.Click += (_, _) => SendReply();
        reply.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendReply();
            }
        }, RoutingStrategies.Tunnel);
    }

    void SendReply()
    {
        session.Send(reply.Text ?? "");
        reply.Text = "";
    }

    public void Refresh()
    {
        var (word, color) = session.State switch
        {
            JobState.Starting => ("啟動中…", MacTheme.Coral),
            JobState.Working => ("工作中", MacTheme.Violet),
            JobState.NeedsPermission => ("等你允許", Color.FromRgb(0xFB, 0xBF, 0x24)),
            JobState.Waiting => ("等你回覆", MacTheme.Good),
            JobState.Stopped => ("已結束", MacTheme.Muted),
            _ => ("已中斷", Color.FromRgb(0xFB, 0x71, 0x85)),
        };
        status.Text = "● " + word;
        status.Foreground = MacTheme.Solid(color);
        BorderBrush = MacTheme.Solid(Color.FromArgb((byte)(session.Running ? 200 : 110), color.R, color.G, color.B));
        title.Text = session.Title;
        where.Text = $"📁 {session.Folder}　權限：{PermissionModes.Of(session.Mode).Name}";
        stop.IsVisible = session.Running;
        close.IsVisible = !session.Running;
        ask.IsVisible = session.Ask != null;
        if (session.Ask is { } question)
        {
            askTitle.Text = "Claude 想要" + question.Summary;
            askDetail.Text = question.Detail;
            askDetail.IsVisible = question.Detail.Length > 0;
        }
        replyRow.IsVisible = session.State == JobState.Waiting;
        RenderLog();
    }

    /// <summary>Rebuilds the log only when a line was added or the reply being written changed.</summary>
    void RenderLog()
    {
        var (lines, live) = session.Snapshot();
        if (lines.Count == rendered && live == liveShown)
            return;
        rendered = lines.Count;
        liveShown = live;
        var inlines = log.Inlines!;
        inlines.Clear();
        void Add(string text, Color color, bool bold = false, double points = 9.5) =>
            inlines.Add(new Run(text) { Foreground = MacTheme.Solid(color), FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, FontSize = MacDraw.Pt(points) });
        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case JobLineKind.User:
                    Add("你：", MacTheme.Coral, bold: true);
                    Add(line.Text + "\n\n", MacTheme.Text);
                    break;
                case JobLineKind.Claude:
                    Add(line.Text + "\n\n", MacTheme.Text);
                    break;
                case JobLineKind.Tool:
                    Add("· " + line.Text + "\n", MacTheme.Muted, points: 8.5);
                    break;
                case JobLineKind.Note:
                    Add("· " + line.Text + "\n", Color.FromRgb(0xC4, 0xB5, 0xFD), points: 8.5);
                    break;
                default:
                    Add("⚠ " + line.Text + "\n", Color.FromRgb(0xFB, 0x71, 0x85), bold: true);
                    break;
            }
        }
        if (live.Length > 0)
            Add(live, Color.FromRgb(0xE6, 0xE2, 0xF8));
        Dispatcher.UIThread.Post(logScroll.ScrollToEnd, DispatcherPriority.Background);
    }
}
