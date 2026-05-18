using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TempMonitor;

public record GpuProcessInfo(string ProcessName, long DedicatedBytes, long SharedBytes);

public sealed class HardwareSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public float CpuUsage { get; init; }
    public float CpuUsageMax { get; init; }
    public float GpuUsagePercent { get; init; }
    public float? GpuTemperature { get; init; }
    public float? GpuTemperatureMax { get; init; }
    public float? GpuPowerWatts { get; init; }
    public float RamUsedGb { get; init; }
    public float RamAvailableGb { get; init; }
    public float RamUsedMaxGb { get; init; }
    public float RamUsagePercent { get; init; }
    public float TotalRamGb { get; init; }
    public float? VramUsedGb { get; init; }
    public float? VramUsedMaxGb { get; init; }
    public string? NetworkInterfaceName { get; init; }
    public float NetTotalBytesPerSecond { get; init; }
    public float NetUploadBytesPerSecond { get; init; }
    public float NetUploadMaxBytesPerSecond { get; init; }
    public float NetDownloadBytesPerSecond { get; init; }
    public float NetDownloadMaxBytesPerSecond { get; init; }
    public string? TopGpuProcess { get; init; }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal sealed class MemoryStatusEx
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;

    public MemoryStatusEx()
    {
        dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
    }
}

public sealed class HardwareMonitorService : IDisposable
{
    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    private static readonly Lazy<HardwareMonitorService> LazyInstance = new(() => new HardwareMonitorService());

    private const int InterfaceRefreshIntervalSeconds = 10;
    private const int ZeroTrafficRecheckSeconds = 5;
    private const int MaxLogBytes = 256 * 1024;
    private const int RetainedLogBytes = 128 * 1024;
    private const int MaxHistoryEntries = 3600;
    private const int ProcessRefreshInterval = 5;

    private readonly object _syncRoot = new();
    private readonly object _logLock = new();
    private readonly List<HardwareSnapshot> _history = new();
    private readonly Dictionary<string, PerformanceCounter> _trafficCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _maxValues = new()
    {
        { "CPU_USAGE", 0 },
        { "GPU_TEMP", 0 },
        { "RAM", 0 },
        { "VRAM", 0 },
        { "UP", 0 },
        { "DOWN", 0 }
    };
    private readonly HashSet<string> _reportedErrors = new(StringComparer.Ordinal);
    private readonly string _logPath;

    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _recvCounter;
    private PerformanceCounter? _sentCounter;
    private IGpuMonitor? _gpuMonitor;
    private WindowsGpuCounterMonitor? _vramFallback;
    private string? _interfaceName;
    private int _zeroTrafficSeconds;
    private int _networkRefreshCounter = InterfaceRefreshIntervalSeconds;
    private int _processRefreshCounter = ProcessRefreshInterval;
    private string? _cachedTopGpuProcess;
    private bool _disposed;

    private CancellationTokenSource? _cts;
    private Thread? _pollThread;

    public static HardwareMonitorService Instance => LazyInstance.Value;

    public event Action<HardwareSnapshot>? DataUpdated;

    public HardwareSnapshot LatestSnapshot { get; private set; } = new();

    private HardwareMonitorService()
    {
        string exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Environment.ProcessPath
            ?? AppContext.BaseDirectory;
        string exeDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        _logPath = Path.Combine(exeDir, "TempMonitor.log");

        Initialize();

        _cts = new CancellationTokenSource();
        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "HardwareMonitorPoll" };
        _pollThread.Start();
    }

    public void ResetMaxValues()
    {
        lock (_syncRoot)
        {
            foreach (string key in _maxValues.Keys)
            {
                _maxValues[key] = 0;
            }
        }
    }

    public string ExportCsv()
    {
        lock (_logLock)
        {
            if (_history.Count == 0)
                return "Timestamp,CPU Usage %,GPU Temp °C,GPU Usage %,RAM Used GB,RAM Usage %,VRAM Used GB,Upload B/s,Download B/s,Top GPU Process";

            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,CPU Usage %,GPU Temp °C,GPU Usage %,RAM Used GB,RAM Usage %,VRAM Used GB,Upload B/s,Download B/s,Top GPU Process");
            foreach (var s in _history)
            {
                var gpuTemp = s.GpuTemperature.HasValue ? $"{s.GpuTemperature.Value:0.0}" : "";
                var vram = s.VramUsedGb.HasValue ? $"{s.VramUsedGb.Value:F1}" : "";
                var topProc = s.TopGpuProcess ?? "";
                sb.AppendLine($"{s.Timestamp:yyyy-MM-dd HH:mm:ss},{s.CpuUsage:0.0},{gpuTemp},{s.GpuUsagePercent:0.0},{s.RamUsedGb:F1},{s.RamUsagePercent:0.0},{vram},{s.NetUploadBytesPerSecond:0},{s.NetDownloadBytesPerSecond:0},{topProc}");
            }
            return sb.ToString();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DataUpdated = null;

        _cts?.Cancel();

        if (_pollThread != null && !_pollThread.Join(3000))
        {
            try { _pollThread.Interrupt(); } catch { }
        }

        _cts?.Dispose();
        _cts = null;
        _pollThread = null;

        lock (_syncRoot)
        {
            _cpuCounter?.Dispose();
            _recvCounter?.Dispose();
            _sentCounter?.Dispose();
            _gpuMonitor?.Dispose();
            _gpuMonitor = null;
            _vramFallback?.Dispose();
            _vramFallback = null;

            foreach (PerformanceCounter counter in _trafficCounters.Values)
            {
                counter.Dispose();
            }

            _trafficCounters.Clear();
        }
    }

    private void PollLoop()
    {
        while (!_disposed)
        {
            try
            {
                if (!_cts!.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    PollMetrics();
                }
                else
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
        }
    }

    private void Initialize()
    {
        lock (_syncRoot)
        {
            try
            {
                _gpuMonitor = TryCreateGpuMonitor();
                _gpuMonitor?.TryInitialize();

                if (_gpuMonitor is not WindowsGpuCounterMonitor)
                {
                    var fallback = new WindowsGpuCounterMonitor();
                    if (fallback.TryInitialize())
                        _vramFallback = fallback;
                    else
                        fallback.Dispose();
                }

                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();

                RefreshNetworkInterfaces();
                SelectBestNetworkInterface();
                ClearReportedError("init");
            }
            catch (Exception ex)
            {
                ReportError("init", ex);
            }
        }
    }

    private void PollMetrics()
    {
        if (_disposed) return;

        HardwareSnapshot snapshot;
        lock (_syncRoot)
        {
            snapshot = BuildSnapshot();
            LatestSnapshot = snapshot;
        }

        lock (_logLock)
        {
            _history.Add(snapshot);
            while (_history.Count > MaxHistoryEntries)
                _history.RemoveAt(0);
        }

        DataUpdated?.Invoke(snapshot);
    }

    private HardwareSnapshot BuildSnapshot()
    {
        float cpuUsage = ReadCpuUsage();
        (float ramUsedGb, float ramUsagePercent, float totalRamGb) = ReadRamUsage();
        (float upload, float download) = ReadNetworkMetrics();

        float gpuUsagePercent = 0;
        float? gpuTemp = null;
        float? gpuPowerWatts = null;
        float? vramGb = null;

        if (_gpuMonitor?.Initialized == true)
        {
            var gpu = _gpuMonitor.Read();
            gpuTemp = gpu.Temperature;
            gpuUsagePercent = gpu.Usage ?? 0;
            vramGb = gpu.VramUsedGb;
            gpuPowerWatts = gpu.PowerWatts;
        }

        if (vramGb == null && _vramFallback?.Initialized == true)
        {
            var fallback = _vramFallback.Read();
            vramGb = fallback.VramUsedGb;
        }

        UpdateMax("CPU_USAGE", cpuUsage);
        UpdateMaxIfHasValue("GPU_TEMP", gpuTemp);
        UpdateMax("RAM", ramUsedGb);
        UpdateMaxIfHasValue("VRAM", vramGb);
        UpdateMax("UP", upload);
        UpdateMax("DOWN", download);

        _processRefreshCounter++;
        if (_processRefreshCounter >= ProcessRefreshInterval)
        {
            _processRefreshCounter = 0;
            _cachedTopGpuProcess = ReadTopGpuProcess();
        }

        return new HardwareSnapshot
        {
            Timestamp = DateTime.Now,
            CpuUsage = cpuUsage,
            CpuUsageMax = _maxValues["CPU_USAGE"],
            GpuUsagePercent = gpuUsagePercent,
            GpuTemperature = gpuTemp,
            GpuTemperatureMax = _maxValues["GPU_TEMP"] > 0 ? _maxValues["GPU_TEMP"] : null,
            GpuPowerWatts = gpuPowerWatts,
            RamUsedGb = ramUsedGb,
            RamAvailableGb = Math.Max(0, totalRamGb - ramUsedGb),
            RamUsedMaxGb = _maxValues["RAM"],
            RamUsagePercent = ramUsagePercent,
            TotalRamGb = totalRamGb,
            VramUsedGb = vramGb,
            VramUsedMaxGb = _maxValues["VRAM"] > 0 ? _maxValues["VRAM"] : null,
            NetworkInterfaceName = _interfaceName,
            NetTotalBytesPerSecond = upload + download,
            NetUploadBytesPerSecond = upload,
            NetUploadMaxBytesPerSecond = _maxValues["UP"],
            NetDownloadBytesPerSecond = download,
            NetDownloadMaxBytesPerSecond = _maxValues["DOWN"],
            TopGpuProcess = _cachedTopGpuProcess
        };
    }

    private float ReadCpuUsage()
    {
        if (_cpuCounter == null) return 0;

        try
        {
            float cpuUsage = _cpuCounter.NextValue();
            ClearReportedError("metric-cpu-usage");
            return cpuUsage;
        }
        catch (Exception ex)
        {
            ReportError("metric-cpu-usage", ex);
            return 0;
        }
    }

    private (float UsedGb, float UsagePercent, float TotalGb) ReadRamUsage()
    {
        try
        {
            var memoryStatus = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(memoryStatus)) return (0, 0, 0);

            double total = memoryStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            double used = (memoryStatus.ullTotalPhys - memoryStatus.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
            ClearReportedError("metric-ram");
            return ((float)used, memoryStatus.dwMemoryLoad, (float)total);
        }
        catch (Exception ex)
        {
            ReportError("metric-ram", ex);
            return (0, 0, 0);
        }
    }

    private (float Upload, float Download) ReadNetworkMetrics()
    {
        _networkRefreshCounter++;
        if (_networkRefreshCounter >= InterfaceRefreshIntervalSeconds)
        {
            _networkRefreshCounter = 0;
            RefreshNetworkInterfaces();
            SelectBestNetworkInterface();
        }

        if (_recvCounter == null || _sentCounter == null) return (0, 0);

        try
        {
            float upload = _sentCounter.NextValue();
            float download = _recvCounter.NextValue();

            if (upload <= 0 && download <= 0)
            {
                _zeroTrafficSeconds++;
            }
            else
            {
                _zeroTrafficSeconds = 0;
            }

            if (_zeroTrafficSeconds >= ZeroTrafficRecheckSeconds)
            {
                _zeroTrafficSeconds = 0;
                SelectBestNetworkInterface();
            }

            ClearReportedError("metric-network");
            return (upload, download);
        }
        catch (Exception ex)
        {
            ReportError("metric-network", ex);
            RefreshNetworkInterfaces();
            SelectBestNetworkInterface();
            return (0, 0);
        }
    }

    private void RefreshNetworkInterfaces()
    {
        try
        {
            var category = new PerformanceCounterCategory("Network Interface");
            string[] instanceNames = category.GetInstanceNames();

            foreach (string instance in instanceNames)
            {
                if (ShouldIgnoreInterface(instance) || _trafficCounters.ContainsKey(instance))
                    continue;

                var counter = new PerformanceCounter("Network Interface", "Bytes Total/sec", instance);
                counter.NextValue();
                _trafficCounters[instance] = counter;
            }

            var staleInstances = _trafficCounters.Keys
                .Where(instance => !instanceNames.Contains(instance, StringComparer.Ordinal))
                .ToList();

            foreach (string instance in staleInstances)
            {
                _trafficCounters[instance].Dispose();
                _trafficCounters.Remove(instance);

                if (string.Equals(_interfaceName, instance, StringComparison.Ordinal))
                {
                    SetActiveNetworkInterface(null);
                }
            }

            ClearReportedError("network-refresh");
        }
        catch (Exception ex)
        {
            ReportError("network-refresh", ex);
        }
    }

    private void SelectBestNetworkInterface()
    {
        if (_trafficCounters.Count == 0) return;

        try
        {
            string? bestInstance = null;
            float highestTraffic = -1;

            foreach ((string instance, PerformanceCounter counter) in _trafficCounters)
            {
                float value;
                try
                {
                    value = counter.NextValue();
                    ClearReportedError($"network-counter:{instance}");
                }
                catch (Exception ex)
                {
                    ReportError($"network-counter:{instance}", ex);
                    continue;
                }

                if (value > highestTraffic)
                {
                    highestTraffic = value;
                    bestInstance = instance;
                }
            }

            if (bestInstance != null && !string.Equals(bestInstance, _interfaceName, StringComparison.Ordinal))
            {
                SetActiveNetworkInterface(bestInstance);
            }

            ClearReportedError("network-select");
        }
        catch (Exception ex)
        {
            ReportError("network-select", ex);
        }
    }

    private void SetActiveNetworkInterface(string? interfaceName)
    {
        _recvCounter?.Dispose();
        _sentCounter?.Dispose();
        _recvCounter = null;
        _sentCounter = null;
        _interfaceName = interfaceName;

        if (string.IsNullOrWhiteSpace(interfaceName)) return;

        _recvCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", interfaceName);
        _sentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", interfaceName);
        _recvCounter.NextValue();
        _sentCounter.NextValue();
    }

    private static bool ShouldIgnoreInterface(string instanceName) =>
        instanceName.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
        instanceName.Contains("Pseudo", StringComparison.OrdinalIgnoreCase) ||
        instanceName.Contains("Teredo", StringComparison.OrdinalIgnoreCase);

    private void UpdateMax(string key, float current)
    {
        if (current > _maxValues[key])
            _maxValues[key] = current;
    }

    private void UpdateMaxIfHasValue(string key, float? current)
    {
        if (current.HasValue)
            UpdateMax(key, current.Value);
    }

    private void ReportError(string key, Exception ex)
    {
        if (!_reportedErrors.Add(key)) return;

        try
        {
            lock (_logLock)
            {
                TrimLogIfNeeded();
                File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {key}: {ex}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    private void TrimLogIfNeeded()
    {
        if (!File.Exists(_logPath)) return;

        var fileInfo = new FileInfo(_logPath);
        if (fileInfo.Length <= MaxLogBytes) return;

        using var source = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long bytesToKeep = Math.Min(RetainedLogBytes, source.Length);
        source.Seek(-bytesToKeep, SeekOrigin.End);

        byte[] buffer = new byte[bytesToKeep];
        int bytesRead = source.Read(buffer, 0, buffer.Length);
        if (bytesRead <= 0) return;

        int startIndex = Array.IndexOf(buffer, (byte)'\n');
        if (startIndex < 0 || startIndex >= bytesRead - 1)
            startIndex = 0;
        else
            startIndex++;

        File.WriteAllBytes(_logPath, buffer[startIndex..bytesRead]);
    }

    private void ClearReportedError(string key) => _reportedErrors.Remove(key);

    private string? ReadTopGpuProcess()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Process Memory");
            string[] instances = category.GetInstanceNames();
            if (instances.Length == 0) return null;

            var processVram = new Dictionary<int, long>();

            foreach (string instance in instances)
            {
                try
                {
                    if (!instance.StartsWith("pid_", StringComparison.Ordinal)) continue;
                    int underscoreIndex = instance.IndexOf('_', 4);
                    string pidPart = underscoreIndex > 4 ? instance[4..underscoreIndex] : instance[4..];
                    if (!int.TryParse(pidPart, out int pid)) continue;

                    using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance);
                    long bytes = counter.RawValue;
                    if (bytes > 0)
                    {
                        if (!processVram.ContainsKey(pid) || bytes > processVram[pid])
                            processVram[pid] = bytes;
                    }
                }
                catch
                {
                }
            }

            if (processVram.Count == 0) return null;

            var top = processVram.OrderByDescending(p => p.Value).First();
            string name = GetProcessName(top.Key);
            double mb = top.Value / (1024.0 * 1024.0);
            return $"{name} ({mb:0.0}MB)";
        }
        catch
        {
            return null;
        }
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return $"pid_{pid}";
        }
    }

    private static IGpuMonitor? TryCreateGpuMonitor()
    {
        if (NvidiaGpuMonitor.IsNvmlAvailable)
        {
            var nvidia = new NvidiaGpuMonitor();
            if (nvidia.TryInitialize())
                return nvidia;
            nvidia.Dispose();
        }

        if (AmdGpuMonitor.IsAdlAvailable)
        {
            var amd = new AmdGpuMonitor();
            if (amd.TryInitialize())
                return amd;
            amd.Dispose();
        }

        var windows = new WindowsGpuCounterMonitor();
        if (windows.TryInitialize())
            return windows;
        windows.Dispose();

        return null;
    }
}
