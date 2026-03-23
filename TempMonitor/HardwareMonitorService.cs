using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor;

public sealed class HardwareSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public float CpuUsage { get; init; }
    public float CpuUsageMax { get; init; }
    public float? CpuClockMhz { get; init; }
    public float? CpuPackagePowerWatts { get; init; }
    public float? CpuTemperature { get; init; }
    public float? CpuTemperatureMax { get; init; }
    public float GpuUsagePercent { get; init; }
    public float? GpuPowerWatts { get; init; }
    public float? GpuFanRpm { get; init; }
    public float? GpuTemperature { get; init; }
    public float? GpuTemperatureMax { get; init; }
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

internal sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware subHardware in hardware.SubHardware)
        {
            subHardware.Accept(this);
        }
    }

    public void VisitSensor(ISensor sensor)
    {
    }

    public void VisitParameter(IParameter parameter)
    {
    }
}

public sealed class HardwareMonitorService : IDisposable
{
    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    private static readonly Lazy<HardwareMonitorService> LazyInstance = new(() => new HardwareMonitorService());

    private readonly object _syncRoot = new();
    private readonly UpdateVisitor _updateVisitor = new();
    private readonly Dictionary<string, PerformanceCounter> _trafficCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _maxValues = new()
    {
        { "CPU_USAGE", 0 },
        { "CPU_TEMP", 0 },
        { "GPU_TEMP", 0 },
        { "RAM", 0 },
        { "VRAM", 0 },
        { "UP", 0 },
        { "DOWN", 0 }
    };
    private readonly HashSet<string> _reportedErrors = new(StringComparer.Ordinal);
    private readonly string _logPath;
    private readonly System.Threading.Timer _timer;

    private Computer? _computer;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _recvCounter;
    private PerformanceCounter? _sentCounter;
    private string? _interfaceName;
    private int _zeroTrafficSeconds;
    private int _networkRefreshCounter = InterfaceRefreshIntervalSeconds;
    private bool _disposed;

    private const int InterfaceRefreshIntervalSeconds = 10;
    private const int ZeroTrafficRecheckSeconds = 5;
    private const int MaxLogBytes = 256 * 1024;
    private const int RetainedLogBytes = 128 * 1024;

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

        InitializeMonitoring();
        _timer = new System.Threading.Timer(_ => PollMetrics(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void ResetMaxValues()
    {
        lock (_syncRoot)
        {
            foreach (string key in _maxValues.Keys.ToList())
            {
                _maxValues[key] = 0;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();

        lock (_syncRoot)
        {
            try
            {
                _computer?.Close();
            }
            catch (Exception ex)
            {
                ReportError("close-hardware", ex);
            }

            _cpuCounter?.Dispose();
            _recvCounter?.Dispose();
            _sentCounter?.Dispose();

            foreach (PerformanceCounter counter in _trafficCounters.Values)
            {
                counter.Dispose();
            }

            _trafficCounters.Clear();
        }
    }

    private void InitializeMonitoring()
    {
        lock (_syncRoot)
        {
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true
                };
                _computer.Open();

                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();

                RefreshNetworkInterfaces();
                SelectBestNetworkInterface();
                ClearReportedError("init");
            }
            catch (Exception ex)
            {
                ReportError("init", ex);
                throw;
            }
        }
    }

    private void PollMetrics()
    {
        if (_disposed)
        {
            return;
        }

        HardwareSnapshot snapshot;
        lock (_syncRoot)
        {
            snapshot = BuildSnapshot();
            LatestSnapshot = snapshot;
        }

        DataUpdated?.Invoke(snapshot);
    }

    private HardwareSnapshot BuildSnapshot()
    {
        float cpuUsage = ReadCpuUsage();
        (float ramUsedGb, float ramUsagePercent, float totalRamGb) = ReadRamUsage();
        (float? cpuTemp, float? cpuClockMhz, float? cpuPackagePowerWatts, float gpuUsagePercent, float? gpuTemp, float? gpuPowerWatts, float? gpuFanRpm, float? vramGb) = ReadHardwareMetrics();
        (float upload, float download) = ReadNetworkMetrics();

        UpdateMax("CPU_USAGE", cpuUsage);
        UpdateMaxIfHasValue("CPU_TEMP", cpuTemp);
        UpdateMaxIfHasValue("GPU_TEMP", gpuTemp);
        UpdateMax("RAM", ramUsedGb);
        UpdateMaxIfHasValue("VRAM", vramGb);
        UpdateMax("UP", upload);
        UpdateMax("DOWN", download);

        return new HardwareSnapshot
        {
            Timestamp = DateTime.Now,
            CpuUsage = cpuUsage,
            CpuUsageMax = _maxValues["CPU_USAGE"],
            CpuClockMhz = cpuClockMhz,
            CpuPackagePowerWatts = cpuPackagePowerWatts,
            CpuTemperature = cpuTemp,
            CpuTemperatureMax = _maxValues["CPU_TEMP"] > 0 ? _maxValues["CPU_TEMP"] : null,
            GpuUsagePercent = gpuUsagePercent,
            GpuPowerWatts = gpuPowerWatts,
            GpuFanRpm = gpuFanRpm,
            GpuTemperature = gpuTemp,
            GpuTemperatureMax = _maxValues["GPU_TEMP"] > 0 ? _maxValues["GPU_TEMP"] : null,
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
            NetDownloadMaxBytesPerSecond = _maxValues["DOWN"]
        };
    }

    private float ReadCpuUsage()
    {
        if (_cpuCounter == null)
        {
            return 0;
        }

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
            if (!GlobalMemoryStatusEx(memoryStatus))
            {
                return (0, 0, 0);
            }

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

    private (float? CpuTemp, float? CpuClockMhz, float? CpuPackagePowerWatts, float GpuUsagePercent, float? GpuTemp, float? GpuPowerWatts, float? GpuFanRpm, float? VramGb) ReadHardwareMetrics()
    {
        if (_computer == null)
        {
            return (null, null, null, 0, null, null, null, null);
        }

        try
        {
            _computer.Accept(_updateVisitor);

            float? cpuTemp = null;
            float? cpuClockMhz = null;
            float? cpuPackagePowerWatts = null;
            float gpuUsagePercent = 0;
            float? gpuTemp = null;
            float? gpuPowerWatts = null;
            float? gpuFanRpm = null;
            float? vramGb = null;

            foreach (IHardware hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    cpuTemp = ReadTemperatureSensor(hardware.Sensors, "Package")
                        ?? ReadTemperatureSensor(hardware.Sensors, "Core")
                        ?? cpuTemp;
                    cpuPackagePowerWatts = ReadPreferredSensorValue(hardware.Sensors, SensorType.Power, "Package")
                        ?? cpuPackagePowerWatts;
                    cpuClockMhz = ReadCpuClock(hardware.Sensors) ?? cpuClockMhz;
                    continue;
                }

                if (!hardware.HardwareType.ToString().Contains("Gpu", StringComparison.Ordinal))
                {
                    continue;
                }

                gpuTemp = ReadTemperatureSensor(hardware.Sensors, "GPU Core")
                    ?? ReadTemperatureSensor(hardware.Sensors, "Core")
                    ?? gpuTemp;
                gpuUsagePercent = ReadGpuUsage(hardware.Sensors) ?? gpuUsagePercent;
                gpuPowerWatts = ReadPreferredSensorValue(hardware.Sensors, SensorType.Power, "Total")
                    ?? ReadPreferredSensorValue(hardware.Sensors, SensorType.Power, "Package")
                    ?? ReadPreferredSensorValue(hardware.Sensors, SensorType.Power, "Power")
                    ?? gpuPowerWatts;
                gpuFanRpm = ReadPreferredSensorValue(hardware.Sensors, SensorType.Fan, "GPU")
                    ?? ReadPreferredSensorValue(hardware.Sensors, SensorType.Fan, "Fan")
                    ?? gpuFanRpm;

                foreach (ISensor sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.SmallData ||
                        !sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (sensor.Name.Contains("Shared", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!sensor.Name.Contains("Dedicated", StringComparison.OrdinalIgnoreCase) &&
                        sensor.Value is > 24000)
                    {
                        continue;
                    }

                    if (sensor.Value is float value and > 0)
                    {
                        vramGb = value / 1024f;
                        break;
                    }
                }
            }

            cpuTemp ??= TryReadCpuTemperatureFromWmi();
            ClearReportedError("metric-hardware");
            return (cpuTemp, cpuClockMhz, cpuPackagePowerWatts, gpuUsagePercent, gpuTemp, gpuPowerWatts, gpuFanRpm, vramGb);
        }
        catch (Exception ex)
        {
            ReportError("metric-hardware", ex);
            return (null, null, null, 0, null, null, null, null);
        }
    }

    private static float? ReadTemperatureSensor(IEnumerable<ISensor> sensors, string preferredName) =>
        sensors.FirstOrDefault(sensor =>
            sensor.SensorType == SensorType.Temperature &&
            sensor.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase) &&
            sensor.Value is > 0)?.Value;

    private static float? ReadPreferredSensorValue(IEnumerable<ISensor> sensors, SensorType sensorType, string preferredName) =>
        sensors.FirstOrDefault(sensor =>
            sensor.SensorType == sensorType &&
            sensor.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase) &&
            sensor.Value is > 0)?.Value
        ?? sensors.FirstOrDefault(sensor =>
            sensor.SensorType == sensorType &&
            sensor.Value is > 0)?.Value;

    private static float? ReadCpuClock(IEnumerable<ISensor> sensors)
    {
        float? averageClock = sensors.FirstOrDefault(sensor =>
            sensor.SensorType == SensorType.Clock &&
            sensor.Name.Contains("Average", StringComparison.OrdinalIgnoreCase) &&
            sensor.Value is > 0)?.Value;
        if (averageClock.HasValue)
        {
            return averageClock.Value;
        }

        float[] coreClocks = sensors
            .Where(sensor =>
                sensor.SensorType == SensorType.Clock &&
                sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
                sensor.Value is > 0)
            .Select(sensor => sensor.Value!.Value)
            .ToArray();

        if (coreClocks.Length == 0)
        {
            return null;
        }

        return coreClocks.Average();
    }

    private static float? ReadGpuUsage(IEnumerable<ISensor> sensors) =>
        sensors.FirstOrDefault(sensor =>
            sensor.SensorType == SensorType.Load &&
            sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase) &&
            sensor.Value is >= 0)?.Value
        ?? sensors.FirstOrDefault(sensor =>
            sensor.SensorType == SensorType.Load &&
            (sensor.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase) ||
             sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
             sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)) &&
            sensor.Value is >= 0)?.Value;

    private float? TryReadCpuTemperatureFromWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["CurrentTemperature"] is uint raw && raw > 0)
                {
                    float celsius = (raw / 10f) - 273.15f;
                    if (celsius is > 0 and < 150)
                    {
                        ClearReportedError("metric-cpu-temp-wmi");
                        return celsius;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ReportError("metric-cpu-temp-wmi", ex);
        }

        return null;
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

        if (_recvCounter == null || _sentCounter == null)
        {
            return (0, 0);
        }

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
                {
                    continue;
                }

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
        if (_trafficCounters.Count == 0)
        {
            return;
        }

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

        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return;
        }

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
        {
            _maxValues[key] = current;
        }
    }

    private void UpdateMaxIfHasValue(string key, float? current)
    {
        if (current.HasValue)
        {
            UpdateMax(key, current.Value);
        }
    }

    private void ReportError(string key, Exception ex)
    {
        if (!_reportedErrors.Add(key))
        {
            return;
        }

        try
        {
            TrimLogIfNeeded();
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {key}: {ex}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void TrimLogIfNeeded()
    {
        if (!File.Exists(_logPath))
        {
            return;
        }

        var fileInfo = new FileInfo(_logPath);
        if (fileInfo.Length <= MaxLogBytes)
        {
            return;
        }

        using var source = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long bytesToKeep = Math.Min(RetainedLogBytes, source.Length);
        source.Seek(-bytesToKeep, SeekOrigin.End);

        byte[] buffer = new byte[bytesToKeep];
        int bytesRead = source.Read(buffer, 0, buffer.Length);
        if (bytesRead <= 0)
        {
            return;
        }

        int startIndex = Array.IndexOf(buffer, (byte)'\n');
        if (startIndex < 0 || startIndex >= bytesRead - 1)
        {
            startIndex = 0;
        }
        else
        {
            startIndex++;
        }

        File.WriteAllBytes(_logPath, buffer[startIndex..bytesRead]);
    }

    private void ClearReportedError(string key) => _reportedErrors.Remove(key);
}
