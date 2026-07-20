using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace TempMonitor;

internal sealed class WindowsGpuCounterMonitor : IGpuMonitor
{
    private const string EngineCategoryName = "GPU Engine";
    private const string EngineUtilizationCounterName = "Utilization Percentage";
    private const string MemoryCategoryName = "GPU Adapter Memory";
    private const string DedicatedUsageCounterName = "Dedicated Usage";
    private const int MaxEngineInstances = 16_384;
    private const int MaxMemoryInstances = 256;
    private const int MaxConsecutiveFailures = 3;
    private const int CachePruneInterval = 60;
    private const long MaxPlausibleMemoryBytes = 1L << 50;
    private static readonly long RetryDelayStopwatchTicks = checked(5L * Stopwatch.Frequency);

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, EngineMetadata> _engineMetadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CounterSample> _previousEngineSamples = new(StringComparer.Ordinal);
    private readonly Dictionary<AdapterKey, float> _smoothedUsage = new();
    private GpuDeviceInfo[] _availableDevices = [];

    private PerformanceCounterCategory? _engineCategory;
    private PerformanceCounterCategory? _memoryCategory;
    private AdapterKey? _selectedAdapter;
    private string? _preferredDeviceIdentifier;
    private long _nextRetryTimestamp;
    private int _consecutiveReadFailures;
    private int _engineReadFailures;
    private int _memoryReadFailures;
    private int _readCount;
    private bool _categoriesReady;
    private bool _accepted;
    private bool _healthy;
    private bool _disposed;

    private readonly record struct AdapterKey(uint LuidHigh, uint LuidLow, int PhysicalAdapter)
    {
        public string Identifier =>
            $"luid_0x{LuidHigh:X8}_0x{LuidLow:X8}_phys_{PhysicalAdapter.ToString(CultureInfo.InvariantCulture)}";
    }

    private readonly record struct EngineKey(AdapterKey Adapter, int EngineIndex);

    private readonly record struct EngineMetadata(AdapterKey Adapter, int EngineIndex);

    internal static bool TryGetEngineGroupKey(string instanceName, out string groupKey)
    {
        if (TryParseEngineKey(instanceName, out AdapterKey adapter, out int engineIndex))
        {
            groupKey = $"{adapter.Identifier}_eng_{engineIndex.ToString(CultureInfo.InvariantCulture)}";
            return true;
        }

        groupKey = string.Empty;
        return false;
    }

    internal static bool TryGetAdapterGroupKey(string instanceName, out string groupKey)
    {
        if (TryParseAdapterKey(instanceName, out AdapterKey adapter))
        {
            groupKey = adapter.Identifier;
            return true;
        }

        groupKey = string.Empty;
        return false;
    }

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
                return _healthy && _categoriesReady && !_disposed;
        }
    }

    public string VendorName => "Windows";

    public IReadOnlyList<GpuDeviceInfo> AvailableDevices
    {
        get
        {
            lock (_syncRoot)
                return (GpuDeviceInfo[])_availableDevices.Clone();
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
            _selectedAdapter = null;
        }
    }

    public void RequestDeviceRefresh()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            ResetCategories(scheduleRetry: false);
            _nextRetryTimestamp = 0;
        }
    }

    public bool TryInitialize()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return false;

            if (_categoriesReady)
                return true;

            return TryInitializeCategories();
        }
    }

    public GpuReading Read()
    {
        lock (_syncRoot)
        {
            if (_disposed || !_accepted)
                return GpuReading.Empty;

            if (!_categoriesReady)
            {
                if (Stopwatch.GetTimestamp() < _nextRetryTimestamp || !TryInitializeCategories())
                    return GpuReading.Empty;
            }

            TryRestoreMissingCategory();

            bool engineReadSucceeded = TryReadEngineUsage(out Dictionary<AdapterKey, float> usageByAdapter);
            bool memoryReadSucceeded = TryReadDedicatedMemory(out Dictionary<AdapterKey, long> memoryByAdapter);

            if (!engineReadSucceeded)
                RegisterEngineFailure();
            else
                _engineReadFailures = 0;

            if (!memoryReadSucceeded)
                RegisterMemoryFailure();
            else
                _memoryReadFailures = 0;

            var adapters = new HashSet<AdapterKey>(usageByAdapter.Keys);
            adapters.UnionWith(memoryByAdapter.Keys);
            if (adapters.Count == 0)
            {
                RegisterReadFailure();
                return GpuReading.Empty;
            }

            var availableDevices = new List<GpuDeviceInfo>(adapters.Count);
            foreach (AdapterKey adapter in adapters)
            {
                availableDevices.Add(new GpuDeviceInfo(
                    VendorName,
                    adapter.Identifier,
                    $"Windows GPU ({adapter.Identifier})"));
            }
            availableDevices.Sort(static (left, right) => string.Compare(
                left.DeviceIdentifier,
                right.DeviceIdentifier,
                StringComparison.Ordinal));
            _availableDevices = availableDevices.ToArray();

            _consecutiveReadFailures = 0;
            _healthy = true;

            AdapterKey selected = SelectAdapter(adapters, usageByAdapter, memoryByAdapter);
            _selectedAdapter = selected;

            foreach ((AdapterKey adapter, float rawValue) in usageByAdapter)
            {
                float rawUsage = Math.Clamp(rawValue, 0, 100);
                float smoothed = _smoothedUsage.TryGetValue(adapter, out float previous)
                    ? previous * 0.7f + rawUsage * 0.3f
                    : rawUsage;

                _smoothedUsage[adapter] = smoothed;
            }

            float? usage = usageByAdapter.ContainsKey(selected) &&
                           _smoothedUsage.TryGetValue(selected, out float selectedUsage)
                ? selectedUsage
                : null;

            float? vramUsed = null;
            if (memoryByAdapter.TryGetValue(selected, out long usedBytes))
                vramUsed = usedBytes / (1024f * 1024f * 1024f);

            PruneAdapterState(adapters);

            return new GpuReading
            {
                Temperature = null,
                Usage = usage,
                VramUsedGb = vramUsed,
                // GPU Adapter Memory exposes committed/used memory, not the physical VRAM capacity.
                VramTotalGb = null,
                PowerWatts = null,
                DeviceName = $"Windows GPU ({selected.Identifier})",
                DeviceIdentifier = selected.Identifier,
                DeviceIndex = selected.PhysicalAdapter
            };
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            ResetCategories(scheduleRetry: false);
            _accepted = false;
            _healthy = false;
            _disposed = true;
        }
    }

    private bool TryInitializeCategories()
    {
        if (Stopwatch.GetTimestamp() < _nextRetryTimestamp)
            return false;

        ResetCategories(scheduleRetry: false);

        try
        {
            bool hasEngineAdapter = false;
            if (PerformanceCounterCategory.Exists(EngineCategoryName))
            {
                _engineCategory = new PerformanceCounterCategory(EngineCategoryName);
                hasEngineAdapter = TryPrimeEngineSamples();
                if (!hasEngineAdapter)
                    _engineCategory = null;
            }

            bool hasMemoryAdapter = false;
            if (PerformanceCounterCategory.Exists(MemoryCategoryName))
            {
                _memoryCategory = new PerformanceCounterCategory(MemoryCategoryName);
                hasMemoryAdapter = TryReadDedicatedMemory(out Dictionary<AdapterKey, long> memory) &&
                                   memory.Count > 0;
                if (!hasMemoryAdapter)
                    _memoryCategory = null;
            }

            if (!hasEngineAdapter && !hasMemoryAdapter)
            {
                ResetCategories(scheduleRetry: true);
                return false;
            }

            _consecutiveReadFailures = 0;
            _engineReadFailures = 0;
            _memoryReadFailures = 0;
            _nextRetryTimestamp = 0;
            _categoriesReady = true;
            _accepted = true;
            _healthy = true;
            return true;
        }
        catch
        {
            ResetCategories(scheduleRetry: true);
            return false;
        }
    }

    private bool TryPrimeEngineSamples()
    {
        if (_engineCategory == null)
            return false;

        try
        {
            InstanceDataCollectionCollection categoryData = _engineCategory.ReadCategory();
            InstanceDataCollection? instances = FindCounter(categoryData, EngineUtilizationCounterName);
            if (instances == null || instances.Count == 0 || instances.Count > MaxEngineInstances)
                return false;

            bool foundAdapter = false;
            foreach (DictionaryEntry entry in instances)
            {
                if (entry.Key is not string instanceName || entry.Value is not InstanceData instanceData ||
                    !TryGetEngineMetadata(instanceName, out EngineMetadata metadata))
                {
                    continue;
                }

                _previousEngineSamples[instanceName] = instanceData.Sample;
                foundAdapter = true;
            }

            return foundAdapter;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadEngineUsage(out Dictionary<AdapterKey, float> usageByAdapter)
    {
        usageByAdapter = new Dictionary<AdapterKey, float>();
        if (_engineCategory == null)
            return false;

        try
        {
            InstanceDataCollectionCollection categoryData = _engineCategory.ReadCategory();
            InstanceDataCollection? instances = FindCounter(categoryData, EngineUtilizationCounterName);
            if (instances == null || instances.Count > MaxEngineInstances)
                return false;

            var engineTotals = new Dictionary<EngineKey, float>();
            var currentInstances = new HashSet<string>(StringComparer.Ordinal);

            foreach (DictionaryEntry entry in instances)
            {
                if (entry.Key is not string instanceName || entry.Value is not InstanceData instanceData ||
                    !TryGetEngineMetadata(instanceName, out EngineMetadata metadata))
                {
                    continue;
                }

                currentInstances.Add(instanceName);
                usageByAdapter.TryAdd(metadata.Adapter, 0);

                CounterSample currentSample = instanceData.Sample;
                if (_previousEngineSamples.TryGetValue(instanceName, out CounterSample previousSample) &&
                    currentSample.RawValue >= previousSample.RawValue)
                {
                    try
                    {
                        float value = CounterSample.Calculate(previousSample, currentSample);
                        if (float.IsFinite(value) && value >= 0)
                        {
                            var key = new EngineKey(metadata.Adapter, metadata.EngineIndex);
                            engineTotals.TryGetValue(key, out float total);
                            engineTotals[key] = total + value;
                        }
                    }
                    catch
                    {
                    }
                }

                _previousEngineSamples[instanceName] = currentSample;
            }

            foreach ((EngineKey engine, float total) in engineTotals)
            {
                float clampedTotal = Math.Clamp(total, 0, 100);
                if (!usageByAdapter.TryGetValue(engine.Adapter, out float busiestEngine) ||
                    clampedTotal > busiestEngine)
                {
                    usageByAdapter[engine.Adapter] = clampedTotal;
                }
            }

            _readCount++;
            if (_readCount % CachePruneInterval == 0)
                PruneEngineCache(currentInstances);

            return true;
        }
        catch
        {
            usageByAdapter.Clear();
            return false;
        }
    }

    private bool TryReadDedicatedMemory(out Dictionary<AdapterKey, long> memoryByAdapter)
    {
        memoryByAdapter = new Dictionary<AdapterKey, long>();
        if (_memoryCategory == null)
            return false;

        try
        {
            InstanceDataCollectionCollection categoryData = _memoryCategory.ReadCategory();
            InstanceDataCollection? instances = FindCounter(categoryData, DedicatedUsageCounterName);
            if (instances == null || instances.Count > MaxMemoryInstances)
                return false;

            foreach (DictionaryEntry entry in instances)
            {
                if (entry.Key is not string instanceName || entry.Value is not InstanceData instanceData ||
                    !TryParseAdapterKey(instanceName, out AdapterKey adapter))
                {
                    continue;
                }

                long rawValue = instanceData.RawValue;
                if (rawValue < 0 || rawValue > MaxPlausibleMemoryBytes)
                    continue;

                memoryByAdapter.TryGetValue(adapter, out long existing);
                if (existing > MaxPlausibleMemoryBytes - rawValue)
                    continue;

                memoryByAdapter[adapter] = existing + rawValue;
            }

            return true;
        }
        catch
        {
            memoryByAdapter.Clear();
            return false;
        }
    }

    private void TryRestoreMissingCategory()
    {
        if (Stopwatch.GetTimestamp() < _nextRetryTimestamp)
            return;

        bool stillMissing = false;

        if (_engineCategory == null)
        {
            try
            {
                if (PerformanceCounterCategory.Exists(EngineCategoryName))
                {
                    _engineCategory = new PerformanceCounterCategory(EngineCategoryName);
                    if (!TryPrimeEngineSamples())
                        _engineCategory = null;
                }
            }
            catch
            {
                _engineCategory = null;
            }

            stillMissing |= _engineCategory == null;
        }

        if (_memoryCategory == null)
        {
            try
            {
                if (PerformanceCounterCategory.Exists(MemoryCategoryName))
                {
                    _memoryCategory = new PerformanceCounterCategory(MemoryCategoryName);
                    if (!TryReadDedicatedMemory(out Dictionary<AdapterKey, long> memory) || memory.Count == 0)
                        _memoryCategory = null;
                }
            }
            catch
            {
                _memoryCategory = null;
            }

            stillMissing |= _memoryCategory == null;
        }

        if (stillMissing)
            ScheduleRetry();
        else
            _nextRetryTimestamp = 0;
    }

    private void RegisterEngineFailure()
    {
        if (_engineCategory == null)
            return;

        _engineReadFailures++;
        if (_engineReadFailures < MaxConsecutiveFailures)
            return;

        _engineCategory = null;
        _engineReadFailures = 0;
        _engineMetadata.Clear();
        _previousEngineSamples.Clear();
        ScheduleRetry();
    }

    private void RegisterMemoryFailure()
    {
        if (_memoryCategory == null)
            return;

        _memoryReadFailures++;
        if (_memoryReadFailures < MaxConsecutiveFailures)
            return;

        _memoryCategory = null;
        _memoryReadFailures = 0;
        ScheduleRetry();
    }

    private void RegisterReadFailure()
    {
        _healthy = false;
        _consecutiveReadFailures++;
        if (_consecutiveReadFailures >= MaxConsecutiveFailures)
            ResetCategories(scheduleRetry: true);
    }

    private void ResetCategories(bool scheduleRetry)
    {
        _engineCategory = null;
        _memoryCategory = null;
        _engineMetadata.Clear();
        _previousEngineSamples.Clear();
        _smoothedUsage.Clear();
        _availableDevices = [];
        _selectedAdapter = null;
        _categoriesReady = false;
        _healthy = false;
        _consecutiveReadFailures = 0;
        _engineReadFailures = 0;
        _memoryReadFailures = 0;

        if (scheduleRetry)
            ScheduleRetry();
    }

    private void ScheduleRetry() =>
        _nextRetryTimestamp = Stopwatch.GetTimestamp() + RetryDelayStopwatchTicks;

    private AdapterKey SelectAdapter(
        HashSet<AdapterKey> adapters,
        Dictionary<AdapterKey, float> usageByAdapter,
        Dictionary<AdapterKey, long> memoryByAdapter)
    {
        if (!string.IsNullOrWhiteSpace(_preferredDeviceIdentifier))
        {
            foreach (AdapterKey adapter in adapters)
            {
                if (string.Equals(
                        adapter.Identifier,
                        _preferredDeviceIdentifier,
                        StringComparison.Ordinal))
                {
                    return adapter;
                }
            }
        }

        using HashSet<AdapterKey>.Enumerator enumerator = adapters.GetEnumerator();
        enumerator.MoveNext();
        AdapterKey best = enumerator.Current;
        while (enumerator.MoveNext())
        {
            if (IsMoreActive(enumerator.Current, best, usageByAdapter, memoryByAdapter))
                best = enumerator.Current;
        }

        if (!_selectedAdapter.HasValue || !adapters.Contains(_selectedAdapter.Value) ||
            _selectedAdapter.Value.Equals(best))
        {
            return best;
        }

        AdapterKey current = _selectedAdapter.Value;
        usageByAdapter.TryGetValue(current, out float currentUsage);
        usageByAdapter.TryGetValue(best, out float bestUsage);
        if (bestUsage > currentUsage + 5f)
            return best;

        memoryByAdapter.TryGetValue(current, out long currentMemory);
        memoryByAdapter.TryGetValue(best, out long bestMemory);
        if (currentUsage < 1f && bestUsage < 1f && bestMemory > currentMemory + 256L * 1024L * 1024L)
            return best;

        return current;
    }

    private static bool IsMoreActive(
        AdapterKey candidate,
        AdapterKey current,
        Dictionary<AdapterKey, float> usageByAdapter,
        Dictionary<AdapterKey, long> memoryByAdapter)
    {
        usageByAdapter.TryGetValue(candidate, out float candidateUsage);
        usageByAdapter.TryGetValue(current, out float currentUsage);
        if (Math.Abs(candidateUsage - currentUsage) > 0.01f)
            return candidateUsage > currentUsage;

        memoryByAdapter.TryGetValue(candidate, out long candidateMemory);
        memoryByAdapter.TryGetValue(current, out long currentMemory);
        if (candidateMemory != currentMemory)
            return candidateMemory > currentMemory;

        int highComparison = candidate.LuidHigh.CompareTo(current.LuidHigh);
        if (highComparison != 0)
            return highComparison < 0;

        int lowComparison = candidate.LuidLow.CompareTo(current.LuidLow);
        if (lowComparison != 0)
            return lowComparison < 0;

        return candidate.PhysicalAdapter < current.PhysicalAdapter;
    }

    private bool TryGetEngineMetadata(string instanceName, out EngineMetadata metadata)
    {
        if (_engineMetadata.TryGetValue(instanceName, out metadata))
            return true;

        if (!TryParseEngineKey(instanceName, out AdapterKey adapter, out int engineIndex))
        {
            metadata = default;
            return false;
        }

        metadata = new EngineMetadata(adapter, engineIndex);
        _engineMetadata[instanceName] = metadata;
        return true;
    }

    private static bool TryParseAdapterKey(string instanceName, out AdapterKey adapter)
    {
        string[] tokens = instanceName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return TryParseAdapterTokens(tokens, out adapter);
    }

    private static bool TryParseEngineKey(
        string instanceName,
        out AdapterKey adapter,
        out int engineIndex)
    {
        string[] tokens = instanceName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        int enginePosition = FindToken(tokens, "eng");
        if (enginePosition < 0 || enginePosition + 1 >= tokens.Length ||
            FindToken(tokens, "engtype") < 0 ||
            !int.TryParse(
                tokens[enginePosition + 1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out engineIndex) ||
            engineIndex < 0 || !TryParseAdapterTokens(tokens, out adapter))
        {
            adapter = default;
            engineIndex = 0;
            return false;
        }

        return true;
    }

    private static bool TryParseAdapterTokens(string[] tokens, out AdapterKey adapter)
    {
        int luidPosition = FindToken(tokens, "luid");
        int physicalPosition = FindToken(tokens, "phys");
        if (luidPosition < 0 || luidPosition + 2 >= tokens.Length ||
            physicalPosition < 0 || physicalPosition + 1 >= tokens.Length ||
            !TryParseHexUInt32(tokens[luidPosition + 1], out uint luidHigh) ||
            !TryParseHexUInt32(tokens[luidPosition + 2], out uint luidLow) ||
            !int.TryParse(tokens[physicalPosition + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int physical) ||
            physical < 0)
        {
            adapter = default;
            return false;
        }

        adapter = new AdapterKey(luidHigh, luidLow, physical);
        return true;
    }

    private static int FindToken(string[] tokens, string value)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(tokens[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static bool TryParseHexUInt32(string value, out uint result)
    {
        ReadOnlySpan<char> span = value.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            span = span[2..];

        return uint.TryParse(span, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);
    }

    internal static InstanceDataCollection? FindCounter(
        InstanceDataCollectionCollection categoryData,
        string counterName)
    {
        foreach (object key in categoryData.Keys)
        {
            if (key is string name && string.Equals(name, counterName, StringComparison.OrdinalIgnoreCase))
                return categoryData[name];
        }

        return null;
    }

    private void PruneEngineCache(HashSet<string> currentInstances)
    {
        var staleInstances = new List<string>();
        foreach (string instanceName in _previousEngineSamples.Keys)
        {
            if (!currentInstances.Contains(instanceName))
                staleInstances.Add(instanceName);
        }

        foreach (string instanceName in staleInstances)
        {
            _previousEngineSamples.Remove(instanceName);
            _engineMetadata.Remove(instanceName);
        }
    }

    private void PruneAdapterState(HashSet<AdapterKey> currentAdapters)
    {
        if (_readCount % CachePruneInterval != 0)
            return;

        var staleAdapters = new List<AdapterKey>();
        foreach (AdapterKey adapter in _smoothedUsage.Keys)
        {
            if (!currentAdapters.Contains(adapter))
                staleAdapters.Add(adapter);
        }

        foreach (AdapterKey adapter in staleAdapters)
            _smoothedUsage.Remove(adapter);
    }
}
