using System;
using Xunit;

namespace TempMonitor.Tests;

public sealed class HistoryAndDiagnosticsTests
{
    [Fact]
    public void SelectRange_FiltersInclusivelyAndPreservesEndpointsWhenDownsampling()
    {
        DateTime start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        HardwareSnapshot[] snapshots = CreateSnapshots(start, 10);

        HardwareSnapshot[] result = SnapshotHistory.SelectRange(
            snapshots,
            start.AddSeconds(1),
            start.AddSeconds(8),
            maxPoints: 4);

        Assert.Equal(4, result.Length);
        Assert.Equal(start.AddSeconds(1), result[0].Timestamp);
        Assert.Equal(start.AddSeconds(8), result[^1].Timestamp);
        Assert.Equal(new[] { 1f, 3f, 6f, 8f }, Array.ConvertAll(result, snapshot => snapshot.CpuUsage));
    }

    [Fact]
    public void SelectRange_OnePointReturnsNewestMatchingSample()
    {
        DateTime start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        HardwareSnapshot[] snapshots = CreateSnapshots(start, 5);

        HardwareSnapshot[] result = SnapshotHistory.SelectRange(
            snapshots,
            start.AddSeconds(1),
            start.AddSeconds(3),
            maxPoints: 1);

        HardwareSnapshot sample = Assert.Single(result);
        Assert.Equal(start.AddSeconds(3), sample.Timestamp);
    }

    [Fact]
    public void SelectRange_RejectsInvalidLimitsAndRanges()
    {
        DateTime timestamp = new(2026, 1, 1);
        HardwareSnapshot[] snapshots = CreateSnapshots(timestamp, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SnapshotHistory.SelectRange(snapshots, null, null, 0));
        Assert.Throws<ArgumentException>(() =>
            SnapshotHistory.SelectRange(snapshots, timestamp.AddSeconds(1), timestamp, 10));
    }

    [Fact]
    public void SelectMonotonicRange_IsUnaffectedByWallClockRollback()
    {
        DateTime displayed = new(2026, 11, 1, 1, 59, 59, DateTimeKind.Local);
        HardwareSnapshot[] snapshots =
        [
            new() { Timestamp = displayed, MonotonicTimestamp = 100 },
            new() { Timestamp = displayed.AddHours(-1), MonotonicTimestamp = 200 },
            new() { Timestamp = displayed.AddHours(-1).AddSeconds(1), MonotonicTimestamp = 300 }
        ];

        HardwareSnapshot[] result = SnapshotHistory.SelectMonotonicRange(
            snapshots,
            startInclusive: 200,
            endInclusive: 300,
            maxPoints: 10);

        Assert.Equal(2, result.Length);
        Assert.Same(snapshots[1], result[0]);
        Assert.Same(snapshots[2], result[1]);
        Assert.Equal(200, result[0].MonotonicTimestamp);
        Assert.Equal(300, result[1].MonotonicTimestamp);
        Assert.True(result[0].Timestamp < snapshots[0].Timestamp);
    }

    [Fact]
    public void SelectRange_DownsamplesLargeInputsDirectlyAndPreservesEndpoints()
    {
        DateTime start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        HardwareSnapshot[] snapshots = CreateSnapshots(start, 10_000);

        HardwareSnapshot[] result = SnapshotHistory.SelectRange(
            snapshots,
            start,
            start.AddSeconds(9_999),
            maxPoints: 64);

        Assert.Equal(64, result.Length);
        Assert.Same(snapshots[0], result[0]);
        Assert.Same(snapshots[^1], result[^1]);
    }

    [Fact]
    public void DiagnosticReport_RedactsIdentifiersAndOmitsProcessDetails()
    {
        var snapshot = new HardwareSnapshot
        {
            CpuName = "Example CPU",
            CpuArchitecture = "X64",
            LogicalProcessorCount = 8,
            SystemUptime = TimeSpan.FromHours(2),
            GpuProviderName = "Windows",
            GpuDeviceName = "Windows GPU (luid_0x00000000_0x0000ABCD_phys_0)",
            GpuCapabilities = GpuMetricCapabilities.Temperature | GpuMetricCapabilities.Usage,
            NetworkInterfaceName = "Private adapter name",
            TopGpuProcess = "secret-process.exe (100MB)",
            SamplingDurationMilliseconds = 1.25
        };

        string report = DiagnosticReportBuilder.Build(
            snapshot,
            samplingIntervalMilliseconds: 1000,
            historyCount: 42,
            ["network-interface:{private-adapter-id}", "metric-cpu"]);

        Assert.Contains("network-interface:[redacted]", report, StringComparison.Ordinal);
        Assert.Contains("CPU temperature: not collected by design", report, StringComparison.Ordinal);
        Assert.Contains("GPU device: Windows GPU (adapter identifier redacted)", report, StringComparison.Ordinal);
        Assert.Contains("GPU usage: available", report, StringComparison.Ordinal);
        Assert.Contains("GPU temperature: available", report, StringComparison.Ordinal);
        Assert.DoesNotContain("luid_", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0000ABCD", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-adapter-id", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private adapter name", report, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-process.exe", report, StringComparison.Ordinal);
    }

    private static HardwareSnapshot[] CreateSnapshots(DateTime start, int count)
    {
        var snapshots = new HardwareSnapshot[count];
        for (int index = 0; index < count; index++)
        {
            snapshots[index] = new HardwareSnapshot
            {
                Timestamp = start.AddSeconds(index),
                TimestampUtc = new DateTimeOffset(start.AddSeconds(index)).ToUniversalTime(),
                MonotonicTimestamp = 1_000 + index,
                CpuUsage = index
            };
        }

        return snapshots;
    }
}
