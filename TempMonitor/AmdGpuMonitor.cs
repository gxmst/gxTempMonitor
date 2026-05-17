using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TempMonitor;

internal sealed class AmdGpuMonitor : IGpuMonitor
{
    private const int AdlOk = 0;
    private const int AdlMaxAdapters = 40;
    private const int AdlMaxPath = 256;
    private const int AdlMaxDevicename = 32;

    private static readonly IntPtr AdlLib;
    private static readonly AdlMainControlCreateFn? AdlMainControlCreate;
    private static readonly AdlMainControlDestroyFn? AdlMainControlDestroy;
    private static readonly AdlAdapterNumberOfAdaptersGetFn? AdlGetAdapterCount;
    private static readonly AdlAdapterAdapterInfoGetFn? AdlGetAdapterInfo;
    private static readonly AdlOverdrive5TemperatureGetFn? AdlGetTemperature;
    private static readonly AdlOverdrive5CurrentActivityGetFn? AdlGetCurrentActivity;
    private static readonly AdlAdapterMemoryInfoGetFn? AdlGetMemoryInfo;
    private static readonly AdlOverdrive6CurrentPowerGetFn? AdlGetCurrentPower;

    private static readonly AdlMemoryAllocFn MemoryAllocCallback = AdlMemoryAlloc;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AdlMemoryAllocFn(int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlMainControlCreateFn(AdlMemoryAllocFn allocCallback, int enumConnectedAdapters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlMainControlDestroyFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlAdapterNumberOfAdaptersGetFn(ref int numAdapters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlAdapterAdapterInfoGetFn(IntPtr infoBuffer, int inputSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlOverdrive5TemperatureGetFn(int adapterIndex, int thermalControllerIndex, out AdlTemperature temperature);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlOverdrive5CurrentActivityGetFn(int adapterIndex, out AdlPMActivity activity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlAdapterMemoryInfoGetFn(int adapterIndex, out AdlMemoryInfo memoryInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlOverdrive6CurrentPowerGetFn(int adapterIndex, out int powerValue);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdlAdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string Uid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string BusNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string DriverNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string PnpString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxDevicename)]
        public string DisplayName;
        public int Present;
        public int Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string VendorName;
        public int VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlTemperature
    {
        public int Size;
        public int Temperature;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlPMActivity
    {
        public int Size;
        public int ActivityPercent;
        public int CurrentClock;
        public int CurrentMemoryClock;
        public int CurrentCoreVoltage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlMemoryInfo
    {
        public long MemorySize;
        public long Stripes;
    }

    public static bool IsAdlAvailable => AdlLib != IntPtr.Zero && AdlMainControlCreate != null;

    static AmdGpuMonitor()
    {
        AdlLib = TryLoadAdlLibrary();
        if (AdlLib == IntPtr.Zero) return;

        AdlMainControlCreate = GetExport<AdlMainControlCreateFn>("ADL_Main_Control_Create");
        AdlMainControlDestroy = GetExport<AdlMainControlDestroyFn>("ADL_Main_Control_Destroy");
        AdlGetAdapterCount = GetExport<AdlAdapterNumberOfAdaptersGetFn>("ADL_Adapter_NumberOfAdapters_Get");
        AdlGetAdapterInfo = GetExport<AdlAdapterAdapterInfoGetFn>("ADL_Adapter_AdapterInfo_Get");
        AdlGetTemperature = GetExport<AdlOverdrive5TemperatureGetFn>("ADL_Overdrive5_Temperature_Get");
        AdlGetCurrentActivity = GetExport<AdlOverdrive5CurrentActivityGetFn>("ADL_Overdrive5_CurrentActivity_Get");
        AdlGetMemoryInfo = GetExport<AdlAdapterMemoryInfoGetFn>("ADL_Adapter_MemoryInfo_Get");
        AdlGetCurrentPower = GetExport<AdlOverdrive6CurrentPowerGetFn>("ADL_Overdrive6_CurrentPower_Get");
    }

    private int _adapterIndex = -1;
    private long _vramTotalBytes;
    private bool _initialized;
    private bool _disposed;

    public bool Initialized => _initialized;
    public string VendorName => "AMD";

    public bool TryInitialize()
    {
        if (_initialized) return true;
        if (AdlMainControlCreate == null) return false;

        try
        {
            if (AdlMainControlCreate(MemoryAllocCallback, 1) != AdlOk)
                return false;

            int adapterCount = 0;
            if (AdlGetAdapterCount == null || AdlGetAdapterCount(ref adapterCount) != AdlOk || adapterCount <= 0)
            {
                TryDestroy();
                return false;
            }

            int structSize = Marshal.SizeOf<AdlAdapterInfo>();
            IntPtr infoBuffer = Marshal.AllocHGlobal(structSize * adapterCount);
            try
            {
                if (AdlGetAdapterInfo == null || AdlGetAdapterInfo(infoBuffer, structSize * adapterCount) != AdlOk)
                {
                    TryDestroy();
                    return false;
                }

                int foundIndex = -1;
                for (int i = 0; i < adapterCount; i++)
                {
                    IntPtr ptr = IntPtr.Add(infoBuffer, i * structSize);
                    var info = Marshal.PtrToStructure<AdlAdapterInfo>(ptr);
                    if (info.VendorId == 0x1002 && info.Present != 0)
                    {
                        foundIndex = info.AdapterIndex;
                        break;
                    }
                }

                if (foundIndex < 0)
                {
                    TryDestroy();
                    return false;
                }

                _adapterIndex = foundIndex;
            }
            finally
            {
                Marshal.FreeHGlobal(infoBuffer);
            }

            if (AdlGetMemoryInfo != null && AdlGetMemoryInfo(_adapterIndex, out AdlMemoryInfo memInfo) == AdlOk)
            {
                _vramTotalBytes = memInfo.MemorySize;
            }

            _initialized = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public GpuReading Read()
    {
        if (!_initialized || _disposed || _adapterIndex < 0)
            return GpuReading.Empty;

        float? temp = null, usage = null, vramUsed = null, vramTotal = null, power = null;

        try
        {
            if (AdlGetTemperature != null)
            {
                var adlTemp = new AdlTemperature { Size = Marshal.SizeOf<AdlTemperature>() };
                if (AdlGetTemperature(_adapterIndex, 0, out adlTemp) == AdlOk)
                    temp = adlTemp.Temperature / 1000f;
            }

            if (AdlGetCurrentActivity != null && AdlGetCurrentActivity(_adapterIndex, out AdlPMActivity activity) == AdlOk)
                usage = activity.ActivityPercent;

            if (_vramTotalBytes > 0)
            {
                vramTotal = _vramTotalBytes / (1024f * 1024f * 1024f);
            }

            if (AdlGetCurrentPower != null && AdlGetCurrentPower(_adapterIndex, out int powerValue) == AdlOk)
                power = powerValue / 1000f;
        }
        catch
        {
            _initialized = false;
        }

        return new GpuReading
        {
            Temperature = temp,
            Usage = usage,
            VramUsedGb = vramUsed,
            VramTotalGb = vramTotal,
            PowerWatts = power
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_initialized) TryDestroy();
        _initialized = false;
    }

    private void TryDestroy()
    {
        try { AdlMainControlDestroy?.Invoke(); } catch { }
    }

    private static IntPtr AdlMemoryAlloc(int size)
    {
        return Marshal.AllocHGlobal(size);
    }

    private static IntPtr TryLoadAdlLibrary()
    {
        if (NativeLibrary.TryLoad("atiadlxx.dll", out IntPtr handle))
            return handle;

        if (NativeLibrary.TryLoad("atiadlxy.dll", out handle))
            return handle;

        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string adlPath = Path.Combine(systemDir, "atiadlxx.dll");
        if (File.Exists(adlPath) && NativeLibrary.TryLoad(adlPath, out handle))
            return handle;

        string amdDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "AMD");
        if (Directory.Exists(amdDir))
        {
            string[] dirs = Directory.GetDirectories(amdDir);
            foreach (string dir in dirs)
            {
                string candidate = Path.Combine(dir, "atiadlxx.dll");
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                    return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static T? GetExport<T>(string name) where T : Delegate
    {
        try
        {
            IntPtr proc = NativeLibrary.GetExport(AdlLib, name);
            return proc != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(proc) : null;
        }
        catch
        {
            return null;
        }
    }
}
