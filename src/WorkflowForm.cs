namespace ClaudeUsageMonitor;

/// <summary>
/// The full view of the workflow (opened from the small window's 「詳細」): the whole log of every open
/// conversation, Claude's questions with the exact command or file, and a larger box to type in. Closing
/// it only hides it; the conversations go on.
/// </summary>
internal sealed class WorkflowForm : Form
{
    static WorkflowForm? open;

    readonly MonitorService service;
    readonly GlassBox composer = new();
    readonly PillButton folderButton = Theme.Button("");
    readonly PillButton modeButton = Theme.Button("");
    readonly ContextMenuStrip modes = new() { ShowImageMargin = false };
    readonly TextBox prompt = new()
    {
        Multiline = true,
        BorderStyle = BorderStyle.None,
        BackColor = GlassBox.FieldColor,
        ForeColor = PageKit.Text,
        PlaceholderText = "要 Claude 做什麼？（Ctrl+Enter 送出）",
        ScrollBars = ScrollBars.Vertical,
    };
    readonly PillButton send = Theme.Button("送出", primary: true);
    readonly Label hint = new() { AutoSize = false, ForeColor = PageKit.Muted, BackColor = Color.Transparent };
    readonly Panel list = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = PageKit.PageTop };
    readonly Label empty = new() { Text = "目前沒有進行中的工作。", AutoSize = false, ForeColor = PageKit.Muted, TextAlign = ContentAlignment.MiddleCenter, BackColor = PageKit.PageTop };
    readonly ContextMenuStrip folders = new() { ShowImageMargin = false };
    readonly Dictionary<ClaudeSession, JobCard> cards = [];
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 80 };
    string folder;
    string mode;

    /// <summary>Opens (or brings forward) the window.</summary>
    public static WorkflowForm Open(MonitorService service)
    {
        if (open is not { IsDisposed: false })
            open = new WorkflowForm(service);
        open.Show();
        if (open.WindowState == FormWindowState.Minimized)
            open.WindowState = FormWindowState.Normal;
        open.Activate();
        open.prompt.Focus();
        return open;
    }

    WorkflowForm(MonitorService service)
    {
        this.service = service;
        var recent = RecentProjects.Load();
        folder = Directory.Exists(service.Settings.WorkflowFolder) ? service.Settings.WorkflowFolder
            : recent.FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        mode = service.Settings.WorkflowMode;
        Text = "Claude 工作流 · 詳細";
        Icon = AppIcon.Load();
        BackColor = PageKit.PageTop;
        ForeColor = PageKit.Text;
        Font = Theme.Ui(9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 680);
        MinimumSize = new Size(460, 460);

        prompt.Font = Theme.Ui(10.5f);
        prompt.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Control)
            {
                e.SuppressKeyPress = true;
                Submit();
            }
        };
        send.Click += (_, _) => Submit();
        folderButton.Click += (_, _) => ShowFolders();
        modeButton.Click += (_, _) => ShowModes();
        hint.Font = Theme.Ui(8.5f);
        composer.Controls.AddRange([folderButton, modeButton, prompt, send, hint]);
        composer.Framed.Add(prompt);
        composer.Height = 188;
        composer.Dock = DockStyle.Top;
        composer.Padding = new Padding(10);
        composer.Layout += (_, _) => LayoutComposer();
        list.Controls.Add(empty);
        list.Resize += (_, _) => LayoutCards();
        Controls.Add(list);
        Controls.Add(composer);
        ShowFolder();
        ShowMode();

        refresh.Tick += (_, _) =>
        {
            refresh.Stop();
            Sync();
        };
        Action onChanged = () =>
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => refresh.Start());
        };
        service.Workflows.Changed += onChanged;
        FormClosed += (_, _) => service.Workflows.Changed -= onChanged;
        // Closing only hides the window; the conversations keep going.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        HandleCreated += (_, _) =>
        {
            Theme.ApplyWindowChrome(this);
            Theme.DarkScrollbars(list);
            Theme.DarkScrollbars(prompt);
        };
        Sync();
    }

    internal string Folder => folder;

    /// <summary>Sets the folder and the instruction; used by --screenshot.</summary>
    internal void Prepare(string path, string text)
    {
        folder = path;
        ShowFolder();
        prompt.Text = text;
    }

    void LayoutComposer()
    {
        float s = DeviceDpi / 96f;
        int pad = (int)(16 * s), w = composer.ClientSize.Width - pad * 2;
        modeButton.Location = new Point(composer.ClientSize.Width - pad - modeButton.Width, (int)(14 * s));
        folderButton.Location = new Point(pad, (int)(14 * s));
        folderButton.MaximumSize = new Size(Math.Max(60, modeButton.Left - pad - (int)(8 * s)), 0);
        prompt.SetBounds(pad + (int)(10 * s), folderButton.Bottom + (int)(14 * s), w - (int)(20 * s), (int)(72 * s));
        send.Location = new Point(composer.ClientSize.Width - pad - send.Width, prompt.Bottom + (int)(14 * s));
        hint.SetBounds(pad, send.Top, Math.Max(10, send.Left - pad - (int)(8 * s)), send.Height);
        hint.TextAlign = ContentAlignment.MiddleLeft;
    }

    void ShowFolder()
    {
        folderButton.Text = "📁 " + Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) + " ▾";
        new ToolTip().SetToolTip(folderButton, "在這個資料夾工作：" + folder);
        LayoutComposer();
    }

    void ShowFolders()
    {
        folders.Items.Clear();
        foreach (string path in RecentProjects.Load())
        {
            var item = new ToolStripMenuItem(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) + "　" + path) { Checked = string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => Choose(path);
            folders.Items.Add(item);
        }
        if (folders.Items.Count > 0)
            folders.Items.Add(new ToolStripSeparator());
        var other = new ToolStripMenuItem("其他資料夾…");
        other.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { InitialDirectory = folder, Description = "Claude 要在哪個資料夾工作？", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                Choose(dialog.SelectedPath);
        };
        folders.Items.Add(other);
        folders.Show(folderButton, new Point(0, folderButton.Height));
    }

    void Choose(string path)
    {
        folder = path;
        ShowFolder();
    }

    void ShowMode()
    {
        var (_, name, note) = PermissionModes.Of(mode);
        modeButton.Text = "🛡 " + name + " ▾";
        modeButton.ForeColor = mode == PermissionModes.Everything ? PageKit.Hex(0xFB7185) : PageKit.Text;
        hint.Text = $"權限：{note}。會用到額度。";
        hint.ForeColor = mode == PermissionModes.Everything ? PageKit.Hex(0xFB7185) : PageKit.Muted;
        LayoutComposer();
    }

    void ShowModes()
    {
        modes.Items.Clear();
        foreach (var (id, name, note) in PermissionModes.All)
        {
            var item = new ToolStripMenuItem($"{name}　{note}") { Checked = id == mode };
            item.Click += (_, _) => ChooseMode(id);
            modes.Items.Add(item);
        }
        modes.Show(modeButton, new Point(0, modeButton.Height));
    }

    void ChooseMode(string id)
    {
        if (id == PermissionModes.Everything && mode != id
            && MessageBox.Show(this, "「全部允許」會讓 Claude 不經詢問就改檔、執行任何指令（包括刪除檔案）。\n確定之後的新工作都用這個模式嗎？",
                "Claude 工作流", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        mode = id;
        service.Settings.WorkflowMode = id;
        service.SaveSettings();
        ShowMode();
    }

    internal ClaudeSession? Submit()
    {
        string text = prompt.Text.Trim();
        if (text.Length == 0)
            return null;
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, "找不到這個資料夾，請重新選擇。", "Claude 工作流", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        try
        {
            var session = service.Workflows.Start(folder, mode, text);
            prompt.Clear();
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

    /// <summary>Adds and removes cards to match the open conversations and refreshes them.</summary>
    void Sync()
    {
        var sessions = service.Workflows.Sessions;
        foreach (var gone in cards.Keys.Except(sessions).ToList())
        {
            cards[gone].Dispose();
            cards.Remove(gone);
        }
        foreach (var session in sessions)
        {
            if (!cards.ContainsKey(session))
            {
                var card = new JobCard(session, () => service.Workflows.Close(session));
                cards[session] = card;
                list.Controls.Add(card);
            }
            cards[session].Refresh(session);
        }
        empty.Visible = cards.Count == 0;
        LayoutCards();
    }

    void LayoutCards()
    {
        float s = DeviceDpi / 96f;
        int pad = (int)(12 * s), width = list.ClientSize.Width - pad * 2;
        int y = pad + list.AutoScrollPosition.Y;
        empty.SetBounds(pad, y, width, (int)(60 * s));
        // Newest first.
        foreach (var card in cards.Values.Reverse())
        {
            card.SetBounds(pad, y, width, card.PreferredHeight(width));
            y += card.Height + pad;
        }
    }
}

/// <summary>A frosted glass panel behind child controls.</summary>
internal class GlassBox : Panel
{
    public GlassBox()
    {
        DoubleBuffered = true;
        BackColor = PageKit.PanelBottom;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    /// <summary>The fill of the dark fields drawn behind text boxes; their own back colour.</summary>
    public static readonly Color FieldColor = PageKit.Hex(0x161A36);

    /// <summary>Text boxes that get a rounded field drawn around them.</summary>
    public List<Control> Framed { get; } = [];

    /// <summary>Light colour of the rim and glow, or null for the neutral panel.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color? Light { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        TilePainter.Prepare(g);
        g.Clear(Parent?.BackColor ?? PageKit.PageTop);
        float s = DeviceDpi / 96f;
        using var paint = new GdiPaint(g, s);
        var r = new RectangleF(4, 4, Width / s - 8, Height / s - 8);
        PageKit.Panel(paint, r, 14);
        if (Light is { } light)
            paint.Stroke(r, PageKit.WithAlpha(light, 190), 1.6f, 14);
        foreach (var control in Framed.Where(c => c.Visible))
        {
            var b = control.Bounds;
            var field = RectangleF.Inflate(new RectangleF(b.X / s, b.Y / s, b.Width / s, b.Height / s), 9, 7);
            paint.Fill(field, FieldColor, 10);
            paint.Stroke(field, PageKit.Hex(0x343A66), 1.1f, 10);
            paint.Line(new PointF(field.X + 10, field.Y + 1.2f), new PointF(field.Right - 10, field.Y + 1.2f), Color.FromArgb(70, 255, 255, 255), 1);
        }
        PaintContent(paint, r);
    }

    protected virtual void PaintContent(IPaint paint, RectangleF r)
    {
    }
}

/// <summary>One conversation: status, the running log, Claude's permission question and the follow-up box.</summary>
internal sealed class JobCard : GlassBox
{
    readonly RichTextBox log = new()
    {
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        BackColor = FieldColor,
        ForeColor = PageKit.Text,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        DetectUrls = false,
    };
    readonly PillButton stop = Theme.Button("停止");
    readonly PillButton close = Theme.Button("關閉");
    readonly Panel ask = new() { BackColor = PageKit.Hex(0x3A2C14) };
    readonly Label askTitle = new() { AutoSize = false, ForeColor = PageKit.Hex(0xFBBF24), BackColor = PageKit.Hex(0x3A2C14) };
    readonly TextBox askDetail = new() { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = PageKit.Hex(0x2A2010), ForeColor = PageKit.Text, ScrollBars = ScrollBars.Vertical };
    readonly PillButton allow = Theme.Button("允許", primary: true);
    readonly PillButton deny = Theme.Button("拒絕");
    readonly TextBox reply = new() { BorderStyle = BorderStyle.None, BackColor = FieldColor, ForeColor = PageKit.Text, PlaceholderText = "接著跟 Claude 說…（Enter 送出）" };
    readonly PillButton send = Theme.Button("送出", primary: true);
    readonly Font normal = Theme.Ui(9.5f), bold = Theme.Ui(9.5f, FontStyle.Bold), small = Theme.Ui(8.5f);
    ClaudeSession session;
    int rendered, liveStart;
    string status = "", title = "";
    Color dot;

    public JobCard(ClaudeSession session, Action onClose)
    {
        this.session = session;
        askTitle.Font = Theme.Ui(10f, FontStyle.Bold);
        askDetail.Font = new Font("Consolas", 9.5f);
        reply.Font = Theme.Ui(10f);
        ask.Controls.AddRange([askTitle, askDetail, allow, deny]);
        Controls.AddRange([log, stop, close, ask, reply, send]);
        Framed.AddRange([log, reply]);
        stop.Click += (_, _) => this.session.Interrupt();
        close.Click += (_, _) => onClose();
        allow.Click += (_, _) => this.session.Answer(true);
        deny.Click += (_, _) => this.session.Answer(false);
        send.Click += (_, _) => SendReply();
        reply.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendReply();
            }
        };
        HandleCreated += (_, _) =>
        {
            Theme.DarkScrollbars(log);
            Theme.DarkScrollbars(askDetail);
        };
        Disposed += (_, _) =>
        {
            normal.Dispose();
            bold.Dispose();
            small.Dispose();
        };
    }

    float S => DeviceDpi / 96f;

    void SendReply()
    {
        session.Send(reply.Text);
        reply.Clear();
    }

    public int PreferredHeight(int width) => (int)((58 + 210 + (ask.Visible ? 132 : 0) + (reply.Visible ? 46 : 0) + 14) * S);

    public void Refresh(ClaudeSession s)
    {
        session = s;
        (status, dot) = s.State switch
        {
            JobState.Starting => ("啟動中…", PageKit.Orange),
            JobState.Working => ("工作中", PageKit.Violet),
            JobState.NeedsPermission => ("等你允許", PageKit.Hex(0xFBBF24)),
            JobState.Waiting => ("等你回覆", PageKit.Green),
            JobState.Stopped => ("已結束", PageKit.Muted),
            _ => ("已中斷", PageKit.Hex(0xFB7185)),
        };
        title = s.Title;
        Light = s.State == JobState.NeedsPermission ? PageKit.Hex(0xFBBF24) : s.Running ? PageKit.Violet : null;
        stop.Visible = s.Running;
        close.Visible = !s.Running;
        ask.Visible = s.Ask != null;
        if (s.Ask is { } question)
        {
            askTitle.Text = "Claude 想要" + question.Summary;
            askDetail.Text = question.Detail;
        }
        reply.Visible = send.Visible = s.State == JobState.Waiting;
        Render(s);
        DoLayout();
        Invalidate();
    }

    /// <summary>Appends new lines and rewrites only the reply being written, so long logs stay quick.</summary>
    void Render(ClaudeSession s)
    {
        var (lines, live) = s.Snapshot();
        log.SuspendLayout();
        // A read-only box ignores edits, even from code: unlock it while the half-written reply is replaced.
        log.ReadOnly = false;
        if (log.TextLength > liveStart)
        {
            log.Select(liveStart, log.TextLength - liveStart);
            log.SelectedText = "";
        }
        for (; rendered < lines.Count; rendered++)
        {
            var line = lines[rendered];
            switch (line.Kind)
            {
                case JobLineKind.User:
                    Append("你：", PageKit.Orange, bold);
                    Append(line.Text + "\n\n", PageKit.Text, normal);
                    break;
                case JobLineKind.Claude:
                    Append(line.Text + "\n\n", PageKit.Text, normal);
                    break;
                case JobLineKind.Tool:
                    Append("· " + line.Text + "\n", PageKit.Muted, small);
                    break;
                case JobLineKind.Note:
                    Append("· " + line.Text + "\n", PageKit.Hex(0xC4B5FD), small);
                    break;
                default:
                    Append("⚠ " + line.Text + "\n", PageKit.Hex(0xFB7185), bold);
                    break;
            }
        }
        liveStart = log.TextLength;
        if (live.Length > 0)
            Append(live, PageKit.Hex(0xE6E2F8), normal);
        log.ReadOnly = true;
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
        log.ResumeLayout();
    }

    void Append(string text, Color color, Font font)
    {
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.SelectionColor = color;
        log.SelectionFont = font;
        log.AppendText(text);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        DoLayout();
    }

    void DoLayout()
    {
        float s = S;
        int pad = (int)(18 * s), w = Width - pad * 2;
        int y = (int)(58 * s);
        foreach (var button in new[] { stop, close })
            button.Location = new Point(Width - pad - button.Width, (int)(14 * s));
        log.SetBounds(pad + (int)(9 * s), y + (int)(7 * s), w - (int)(18 * s), (int)(186 * s));
        y = log.Bottom + (int)(17 * s);
        if (ask.Visible)
        {
            ask.SetBounds(pad, y, w, (int)(122 * s));
            int ap = (int)(10 * s);
            askTitle.SetBounds(ap, ap, ask.Width - ap * 2, (int)(22 * s));
            allow.Location = new Point(ask.Width - ap - allow.Width, ask.Height - ap - allow.Height);
            deny.Location = new Point(allow.Left - (int)(8 * s) - deny.Width, allow.Top);
            askDetail.SetBounds(ap, askTitle.Bottom + (int)(4 * s), ask.Width - ap * 2, allow.Top - askTitle.Bottom - (int)(10 * s));
            y = ask.Bottom + (int)(10 * s);
        }
        if (reply.Visible)
        {
            send.Location = new Point(Width - pad - send.Width, y);
            reply.SetBounds(pad + (int)(9 * s), y + (send.Height - reply.Height) / 2, send.Left - pad - (int)(26 * s), reply.Height);
        }
    }

    protected override void PaintContent(IPaint paint, RectangleF r)
    {
        paint.Circle(new PointF(r.X + 20, r.Y + 22), 5, dot);
        paint.Circle(new PointF(r.X + 20, r.Y + 22), 9, PageKit.WithAlpha(dot, 50));
        paint.Text(status, new RectangleF(r.X + 34, r.Y + 12, 90, 20), 9.5f, dot, bold: true, middle: true);
        float right = (Math.Max(stop.Visible ? stop.Left : 0, close.Visible ? close.Left : 0) - 10) / S;
        paint.Text(title, new RectangleF(r.X + 34 + 70, r.Y + 12, Math.Max(20, right - r.X - 104), 20), 10f, PageKit.Text, bold: true, middle: true);
        PageKit.Icon(paint, IconKind.Folder, new RectangleF(r.X + 34, r.Y + 34, 13, 13), PageKit.Muted, 1.2f);
        paint.Text($"{session.Folder}　權限：{PermissionModes.Of(session.Mode).Name}", new RectangleF(r.X + 52, r.Y + 32, Math.Max(20, right - r.X - 52), 18), 8.5f, PageKit.Muted, middle: true);
    }
}
