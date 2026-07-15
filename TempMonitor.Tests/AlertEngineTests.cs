using System;
using System.Diagnostics;
using Xunit;

namespace TempMonitor.Tests;

public sealed class AlertEngineTests
{
    [Fact]
    public void Evaluate_RequiresSustainedThresholdAndClearsWithHysteresis()
    {
        var engine = new AlertEngine();
        var config = new AppConfig
        {
            EnableAlerts = true,
            CpuUsageAlertThreshold = 90,
            AlertSustainSeconds = 3,
            AlertHysteresis = 5,
            AlertCooldownSeconds = 60
        };
        var high = new HardwareSnapshot { HasCpuUsage = true, CpuUsage = 95 };

        Assert.False(engine.Evaluate(high, config, AtSeconds(0)).IsActive);
        Assert.False(engine.Evaluate(high, config, AtSeconds(2)).IsActive);
        AlertEvaluation activated = engine.Evaluate(high, config, AtSeconds(3));
        Assert.True(activated.IsActive);
        Assert.True(activated.BecameActive);
        Assert.True(activated.ShouldNotify);

        var aboveRecoveryBoundary = new HardwareSnapshot { HasCpuUsage = true, CpuUsage = 86 };
        Assert.True(engine.Evaluate(aboveRecoveryBoundary, config, AtSeconds(4)).IsActive);

        var recovered = new HardwareSnapshot { HasCpuUsage = true, CpuUsage = 84 };
        AlertEvaluation cleared = engine.Evaluate(recovered, config, AtSeconds(5));
        Assert.False(cleared.IsActive);
        Assert.True(cleared.BecameInactive);
    }

    [Fact]
    public void Evaluate_RespectsNotificationCooldownAcrossRetriggers()
    {
        var engine = new AlertEngine();
        var config = new AppConfig
        {
            EnableAlerts = true,
            GpuTemperatureAlertThreshold = 80,
            AlertSustainSeconds = 0,
            AlertHysteresis = 5,
            AlertCooldownSeconds = 60
        };
        var hot = new HardwareSnapshot { GpuTemperature = 85 };
        var cool = new HardwareSnapshot { GpuTemperature = 70 };

        Assert.True(engine.Evaluate(hot, config, AtSeconds(0)).ShouldNotify);
        Assert.True(engine.Evaluate(cool, config, AtSeconds(1)).BecameInactive);
        AlertEvaluation second = engine.Evaluate(hot, config, AtSeconds(10));
        Assert.True(second.IsActive);
        Assert.False(second.ShouldNotify);
        engine.Evaluate(cool, config, AtSeconds(11));
        Assert.True(engine.Evaluate(hot, config, AtSeconds(61)).ShouldNotify);
    }

    [Fact]
    public void Evaluate_DisablingAlertsClearsExistingState()
    {
        var engine = new AlertEngine();
        var config = new AppConfig
        {
            EnableAlerts = true,
            RamUsageAlertThreshold = 90,
            AlertSustainSeconds = 0
        };
        var snapshot = new HardwareSnapshot { HasRamData = true, RamUsagePercent = 95 };
        Assert.True(engine.Evaluate(snapshot, config, AtSeconds(0)).IsActive);

        config.EnableAlerts = false;
        AlertEvaluation disabled = engine.Evaluate(snapshot, config, AtSeconds(1));
        Assert.False(disabled.IsActive);
        Assert.True(disabled.BecameInactive);
    }

    [Fact]
    public void Evaluate_DoesNotCarrySustainTimeAcrossDifferentMetrics()
    {
        var engine = new AlertEngine();
        var config = new AppConfig
        {
            CpuUsageAlertThreshold = 90,
            RamUsageAlertThreshold = 90,
            AlertSustainSeconds = 3
        };

        var cpuHigh = new HardwareSnapshot { HasCpuUsage = true, CpuUsage = 95 };
        var ramHigh = new HardwareSnapshot { HasRamData = true, RamUsagePercent = 95 };
        Assert.False(engine.Evaluate(cpuHigh, config, AtSeconds(0)).IsActive);
        Assert.False(engine.Evaluate(cpuHigh, config, AtSeconds(2)).IsActive);
        Assert.False(engine.Evaluate(ramHigh, config, AtSeconds(2.5)).IsActive);
        Assert.False(engine.Evaluate(ramHigh, config, AtSeconds(5)).IsActive);
        Assert.True(engine.Evaluate(ramHigh, config, AtSeconds(5.5)).IsActive);
    }

    private static long AtSeconds(double seconds) =>
        checked((long)(seconds * Stopwatch.Frequency));
}
