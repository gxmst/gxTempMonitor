using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TempMonitor;

public class AppConfig
{
    public double Top { get; set; } = 100;
    public double Left { get; set; } = 100;
    public bool IsDockedRight { get; set; } = true;
    public bool IsLocked { get; set; }
    public bool ShowRam { get; set; } = true;
    public bool ShowVram { get; set; } = true;
    public bool ShowUpload { get; set; } = true;
    public bool ShowDownload { get; set; } = true;
}

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const double BaseWidth = 135;
    private const double FullWidth = 205;
    private const double AnimDurationMs = 200;
    private const double MaxContainerWidth = 60;
    private const double IdleBackgroundOpacity = 0.6;
    private const double IdleTextOpacity = 0.7;

    private static readonly System.Windows.Media.Brush WarningBrush = CreateFrozenBrush("#FFA500");
    private static readonly System.Windows.Media.Brush CriticalBrush = CreateFrozenBrush("#FF4444");

    private readonly string _configPath;
    private DispatcherTimer? _idleTimer;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ToolStripMenuItem? _trayLockMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayStartupMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayShowRamMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayShowVramMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayShowUpMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayShowDownMenuItem;
    private DashboardWindow? _dashboardWindow;

    private bool _isLocked;
    private bool _showRam = true;
    private bool _showVram = true;
    private bool _showUpload = true;
    private bool _showDownload = true;

    public MainWindow()
    {
        string exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Environment.ProcessPath
            ?? AppContext.BaseDirectory;
        string exeDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

        Directory.SetCurrentDirectory(exeDir);
        _configPath = Path.Combine(exeDir, "config.json");

        InitializeComponent();
        InitializeTrayIcon();
        LoadConfig();
        HardwareMonitorService.Instance.DataUpdated += OnHardwareDataUpdated;
        ApplySnapshot(HardwareMonitorService.Instance.LatestSnapshot);

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _idleTimer.Tick += IdleTimer_Tick;
        _idleTimer.Start();
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "gxTempMonitor",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayLockMenuItem = new System.Windows.Forms.ToolStripMenuItem("锁定 (鼠标穿透)", null, (_, _) => SetLock(!_isLocked));
        _trayStartupMenuItem = new System.Windows.Forms.ToolStripMenuItem("开机自启", null, (_, _) => ToggleStartup());
        _trayShowRamMenuItem = new System.Windows.Forms.ToolStripMenuItem("显示内存 (RAM)", null, (_, _) => SetMetricVisibility(MetricVisibility.Ram, !_showRam));
        _trayShowVramMenuItem = new System.Windows.Forms.ToolStripMenuItem("显示显存 (VRAM)", null, (_, _) => SetMetricVisibility(MetricVisibility.Vram, !_showVram));
        _trayShowUpMenuItem = new System.Windows.Forms.ToolStripMenuItem("显示上传 (UP)", null, (_, _) => SetMetricVisibility(MetricVisibility.Upload, !_showUpload));
        _trayShowDownMenuItem = new System.Windows.Forms.ToolStripMenuItem("显示下载 (DN)", null, (_, _) => SetMetricVisibility(MetricVisibility.Download, !_showDownload));

        var visibilityMenu = new System.Windows.Forms.ToolStripMenuItem("显示项目");
        visibilityMenu.DropDownItems.Add(_trayShowRamMenuItem);
        visibilityMenu.DropDownItems.Add(_trayShowVramMenuItem);
        visibilityMenu.DropDownItems.Add(_trayShowUpMenuItem);
        visibilityMenu.DropDownItems.Add(_trayShowDownMenuItem);

        contextMenu.Items.Add(_trayLockMenuItem);
        contextMenu.Items.Add("-");
        contextMenu.Items.Add(_trayStartupMenuItem);
        contextMenu.Items.Add("-");
        contextMenu.Items.Add(visibilityMenu);
        contextMenu.Items.Add("恢复默认状态", null, (_, _) => RestoreDefaultState());
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("重置最大值", null, (_, _) => ResetMaxValues());
        contextMenu.Items.Add("退出 (Exit)", null, (_, _) => ExitApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;

        UpdateVisibilityMenuItems();
        UpdateStartupMenuItem();
    }

    private void OnHardwareDataUpdated(HardwareSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(HardwareSnapshot snapshot)
    {
        CpuUsageText.Text = $"{snapshot.CpuUsage:0.0} %";
        CpuMaxText.Text = $"{snapshot.CpuUsageMax:0.0} %";
        CpuUsageText.Foreground = GetAlertBrush(snapshot.CpuUsage);
        UpdateIndicator(CpuIndicator, snapshot.CpuUsage);

        if (snapshot.GpuTemperature.HasValue)
        {
            GpuTempText.Text = $"{snapshot.GpuTemperature.Value:0.0} °C";
            GpuMaxText.Text = $"{(snapshot.GpuTemperatureMax ?? snapshot.GpuTemperature.Value):0.0} °C";
            GpuTempText.Foreground = GetAlertBrush(snapshot.GpuTemperature.Value);
            UpdateIndicator(GpuIndicator, snapshot.GpuTemperature.Value);
        }
        else
        {
            GpuTempText.Text = "-- °C";
            GpuMaxText.Text = "-- °C";
            GpuTempText.Foreground = System.Windows.Media.Brushes.White;
            UpdateIndicator(GpuIndicator, 0);
        }

        RamUsedText.Text = $"{snapshot.RamUsedGb:F1} GB";
        RamMaxText.Text = $"{snapshot.RamUsedMaxGb:F1} GB";
        RamUsedText.Foreground = GetAlertBrush(snapshot.RamUsagePercent);
        UpdateIndicator(RamIndicator, snapshot.RamUsagePercent);

        if (snapshot.VramUsedGb.HasValue)
        {
            VramUsedText.Text = $"{snapshot.VramUsedGb.Value:F1} GB";
            VramMaxText.Text = $"{(snapshot.VramUsedMaxGb ?? snapshot.VramUsedGb.Value):F1} GB";
        }
        else
        {
            VramUsedText.Text = "-- GB";
            VramMaxText.Text = "-- GB";
        }

        NetUpText.Text = FormatSpeed(snapshot.NetUploadBytesPerSecond);
        NetUpMaxText.Text = FormatSpeed(snapshot.NetUploadMaxBytesPerSecond);
        NetDownText.Text = FormatSpeed(snapshot.NetDownloadBytesPerSecond);
        NetDownMaxText.Text = FormatSpeed(snapshot.NetDownloadMaxBytesPerSecond);
    }

    private void SetLock(bool lockIt)
    {
        _isLocked = lockIt;

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int extendedStyle = GetWindowLong(hwnd, GwlExStyle);

        if (lockIt)
        {
            SetWindowLong(hwnd, GwlExStyle, extendedStyle | WsExTransparent);
        }
        else
        {
            SetWindowLong(hwnd, GwlExStyle, extendedStyle & ~WsExTransparent);
        }

        LockMenuItem.Header = lockIt ? "√ 锁定中 (右键托盘解锁)" : "锁定 (鼠标穿透)";
        if (_trayLockMenuItem != null)
        {
            _trayLockMenuItem.Text = lockIt ? "√ 锁定中 (鼠标穿透)" : "锁定 (鼠标穿透)";
        }

        SaveConfig();
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => SetLock(!_isLocked);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Topmost = true;
        EnsureWindowIsVisible();
        FixedToRight();

        if (_isLocked)
        {
            SetLock(true);
        }
    }

    private void FixedToRight()
    {
        Left = SystemParameters.WorkArea.Width - FullWidth;
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                ApplyVisibilitySettings();
                return;
            }

            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath));
            if (config != null)
            {
                Top = config.Top;
                _isLocked = config.IsLocked;
                _showRam = config.ShowRam;
                _showVram = config.ShowVram;
                _showUpload = config.ShowUpload;
                _showDownload = config.ShowDownload;
            }

            ApplyVisibilitySettings();
        }
        catch
        {
        }
    }

    private void SaveConfig()
    {
        try
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(new AppConfig
            {
                Top = Top,
                Left = Left,
                IsDockedRight = true,
                IsLocked = _isLocked,
                ShowRam = _showRam,
                ShowVram = _showVram,
                ShowUpload = _showUpload,
                ShowDownload = _showDownload
            }));
        }
        catch
        {
        }
    }

    private void EnsureWindowIsVisible()
    {
        double currentHeight = ActualHeight > 0 ? ActualHeight : 130;
        double maxTop = Math.Max(0, SystemParameters.WorkArea.Height - currentHeight);
        if (Top < 0 || Top > maxTop)
        {
            Top = Math.Min(100, maxTop);
        }
    }

    private void ApplyVisibilitySettings()
    {
        RamRow.Visibility = _showRam ? Visibility.Visible : Visibility.Collapsed;
        VramRow.Visibility = _showVram ? Visibility.Visible : Visibility.Collapsed;
        NetUpRow.Visibility = _showUpload ? Visibility.Visible : Visibility.Collapsed;
        NetDownRow.Visibility = _showDownload ? Visibility.Visible : Visibility.Collapsed;
        UpdateVisibilityMenuItems();
    }

    private void UpdateVisibilityMenuItems()
    {
        ShowRamMenuItem.IsChecked = _showRam;
        ShowVramMenuItem.IsChecked = _showVram;
        ShowUpMenuItem.IsChecked = _showUpload;
        ShowDownMenuItem.IsChecked = _showDownload;

        if (_trayShowRamMenuItem != null)
        {
            _trayShowRamMenuItem.Checked = _showRam;
        }

        if (_trayShowVramMenuItem != null)
        {
            _trayShowVramMenuItem.Checked = _showVram;
        }

        if (_trayShowUpMenuItem != null)
        {
            _trayShowUpMenuItem.Checked = _showUpload;
        }

        if (_trayShowDownMenuItem != null)
        {
            _trayShowDownMenuItem.Checked = _showDownload;
        }
    }

    private void SetMetricVisibility(MetricVisibility metric, bool isVisible)
    {
        switch (metric)
        {
            case MetricVisibility.Ram:
                _showRam = isVisible;
                break;
            case MetricVisibility.Vram:
                _showVram = isVisible;
                break;
            case MetricVisibility.Upload:
                _showUpload = isVisible;
                break;
            case MetricVisibility.Download:
                _showDownload = isVisible;
                break;
        }

        ApplyVisibilitySettings();
        SaveConfig();
    }

    private void RestoreDefaultState()
    {
        _showRam = true;
        _showVram = true;
        _showUpload = true;
        _showDownload = true;
        ResetMaxValues();
        ApplyVisibilitySettings();
        ResetWindowPosition();
        if (_isLocked)
        {
            SetLock(false);
        }

        EnsureWindowIsVisible();
        FixedToRight();
        SaveConfig();
    }

    private void ResetWindowPosition()
    {
        Top = 100;
        Left = SystemParameters.WorkArea.Width - FullWidth;
    }

    private string GetStartupShortcutPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "TempMonitor.lnk");

    private void ToggleStartup()
    {
        string path = GetStartupShortcutPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? throw new InvalidOperationException("无法获取当前程序路径。");
                Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("WScript.Shell 不可用。");
                dynamic shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("无法创建快捷方式对象。");
                dynamic shortcut = shell.CreateShortcut(path);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Save();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("失败: " + ex.Message);
            }
        }

        UpdateStartupMenuItem();
    }

    private void UpdateStartupMenuItem()
    {
        bool enabled = File.Exists(GetStartupShortcutPath());
        StartupMenuItem.Header = enabled ? "√ 开机自启" : "开机自启";
        if (_trayStartupMenuItem != null)
        {
            _trayStartupMenuItem.Text = enabled ? "√ 开机自启" : "开机自启";
        }
    }

    private void Startup_Click(object sender, RoutedEventArgs e) => ToggleStartup();

    private void ShowRam_Click(object sender, RoutedEventArgs e) => SetMetricVisibility(MetricVisibility.Ram, !_showRam);

    private void ShowVram_Click(object sender, RoutedEventArgs e) => SetMetricVisibility(MetricVisibility.Vram, !_showVram);

    private void ShowUp_Click(object sender, RoutedEventArgs e) => SetMetricVisibility(MetricVisibility.Upload, !_showUpload);

    private void ShowDown_Click(object sender, RoutedEventArgs e) => SetMetricVisibility(MetricVisibility.Download, !_showDownload);

    private void RestoreDefaultState_Click(object sender, RoutedEventArgs e) => RestoreDefaultState();

    private void IdleTimer_Tick(object? sender, EventArgs e)
    {
        AnimateBackgroundOpacity(IdleBackgroundOpacity, TimeSpan.FromSeconds(1));
        AnimateMainContentOpacity(IdleTextOpacity, TimeSpan.FromSeconds(1));
        _idleTimer?.Stop();
    }

    private void MainBorder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _idleTimer?.Stop();
        AnimateBackgroundOpacity(1.0, TimeSpan.FromSeconds(0.2));
        AnimateMainContentOpacity(1.0, TimeSpan.FromSeconds(0.2));

        double duration = AnimDurationMs;
        MainBorder.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(FullWidth, TimeSpan.FromMilliseconds(duration))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });

        AnimateMaxContainer(MaxContainerWidth, duration);
        AnimateOpacity(HeaderGrid, 1, duration);
    }

    private void MainBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _idleTimer?.Start();

        double duration = AnimDurationMs;
        MainBorder.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(BaseWidth, TimeSpan.FromMilliseconds(duration))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });

        AnimateMaxContainer(0, duration);
        AnimateOpacity(HeaderGrid, 0, duration);
    }

    private void AnimateMaxContainer(double target, double milliseconds) =>
        MaxContainer.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            });

    private void AnimateOpacity(UIElement element, double target, double milliseconds) =>
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds)));

    private void AnimateBackgroundOpacity(double targetOpacity, TimeSpan duration) =>
        MainBackgroundBrush.BeginAnimation(SolidColorBrush.OpacityProperty, new DoubleAnimation(targetOpacity, duration));

    private void AnimateMainContentOpacity(double targetOpacity, TimeSpan duration) =>
        MainStack.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(targetOpacity, duration));

    private void UpdateIndicator(System.Windows.Controls.Border indicator, float value)
    {
        if (value >= 90)
        {
            indicator.Background = CriticalBrush;
            indicator.Opacity = 1;
            return;
        }

        if (value >= 80)
        {
            indicator.Background = WarningBrush;
            indicator.Opacity = 1;
            return;
        }

        indicator.Opacity = 0;
    }

    private static System.Windows.Media.Brush CreateFrozenBrush(string colorHex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Brush GetAlertBrush(float value)
    {
        if (value >= 90)
        {
            return CriticalBrush;
        }

        if (value >= 80)
        {
            return WarningBrush;
        }

        return System.Windows.Media.Brushes.White;
    }

    private static string FormatSpeed(float bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond:0.0}B";
        }

        float kb = bytesPerSecond / 1024;
        if (kb < 1024)
        {
            return $"{kb:0.0}K";
        }

        return $"{kb / 1024.0:0.1}M";
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
            FixedToRight();
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        FixedToRight();
        SaveConfig();
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _dashboardWindow ??= new DashboardWindow();
        if (!_dashboardWindow.IsVisible)
        {
            _dashboardWindow.Show();
        }

        if (_dashboardWindow.WindowState == WindowState.Minimized)
        {
            _dashboardWindow.WindowState = WindowState.Normal;
        }

        _dashboardWindow.Activate();
    }

    private void ResetMaxValues() => HardwareMonitorService.Instance.ResetMaxValues();

    private void ResetMax_Click(object sender, RoutedEventArgs e) => ResetMaxValues();

    private void ExitApplication()
    {
        if (_dashboardWindow != null)
        {
            _dashboardWindow.PrepareForExit();
            _dashboardWindow.Close();
            _dashboardWindow = null;
        }

        System.Windows.Application.Current.Shutdown();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    protected override void OnClosed(EventArgs e)
    {
        HardwareMonitorService.Instance.DataUpdated -= OnHardwareDataUpdated;
        _notifyIcon?.Dispose();
        SaveConfig();
        _idleTimer?.Stop();
        base.OnClosed(e);
    }

    private enum MetricVisibility
    {
        Ram,
        Vram,
        Upload,
        Download
    }
}
