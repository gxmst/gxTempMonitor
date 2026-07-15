using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TempMonitor;

internal sealed class NvidiaGpuMonitor : IGpuMonitor
{
    private const int NvmlSuccess = 0;
    private const int NvmlTempGpu = 0;
    private const int MaxNvmlDevices = 64;
    private const int MaxConsecutiveReadFailures = 3;
    private const int NvmlStringBufferSize = 256;
    private const ulong MaxPlausibleVramBytes = 1UL << 50;
    private static readonly long RetryDelayStopwatchTicks = checked(5L * Stopwatch.Frequency);

    private readonly object _syncRoot = new();
    private readonly List<DeviceState> _devices = new();

    private IntPtr _libraryHandle;
    private NvmlInitFn? _nvmlInit;
    private NvmlShutdownFn? _nvmlShutdown;
    private NvmlDeviceGetCountFn? _nvmlGetCount;
    private NvmlDeviceGetHandleByIndexFn? _nvmlGetHandle;
    private NvmlDeviceGetTemperatureFn? _nvmlGetTemperature;
    private NvmlDeviceGetUtilizationFn? _nvmlGetUtilization;
    private NvmlDeviceGetMemoryInfoFn? _nvmlGetMemoryInfo;
    private NvmlDeviceGetPowerUsageFn? _nvmlGetPowerUsage;
    private NvmlDeviceGetStringFn? _nvmlGetName;
    private NvmlDeviceGetStringFn? _nvmlGetUuid;

    private int _selectedDevicePosition = -1;
    private string? _preferredDeviceIdentifier;
    private int _consecutiveReadFailures;
    private long _nextRetryTimestamp;
    private bool _nvmlInitialized;
    private bool _sessionReady;
    private bool _accepted;
    private bool _healthy;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInitFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdownFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetCountFn(ref uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandleByIndexFn(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetTemperatureFn(IntPtr device, int type, out uint temperature);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetUtilizationFn(IntPtr device, out NvmlUtilization utilization);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetMemoryInfoFn(IntPtr device, out NvmlMemory memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetPowerUsageFn(IntPtr device, out uint powerMilliwatts);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetStringFn(IntPtr device, IntPtr buffer, uint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private sealed class DeviceState
    {
        public required IntPtr Handle { get; init; }
        public required uint Index { get; init; }
        public required string Name { get; init; }
        public required string Identifier { get; init; }
    }

    private readonly struct DeviceSample
    {
        public required int Position { get; init; }
        public required DeviceState Device { get; init; }
        public required bool NativeCallSucceeded { get; init; }
        public float? Temperature { get; init; }
        public float? Usage { get; init; }
        public float? VramUsedGb { get; init; }
        public float? VramTotalGb { get; init; }
        public float? PowerWatts { get; init; }
    }

    public static bool IsNvmlAvailable => FindTrustedNvmlPath() != null;

    public bool Initialized
    {
        get
        {
            lock (_syncRoot)
                return _accepted && !_disposed;
        }
    }

    public bool IsHealthy
    {
        get
        {
            lock (_syncRoot)
                return _healthy && _sessionReady && !_disposed;
        }
    }

    public string VendorName => "NVIDIA";

    public IReadOnlyList<GpuDeviceInfo> AvailableDevices
    {
        get
        {
            lock (_syncRoot)
            {
                var devices = new GpuDeviceInfo[_devices.Count];
                for (int index = 0; index < _devices.Count; index++)
                {
                    DeviceState device = _devices[index];
                    devices[index] = new GpuDeviceInfo(VendorName, device.Identifier, device.Name);
                }

                return devices;
            }
        }
    }

    public void SetPreferredDevice(string? deviceIdentifier)
    {
        lock (_syncRoot)
        {
            string? normalized = string.IsNullOrWhiteSpace(deviceIdentifier)
                ? null
                : deviceIdentifier;
            if (string.Equals(_preferredDeviceIdentifier, normalized, StringComparison.Ordinal))
                return;

            _preferredDeviceIdentifier = normalized;
            _selectedDevicePosition = -1;
        }
    }

    public void RequestDeviceRefresh()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            ResetSession(scheduleRetry: false);
            _nextRetryTimestamp = 0;
        }
    }

    public bool TryInitialize()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return false;

            if (_sessionReady)
                return true;

            return TryInitializeSession();
        }
    }

    public GpuReading Read()
    {
        lock (_syncRoot)
        {
            if (_disposed || !_accepted)
                return GpuReading.Empty;

            if (!_sessionReady)
            {
                if (Stopwatch.GetTimestamp() < _nextRetryTimestamp || !TryInitializeSession())
                    return GpuReading.Empty;
            }

            try
            {
                var samples = new List<DeviceSample>(_devices.Count);
                for (int position = 0; position < _devices.Count; position++)
                {
                    DeviceSample sample = ReadDevice(position, _devices[position]);
                    if (sample.NativeCallSucceeded)
                        samples.Add(sample);
                }

                if (samples.Count == 0)
                {
                    RegisterReadFailure();
                    return GpuReading.Empty;
                }

                _consecutiveReadFailures = 0;
                _healthy = true;

                DeviceSample selected = SelectDevice(samples);
                _selectedDevicePosition = selected.Position;

                return new GpuReading
                {
                    Temperature = selected.Temperature,
                    Usage = selected.Usage,
                    VramUsedGb = selected.VramUsedGb,
                    VramTotalGb = selected.VramTotalGb,
                    PowerWatts = selected.PowerWatts,
                    DeviceName = selected.Device.Name,
                    DeviceIdentifier = selected.Device.Identifier,
                    DeviceIndex = checked((int)selected.Device.Index)
                };
            }
            catch
            {
                RegisterReadFailure();
                return GpuReading.Empty;
            }
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            ResetSession(scheduleRetry: false);
            UnloadLibrary();
            _accepted = false;
            _healthy = false;
            _disposed = true;
        }
    }

    private bool TryInitializeSession()
    {
        if (Stopwatch.GetTimestamp() < _nextRetryTimestamp)
            return false;

        try
        {
            if (!EnsureLibraryLoaded() || _nvmlInit == null || _nvmlGetCount == null ||
                _nvmlGetHandle == null || _nvmlShutdown == null)
            {
                ScheduleRetry();
                return false;
            }

            if (_nvmlInit() != NvmlSuccess)
            {
                ScheduleRetry();
                return false;
            }

            _nvmlInitialized = true;

            uint count = 0;
            if (_nvmlGetCount(ref count) != NvmlSuccess || count == 0 || count > MaxNvmlDevices)
            {
                ResetSession(scheduleRetry: true);
                return false;
            }

            _devices.Clear();
            for (uint index = 0; index < count; index++)
            {
                if (_nvmlGetHandle(index, out IntPtr handle) != NvmlSuccess || handle == IntPtr.Zero)
                    continue;

                string name = TryReadDeviceString(_nvmlGetName, handle) ?? $"NVIDIA GPU {index}";
                string identifier = TryReadDeviceString(_nvmlGetUuid, handle) ?? $"nvml:{index}";
                _devices.Add(new DeviceState
                {
                    Handle = handle,
                    Index = index,
                    Name = name,
                    Identifier = identifier
                });
            }

            if (_devices.Count == 0)
            {
                ResetSession(scheduleRetry: true);
                return false;
            }

            // Do not claim the provider if the loaded DLL cannot actually read any device.
            // This lets the service choose the Windows counter fallback immediately instead
            // of being stuck behind an ABI-compatible but unusable NVML installation.
            for (int position = _devices.Count - 1; position >= 0; position--)
            {
                DeviceSample probe = ReadDevice(position, _devices[position]);
                if (!probe.NativeCallSucceeded)
                    _devices.RemoveAt(position);
            }

            if (_devices.Count == 0)
            {
                ResetSession(scheduleRetry: true);
                return false;
            }

            _selectedDevicePosition = -1;
            _consecutiveReadFailures = 0;
            _nextRetryTimestamp = 0;
            _sessionReady = true;
            _accepted = true;
            _healthy = true;
            return true;
        }
        catch
        {
            ResetSession(scheduleRetry: true);
            return false;
        }
    }

    private DeviceSample ReadDevice(int position, DeviceState device)
    {
        bool callSucceeded = false;
        float? temperature = null;
        float? usage = null;
        float? vramUsed = null;
        float? vramTotal = null;
        float? power = null;

        if (_nvmlGetTemperature != null &&
            _nvmlGetTemperature(device.Handle, NvmlTempGpu, out uint rawTemperature) == NvmlSuccess)
        {
            callSucceeded = true;
            if (rawTemperature <= 150)
                temperature = rawTemperature;
        }

        if (_nvmlGetUtilization != null &&
            _nvmlGetUtilization(device.Handle, out NvmlUtilization rawUtilization) == NvmlSuccess)
        {
            callSucceeded = true;
            if (rawUtilization.Gpu <= 100)
                usage = rawUtilization.Gpu;
        }

        if (_nvmlGetMemoryInfo != null &&
            _nvmlGetMemoryInfo(device.Handle, out NvmlMemory rawMemory) == NvmlSuccess)
        {
            callSucceeded = true;
            if (rawMemory.Total is > 0 and <= MaxPlausibleVramBytes &&
                rawMemory.Used <= rawMemory.Total)
            {
                const float bytesPerGiB = 1024f * 1024f * 1024f;
                vramUsed = rawMemory.Used / bytesPerGiB;
                vramTotal = rawMemory.Total / bytesPerGiB;
            }
        }

        if (_nvmlGetPowerUsage != null &&
            _nvmlGetPowerUsage(device.Handle, out uint rawPowerMilliwatts) == NvmlSuccess)
        {
            callSucceeded = true;
            if (rawPowerMilliwatts <= 5_000_000)
                power = rawPowerMilliwatts / 1000f;
        }

        return new DeviceSample
        {
            Position = position,
            Device = device,
            NativeCallSucceeded = callSucceeded,
            Temperature = temperature,
            Usage = usage,
            VramUsedGb = vramUsed,
            VramTotalGb = vramTotal,
            PowerWatts = power
        };
    }

    private DeviceSample SelectDevice(List<DeviceSample> samples)
    {
        if (!string.IsNullOrWhiteSpace(_preferredDeviceIdentifier))
        {
            foreach (DeviceSample sample in samples)
            {
                if (string.Equals(
                        sample.Device.Identifier,
                        _preferredDeviceIdentifier,
                        StringComparison.Ordinal))
                {
                    return sample;
                }
            }
        }

        DeviceSample best = samples[0];
        for (int i = 1; i < samples.Count; i++)
        {
            if (IsMoreActive(samples[i], best))
                best = samples[i];
        }

        DeviceSample? current = null;
        foreach (DeviceSample sample in samples)
        {
            if (sample.Position == _selectedDevicePosition)
            {
                current = sample;
                break;
            }
        }

        if (!current.HasValue || current.Value.Position == best.Position)
            return best;

        float currentUsage = current.Value.Usage ?? 0;
        float bestUsage = best.Usage ?? 0;
        if (bestUsage > currentUsage + 5f)
            return best;

        float currentPower = current.Value.PowerWatts ?? 0;
        float bestPower = best.PowerWatts ?? 0;
        if (currentUsage < 1f && bestUsage < 1f && bestPower > currentPower + 10f)
            return best;

        float currentMemory = current.Value.VramUsedGb ?? 0;
        float bestMemory = best.VramUsedGb ?? 0;
        if (currentUsage < 1f && bestUsage < 1f && bestMemory > currentMemory + 0.25f)
            return best;

        return current.Value;
    }

    private static bool IsMoreActive(DeviceSample candidate, DeviceSample current)
    {
        float candidateUsage = candidate.Usage ?? 0;
        float currentUsage = current.Usage ?? 0;
        if (Math.Abs(candidateUsage - currentUsage) > 0.01f)
            return candidateUsage > currentUsage;

        float candidatePower = candidate.PowerWatts ?? 0;
        float currentPower = current.PowerWatts ?? 0;
        if (Math.Abs(candidatePower - currentPower) > 1f)
            return candidatePower > currentPower;

        float candidateMemory = candidate.VramUsedGb ?? 0;
        float currentMemory = current.VramUsedGb ?? 0;
        if (Math.Abs(candidateMemory - currentMemory) > 0.001f)
            return candidateMemory > currentMemory;

        return candidate.Device.Index < current.Device.Index;
    }

    private void RegisterReadFailure()
    {
        _healthy = false;
        _consecutiveReadFailures++;
        if (_consecutiveReadFailures >= MaxConsecutiveReadFailures)
            ResetSession(scheduleRetry: true);
    }

    private void ResetSession(bool scheduleRetry)
    {
        if (_nvmlInitialized)
        {
            try
            {
                _nvmlShutdown?.Invoke();
            }
            catch
            {
            }
        }

        _nvmlInitialized = false;
        _sessionReady = false;
        _healthy = false;
        _consecutiveReadFailures = 0;
        _selectedDevicePosition = -1;
        _devices.Clear();

        if (scheduleRetry)
            ScheduleRetry();
    }

    private void ScheduleRetry() =>
        _nextRetryTimestamp = Stopwatch.GetTimestamp() + RetryDelayStopwatchTicks;

    private bool EnsureLibraryLoaded()
    {
        if (_libraryHandle != IntPtr.Zero)
            return true;

        string? path = FindTrustedNvmlPath();
        const DllImportSearchPath dependencySearchPath =
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32;
        if (path == null ||
            !NativeLibrary.TryLoad(
                path,
                typeof(NvidiaGpuMonitor).Assembly,
                dependencySearchPath,
                out _libraryHandle))
        {
            return false;
        }

        _nvmlInit = GetExport<NvmlInitFn>("nvmlInit_v2") ?? GetExport<NvmlInitFn>("nvmlInit");
        _nvmlShutdown = GetExport<NvmlShutdownFn>("nvmlShutdown");
        _nvmlGetCount = GetExport<NvmlDeviceGetCountFn>("nvmlDeviceGetCount_v2") ??
                        GetExport<NvmlDeviceGetCountFn>("nvmlDeviceGetCount");
        _nvmlGetHandle = GetExport<NvmlDeviceGetHandleByIndexFn>("nvmlDeviceGetHandleByIndex_v2") ??
                         GetExport<NvmlDeviceGetHandleByIndexFn>("nvmlDeviceGetHandleByIndex");
        _nvmlGetTemperature = GetExport<NvmlDeviceGetTemperatureFn>("nvmlDeviceGetTemperature");
        _nvmlGetUtilization = GetExport<NvmlDeviceGetUtilizationFn>("nvmlDeviceGetUtilizationRates");
        _nvmlGetMemoryInfo = GetExport<NvmlDeviceGetMemoryInfoFn>("nvmlDeviceGetMemoryInfo");
        _nvmlGetPowerUsage = GetExport<NvmlDeviceGetPowerUsageFn>("nvmlDeviceGetPowerUsage");
        _nvmlGetName = GetExport<NvmlDeviceGetStringFn>("nvmlDeviceGetName");
        _nvmlGetUuid = GetExport<NvmlDeviceGetStringFn>("nvmlDeviceGetUUID");

        bool hasRequiredExports = _nvmlInit != null && _nvmlShutdown != null &&
                                  _nvmlGetCount != null && _nvmlGetHandle != null;
        bool hasTelemetryExport = _nvmlGetTemperature != null || _nvmlGetUtilization != null ||
                                  _nvmlGetMemoryInfo != null || _nvmlGetPowerUsage != null;
        if (hasRequiredExports && hasTelemetryExport)
            return true;

        UnloadLibrary();
        return false;
    }

    private T? GetExport<T>(string name) where T : Delegate
    {
        if (_libraryHandle == IntPtr.Zero)
            return null;

        try
        {
            if (!NativeLibrary.TryGetExport(_libraryHandle, name, out IntPtr address) || address == IntPtr.Zero)
                return null;

            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
        catch
        {
            return null;
        }
    }

    private void UnloadLibrary()
    {
        _nvmlInit = null;
        _nvmlShutdown = null;
        _nvmlGetCount = null;
        _nvmlGetHandle = null;
        _nvmlGetTemperature = null;
        _nvmlGetUtilization = null;
        _nvmlGetMemoryInfo = null;
        _nvmlGetPowerUsage = null;
        _nvmlGetName = null;
        _nvmlGetUuid = null;

        if (_libraryHandle == IntPtr.Zero)
            return;

        IntPtr handle = _libraryHandle;
        _libraryHandle = IntPtr.Zero;
        try
        {
            NativeLibrary.Free(handle);
        }
        catch
        {
        }
    }

    private static string? TryReadDeviceString(NvmlDeviceGetStringFn? reader, IntPtr device)
    {
        if (reader == null)
            return null;

        IntPtr buffer = Marshal.AllocHGlobal(NvmlStringBufferSize);
        try
        {
            byte[] bytes = new byte[NvmlStringBufferSize];
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            if (reader(device, buffer, NvmlStringBufferSize) != NvmlSuccess)
                return null;

            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            int length = Array.IndexOf(bytes, (byte)0);
            if (length < 0)
                return null;

            string value = Encoding.UTF8.GetString(bytes, 0, length).Trim();
            return value.Length > 0 ? value : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? FindTrustedNvmlPath()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string? systemPath = GetTrustedFile(systemDirectory, "nvml.dll");
        if (systemPath != null)
            return systemPath;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return GetTrustedFile(programFiles, Path.Combine("NVIDIA Corporation", "NVSMI", "nvml.dll"));
    }

    private static string? GetTrustedFile(string trustedRoot, string relativePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(trustedRoot) || !Path.IsPathFullyQualified(trustedRoot))
                return null;

            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
            string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(candidate), "nvml.dll", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(candidate))
            {
                return null;
            }

            FileAttributes attributes = File.GetAttributes(candidate);
            return (attributes & FileAttributes.ReparsePoint) == 0 ? candidate : null;
        }
        catch
        {
            return null;
        }
    }
}
