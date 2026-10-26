using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace ClaudeUsageMonitor;

internal sealed class SettingsWindow : Window
{
    readonly MonitorService service;
    readonly OverlayWindow overlay;
    readonly NumericUpDown interval = Number(60, 900, 30);
    readonly CheckBox alerts = Check("額度偏低時通知");
    readonly NumericUpDown warn = Number(1, 90, 5);
    readonly NumericUpDown critical = Number(0, 50, 1);
    readonly CheckBox notifyReset = Check("偏低後恢復時通知");
    readonly CheckBox topMost = Check("浮窗永遠在最上層");
    readonly NumericUpDown opacity = Number(40, 100, 5);
    readonly CheckBox startHidden = Check("啟動時只顯示選單列圖示");
    readonly CheckBox startAtLogin = Check("登入時自動啟動");
    readonly NumericUpDown retention = Number(7, 365, 7);
    readonly TextBox claudePath = new() { Width = 320, Watermark = "空白＝自動尋找" };

    public SettingsWindow(MonitorService service, OverlayWindow overlay)
    {
        this.service = service;
        this.overlay = overlay;
        Title = "Claude 額度 · 設定";
        Icon = MacShell.AppIcon();
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Background = MacTheme.Solid(MacTheme.Canvas);
        FontFamily = MacTheme.UiFont;

        var s = service.Settings;
        interval.Value = s.IntervalSeconds;
        alerts.IsChecked = s.Alerts;
        warn.Value = s.WarnRemaining;
        critical.Value = s.CriticalRemaining;
        notifyReset.IsChecked = s.NotifyReset;
        topMost.IsChecked = s.TopMost;
        opacity.Value = s.OpacityPercent;
        startHidden.IsChecked = s.StartHidden;
        startAtLogin.IsChecked = Autostart.Enabled;
        startAtLogin.IsEnabled = Autostart.Supported && !service.Demo;
        retention.Value = s.RetentionDays;
        claudePath.Text = s.ClaudePath;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), Margin = new Thickness(20, 16, 20, 4) };
        int row = 0;
        void Add(Control left, Control? right = null)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(left, row);
            if (right == null)
                Grid.SetColumnSpan(left, 2);
            grid.Children.Add(left);
            if (right != null)
            {
                Grid.SetRow(right, row);
                Grid.SetColumn(right, 1);
                grid.Children.Add(right);
            }
            row++;
        }
        TextBlock Label(string text) => new() { Text = text, Foreground = MacTheme.Solid(MacTheme.Muted), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 16, 6) };

        var browse = new Button { Content = "選擇…", Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += async (_, _) => await BrowseAsync();

        Add(Label("更新間隔（秒）"), interval);
        Add(alerts);
        Add(Label("剩餘低於（%）先提醒"), warn);
        Add(Label("剩餘低於（%）再提醒"), critical);
        Add(notifyReset);
        Add(topMost);
        Add(Label("浮窗不透明度（%）"), opacity);
        Add(startHidden);
        Add(startAtLogin);
        Add(Label("紀錄保留天數"), retention);
        Add(Label("claude 位置"), new StackPanel { Orientation = Orientation.Horizontal, Children = { claudePath, browse } });
        Add(new TextBlock
        {
            Text = "額度沒有變化時會自動放慢（最多 10 分鐘一次），有變化時立即恢復。\n只會執行簽章屬於 Anthropic 的 claude；查詢額度不送出任何提問，不耗用額度。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            Foreground = MacTheme.Solid(MacTheme.Muted),
            Margin = new Thickness(0, 10, 0, 0),
        });

        var save = new Button { Content = "儲存", Classes = { "accent" } };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Children =
            {
                grid,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(20, 12, 20, 20),
                    Children = { cancel, save },
                },
            },
        };
    }

    async Task BrowseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "選擇 Claude Code 的 claude 執行檔", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            claudePath.Text = path;
    }

    void Save()
    {
        string path = (claudePath.Text ?? "").Trim().Trim('"');
        if (path.Length > 0 && !ClaudeLocator.IsVerified(path))
        {
            MacDialog.Show(this, "claude 位置", "這個檔案不存在，或簽章不屬於 Anthropic，不能使用。");
            return;
        }
        if ((critical.Value ?? 0) >= (warn.Value ?? 0))
        {
            MacDialog.Show(this, "提醒門檻", "「再提醒」的門檻要比「先提醒」低。");
            return;
        }

        var s = service.Settings;
        s.IntervalSeconds = (int)(interval.Value ?? 120);
        s.Alerts = alerts.IsChecked == true;
        s.WarnRemaining = (int)(warn.Value ?? 20);
        s.CriticalRemaining = (int)(critical.Value ?? 5);
        s.NotifyReset = notifyReset.IsChecked == true;
        s.TopMost = topMost.IsChecked == true;
        s.OpacityPercent = (int)(opacity.Value ?? 95);
        s.StartHidden = startHidden.IsChecked == true;
        s.RetentionDays = (int)(retention.Value ?? 90);
        s.ClaudePath = path;
        if (!service.SaveSettings())
        {
            MacDialog.Show(this, "設定", "設定沒有存成功，請稍後再試。");
            return;
        }
        if (Autostart.Supported && !service.Demo && startAtLogin.IsChecked != Autostart.Enabled)
        {
            try
            {
                Autostart.Enabled = startAtLogin.IsChecked == true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                MacDialog.Show(this, "設定", "無法變更登入時自動啟動。其他設定已儲存。");
            }
        }
        overlay.ApplySettings();
        service.Refresh();
        Close();
    }

    static NumericUpDown Number(int min, int max, int step) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        FormatString = "0",
        Width = 130,
        Margin = new Thickness(0, 4),
    };

    static CheckBox Check(string text) => new() { Content = text, Margin = new Thickness(0, 4) };
}
