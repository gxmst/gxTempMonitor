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
    public float CpuUsage { get; init; }
    public bool HasCpuUsage { get; init; }
    public float CpuUsageMax { get; init; }
    public float GpuUsagePercent { get; init; }
    public bool HasGpuUsage { get; init; }
    public float? GpuTemperature { get; init; }
    public float? GpuTemperatureMax { get; init; }
    public float? GpuPowerWatts { get; init; }
    public string? GpuDeviceName { get; init; }
    public string? GpuProviderName { get; init; }
    public float RamUsedGb { get; init; }
    public bool HasRamData { get; init; }
    public float RamAvailableGb { get; init; }
    public float RamUsedMaxGb { get; init; }
    public float RamUsagePercent { get; init; }
    public float TotalRamGb { get; init; }
    public float? VramUsedGb { get; init; }
    public float? VramUsedMaxGb { get; init; }
    public string? NetworkInterfaceName { get; init; }
    public float NetTotalBytesPerSecond { get; init; }
    public bool HasNetworkData { get; init; }
    public float NetUploadBytesPerSecond { get; init; }
    public float NetUploadMaxBytesPerSecond { get; init; }
    public float NetDownloadBytesPerSecond { get; init; }
    public float NetDownloadMaxBytesPerSecond { get; init; }
    public string? TopGpuProcess { get; init; }
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

    private readonly object _maxLock = new();
    private readonly object _historyLock = new();
    private readonly object _logLock = new();
    private readonly Queue<HardwareSnapshot> _history = new(MaxHistoryEntries);
    private readonly Dictionary<string, NetworkBaseline> _networkBaselines = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedErrors = new(StringComparer.Ordinal);
    private readonly List<IGpuMonitor> _gpuMonitors = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _pollThread;

    private NetworkInterface[] _networkInterfaces = [];
    private HardwareSnapshot _latestSnapshot = new();
    private string? _networkInterfaceId;
    private string? _networkInterfaceName;
    private string? _cachedTopGpuProcess;
    private string? _selectedGpuProvider;

    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;
    private float _lastCpuUsage;
    private bool _hasCpuBaseline;
    private long _nextNetworkRefreshTimestamp;
    private long _nextGpuProbeTimestamp;
    private long _nextProcessRefreshTimestamp;

    private float _cpuUsageMax;
    private float _gpuTemperatureMax;
    private float _ramUsedMax;
    private float _vramUsedMax;
    private float _uploadMax;
    private float _downloadMax;

    private int _samplingIntervalMilliseconds = 1000;
    private int _trackTopGpuProcess;
    private int _disposeState;

    public static HardwareMonitorService Instance => LazyInstance.Value;
    public static bool IsValueCreated => LazyInstance.IsValueCreated;

    public event Action<HardwareSnapshot>? DataUpdated;

    public HardwareSnapshot LatestSnapshot => Volatile.Read(ref _latestSnapshot);

    private HardwareMonitorService()
    {
        AppPaths.EnsureDataDirectory();

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
        Volatile.Write(ref _samplingIntervalMilliseconds, normalized * 1000);
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
            _gpuTemperatureMax = 0;
            _ramUsedMax = 0;
            _vramUsedMax = 0;
            _uploadMax = 0;
            _downloadMax = 0;
        }
    }

    public string ExportCsv()
    {
        HardwareSnapshot[] snapshots;
        lock (_historyLock)
            snapshots = _history.ToArray();

        var builder = new StringBuilder(Math.Max(256, snapshots.Length * 160));
        builder.AppendLine("Timestamp,CPU Usage %,GPU Temp °C,GPU Usage %,GPU Device,GPU Provider,RAM Used GB,RAM Usage %,VRAM Used GB,Upload B/s,Download B/s,Top GPU Process");

        foreach (HardwareSnapshot snapshot in snapshots)
        {
            string[] values =
            [
                snapshot.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                snapshot.HasCpuUsage ? FormatNumber(snapshot.CpuUsage) : string.Empty,
                FormatOptionalNumber(snapshot.GpuTemperature),
                snapshot.HasGpuUsage ? FormatNumber(snapshot.GpuUsagePercent) : string.Empty,
                EscapeSpreadsheetFormula(snapshot.GpuDeviceName ?? string.Empty),
                EscapeSpreadsheetFormula(snapshot.GpuProviderName ?? string.Empty),
                snapshot.HasRamData ? FormatNumber(snapshot.RamUsedGb) : string.Empty,
                snapshot.HasRamData ? FormatNumber(snapshot.RamUsagePercent) : string.Empty,
                FormatOptionalNumber(snapshot.VramUsedGb),
                snapshot.HasNetworkData ? FormatNumber(snapshot.NetUploadBytesPerSecond, "0") : string.Empty,
                snapshot.HasNetworkData ? FormatNumber(snapshot.NetDownloadBytesPerSecond, "0") : string.Empty,
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
            while (!_cancellation.IsCancellationRequested)
            {
                int interval = Volatile.Read(ref _samplingIntervalMilliseconds);
                if (cancellationHandle.WaitOne(interval)) break;

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
        }
    }

    private void PollMetrics()
    {
        if (Volatile.Read(ref _disposeState) != 0) return;

        HardwareSnapshot snapshot = BuildSnapshot();

        Volatile.Write(ref _latestSnapshot, snapshot);

        lock (_historyLock)
        {
            while (_history.Count >= MaxHistoryEntries)
                _history.Dequeue();
            _history.Enqueue(snapshot);
        }

        NotifySubscribers(snapshot);
    }

    private HardwareSnapshot BuildSnapshot()
    {
        (bool hasCpuUsage, float cpuUsage) = ReadCpuUsage();
        (bool hasRamData, float ramUsedGb, float ramAvailableGb, float ramUsagePercent, float totalRamGb) = ReadRamUsage();
        (bool hasNetworkData, float upload, float download) = ReadNetworkMetrics();

        TryProbeGpu(force: false);
        (GpuReading gpu, string? gpuProviderName) = ReadGpuMetrics();

        bool hasGpuUsage = gpu.Usage.HasValue;
        float gpuUsage = Math.Clamp(gpu.Usage ?? 0, 0, 100);
        float cpuUsageMax;
        float gpuTemperatureMax;
        float ramUsedMax;
        float vramUsedMax;
        float uploadMax;
        float downloadMax;
        lock (_maxLock)
        {
            if (hasCpuUsage)
                UpdateMax(ref _cpuUsageMax, cpuUsage);
            UpdateMax(ref _gpuTemperatureMax, gpu.Temperature);
            if (hasRamData)
                UpdateMax(ref _ramUsedMax, ramUsedGb);
            UpdateMax(ref _vramUsedMax, gpu.VramUsedGb);
            if (hasNetworkData)
            {
                UpdateMax(ref _uploadMax, upload);
                UpdateMax(ref _downloadMax, download);
            }

            cpuUsageMax = _cpuUsageMax;
            gpuTemperatureMax = _gpuTemperatureMax;
            ramUsedMax = _ramUsedMax;
            vramUsedMax = _vramUsedMax;
            uploadMax = _uploadMax;
            downloadMax = _downloadMax;
        }

        UpdateTopGpuProcessIfNeeded();

        return new HardwareSnapshot
        {
            Timestamp = DateTime.Now,
            CpuUsage = cpuUsage,
            HasCpuUsage = hasCpuUsage,
            CpuUsageMax = cpuUsageMax,
            GpuUsagePercent = gpuUsage,
            HasGpuUsage = hasGpuUsage,
            GpuTemperature = gpu.Temperature,
            GpuTemperatureMax = gpuTemperatureMax > 0 ? gpuTemperatureMax : null,
            GpuPowerWatts = gpu.PowerWatts,
            GpuDeviceName = gpu.DeviceName,
            GpuProviderName = gpuProviderName,
            RamUsedGb = ramUsedGb,
            HasRamData = hasRamData,
            RamAvailableGb = ramAvailableGb,
            RamUsedMaxGb = ramUsedMax,
            RamUsagePercent = ramUsagePercent,
            TotalRamGb = totalRamGb,
            VramUsedGb = gpu.VramUsedGb,
            VramUsedMaxGb = vramUsedMax > 0 ? vramUsedMax : null,
            NetworkInterfaceName = _networkInterfaceName,
            NetTotalBytesPerSecond = upload + download,
            HasNetworkData = hasNetworkData,
            NetUploadBytesPerSecond = upload,
            NetUploadMaxBytesPerSecond = uploadMax,
            NetDownloadBytesPerSecond = download,
            NetDownloadMaxBytesPerSecond = downloadMax,
            TopGpuProcess = Volatile.Read(ref _trackTopGpuProcess) != 0 ? _cachedTopGpuProcess : null
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

    private (bool Available, float Upload, float Download) ReadNetworkMetrics()
    {
        long now = Stopwatch.GetTimestamp();
        if (now >= _nextNetworkRefreshTimestamp)
            RefreshNetworkInterfaces();

        var rates = new List<NetworkRate>(_networkInterfaces.Length);
        var activeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (NetworkInterface networkInterface in _networkInterfaces)
        {
            string id = string.IsNullOrWhiteSpace(networkInterface.Id)
                ? $"{networkInterface.NetworkInterfaceType}:{networkInterface.Name}"
                : networkInterface.Id;
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
            _networkInterfaceId = null;
            _networkInterfaceName = null;
            return (false, 0, 0);
        }

        NetworkRate selected = rates
            .OrderByDescending(rate => rate.Upload + rate.Download)
            .First();

        if (selected.Upload + selected.Download <= 0 && _networkInterfaceId != null)
        {
            NetworkRate? previousSelection = rates
                .Cast<NetworkRate?>()
                .FirstOrDefault(rate => string.Equals(rate?.Id, _networkInterfaceId, StringComparison.Ordinal));
            if (previousSelection.HasValue)
                selected = previousSelection.Value;
        }

        _networkInterfaceId = selected.Id;
        _networkInterfaceName = selected.Name;
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
            _nextNetworkRefreshTimestamp = Stopwatch.GetTimestamp() + SecondsToStopwatchTicks(NetworkRefreshSeconds);
            ClearReportedError("network-refresh");
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            _networkInterfaces = [];
            _nextNetworkRefreshTimestamp = Stopwatch.GetTimestamp() + SecondsToStopwatchTicks(NetworkRefreshSeconds);
            ReportError("network-refresh", ex);
        }
    }

    private static bool IsUsableNetworkInterface(NetworkInterface networkInterface) =>
        networkInterface.OperationalStatus == OperationalStatus.Up &&
        networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel &&
        !networkInterface.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

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

    private (GpuReading Reading, string? ProviderName) ReadGpuMetrics()
    {
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

        if (candidates.Count == 0)
        {
            _selectedGpuProvider = null;
            return (GpuReading.Empty, null);
        }

        GpuCandidate best = candidates[0];
        for (int index = 1; index < candidates.Count; index++)
        {
            if (IsBetterGpuCandidate(candidates[index], best))
                best = candidates[index];
        }

        GpuCandidate? current = candidates
            .Cast<GpuCandidate?>()
            .FirstOrDefault(candidate => string.Equals(
                candidate?.Monitor.VendorName,
                _selectedGpuProvider,
                StringComparison.Ordinal));

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

        _selectedGpuProvider = selected.Monitor.VendorName;
        return (selected.Reading, selected.Monitor.VendorName);
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
            _cachedTopGpuProcess = null;
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (now < _nextProcessRefreshTimestamp) return;

        _nextProcessRefreshTimestamp = now + SecondsToStopwatchTicks(ProcessRefreshSeconds);
        _cachedTopGpuProcess = ReadTopGpuProcess();
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

    private static string FormatNumber(float value, string format = "0.0") =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string FormatOptionalNumber(float? value, string format = "0.0") =>
        value.HasValue ? FormatNumber(value.Value, format) : string.Empty;

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
