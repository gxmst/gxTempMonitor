using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TempMonitor;

internal sealed class AmdGpuMonitor : IGpuMonitor
{
    private const int AdlOk = 0;
    private const int AmdVendorIdHex = 0x1002;
    private const int AmdVendorIdAdlLegacy = 1002;
    private const int AdlMaxAdapters = 40;
    private const int AdlMaxPath = 256;
    private const int MaxConsecutiveReadFailures = 3;
    private const int AdlPowerTypeTotal = 0;
    private const long MaxPlausibleVramBytes = 1L << 50;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly AdlMemoryAllocFn MemoryAllocCallback = AllocateAdlMemory;

    private readonly object _syncRoot = new();
    private readonly List<DeviceState> _devices = new();

    private IntPtr _libraryHandle;
    private IntPtr _context;
    private Adl2MainControlCreateFn? _adlCreate;
    private Adl2MainControlDestroyFn? _adlDestroy;
    private Adl2AdapterNumberOfAdaptersGetFn? _adlGetAdapterCount;
    private Adl2AdapterAdapterInfoGetFn? _adlGetAdapterInfo;
    private Adl2OverdriveCapsFn? _adlGetOverdriveCaps;
    private Adl2Overdrive5TemperatureGetFn? _adlGetTemperature;
    private Adl2Overdrive5CurrentActivityGetFn? _adlGetCurrentActivity;
    private Adl2AdapterMemoryInfoGetFn? _adlGetMemoryInfo;
    private Adl2AdapterMemoryInfoX4GetFn? _adlGetMemoryInfoX4;
    private Adl2AdapterDedicatedVramUsageGetFn? _adlGetDedicatedVramUsage;
    private Adl2Overdrive6CurrentPowerGetFn? _adlGetCurrentPower;

    private int _selectedDevicePosition = -1;
    private int _consecutiveReadFailures;
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private bool _sessionReady;
    private bool _accepted;
    private bool _healthy;
    private bool _disposed;

    // ADL_MAIN_MALLOC_CALLBACK is __stdcall on Windows even though the ADL
    // entry points themselves use the C calling convention.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr AdlMemoryAllocFn(int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2MainControlCreateFn(AdlMemoryAllocFn allocate, int enumerateConnectedAdapters, out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2MainControlDestroyFn(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2AdapterNumberOfAdaptersGetFn(IntPtr context, ref int adapterCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2AdapterAdapterInfoGetFn(IntPtr context, IntPtr adapterInfo, int inputSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2OverdriveCapsFn(
        IntPtr context,
        int adapterIndex,
        out int supported,
        out int enabled,
        out int version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2Overdrive5TemperatureGetFn(
        IntPtr context,
        int adapterIndex,
        int thermalControllerIndex,
        ref AdlTemperature temperature);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2Overdrive5CurrentActivityGetFn(
        IntPtr context,
        int adapterIndex,
        ref AdlPmActivity activity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2AdapterMemoryInfoGetFn(
        IntPtr context,
        int adapterIndex,
        out AdlMemoryInfo memoryInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2AdapterMemoryInfoX4GetFn(
        IntPtr context,
        int adapterIndex,
        out AdlMemoryInfoX4 memoryInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2AdapterDedicatedVramUsageGetFn(
        IntPtr context,
        int adapterIndex,
        out int usageMegabytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2Overdrive6CurrentPowerGetFn(
        IntPtr context,
        int adapterIndex,
        int powerType,
        ref int currentValue);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdlAdapterInfo
    {
        public int Size;
        public int AdapterIndex;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string Udid;

        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string AdapterName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string DisplayName;

        public int Present;
        public int Exist;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string DriverPath;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string DriverPathExt;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string PnpString;

        public int OsDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlTemperature
    {
        public int Size;
        public int Temperature;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlPmActivity
    {
        public int Size;
        public int EngineClock;
        public int MemoryClock;
        public int CoreVoltage;
        public int ActivityPercent;
        public int CurrentPerformanceLevel;
        public int CurrentBusSpeed;
        public int CurrentBusLanes;
        public int MaximumBusLanes;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdlMemoryInfo
    {
        public long MemorySize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string MemoryType;

        public long MemoryBandwidth;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdlMemoryInfoX4
    {
        public long MemorySize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string MemoryType;

        public long MemoryBandwidth;
        public long HyperMemorySize;
        public long InvisibleMemorySize;
        public long VisibleMemorySize;
        public long VramVendorRevisionId;
        public long MemoryBandwidthX2;
        public long MemoryBitRateX2;
    }

    private sealed class DeviceState
    {
        public required int AdapterIndex { get; init; }
        public required string Name { get; init; }
        public required string Identifier { get; init; }
        public required string PhysicalKey { get; init; }
        public required bool OverdriveSupported { get; init; }
        public required int OverdriveVersion { get; init; }
        public long VramTotalBytes { get; init; }
    }

    private readonly struct DeviceSample
    {
        public required int Position { get; init; }
        public required DeviceState Device { get; init; }
        public required bool PrimaryCallSucceeded { get; init; }
        public float? Temperature { get; init; }
        public float? Usage { get; init; }
        public float? VramUsedGb { get; init; }
        public float? VramTotalGb { get; init; }
        public float? PowerWatts { get; init; }
    }

    public static bool IsAdlAvailable => FindTrustedAdlPath() != null;

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

    public string VendorName => "AMD";

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
                if (DateTime.UtcNow < _nextRetryUtc || !TryInitializeSession())
                    return GpuReading.Empty;
            }

            try
            {
                var samples = new List<DeviceSample>(_devices.Count);
                for (int position = 0; position < _devices.Count; position++)
                {
                    DeviceSample sample = ReadDevice(position, _devices[position]);
                    if (sample.PrimaryCallSucceeded)
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
                    DeviceIndex = selected.Device.AdapterIndex
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
        if (DateTime.UtcNow < _nextRetryUtc)
            return false;

        try
        {
            if (!ValidateAbi() || !EnsureLibraryLoaded() || _adlCreate == null || _adlDestroy == null ||
                _adlGetAdapterCount == null || _adlGetAdapterInfo == null)
            {
                ScheduleRetry();
                return false;
            }

            int createStatus = _adlCreate(MemoryAllocCallback, 1, out IntPtr createdContext);
            if (createStatus != AdlOk || createdContext == IntPtr.Zero)
            {
                if (createdContext != IntPtr.Zero)
                {
                    try
                    {
                        _adlDestroy(createdContext);
                    }
                    catch
                    {
                    }
                }

                ScheduleRetry();
                return false;
            }

            _context = createdContext;

            int adapterCount = 0;
            if (_adlGetAdapterCount(_context, ref adapterCount) != AdlOk ||
                adapterCount <= 0 || adapterCount > AdlMaxAdapters)
            {
                ResetSession(scheduleRetry: true);
                return false;
            }

            int structSize = Marshal.SizeOf<AdlAdapterInfo>();
            int bufferSize = checked(structSize * adapterCount);
            IntPtr infoBuffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                // AMD's ADL2 samples zero the AdapterInfo array and do not pre-fill
                // iSize for this bulk-output API. Sized input/output structures below
                // are instead initialized and passed by ref as required.
                byte[] zeroedBuffer = new byte[bufferSize];
                Marshal.Copy(zeroedBuffer, 0, infoBuffer, bufferSize);

                if (_adlGetAdapterInfo(_context, infoBuffer, bufferSize) != AdlOk)
                {
                    ResetSession(scheduleRetry: true);
                    return false;
                }

                _devices.Clear();
                for (int i = 0; i < adapterCount; i++)
                {
                    IntPtr current = IntPtr.Add(infoBuffer, checked(i * structSize));
                    AdlAdapterInfo info = Marshal.PtrToStructure<AdlAdapterInfo>(current);
                    if (!IsUsableAmdAdapter(info))
                        continue;

                    string physicalKey = GetPhysicalAdapterKey(info);
                    bool overdriveSupported = false;
                    int overdriveVersion = 0;
                    if (_adlGetOverdriveCaps != null &&
                        _adlGetOverdriveCaps(
                            _context,
                            info.AdapterIndex,
                            out int supported,
                            out _,
                            out int version) == AdlOk &&
                        supported != 0 && version >= 5)
                    {
                        overdriveSupported = true;
                        overdriveVersion = version;
                    }

                    long vramTotalBytes = 0;
                    if (_adlGetMemoryInfoX4 != null &&
                        _adlGetMemoryInfoX4(_context, info.AdapterIndex, out AdlMemoryInfoX4 memoryInfoX4) == AdlOk &&
                        IsPlausibleVramSize(memoryInfoX4.MemorySize))
                    {
                        vramTotalBytes = memoryInfoX4.MemorySize;
                    }
                    else if (_adlGetMemoryInfo != null &&
                        _adlGetMemoryInfo(_context, info.AdapterIndex, out AdlMemoryInfo memoryInfo) == AdlOk &&
                        IsPlausibleVramSize(memoryInfo.MemorySize))
                    {
                        vramTotalBytes = memoryInfo.MemorySize;
                    }

                    string name = string.IsNullOrWhiteSpace(info.AdapterName)
                        ? $"AMD GPU {info.AdapterIndex}"
                        : info.AdapterName.Trim();
                    string identifier = GetDeviceIdentifier(info);

                    _devices.Add(new DeviceState
                    {
                        AdapterIndex = info.AdapterIndex,
                        Name = name,
                        Identifier = identifier,
                        PhysicalKey = physicalKey,
                        OverdriveSupported = overdriveSupported,
                        OverdriveVersion = overdriveVersion,
                        VramTotalBytes = vramTotalBytes
                    });
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoBuffer);
            }

            var survivingPhysicalAdapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int position = 0; position < _devices.Count;)
            {
                DeviceSample probe = ReadDevice(position, _devices[position]);
                if (!probe.PrimaryCallSucceeded ||
                    !survivingPhysicalAdapters.Add(_devices[position].PhysicalKey))
                {
                    _devices.RemoveAt(position);
                    continue;
                }

                position++;
            }

            if (_devices.Count == 0)
            {
                ResetSession(scheduleRetry: true);
                return false;
            }

            _selectedDevicePosition = -1;
            _consecutiveReadFailures = 0;
            _nextRetryUtc = DateTime.MinValue;
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
        bool primaryCallSucceeded = false;
        float? temperature = null;
        float? usage = null;
        float? vramUsed = null;
        float? vramTotal = null;
        float? power = null;

        if (device.OverdriveSupported && _adlGetTemperature != null)
        {
            var rawTemperature = new AdlTemperature { Size = Marshal.SizeOf<AdlTemperature>() };
            if (_adlGetTemperature(_context, device.AdapterIndex, 0, ref rawTemperature) == AdlOk)
            {
                primaryCallSucceeded = true;
                if (rawTemperature.Temperature is >= 0 and <= 150_000)
                    temperature = rawTemperature.Temperature / 1000f;
            }
        }

        if (device.OverdriveSupported && _adlGetCurrentActivity != null)
        {
            var rawActivity = new AdlPmActivity { Size = Marshal.SizeOf<AdlPmActivity>() };
            if (_adlGetCurrentActivity(_context, device.AdapterIndex, ref rawActivity) == AdlOk)
            {
                primaryCallSucceeded = true;
                if (rawActivity.ActivityPercent is >= 0 and <= 100)
                    usage = rawActivity.ActivityPercent;
            }
        }

        if (device.VramTotalBytes > 0)
            vramTotal = BytesToGiB(device.VramTotalBytes);

        if (_adlGetDedicatedVramUsage != null &&
            _adlGetDedicatedVramUsage(_context, device.AdapterIndex, out int usageMegabytes) == AdlOk)
        {
            long usageBytes = (long)usageMegabytes * 1024L * 1024L;
            if (usageMegabytes >= 0 && usageBytes <= MaxPlausibleVramBytes &&
                (device.VramTotalBytes <= 0 || usageBytes <= device.VramTotalBytes))
            {
                vramUsed = BytesToGiB(usageBytes);
            }
        }

        if (device.OverdriveSupported && device.OverdriveVersion >= 6 && _adlGetCurrentPower != null)
        {
            int rawPower = 0;
            if (_adlGetCurrentPower(
                    _context,
                    device.AdapterIndex,
                    AdlPowerTypeTotal,
                    ref rawPower) == AdlOk)
            {
                primaryCallSucceeded = true;
                // ADL Overdrive 6 reports watts with eight fractional bits.
                if (rawPower is >= 0 and <= 1_280_000)
                    power = rawPower / 256f;
            }
        }

        return new DeviceSample
        {
            Position = position,
            Device = device,
            PrimaryCallSucceeded = primaryCallSucceeded,
            Temperature = temperature,
            Usage = usage,
            VramUsedGb = vramUsed,
            VramTotalGb = vramTotal,
            PowerWatts = power
        };
    }

    private DeviceSample SelectDevice(List<DeviceSample> samples)
    {
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

        return candidate.Device.AdapterIndex < current.Device.AdapterIndex;
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
        if (_context != IntPtr.Zero)
        {
            IntPtr context = _context;
            _context = IntPtr.Zero;
            try
            {
                _adlDestroy?.Invoke(context);
            }
            catch
            {
            }
        }

        _sessionReady = false;
        _healthy = false;
        _consecutiveReadFailures = 0;
        _selectedDevicePosition = -1;
        _devices.Clear();

        if (scheduleRetry)
            ScheduleRetry();
    }

    private void ScheduleRetry() => _nextRetryUtc = DateTime.UtcNow + RetryDelay;

    private bool EnsureLibraryLoaded()
    {
        if (_libraryHandle != IntPtr.Zero)
            return true;

        string? path = FindTrustedAdlPath();
        const DllImportSearchPath dependencySearchPath =
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32;
        if (path == null ||
            !NativeLibrary.TryLoad(
                path,
                typeof(AmdGpuMonitor).Assembly,
                dependencySearchPath,
                out _libraryHandle))
        {
            return false;
        }

        _adlCreate = GetExport<Adl2MainControlCreateFn>("ADL2_Main_Control_Create");
        _adlDestroy = GetExport<Adl2MainControlDestroyFn>("ADL2_Main_Control_Destroy");
        _adlGetAdapterCount = GetExport<Adl2AdapterNumberOfAdaptersGetFn>("ADL2_Adapter_NumberOfAdapters_Get");
        _adlGetAdapterInfo = GetExport<Adl2AdapterAdapterInfoGetFn>("ADL2_Adapter_AdapterInfo_Get");
        _adlGetOverdriveCaps = GetExport<Adl2OverdriveCapsFn>("ADL2_Overdrive_Caps");
        _adlGetTemperature = GetExport<Adl2Overdrive5TemperatureGetFn>("ADL2_Overdrive5_Temperature_Get");
        _adlGetCurrentActivity = GetExport<Adl2Overdrive5CurrentActivityGetFn>("ADL2_Overdrive5_CurrentActivity_Get");
        _adlGetMemoryInfoX4 = GetExport<Adl2AdapterMemoryInfoX4GetFn>("ADL2_Adapter_MemoryInfoX4_Get");
        _adlGetMemoryInfo = GetExport<Adl2AdapterMemoryInfoGetFn>("ADL2_Adapter_MemoryInfo_Get");
        _adlGetDedicatedVramUsage = GetExport<Adl2AdapterDedicatedVramUsageGetFn>("ADL2_Adapter_DedicatedVRAMUsage_Get");
        _adlGetCurrentPower = GetExport<Adl2Overdrive6CurrentPowerGetFn>("ADL2_Overdrive6_CurrentPower_Get");

        bool hasRequiredExports = _adlCreate != null && _adlDestroy != null &&
                                  _adlGetAdapterCount != null && _adlGetAdapterInfo != null;
        bool hasPrimaryTelemetryExport = _adlGetOverdriveCaps != null &&
                                         (_adlGetTemperature != null || _adlGetCurrentActivity != null ||
                                          _adlGetCurrentPower != null);
        if (hasRequiredExports && hasPrimaryTelemetryExport)
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
        _adlCreate = null;
        _adlDestroy = null;
        _adlGetAdapterCount = null;
        _adlGetAdapterInfo = null;
        _adlGetOverdriveCaps = null;
        _adlGetTemperature = null;
        _adlGetCurrentActivity = null;
        _adlGetMemoryInfo = null;
        _adlGetMemoryInfoX4 = null;
        _adlGetDedicatedVramUsage = null;
        _adlGetCurrentPower = null;

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

    private static IntPtr AllocateAdlMemory(int size)
    {
        if (size <= 0)
            return IntPtr.Zero;

        try
        {
            return Marshal.AllocHGlobal(size);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    internal static bool ValidateAbi()
    {
        return Marshal.SizeOf<AdlAdapterInfo>() == 1572 &&
               Marshal.OffsetOf<AdlAdapterInfo>(nameof(AdlAdapterInfo.BusNumber)).ToInt32() == 264 &&
               Marshal.OffsetOf<AdlAdapterInfo>(nameof(AdlAdapterInfo.VendorId)).ToInt32() == 276 &&
               Marshal.OffsetOf<AdlAdapterInfo>(nameof(AdlAdapterInfo.AdapterName)).ToInt32() == 280 &&
               Marshal.OffsetOf<AdlAdapterInfo>(nameof(AdlAdapterInfo.OsDisplayIndex)).ToInt32() == 1568 &&
               Marshal.SizeOf<AdlTemperature>() == 8 &&
               Marshal.SizeOf<AdlPmActivity>() == 40 &&
               Marshal.OffsetOf<AdlPmActivity>(nameof(AdlPmActivity.ActivityPercent)).ToInt32() == 16 &&
               Marshal.SizeOf<AdlMemoryInfo>() == 272 &&
               Marshal.OffsetOf<AdlMemoryInfo>(nameof(AdlMemoryInfo.MemoryBandwidth)).ToInt32() == 264 &&
               Marshal.SizeOf<AdlMemoryInfoX4>() == 320 &&
               Marshal.OffsetOf<AdlMemoryInfoX4>(nameof(AdlMemoryInfoX4.MemoryBitRateX2)).ToInt32() == 312;
    }

    private static bool IsUsableAmdAdapter(AdlAdapterInfo info)
    {
        if (info.AdapterIndex < 0 || (info.Present == 0 && info.Exist == 0))
            return false;

        if (info.VendorId == AmdVendorIdHex || info.VendorId == AmdVendorIdAdlLegacy)
            return true;

        return ContainsAmdVendorId(info.Udid) || ContainsAmdVendorId(info.PnpString);
    }

    private static bool ContainsAmdVendorId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase);

    private static string GetPhysicalAdapterKey(AdlAdapterInfo info)
    {
        if (info.BusNumber >= 0 && info.DeviceNumber >= 0 && info.FunctionNumber >= 0)
            return $"pci:{info.BusNumber:X2}:{info.DeviceNumber:X2}.{info.FunctionNumber}";

        if (!string.IsNullOrWhiteSpace(info.Udid))
            return info.Udid.Trim();

        return $"adl:{info.AdapterIndex}";
    }

    private static string GetDeviceIdentifier(AdlAdapterInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.PnpString))
            return info.PnpString.Trim();

        if (!string.IsNullOrWhiteSpace(info.Udid))
            return info.Udid.Trim();

        return GetPhysicalAdapterKey(info);
    }

    private static bool IsPlausibleVramSize(long bytes) => bytes > 0 && bytes <= MaxPlausibleVramBytes;

    private static float BytesToGiB(long bytes) => bytes / (1024f * 1024f * 1024f);

    private static string? FindTrustedAdlPath()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return null;

        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string? systemPath = GetTrustedFile(systemDirectory, "atiadlxx.dll");
        if (systemPath != null)
            return systemPath;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] relativePaths =
        {
            Path.Combine("AMD", "CNext", "CNext", "atiadlxx.dll"),
            Path.Combine("AMD", "CNext", "atiadlxx.dll"),
            Path.Combine("ATI Technologies", "ATI.ACE", "Core-Static", "atiadlxx.dll")
        };

        foreach (string relativePath in relativePaths)
        {
            string? candidate = GetTrustedFile(programFiles, relativePath);
            if (candidate != null)
                return candidate;
        }

        return null;
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
                !string.Equals(Path.GetFileName(candidate), "atiadlxx.dll", StringComparison.OrdinalIgnoreCase) ||
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
