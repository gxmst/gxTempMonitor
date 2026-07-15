using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TempMonitor;

internal readonly record struct AlertEvaluation(
    bool IsActive,
    bool BecameActive,
    bool BecameInactive,
    bool ShouldNotify,
    string Message);

/// <summary>
/// Converts noisy samples into a stable alert state. A condition must remain over
/// its threshold for the configured duration and must drop below a lower recovery
/// threshold before it clears.
/// </summary>
internal sealed class AlertEngine
{
    [Flags]
    private enum AlertCondition
    {
        None = 0,
        Cpu = 1,
        GpuTemperature = 2,
        Ram = 4
    }

    private long? _cpuCandidateSince;
    private long? _gpuCandidateSince;
    private long? _ramCandidateSince;
    private long? _lastNotificationTimestamp;
    private AlertCondition _activeConditions;

    public AlertEvaluation Evaluate(HardwareSnapshot snapshot, AppConfig config, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);

        if (!config.EnableAlerts)
        {
            bool wasActive = _activeConditions != AlertCondition.None;
            Reset();
            return new AlertEvaluation(false, false, wasActive, false, string.Empty);
        }

        AlertCondition previousConditions = _activeConditions;
        AlertCondition newlyActivated = AlertCondition.None;
        UpdateCondition(
            AlertCondition.Cpu,
            snapshot.HasCpuUsage,
            snapshot.CpuUsage,
            config.CpuUsageAlertThreshold,
            config,
            timestamp,
            ref _cpuCandidateSince,
            ref newlyActivated);
        UpdateCondition(
            AlertCondition.GpuTemperature,
            snapshot.GpuTemperature.HasValue,
            snapshot.GpuTemperature ?? 0,
            config.GpuTemperatureAlertThreshold,
            config,
            timestamp,
            ref _gpuCandidateSince,
            ref newlyActivated);
        UpdateCondition(
            AlertCondition.Ram,
            snapshot.HasRamData,
            snapshot.RamUsagePercent,
            config.RamUsageAlertThreshold,
            config,
            timestamp,
            ref _ramCandidateSince,
            ref newlyActivated);

        bool becameActive = newlyActivated != AlertCondition.None;
        bool becameInactive = previousConditions != AlertCondition.None &&
                              _activeConditions == AlertCondition.None;
        bool shouldNotify = false;
        if (becameActive)
        {
            TimeSpan cooldown = TimeSpan.FromSeconds(config.AlertCooldownSeconds);
            if (!_lastNotificationTimestamp.HasValue ||
                Stopwatch.GetElapsedTime(_lastNotificationTimestamp.Value, timestamp) >= cooldown)
            {
                shouldNotify = true;
                _lastNotificationTimestamp = timestamp;
            }
        }

        string message = BuildMessage(snapshot);
        return new AlertEvaluation(
            _activeConditions != AlertCondition.None,
            becameActive,
            becameInactive,
            shouldNotify,
            message);
    }

    public void Reset()
    {
        _cpuCandidateSince = null;
        _gpuCandidateSince = null;
        _ramCandidateSince = null;
        _lastNotificationTimestamp = null;
        _activeConditions = AlertCondition.None;
    }

    private void UpdateCondition(
        AlertCondition condition,
        bool hasValue,
        float value,
        int threshold,
        AppConfig config,
        long timestamp,
        ref long? candidateSince,
        ref AlertCondition newlyActivated)
    {
        if ((_activeConditions & condition) != 0)
        {
            if (!hasValue || value < threshold - config.AlertHysteresis)
                _activeConditions &= ~condition;
            return;
        }

        if (!hasValue || value < threshold)
        {
            candidateSince = null;
            return;
        }

        candidateSince ??= timestamp;
        if (Stopwatch.GetElapsedTime(candidateSince.Value, timestamp) >=
            TimeSpan.FromSeconds(config.AlertSustainSeconds))
        {
            _activeConditions |= condition;
            newlyActivated |= condition;
            candidateSince = null;
        }
    }

    private string BuildMessage(HardwareSnapshot snapshot)
    {
        var triggered = new List<string>(3);
        if ((_activeConditions & AlertCondition.Cpu) != 0)
            triggered.Add($"CPU {snapshot.CpuUsage:0}%");
        if ((_activeConditions & AlertCondition.GpuTemperature) != 0 &&
            snapshot.GpuTemperature.HasValue)
        {
            triggered.Add($"GPU {snapshot.GpuTemperature.Value:0}°C");
        }
        if ((_activeConditions & AlertCondition.Ram) != 0)
            triggered.Add($"内存 {snapshot.RamUsagePercent:0}%");

        return string.Join("；", triggered);
    }
}
