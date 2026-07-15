using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;

namespace TempMonitor;

public partial class DashboardWindow : Window
{
    private const int TrendCapacity = 120;
    private static readonly CubicEase EaseOut = CreateFrozenEaseOut();

    private AppConfig _config;
    private bool _allowClose;
    private volatile bool _isMonitoring;
    private int _monitorGeneration;
    private FrameworkElement? _currentView;
    private TimeSpan _trendRange = TimeSpan.FromMinutes(10);

    public DashboardWindow() : this(new AppConfig())
    {
    }

    internal DashboardWindow(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config.Clone();
        InitializeComponent();
        _currentView = OverviewView;
        UpdateVisibleSection("Overview", animate: false);
        NavigationListBox.SelectedIndex = 0;
        IsVisibleChanged += DashboardWindow_IsVisibleChanged;
    }

    internal void ApplyConfiguration(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config.Clone();

        if (IsVisible && !_allowClose)
            ApplySnapshot(HardwareMonitorService.Instance.LatestSnapshot);
    }

    public void PrepareForExit()
    {
        _allowClose = true;
        DetachFromMonitor();
    }

    private void DashboardWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && !_allowClose)
        {
            AttachToMonitor();
        }
        else
        {
            DetachFromMonitor();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            DetachFromMonitor();
            Hide();
            return;
        }

        DetachFromMonitor();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachFromMonitor();
        IsVisibleChanged -= DashboardWindow_IsVisibleChanged;
        base.OnClosed(e);
    }

    private void AttachToMonitor()
    {
        if (_isMonitoring || _allowClose)
        {
            return;
        }

        Interlocked.Increment(ref _monitorGeneration);
        HardwareMonitorService.Instance.DataUpdated += OnDataUpdated;
        _isMonitoring = true;
        ApplySnapshot(HardwareMonitorService.Instance.LatestSnapshot);
    }

    private void DetachFromMonitor()
    {
        if (!_isMonitoring)
        {
            return;
        }

        // Gate callbacks before removing the handler so an in-flight publication is harmless.
        _isMonitoring = false;
        Interlocked.Increment(ref _monitorGeneration);
        HardwareMonitorService.Instance.DataUpdated -= OnDataUpdated;
    }

    private void OnDataUpdated(HardwareSnapshot snapshot)
    {
        int generation = Volatile.Read(ref _monitorGeneration);
        if (!_isMonitoring || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_isMonitoring &&
                    generation == Volatile.Read(ref _monitorGeneration) &&
                    IsVisible &&
                    !_allowClose)
                {
                    ApplySnapshot(snapshot);
                }
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ApplySnapshot(HardwareSnapshot snapshot)
    {
        ApplyCurrentViewSnapshot(snapshot);
    }

    private void ApplyCurrentViewSnapshot(HardwareSnapshot snapshot)
    {
        if (ReferenceEquals(_currentView, CpuView))
        {
            ApplyCpuDetailSnapshot(snapshot);
        }
        else if (ReferenceEquals(_currentView, GpuView))
        {
            ApplyGpuDetailSnapshot(snapshot);
        }
        else if (ReferenceEquals(_currentView, RamView))
        {
            ApplyRamDetailSnapshot(snapshot);
        }
        else if (ReferenceEquals(_currentView, NetworkView))
        {
            ApplyNetworkDetailSnapshot(snapshot);
        }
        else if (ReferenceEquals(_currentView, SystemView))
        {
            ApplySystemSnapshot(snapshot);
        }
        else
        {
            ApplyOverviewSnapshot(snapshot);
            ApplyTrendSnapshot(snapshot);
        }
    }

    private void ApplyOverviewSnapshot(HardwareSnapshot s)
    {
        if (s.HasCpuUsage)
            AnimateGauge(CpuGauge, s.CpuUsage);
        else
            SetGaugeUnavailable(CpuGauge);
        CpuInfoValueText.Text = string.IsNullOrWhiteSpace(s.CpuName)
            ? $"{s.CpuArchitecture ?? "未知架构"} · {s.LogicalProcessorCount} 线程"
            : s.CpuName;

        if (s.HasGpuUsage)
            AnimateGauge(GpuGauge, s.GpuUsagePercent);
        else
            SetGaugeUnavailable(GpuGauge);
        if (s.GpuTemperature.HasValue)
        {
            GpuTempValueText.Text = UiHelper.FormatOptionalTemp(s.GpuTemperature);
            GpuTempValueText.Foreground = GetConfiguredAlertBrush(
                s.GpuTemperature.Value,
                _config.GpuTemperatureAlertThreshold);
        }
        else
        {
            GpuTempValueText.Text = HasGpuCapability(s, GpuMetricCapabilities.Temperature)
                ? "GPU 温度暂时不可用"
                : string.IsNullOrWhiteSpace(s.GpuProviderName)
                    ? "暂未获得 GPU 指标"
                    : $"{s.GpuProviderName} · 不提供温度";
            GpuTempValueText.Foreground = UiHelper.NormalBrush;
        }
        GpuVramValueText.Text = s.VramUsedGb.HasValue
            ? s.VramTotalGb.HasValue
                ? $"显存 {s.VramUsedGb.Value:F1} / {s.VramTotalGb.Value:F1} GB"
                : $"显存 {s.VramUsedGb.Value:F1} GB"
            : HasGpuCapability(s, GpuMetricCapabilities.VramUsed)
                ? "显存暂时不可用"
                : "当前数据源不提供显存";

        if (s.HasRamData)
        {
            AnimateGauge(RamGauge, s.RamUsagePercent);
            RamUsedValueText.Text = $"{s.RamUsedGb:F1} / {s.TotalRamGb:F1} GB";
            RamPercentValueText.Text = $"使用率 {s.RamUsagePercent:0.0} %";
        }
        else
        {
            SetGaugeUnavailable(RamGauge);
            RamUsedValueText.Text = "-- / -- GB";
            RamPercentValueText.Text = "使用率 --";
        }

        NetDownValueText.Text = s.HasNetworkData ? $"↓ {UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond)}" : "↓ --";
        NetUpValueText.Text = s.HasNetworkData ? $"↑ {UiHelper.FormatSpeed(s.NetUploadBytesPerSecond)}" : "↑ --";
        float networkActivity = Math.Min(100, (s.NetDownloadBytesPerSecond + s.NetUploadBytesPerSecond) / 1024f / 1024f * 10f);
        AnimateProgressBar(NetActivityProgressBar, networkActivity);
    }

    private void ApplyTrendSnapshot(HardwareSnapshot s)
    {
        HardwareSnapshot[] history = HardwareMonitorService.Instance.GetRecentHistory(
            _trendRange,
            TrendCapacity);
        GpuPrimaryMetric gpuMetric = s.GpuPrimaryMetric;

        TrendCpuValueText.Text = s.HasCpuUsage ? $"{s.CpuUsage:0.0} %" : "--";
        TrendGpuValueText.Text = gpuMetric switch
        {
            GpuPrimaryMetric.Temperature => UiHelper.FormatOptionalTemp(s.GpuTemperature),
            GpuPrimaryMetric.Usage when s.HasGpuUsage => $"{s.GpuUsagePercent:0.0} %",
            GpuPrimaryMetric.Power when s.GpuPowerWatts.HasValue => $"{s.GpuPowerWatts.Value:0.0} W",
            _ => "--"
        };
        TrendRamValueText.Text = s.HasRamData ? $"{s.RamUsedGb:F1} GB" : "--";
        TrendVramValueText.Text = s.VramUsedGb.HasValue ? $"{s.VramUsedGb.Value:F1} GB" : "--";
        TrendUpValueText.Text = s.HasNetworkData ? UiHelper.FormatSpeed(s.NetUploadBytesPerSecond) : "--";
        TrendDownValueText.Text = s.HasNetworkData ? UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond) : "--";
        TrendCpuChart.UpdateValues(history.Where(item => item.HasCpuUsage).Select(item => (double)item.CpuUsage).ToArray());
        double[] gpuValues = gpuMetric switch
        {
            GpuPrimaryMetric.Temperature => history
                .Where(item => item.GpuTemperature.HasValue)
                .Select(item => (double)item.GpuTemperature!.Value)
                .ToArray(),
            GpuPrimaryMetric.Usage => history
                .Where(item => item.HasGpuUsage)
                .Select(item => (double)item.GpuUsagePercent)
                .ToArray(),
            GpuPrimaryMetric.Power => history
                .Where(item => item.GpuPowerWatts.HasValue)
                .Select(item => (double)item.GpuPowerWatts!.Value)
                .ToArray(),
            _ => []
        };
        TrendGpuChart.UpdateValues(gpuValues);
        TrendRamChart.UpdateValues(history.Where(item => item.HasRamData).Select(item => (double)item.RamUsedGb).ToArray());
        TrendVramChart.UpdateValues(history.Where(item => item.VramUsedGb.HasValue).Select(item => (double)item.VramUsedGb!.Value).ToArray());
        TrendUpChart.UpdateValues(history.Where(item => item.HasNetworkData).Select(item => (double)item.NetUploadBytesPerSecond).ToArray());
        TrendDownChart.UpdateValues(history.Where(item => item.HasNetworkData).Select(item => (double)item.NetDownloadBytesPerSecond).ToArray());
    }

    private void ApplyCpuDetailSnapshot(HardwareSnapshot s)
    {
        if (!s.HasCpuUsage)
        {
            CpuDetailUsageText.Text = "--";
            CpuDetailUsageText.Foreground = UiHelper.NormalBrush;
            CpuDetailMaxUsageText.Text = s.CpuUsageMax > 0 ? $"峰值 {s.CpuUsageMax:0.0} %" : "峰值 --";
            AnimateProgressBar(CpuDetailUsageProgressBar, 0);
        }
        else
        {
            CpuDetailUsageText.Text = $"{s.CpuUsage:0.0} %";
            CpuDetailUsageText.Foreground = GetConfiguredAlertBrush(
                s.CpuUsage,
                _config.CpuUsageAlertThreshold);
            CpuDetailMaxUsageText.Text = $"峰值 {s.CpuUsageMax:0.0} %";
            AnimateProgressBar(CpuDetailUsageProgressBar, s.CpuUsage);
        }

        CpuDetailNameText.Text = string.IsNullOrWhiteSpace(s.CpuName) ? "处理器名称不可用" : s.CpuName;
        CpuDetailArchitectureText.Text = string.IsNullOrWhiteSpace(s.CpuArchitecture) ? "未知" : s.CpuArchitecture;
        CpuDetailLogicalProcessorsText.Text = s.LogicalProcessorCount > 0
            ? s.LogicalProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "未知";
        CpuDetailUptimeText.Text = $"系统运行时间 {FormatDuration(s.SystemUptime)}";
        CpuDetailSampleDurationText.Text = $"最近采样耗时 {s.SamplingDurationMilliseconds:0.###} ms";
    }

    private void ApplyGpuDetailSnapshot(HardwareSnapshot s)
    {
        GpuDetailDeviceText.Text = string.IsNullOrWhiteSpace(s.GpuDeviceName)
            ? "尚未识别到可用 GPU 数据源"
            : $"{s.GpuDeviceName} · {s.GpuProviderName ?? "未知 Provider"}";

        GpuUsageCard.Visibility = HasGpuCapability(s, GpuMetricCapabilities.Usage)
            ? Visibility.Visible
            : Visibility.Collapsed;
        GpuTemperatureCard.Visibility = HasGpuCapability(s, GpuMetricCapabilities.Temperature)
            ? Visibility.Visible
            : Visibility.Collapsed;
        GpuPowerCard.Visibility = HasGpuCapability(s, GpuMetricCapabilities.Power)
            ? Visibility.Visible
            : Visibility.Collapsed;
        GpuMemoryCard.Visibility = HasGpuCapability(s, GpuMetricCapabilities.VramUsed)
            ? Visibility.Visible
            : Visibility.Collapsed;

        GpuDetailUsageText.Text = s.HasGpuUsage ? $"{s.GpuUsagePercent:0.0} %" : "--";
        GpuDetailUsageText.Foreground = s.HasGpuUsage && _config.EnableAlerts
            ? UiHelper.GetAlertBrush(s.GpuUsagePercent)
            : UiHelper.NormalBrush;
        GpuDetailUsageMaxText.Text = s.GpuUsageMaxPercent.HasValue
            ? $"峰值 {s.GpuUsageMaxPercent.Value:0.0} %"
            : "峰值 --";
        GpuDetailMemoryValueText.Text = UiHelper.FormatOptionalGb(s.VramUsedGb);
        GpuDetailMemoryTotalText.Text = s.VramTotalGb.HasValue
            ? $"总量 {s.VramTotalGb.Value:F1} GB"
            : "当前数据源不提供总显存";
        GpuDetailTemperatureText.Text = UiHelper.FormatOptionalTemp(s.GpuTemperature);
        GpuDetailTemperatureText.Foreground = s.GpuTemperature.HasValue
            ? GetConfiguredAlertBrush(
                s.GpuTemperature.Value,
                _config.GpuTemperatureAlertThreshold)
            : UiHelper.NormalBrush;
        GpuDetailMaxTempText.Text = s.GpuTemperatureMax.HasValue
            ? $"峰值 {s.GpuTemperatureMax.Value:0.0} °C"
            : "峰值 --";
        GpuDetailPowerText.Text = s.GpuPowerWatts.HasValue
            ? $"{s.GpuPowerWatts.Value:0.0} W"
            : "-- W";
        GpuDetailPowerMaxText.Text = s.GpuPowerMaxWatts.HasValue
            ? $"峰值 {s.GpuPowerMaxWatts.Value:0.0} W"
            : "峰值 --";
        AnimateProgressBar(GpuDetailUsageProgressBar, s.HasGpuUsage ? s.GpuUsagePercent : 0);

        string[] supported =
        [
            .. new[]
            {
                HasGpuCapability(s, GpuMetricCapabilities.Usage) ? "负载" : null,
                HasGpuCapability(s, GpuMetricCapabilities.Temperature) ? "温度" : null,
                HasGpuCapability(s, GpuMetricCapabilities.Power) ? "功耗" : null,
                HasGpuCapability(s, GpuMetricCapabilities.VramUsed) ? "显存" : null
            }.Where(value => value != null).Cast<string>()
        ];
        GpuCapabilityNoteText.Text = supported.Length == 0
            ? "当前驱动接口没有返回可用指标，程序会继续按计划重试。"
            : $"当前数据源支持：{string.Join("、", supported)}。不支持的卡片已自动隐藏。";
    }

    private void ApplyRamDetailSnapshot(HardwareSnapshot s)
    {
        if (!s.HasRamData)
        {
            RamDetailUsedText.Text = "-- / -- GB";
            RamDetailUsedText.Foreground = UiHelper.NormalBrush;
            RamDetailPercentText.Text = "使用率 --";
            RamDetailAvailableText.Text = "-- GB";
            RamDetailTotalText.Text = "总内存 --";
            RamDetailPeakText.Text = s.RamUsedMaxGb > 0 ? $"{s.RamUsedMaxGb:F1} GB" : "--";
            RamDetailHeadroomText.Text = "--";
            RamDetailPercentChipText.Text = "--";
            RamDetailAvailChipText.Text = "--";
            RamDetailUsedFootText.Text = "--";
            RamDetailAvailFootText.Text = "--";
            RamDetailPeakFootText.Text = s.RamUsedMaxGb > 0 ? $"{s.RamUsedMaxGb:F1} GB" : "--";
            RamDetailPercentFootText.Text = "--";
            AnimateProgressBar(RamDetailUsageProgressBar, 0);
            return;
        }

        RamDetailUsedText.Text = $"{s.RamUsedGb:F1} / {s.TotalRamGb:F1} GB";
        RamDetailUsedText.Foreground = GetConfiguredAlertBrush(
            s.RamUsagePercent,
            _config.RamUsageAlertThreshold);
        RamDetailPercentText.Text = $"使用率 {s.RamUsagePercent:0.0} %";
        RamDetailAvailableText.Text = $"{s.RamAvailableGb:F1} GB";
        RamDetailTotalText.Text = $"总内存 {s.TotalRamGb:F1} GB";
        RamDetailPeakText.Text = $"{s.RamUsedMaxGb:F1} GB";
        RamDetailHeadroomText.Text = $"{s.RamAvailableGb:F1} GB";
        RamDetailPercentChipText.Text = $"{s.RamUsagePercent:0.0} %";
        RamDetailAvailChipText.Text = $"{s.RamAvailableGb:F1} GB";
        RamDetailUsedFootText.Text = $"{s.RamUsedGb:F1} GB";
        RamDetailAvailFootText.Text = $"{s.RamAvailableGb:F1} GB";
        RamDetailPeakFootText.Text = $"{s.RamUsedMaxGb:F1} GB";
        RamDetailPercentFootText.Text = $"{s.RamUsagePercent:0.0} %";
        AnimateProgressBar(RamDetailUsageProgressBar, s.RamUsagePercent);
    }

    private void ApplyNetworkDetailSnapshot(HardwareSnapshot s)
    {
        if (!s.HasNetworkData)
        {
            NetworkDetailDownText.Text = "↓ --";
            NetworkDetailUpText.Text = "↑ --";
            NetworkDetailInterfaceText.Text = string.IsNullOrWhiteSpace(s.NetworkInterfaceName) ? "未识别" : s.NetworkInterfaceName;
            NetworkDetailTotalText.Text = "总吞吐 --";
            NetworkDetailPeakUpText.Text = s.NetUploadMaxBytesPerSecond > 0 ? UiHelper.FormatSpeed(s.NetUploadMaxBytesPerSecond) : "--";
            NetworkDetailPeakDownText.Text = s.NetDownloadMaxBytesPerSecond > 0 ? UiHelper.FormatSpeed(s.NetDownloadMaxBytesPerSecond) : "--";
            NetworkDetailDownChipText.Text = "--";
            NetworkDetailUpChipText.Text = "--";
            NetworkDetailInterfaceFootText.Text = NetworkDetailInterfaceText.Text;
            NetworkDetailTotalFootText.Text = "--";
            NetworkDetailDownFootText.Text = "--";
            NetworkDetailUpFootText.Text = "--";
            AnimateProgressBar(NetworkDetailActivityProgressBar, 0);
            return;
        }

        NetworkDetailDownText.Text = $"↓ {UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond)}";
        NetworkDetailUpText.Text = $"↑ {UiHelper.FormatSpeed(s.NetUploadBytesPerSecond)}";
        NetworkDetailInterfaceText.Text = string.IsNullOrWhiteSpace(s.NetworkInterfaceName)
            ? "未识别"
            : s.NetworkInterfaceName;
        NetworkDetailTotalText.Text = $"总吞吐 {UiHelper.FormatSpeed(s.NetTotalBytesPerSecond)}";
        NetworkDetailPeakUpText.Text = UiHelper.FormatSpeed(s.NetUploadMaxBytesPerSecond);
        NetworkDetailPeakDownText.Text = UiHelper.FormatSpeed(s.NetDownloadMaxBytesPerSecond);
        NetworkDetailDownChipText.Text = UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond);
        NetworkDetailUpChipText.Text = UiHelper.FormatSpeed(s.NetUploadBytesPerSecond);
        NetworkDetailInterfaceFootText.Text = string.IsNullOrWhiteSpace(s.NetworkInterfaceName) ? "未识别" : s.NetworkInterfaceName;
        NetworkDetailTotalFootText.Text = UiHelper.FormatSpeed(s.NetTotalBytesPerSecond);
        NetworkDetailDownFootText.Text = UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond);
        NetworkDetailUpFootText.Text = UiHelper.FormatSpeed(s.NetUploadBytesPerSecond);
        float networkActivity = Math.Min(100, (s.NetDownloadBytesPerSecond + s.NetUploadBytesPerSecond) / 1024f / 1024f * 10f);
        AnimateProgressBar(NetworkDetailActivityProgressBar, networkActivity);
    }

    private void ApplySystemSnapshot(HardwareSnapshot s)
    {
        if (s.IsBatteryPresent == true)
        {
            BatteryValueText.FontSize = 28;
            BatteryValueText.Text = s.BatteryChargePercent.HasValue
                ? $"{s.BatteryChargePercent.Value:0} %"
                : "电量未知";
            PowerSourceText.Text = s.IsOnAcPower switch
            {
                true => "已连接交流电源",
                false => "正在使用电池",
                _ => "电源状态未知"
            };
        }
        else
        {
            BatteryValueText.Text = "未检测到电池";
            BatteryValueText.FontSize = 24;
            PowerSourceText.Text = s.IsOnAcPower == true ? "交流电源" : "台式机或电池接口不可用";
        }

        if (s.HasSystemDriveData && s.SystemDriveTotalGb > 0)
        {
            SystemDriveFreeText.Text = $"{s.SystemDriveAvailableGb:F1} GB 可用";
            SystemDriveTotalText.Text = $"总容量 {s.SystemDriveTotalGb:F1} GB";
            double usedPercent = Math.Clamp(
                (s.SystemDriveTotalGb - s.SystemDriveAvailableGb) / s.SystemDriveTotalGb * 100,
                0,
                100);
            AnimateProgressBar(SystemDriveUsageProgressBar, usedPercent);
        }
        else
        {
            SystemDriveFreeText.Text = "数据不可用";
            SystemDriveTotalText.Text = "系统盘容量接口未返回数据";
            AnimateProgressBar(SystemDriveUsageProgressBar, 0);
        }

        SystemUptimeText.Text = FormatDuration(s.SystemUptime);
        SamplingDurationText.Text = $"{s.SamplingDurationMilliseconds:0.###} ms";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "--";
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays} 天 {duration.Hours} 小时";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分";
        return $"{duration.Minutes} 分 {duration.Seconds} 秒";
    }

    private static void AnimateGauge(CircularProgressBar gauge, double target)
    {
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromSeconds(0.35),
            EasingFunction = EaseOut
        };

        gauge.BeginAnimation(CircularProgressBar.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetGaugeUnavailable(CircularProgressBar gauge)
    {
        gauge.BeginAnimation(CircularProgressBar.ValueProperty, null);
        gauge.Value = 0;
        gauge.CenterText = "--";
    }

    private static void AnimateProgressBar(System.Windows.Controls.ProgressBar progressBar, double target)
    {
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromSeconds(0.3),
            EasingFunction = EaseOut
        };

        progressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void NavigationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavigationListBox.SelectedItem is ListBoxItem item && item.Tag is string target)
        {
            UpdateVisibleSection(target, animate: true);

            if (IsVisible)
            {
                ApplySnapshot(HardwareMonitorService.Instance.LatestSnapshot);
            }
        }
    }

    internal System.Windows.Media.Brush GetConfiguredAlertBrush(float value, int threshold)
        => ResolveConfiguredAlertBrush(_config, value, threshold);

    internal static System.Windows.Media.Brush ResolveConfiguredAlertBrush(
        AppConfig config,
        float value,
        int threshold)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.EnableAlerts) return UiHelper.NormalBrush;
        if (value >= threshold) return UiHelper.CriticalBrush;
        if (value >= threshold - config.AlertHysteresis) return UiHelper.WarningBrush;
        return UiHelper.NormalBrush;
    }

    private static bool HasGpuCapability(HardwareSnapshot snapshot, GpuMetricCapabilities capability) =>
        (snapshot.GpuCapabilities & capability) != 0;

    private void TrendRangeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrendRangeComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int minutes) && minutes > 0)
        {
            _trendRange = TimeSpan.FromMinutes(minutes);
            if (IsVisible && ReferenceEquals(_currentView, OverviewView))
                ApplyTrendSnapshot(HardwareMonitorService.Instance.LatestSnapshot);
        }
    }

    private void UpdateVisibleSection(string target, bool animate)
    {
        FrameworkElement nextView = GetSection(target);
        if (!animate)
        {
            foreach (FrameworkElement view in GetAllViews())
            {
                view.Visibility = ReferenceEquals(view, nextView) ? Visibility.Visible : Visibility.Collapsed;
                view.Opacity = ReferenceEquals(view, nextView) ? 1 : 0;
                GetTranslateTransform(view).Y = 0;
            }

            _currentView = nextView;
            return;
        }

        if (ReferenceEquals(_currentView, nextView))
        {
            return;
        }

        FrameworkElement? previousView = _currentView;
        _currentView = nextView;

        if (previousView != null)
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromSeconds(0.12));
            fadeOut.Completed += (_, _) =>
            {
                if (ShouldCollapseAfterTransition(_currentView, previousView))
                    previousView.Visibility = Visibility.Collapsed;
            };
            previousView.BeginAnimation(OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
        }

        TranslateTransform nextTransform = GetTranslateTransform(nextView);
        nextView.Visibility = Visibility.Visible;
        nextView.Opacity = 0;
        nextTransform.Y = 10;

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromSeconds(0.25),
            EasingFunction = EaseOut
        };
        var slideIn = new DoubleAnimation
        {
            From = 10,
            To = 0,
            Duration = TimeSpan.FromSeconds(0.25),
            EasingFunction = EaseOut
        };

        nextView.BeginAnimation(OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);
        nextTransform.BeginAnimation(TranslateTransform.YProperty, slideIn, HandoffBehavior.SnapshotAndReplace);
    }

    private Grid GetSection(string target) => target switch
    {
        "Cpu" => CpuView,
        "Gpu" => GpuView,
        "Ram" => RamView,
        "Network" => NetworkView,
        "System" => SystemView,
        _ => OverviewView
    };

    private FrameworkElement[] GetAllViews() => [OverviewView, CpuView, GpuView, RamView, NetworkView, SystemView];

    internal static bool ShouldCollapseAfterTransition(object? currentView, object previousView) =>
        !ReferenceEquals(currentView, previousView);

    private static TranslateTransform GetTranslateTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static CubicEase CreateFrozenEaseOut()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }

}
