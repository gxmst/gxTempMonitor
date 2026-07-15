using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TempMonitor;

internal readonly record struct StaticSystemMetrics(
    string? CpuName,
    int LogicalProcessorCount,
    string CpuArchitecture);

internal readonly record struct DynamicSystemMetrics(
    TimeSpan SystemUptime,
    bool? IsBatteryPresent,
    float? BatteryChargePercent,
    bool? IsOnAcPower,
    bool HasSystemDriveData,
    float SystemDriveTotalGb,
    float SystemDriveAvailableGb);

/// <summary>
/// Reads inexpensive Windows system information without WMI, drivers, elevation,
/// hardware buses, or access to other processes.
/// </summary>
internal sealed class SystemMetricsReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    private const ushort AllProcessorGroups = 0xffff;
    private const int DiskRefreshSeconds = 60;

    private long _nextDiskRefreshTimestamp;
    private bool _hasSystemDriveData;
    private float _systemDriveTotalGb;
    private float _systemDriveAvailableGb;

    public StaticSystemMetrics StaticMetrics { get; } = new(
        ReadCpuName(),
        ReadLogicalProcessorCount(),
        RuntimeInformation.OSArchitecture.ToString());

    public DynamicSystemMetrics Read()
    {
        long now = Stopwatch.GetTimestamp();
        if (now >= _nextDiskRefreshTimestamp)
            RefreshSystemDrive(now);

        ReadPowerStatus(
            out bool? isBatteryPresent,
            out float? batteryChargePercent,
            out bool? isOnAcPower);

        return new DynamicSystemMetrics(
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            isBatteryPresent,
            batteryChargePercent,
            isOnAcPower,
            _hasSystemDriveData,
            _systemDriveTotalGb,
            _systemDriveAvailableGb);
    }

    private static string? ReadCpuName()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString",
                null);
            if (value is not string name || string.IsNullOrWhiteSpace(name))
                return null;

            return string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static int ReadLogicalProcessorCount()
    {
        try
        {
            uint processorCount = GetActiveProcessorCount(AllProcessorGroups);
            if (processorCount is > 0 and <= int.MaxValue)
                return (int)processorCount;
        }
        catch (EntryPointNotFoundException)
        {
        }

        return Math.Max(1, Environment.ProcessorCount);
    }

    private static void ReadPowerStatus(
        out bool? isBatteryPresent,
        out float? batteryChargePercent,
        out bool? isOnAcPower)
    {
        isBatteryPresent = null;
        batteryChargePercent = null;
        isOnAcPower = null;

        if (!GetSystemPowerStatus(out SystemPowerStatus status))
            return;

        isOnAcPower = status.AcLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null
        };

        if (status.BatteryFlag != byte.MaxValue)
            isBatteryPresent = (status.BatteryFlag & 0x80) == 0;

        if (isBatteryPresent == true && status.BatteryLifePercent <= 100)
            batteryChargePercent = status.BatteryLifePercent;
    }

    private void RefreshSystemDrive(long now)
    {
        _nextDiskRefreshTimestamp = now + checked((long)DiskRefreshSeconds * Stopwatch.Frequency);

        try
        {
            string? root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(root))
                throw new IOException("The Windows system drive could not be determined.");

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
                throw new IOException("The Windows system drive is not ready.");

            const double bytesPerGb = 1024d * 1024d * 1024d;
            _systemDriveTotalGb = (float)(drive.TotalSize / bytesPerGb);
            _systemDriveAvailableGb = (float)(drive.AvailableFreeSpace / bytesPerGb);
            _hasSystemDriveData = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _systemDriveTotalGb = 0;
            _systemDriveAvailableGb = 0;
            _hasSystemDriveData = false;
        }
    }
}
