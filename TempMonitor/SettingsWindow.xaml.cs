using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfSelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace TempMonitor;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the Window lifetime; OnClosed cancels and disposes the token source.")]
public partial class SettingsWindow : Window
{
    private readonly HardwareMonitorService _monitorService;
    private readonly AppConfig _workingConfig;
    private readonly CancellationTokenSource _updateCancellation = new();

    public ObservableCollection<MetricItem> MetricItems { get; } = [];
    public event Action<AppConfig>? SettingsApplied;

    internal SettingsWindow(AppConfig config, HardwareMonitorService monitorService)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(monitorService);

        _workingConfig = config.Clone();
        _monitorService = monitorService;
        DataContext = this;
        InitializeComponent();
        PopulateChoices();
        LoadConfigIntoControls();
        RefreshDeviceLists();
        RefreshDiagnostics();
    }

    private void PopulateChoices()
    {
        GpuDisplayMetricComboBox.ItemsSource = new[]
        {
            new Choice<GpuDisplayMetric>("自动（温度 → 负载 → 功耗）", GpuDisplayMetric.Auto),
            new Choice<GpuDisplayMetric>("温度", GpuDisplayMetric.Temperature),
            new Choice<GpuDisplayMetric>("使用率", GpuDisplayMetric.Usage),
            new Choice<GpuDisplayMetric>("功耗", GpuDisplayMetric.Power)
        };
        NetworkUnitComboBox.ItemsSource = new[]
        {
            new Choice<NetworkDisplayUnit>("自动 B/s", NetworkDisplayUnit.Auto),
            new Choice<NetworkDisplayUnit>("KiB/s · MiB/s", NetworkDisplayUnit.BytesPerSecond),
            new Choice<NetworkDisplayUnit>("Kbps · Mbps", NetworkDisplayUnit.BitsPerSecond)
        };
        var memoryModes = new[]
        {
            new Choice<MemoryDisplayMode>("已用容量", MemoryDisplayMode.Used),
            new Choice<MemoryDisplayMode>("使用百分比", MemoryDisplayMode.Percentage)
        };
        RamDisplayModeComboBox.ItemsSource = memoryModes;
        VramDisplayModeComboBox.ItemsSource = memoryModes;
        ThemeComboBox.ItemsSource = new[]
        {
            new Choice<WidgetTheme>("深色", WidgetTheme.Dark),
            new Choice<WidgetTheme>("浅色", WidgetTheme.Light)
        };
        NetworkSelectionModeComboBox.ItemsSource = new[]
        {
            new Choice<NetworkSelectionMode>("自动选择活跃网卡", NetworkSelectionMode.Auto),
            new Choice<NetworkSelectionMode>("汇总全部可用网卡", NetworkSelectionMode.Aggregate),
            new Choice<NetworkSelectionMode>("固定网卡", NetworkSelectionMode.Fixed)
        };
        AlertPresentationComboBox.ItemsSource = new[]
        {
            new Choice<AlertPresentation>("仅数值变色（低干扰）", AlertPresentation.ColorOnly),
            new Choice<AlertPresentation>("托盘通知", AlertPresentation.TrayNotification),
            new Choice<AlertPresentation>("挂件背景闪烁", AlertPresentation.Flash)
        };
        SamplingIntervalComboBox.ItemsSource = new[]
        {
            new Choice<int>("1 秒", 1),
            new Choice<int>("2 秒", 2),
            new Choice<int>("5 秒", 5)
        };
        FullscreenBehaviorComboBox.ItemsSource = new[]
        {
            new Choice<FullscreenBehavior>("保持显示", FullscreenBehavior.StayVisible),
            new Choice<FullscreenBehavior>("自动隐藏", FullscreenBehavior.Hide),
            new Choice<FullscreenBehavior>("降低透明度", FullscreenBehavior.Dim)
        };
        DelayedStartComboBox.ItemsSource = new[]
        {
            new Choice<int>("关闭", 0),
            new Choice<int>("10 秒", 10),
            new Choice<int>("20 秒", 20),
            new Choice<int>("30 秒", 30),
            new Choice<int>("60 秒", 60)
        };
    }

    private void LoadConfigIntoControls()
    {
        foreach (WidgetMetric metric in _workingConfig.MetricOrder)
        {
            MetricItems.Add(new MetricItem(
                metric,
                GetMetricDisplayName(metric),
                _workingConfig.IsMetricVisible(metric)));
        }

        GpuDisplayMetricComboBox.SelectedValue = _workingConfig.GpuDisplayMetric;
        NetworkUnitComboBox.SelectedValue = _workingConfig.NetworkDisplayUnit;
        RamDisplayModeComboBox.SelectedValue = _workingConfig.RamDisplayMode;
        VramDisplayModeComboBox.SelectedValue = _workingConfig.VramDisplayMode;
        ThemeComboBox.SelectedValue = _workingConfig.Theme;
        OpacitySlider.Value = _workingConfig.WidgetOpacity * 100;
        OpacityValueText.Text = $"{OpacitySlider.Value:0}%";
        OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
        AlwaysOnTopCheckBox.IsChecked = _workingConfig.AlwaysOnTop;

        NetworkSelectionModeComboBox.SelectedValue = _workingConfig.NetworkSelectionMode;
        EnableAlertsCheckBox.IsChecked = _workingConfig.EnableAlerts;
        AlertPresentationComboBox.SelectedValue = _workingConfig.AlertPresentation;
        CpuAlertThresholdTextBox.Text = _workingConfig.CpuUsageAlertThreshold.ToString(CultureInfo.InvariantCulture);
        GpuAlertThresholdTextBox.Text = _workingConfig.GpuTemperatureAlertThreshold.ToString(CultureInfo.InvariantCulture);
        RamAlertThresholdTextBox.Text = _workingConfig.RamUsageAlertThreshold.ToString(CultureInfo.InvariantCulture);
        AlertSustainTextBox.Text = _workingConfig.AlertSustainSeconds.ToString(CultureInfo.InvariantCulture);
        AlertHysteresisTextBox.Text = _workingConfig.AlertHysteresis.ToString(CultureInfo.InvariantCulture);
        AlertCooldownTextBox.Text = _workingConfig.AlertCooldownSeconds.ToString(CultureInfo.InvariantCulture);

        SamplingIntervalComboBox.SelectedValue = _workingConfig.SamplingIntervalSeconds;
        AdaptiveSamplingCheckBox.IsChecked = _workingConfig.EnableAdaptiveSampling;
        FullscreenBehaviorComboBox.SelectedValue = _workingConfig.FullscreenBehavior;
        GlobalHotkeyCheckBox.IsChecked = _workingConfig.EnableGlobalHotkey;
        ProcessTrackingCheckBox.IsChecked = _workingConfig.TrackTopGpuProcess;
        DelayedStartComboBox.SelectedValue = _workingConfig.DelayedStartSeconds;
        UpdateNetworkInterfaceEnabledState();
    }

    private void RefreshDeviceLists(bool preserveCurrentSelection = false)
    {
        GpuDeviceChoice? currentGpuSelection = preserveCurrentSelection
            ? GpuDeviceComboBox.SelectedItem as GpuDeviceChoice
            : null;
        NetworkDeviceChoice? currentNetworkSelection = preserveCurrentSelection
            ? NetworkInterfaceComboBox.SelectedItem as NetworkDeviceChoice
            : null;
        string? desiredGpuProvider = currentGpuSelection != null
            ? currentGpuSelection.ProviderName
            : _workingConfig.PreferredGpuProvider;
        string? desiredGpuIdentifier = currentGpuSelection != null
            ? currentGpuSelection.DeviceIdentifier
            : _workingConfig.PreferredGpuDeviceIdentifier;
        string? desiredNetworkIdentifier = currentNetworkSelection?.InterfaceId ?? _workingConfig.PreferredNetworkInterfaceId;

        IReadOnlyList<GpuDeviceInfo> gpuDevices = _monitorService.GetAvailableGpuDevices();
        var gpuChoices = new List<GpuDeviceChoice>
        {
            new("自动选择活跃 GPU", null, null)
        };
        gpuChoices.AddRange(gpuDevices
            .OrderBy(device => device.ProviderName, StringComparer.Ordinal)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCulture)
            .Select(device => new GpuDeviceChoice(
                string.Equals(device.ProviderName, "Windows", StringComparison.Ordinal)
                    ? $"{device.ProviderName} · {device.DisplayName}（会话标识）"
                    : $"{device.ProviderName} · {device.DisplayName}",
                device.ProviderName,
                device.DeviceIdentifier)));

        GpuDeviceChoice? selectedGpu = gpuChoices.FirstOrDefault(choice =>
            string.Equals(choice.ProviderName, desiredGpuProvider, StringComparison.Ordinal) &&
            string.Equals(choice.DeviceIdentifier, desiredGpuIdentifier, StringComparison.Ordinal));
        if (selectedGpu == null && !string.IsNullOrWhiteSpace(desiredGpuIdentifier))
        {
            selectedGpu = new GpuDeviceChoice(
                "之前选择的 GPU（当前不可用，将自动回退）",
                desiredGpuProvider,
                desiredGpuIdentifier);
            gpuChoices.Add(selectedGpu);
        }
        GpuDeviceComboBox.ItemsSource = gpuChoices;
        GpuDeviceComboBox.SelectedItem = selectedGpu ?? gpuChoices[0];

        IReadOnlyList<NetworkInterfaceInfo> networkInterfaces = _monitorService.GetAvailableNetworkInterfaces();
        var networkChoices = networkInterfaces
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
            .Select(item => new NetworkDeviceChoice(item.DisplayName, item.InterfaceId))
            .ToList();
        NetworkDeviceChoice? selectedNetwork = networkChoices.FirstOrDefault(choice => string.Equals(
            choice.InterfaceId,
            desiredNetworkIdentifier,
            StringComparison.Ordinal));
        if (selectedNetwork == null && !string.IsNullOrWhiteSpace(desiredNetworkIdentifier))
        {
            selectedNetwork = new NetworkDeviceChoice(
                "之前选择的网卡（当前不可用，将自动回退）",
                desiredNetworkIdentifier);
            networkChoices.Add(selectedNetwork);
        }
        NetworkInterfaceComboBox.ItemsSource = networkChoices;
        NetworkInterfaceComboBox.SelectedItem = selectedNetwork ?? networkChoices.FirstOrDefault();
        UpdateNetworkInterfaceEnabledState();
    }

    private bool TryReadControlsIntoWorkingConfig()
    {
        if (!MetricItems.Any(item => item.IsVisible))
        {
            ShowValidationError("请至少保留一个挂件显示项。");
            return false;
        }

        if (!TryReadInt(CpuAlertThresholdTextBox, 50, 100, "CPU 告警阈值", out int cpuThreshold) ||
            !TryReadInt(GpuAlertThresholdTextBox, 40, 120, "GPU 温度告警阈值", out int gpuThreshold) ||
            !TryReadInt(RamAlertThresholdTextBox, 50, 100, "内存告警阈值", out int ramThreshold) ||
            !TryReadInt(AlertSustainTextBox, 0, 60, "持续时间", out int sustain) ||
            !TryReadInt(AlertHysteresisTextBox, 1, 20, "恢复滞回", out int hysteresis) ||
            !TryReadInt(AlertCooldownTextBox, 0, 3600, "提醒冷却", out int cooldown))
        {
            return false;
        }

        _workingConfig.MetricOrder = MetricItems.Select(item => item.Metric).ToList();
        foreach (MetricItem item in MetricItems)
            _workingConfig.SetMetricVisible(item.Metric, item.IsVisible);

        _workingConfig.GpuDisplayMetric = GetSelectedValue(GpuDisplayMetricComboBox, GpuDisplayMetric.Auto);
        _workingConfig.NetworkDisplayUnit = GetSelectedValue(NetworkUnitComboBox, NetworkDisplayUnit.Auto);
        _workingConfig.RamDisplayMode = GetSelectedValue(RamDisplayModeComboBox, MemoryDisplayMode.Used);
        _workingConfig.VramDisplayMode = GetSelectedValue(VramDisplayModeComboBox, MemoryDisplayMode.Used);
        _workingConfig.Theme = GetSelectedValue(ThemeComboBox, WidgetTheme.Dark);
        _workingConfig.WidgetOpacity = OpacitySlider.Value / 100d;
        _workingConfig.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;

        _workingConfig.NetworkSelectionMode = GetSelectedValue(
            NetworkSelectionModeComboBox,
            NetworkSelectionMode.Auto);
        if (_workingConfig.NetworkSelectionMode == NetworkSelectionMode.Fixed &&
            NetworkInterfaceComboBox.SelectedItem is NetworkDeviceChoice selectedNetwork)
        {
            _workingConfig.PreferredNetworkInterfaceId = selectedNetwork.InterfaceId;
        }
        else
        {
            _workingConfig.PreferredNetworkInterfaceId = null;
        }

        if (GpuDeviceComboBox.SelectedItem is GpuDeviceChoice selectedGpu)
        {
            _workingConfig.PreferredGpuProvider = selectedGpu.ProviderName;
            _workingConfig.PreferredGpuDeviceIdentifier = selectedGpu.DeviceIdentifier;
        }

        _workingConfig.EnableAlerts = EnableAlertsCheckBox.IsChecked == true;
        _workingConfig.AlertPresentation = GetSelectedValue(AlertPresentationComboBox, AlertPresentation.ColorOnly);
        _workingConfig.CpuUsageAlertThreshold = cpuThreshold;
        _workingConfig.GpuTemperatureAlertThreshold = gpuThreshold;
        _workingConfig.RamUsageAlertThreshold = ramThreshold;
        _workingConfig.AlertSustainSeconds = sustain;
        _workingConfig.AlertHysteresis = hysteresis;
        _workingConfig.AlertCooldownSeconds = cooldown;

        _workingConfig.SamplingIntervalSeconds = GetSelectedValue(SamplingIntervalComboBox, 1);
        _workingConfig.EnableAdaptiveSampling = AdaptiveSamplingCheckBox.IsChecked == true;
        _workingConfig.FullscreenBehavior = GetSelectedValue(
            FullscreenBehaviorComboBox,
            FullscreenBehavior.StayVisible);
        _workingConfig.EnableGlobalHotkey = GlobalHotkeyCheckBox.IsChecked == true;
        _workingConfig.TrackTopGpuProcess = ProcessTrackingCheckBox.IsChecked == true;
        _workingConfig.DelayedStartSeconds = GetSelectedValue(DelayedStartComboBox, 0);
        _workingConfig.Normalize();

        return true;
    }

    private bool TryApplyControls()
    {
        if (!TryReadControlsIntoWorkingConfig())
            return false;

        SettingsApplied?.Invoke(_workingConfig.Clone());
        return true;
    }

    private void MoveMetricUp_Click(object sender, RoutedEventArgs e) => MoveSelectedMetric(-1);

    private void MoveMetricDown_Click(object sender, RoutedEventArgs e) => MoveSelectedMetric(1);

    private void MoveSelectedMetric(int offset)
    {
        int currentIndex = MetricListBox.SelectedIndex;
        int targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= MetricItems.Count)
            return;

        MetricItems.Move(currentIndex, targetIndex);
        MetricListBox.SelectedIndex = targetIndex;
        MetricListBox.ScrollIntoView(MetricItems[targetIndex]);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText != null)
            OpacityValueText.Text = $"{e.NewValue:0}%";
    }

    private void NetworkSelectionModeComboBox_SelectionChanged(object sender, WpfSelectionChangedEventArgs e) =>
        UpdateNetworkInterfaceEnabledState();

    private void UpdateNetworkInterfaceEnabledState()
    {
        if (NetworkInterfaceComboBox == null || NetworkSelectionModeComboBox == null)
            return;

        NetworkInterfaceComboBox.IsEnabled = GetSelectedValue(
            NetworkSelectionModeComboBox,
            NetworkSelectionMode.Auto) == NetworkSelectionMode.Fixed;
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        CancellationToken cancellationToken = _updateCancellation.Token;
        string originalContent = RefreshDevicesButton.Content?.ToString() ?? "刷新设备列表";
        var refreshCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnDevicesRefreshed() => refreshCompleted.TrySetResult(true);

        RefreshDevicesButton.IsEnabled = false;
        RefreshDevicesButton.Content = "正在重新探测...";
        _monitorService.DevicesRefreshed += OnDevicesRefreshed;
        try
        {
            _monitorService.RequestDeviceRefresh();
            await Task.WhenAny(
                refreshCompleted.Task,
                Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            RefreshDeviceLists(preserveCurrentSelection: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _monitorService.DevicesRefreshed -= OnDevicesRefreshed;
            if (IsLoaded)
            {
                RefreshDevicesButton.Content = originalContent;
                RefreshDevicesButton.IsEnabled = true;
            }
        }
    }

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private void ExportConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadControlsIntoWorkingConfig())
            return;

        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "gxTempMonitor-config.json",
                DefaultExt = ".json",
                Filter = "JSON 配置|*.json"
            };
            if (dialog.ShowDialog(this) != true) return;

            File.WriteAllText(
                dialog.FileName,
                ConfigStore.SerializeForExport(_workingConfig),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            System.Windows.MessageBox.Show(
                this,
                "配置导出失败，请检查目标目录是否可写。",
                "gxTempMonitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (InvalidOperationException)
        {
            System.Windows.MessageBox.Show(
                this,
                "配置来自更高版本、文件过大、损坏或暂时无法读取，本版本不会导出可能丢失数据的副本。",
                "配置为只读",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void ImportConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                DefaultExt = ".json",
                Filter = "JSON 配置|*.json",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;

            var file = new FileInfo(dialog.FileName);
            if (file.Length > 256 * 1024 ||
                !ConfigStore.TryParseImport(File.ReadAllText(dialog.FileName), out AppConfig imported))
            {
                throw new InvalidDataException("The imported configuration is invalid.");
            }

            MessageBoxResult answer = System.Windows.MessageBox.Show(
                this,
                "导入会替换显示、设备、告警和低干扰选项；当前挂件位置会保留。是否继续？",
                "导入配置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            SettingsApplied?.Invoke(imported);
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or InvalidDataException)
        {
            System.Windows.MessageBox.Show(
                this,
                "无法导入该配置。文件可能损坏、过大或不是 gxTempMonitor 配置。",
                "导入配置失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CancellationToken cancellationToken = _updateCancellation.Token;
        CheckUpdatesButton.IsEnabled = false;
        string originalContent = CheckUpdatesButton.Content?.ToString() ?? "手动检查更新";
        CheckUpdatesButton.Content = "正在检查...";
        try
        {
            UpdateCheckResult result = await UpdateChecker.CheckAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested || !IsLoaded)
                return;
            if (result.IsUpdateAvailable)
            {
                MessageBoxResult answer = System.Windows.MessageBox.Show(
                    this,
                    $"发现新版本 {result.LatestTag}，当前版本为 {result.CurrentVersion}。\n\n是否打开官方 GitHub Release 页面？",
                    "发现更新",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesUrl)
                    {
                        UseShellExecute = true
                    });
                }
            }
            else
            {
                System.Windows.MessageBox.Show(
                    this,
                    $"当前已经是最新版本（{result.LatestTag}）。",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException or Win32Exception)
        {
            System.Windows.MessageBox.Show(
                this,
                "暂时无法从 GitHub 获取最新版本信息。程序不会在后台重试或下载文件。",
                "检查更新失败",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        finally
        {
            if (IsLoaded)
            {
                CheckUpdatesButton.Content = originalContent;
                CheckUpdatesButton.IsEnabled = true;
            }
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(DiagnosticsTextBox.Text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(
                this,
                "暂时无法访问剪贴板，请稍后重试。",
                "gxTempMonitor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void RefreshDiagnostics() =>
        DiagnosticsTextBox.Text = _monitorService.CreateDiagnosticReport();

    private void Apply_Click(object sender, RoutedEventArgs e) => TryApplyControls();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (TryApplyControls())
            Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _updateCancellation.Cancel();
        _updateCancellation.Dispose();
        base.OnClosed(e);
    }

    private bool TryReadInt(WpfTextBox textBox, int minimum, int maximum, string fieldName, out int value)
    {
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value >= minimum && value <= maximum)
        {
            return true;
        }

        ShowValidationError($"{fieldName}应在 {minimum} 到 {maximum} 之间。");
        textBox.Focus();
        textBox.SelectAll();
        return false;
    }

    private void ShowValidationError(string message) => System.Windows.MessageBox.Show(
        this,
        message,
        "设置无法应用",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);

    private static T GetSelectedValue<T>(WpfComboBox comboBox, T fallback) =>
        comboBox.SelectedValue is T value ? value : fallback;

    private static string GetMetricDisplayName(WidgetMetric metric) => metric switch
    {
        WidgetMetric.Cpu => "CPU 使用率",
        WidgetMetric.Gpu => "GPU 主数值",
        WidgetMetric.Ram => "内存",
        WidgetMetric.Vram => "显存",
        WidgetMetric.Upload => "网络上传",
        WidgetMetric.Download => "网络下载",
        _ => metric.ToString()
    };

    public sealed class MetricItem : INotifyPropertyChanged
    {
        private bool _isVisible;

        public MetricItem(WidgetMetric metric, string displayName, bool isVisible)
        {
            Metric = metric;
            DisplayName = displayName;
            _isVisible = isVisible;
        }

        public WidgetMetric Metric { get; }
        public string DisplayName { get; }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record Choice<T>(string Label, T Value);
    private sealed record GpuDeviceChoice(string Label, string? ProviderName, string? DeviceIdentifier);
    private sealed record NetworkDeviceChoice(string Label, string InterfaceId);
}
