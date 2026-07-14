using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TempMonitor;

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

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the Window lifetime; CleanupResources deterministically releases UI resources.")]
public partial class MainWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkComObject
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, IntPtr findData, uint flags);
        [PreserveSig]
        int GetIdList(out IntPtr itemIdList);
        [PreserveSig]
        int SetIdList(IntPtr itemIdList);
        [PreserveSig]
        int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig]
        int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig]
        int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig]
        int GetHotKey(out short hotKey);
        [PreserveSig]
        int SetHotKey(short hotKey);
        [PreserveSig]
        int GetShowCommand(out int showCommand);
        [PreserveSig]
        int SetShowCommand(int showCommand);
        [PreserveSig]
        int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int capacity, out int iconIndex);
        [PreserveSig]
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        [PreserveSig]
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig]
        int Resolve(IntPtr windowHandle, uint flags);
        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WmHotkey = 0x0312;
    private const int HotkeyToggleId = 0x0001;
    private const int ShowCommandNormal = 1;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkM = 0x4D;
    private const double BaseWidth = 135;
    private const double FullWidth = 205;
    private const double AnimDurationMs = 200;
    private const double MaxContainerWidth = 60;

    private readonly AppConfig _config;
    private DispatcherTimer? _idleTimer;
    private HwndSource? _windowSource;
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
    private System.Windows.Forms.ToolStripMenuItem? _trayHotkeyMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayProcessTrackingMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayAlertsMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _traySample1MenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _traySample2MenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _traySample5MenuItem;
    private DashboardWindow? _dashboardWindow;
    private HardwareSnapshot? _pendingSnapshot;
    private int _snapshotDispatchScheduled;

    private bool _isLocked;
    private bool _showRam = true;
    private bool _showVram = true;
    private bool _showUpload = true;
    private bool _showDownload = true;
    private bool _isFlashing;
    private bool _isDockedRight = true;
    private bool _enableGlobalHotkey;
    private bool _hotkeyRegistered;
    private bool _trackTopGpuProcess;
    private bool _enableAlerts = true;
    private volatile bool _isExiting;
    private bool _systemEventsSubscribed;
    private WidgetTheme _currentTheme = WidgetTheme.Dark;
    private double _widgetOpacity = 0.78;
    private int _delayedStartSeconds;
    private int _samplingIntervalSeconds = 1;

    public MainWindow() : this(ConfigStore.Load())
    {
    }

    internal MainWindow(AppConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ApplyConfig(config);
        InitializeComponent();
        InitializeTrayIcon();
        ApplyVisibilitySettings();
        HardwareMonitorService.Instance.DataUpdated += OnHardwareDataUpdated;
        HardwareMonitorService.Instance.Configure(_samplingIntervalSeconds, _trackTopGpuProcess);
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
        _notifyIcon.DoubleClick += (_, _) => ToggleVisibility();

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
        _trayHotkeyMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u5168\u5C40\u70ED\u952E Ctrl+Shift+M", null, (_, _) => SetGlobalHotkeyEnabled(!_enableGlobalHotkey));
        _trayProcessTrackingMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u8FDB\u7A0B\u7EA7 GPU \u663E\u5B58", null, (_, _) => SetProcessTrackingEnabled(!_trackTopGpuProcess));
        _trayAlertsMenuItem = new System.Windows.Forms.ToolStripMenuItem("\u544A\u8B66\u95EA\u70C1", null, (_, _) => SetAlertsEnabled(!_enableAlerts));
        _traySample1MenuItem = new System.Windows.Forms.ToolStripMenuItem("1 \u79D2", null, (_, _) => SetSamplingInterval(1));
        _traySample2MenuItem = new System.Windows.Forms.ToolStripMenuItem("2 \u79D2", null, (_, _) => SetSamplingInterval(2));
        _traySample5MenuItem = new System.Windows.Forms.ToolStripMenuItem("5 \u79D2", null, (_, _) => SetSamplingInterval(5));

        var visibilityMenu = new System.Windows.Forms.ToolStripMenuItem("\u663E\u793A\u9879\u76EE");
        visibilityMenu.DropDownItems.Add(_trayShowRamMenuItem);
        visibilityMenu.DropDownItems.Add(_trayShowVramMenuItem);
        visibilityMenu.DropDownItems.Add(_trayShowUpMenuItem);
        visibilityMenu.DropDownItems.Add(_trayShowDownMenuItem);

        var themeMenu = new System.Windows.Forms.ToolStripMenuItem("\u4E3B\u9898");
        themeMenu.DropDownItems.Add(_trayDarkThemeMenuItem);
        themeMenu.DropDownItems.Add(_trayLightThemeMenuItem);

        var samplingMenu = new System.Windows.Forms.ToolStripMenuItem("\u91C7\u6837\u9891\u7387");
        samplingMenu.DropDownItems.Add(_traySample1MenuItem);
        samplingMenu.DropDownItems.Add(_traySample2MenuItem);
        samplingMenu.DropDownItems.Add(_traySample5MenuItem);

        var lowImpactMenu = new System.Windows.Forms.ToolStripMenuItem("\u4F4E\u5E72\u6270\u9009\u9879");
        lowImpactMenu.DropDownItems.Add(_trayHotkeyMenuItem);
        lowImpactMenu.DropDownItems.Add(_trayProcessTrackingMenuItem);
        lowImpactMenu.DropDownItems.Add(_trayAlertsMenuItem);
        lowImpactMenu.DropDownItems.Add(samplingMenu);

        contextMenu.Items.Add(_trayLockMenuItem);
        contextMenu.Items.Add("-");
        contextMenu.Items.Add(_trayStartupMenuItem);
        contextMenu.Items.Add("-");
        contextMenu.Items.Add(visibilityMenu);
        contextMenu.Items.Add(themeMenu);
        contextMenu.Items.Add(lowImpactMenu);
        contextMenu.Items.Add(_trayExportCsvMenuItem);
        contextMenu.Items.Add("\u6062\u590D\u9ED8\u8BA4\u72B6\u6001", null, (_, _) => RestoreDefaultState());
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("\u91CD\u7F6E\u6700\u5927\u503C", null, (_, _) => ResetMaxValues());
        contextMenu.Items.Add("\u9000\u51FA (Exit)", null, (_, _) => ExitApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;

        UpdateVisibilityMenuItems();
        UpdateStartupMenuItem();
        UpdateRuntimeOptionMenuItems();
    }

    private void OnHardwareDataUpdated(HardwareSnapshot snapshot)
    {
        Volatile.Write(ref _pendingSnapshot, snapshot);
        TryScheduleSnapshotDispatch();
    }

    private void TryScheduleSnapshotDispatch()
    {
        if (_isExiting || Dispatcher.HasShutdownStarted ||
            Interlocked.CompareExchange(ref _snapshotDispatchScheduled, 1, 0) != 0)
            return;

        try
        {
            _ = Dispatcher.InvokeAsync(ProcessPendingSnapshot, DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _snapshotDispatchScheduled, 0);
        }
    }

    private void ProcessPendingSnapshot()
    {
        try
        {
            HardwareSnapshot? snapshot = Interlocked.Exchange(ref _pendingSnapshot, null);
            if (!_isExiting && snapshot != null)
                ApplySnapshot(snapshot);
        }
        finally
        {
            Interlocked.Exchange(ref _snapshotDispatchScheduled, 0);
            if (Volatile.Read(ref _pendingSnapshot) != null)
                TryScheduleSnapshotDispatch();
        }
    }

    private void ApplySnapshot(HardwareSnapshot snapshot)
    {
        if (snapshot.HasCpuUsage)
        {
            CpuUsageText.Text = $"{snapshot.CpuUsage:0.0} %";
            CpuMaxText.Text = $"{snapshot.CpuUsageMax:0.0} %";
            CpuUsageText.Foreground = UiHelper.GetAlertBrush(snapshot.CpuUsage);
            UpdateIndicator(CpuIndicator, snapshot.CpuUsage);
        }
        else
        {
            CpuUsageText.Text = "-- %";
            CpuMaxText.Text = snapshot.CpuUsageMax > 0 ? $"{snapshot.CpuUsageMax:0.0} %" : "-- %";
            CpuUsageText.Foreground = UiHelper.NormalBrush;
            UpdateIndicator(CpuIndicator, 0);
        }

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

        if (snapshot.HasRamData)
        {
            RamUsedText.Text = $"{snapshot.RamUsedGb:F1} GB";
            RamMaxText.Text = $"{snapshot.RamUsedMaxGb:F1} GB";
            RamUsedText.Foreground = UiHelper.GetAlertBrush(snapshot.RamUsagePercent);
            UpdateIndicator(RamIndicator, snapshot.RamUsagePercent);
        }
        else
        {
            RamUsedText.Text = "-- GB";
            RamMaxText.Text = snapshot.RamUsedMaxGb > 0 ? $"{snapshot.RamUsedMaxGb:F1} GB" : "-- GB";
            RamUsedText.Foreground = UiHelper.NormalBrush;
            UpdateIndicator(RamIndicator, 0);
        }

        VramUsedText.Text = UiHelper.FormatOptionalGb(snapshot.VramUsedGb);
        VramMaxText.Text = UiHelper.FormatOptionalGb(snapshot.VramUsedMaxGb);

        NetUpText.Text = snapshot.HasNetworkData ? UiHelper.FormatSpeed(snapshot.NetUploadBytesPerSecond) : "--";
        NetUpMaxText.Text = UiHelper.FormatSpeed(snapshot.NetUploadMaxBytesPerSecond);
        NetDownText.Text = snapshot.HasNetworkData ? UiHelper.FormatSpeed(snapshot.NetDownloadBytesPerSecond) : "--";
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

        string cpuText = snapshot.HasCpuUsage ? $"{snapshot.CpuUsage:0.0}%" : "--";
        string ramText = snapshot.HasRamData
            ? $"{snapshot.RamUsedGb:F1}GB ({snapshot.RamUsagePercent:0.0}%)"
            : "--";
        var text = $"CPU {cpuText} | GPU {gpuTemp}\n" +
                   $"RAM {ramText}" +
                   $"{processLine}";

        if (text.Length > 127)
            text = text.Substring(0, 127);

        _notifyIcon.Text = text;
    }

    private void CheckAlerts(HardwareSnapshot snapshot)
    {
        if (!_enableAlerts)
        {
            if (_isFlashing)
            {
                _isFlashing = false;
                StopFlashAnimation();
            }

            return;
        }

        bool alert = false;

        if (snapshot.GpuTemperature.HasValue && snapshot.GpuTemperature.Value >= 85)
            alert = true;

        if (snapshot.HasRamData && snapshot.RamUsagePercent >= 90)
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

        SetFrozenResource("ThemeLabelBrush", profile.LabelColor);
        SetFrozenResource("ThemeValueBrush", profile.ValueColor);
        SetFrozenResource("ThemeMaxBrush", profile.MaxColor);
        SetFrozenResource("ThemeIndicatorBrush", profile.IndicatorColor);
        SetFrozenResource("ThemeNetUpBrush", profile.NetUpColor);
        SetFrozenResource("ThemeNetDownBrush", profile.NetDownColor);
        SetFrozenResource("ThemeBorderBrush", profile.BorderColor);
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

    private void SetFrozenResource(string key, System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
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

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int extendedStyle = GetWindowLong(hwnd, GwlExStyle);
            int newStyle = lockIt
                ? extendedStyle | WsExTransparent | WsExNoActivate
                : (extendedStyle & ~WsExTransparent) | WsExNoActivate;
            int previousStyle = SetWindowLong(hwnd, GwlExStyle, newStyle);
            if (previousStyle == 0 && Marshal.GetLastPInvokeError() != 0)
                Debug.WriteLine($"\u66F4\u65B0\u7A97\u53E3\u6837\u5F0F\u5931\u8D25: {Marshal.GetLastPInvokeError()}");
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
        if (_isDockedRight)
            FixedToRight();

        SetLock(_isLocked);

        ApplyTheme();
        UpdateOpacityMenuItems();
        UpdateDelayMenuItems();
        UpdateRuntimeOptionMenuItems();

        var helper = new WindowInteropHelper(this);
        _windowSource = HwndSource.FromHwnd(helper.Handle);
        _windowSource?.AddHook(WndProc);
        UpdateHotkeyRegistration();
        SubscribeToDisplayChanges();
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
        Rect workArea = GetCurrentWorkArea();
        Left = workArea.Right - FullWidth;
        Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Math.Max(ActualHeight, 100)));
    }

    private void ApplyConfig(AppConfig config)
    {
        config.Normalize();
        Top = config.Top;
        Left = config.Left;
        _isDockedRight = config.IsDockedRight;
        _isLocked = config.IsLocked;
        _showRam = config.ShowRam;
        _showVram = config.ShowVram;
        _showUpload = config.ShowUpload;
        _showDownload = config.ShowDownload;
        _currentTheme = config.Theme;
        _widgetOpacity = config.WidgetOpacity;
        _delayedStartSeconds = config.DelayedStartSeconds;
        _enableGlobalHotkey = config.EnableGlobalHotkey;
        _trackTopGpuProcess = config.TrackTopGpuProcess;
        _enableAlerts = config.EnableAlerts;
        _samplingIntervalSeconds = config.SamplingIntervalSeconds;
    }

    private void SaveConfig()
    {
        _config.Top = Top;
        _config.Left = Left;
        _config.IsDockedRight = _isDockedRight;
        _config.IsLocked = _isLocked;
        _config.ShowRam = _showRam;
        _config.ShowVram = _showVram;
        _config.ShowUpload = _showUpload;
        _config.ShowDownload = _showDownload;
        _config.Theme = _currentTheme;
        _config.WidgetOpacity = _widgetOpacity;
        _config.DelayedStartSeconds = _delayedStartSeconds;
        _config.EnableGlobalHotkey = _enableGlobalHotkey;
        _config.TrackTopGpuProcess = _trackTopGpuProcess;
        _config.EnableAlerts = _enableAlerts;
        _config.SamplingIntervalSeconds = _samplingIntervalSeconds;

        if (!ConfigStore.TrySave(_config))
            Debug.WriteLine("\u4FDD\u5B58\u914D\u7F6E\u5931\u8D25\u3002");
    }

    private void EnsureWindowIsVisible()
    {
        Rect workArea = GetCurrentWorkArea();
        double currentWidth = ActualWidth > 0 ? ActualWidth : FullWidth;
        double currentHeight = ActualHeight > 0 ? ActualHeight : 130;
        Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - currentWidth));
        Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - currentHeight));
    }

    private Rect GetCurrentWorkArea()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        System.Windows.Forms.Screen screen = hwnd != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(hwnd)
            : System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        System.Drawing.Rectangle pixels = screen.WorkingArea;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            pixels.Left / dpi.DpiScaleX,
            pixels.Top / dpi.DpiScaleY,
            pixels.Width / dpi.DpiScaleX,
            pixels.Height / dpi.DpiScaleY);
    }

    private void SubscribeToDisplayChanges()
    {
        if (_systemEventsSubscribed) return;

        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            _systemEventsSubscribed = true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            UnsubscribeFromDisplayChanges();
            Debug.WriteLine($"\u65E0\u6CD5\u76D1\u542C\u663E\u793A\u5668\u53D8\u5316: {ex.Message}");
        }
    }

    private void UnsubscribeFromDisplayChanges()
    {
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            Debug.WriteLine($"\u53D6\u6D88\u663E\u793A\u5668\u53D8\u5316\u76D1\u542C\u5931\u8D25: {ex.Message}");
        }

        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            Debug.WriteLine($"\u53D6\u6D88\u7528\u6237\u504F\u597D\u76D1\u542C\u5931\u8D25: {ex.Message}");
        }

        _systemEventsSubscribed = false;
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e) =>
        ScheduleDisplayPositionRefresh();

    private void SystemEvents_UserPreferenceChanged(
        object? sender,
        Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.Desktop or
            Microsoft.Win32.UserPreferenceCategory.General)
        {
            ScheduleDisplayPositionRefresh();
        }
    }

    private void ScheduleDisplayPositionRefresh()
    {
        if (_isExiting || Dispatcher.HasShutdownStarted) return;

        try
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_isExiting || Dispatcher.HasShutdownStarted) return;

                EnsureWindowIsVisible();
                if (_isDockedRight)
                    FixedToRight();
                SaveConfig();
            }, DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ScheduleDisplayPositionRefresh();
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

    private void UpdateRuntimeOptionMenuItems()
    {
        HotkeyMenuItem.IsChecked = _enableGlobalHotkey;
        HotkeyMenuItem.Header = _enableGlobalHotkey && IsLoaded && !_hotkeyRegistered
            ? "\u5168\u5C40\u70ED\u952E Ctrl+Shift+M\uFF08\u5DF2\u88AB\u5360\u7528\uFF09"
            : "\u5168\u5C40\u70ED\u952E Ctrl+Shift+M";
        ProcessTrackingMenuItem.IsChecked = _trackTopGpuProcess;
        AlertsMenuItem.IsChecked = _enableAlerts;
        Sample1MenuItem.IsChecked = _samplingIntervalSeconds == 1;
        Sample2MenuItem.IsChecked = _samplingIntervalSeconds == 2;
        Sample5MenuItem.IsChecked = _samplingIntervalSeconds == 5;

        if (_trayHotkeyMenuItem != null)
        {
            _trayHotkeyMenuItem.Checked = _enableGlobalHotkey;
            _trayHotkeyMenuItem.Text = HotkeyMenuItem.Header?.ToString() ?? "\u5168\u5C40\u70ED\u952E Ctrl+Shift+M";
        }

        if (_trayProcessTrackingMenuItem != null)
            _trayProcessTrackingMenuItem.Checked = _trackTopGpuProcess;
        if (_trayAlertsMenuItem != null)
            _trayAlertsMenuItem.Checked = _enableAlerts;
        if (_traySample1MenuItem != null)
            _traySample1MenuItem.Checked = _samplingIntervalSeconds == 1;
        if (_traySample2MenuItem != null)
            _traySample2MenuItem.Checked = _samplingIntervalSeconds == 2;
        if (_traySample5MenuItem != null)
            _traySample5MenuItem.Checked = _samplingIntervalSeconds == 5;
    }

    private void SetGlobalHotkeyEnabled(bool enabled)
    {
        _enableGlobalHotkey = enabled;
        UpdateHotkeyRegistration();
        SaveConfig();
    }

    private void UpdateHotkeyRegistration()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (_hotkeyRegistered && !_enableGlobalHotkey)
        {
            if (UnregisterHotKey(hwnd, HotkeyToggleId))
            {
                _hotkeyRegistered = false;
            }
            else
            {
                _enableGlobalHotkey = true;
                Debug.WriteLine($"\u53D6\u6D88\u6CE8\u518C\u5168\u5C40\u70ED\u952E\u5931\u8D25: {Marshal.GetLastPInvokeError()}");
            }
        }
        else if (!_hotkeyRegistered && _enableGlobalHotkey)
        {
            _hotkeyRegistered = RegisterHotKey(
                hwnd,
                HotkeyToggleId,
                ModControl | ModShift | ModNoRepeat,
                VkM);
        }

        UpdateRuntimeOptionMenuItems();
    }

    private void SetProcessTrackingEnabled(bool enabled)
    {
        _trackTopGpuProcess = enabled;
        HardwareMonitorService.Instance.SetProcessTrackingEnabled(enabled);
        UpdateRuntimeOptionMenuItems();
        SaveConfig();
    }

    private void SetAlertsEnabled(bool enabled)
    {
        _enableAlerts = enabled;
        if (!enabled && _isFlashing)
        {
            _isFlashing = false;
            StopFlashAnimation();
        }

        UpdateRuntimeOptionMenuItems();
        SaveConfig();
    }

    private void SetSamplingInterval(int seconds)
    {
        _samplingIntervalSeconds = seconds is 1 or 2 or 5 ? seconds : 1;
        HardwareMonitorService.Instance.SetSamplingInterval(_samplingIntervalSeconds);
        UpdateRuntimeOptionMenuItems();
        SaveConfig();
    }

    private void Hotkey_Click(object sender, RoutedEventArgs e) => SetGlobalHotkeyEnabled(!_enableGlobalHotkey);
    private void ProcessTracking_Click(object sender, RoutedEventArgs e) => SetProcessTrackingEnabled(!_trackTopGpuProcess);
    private void Alerts_Click(object sender, RoutedEventArgs e) => SetAlertsEnabled(!_enableAlerts);
    private void Sample1_Click(object sender, RoutedEventArgs e) => SetSamplingInterval(1);
    private void Sample2_Click(object sender, RoutedEventArgs e) => SetSamplingInterval(2);
    private void Sample5_Click(object sender, RoutedEventArgs e) => SetSamplingInterval(5);

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
        _enableGlobalHotkey = false;
        _trackTopGpuProcess = false;
        _enableAlerts = true;
        _samplingIntervalSeconds = 1;
        _isDockedRight = true;
        HardwareMonitorService.Instance.Configure(_samplingIntervalSeconds, _trackTopGpuProcess);
        UpdateHotkeyRegistration();
        ResetMaxValues();
        ApplyVisibilitySettings();
        ApplyTheme();
        UpdateOpacityMenuItems();
        UpdateDelayMenuItems();
        UpdateRuntimeOptionMenuItems();
        ResetWindowPosition();
        if (_isLocked)
        {
            SetLock(false);
        }

        EnsureWindowIsVisible();
        FixedToRight();
        SaveConfig();
        if (IsStartupEnabled())
            CreateOrUpdateStartupShortcut(showError: false);
    }

    private void ResetWindowPosition()
    {
        Top = 100;
        _isDockedRight = true;
        FixedToRight();
    }

    private static string GetStartupShortcutPath() =>
        BuildStartupShortcutPath("gxTempMonitor.lnk");

    private static string GetLegacyStartupShortcutPath() =>
        BuildStartupShortcutPath("TempMonitor.lnk");

    private static string BuildStartupShortcutPath(string fileName)
    {
        string startupDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Startup,
            Environment.SpecialFolderOption.DoNotVerify);
        return !string.IsNullOrWhiteSpace(startupDirectory) &&
               Path.IsPathFullyQualified(startupDirectory)
            ? Path.Combine(startupDirectory, fileName)
            : string.Empty;
    }

    private static bool IsStartupEnabled() =>
        IsOwnedStartupShortcut(GetStartupShortcutPath()) ||
        IsOwnedStartupShortcut(GetLegacyStartupShortcutPath());

    private void ToggleStartup()
    {
        if (IsStartupEnabled())
        {
            try
            {
                DeleteOwnedStartupShortcut(GetStartupShortcutPath());
                DeleteOwnedStartupShortcut(GetLegacyStartupShortcutPath());
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"\u5220\u9664\u81EA\u542F\u5FEB\u6377\u65B9\u5F0F\u5931\u8D25: {ex.Message}");
            }
        }
        else
        {
            CreateOrUpdateStartupShortcut(showError: true);
        }

        UpdateStartupMenuItem();
    }

    private bool CreateOrUpdateStartupShortcut(bool showError)
    {
        IShellLinkW? shortcut = null;
        try
        {
            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("\u65E0\u6CD5\u83B7\u53D6\u5F53\u524D\u7A0B\u5E8F\u8DEF\u5F84\u3002");
            string shortcutPath = GetStartupShortcutPath();
            if (string.IsNullOrWhiteSpace(shortcutPath))
                throw new InvalidOperationException("\u65E0\u6CD5\u83B7\u53D6 Windows \u5F00\u673A\u542F\u52A8\u6587\u4EF6\u5939\u3002");
            if (File.Exists(shortcutPath) && !IsOwnedStartupShortcut(shortcutPath))
            {
                bool? replaceBrokenShortcut = showError
                    ? ConfirmReplacingBrokenStartupShortcut(shortcutPath)
                    : null;
                if (replaceBrokenShortcut == false)
                    return false;
                if (replaceBrokenShortcut != true)
                    throw new InvalidOperationException("\u540C\u540D\u542F\u52A8\u9879\u4E0D\u5C5E\u4E8E gxTempMonitor\uFF0C\u672A\u8FDB\u884C\u8986\u76D6\u3002");
            }

            shortcut = (IShellLinkW)new ShellLinkComObject();
            Marshal.ThrowExceptionForHR(shortcut.SetPath(executablePath));
            Marshal.ThrowExceptionForHR(shortcut.SetWorkingDirectory(Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory));
            Marshal.ThrowExceptionForHR(shortcut.SetArguments(_delayedStartSeconds > 0
                ? $"--startup --delay {_delayedStartSeconds}"
                : "--startup"));
            Marshal.ThrowExceptionForHR(shortcut.SetDescription("gxTempMonitor hardware monitor"));
            Marshal.ThrowExceptionForHR(shortcut.SetIconLocation(executablePath, 0));
            Marshal.ThrowExceptionForHR(shortcut.SetShowCommand(ShowCommandNormal));

            ((System.Runtime.InteropServices.ComTypes.IPersistFile)shortcut).Save(shortcutPath, true);

            DeleteOwnedStartupShortcut(GetLegacyStartupShortcutPath());
            return true;
        }
        catch (Exception ex)
        {
            if (showError)
                System.Windows.MessageBox.Show("\u521B\u5EFA\u81EA\u542F\u5FEB\u6377\u65B9\u5F0F\u5931\u8D25: " + ex.Message);
            else
                Debug.WriteLine($"\u66F4\u65B0\u81EA\u542F\u5FEB\u6377\u65B9\u5F0F\u5931\u8D25: {ex.Message}");
            return false;
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
        }
    }

    private static bool? ConfirmReplacingBrokenStartupShortcut(string shortcutPath)
    {
        if (!TryReadStartupShortcut(shortcutPath, out string targetPath, out _) ||
            !Path.IsPathFullyQualified(targetPath) ||
            File.Exists(targetPath))
        {
            return null;
        }

        MessageBoxResult result = System.Windows.MessageBox.Show(
            $"\u73B0\u6709\u540C\u540D\u5F00\u673A\u542F\u52A8\u9879\u6307\u5411\u4E00\u4E2A\u5DF2\u4E0D\u5B58\u5728\u7684\u6587\u4EF6\uFF1A\n\n{targetPath}\n\n\u662F\u5426\u7528\u5F53\u524D gxTempMonitor \u66FF\u6362\u5B83\uFF1F",
            "gxTempMonitor",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private static bool IsOwnedStartupShortcut(string path)
    {
        if (!TryReadStartupShortcut(path, out string targetPath, out _))
            return false;

        try
        {
            string? currentExecutable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentExecutable) &&
                string.Equals(
                    Path.GetFullPath(targetPath),
                    Path.GetFullPath(currentExecutable),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!File.Exists(targetPath) || string.IsNullOrWhiteSpace(currentExecutable))
                return false;

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(targetPath);
            bool recognizedProduct = string.Equals(version.ProductName, "gxTempMonitor", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(version.ProductName, "TempMonitor", StringComparison.OrdinalIgnoreCase);
            bool recognizedCompany = string.Equals(version.CompanyName, "gxmst", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(version.CompanyName, "TempMonitor", StringComparison.OrdinalIgnoreCase);
            string shortcutName = Path.GetFileName(path);
            string targetName = Path.GetFileName(targetPath);
            bool expectedFileNames =
                (string.Equals(shortcutName, "gxTempMonitor.lnk", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(targetName, "gxTempMonitor.exe", StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(shortcutName, "TempMonitor.lnk", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(targetName, "TempMonitor.exe", StringComparison.OrdinalIgnoreCase));
            bool sameDirectory = string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(targetPath)),
                Path.GetDirectoryName(Path.GetFullPath(currentExecutable)),
                StringComparison.OrdinalIgnoreCase);
            // Product metadata is not a security boundary. Never claim or delete a
            // cross-directory shortcut solely because its metadata looks familiar.
            return expectedFileNames && recognizedProduct && recognizedCompany && sameDirectory;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryReadStartupShortcut(string path, out string targetPath, out string description)
    {
        targetPath = string.Empty;
        description = string.Empty;
        if (!File.Exists(path)) return false;

        IShellLinkW? shortcut = null;
        try
        {
            shortcut = (IShellLinkW)new ShellLinkComObject();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)shortcut).Load(path, 0);

            var target = new StringBuilder(32768);
            Marshal.ThrowExceptionForHR(shortcut.GetPath(target, target.Capacity, IntPtr.Zero, 0));
            var details = new StringBuilder(1024);
            Marshal.ThrowExceptionForHR(shortcut.GetDescription(details, details.Capacity));

            targetPath = target.ToString();
            description = details.ToString();
            return !string.IsNullOrWhiteSpace(targetPath);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
        }
    }

    private static void DeleteOwnedStartupShortcut(string path)
    {
        if (IsOwnedStartupShortcut(path))
            File.Delete(path);
    }

    private void UpdateStartupMenuItem()
    {
        bool enabled = IsStartupEnabled();
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
        _delayedStartSeconds = Math.Clamp(seconds, 0, 60);
        UpdateDelayMenuItems();
        SaveConfig();
        if (IsStartupEnabled())
            CreateOrUpdateStartupShortcut(showError: false);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e) => ExportCsv();

    private static void ExportCsv()
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
            File.WriteAllText(dialog.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
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

    private static void AnimateOpacity(UIElement element, double target, double milliseconds) =>
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds)));

    private void AnimateBackgroundOpacity(double targetOpacity, TimeSpan duration) =>
        MainBackgroundBrush.BeginAnimation(SolidColorBrush.OpacityProperty, new DoubleAnimation(targetOpacity, duration));

    private void AnimateMainContentOpacity(double targetOpacity, TimeSpan duration) =>
        MainStack.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(targetOpacity, duration));

    private static void UpdateIndicator(System.Windows.Controls.Border indicator, float value)
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
            UpdateDockState();
            SaveConfig();
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        UpdateDockState();
        SaveConfig();
    }

    private void UpdateDockState()
    {
        Rect workArea = GetCurrentWorkArea();
        _isDockedRight = Math.Abs((Left + FullWidth) - workArea.Right) <= 16;
        if (_isDockedRight)
            FixedToRight();
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

    private static void ResetMaxValues()
    {
        HardwareMonitorService.Instance.ResetMaxValues();
    }

    private void ResetMax_Click(object sender, RoutedEventArgs e) => ResetMaxValues();

    private void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;
        SaveConfig();

        if (_dashboardWindow != null)
        {
            _dashboardWindow.PrepareForExit();
            _dashboardWindow.Close();
        }

        CleanupResources();
        System.Windows.Application.Current.Shutdown();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CleanupResources();
        base.OnClosed(e);
    }

    private void CleanupResources()
    {
        HardwareMonitorService.Instance.DataUpdated -= OnHardwareDataUpdated;
        Interlocked.Exchange(ref _pendingSnapshot, null);
        _idleTimer?.Stop();
        if (_idleTimer != null)
            _idleTimer.Tick -= IdleTimer_Tick;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (_hotkeyRegistered && hwnd != IntPtr.Zero)
        {
            if (!UnregisterHotKey(hwnd, HotkeyToggleId))
                Debug.WriteLine($"\u9000\u51FA\u65F6\u53D6\u6D88\u70ED\u952E\u5931\u8D25: {Marshal.GetLastPInvokeError()}");
            _hotkeyRegistered = false;
        }

        _windowSource?.RemoveHook(WndProc);
        _windowSource = null;
        if (_systemEventsSubscribed)
            UnsubscribeFromDisplayChanges();
        _notifyIcon?.Dispose();
        _notifyIcon = null;
    }

    private enum MetricVisibility
    {
        Ram,
        Vram,
        Upload,
        Download
    }
}
