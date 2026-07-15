using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TempMonitor;

public sealed class HardwareSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    internal long MonotonicTimestamp { get; init; } = Stopwatch.GetTimestamp();
    public string? CpuName { get; init; }
    public int LogicalProcessorCount { get; init; }
    public string? CpuArchitecture { get; init; }
    public TimeSpan SystemUptime { get; init; }
    public float CpuUsage { get; init; }
    public bool HasCpuUsage { get; init; }
    public float CpuUsageMax { get; init; }
    public float GpuUsagePercent { get; init; }
    public bool HasGpuUsage { get; init; }
    public float? GpuUsageMaxPercent { get; init; }
    public float? GpuTemperature { get; init; }
    public float? GpuTemperatureMax { get; init; }
    public float? GpuPowerWatts { get; init; }
    public float? GpuPowerMaxWatts { get; init; }
    public string? GpuDeviceName { get; init; }
    public string? GpuProviderName { get; init; }
    public GpuMetricCapabilities GpuCapabilities { get; init; }
    public GpuPrimaryMetric GpuPrimaryMetric { get; init; }
    public float RamUsedGb { get; init; }
    public bool HasRamData { get; init; }
    public float RamAvailableGb { get; init; }
    public float RamUsedMaxGb { get; init; }
    public float RamUsagePercent { get; init; }
    public float TotalRamGb { get; init; }
    public float? VramUsedGb { get; init; }
    public float? VramUsedMaxGb { get; init; }
    public float? VramTotalGb { get; init; }
    public bool? IsBatteryPresent { get; init; }
    public float? BatteryChargePercent { get; init; }
    public bool? IsOnAcPower { get; init; }
    public bool HasSystemDriveData { get; init; }
    public float SystemDriveTotalGb { get; init; }
    public float SystemDriveAvailableGb { get; init; }
    public string? NetworkInterfaceName { get; init; }
    public float NetTotalBytesPerSecond { get; init; }
    public bool HasNetworkData { get; init; }
    public float NetUploadBytesPerSecond { get; init; }
    public float NetUploadMaxBytesPerSecond { get; init; }
    public float NetDownloadBytesPerSecond { get; init; }
    public float NetDownloadMaxBytesPerSecond { get; init; }
    public string? TopGpuProcess { get; init; }
    public double SamplingDurationMilliseconds { get; init; }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal sealed class MemoryStatusEx
{
    public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
    public uint MemoryLoad;
    public ulong TotalPhysical;
    public ulong AvailablePhysical;
    public ulong TotalPageFile;
    public ulong AvailablePageFile;
    public ulong TotalVirtual;
    public ulong AvailableVirtual;
    public ulong AvailableExtendedVirtual;
}

public sealed class HardwareMonitorService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    private readonly record struct NetworkBaseline(long Received, long Sent, long Timestamp);

    private readonly record struct NetworkRate(
        string Id,
        string Name,
        bool HasMeasurement,
        float Upload,
        float Download);

    private readonly record struct GpuCandidate(IGpuMonitor Monitor, GpuReading Reading);

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    private static readonly Lazy<HardwareMonitorService> LazyInstance =
        new(() => new HardwareMonitorService(), LazyThreadSafetyMode.ExecutionAndPublication);

    private const int MaxLogBytes = 256 * 1024;
    private const int MaxHistoryEntries = 3600;
    private const int NetworkRefreshSeconds = 10;
    private const int GpuRetrySeconds = 30;
    private const int ProcessRefreshSeconds = 15;
    private const int GpuProviderMissingSamplesBeforeFallback = 3;

    private readonly object _maxLock = new();
    private readonly object _historyLock = new();
    private readonly object _logLock = new();
    private readonly object _deviceStateLock = new();
    private readonly object _selectionCommitLock = new();
    private readonly Queue<HardwareSnapshot> _history = new(MaxHistoryEntries);
    private readonly Dictionary<string, NetworkBaseline> _networkBaselines = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedErrors = new(StringComparer.Ordinal);
    private readonly List<IGpuMonitor> _gpuMonitors = new();
    private readonly GpuPresentationStabilizer _gpuPresentationStabilizer = new();
    private readonly SystemMetricsReader _systemMetricsReader = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly AutoResetEvent _pollSignal = new(initialState: false);
    private readonly Thread _pollThread;

    private NetworkInterface[] _networkInterfaces = [];
    private GpuDeviceInfo[] _availableGpuDevices = [];
    private NetworkInterfaceInfo[] _availableNetworkInterfaces = [];
    private HardwareSnapshot _latestSnapshot = new();
    private string? _networkInterfaceId;
    private string? _networkInterfaceName;
    private string? _cachedTopGpuProcess;
    private string? _selectedGpuProvider;
    private MonitoringSelectionOptions _selectionOptions = MonitoringSelectionOptions.Default;

    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;
    private float _lastCpuUsage;
    private bool _hasCpuBaseline;
    private long _nextNetworkRefreshTimestamp;
    private long _nextGpuProbeTimestamp;
    private long _nextProcessRefreshTimestamp;

    private float _cpuUsageMax;
    private float? _gpuUsageMax;
    private float _gpuTemperatureMax;
    private float? _gpuPowerMax;
    private float _ramUsedMax;
    private float _vramUsedMax;
    private float _uploadMax;
    private float _downloadMax;

    private int _samplingIntervalMilliseconds = 1000;
    private int _trackTopGpuProcess;
    private int _deviceRefreshRequested;
    private int _selectedGpuProviderMissingSamples;
    private int _disposeState;

    public static HardwareMonitorService Instance => LazyInstance.Value;
    public static bool IsValueCreated => LazyInstance.IsValueCreated;

    public event Action<HardwareSnapshot>? DataUpdated;
    public event Action? DevicesRefreshed;

    public HardwareSnapshot LatestSnapshot => Volatile.Read(ref _latestSnapshot);

    internal IReadOnlyList<GpuDeviceInfo> GetAvailableGpuDevices()
    {
        lock (_deviceStateLock)
            return (GpuDeviceInfo[])_availableGpuDevices.Clone();
    }

    internal IReadOnlyList<NetworkInterfaceInfo> GetAvailableNetworkInterfaces()
    {
        lock (_deviceStateLock)
            return (NetworkInterfaceInfo[])_availableNetworkInterfaces.Clone();
    }

    public void ConfigureSelections(
        string? preferredGpuProvider,
        string? preferredGpuDeviceIdentifier,
        NetworkSelectionMode networkSelectionMode,
        string? preferredNetworkInterfaceId)
    {
        MonitoringSelectionOptions next = MonitoringSelectionOptions.Create(
            preferredGpuProvider,
            preferredGpuDeviceIdentifier,
            networkSelectionMode,
            preferredNetworkInterfaceId);
        lock (_selectionCommitLock)
        {
            MonitoringSelectionOptions previous = Volatile.Read(ref _selectionOptions);
            if (previous == next) return;
            Interlocked.Exchange(ref _selectionOptions, next);

            bool gpuChanged = !string.Equals(previous.GpuProvider, next.GpuProvider, StringComparison.Ordinal) ||
                              !string.Equals(previous.GpuDeviceIdentifier, next.GpuDeviceIdentifier, StringComparison.Ordinal);
            bool networkChanged = previous.NetworkMode != next.NetworkMode ||
                                  !string.Equals(previous.NetworkInterfaceId, next.NetworkInterfaceId, StringComparison.Ordinal);

            lock (_maxLock)
            {
                if (gpuChanged)
                {
                    _gpuUsageMax = null;
                    _gpuTemperatureMax = 0;
                    _gpuPowerMax = null;
                    _vramUsedMax = 0;
                }

                if (networkChanged)
                {
                    _uploadMax = 0;
                    _downloadMax = 0;
                }
            }

            if (gpuChanged)
            {
                Volatile.Write(ref _selectedGpuProvider, null);
                Interlocked.Exchange(ref _selectedGpuProviderMissingSamples, 0);
                _gpuPresentationStabilizer.Reset();
            }
            if (networkChanged)
            {
                Volatile.Write(ref _networkInterfaceId, null);
                Volatile.Write(ref _networkInterfaceName, null);
            }

            // A trend must not visually connect measurements from two different devices.
            lock (_historyLock)
                _history.Clear();
        }

        SignalSampler();
    }

    public void RequestDeviceRefresh()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        Interlocked.Exchange(ref _deviceRefreshRequested, 1);
        SignalSampler();
    }

    /// <summary>
    /// Returns an inclusive history range. When the range contains more than
    /// <paramref name="maxPoints"/> samples, points are selected evenly and both
    /// endpoints are retained.
    /// </summary>
    public HardwareSnapshot[] GetHistory(
        DateTime? startInclusive = null,
        DateTime? endInclusive = null,
        int maxPoints = 600)
    {
        HardwareSnapshot[] snapshots = CopyHistory();
        return SnapshotHistory.SelectRange(snapshots, startInclusive, endInclusive, maxPoints);
    }

    /// <summary>
    /// Returns a time window ending at the newest buffered sample.
    /// </summary>
    public HardwareSnapshot[] GetRecentHistory(TimeSpan duration, int maxPoints = 600)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "The history duration must be positive.");

        HardwareSnapshot[] snapshots = CopyHistory();
        if (snapshots.Length == 0)
            return [];

        long end = snapshots[^1].MonotonicTimestamp;
        long durationTicks = ToStopwatchTicks(duration);
        long start = durationTicks >= end ? long.MinValue : end - durationTicks;
        return SnapshotHistory.SelectMonotonicRange(snapshots, start, end, maxPoints);
    }

    /// <summary>
    /// Creates a report suitable for issue submissions. Personal identifiers,
    /// paths, process names, network adapter identifiers, and log contents are
    /// deliberately omitted.
    /// </summary>
    public string CreateDiagnosticReport()
    {
        int historyCount;
        lock (_historyLock)
            historyCount = _history.Count;

        string[] issueKeys;
        lock (_logLock)
            issueKeys = _reportedErrors.ToArray();

        return DiagnosticReportBuilder.Build(
            LatestSnapshot,
            Volatile.Read(ref _samplingIntervalMilliseconds),
            historyCount,
            issueKeys);
    }

    private HardwareMonitorService()
    {
        RefreshNetworkInterfaces();
        TryProbeGpu(force: true);

        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "gxTempMonitor.Sampler",
            Priority = ThreadPriority.BelowNormal
        };
        _pollThread.Start();
    }

    public void Configure(int samplingIntervalSeconds, bool trackTopGpuProcess)
    {
        SetSamplingInterval(samplingIntervalSeconds);
        SetProcessTrackingEnabled(trackTopGpuProcess);
    }

    public void SetSamplingInterval(int seconds)
    {
        int normalized = seconds is 1 or 2 or 5 ? seconds : 1;
        int intervalMilliseconds = normalized * 1000;
        int previous = Interlocked.Exchange(ref _samplingIntervalMilliseconds, intervalMilliseconds);
        if (previous != intervalMilliseconds)
            SignalSampler();
    }

    public void SetProcessTrackingEnabled(bool enabled)
    {
        Volatile.Write(ref _trackTopGpuProcess, enabled ? 1 : 0);
        Volatile.Write(ref _cachedTopGpuProcess, null);
        Interlocked.Exchange(ref _nextProcessRefreshTimestamp, 0);
    }

    public void ResetMaxValues()
    {
        lock (_maxLock)
        {
            _cpuUsageMax = 0;
            _gpuUsageMax = null;
            _gpuTemperatureMax = 0;
            _gpuPowerMax = null;
            _ramUsedMax = 0;
            _vramUsedMax = 0;
            _uploadMax = 0;
            _downloadMax = 0;
        }
    }

    private HardwareSnapshot[] CopyHistory()
    {
        lock (_historyLock)
            return _history.ToArray();
    }

    public string ExportCsv()
    {
        HardwareSnapshot[] snapshots = CopyHistory();

        var builder = new StringBuilder(Math.Max(512, snapshots.Length * 320));
        builder.AppendLine("Timestamp UTC,CPU Usage %,CPU Usage Max %,CPU Name,CPU Architecture,Logical Processors,System Uptime s,GPU Temp °C,GPU Usage %,GPU Usage Max %,GPU Power W,GPU Power Max W,GPU Device,GPU Provider,RAM Used GB,RAM Usage %,VRAM Used GB,VRAM Used Max GB,VRAM Total GB,Battery Present,Battery Charge %,AC Connected,System Drive Total GB,System Drive Available GB,Upload B/s,Download B/s,Sampling Duration ms,Top GPU Process");

        foreach (HardwareSnapshot snapshot in snapshots)
        {
            string[] values =
            [
                snapshot.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                snapshot.HasCpuUsage ? FormatNumber(snapshot.CpuUsage) : string.Empty,
                snapshot.HasCpuUsage ? FormatNumber(snapshot.CpuUsageMax) : string.Empty,
                EscapeSpreadsheetFormula(snapshot.CpuName ?? string.Empty),
                EscapeSpreadsheetFormula(snapshot.CpuArchitecture ?? string.Empty),
                snapshot.LogicalProcessorCount > 0
                    ? snapshot.LogicalProcessorCount.ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                snapshot.SystemUptime > TimeSpan.Zero
                    ? snapshot.SystemUptime.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                    : string.Empty,
                FormatOptionalNumber(snapshot.GpuTemperature),
                snapshot.HasGpuUsage ? FormatNumber(snapshot.GpuUsagePercent) : string.Empty,
                FormatOptionalNumber(snapshot.GpuUsageMaxPercent),
                FormatOptionalNumber(snapshot.GpuPowerWatts),
                FormatOptionalNumber(snapshot.GpuPowerMaxWatts),
                EscapeSpreadsheetFormula(snapshot.GpuDeviceName ?? string.Empty),
                EscapeSpreadsheetFormula(snapshot.GpuProviderName ?? string.Empty),
                snapshot.HasRamData ? FormatNumber(snapshot.RamUsedGb) : string.Empty,
                snapshot.HasRamData ? FormatNumber(snapshot.RamUsagePercent) : string.Empty,
                FormatOptionalNumber(snapshot.VramUsedGb),
                FormatOptionalNumber(snapshot.VramUsedMaxGb),
                FormatOptionalNumber(snapshot.VramTotalGb),
                FormatOptionalBoolean(snapshot.IsBatteryPresent),
                FormatOptionalNumber(snapshot.BatteryChargePercent, "0"),
                FormatOptionalBoolean(snapshot.IsOnAcPower),
                snapshot.HasSystemDriveData ? FormatNumber(snapshot.SystemDriveTotalGb) : string.Empty,
                snapshot.HasSystemDriveData ? FormatNumber(snapshot.SystemDriveAvailableGb) : string.Empty,
                snapshot.HasNetworkData ? FormatNumber(snapshot.NetUploadBytesPerSecond, "0") : string.Empty,
                snapshot.HasNetworkData ? FormatNumber(snapshot.NetDownloadBytesPerSecond, "0") : string.Empty,
                snapshot.SamplingDurationMilliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                EscapeSpreadsheetFormula(snapshot.TopGpuProcess ?? string.Empty)
            ];

            builder.AppendLine(string.Join(',', values.Select(EscapeCsv)));
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;

        DataUpdated = null;
        DevicesRefreshed = null;
        _cancellation.Cancel();
        bool samplerStopped = _pollThread.Join(TimeSpan.FromSeconds(3));

        if (!samplerStopped)
        {
            ReportError("shutdown-sampler-timeout", new TimeoutException("The sampler thread did not stop within three seconds."));
            return;
        }

    }

    private void PollLoop()
    {
        try
        {
            WaitHandle cancellationHandle = _cancellation.Token.WaitHandle;
            WaitHandle[] waitHandles = [cancellationHandle, _pollSignal];
            while (!_cancellation.IsCancellationRequested)
            {
                int interval = Volatile.Read(ref _samplingIntervalMilliseconds);
                int signaled = WaitHandle.WaitAny(waitHandles, interval);
                if (signaled == 0) break;

                try
                {
                    PollMetrics();
                    ClearReportedError("poll-loop");
                }
                catch (Exception ex)
                {
                    ReportError("poll-loop", ex);
                }
            }
        }
        finally
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                foreach (IGpuMonitor monitor in _gpuMonitors)
                    monitor.Dispose();
                _gpuMonitors.Clear();
            }

            _cancellation.Dispose();
            _pollSignal.Dispose();
        }
    }

    private void PollMetrics()
    {
        if (Volatile.Read(ref _disposeState) != 0) return;

        bool devicesRefreshed = ProcessDeviceRefreshRequest();
        MonitoringSelectionOptions selections = Volatile.Read(ref _selectionOptions);

        HardwareSnapshot snapshot = BuildSnapshot(selections);
        bool staleSelection;
        lock (_selectionCommitLock)
        {
            MonitoringSelectionOptions currentSelections = Volatile.Read(ref _selectionOptions);
            staleSelection = !ReferenceEquals(selections, currentSelections);
            if (staleSelection)
            {
                DiscardStaleSelectionSample();
                if (devicesRefreshed)
                    Interlocked.Exchange(ref _deviceRefreshRequested, 1);
            }
            else
            {
                Volatile.Write(ref _latestSnapshot, snapshot);

                lock (_historyLock)
                {
                    while (_history.Count >= MaxHistoryEntries)
                        _history.Dequeue();
                    _history.Enqueue(snapshot);
                }
            }
        }

        if (staleSelection)
        {
            SignalSampler();
            return;
        }

        NotifySubscribers(snapshot);
        if (devicesRefreshed)
            NotifyDeviceRefreshSubscribers();
    }

    private bool ProcessDeviceRefreshRequest()
    {
        if (Interlocked.Exchange(ref _deviceRefreshRequested, 0) == 0)
            return false;

        RefreshNetworkInterfaces();
        RefreshGpuMonitorSessions(_gpuMonitors);
        TryProbeGpu(force: true);
        return true;
    }

    internal static void RefreshGpuMonitorSessions(List<IGpuMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        for (int index = monitors.Count - 1; index >= 0; index--)
        {
            IGpuMonitor monitor = monitors[index];
            monitor.RequestDeviceRefresh();
            if (monitor.TryInitialize() || monitor.Initialized)
                continue;

            monitor.Dispose();
            monitors.RemoveAt(index);
        }
    }

    private void SignalSampler()
    {
        try
        {
            _pollSignal.Set();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeState) != 0)
        {
        }
    }

    private void DiscardStaleSelectionSample()
    {
        lock (_maxLock)
        {
            // A reference mismatch proves that at least one real selection change
            // occurred while this sample was being assembled. Reset both domains:
            // in an A -> B -> A sequence the endpoint values alone cannot reveal
            // which domain changed, and the discarded sample may have updated both.
            _gpuUsageMax = null;
            _gpuTemperatureMax = 0;
            _gpuPowerMax = null;
            _vramUsedMax = 0;
            _uploadMax = 0;
            _downloadMax = 0;
        }

        Volatile.Write(ref _selectedGpuProvider, null);
        Interlocked.Exchange(ref _selectedGpuProviderMissingSamples, 0);
        _gpuPresentationStabilizer.Reset();
        Volatile.Write(ref _networkInterfaceId, null);
        Volatile.Write(ref _networkInterfaceName, null);

        lock (_historyLock)
            _history.Clear();
    }

    private HardwareSnapshot BuildSnapshot(MonitoringSelectionOptions selections)
    {
        long samplingStartedAt = Stopwatch.GetTimestamp();
        DateTimeOffset sampledAtUtc = DateTimeOffset.UtcNow;
        (bool hasCpuUsage, float cpuUsage) = ReadCpuUsage();
        (bool hasRamData, float ramUsedGb, float ramAvailableGb, float ramUsagePercent, float totalRamGb) = ReadRamUsage();
        (bool hasNetworkData, float upload, float download) = ReadNetworkMetrics(selections);
        DynamicSystemMetrics systemMetrics = _systemMetricsReader.Read();
        StaticSystemMetrics staticSystemMetrics = _systemMetricsReader.StaticMetrics;

        TryProbeGpu(force: false);
        (GpuReading gpu, string? gpuProviderName) = ReadGpuMetrics(selections);
        StableGpuPresentation gpuPresentation = _gpuPresentationStabilizer.Update(
            gpuProviderName,
            gpu);

        bool hasGpuUsage = gpu.Usage.HasValue;
        float gpuUsage = Math.Clamp(gpu.Usage ?? 0, 0, 100);
        float cpuUsageMax;
        float? gpuUsageMax;
        float gpuTemperatureMax;
        float? gpuPowerMax;
        float ramUsedMax;
        float vramUsedMax;
        float uploadMax;
        float downloadMax;
        lock (_maxLock)
        {
            if (hasCpuUsage)
                UpdateMax(ref _cpuUsageMax, cpuUsage);
            UpdateMax(ref _gpuUsageMax, hasGpuUsage ? gpuUsage : null);
            UpdateMax(ref _gpuTemperatureMax, gpu.Temperature);
            UpdateMax(ref _gpuPowerMax, gpu.PowerWatts);
            if (hasRamData)
                UpdateMax(ref _ramUsedMax, ramUsedGb);
            UpdateMax(ref _vramUsedMax, gpu.VramUsedGb);
            if (hasNetworkData)
            {
                UpdateMax(ref _uploadMax, upload);
                UpdateMax(ref _downloadMax, download);
            }

            cpuUsageMax = _cpuUsageMax;
            gpuUsageMax = _gpuUsageMax;
            gpuTemperatureMax = _gpuTemperatureMax;
            gpuPowerMax = _gpuPowerMax;
            ramUsedMax = _ramUsedMax;
            vramUsedMax = _vramUsedMax;
            uploadMax = _uploadMax;
            downloadMax = _downloadMax;
        }

        UpdateTopGpuProcessIfNeeded();
        double samplingDurationMilliseconds = Stopwatch.GetElapsedTime(samplingStartedAt).TotalMilliseconds;

        return new HardwareSnapshot
        {
            Timestamp = sampledAtUtc.LocalDateTime,
            TimestampUtc = sampledAtUtc,
            MonotonicTimestamp = samplingStartedAt,
            CpuName = staticSystemMetrics.CpuName,
            LogicalProcessorCount = staticSystemMetrics.LogicalProcessorCount,
            CpuArchitecture = staticSystemMetrics.CpuArchitecture,
            SystemUptime = systemMetrics.SystemUptime,
            CpuUsage = cpuUsage,
            HasCpuUsage = hasCpuUsage,
            CpuUsageMax = cpuUsageMax,
            GpuUsagePercent = gpuUsage,
            HasGpuUsage = hasGpuUsage,
            GpuUsageMaxPercent = gpuUsageMax,
            GpuTemperature = gpu.Temperature,
            GpuTemperatureMax = gpuTemperatureMax > 0 ? gpuTemperatureMax : null,
            GpuPowerWatts = gpu.PowerWatts,
            GpuPowerMaxWatts = gpuPowerMax,
            GpuDeviceName = gpu.DeviceName,
            GpuProviderName = gpuProviderName,
            GpuCapabilities = gpuPresentation.Capabilities,
            GpuPrimaryMetric = gpuPresentation.PrimaryMetric,
            RamUsedGb = ramUsedGb,
            HasRamData = hasRamData,
            RamAvailableGb = ramAvailableGb,
            RamUsedMaxGb = ramUsedMax,
            RamUsagePercent = ramUsagePercent,
            TotalRamGb = totalRamGb,
            VramUsedGb = gpu.VramUsedGb,
            VramUsedMaxGb = vramUsedMax > 0 ? vramUsedMax : null,
            VramTotalGb = gpu.VramTotalGb,
            IsBatteryPresent = systemMetrics.IsBatteryPresent,
            BatteryChargePercent = systemMetrics.BatteryChargePercent,
            IsOnAcPower = systemMetrics.IsOnAcPower,
            HasSystemDriveData = systemMetrics.HasSystemDriveData,
            SystemDriveTotalGb = systemMetrics.SystemDriveTotalGb,
            SystemDriveAvailableGb = systemMetrics.SystemDriveAvailableGb,
            NetworkInterfaceName = Volatile.Read(ref _networkInterfaceName),
            NetTotalBytesPerSecond = upload + download,
            HasNetworkData = hasNetworkData,
            NetUploadBytesPerSecond = upload,
            NetUploadMaxBytesPerSecond = uploadMax,
            NetDownloadBytesPerSecond = download,
            NetDownloadMaxBytesPerSecond = downloadMax,
            TopGpuProcess = Volatile.Read(ref _trackTopGpuProcess) != 0
                ? Volatile.Read(ref _cachedTopGpuProcess)
                : null,
            SamplingDurationMilliseconds = samplingDurationMilliseconds
        };
    }

    private (bool Available, float Usage) ReadCpuUsage()
    {
        try
        {
            if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
                throw new InvalidOperationException($"GetSystemTimes failed with Win32 error {Marshal.GetLastPInvokeError()}.");

            ulong idleValue = idle.ToUInt64();
            ulong kernelValue = kernel.ToUInt64();
            ulong userValue = user.ToUInt64();

            bool hasMeasurement = _hasCpuBaseline;
            if (_hasCpuBaseline &&
                idleValue >= _lastIdleTime &&
                kernelValue >= _lastKernelTime &&
                userValue >= _lastUserTime)
            {
                ulong idleDelta = idleValue - _lastIdleTime;
                ulong kernelDelta = kernelValue - _lastKernelTime;
                ulong userDelta = userValue - _lastUserTime;
                ulong totalDelta = kernelDelta + userDelta;

                if (totalDelta > 0 && idleDelta <= totalDelta)
                    _lastCpuUsage = Math.Clamp((float)((totalDelta - idleDelta) * 100d / totalDelta), 0, 100);
            }
            else if (_hasCpuBaseline)
            {
                hasMeasurement = false;
            }

            _lastIdleTime = idleValue;
            _lastKernelTime = kernelValue;
            _lastUserTime = userValue;
            _hasCpuBaseline = true;
            ClearReportedError("metric-cpu");
            return (hasMeasurement, _lastCpuUsage);
        }
        catch (Exception ex)
        {
            ReportError("metric-cpu", ex);
            _hasCpuBaseline = false;
            return (false, _lastCpuUsage);
        }
    }

    private (bool Available, float UsedGb, float AvailableGb, float UsagePercent, float TotalGb) ReadRamUsage()
    {
        try
        {
            var status = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(status))
                throw new InvalidOperationException($"GlobalMemoryStatusEx failed with Win32 error {Marshal.GetLastPInvokeError()}.");

            const double bytesPerGb = 1024d * 1024d * 1024d;
            double total = status.TotalPhysical / bytesPerGb;
            double available = status.AvailablePhysical / bytesPerGb;
            double used = Math.Max(0, total - available);
            ClearReportedError("metric-ram");
            return (true, (float)used, (float)available, status.MemoryLoad, (float)total);
        }
        catch (Exception ex)
        {
            ReportError("metric-ram", ex);
            return (false, 0, 0, 0, 0);
        }
    }

    private (bool Available, float Upload, float Download) ReadNetworkMetrics(
        MonitoringSelectionOptions selections)
    {
        long now = Stopwatch.GetTimestamp();
        if (now >= _nextNetworkRefreshTimestamp)
            RefreshNetworkInterfaces();

        var rates = new List<NetworkRate>(_networkInterfaces.Length);
        var activeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (NetworkInterface networkInterface in _networkInterfaces)
        {
            string id = GetNetworkInterfaceId(networkInterface);
            activeIds.Add(id);

            try
            {
                IPv4InterfaceStatistics statistics = networkInterface.GetIPv4Statistics();
                var current = new NetworkBaseline(statistics.BytesReceived, statistics.BytesSent, now);

                float upload = 0;
                float download = 0;
                bool hasMeasurement = false;
                if (_networkBaselines.TryGetValue(id, out NetworkBaseline previous))
                {
                    double elapsedSeconds = (now - previous.Timestamp) / (double)Stopwatch.Frequency;
                    long receivedDelta = current.Received - previous.Received;
                    long sentDelta = current.Sent - previous.Sent;
                    if (elapsedSeconds is >= 0.1 and <= 15 && receivedDelta >= 0 && sentDelta >= 0)
                    {
                        download = (float)(receivedDelta / elapsedSeconds);
                        upload = (float)(sentDelta / elapsedSeconds);
                        hasMeasurement = true;
                    }
                }

                _networkBaselines[id] = current;
                rates.Add(new NetworkRate(id, networkInterface.Name, hasMeasurement, upload, download));
                ClearReportedError($"network-interface:{id}");
            }
            catch (Exception ex) when (ex is NetworkInformationException or
                                       InvalidOperationException or
                                       PlatformNotSupportedException)
            {
                _networkBaselines.Remove(id);
                ReportError($"network-interface:{id}", ex);
            }
        }

        foreach (string staleId in _networkBaselines.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            _networkBaselines.Remove(staleId);

        if (rates.Count == 0)
        {
            Volatile.Write(ref _networkInterfaceId, null);
            Volatile.Write(ref _networkInterfaceName, null);
            return (false, 0, 0);
        }

        NetworkSelectionMode selectionMode = selections.NetworkMode;
        if (selectionMode == NetworkSelectionMode.Aggregate)
        {
            NetworkRate[] measuredRates = rates.Where(rate => rate.HasMeasurement).ToArray();
            Volatile.Write(ref _networkInterfaceId, null);
            Volatile.Write(
                ref _networkInterfaceName,
                measuredRates.Length > 0
                    ? $"全部网卡 ({measuredRates.Length})"
                    : "全部网卡");
            if (measuredRates.Length == 0)
                return (false, 0, 0);

            double totalUpload = measuredRates.Sum(rate => (double)rate.Upload);
            double totalDownload = measuredRates.Sum(rate => (double)rate.Download);
            ClearReportedError("metric-network");
            return (
                true,
                (float)Math.Min(float.MaxValue, totalUpload),
                (float)Math.Min(float.MaxValue, totalDownload));
        }

        NetworkRate? fixedSelection = null;
        if (selectionMode == NetworkSelectionMode.Fixed)
        {
            fixedSelection = rates
                .Cast<NetworkRate?>()
                .FirstOrDefault(rate => string.Equals(
                    rate?.Id,
                    selections.NetworkInterfaceId,
                    StringComparison.Ordinal));
        }

        NetworkRate selected = fixedSelection ?? rates
            .OrderByDescending(rate => rate.Upload + rate.Download)
            .First();

        if (!fixedSelection.HasValue &&
            selected.Upload + selected.Download <= 0 && Volatile.Read(ref _networkInterfaceId) != null)
        {
            string? previousInterfaceId = Volatile.Read(ref _networkInterfaceId);
            NetworkRate? previousSelection = rates
                .Cast<NetworkRate?>()
                .FirstOrDefault(rate => string.Equals(rate?.Id, previousInterfaceId, StringComparison.Ordinal));
            if (previousSelection.HasValue)
                selected = previousSelection.Value;
        }

        Volatile.Write(ref _networkInterfaceId, selected.Id);
        Volatile.Write(ref _networkInterfaceName, selected.Name);
        ClearReportedError("metric-network");
        return (selected.HasMeasurement, selected.Upload, selected.Download);
    }

    private void RefreshNetworkInterfaces()
    {
        try
        {
            _networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsUsableNetworkInterface)
                .ToArray();
            var options = new NetworkInterfaceInfo[_networkInterfaces.Length];
            for (int index = 0; index < _networkInterfaces.Length; index++)
            {
                NetworkInterface networkInterface = _networkInterfaces[index];
                options[index] = new NetworkInterfaceInfo(
                    GetNetworkInterfaceId(networkInterface),
                    $"{networkInterface.Name} · {networkInterface.NetworkInterfaceType}");
            }
            lock (_deviceStateLock)
                _availableNetworkInterfaces = options;
            _nextNetworkRefreshTimestamp = Stopwatch.GetTimestamp() + SecondsToStopwatchTicks(NetworkRefreshSeconds);
            ClearReportedError("network-refresh");
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            _networkInterfaces = [];
            lock (_deviceStateLock)
                _availableNetworkInterfaces = [];
            _nextNetworkRefreshTimestamp = Stopwatch.GetTimestamp() + SecondsToStopwatchTicks(NetworkRefreshSeconds);
            ReportError("network-refresh", ex);
        }
    }

    private static bool IsUsableNetworkInterface(NetworkInterface networkInterface) =>
        networkInterface.OperationalStatus == OperationalStatus.Up &&
        networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel &&
        !networkInterface.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

    private static string GetNetworkInterfaceId(NetworkInterface networkInterface) =>
        string.IsNullOrWhiteSpace(networkInterface.Id)
            ? $"{networkInterface.NetworkInterfaceType}:{networkInterface.Name}"
            : networkInterface.Id;

    private void TryProbeGpu(bool force)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && now < _nextGpuProbeTimestamp) return;

        if (NvidiaGpuMonitor.IsNvmlAvailable)
            TryAddGpuMonitor(() => new NvidiaGpuMonitor());
        if (AmdGpuMonitor.IsAdlAvailable)
            TryAddGpuMonitor(() => new AmdGpuMonitor());
        TryAddGpuMonitor(() => new WindowsGpuCounterMonitor());
        _nextGpuProbeTimestamp = now + SecondsToStopwatchTicks(GpuRetrySeconds);
    }

    private void TryAddGpuMonitor<TMonitor>(Func<TMonitor> factory)
        where TMonitor : class, IGpuMonitor
    {
        if (_gpuMonitors.Any(monitor => monitor is TMonitor)) return;

        TMonitor? monitor = null;
        try
        {
            monitor = factory();
            if (monitor.TryInitialize())
            {
                _gpuMonitors.Add(monitor);
                ClearReportedError($"gpu-probe:{typeof(TMonitor).Name}");
                monitor = null;
            }
        }
        catch (Exception ex)
        {
            ReportError($"gpu-probe:{typeof(TMonitor).Name}", ex);
        }
        finally
        {
            monitor?.Dispose();
        }
    }

    private (GpuReading Reading, string? ProviderName) ReadGpuMetrics(
        MonitoringSelectionOptions selections)
    {
        string? preferredProvider = selections.GpuProvider;
        string? preferredDeviceIdentifier = selections.GpuDeviceIdentifier;
        var candidates = new List<GpuCandidate>(_gpuMonitors.Count);
        for (int index = _gpuMonitors.Count - 1; index >= 0; index--)
        {
            IGpuMonitor monitor = _gpuMonitors[index];
            if (!monitor.Initialized)
            {
                monitor.Dispose();
                _gpuMonitors.RemoveAt(index);
                continue;
            }

            bool isPreferredProvider = string.Equals(
                monitor.VendorName,
                preferredProvider,
                StringComparison.Ordinal);
            monitor.SetPreferredDevice(isPreferredProvider ? preferredDeviceIdentifier : null);

            GpuReading reading = monitor.Read();
            if (!monitor.Initialized)
            {
                monitor.Dispose();
                _gpuMonitors.RemoveAt(index);
                continue;
            }

            if (HasAnyGpuMetric(reading))
                candidates.Add(new GpuCandidate(monitor, reading));
        }

        var availableDevices = new List<GpuDeviceInfo>();
        foreach (IGpuMonitor monitor in _gpuMonitors)
        {
            foreach (GpuDeviceInfo device in monitor.AvailableDevices)
            {
                if (!availableDevices.Any(existing =>
                        string.Equals(existing.ProviderName, device.ProviderName, StringComparison.Ordinal) &&
                        string.Equals(existing.DeviceIdentifier, device.DeviceIdentifier, StringComparison.Ordinal)))
                {
                    availableDevices.Add(device);
                }
            }
        }
        lock (_deviceStateLock)
            _availableGpuDevices = availableDevices.ToArray();

        if (candidates.Count == 0)
        {
            string? currentProvider = Volatile.Read(ref _selectedGpuProvider);
            if (ShouldHoldMissingGpuProvider(currentProvider))
                return (GpuReading.Empty, currentProvider);

            Volatile.Write(ref _selectedGpuProvider, null);
            Interlocked.Exchange(ref _selectedGpuProviderMissingSamples, 0);
            return (GpuReading.Empty, null);
        }

        GpuCandidate? preferred = candidates
            .Cast<GpuCandidate?>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate?.Monitor.VendorName, preferredProvider, StringComparison.Ordinal) &&
                string.Equals(
                    candidate?.Reading.DeviceIdentifier,
                    preferredDeviceIdentifier,
                    StringComparison.Ordinal));
        if (preferred.HasValue)
        {
            Volatile.Write(ref _selectedGpuProvider, preferred.Value.Monitor.VendorName);
            Interlocked.Exchange(ref _selectedGpuProviderMissingSamples, 0);
            return (preferred.Value.Reading, preferred.Value.Monitor.VendorName);
        }

        GpuCandidate best = candidates[0];
        for (int index = 1; index < candidates.Count; index++)
        {
            if (IsBetterGpuCandidate(candidates[index], best))
                best = candidates[index];
        }

        string? currentProviderName = Volatile.Read(ref _selectedGpuProvider);
        GpuCandidate? current = candidates
            .Cast<GpuCandidate?>()
            .FirstOrDefault(candidate => string.Equals(
                candidate?.Monitor.VendorName,
                currentProviderName,
                StringComparison.Ordinal));

        if (!current.HasValue && ShouldHoldMissingGpuProvider(currentProviderName))
            return (GpuReading.Empty, currentProviderName);

        Interlocked.Exchange(ref _selectedGpuProviderMissingSamples, 0);

        GpuCandidate selected = best;
        if (current.HasValue && !ReferenceEquals(current.Value.Monitor, best.Monitor))
        {
            float? currentUsage = current.Value.Reading.Usage;
            float? bestUsage = best.Reading.Usage;
            bool currentIsWindows = current.Value.Monitor is WindowsGpuCounterMonitor;
            bool bestIsWindows = best.Monitor is WindowsGpuCounterMonitor;

            bool keepCurrent;
            if (currentIsWindows && !bestIsWindows)
            {
                keepCurrent = currentUsage.HasValue && bestUsage.HasValue &&
                              currentUsage.Value > bestUsage.Value + 10f;
            }
            else
            {
                float switchThreshold = bestIsWindows && !currentIsWindows ? 10f : 5f;
                keepCurrent = (currentUsage.HasValue && !bestUsage.HasValue) ||
                              (currentUsage.HasValue && bestUsage.HasValue &&
                               bestUsage.Value <= currentUsage.Value + switchThreshold) ||
                              (!currentUsage.HasValue && !bestUsage.HasValue);
            }

            if (keepCurrent)
            {
                selected = current.Value;
            }
        }

        Volatile.Write(ref _selectedGpuProvider, selected.Monitor.VendorName);
        return (selected.Reading, selected.Monitor.VendorName);
    }

    private bool ShouldHoldMissingGpuProvider(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return false;

        int missingSamples = Interlocked.Increment(ref _selectedGpuProviderMissingSamples);
        return missingSamples < GpuProviderMissingSamplesBeforeFallback;
    }

    private static bool HasAnyGpuMetric(GpuReading reading) =>
        reading.Temperature.HasValue || reading.Usage.HasValue || reading.VramUsedGb.HasValue ||
        reading.VramTotalGb.HasValue || reading.PowerWatts.HasValue;

    private static bool IsBetterGpuCandidate(GpuCandidate candidate, GpuCandidate current)
    {
        float candidateUsage = candidate.Reading.Usage ?? -1;
        float currentUsage = current.Reading.Usage ?? -1;
        bool candidateNative = candidate.Monitor is not WindowsGpuCounterMonitor;
        bool currentNative = current.Monitor is not WindowsGpuCounterMonitor;
        if (candidateNative != currentNative)
        {
            float nativeUsage = candidateNative ? candidateUsage : currentUsage;
            float windowsUsage = candidateNative ? currentUsage : candidateUsage;
            return windowsUsage > nativeUsage + 10f ? !candidateNative : candidateNative;
        }

        if (Math.Abs(candidateUsage - currentUsage) > 0.01f)
            return candidateUsage > currentUsage;

        int candidateMetrics = CountGpuMetrics(candidate.Reading);
        int currentMetrics = CountGpuMetrics(current.Reading);
        return candidateMetrics > currentMetrics;
    }

    private static int CountGpuMetrics(GpuReading reading) =>
        (reading.Temperature.HasValue ? 1 : 0) +
        (reading.Usage.HasValue ? 1 : 0) +
        (reading.VramUsedGb.HasValue ? 1 : 0) +
        (reading.VramTotalGb.HasValue ? 1 : 0) +
        (reading.PowerWatts.HasValue ? 1 : 0);

    private void UpdateTopGpuProcessIfNeeded()
    {
        if (Volatile.Read(ref _trackTopGpuProcess) == 0)
        {
            Volatile.Write(ref _cachedTopGpuProcess, null);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (now < Volatile.Read(ref _nextProcessRefreshTimestamp)) return;

        Volatile.Write(
            ref _nextProcessRefreshTimestamp,
            now + SecondsToStopwatchTicks(ProcessRefreshSeconds));
        Volatile.Write(ref _cachedTopGpuProcess, ReadTopGpuProcess());
    }

    private string? ReadTopGpuProcess()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Process Memory");
            string[] instances = category.GetInstanceNames();
            var processVram = new Dictionary<int, long>();

            foreach (string instance in instances)
            {
                try
                {
                    if (!TryParseProcessId(instance, out int processId)) continue;

                    using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, readOnly: true);
                    long bytes = Math.Max(0, counter.RawValue);
                    if (bytes == 0) continue;

                    processVram.TryGetValue(processId, out long accumulated);
                    processVram[processId] = accumulated > long.MaxValue - bytes
                        ? long.MaxValue
                        : accumulated + bytes;
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (processVram.Count == 0) return null;

            KeyValuePair<int, long> top = processVram.MaxBy(pair => pair.Value);
            string name = GetProcessName(top.Key);
            double megabytes = top.Value / (1024d * 1024d);
            ClearReportedError("metric-gpu-process");
            return $"{name} ({megabytes:0.0}MB)";
        }
        catch (Exception ex)
        {
            ReportError("metric-gpu-process", ex);
            return null;
        }
    }

    internal static bool TryParseProcessId(string instanceName, out int processId)
    {
        processId = 0;
        if (!instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return false;

        int separator = instanceName.IndexOf('_', 4);
        ReadOnlySpan<char> value = separator > 4
            ? instanceName.AsSpan(4, separator - 4)
            : instanceName.AsSpan(4);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out processId) && processId > 0;
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return $"pid_{processId}";
        }
        catch (InvalidOperationException)
        {
            return $"pid_{processId}";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return $"pid_{processId}";
        }
    }

    private void NotifySubscribers(HardwareSnapshot snapshot)
    {
        Action<HardwareSnapshot>? handlers = DataUpdated;
        if (handlers == null) return;

        foreach (Action<HardwareSnapshot> handler in handlers.GetInvocationList().Cast<Action<HardwareSnapshot>>())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception ex)
            {
                string subscriber = handler.Method.DeclaringType?.FullName ?? "unknown";
                ReportError($"subscriber:{subscriber}", ex);
            }
        }
    }

    private void NotifyDeviceRefreshSubscribers()
    {
        Action? handlers = DevicesRefreshed;
        if (handlers == null) return;

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                string subscriber = handler.Method.DeclaringType?.FullName ?? "unknown";
                ReportError($"device-refresh-subscriber:{subscriber}", ex);
            }
        }
    }

    private void ReportError(string key, Exception exception)
    {
        lock (_logLock)
        {
            if (!_reportedErrors.Add(key)) return;

            try
            {
                AppPaths.EnsureDataDirectory();
                RotateLogIfNeeded();
                File.AppendAllText(
                    AppPaths.LogPath,
                    $"[{DateTimeOffset.Now:O}] {key}: {exception}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
    }

    private void ClearReportedError(string key)
    {
        lock (_logLock)
            _reportedErrors.Remove(key);
    }

    private static void RotateLogIfNeeded()
    {
        if (!File.Exists(AppPaths.LogPath) || new FileInfo(AppPaths.LogPath).Length <= MaxLogBytes) return;

        string backupPath = AppPaths.LogPath + ".1";
        File.Move(AppPaths.LogPath, backupPath, overwrite: true);
    }

    private static long SecondsToStopwatchTicks(int seconds) => checked((long)seconds * Stopwatch.Frequency);

    private static long ToStopwatchTicks(TimeSpan duration)
    {
        double stopwatchTicks = duration.TotalSeconds * Stopwatch.Frequency;
        if (!double.IsFinite(stopwatchTicks) || stopwatchTicks >= long.MaxValue)
            return long.MaxValue;

        return Math.Max(1, (long)Math.Ceiling(stopwatchTicks));
    }

    private static void UpdateMax(ref float maximum, float current)
    {
        if (float.IsFinite(current) && current > maximum)
            maximum = current;
    }

    private static void UpdateMax(ref float maximum, float? current)
    {
        if (current.HasValue)
            UpdateMax(ref maximum, current.Value);
    }

    private static void UpdateMax(ref float? maximum, float? current)
    {
        if (current.HasValue && float.IsFinite(current.Value) &&
            (!maximum.HasValue || current.Value > maximum.Value))
        {
            maximum = current.Value;
        }
    }

    private static string FormatNumber(float value, string format = "0.0") =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string FormatOptionalNumber(float? value, string format = "0.0") =>
        value.HasValue ? FormatNumber(value.Value, format) : string.Empty;

    private static string FormatOptionalBoolean(bool? value) => value switch
    {
        true => "true",
        false => "false",
        _ => string.Empty
    };

    internal static string EscapeSpreadsheetFormula(string value)
    {
        ReadOnlySpan<char> trimmed = value.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value;
    }

    internal static string EscapeCsv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return value;

        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
