using System;

namespace TempMonitor;

internal interface IGpuMonitor : IDisposable
{
    bool Initialized { get; }
    string VendorName { get; }
    bool TryInitialize();
    GpuReading Read();
}

internal readonly struct GpuReading
{
    public float? Temperature { get; init; }
    public float? Usage { get; init; }
    public float? VramUsedGb { get; init; }
    public float? VramTotalGb { get; init; }
    public float? PowerWatts { get; init; }

    public static GpuReading Empty => new();
}
