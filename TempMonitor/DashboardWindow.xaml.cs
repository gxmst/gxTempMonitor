using System;
using System.ComponentModel;
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
    private const int TrendCapacity = 48;
    private static readonly CubicEase EaseOut = CreateFrozenEaseOut();

    private bool _allowClose;
    private volatile bool _isMonitoring;
    private int _monitorGeneration;
    private FrameworkElement? _currentView;
    private DateTime _lastTrendTimestamp = DateTime.MinValue;
    private readonly TrendBuffer _cpuTrend = new(TrendCapacity);
    private readonly TrendBuffer _gpuTrend = new(TrendCapacity);
    private readonly TrendBuffer _ramTrend = new(TrendCapacity);
    private readonly TrendBuffer _vramTrend = new(TrendCapacity);
    private readonly TrendBuffer _upTrend = new(TrendCapacity);
    private readonly TrendBuffer _downTrend = new(TrendCapacity);

    public DashboardWindow()
    {
        InitializeComponent();
        _currentView = OverviewView;
        UpdateVisibleSection("Overview", animate: false);
        NavigationListBox.SelectedIndex = 0;
        IsVisibleChanged += DashboardWindow_IsVisibleChanged;
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
        RecordTrends(snapshot);
        ApplyCurrentViewSnapshot(snapshot);
    }

    private void RecordTrends(HardwareSnapshot snapshot)
    {
        if (snapshot.Timestamp == _lastTrendTimestamp)
        {
            return;
        }

        _lastTrendTimestamp = snapshot.Timestamp;
        if (snapshot.HasCpuUsage)
            _cpuTrend.Add(snapshot.CpuUsage);
        if (snapshot.GpuTemperature.HasValue)
            _gpuTrend.Add(snapshot.GpuTemperature.Value);
        if (snapshot.HasRamData)
            _ramTrend.Add(snapshot.RamUsedGb);
        if (snapshot.VramUsedGb.HasValue)
            _vramTrend.Add(snapshot.VramUsedGb.Value);
        if (snapshot.HasNetworkData)
        {
            _upTrend.Add(snapshot.NetUploadBytesPerSecond);
            _downTrend.Add(snapshot.NetDownloadBytesPerSecond);
        }
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
        CpuTempValueText.Text = "温度 --";

        if (s.HasGpuUsage)
            AnimateGauge(GpuGauge, s.GpuUsagePercent);
        else
            SetGaugeUnavailable(GpuGauge);
        GpuTempValueText.Text = UiHelper.FormatOptionalTemp(s.GpuTemperature);
        GpuTempValueText.Foreground = UiHelper.GetAlertBrush(s.GpuTemperature ?? 0);
        GpuVramValueText.Text = s.VramUsedGb.HasValue
            ? $"显存 {s.VramUsedGb.Value:F1} GB"
            : "显存 --";

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
        TrendCpuValueText.Text = s.HasCpuUsage ? $"{s.CpuUsage:0.0} %" : "--";
        TrendGpuValueText.Text = UiHelper.FormatOptionalTemp(s.GpuTemperature);
        TrendRamValueText.Text = s.HasRamData ? $"{s.RamUsedGb:F1} GB" : "--";
        TrendVramValueText.Text = s.VramUsedGb.HasValue ? $"{s.VramUsedGb.Value:F1} GB" : "--";
        TrendUpValueText.Text = s.HasNetworkData ? UiHelper.FormatSpeed(s.NetUploadBytesPerSecond) : "--";
        TrendDownValueText.Text = s.HasNetworkData ? UiHelper.FormatSpeed(s.NetDownloadBytesPerSecond) : "--";
        TrendCpuChart.UpdateValues(_cpuTrend);
        TrendGpuChart.UpdateValues(_gpuTrend);
        TrendRamChart.UpdateValues(_ramTrend);
        TrendVramChart.UpdateValues(_vramTrend);
        TrendUpChart.UpdateValues(_upTrend);
        TrendDownChart.UpdateValues(_downTrend);
    }

    private void ApplyCpuDetailSnapshot(HardwareSnapshot s)
    {
        if (!s.HasCpuUsage)
        {
            CpuDetailUsageText.Text = "--";
            CpuDetailUsageText.Foreground = UiHelper.NormalBrush;
            CpuDetailClockText.Text = "频率 --";
            CpuDetailTemperatureText.Text = "--";
            CpuDetailPowerText.Text = "功耗 --";
            CpuDetailMaxUsageText.Text = s.CpuUsageMax > 0 ? $"{s.CpuUsageMax:0.0} %" : "--";
            CpuDetailMaxTempText.Text = "--";
            CpuDetailClockChipText.Text = "--";
            CpuDetailPowerChipText.Text = "--";
            CpuDetailUsageFootText.Text = "--";
            CpuDetailTempFootText.Text = "--";
            CpuDetailPowerFootText.Text = "--";
            CpuDetailFreqFootText.Text = "--";
            AnimateProgressBar(CpuDetailUsageProgressBar, 0);
            return;
        }

        CpuDetailUsageText.Text = $"{s.CpuUsage:0.0} %";
        CpuDetailUsageText.Foreground = UiHelper.GetAlertBrush(s.CpuUsage);
        CpuDetailClockText.Text = "频率 --";
        CpuDetailTemperatureText.Text = "--";
        CpuDetailPowerText.Text = "功耗 --";

        CpuDetailMaxUsageText.Text = $"{s.CpuUsageMax:0.0} %";
        CpuDetailMaxTempText.Text = "--";
        CpuDetailClockChipText.Text = "--";
        CpuDetailPowerChipText.Text = "--";
        CpuDetailUsageFootText.Text = $"{s.CpuUsage:0.0} %";
        CpuDetailTempFootText.Text = "--";
        CpuDetailPowerFootText.Text = "--";
        CpuDetailFreqFootText.Text = "--";
        AnimateProgressBar(CpuDetailUsageProgressBar, s.CpuUsage);
    }

    private void ApplyGpuDetailSnapshot(HardwareSnapshot s)
    {
        GpuDetailUsageText.Text = s.HasGpuUsage ? $"{s.GpuUsagePercent:0.0} %" : "--";
        GpuDetailUsageText.Foreground = s.HasGpuUsage
            ? UiHelper.GetAlertBrush(s.GpuUsagePercent)
            : UiHelper.NormalBrush;
        GpuDetailVramText.Text = s.VramUsedGb.HasValue
            ? $"显存 {s.VramUsedGb.Value:F1} GB"
            : "显存 --";
        GpuDetailMemoryValueText.Text = UiHelper.FormatOptionalGb(s.VramUsedGb);
        GpuDetailTemperatureText.Text = UiHelper.FormatOptionalTemp(s.GpuTemperature);
        GpuDetailTemperatureText.Foreground = UiHelper.GetAlertBrush(s.GpuTemperature ?? 0);
        GpuDetailPowerText.Text = s.GpuPowerWatts.HasValue
            ? $"功耗 {s.GpuPowerWatts.Value:0.0} W"
            : "功耗 --";
        GpuDetailFanText.Text = "风扇 --";

        string gpuPowerText = s.GpuPowerWatts.HasValue ? $"{s.GpuPowerWatts.Value:0.0} W" : "--";
        string gpuFanText = "--";
        string gpuTempText = UiHelper.FormatOptionalTemp(s.GpuTemperature, "--");

        GpuDetailMaxTempText.Text = UiHelper.FormatOptionalTemp(s.GpuTemperatureMax);
        GpuDetailFanChipText.Text = gpuFanText;
        GpuDetailPowerChipText.Text = gpuPowerText;
        GpuDetailUsageFootText.Text = s.HasGpuUsage ? $"{s.GpuUsagePercent:0.0} %" : "--";
        GpuDetailTempFootText.Text = gpuTempText;
        GpuDetailPowerFootText.Text = gpuPowerText;
        GpuDetailFanFootText.Text = gpuFanText;
        AnimateProgressBar(GpuDetailUsageProgressBar, s.HasGpuUsage ? s.GpuUsagePercent : 0);
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
        RamDetailUsedText.Foreground = UiHelper.GetAlertBrush(s.RamUsagePercent);
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
            fadeOut.Completed += (_, _) => previousView.Visibility = Visibility.Collapsed;
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
        _ => OverviewView
    };

    private FrameworkElement[] GetAllViews() => [OverviewView, CpuView, GpuView, RamView, NetworkView];

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

    private sealed class TrendBuffer : IReadOnlyList<double>
    {
        private readonly double[] _values;
        private int _start;

        public TrendBuffer(int capacity)
        {
            _values = new double[capacity];
        }

        public int Count { get; private set; }

        public double this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _values[(_start + index) % _values.Length];
            }
        }

        public void Add(double value)
        {
            if (Count < _values.Length)
            {
                _values[(_start + Count) % _values.Length] = value;
                Count++;
                return;
            }

            _values[_start] = value;
            _start = (_start + 1) % _values.Length;
        }

        public IEnumerator<double> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
