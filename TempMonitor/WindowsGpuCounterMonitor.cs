using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TempMonitor;

internal sealed class WindowsGpuCounterMonitor : IGpuMonitor
{
    private List<PerformanceCounter>? _engineCounters;
    private PerformanceCounter? _dedicatedUsageCounter;
    private PerformanceCounter? _dedicatedTotalCounter;
    private float _lastUsage;
    private bool _initialized;
    private bool _disposed;

    public bool Initialized => _initialized;
    public string VendorName => "Windows";

    public bool TryInitialize()
    {
        if (_initialized) return true;

        try
        {
            _engineCounters = CreateEngineCounters();
            if (_engineCounters == null || _engineCounters.Count == 0)
                return false;

            TryCreateMemoryCounters();

            foreach (var counter in _engineCounters)
                counter.NextValue();

            _dedicatedUsageCounter?.NextValue();
            _dedicatedTotalCounter?.NextValue();

            _initialized = true;
            return true;
        }
        catch
        {
            DisposeCounters();
            return false;
        }
    }

    public GpuReading Read()
    {
        if (!_initialized || _disposed)
            return GpuReading.Empty;

        float? usage = null, vramUsed = null, vramTotal = null;

        try
        {
            if (_engineCounters != null && _engineCounters.Count > 0)
            {
                float maxUsage = 0;
                foreach (var counter in _engineCounters)
                {
                    try
                    {
                        float val = counter.NextValue();
                        if (val > maxUsage)
                            maxUsage = val;
                    }
                    catch
                    {
                    }
                }

                _lastUsage = _lastUsage * 0.7f + maxUsage * 0.3f;
                usage = _lastUsage;
            }

            if (_dedicatedUsageCounter != null)
            {
                try
                {
                    float usedBytes = _dedicatedUsageCounter.NextValue();
                    if (usedBytes > 0)
                        vramUsed = usedBytes / (1024f * 1024f * 1024f);
                }
                catch
                {
                }
            }

            if (_dedicatedTotalCounter != null)
            {
                try
                {
                    float totalBytes = _dedicatedTotalCounter.NextValue();
                    if (totalBytes > 0)
                        vramTotal = totalBytes / (1024f * 1024f * 1024f);
                }
                catch
                {
                }
            }
        }
        catch
        {
            _initialized = false;
        }

        return new GpuReading
        {
            Temperature = null,
            Usage = usage,
            VramUsedGb = vramUsed,
            VramTotalGb = vramTotal,
            PowerWatts = null
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initialized = false;
        DisposeCounters();
    }

    private void DisposeCounters()
    {
        if (_engineCounters != null)
        {
            foreach (var c in _engineCounters)
                try { c.Dispose(); } catch { }
            _engineCounters = null;
        }

        try { _dedicatedUsageCounter?.Dispose(); } catch { }
        try { _dedicatedTotalCounter?.Dispose(); } catch { }
        _dedicatedUsageCounter = null;
        _dedicatedTotalCounter = null;
    }

    private List<PerformanceCounter>? CreateEngineCounters()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            string[] instances = category.GetInstanceNames();
            if (instances.Length == 0)
                return null;

            var counters = new List<PerformanceCounter>();
            var seenLuids = new HashSet<string>(StringComparer.Ordinal);

            foreach (string instance in instances)
            {
                if (!instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) &&
                    !instance.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase) &&
                    !instance.Contains("engtype_VideoEncode", StringComparison.OrdinalIgnoreCase) &&
                    !instance.Contains("engtype_VideoDecode", StringComparison.OrdinalIgnoreCase))
                    continue;

                int luidEnd = instance.IndexOf("_", StringComparison.Ordinal);
                if (luidEnd < 0) continue;

                string luid = instance.Substring(0, luidEnd);
                if (!seenLuids.Add(luid)) continue;

                try
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance);
                    counters.Add(counter);
                }
                catch
                {
                }
            }

            return counters.Count > 0 ? counters : null;
        }
        catch
        {
            return null;
        }
    }

    private void TryCreateMemoryCounters()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Adapter Memory");
            string[] instances = category.GetInstanceNames();
            if (instances.Length == 0) return;

            string firstInstance = instances[0];

            try
            {
                _dedicatedUsageCounter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", firstInstance);
            }
            catch { }

            try
            {
                _dedicatedTotalCounter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Total", firstInstance);
            }
            catch { }
        }
        catch
        {
        }
    }
}
