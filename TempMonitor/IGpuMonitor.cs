using System;
using System.Collections.Generic;

namespace TempMonitor;

internal interface IGpuMonitor : IDisposable
{
    bool Initialized { get; }
    bool IsHealthy { get; }
    string VendorName { get; }
    IReadOnlyList<GpuDeviceInfo> AvailableDevices { get; }
    bool TryInitialize();
    void SetPreferredDevice(string? deviceIdentifier);
    void RequestDeviceRefresh();
    GpuReading Read();
}

internal readonly record struct GpuDeviceInfo(
    string ProviderName,
    string DeviceIdentifier,
    string DisplayName);

[Flags]
public enum GpuMetricCapabilities
{
    None = 0,
    Temperature = 1 << 0,
    Usage = 1 << 1,
    VramUsed = 1 << 2,
    VramTotal = 1 << 3,
    Power = 1 << 4
}

public enum GpuPrimaryMetric
{
    None,
    Temperature,
    Usage,
    Power
}

internal readonly struct GpuReading
{
    public float? Temperature { get; init; }
    public float? Usage { get; init; }
    public float? VramUsedGb { get; init; }
    public float? VramTotalGb { get; init; }
    public float? PowerWatts { get; init; }
    public string? DeviceName { get; init; }
    public string? DeviceIdentifier { get; init; }
    public int? DeviceIndex { get; init; }

    public static GpuReading Empty => new();
}
