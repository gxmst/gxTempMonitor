using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
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
    private const double BaseWidth = 147;
    private const double FullWidth = 229;
    private const double AnimDurationMs = 200;
    private const double MaxContainerWidth = 72;

    private AppConfig _config;
    private readonly AlertEngine _alertEngine = new();
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _environmentTimer;
    private HwndSource? _windowSource;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayContextMenu;
    private System.Drawing.Icon? _applicationIcon;
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
    private SettingsWindow? _settingsWindow;
    private WelcomeWindow? _welcomeWindow;
    private HardwareSnapshot? _pendingSnapshot;
    private int _snapshotDispatchScheduled;

    private bool _isLocked;
    private bool _showCpu = true;
    private bool _showGpu = true;
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
    private bool _alwaysOnTop = true;
    private bool _enableAdaptiveSampling = true;
    private bool _hiddenForFullscreen;
    private bool _dimmedForFullscreen;
    private volatile bool _isExiting;
    private bool _futureConfigWarningShown;
    private bool _configSaveWarningShown;
    private bool _systemEventsSubscribed;
    private WidgetTheme _currentTheme = WidgetTheme.Dark;
    private GpuDisplayMetric _gpuDisplayMetric = GpuDisplayMetric.Auto;
    private MemoryDisplayMode _ramDisplayMode = MemoryDisplayMode.Used;
    private MemoryDisplayMode _vramDisplayMode = MemoryDisplayMode.Used;
    private NetworkDisplayUnit _networkDisplayUnit = NetworkDisplayUnit.Auto;
    private FullscreenBehavior _fullscreenBehavior = FullscreenBehavior.StayVisible;
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
        HardwareMonitorService.Instance.ConfigureSelections(
            _config.PreferredGpuProvider,
            _config.PreferredGpuDeviceIdentifier,
            _config.NetworkSelectionMode,
            _config.PreferredNetworkInterfaceId);
        ApplySnapshot(HardwareMonitorService.Instance.LatestSnapshot);

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _idleTimer.Tick += IdleTimer_Tick;
        _idleTimer.Start();

        _environmentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _environmentTimer.Tick += EnvironmentTimer_Tick;
        UpdateEnvironmentMonitoring();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            string? executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
                _applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            Debug.WriteLine($"无法读取应用图标: {ex.Message}");
        }

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "gxTempMonitor",
            Icon = _applicationIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleVisibility();

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayContextMenu = contextMenu;
        var settingsMenuItem = new System.Windows.Forms.ToolStripMenuItem("设置...", null, (_, _) => OpenSettings());
        var dashboardMenuItem = new System.Windows.Forms.ToolStripMenuItem("打开 Dashboard", null, (_, _) => ShowDashboard());
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
        _trayProcessTrackingMenuItem = new System.Windows.Forms.ToolStripMenuItem("进程级 GPU 显存（全部 GPU）", null, (_, _) => SetProcessTrackingEnabled(!_trackTopGpuProcess));
        _trayAlertsMenuItem = new System.Windows.Forms.ToolStripMenuItem("启用告警", null, (_, _) => SetAlertsEnabled(!_enableAlerts));
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

        contextMenu.Items.Add(dashboardMenuItem);
        contextMenu.Items.Add(settingsMenuItem);
        contextMenu.Items.Add(_trayLockMenuItem);
        contextMenu.Items.Add("-");
        contextMenu.Items.Add(_trayStartupMenuItem);
        contextMenu.Items.Add(_trayExportCsvMenuItem);
        contextMenu.Items.Add("-");
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
            CpuUsageText.Foreground = GetConfiguredAlertBrush(
                snapshot.CpuUsage,
                _config.CpuUsageAlertThreshold);
            UpdateIndicator(
                CpuIndicator,
                snapshot.CpuUsage,
                _config.EnableAlerts ? _config.CpuUsageAlertThreshold - _config.AlertHysteresis : float.PositiveInfinity,
                _config.EnableAlerts ? _config.CpuUsageAlertThreshold : float.PositiveInfinity);
        }
        else
        {
            CpuUsageText.Text = "-- %";
            CpuMaxText.Text = snapshot.CpuUsageMax > 0 ? $"{snapshot.CpuUsageMax:0.0} %" : "-- %";
            CpuUsageText.Foreground = UiHelper.NormalBrush;
            UpdateIndicator(CpuIndicator, 0);
        }

        GpuDisplayMetric effectiveGpuMetric = ResolveGpuDisplayMetric(snapshot);
        switch (effectiveGpuMetric)
        {
            case GpuDisplayMetric.Temperature when snapshot.GpuTemperature.HasValue:
                GpuTempText.Text = $"{snapshot.GpuTemperature.Value:0.0} \u00B0C";
                GpuMaxText.Text = $"{(snapshot.GpuTemperatureMax ?? snapshot.GpuTemperature.Value):0.0} \u00B0C";
                GpuTempText.Foreground = GetConfiguredAlertBrush(
                    snapshot.GpuTemperature.Value,
                    _config.GpuTemperatureAlertThreshold);
                UpdateIndicator(
                    GpuIndicator,
                    snapshot.GpuTemperature.Value,
                    _config.EnableAlerts ? _config.GpuTemperatureAlertThreshold - _config.AlertHysteresis : float.PositiveInfinity,
                    _config.EnableAlerts ? _config.GpuTemperatureAlertThreshold : float.PositiveInfinity);
                break;
            case GpuDisplayMetric.Usage when snapshot.HasGpuUsage:
                GpuTempText.Text = $"{snapshot.GpuUsagePercent:0.0} %";
                GpuMaxText.Text = $"{(snapshot.GpuUsageMaxPercent ?? snapshot.GpuUsagePercent):0.0} %";
                GpuTempText.Foreground = _config.EnableAlerts
                    ? UiHelper.GetAlertBrush(snapshot.GpuUsagePercent)
                    : UiHelper.NormalBrush;
                UpdateIndicator(
                    GpuIndicator,
                    snapshot.GpuUsagePercent,
                    _config.EnableAlerts ? 80 : float.PositiveInfinity,
                    _config.EnableAlerts ? 90 : float.PositiveInfinity);
                break;
            case GpuDisplayMetric.Power when snapshot.GpuPowerWatts.HasValue:
                GpuTempText.Text = $"{snapshot.GpuPowerWatts.Value:0.0} W";
                GpuMaxText.Text = $"{(snapshot.GpuPowerMaxWatts ?? snapshot.GpuPowerWatts.Value):0.0} W";
                GpuTempText.Foreground = UiHelper.NormalBrush;
                UpdateIndicator(GpuIndicator, 0);
                break;
            default:
                GpuTempText.Text = effectiveGpuMetric switch
                {
                    GpuDisplayMetric.Usage => "-- %",
                    GpuDisplayMetric.Power => "-- W",
                    _ => "-- \u00B0C"
                };
                GpuMaxText.Text = GpuTempText.Text;
                GpuTempText.Foreground = UiHelper.NormalBrush;
                UpdateIndicator(GpuIndicator, 0);
                break;
        }

        if (snapshot.HasRamData)
        {
            if (_ramDisplayMode == MemoryDisplayMode.Percentage)
            {
                RamUsedText.Text = $"{snapshot.RamUsagePercent:0.0} %";
                float maximumPercent = snapshot.TotalRamGb > 0
                    ? Math.Clamp(snapshot.RamUsedMaxGb / snapshot.TotalRamGb * 100, 0, 100)
                    : snapshot.RamUsagePercent;
                RamMaxText.Text = $"{maximumPercent:0.0} %";
            }
            else
            {
                RamUsedText.Text = $"{snapshot.RamUsedGb:F1} GB";
                RamMaxText.Text = $"{snapshot.RamUsedMaxGb:F1} GB";
            }
            RamUsedText.Foreground = GetConfiguredAlertBrush(
                snapshot.RamUsagePercent,
                _config.RamUsageAlertThreshold);
            UpdateIndicator(
                RamIndicator,
                snapshot.RamUsagePercent,
                _config.EnableAlerts ? _config.RamUsageAlertThreshold - _config.AlertHysteresis : float.PositiveInfinity,
                _config.EnableAlerts ? _config.RamUsageAlertThreshold : float.PositiveInfinity);
        }
        else
        {
            RamUsedText.Text = "-- GB";
            RamMaxText.Text = snapshot.RamUsedMaxGb > 0 ? $"{snapshot.RamUsedMaxGb:F1} GB" : "-- GB";
            RamUsedText.Foreground = UiHelper.NormalBrush;
            UpdateIndicator(RamIndicator, 0);
        }

        if (_vramDisplayMode == MemoryDisplayMode.Percentage &&
            snapshot.VramUsedGb.HasValue && snapshot.VramTotalGb is > 0)
        {
            float vramPercent = Math.Clamp(snapshot.VramUsedGb.Value / snapshot.VramTotalGb.Value * 100, 0, 100);
            VramUsedText.Text = $"{vramPercent:0.0} %";
            float vramMaximumPercent = snapshot.VramUsedMaxGb.HasValue
                ? Math.Clamp(snapshot.VramUsedMaxGb.Value / snapshot.VramTotalGb.Value * 100, 0, 100)
                : vramPercent;
            VramMaxText.Text = $"{vramMaximumPercent:0.0} %";
        }
        else
        {
            VramUsedText.Text = UiHelper.FormatOptionalGb(snapshot.VramUsedGb);
            VramMaxText.Text = UiHelper.FormatOptionalGb(snapshot.VramUsedMaxGb);
        }

        NetUpText.Text = snapshot.HasNetworkData ? FormatNetworkSpeed(snapshot.NetUploadBytesPerSecond) : "--";
        NetUpMaxText.Text = FormatNetworkSpeed(snapshot.NetUploadMaxBytesPerSecond);
        NetDownText.Text = snapshot.HasNetworkData ? FormatNetworkSpeed(snapshot.NetDownloadBytesPerSecond) : "--";
        NetDownMaxText.Text = FormatNetworkSpeed(snapshot.NetDownloadMaxBytesPerSecond);

        CheckAlerts(snapshot);
        UpdateTrayTooltip(snapshot);
    }

    private void UpdateTrayTooltip(HardwareSnapshot snapshot)
    {
        if (_notifyIcon == null) return;

        string gpuValue = ResolveGpuDisplayMetric(snapshot) switch
        {
            GpuDisplayMetric.Temperature when snapshot.GpuTemperature.HasValue =>
                $"{snapshot.GpuTemperature.Value:0.0}\u00B0C",
            GpuDisplayMetric.Usage when snapshot.HasGpuUsage =>
                $"{snapshot.GpuUsagePercent:0.0}%",
            GpuDisplayMetric.Power when snapshot.GpuPowerWatts.HasValue =>
                $"{snapshot.GpuPowerWatts.Value:0.0}W",
            _ => "--"
        };
        var topProcess = snapshot.TopGpuProcess;
        var processLine = topProcess != null ? $"\nGPU\u8FDB\u7A0B: {topProcess}" : "";

        string cpuText = snapshot.HasCpuUsage ? $"{snapshot.CpuUsage:0.0}%" : "--";
        string ramText = snapshot.HasRamData
            ? $"{snapshot.RamUsedGb:F1}GB ({snapshot.RamUsagePercent:0.0}%)"
            : "--";
        var text = $"CPU {cpuText} | GPU {gpuValue}\n" +
                   $"RAM {ramText}" +
                   $"{processLine}";

        if (text.Length > 127)
            text = text.Substring(0, 127);

        // Every assignment calls Shell_NotifyIcon; identical text can be skipped.
        if (!string.Equals(_notifyIcon.Text, text, StringComparison.Ordinal))
            _notifyIcon.Text = text;
    }

    private void CheckAlerts(HardwareSnapshot snapshot)
    {
        AlertEvaluation evaluation = _alertEngine.Evaluate(snapshot, _config, Stopwatch.GetTimestamp());
        bool shouldFlash = evaluation.IsActive && _config.AlertPresentation == AlertPresentation.Flash;
        if (shouldFlash && !_isFlashing)
        {
            _isFlashing = true;
            StartFlashAnimation();
        }
        else if (!shouldFlash && _isFlashing)
        {
            _isFlashing = false;
            StopFlashAnimation();
        }

        if (evaluation.BecameActive && evaluation.ShouldNotify &&
            _config.AlertPresentation == AlertPresentation.TrayNotification &&
            _notifyIcon != null)
        {
            _notifyIcon.ShowBalloonTip(
                5000,
                "gxTempMonitor 告警",
                evaluation.Message,
                System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private GpuDisplayMetric ResolveGpuDisplayMetric(HardwareSnapshot snapshot)
    {
        if (_gpuDisplayMetric != GpuDisplayMetric.Auto)
            return _gpuDisplayMetric;

        return snapshot.GpuPrimaryMetric switch
        {
            GpuPrimaryMetric.Temperature => GpuDisplayMetric.Temperature,
            GpuPrimaryMetric.Usage => GpuDisplayMetric.Usage,
            GpuPrimaryMetric.Power => GpuDisplayMetric.Power,
            _ => GpuDisplayMetric.Usage
        };
    }

    private string FormatNetworkSpeed(float bytesPerSecond)
    {
        if (_networkDisplayUnit == NetworkDisplayUnit.Auto)
            return UiHelper.FormatSpeed(bytesPerSecond);

        if (_networkDisplayUnit == NetworkDisplayUnit.BytesPerSecond)
        {
            float normalized = Math.Max(0, bytesPerSecond);
            return normalized >= 1024 * 1024
                ? $"{normalized / 1024 / 1024:0.0}MiB/s"
                : $"{normalized / 1024:0.0}KiB/s";
        }

        float bitsPerSecond = Math.Max(0, bytesPerSecond) * 8;
        return bitsPerSecond >= 1_000_000
            ? $"{bitsPerSecond / 1_000_000:0.0}Mbps"
            : $"{bitsPerSecond / 1_000:0.0}Kbps";
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
        Delay60MenuItem.IsChecked = _delayedStartSeconds == 60;
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
            // SetWindowLong can legitimately return 0, so failure is only
            // distinguishable when the last error was cleared beforehand.
            Marshal.SetLastSystemError(0);
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
        Topmost = _alwaysOnTop;
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

        if (_config.IsReadOnlyDueToUnsupportedConfig)
            _ = Dispatcher.InvokeAsync(ShowFutureConfigWarningIfNeeded, DispatcherPriority.ApplicationIdle);

        if (!_config.HasCompletedOnboarding)
            _ = Dispatcher.InvokeAsync(ShowWelcome, DispatcherPriority.ApplicationIdle);
    }

    private void ShowFutureConfigWarningIfNeeded()
    {
        if (_futureConfigWarningShown || !_config.IsReadOnlyDueToUnsupportedConfig || _isExiting)
            return;

        _futureConfigWarningShown = true;
        System.Windows.MessageBox.Show(
            this,
            "检测到的配置来自更高版本、文件过大、损坏或暂时无法读取。为防止丢失数据，本版本不会保存或覆盖该配置。\n\n如需重置，请先备份或移走 %LocalAppData%\\gxTempMonitor\\config.json，然后重启程序。",
            "配置为只读",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ShowWelcome()
    {
        if (_isExiting || _welcomeWindow != null || _config.HasCompletedOnboarding)
            return;

        _welcomeWindow = new WelcomeWindow { Owner = this };
        _welcomeWindow.SettingsRequested += WelcomeWindow_SettingsRequested;
        _welcomeWindow.Closed += WelcomeWindow_Closed;
        _welcomeWindow.Show();
        _welcomeWindow.Activate();
    }

    private void WelcomeWindow_SettingsRequested()
    {
        CompleteOnboarding();
        _ = Dispatcher.InvokeAsync(OpenSettings, DispatcherPriority.ApplicationIdle);
    }

    private void WelcomeWindow_Closed(object? sender, EventArgs e) => CompleteOnboarding();

    private void CompleteOnboarding()
    {
        if (_welcomeWindow != null)
        {
            _welcomeWindow.SettingsRequested -= WelcomeWindow_SettingsRequested;
            _welcomeWindow.Closed -= WelcomeWindow_Closed;
            _welcomeWindow = null;
        }

        if (!_config.HasCompletedOnboarding)
        {
            _config.HasCompletedOnboarding = true;
            SaveConfig();
        }
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
            _hiddenForFullscreen = false;
            Hide();
        }
        else
        {
            _hiddenForFullscreen = false;
            Show();
            Topmost = _alwaysOnTop;
        }

        ApplyEffectiveSamplingInterval();
    }

    private void OpenSettings()
    {
        ShowFutureConfigWarningIfNeeded();

        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        if (_welcomeWindow != null)
        {
            WelcomeWindow welcomeWindow = _welcomeWindow;
            CompleteOnboarding();
            welcomeWindow.Close();
        }

        var settingsWindow = new SettingsWindow(_config, HardwareMonitorService.Instance)
        {
            Owner = this
        };
        _settingsWindow = settingsWindow;
        settingsWindow.SettingsApplied += SettingsWindow_SettingsApplied;
        if (_trayContextMenu != null)
            _trayContextMenu.Enabled = false;

        try
        {
            settingsWindow.ShowDialog();
        }
        finally
        {
            settingsWindow.SettingsApplied -= SettingsWindow_SettingsApplied;
            if (ReferenceEquals(_settingsWindow, settingsWindow))
                _settingsWindow = null;
            if (!_isExiting && _trayContextMenu != null)
                _trayContextMenu.Enabled = true;
        }
    }

    private void SettingsWindow_SettingsApplied(AppConfig updatedConfig)
    {
        if (_config.IsReadOnlyDueToUnsupportedConfig)
            updatedConfig.IsReadOnlyDueToUnsupportedConfig = true;
        updatedConfig.Top = Top;
        updatedConfig.Left = Left;
        updatedConfig.IsDockedRight = _isDockedRight;
        updatedConfig.IsLocked = _isLocked;
        _config = updatedConfig;
        ApplyConfig(_config);
        ApplyRuntimeConfiguration();
        SaveConfig();

        if (IsStartupEnabled())
            CreateOrUpdateStartupShortcut(showError: false);
    }

    private void ApplyRuntimeConfiguration()
    {
        Topmost = _alwaysOnTop;
        ApplyVisibilitySettings();
        ApplyTheme();
        UpdateOpacityMenuItems();
        UpdateDelayMenuItems();
        UpdateHotkeyRegistration();
        UpdateRuntimeOptionMenuItems();
        UpdateEnvironmentMonitoring();
        _alertEngine.Reset();

        HardwareMonitorService.Instance.Configure(
            _samplingIntervalSeconds,
            _trackTopGpuProcess);
        HardwareMonitorService.Instance.ConfigureSelections(
            _config.PreferredGpuProvider,
            _config.PreferredGpuDeviceIdentifier,
            _config.NetworkSelectionMode,
            _config.PreferredNetworkInterfaceId);
        _dashboardWindow?.ApplyConfiguration(_config);
        ApplyEffectiveSamplingInterval();
        ApplySnapshot(HardwareMonitorService.Instance.LatestSnapshot);
    }

    private void EnvironmentTimer_Tick(object? sender, EventArgs e)
    {
        if (!ShouldMonitorFullscreen(_fullscreenBehavior))
        {
            UpdateEnvironmentMonitoring();
            return;
        }

        IntPtr mainHandle = new WindowInteropHelper(this).Handle;
        IntPtr dashboardHandle = _dashboardWindow == null
            ? IntPtr.Zero
            : new WindowInteropHelper(_dashboardWindow).Handle;
        IntPtr settingsHandle = _settingsWindow == null
            ? IntPtr.Zero
            : new WindowInteropHelper(_settingsWindow).Handle;
        bool fullscreen = FullscreenDetector.IsForegroundFullscreen(
            mainHandle,
            dashboardHandle,
            settingsHandle);

        ApplyFullscreenBehavior(fullscreen);
    }

    internal static bool ShouldMonitorFullscreen(FullscreenBehavior behavior) =>
        behavior is FullscreenBehavior.Hide or FullscreenBehavior.Dim;

    private void UpdateEnvironmentMonitoring()
    {
        if (_environmentTimer == null)
            return;

        if (ShouldMonitorFullscreen(_fullscreenBehavior))
        {
            if (!_environmentTimer.IsEnabled)
                _environmentTimer.Start();
            return;
        }

        _environmentTimer.Stop();
        if (_hiddenForFullscreen || _dimmedForFullscreen)
            ApplyFullscreenBehavior(fullscreen: false);
    }

    private void ApplyFullscreenBehavior(bool fullscreen)
    {
        if (_fullscreenBehavior == FullscreenBehavior.Hide)
        {
            if (fullscreen && Visibility == Visibility.Visible && !_hiddenForFullscreen)
            {
                _hiddenForFullscreen = true;
                Hide();
            }
            else if (!fullscreen && _hiddenForFullscreen)
            {
                _hiddenForFullscreen = false;
                Show();
                Topmost = _alwaysOnTop;
            }
        }
        else if (_hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            Show();
            Topmost = _alwaysOnTop;
        }

        bool shouldDim = fullscreen && _fullscreenBehavior == FullscreenBehavior.Dim;
        if (shouldDim != _dimmedForFullscreen)
        {
            _dimmedForFullscreen = shouldDim;
            Opacity = shouldDim ? 0.30 : 1.0;
        }

        ApplyEffectiveSamplingInterval();
    }

    private void ApplyEffectiveSamplingInterval()
    {
        bool dashboardVisible = _dashboardWindow?.IsVisible == true;
        bool lowActivity = Visibility != Visibility.Visible && !dashboardVisible;
        int effectiveInterval = _enableAdaptiveSampling && lowActivity
            ? Math.Max(5, _samplingIntervalSeconds)
            : _samplingIntervalSeconds;
        HardwareMonitorService.Instance.SetSamplingInterval(effectiveInterval);
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
        _showCpu = config.ShowCpu;
        _showGpu = config.ShowGpu;
        _showRam = config.ShowRam;
        _showVram = config.ShowVram;
        _showUpload = config.ShowUpload;
        _showDownload = config.ShowDownload;
        _currentTheme = config.Theme;
        _gpuDisplayMetric = config.GpuDisplayMetric;
        _ramDisplayMode = config.RamDisplayMode;
        _vramDisplayMode = config.VramDisplayMode;
        _networkDisplayUnit = config.NetworkDisplayUnit;
        _widgetOpacity = config.WidgetOpacity;
        _alwaysOnTop = config.AlwaysOnTop;
        _fullscreenBehavior = config.FullscreenBehavior;
        _delayedStartSeconds = config.DelayedStartSeconds;
        _enableGlobalHotkey = config.EnableGlobalHotkey;
        _trackTopGpuProcess = config.TrackTopGpuProcess;
        _enableAlerts = config.EnableAlerts;
        _samplingIntervalSeconds = config.SamplingIntervalSeconds;
        _enableAdaptiveSampling = config.EnableAdaptiveSampling;
    }

    private void SaveConfig()
    {
        _config.Top = Top;
        _config.Left = Left;
        _config.IsDockedRight = _isDockedRight;
        _config.IsLocked = _isLocked;
        _config.ShowCpu = _showCpu;
        _config.ShowGpu = _showGpu;
        _config.ShowRam = _showRam;
        _config.ShowVram = _showVram;
        _config.ShowUpload = _showUpload;
        _config.ShowDownload = _showDownload;
        _config.Theme = _currentTheme;
        _config.GpuDisplayMetric = _gpuDisplayMetric;
        _config.RamDisplayMode = _ramDisplayMode;
        _config.VramDisplayMode = _vramDisplayMode;
        _config.NetworkDisplayUnit = _networkDisplayUnit;
        _config.WidgetOpacity = _widgetOpacity;
        _config.AlwaysOnTop = _alwaysOnTop;
        _config.FullscreenBehavior = _fullscreenBehavior;
        _config.DelayedStartSeconds = _delayedStartSeconds;
        _config.EnableGlobalHotkey = _enableGlobalHotkey;
        _config.TrackTopGpuProcess = _trackTopGpuProcess;
        _config.EnableAlerts = _enableAlerts;
        _config.SamplingIntervalSeconds = _samplingIntervalSeconds;
        _config.EnableAdaptiveSampling = _enableAdaptiveSampling;

        if (ConfigStore.TrySave(_config))
            return;

        Debug.WriteLine("\u4FDD\u5B58\u914D\u7F6E\u5931\u8D25\u3002");
        if (_config.IsReadOnlyDueToUnsupportedConfig || _configSaveWarningShown || !IsLoaded || _isExiting)
            return;

        _configSaveWarningShown = true;
        const string message =
            "配置保存失败，本次更改只在当前运行期间生效。请检查磁盘空间以及 %LocalAppData%\\gxTempMonitor 的写入权限。";
        if (_settingsWindow?.IsVisible == true)
        {
            System.Windows.MessageBox.Show(
                _settingsWindow,
                message,
                "无法保存配置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else if (_notifyIcon != null)
        {
            // Startup failures should not steal focus from the foreground app.
            _notifyIcon.ShowBalloonTip(
                8000,
                "gxTempMonitor 无法保存配置",
                message,
                System.Windows.Forms.ToolTipIcon.Warning);
        }
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
        CpuRow.Visibility = _showCpu ? Visibility.Visible : Visibility.Collapsed;
        GpuRow.Visibility = _showGpu ? Visibility.Visible : Visibility.Collapsed;
        RamRow.Visibility = _showRam ? Visibility.Visible : Visibility.Collapsed;
        VramRow.Visibility = _showVram ? Visibility.Visible : Visibility.Collapsed;
        NetUpRow.Visibility = _showUpload ? Visibility.Visible : Visibility.Collapsed;
        NetDownRow.Visibility = _showDownload ? Visibility.Visible : Visibility.Collapsed;
        ApplyMetricOrder();
        UpdateVisibilityMenuItems();
    }

    private void ApplyMetricOrder()
    {
        UIElement[] rows = [CpuRow, GpuRow, RamRow, VramRow, NetUpRow, NetDownRow];
        foreach (UIElement row in rows)
            MainStack.Children.Remove(row);

        foreach (WidgetMetric metric in _config.MetricOrder)
        {
            UIElement row = metric switch
            {
                WidgetMetric.Cpu => CpuRow,
                WidgetMetric.Gpu => GpuRow,
                WidgetMetric.Ram => RamRow,
                WidgetMetric.Vram => VramRow,
                WidgetMetric.Upload => NetUpRow,
                WidgetMetric.Download => NetDownRow,
                _ => CpuRow
            };
            MainStack.Children.Add(row);
        }
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
        ApplySnapshot(HardwareMonitorService.Instance.LatestSnapshot);
        SaveConfig();
    }

    private void SetAlertsEnabled(bool enabled)
    {
        _enableAlerts = enabled;
        _config.EnableAlerts = enabled;
        _alertEngine.Reset();
        if (!enabled && _isFlashing)
        {
            _isFlashing = false;
            StopFlashAnimation();
        }

        UpdateRuntimeOptionMenuItems();
        ApplySnapshot(HardwareMonitorService.Instance.LatestSnapshot);
        _dashboardWindow?.ApplyConfiguration(_config);
        SaveConfig();
    }

    private void SetSamplingInterval(int seconds)
    {
        _samplingIntervalSeconds = seconds is 1 or 2 or 5 ? seconds : 1;
        _config.SamplingIntervalSeconds = _samplingIntervalSeconds;
        ApplyEffectiveSamplingInterval();
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
                _config.ShowRam = isVisible;
                break;
            case MetricVisibility.Vram:
                _showVram = isVisible;
                _config.ShowVram = isVisible;
                break;
            case MetricVisibility.Upload:
                _showUpload = isVisible;
                _config.ShowUpload = isVisible;
                break;
            case MetricVisibility.Download:
                _showDownload = isVisible;
                _config.ShowDownload = isVisible;
                break;
        }

        ApplyVisibilitySettings();
        SaveConfig();
    }

    private void RestoreDefaultState()
    {
        bool wasLocked = _isLocked;
        bool preserveUnsupportedConfigReadOnly = _config.IsReadOnlyDueToUnsupportedConfig;
        _config = new AppConfig
        {
            IsReadOnlyDueToUnsupportedConfig = preserveUnsupportedConfigReadOnly
        };
        ApplyConfig(_config);
        ApplyRuntimeConfiguration();
        ResetMaxValues();
        ResetWindowPosition();
        if (wasLocked)
            SetLock(false);

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
    private void Delay60_Click(object sender, RoutedEventArgs e) => SetDelayedStart(60);

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException)
        {
            System.Windows.MessageBox.Show($"\u5BFC\u51FA\u5931\u8D25: {ex.Message}", "\u9519\u8BEF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void IdleTimer_Tick(object? sender, EventArgs e)
    {
        AnimateBackgroundOpacity(_widgetOpacity, TimeSpan.FromSeconds(1));
        AnimateMainContentOpacity(GetIdleContentOpacity(_widgetOpacity), TimeSpan.FromSeconds(1));
        _idleTimer?.Stop();
    }

    internal static double GetIdleContentOpacity(double widgetOpacity) =>
        Math.Clamp(widgetOpacity + 0.06, 0, 1);

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

    private System.Windows.Media.Brush GetConfiguredAlertBrush(float value, int threshold)
    {
        if (!_config.EnableAlerts) return UiHelper.NormalBrush;
        if (value >= threshold) return UiHelper.CriticalBrush;
        if (value >= threshold - _config.AlertHysteresis) return UiHelper.WarningBrush;
        return UiHelper.NormalBrush;
    }

    private static void UpdateIndicator(
        System.Windows.Controls.Border indicator,
        float value,
        float warningThreshold = 80,
        float criticalThreshold = 90)
    {
        if (value >= criticalThreshold)
        {
            indicator.Background = UiHelper.CriticalBrush;
            indicator.Opacity = 1;
            return;
        }

        if (value >= warningThreshold)
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
        EnsureDashboardWindow();
        if (_dashboardWindow?.Visibility == Visibility.Visible)
        {
            _dashboardWindow.Hide();
        }
        else
        {
            _dashboardWindow?.Show();
            _dashboardWindow?.Activate();
        }

        ApplyEffectiveSamplingInterval();
    }

    private void ShowDashboard()
    {
        EnsureDashboardWindow();
        _dashboardWindow?.Show();
        _dashboardWindow?.Activate();
        ApplyEffectiveSamplingInterval();
    }

    private void EnsureDashboardWindow()
    {
        if (_dashboardWindow != null) return;

        _dashboardWindow = new DashboardWindow(_config);
        _dashboardWindow.IsVisibleChanged += DashboardWindow_IsVisibleChanged;
    }

    private void DashboardWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        ApplyEffectiveSamplingInterval();

    private void Dashboard_Click(object sender, RoutedEventArgs e) => ShowDashboard();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

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
            _dashboardWindow.IsVisibleChanged -= DashboardWindow_IsVisibleChanged;
            _dashboardWindow.PrepareForExit();
            _dashboardWindow.Close();
        }

        if (_settingsWindow != null)
        {
            _settingsWindow.SettingsApplied -= SettingsWindow_SettingsApplied;
            _settingsWindow.Close();
            _settingsWindow = null;
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
        _environmentTimer?.Stop();
        if (_environmentTimer != null)
            _environmentTimer.Tick -= EnvironmentTimer_Tick;

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
        _trayContextMenu?.Dispose();
        _trayContextMenu = null;
        _applicationIcon?.Dispose();
        _applicationIcon = null;
    }

    private enum MetricVisibility
    {
        Ram,
        Vram,
        Upload,
        Download
    }
}
