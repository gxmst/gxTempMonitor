using System;

namespace TempMonitor;

internal readonly record struct StableGpuPresentation(
    GpuMetricCapabilities Capabilities,
    GpuPrimaryMetric PrimaryMetric);

/// <summary>
/// Keeps device capabilities and the automatic headline metric stable across
/// isolated driver/API failures without presenting stale sensor values.
/// </summary>
internal sealed class GpuPresentationStabilizer
{
    internal const int MissingSamplesBeforeRemoval = 3;
    internal const int SamplesBeforeCapabilityRestore = 2;

    private static readonly GpuMetricCapabilities[] CapabilityOrder =
    [
        GpuMetricCapabilities.Temperature,
        GpuMetricCapabilities.Usage,
        GpuMetricCapabilities.VramUsed,
        GpuMetricCapabilities.VramTotal,
        GpuMetricCapabilities.Power
    ];

    private readonly object _syncRoot = new();
    private readonly int[] _missingSamples = new int[CapabilityOrder.Length];
    private readonly int[] _observedSamples = new int[CapabilityOrder.Length];

    private string? _deviceIdentity;
    private GpuMetricCapabilities _capabilities;

    public StableGpuPresentation Update(string? providerName, GpuReading reading)
    {
        lock (_syncRoot)
        {
            GpuMetricCapabilities observed = GetObservedCapabilities(reading);
            string? identity = BuildIdentity(providerName, reading);
            if (identity != null && !string.Equals(identity, _deviceIdentity, StringComparison.Ordinal))
            {
                ResetCore(identity);
                _capabilities = observed;
                return BuildPresentation();
            }

            if (_deviceIdentity == null)
            {
                if (identity == null)
                    return default;

                _deviceIdentity = identity;
            }

            // After a complete outage, the first valid sample re-establishes the
            // device baseline immediately. Later additions require confirmation.
            if (_capabilities == GpuMetricCapabilities.None && observed != GpuMetricCapabilities.None)
            {
                Array.Clear(_missingSamples);
                Array.Clear(_observedSamples);
                _capabilities = observed;
                return BuildPresentation();
            }

            for (int index = 0; index < CapabilityOrder.Length; index++)
            {
                GpuMetricCapabilities capability = CapabilityOrder[index];
                bool isObserved = (observed & capability) != 0;
                bool isKnown = (_capabilities & capability) != 0;

                if (isObserved)
                {
                    _missingSamples[index] = 0;
                    if (isKnown)
                    {
                        _observedSamples[index] = 0;
                        continue;
                    }

                    _observedSamples[index]++;
                    if (_observedSamples[index] >= SamplesBeforeCapabilityRestore)
                    {
                        _capabilities |= capability;
                        _observedSamples[index] = 0;
                    }
                }
                else
                {
                    _observedSamples[index] = 0;
                    if (!isKnown)
                    {
                        _missingSamples[index] = 0;
                        continue;
                    }

                    _missingSamples[index]++;
                    if (_missingSamples[index] >= MissingSamplesBeforeRemoval)
                    {
                        _capabilities &= ~capability;
                        _missingSamples[index] = 0;
                    }
                }
            }

            return BuildPresentation();
        }
    }

    public void Reset()
    {
        lock (_syncRoot)
            ResetCore(null);
    }

    internal static GpuMetricCapabilities GetObservedCapabilities(GpuReading reading)
    {
        GpuMetricCapabilities capabilities = GpuMetricCapabilities.None;
        if (reading.Temperature.HasValue) capabilities |= GpuMetricCapabilities.Temperature;
        if (reading.Usage.HasValue) capabilities |= GpuMetricCapabilities.Usage;
        if (reading.VramUsedGb.HasValue) capabilities |= GpuMetricCapabilities.VramUsed;
        if (reading.VramTotalGb.HasValue) capabilities |= GpuMetricCapabilities.VramTotal;
        if (reading.PowerWatts.HasValue) capabilities |= GpuMetricCapabilities.Power;
        return capabilities;
    }

    private StableGpuPresentation BuildPresentation() => new(
        _capabilities,
        ResolvePrimaryMetric(_capabilities));

    private static GpuPrimaryMetric ResolvePrimaryMetric(GpuMetricCapabilities capabilities)
    {
        if ((capabilities & GpuMetricCapabilities.Temperature) != 0)
            return GpuPrimaryMetric.Temperature;
        if ((capabilities & GpuMetricCapabilities.Usage) != 0)
            return GpuPrimaryMetric.Usage;
        if ((capabilities & GpuMetricCapabilities.Power) != 0)
            return GpuPrimaryMetric.Power;
        return GpuPrimaryMetric.None;
    }

    private static string? BuildIdentity(string? providerName, GpuReading reading)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return null;

        string? device = !string.IsNullOrWhiteSpace(reading.DeviceIdentifier)
            ? reading.DeviceIdentifier
            : !string.IsNullOrWhiteSpace(reading.DeviceName)
                ? reading.DeviceName
                : reading.DeviceIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(device))
            return null;

        return $"{providerName}\n{device}";
    }

    private void ResetCore(string? identity)
    {
        _deviceIdentity = identity;
        _capabilities = GpuMetricCapabilities.None;
        Array.Clear(_missingSamples);
        Array.Clear(_observedSamples);
    }
}
