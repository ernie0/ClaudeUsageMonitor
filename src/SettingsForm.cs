namespace ClaudeUsageMonitor;

internal sealed class SettingsForm : Form
{
    readonly MonitorService service;
    readonly NumericUpDown interval = Number(60, 900, 30);
    readonly CheckBox alerts = Check("額度偏低時通知");
    readonly NumericUpDown warn = Number(1, 90, 5);
    readonly NumericUpDown critical = Number(0, 50, 1);
    readonly CheckBox notifyReset = Check("偏低後恢復時通知");
    readonly CheckBox topMost = Check("浮窗永遠在最上層");
    readonly NumericUpDown opacity = Number(40, 100, 5);
    readonly CheckBox startHidden = Check("啟動時只顯示系統匣圖示");
    readonly CheckBox startWithWindows = Check("登入 Windows 時自動啟動");
    readonly NumericUpDown retention = Number(7, 365, 7);
    readonly TextBox claudePath = new() { Width = 300, BackColor = Theme.Raised, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };

    public static bool Edit(MonitorService service, IWin32Window? owner)
    {
        using var form = new SettingsForm(service);
        return form.ShowDialog(owner) == DialogResult.OK;
    }

    internal SettingsForm(MonitorService service)
    {
        this.service = service;
        Text = "Claude 額度 · 設定";
        Icon = AppIcon.Load();
        BackColor = Theme.Canvas;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var s = service.Settings;
        interval.Value = s.IntervalSeconds;
        alerts.Checked = s.Alerts;
        warn.Value = s.WarnRemaining;
        critical.Value = s.CriticalRemaining;
        notifyReset.Checked = s.NotifyReset;
        topMost.Checked = s.TopMost;
        opacity.Value = s.OpacityPercent;
        startHidden.Checked = s.StartHidden;
        startWithWindows.Checked = Autostart.Enabled;
        startWithWindows.Enabled = !service.Demo;
        retention.Value = s.RetentionDays;
        claudePath.Text = s.ClaudePath;
        claudePath.PlaceholderText = "空白＝自動尋找";

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(16, 14, 16, 4), BackColor = Theme.Canvas };
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 7, 12, 3) });
            grid.Controls.Add(control);
        }
        void Full(Control control)
        {
            grid.Controls.Add(control);
            grid.SetColumnSpan(control, 2);
        }

        Row("更新間隔（秒）", interval);
        Full(alerts);
        Row("剩餘低於（%）先提醒", warn);
        Row("剩餘低於（%）再提醒", critical);
        Full(notifyReset);
        Full(topMost);
        Row("浮窗不透明度（%）", opacity);
        Full(startHidden);
        Full(startWithWindows);
        Row("紀錄保留天數", retention);

        var pathRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var browse = Theme.Button("選擇…");
        browse.Click += (_, _) => Browse();
        pathRow.Controls.Add(claudePath);
        pathRow.Controls.Add(browse);
        Row("claude.exe 位置", pathRow);
        Full(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Theme.Muted,
            Margin = new Padding(3, 8, 3, 3),
            Text = "額度沒有變化時會自動放慢（最多 10 分鐘一次），鎖定螢幕時 15 分鐘一次，有變化或解鎖時立即恢復。\n"
                + "只會執行有 Anthropic 有效數位簽章的 claude.exe；查詢額度不送出任何提問，不耗用額度。",
        });

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Padding = new Padding(12, 6, 12, 12), BackColor = Theme.Canvas };
        var cancel = Theme.Button("取消");
        cancel.DialogResult = DialogResult.Cancel;
        var save = Theme.Button("儲存", primary: true);
        save.Click += (_, _) => Save();
        buttons.Controls.AddRange([cancel, save]);
        CancelButton = cancel;
        AcceptButton = save;

        var root = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Canvas };
        root.Controls.Add(grid);
        root.Controls.Add(buttons);
        Controls.Add(root);
        HandleCreated += (_, _) => Theme.ApplyWindowChrome(this);
    }

    void Browse()
    {
        using var dialog = new OpenFileDialog { Filter = "claude.exe|claude.exe", Title = "選擇 Claude Code 的 claude.exe" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            claudePath.Text = dialog.FileName;
    }

    void Save()
    {
        string path = claudePath.Text.Trim().Trim('"');
        if (path.Length > 0 && !ClaudeLocator.IsVerified(path))
        {
            MessageBox.Show(this, "這個檔案不存在，或沒有 Anthropic 的有效數位簽章，不能使用。", "claude.exe 位置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (critical.Value >= warn.Value)
        {
            MessageBox.Show(this, "「再提醒」的門檻要比「先提醒」低。", "提醒門檻", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var s = service.Settings;
        s.IntervalSeconds = (int)interval.Value;
        s.Alerts = alerts.Checked;
        s.WarnRemaining = (int)warn.Value;
        s.CriticalRemaining = (int)critical.Value;
        s.NotifyReset = notifyReset.Checked;
        s.TopMost = topMost.Checked;
        s.OpacityPercent = (int)opacity.Value;
        s.StartHidden = startHidden.Checked;
        s.RetentionDays = (int)retention.Value;
        s.ClaudePath = path;
        if (!service.SaveSettings())
        {
            MessageBox.Show(this, "設定沒有存成功，請稍後再試。", "設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!service.Demo && startWithWindows.Checked != Autostart.Enabled)
        {
            try
            {
                Autostart.Enabled = startWithWindows.Checked;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
            {
                MessageBox.Show(this, "無法變更開機自動啟動（權限不足）。其他設定已儲存。", "設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        service.Refresh();
        DialogResult = DialogResult.OK;
    }

    static NumericUpDown Number(int min, int max, int step) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        Width = 90,
        BackColor = Theme.Raised,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
    };

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(3, 6, 3, 3) };
}
