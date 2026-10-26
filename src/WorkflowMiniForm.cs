namespace ClaudeUsageMonitor;

/// <summary>
/// The small workflow window (layout in Core/WorkflowMini.cs), as compact as the overlay: it floats on
/// top, takes an instruction on one line and grows a short row for each open conversation, with the
/// buttons that matter (allow, deny, stop, reply, close). It comes back by itself, without taking the
/// keyboard, when Claude waits for an answer. 「詳細」 opens the full log.
/// </summary>
internal sealed class WorkflowMiniForm : Form
{
    static WorkflowMiniForm? open;

    readonly MonitorService service;
    readonly MiniView view;
    readonly TextBox input = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = GlassBox.FieldColor,
        ForeColor = PageKit.Text,
    };
    readonly ContextMenuStrip menu = new() { ShowImageMargin = false };
    List<ClaudeSession> sessions = [];
    ClaudeSession? replyTo;
    string folder, mode;

    /// <summary>Opens the window next to <paramref name="near"/> (the overlay) and puts the cursor in the line.</summary>
    public static WorkflowMiniForm Open(MonitorService service, Form? near = null)
    {
        var form = Reveal(service, near);
        form.Activate();
        form.input.Focus();
        return form;
    }

    /// <summary>The overlay's ">_" button: shows the window, or hides it when it is already showing.</summary>
    public static void Toggle(MonitorService service, Form near)
    {
        if (open is { IsDisposed: false, Visible: true })
            open.Hide();
        else
            Open(service, near);
    }

    /// <summary>Shows the window without taking the keyboard (Claude waits for an answer).</summary>
    public static WorkflowMiniForm Reveal(MonitorService service, Form? near = null)
    {
        if (open is not { IsDisposed: false })
            open = new WorkflowMiniForm(service);
        if (!open.Visible)
        {
            open.Place(near);
            open.Show();
        }
        return open;
    }

    WorkflowMiniForm(MonitorService service)
    {
        this.service = service;
        folder = Directory.Exists(service.Settings.WorkflowFolder) ? service.Settings.WorkflowFolder
            : RecentProjects.Load().FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        mode = service.Settings.WorkflowMode;
        Text = "Claude 工作流";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = service.Settings.TopMost;
        StartPosition = FormStartPosition.Manual;
        BackColor = PageKit.PageTop;
        AutoScaleMode = AutoScaleMode.None;
        view = new MiniView(this) { Dock = DockStyle.Fill };
        input.Font = Theme.Ui(10.5f);
        ShowPlaceholder();
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Submit();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (replyTo != null)
                    Target(null);
                else
                    Hide();
            }
        };
        view.Controls.Add(input);
        Controls.Add(view);
        float s = DeviceDpi / 96f;
        ClientSize = new Size((int)(WorkflowMini.Width * s), (int)(92 * s));

        Action onChanged = () =>
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(view.Invalidate);
        };
        service.Workflows.Changed += onChanged;
        FormClosed += (_, _) => service.Workflows.Changed -= onChanged;
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        HandleCreated += (_, _) => Theme.ApplyWindowChrome(this, rounded: true);
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Above the overlay when there is room, otherwise below it; a position the user dragged to wins.</summary>
    void Place(Form? near)
    {
        if (service.Settings.WorkflowX is int x && service.Settings.WorkflowY is int y)
        {
            Location = new Point(x, y);
        }
        else if (near != null)
        {
            var area = Screen.FromControl(near).WorkingArea;
            int top = near.Top - Height - 10;
            Location = new Point(Math.Clamp(near.Right - Width, area.Left, area.Right - Width), top >= area.Top ? top : near.Bottom + 10);
        }
        else
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            Location = new Point(area.Right - Width - 24, area.Bottom - Height - 200);
        }
        Keep();
    }

    /// <summary>Keeps the whole window on its screen as it grows.</summary>
    void Keep()
    {
        var area = Screen.FromPoint(Location).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    internal void SavePlace()
    {
        service.Settings.WorkflowX = Left;
        service.Settings.WorkflowY = Top;
        service.SaveSettings();
    }

    /// <summary>Draws through the shared layout, keeps the text box on its field and the window as tall as the content.</summary>
    internal void Draw(IPaint paint, PageState state)
    {
        sessions = service.Workflows.Sessions.ToList();
        if (replyTo != null && !sessions.Contains(replyTo))
            Target(null);
        float height = WorkflowMini.Render(paint, state, new WorkflowMiniData(folder, mode, sessions, replyTo), out var box);
        float s = DeviceDpi / 96f;
        var bounds = new Rectangle((int)(box.X * s), (int)(box.Y * s + (box.Height * s - input.Height) / 2), (int)(box.Width * s), input.Height);
        int pixels = (int)Math.Ceiling(height * s);
        if (input.Bounds != bounds || ClientSize.Height != pixels)
        {
            BeginInvoke(() =>
            {
                input.Bounds = bounds;
                if (ClientSize.Height != pixels)
                {
                    ClientSize = new Size(ClientSize.Width, pixels);
                    Keep();
                }
            });
        }
    }

    /// <summary>Sets the folder and the instruction; used by --screenshot.</summary>
    internal void Prepare(string path, string text)
    {
        folder = path;
        input.Text = text;
        view.Invalidate();
    }

    internal ClaudeSession? Submit()
    {
        string text = input.Text.Trim();
        if (text.Length == 0)
            return null;
        if (replyTo is { State: JobState.Waiting } target)
        {
            target.Send(text);
            input.Clear();
            Target(null);
            return target;
        }
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, "找不到這個資料夾，請重新選擇。", "Claude 工作流", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        try
        {
            var session = service.Workflows.Start(folder, mode, text);
            input.Clear();
            service.Settings.WorkflowFolder = folder;
            service.SaveSettings();
            return session;
        }
        catch (MonitorException e)
        {
            MessageBox.Show(this, MonitorService.Explain(e.Code), "無法啟動 Claude Code", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
    }

    void Target(ClaudeSession? session)
    {
        replyTo = session;
        ShowPlaceholder();
        view.Invalidate();
    }

    void ShowPlaceholder() => input.PlaceholderText = replyTo is { } s
        ? $"回覆「{s.Title}」…（Esc 取消）"
        : "要 Claude 做什麼？（Enter 送出）";

    internal void Act(string action)
    {
        int colon = action.IndexOf(':');
        var session = colon > 0 && int.TryParse(action[(colon + 1)..], out int i) && i >= 0 && i < sessions.Count ? sessions[i] : null;
        switch (colon > 0 ? action[..colon] : action)
        {
            case "input":
                input.Focus();
                break;
            case "folder":
                ShowFolders();
                break;
            case "mode":
                ShowModes();
                break;
            case "detail":
            case "open":
                WorkflowForm.Open(service);
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
                Activate();
                input.Focus();
                break;
            case "close" when session != null:
                service.Workflows.Close(session);
                break;
        }
    }

    void ShowFolders()
    {
        menu.Items.Clear();
        foreach (string path in RecentProjects.Load())
        {
            var item = new ToolStripMenuItem(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) + "　" + path) { Checked = string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => SetFolder(path);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0)
            menu.Items.Add(new ToolStripSeparator());
        var other = new ToolStripMenuItem("其他資料夾…");
        other.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { InitialDirectory = folder, Description = "Claude 要在哪個資料夾工作？", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                SetFolder(dialog.SelectedPath);
        };
        menu.Items.Add(other);
        menu.Show(this, PointToClient(Cursor.Position));
    }

    void SetFolder(string path)
    {
        folder = path;
        view.Invalidate();
    }

    void ShowModes()
    {
        menu.Items.Clear();
        foreach (var (id, name, note) in PermissionModes.All)
        {
            var item = new ToolStripMenuItem($"{name}　{note}") { Checked = id == mode };
            item.Click += (_, _) => SetMode(id);
            menu.Items.Add(item);
        }
        menu.Show(this, PointToClient(Cursor.Position));
    }

    void SetMode(string id)
    {
        if (id == PermissionModes.Everything && mode != id
            && MessageBox.Show(this, "「全部允許」會讓 Claude 不經詢問就改檔、執行任何指令（包括刪除檔案）。\n確定之後的新工作都用這個模式嗎？",
                "Claude 工作流", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        mode = id;
        service.Settings.WorkflowMode = id;
        service.SaveSettings();
        view.Invalidate();
    }
}

/// <summary>Draws the small window through the shared host; dragging an empty spot moves the window.</summary>
internal sealed class MiniView(WorkflowMiniForm owner) : GlassView
{
    Point? grab;

    protected override void Draw(IPaint paint, float width) => owner.Draw(paint, State);

    protected override void Act(string action) => owner.Act(action);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && State.HitAt(new PointF(e.X / UiScale, e.Y / UiScale)) == null)
            grab = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (grab is { } start && e.Button == MouseButtons.Left)
        {
            owner.Location = new Point(owner.Left + e.X - start.X, owner.Top + e.Y - start.Y);
            return;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (grab != null)
        {
            grab = null;
            owner.SavePlace();
        }
    }
}
