using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TempMonitor;

public enum WidgetTheme
{
    Dark,
    Light
}

internal sealed class ThemeProfile
{
    public System.Windows.Media.Color BackgroundColor { get; init; }
    public System.Windows.Media.Color BorderColor { get; init; }
    public System.Windows.Media.Color LabelColor { get; init; }
    public System.Windows.Media.Color ValueColor { get; init; }
    public System.Windows.Media.Color MaxColor { get; init; }
    public System.Windows.Media.Color IndicatorColor { get; init; }
    public System.Windows.Media.Color NetUpColor { get; init; }
    public System.Windows.Media.Color NetDownColor { get; init; }
    public System.Windows.Media.Color FlashBaseColor { get; init; }
    public System.Windows.Media.Color FlashAlertColor { get; init; }

    public static ThemeProfile Dark => new()
    {
        BackgroundColor = System.Windows.Media.Color.FromRgb(0x14, 0x17, 0x1C),
        BorderColor = System.Windows.Media.Color.FromArgb(0x3A, 0xF2, 0xF6, 0xFF),
        LabelColor = System.Windows.Media.Color.FromRgb(0xD6, 0xDE, 0xE8),
        ValueColor = System.Windows.Media.Color.FromRgb(0xF8, 0xFB, 0xFF),
        MaxColor = System.Windows.Media.Color.FromRgb(0xFF, 0xD3, 0x6B),
        IndicatorColor = System.Windows.Media.Color.FromRgb(0xFF, 0x74, 0x48),
        NetUpColor = System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0xA7),
        NetDownColor = System.Windows.Media.Color.FromRgb(0x9F, 0xF3, 0xC6),
        FlashBaseColor = System.Windows.Media.Color.FromRgb(0x14, 0x17, 0x1C),
        FlashAlertColor = System.Windows.Media.Color.FromRgb(0x6B, 0x14, 0x14)
    };

    public static ThemeProfile Light => new()
    {
        BackgroundColor = System.Windows.Media.Color.FromRgb(0xF0, 0xF2, 0xF5),
        BorderColor = System.Windows.Media.Color.FromArgb(0x3A, 0x88, 0x88, 0x88),
        LabelColor = System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x44),
        ValueColor = System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x2E),
        MaxColor = System.Windows.Media.Color.FromRgb(0xB8, 0x86, 0x0B),
        IndicatorColor = System.Windows.Media.Color.FromRgb(0xFF, 0x44, 0x44),
        NetUpColor = System.Windows.Media.Color.FromRgb(0xCC, 0x44, 0x44),
        NetDownColor = System.Windows.Media.Color.FromRgb(0x22, 0xAA, 0x66),
        FlashBaseColor = System.Windows.Media.Color.FromRgb(0xF0, 0xF2, 0xF5),
        FlashAlertColor = System.Windows.Media.Color.FromRgb(0xFF, 0xDD, 0xDD)
    };
}

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
    public WidgetTheme Theme { get; set; } = WidgetTheme.Dark;
    public double WidgetOpacity { get; set; } = 0.78;
    public int DelayedStartSeconds { get; set; }
}

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WmHotkey = 0x0312;
    private const int HotkeyToggleId = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkM = 0x4D;
    private const double BaseWidth = 135;
    private const double FullWidth = 205;
    private const double AnimDurationMs = 200;
    private const double MaxContainerWidth = 60;

    private readonly string _configPath;
    private DispatcherTimer? _idleTimer;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ToolStripMenuItem? _trayLockMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayStartupMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayShowRamMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayShowVramMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayShowUpMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayShowDownMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayDarkThemeMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayLightThemeMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayExportCsvMenuItem;
    private DashboardWindow? _dashboardWindow;

    private bool _isLocked;
    private bool _showRam = true;
    private bool _showVram = true;
    private bool _showUpload = true;
    private bool _showDownload = true;
    private bool _isFlashing;
    private WidgetTheme _currentTheme = WidgetTheme.Dark;
    private double _widgetOpacity = 0.78;
    private int _delayedStartSeconds;

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
        _trayLockMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u9501\u5B9A (\u9F20\u6807\u7A7F\u900F)", null, (_, _) => SetLock(!_isLocked));
        _trayStartupMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u5F00\u673A\u81EA\u542F", null, (_, _) => ToggleStartup());
        _trayShowRamMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u663E\u793A\u5185\u5B58 (RAM)", null, (_, _) => SetMetricVisibility(MetricVisibility.Ram, !_showRam));
        _trayShowVramMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u663E\u793A\u663E\u5B58 (VRAM)", null, (_, _) => SetMetricVisibility(MetricVisibility.Vram, !_showVram));
        _trayShowUpMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u663E\u793A\u4E0A\u4F20 (UP)", null, (_, _) => SetMetricVisibility(MetricVisibility.Upload, !_showUpload));
        _trayShowDownMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u663E\u793A\u4E0B\u8F7D (DN)", null, (_, _) => SetMetricVisibility(MetricVisibility.Download, !_showDownload));
        _trayDarkThemeMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u6DF1\u8272\u4E3B\u9898", null, (_, _) => SetTheme(WidgetTheme.Dark));
        _trayLightThemeMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u6D45\u8272\u4E3B\u9898", null, (_, _) => SetTheme(WidgetTheme.Light));
        _trayExportCsvMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u5BFC\u51FA CSV", null, (_, _) => ExportCsv());

        var visibilityMenu = new System.Windows.Forms.ToolStripMenuItem("\u663E\u793A\u9879\u76EE");
        visibilityMenu.DropDownItems.Add(_trayShowRamMenuItem);
        visibilityMenu.DropDownItems.Add(_trayShowVramMenuItem);
        visibilityMenu.DropDownItems.Add(_trayShowUpMenuItem);
        visibilityMenu.DropDownItems.Add(_trayShowDownMenuItem);

        var themeMenu = new System.Windows.Forms.ToolStripMenuItem("\u4E3B\u9898");
        themeMenu.DropDownItems.Add(_trayDarkThemeMenuItem);
        themeMenu.DropDownItems.Add(_trayLightThemeMenuItem);

        contextMenu.Items.Add(_trayLockMenuItem);
        contextMenu.Items.Add("-");
        contextMenu.Items.Add(_trayStartupMenuItem);
        contextMenu.Items.Add("-");
        contextMenu.Items.Add(visibilityMenu);
        contextMenu.Items.Add(themeMenu);
        contextMenu.Items.Add(_trayExportCsvMenuItem);
        contextMenu.Items.Add("\u6062\u590D\u9ED8\u8BA4\u72B6\u6001", null, (_, _) => RestoreDefaultState());
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("\u91CD\u7F6E\u6700\u5927\u503C", null, (_, _) => ResetMaxValues());
        contextMenu.Items.Add("\u9000\u51FA (Exit)", null, (_, _) => ExitApplication());
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
        CpuUsageText.Foreground = UiHelper.GetAlertBrush(snapshot.CpuUsage);
        UpdateIndicator(CpuIndicator, snapshot.CpuUsage);

        if (snapshot.GpuTemperature.HasValue)
        {
            GpuTempText.Text = $"{snapshot.GpuTemperature.Value:0.0} \u00B0C";
            GpuMaxText.Text = $"{(snapshot.GpuTemperatureMax ?? snapshot.GpuTemperature.Value):0.0} \u00B0C";
            GpuTempText.Foreground = UiHelper.GetAlertBrush(snapshot.GpuTemperature.Value);
            UpdateIndicator(GpuIndicator, snapshot.GpuTemperature.Value);
        }
        else
        {
            GpuTempText.Text = "-- \u00B0C";
            GpuMaxText.Text = "-- \u00B0C";
            GpuTempText.Foreground = UiHelper.NormalBrush;
            UpdateIndicator(GpuIndicator, 0);
        }

        RamUsedText.Text = $"{snapshot.RamUsedGb:F1} GB";
        RamMaxText.Text = $"{snapshot.RamUsedMaxGb:F1} GB";
        RamUsedText.Foreground = UiHelper.GetAlertBrush(snapshot.RamUsagePercent);
        UpdateIndicator(RamIndicator, snapshot.RamUsagePercent);

        VramUsedText.Text = UiHelper.FormatOptionalGb(snapshot.VramUsedGb);
        VramMaxText.Text = UiHelper.FormatOptionalGb(snapshot.VramUsedMaxGb);

        NetUpText.Text = UiHelper.FormatSpeed(snapshot.NetUploadBytesPerSecond);
        NetUpMaxText.Text = UiHelper.FormatSpeed(snapshot.NetUploadMaxBytesPerSecond);
        NetDownText.Text = UiHelper.FormatSpeed(snapshot.NetDownloadBytesPerSecond);
        NetDownMaxText.Text = UiHelper.FormatSpeed(snapshot.NetDownloadMaxBytesPerSecond);

        CheckAlerts(snapshot);
        UpdateTrayTooltip(snapshot);
    }

    private void UpdateTrayTooltip(HardwareSnapshot snapshot)
    {
        if (_notifyIcon == null) return;

        var gpuTemp = snapshot.GpuTemperature.HasValue ? $"{snapshot.GpuTemperature.Value:0.0}\u00B0C" : "--";
        var topProcess = snapshot.TopGpuProcess;
        var processLine = topProcess != null ? $"\nGPU\u8FDB\u7A0B: {topProcess}" : "";

        var text = $"CPU {snapshot.CpuUsage:0.0}% | GPU {gpuTemp}\n" +
                   $"RAM {snapshot.RamUsedGb:F1}GB ({snapshot.RamUsagePercent:0.0}%)" +
                   $"{processLine}";

        if (text.Length > 127)
            text = text.Substring(0, 127);

        _notifyIcon.Text = text;
    }

    private void CheckAlerts(HardwareSnapshot snapshot)
    {
        bool alert = false;

        if (snapshot.GpuTemperature.HasValue && snapshot.GpuTemperature.Value >= 85)
            alert = true;

        if (snapshot.RamUsagePercent >= 90)
            alert = true;

        if (alert && !_isFlashing)
        {
            _isFlashing = true;
            StartFlashAnimation();
        }
        else if (!alert && _isFlashing)
        {
            _isFlashing = false;
            StopFlashAnimation();
        }
    }

    private void StartFlashAnimation()
    {
        var profile = _currentTheme == WidgetTheme.Light ? ThemeProfile.Light : ThemeProfile.Dark;
        var colorAnim = new ColorAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.6),
            RepeatBehavior = RepeatBehavior.Forever
        };
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(profile.FlashBaseColor, KeyTime.FromPercent(0)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(profile.FlashAlertColor, KeyTime.FromPercent(0.3)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(profile.FlashBaseColor, KeyTime.FromPercent(0.6)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(profile.FlashAlertColor, KeyTime.FromPercent(0.9)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(profile.FlashBaseColor, KeyTime.FromPercent(1)));
        MainBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
    }

    private void StopFlashAnimation()
    {
        MainBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        var profile = _currentTheme == WidgetTheme.Light ? ThemeProfile.Light : ThemeProfile.Dark;
        MainBackgroundBrush.Color = profile.BackgroundColor;
    }

    private void SetTheme(WidgetTheme theme)
    {
        _currentTheme = theme;
        ApplyTheme();
        SaveConfig();
    }

    private void ApplyTheme()
    {
        var profile = _currentTheme == WidgetTheme.Light ? ThemeProfile.Light : ThemeProfile.Dark;

        Resources["ThemeLabelBrush"] = new SolidColorBrush(profile.LabelColor);
        Resources["ThemeValueBrush"] = new SolidColorBrush(profile.ValueColor);
        Resources["ThemeMaxBrush"] = new SolidColorBrush(profile.MaxColor);
        Resources["ThemeIndicatorBrush"] = new SolidColorBrush(profile.IndicatorColor);
        Resources["ThemeNetUpBrush"] = new SolidColorBrush(profile.NetUpColor);
        Resources["ThemeNetDownBrush"] = new SolidColorBrush(profile.NetDownColor);
        Resources["ThemeBorderBrush"] = new SolidColorBrush(profile.BorderColor);
        Resources["ThemeFlashBaseColor"] = profile.FlashBaseColor;
        Resources["ThemeFlashAlertColor"] = profile.FlashAlertColor;

        if (!_isFlashing)
        {
            MainBackgroundBrush.Color = profile.BackgroundColor;
        }

        DarkThemeMenuItem.IsChecked = _currentTheme == WidgetTheme.Dark;
        LightThemeMenuItem.IsChecked = _currentTheme == WidgetTheme.Light;

        if (_trayDarkThemeMenuItem != null)
            _trayDarkThemeMenuItem.Checked = _currentTheme == WidgetTheme.Dark;
        if (_trayLightThemeMenuItem != null)
            _trayLightThemeMenuItem.Checked = _currentTheme == WidgetTheme.Light;
    }

    private void SetWidgetOpacity(double opacity)
    {
        _widgetOpacity = opacity;
        UpdateOpacityMenuItems();
        SaveConfig();
    }

    private void UpdateOpacityMenuItems()
    {
        Opacity50MenuItem.IsChecked = Math.Abs(_widgetOpacity - 0.50) < 0.01;
        Opacity65MenuItem.IsChecked = Math.Abs(_widgetOpacity - 0.65) < 0.01;
        Opacity80MenuItem.IsChecked = Math.Abs(_widgetOpacity - 0.80) < 0.01;
        Opacity95MenuItem.IsChecked = Math.Abs(_widgetOpacity - 0.95) < 0.01;
    }

    private void UpdateDelayMenuItems()
    {
        DelayOffMenuItem.IsChecked = _delayedStartSeconds == 0;
        Delay10MenuItem.IsChecked = _delayedStartSeconds == 10;
        Delay20MenuItem.IsChecked = _delayedStartSeconds == 20;
        Delay30MenuItem.IsChecked = _delayedStartSeconds == 30;
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

        LockMenuItem.Header = lockIt ? "\u2713 \u9501\u5B9A\u4E2D (\u53F3\u952E\u6258\u76D8\u89E3\u9501)" : "\u9501\u5B9A (\u9F20\u6807\u7A7F\u900F)";
        if (_trayLockMenuItem != null)
        {
            _trayLockMenuItem.Text = lockIt ? "\u2713 \u9501\u5B9A\u4E2D (\u9F20\u6807\u7A7F\u900F)" : "\u9501\u5B9A (\u9F20\u6807\u7A7F\u900F)";
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

        ApplyTheme();
        UpdateOpacityMenuItems();
        UpdateDelayMenuItems();

        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        RegisterHotKey(helper.Handle, HotkeyToggleId, ModControl | ModShift, VkM);
        System.Windows.Interop.HwndSource.FromHwnd(helper.Handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyToggleId)
        {
            ToggleVisibility();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ToggleVisibility()
    {
        if (Visibility == Visibility.Visible)
        {
            Hide();
        }
        else
        {
            Show();
            Topmost = true;
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

            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath), options);
            if (config != null)
            {
                Top = config.Top;
                _isLocked = config.IsLocked;
                _showRam = config.ShowRam;
                _showVram = config.ShowVram;
                _showUpload = config.ShowUpload;
                _showDownload = config.ShowDownload;
                _currentTheme = config.Theme;
                _widgetOpacity = config.WidgetOpacity;
                _delayedStartSeconds = config.DelayedStartSeconds;
            }

            ApplyVisibilitySettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"\u52A0\u8F7D\u914D\u7F6E\u5931\u8D25: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(new AppConfig
            {
                Top = Top,
                Left = Left,
                IsDockedRight = true,
                IsLocked = _isLocked,
                ShowRam = _showRam,
                ShowVram = _showVram,
                ShowUpload = _showUpload,
                ShowDownload = _showDownload,
                Theme = _currentTheme,
                WidgetOpacity = _widgetOpacity,
                DelayedStartSeconds = _delayedStartSeconds
            }, options));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"\u4FDD\u5B58\u914D\u7F6E\u5931\u8D25: {ex.Message}");
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
            _trayShowRamMenuItem.Checked = _showRam;
        if (_trayShowVramMenuItem != null)
            _trayShowVramMenuItem.Checked = _showVram;
        if (_trayShowUpMenuItem != null)
            _trayShowUpMenuItem.Checked = _showUpload;
        if (_trayShowDownMenuItem != null)
            _trayShowDownMenuItem.Checked = _showDownload;
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
        _currentTheme = WidgetTheme.Dark;
        _widgetOpacity = 0.78;
        _delayedStartSeconds = 0;
        ResetMaxValues();
        ApplyVisibilitySettings();
        ApplyTheme();
        UpdateOpacityMenuItems();
        UpdateDelayMenuItems();
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
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"\u5220\u9664\u81EA\u542F\u5FEB\u6377\u65B9\u5F0F\u5931\u8D25: {ex.Message}");
            }
        }
        else
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? throw new InvalidOperationException("\u65E0\u6CD5\u83B7\u53D6\u5F53\u524D\u7A0B\u5E8F\u8DEF\u5F84\u3002");
                Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("WScript.Shell \u4E0D\u53EF\u7528\u3002");
                dynamic shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("\u65E0\u6CD5\u521B\u5EFA\u5FEB\u6377\u65B9\u5F0F\u5BF9\u8C61\u3002");
                dynamic shortcut = shell.CreateShortcut(path);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                if (_delayedStartSeconds > 0)
                    shortcut.Arguments = $"--delay {_delayedStartSeconds}";
                shortcut.Save();
                Marshal.ReleaseComObject(shortcut);
                Marshal.ReleaseComObject(shell);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("\u521B\u5EFA\u81EA\u542F\u5FEB\u6377\u65B9\u5F0F\u5931\u8D25: " + ex.Message);
            }
        }

        UpdateStartupMenuItem();
    }

    private void UpdateStartupMenuItem()
    {
        bool enabled = File.Exists(GetStartupShortcutPath());
        StartupMenuItem.Header = enabled ? "\u2713 \u5F00\u673A\u81EA\u542F" : "\u5F00\u673A\u81EA\u542F";
        if (_trayStartupMenuItem != null)
            _trayStartupMenuItem.Text = enabled ? "\u2713 \u5F00\u673A\u81EA\u542F" : "\u5F00\u673A\u81EA\u542F";
    }

    private void Startup_Click(object sender, RoutedEventArgs e) => ToggleStartup();

    private void ShowRam_Click(object sender, RoutedEventArgs e) => SetMetricVisibility(MetricVisibility.Ram, !_showRam);

    private void ShowVram_Click(object sender, RoutedEventArgs e) => SetMetricVisibility(MetricVisibility.Vram, !_showVram);

    private void ShowUp_Click(object sender, RoutedEventArgs e) => SetMetricVisibility(MetricVisibility.Upload, !_showUpload);

    private void ShowDown_Click(object sender, RoutedEventArgs e) => SetMetricVisibility(MetricVisibility.Download, !_showDownload);

    private void RestoreDefaultState_Click(object sender, RoutedEventArgs e) => RestoreDefaultState();

    private void DarkTheme_Click(object sender, RoutedEventArgs e) => SetTheme(WidgetTheme.Dark);

    private void LightTheme_Click(object sender, RoutedEventArgs e) => SetTheme(WidgetTheme.Light);

    private void Opacity50_Click(object sender, RoutedEventArgs e) => SetWidgetOpacity(0.50);
    private void Opacity65_Click(object sender, RoutedEventArgs e) => SetWidgetOpacity(0.65);
    private void Opacity80_Click(object sender, RoutedEventArgs e) => SetWidgetOpacity(0.80);
    private void Opacity95_Click(object sender, RoutedEventArgs e) => SetWidgetOpacity(0.95);

    private void DelayOff_Click(object sender, RoutedEventArgs e) => SetDelayedStart(0);
    private void Delay10_Click(object sender, RoutedEventArgs e) => SetDelayedStart(10);
    private void Delay20_Click(object sender, RoutedEventArgs e) => SetDelayedStart(20);
    private void Delay30_Click(object sender, RoutedEventArgs e) => SetDelayedStart(30);

    private void SetDelayedStart(int seconds)
    {
        _delayedStartSeconds = seconds;
        UpdateDelayMenuItems();
        SaveConfig();
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e) => ExportCsv();

    private void ExportCsv()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"TempMonitor_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".csv",
                Filter = "CSV \u6587\u4EF6|*.csv"
            };

            if (dialog.ShowDialog() != true) return;

            string csv = HardwareMonitorService.Instance.ExportCsv();
            File.WriteAllText(dialog.FileName, csv);
            System.Windows.MessageBox.Show($"\u5DF2\u5BFC\u51FA\u5230 {dialog.FileName}", "\u5BFC\u51FA\u6210\u529F", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"\u5BFC\u51FA\u5931\u8D25: {ex.Message}", "\u9519\u8BEF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void IdleTimer_Tick(object? sender, EventArgs e)
    {
        AnimateBackgroundOpacity(_widgetOpacity, TimeSpan.FromSeconds(1));
        AnimateMainContentOpacity(_widgetOpacity + 0.06, TimeSpan.FromSeconds(1));
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
            indicator.Background = UiHelper.CriticalBrush;
            indicator.Opacity = 1;
            return;
        }

        if (value >= 80)
        {
            indicator.Background = UiHelper.WarningBrush;
            indicator.Opacity = 0.8;
            return;
        }

        indicator.Opacity = 0;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && !_isLocked)
        {
            DragMove();
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SaveConfig();
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_dashboardWindow == null)
        {
            _dashboardWindow = new DashboardWindow();
            _dashboardWindow.Show();
        }
        else
        {
            if (_dashboardWindow.Visibility == Visibility.Visible)
            {
                _dashboardWindow.Hide();
            }
            else
            {
                _dashboardWindow.Show();
            }
        }
    }

    private void ResetMaxValues()
    {
        HardwareMonitorService.Instance.ResetMaxValues();
    }

    private void ResetMax_Click(object sender, RoutedEventArgs e) => ResetMaxValues();

    private void ExitApplication()
    {
        _notifyIcon?.Dispose();
        _notifyIcon = null;

        if (_dashboardWindow != null)
        {
            _dashboardWindow.PrepareForExit();
            _dashboardWindow.Close();
        }

        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        UnregisterHotKey(helper.Handle, HotkeyToggleId);

        SaveConfig();
        HardwareMonitorService.Instance.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    protected override void OnClosed(EventArgs e)
    {
        _notifyIcon?.Dispose();
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
