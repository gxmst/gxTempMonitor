using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TempMonitor;

internal sealed class VendorGpuMonitor : IDisposable
{
    private const int NvmlSuccess = 0;
    private const int NvmlTempGpu = 0;

    private static readonly IntPtr NvmlLib;
    private static readonly NvmlInitFn? NvmlInit;
    private static readonly NvmlShutdownFn? NvmlShutdown;
    private static readonly NvmlDeviceGetCountFn? NvmlGetCount;
    private static readonly NvmlDeviceGetHandleByIndexFn? NvmlGetHandle;
    private static readonly NvmlDeviceGetTemperatureFn? NvmlGetTemp;
    private static readonly NvmlDeviceGetUtilizationFn? NvmlGetUtil;
    private static readonly NvmlDeviceGetMemoryInfoFn? NvmlGetMem;
    private static readonly NvmlDeviceGetPowerUsageFn? NvmlGetPower;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlInitFn();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlShutdownFn();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlDeviceGetCountFn(ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlDeviceGetHandleByIndexFn(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlDeviceGetTemperatureFn(IntPtr device, int type, out uint temp);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlDeviceGetUtilizationFn(IntPtr device, out NvmlUtilization util);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlDeviceGetMemoryInfoFn(IntPtr device, out NvmlMemory mem);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvmlDeviceGetPowerUsageFn(IntPtr device, out uint powerMw);

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

    public static bool IsNvmlAvailable => NvmlLib != IntPtr.Zero && NvmlInit != null;

    static VendorGpuMonitor()
    {
        NvmlLib = TryLoadNvmlLibrary();
        if (NvmlLib == IntPtr.Zero) return;

        NvmlInit = GetExport<NvmlInitFn>("nvmlInit_v2");
        NvmlShutdown = GetExport<NvmlShutdownFn>("nvmlShutdown");
        NvmlGetCount = GetExport<NvmlDeviceGetCountFn>("nvmlDeviceGetCount_v2");
        NvmlGetHandle = GetExport<NvmlDeviceGetHandleByIndexFn>("nvmlDeviceGetHandleByIndex_v2");
        NvmlGetTemp = GetExport<NvmlDeviceGetTemperatureFn>("nvmlDeviceGetTemperature");
        NvmlGetUtil = GetExport<NvmlDeviceGetUtilizationFn>("nvmlDeviceGetUtilizationRates");
        NvmlGetMem = GetExport<NvmlDeviceGetMemoryInfoFn>("nvmlDeviceGetMemoryInfo");
        NvmlGetPower = GetExport<NvmlDeviceGetPowerUsageFn>("nvmlDeviceGetPowerUsage");
    }

    private IntPtr _deviceHandle;
    private bool _initialized;
    private bool _disposed;

    public bool Initialized => _initialized;

    public bool TryInitialize()
    {
        if (_initialized) return true;
        if (NvmlInit == null) return false;

        try
        {
            if (NvmlInit() != NvmlSuccess) return false;

            uint count = 0;
            if (NvmlGetCount == null || NvmlGetCount(ref count) != NvmlSuccess || count == 0)
            {
                TryShutdown();
                return false;
            }

            if (NvmlGetHandle == null || NvmlGetHandle(0, out _deviceHandle) != NvmlSuccess)
            {
                TryShutdown();
                return false;
            }

            _initialized = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public (float? Temperature, float? Usage, float? VramUsedGb, float? VramTotalGb, float? PowerWatts) Read()
    {
        if (!_initialized || _disposed)
            return (null, null, null, null, null);

        float? temp = null, usage = null, vramUsed = null, vramTotal = null, power = null;

        try
        {
            if (NvmlGetTemp != null && NvmlGetTemp(_deviceHandle, NvmlTempGpu, out uint t) == NvmlSuccess)
                temp = t;

            if (NvmlGetUtil != null && NvmlGetUtil(_deviceHandle, out NvmlUtilization u) == NvmlSuccess)
                usage = u.Gpu;

            if (NvmlGetMem != null && NvmlGetMem(_deviceHandle, out NvmlMemory m) == NvmlSuccess)
            {
                vramUsed = m.Used / (1024f * 1024f * 1024f);
                vramTotal = m.Total / (1024f * 1024f * 1024f);
            }

            if (NvmlGetPower != null && NvmlGetPower(_deviceHandle, out uint mw) == NvmlSuccess)
                power = mw / 1000f;
        }
        catch
        {
            _initialized = false;
        }

        return (temp, usage, vramUsed, vramTotal, power);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_initialized) TryShutdown();
        _initialized = false;
    }

    private void TryShutdown()
    {
        try { NvmlShutdown?.Invoke(); } catch { }
    }

    private static IntPtr TryLoadNvmlLibrary()
    {
        if (NativeLibrary.TryLoad("nvml.dll", out IntPtr handle))
            return handle;

        string nvidiaDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI");
        string nvmlPath = Path.Combine(nvidiaDir, "nvml.dll");

        if (File.Exists(nvmlPath) && NativeLibrary.TryLoad(nvmlPath, out handle))
            return handle;

        return IntPtr.Zero;
    }

    private static T? GetExport<T>(string name) where T : Delegate
    {
        try
        {
            IntPtr proc = NativeLibrary.GetExport(NvmlLib, name);
            return proc != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(proc) : null;
        }
        catch
        {
            return null;
        }
    }
}
