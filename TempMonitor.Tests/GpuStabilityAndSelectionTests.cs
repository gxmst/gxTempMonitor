using Xunit;

namespace TempMonitor.Tests;

public sealed class GpuStabilityAndSelectionTests
{
    [Fact]
    public void SelectionOptions_NormalizesDevicePairsAsOneCoherentValue()
    {
        MonitoringSelectionOptions partialGpu = MonitoringSelectionOptions.Create(
            "NVIDIA",
            null,
            NetworkSelectionMode.Fixed,
            null);

        Assert.Null(partialGpu.GpuProvider);
        Assert.Null(partialGpu.GpuDeviceIdentifier);
        Assert.Equal(NetworkSelectionMode.Auto, partialGpu.NetworkMode);
        Assert.Null(partialGpu.NetworkInterfaceId);

        MonitoringSelectionOptions fixedDevices = MonitoringSelectionOptions.Create(
            " NVIDIA ",
            " GPU-123 ",
            NetworkSelectionMode.Fixed,
            " adapter-1 ");

        Assert.Equal("NVIDIA", fixedDevices.GpuProvider);
        Assert.Equal("GPU-123", fixedDevices.GpuDeviceIdentifier);
        Assert.Equal(NetworkSelectionMode.Fixed, fixedDevices.NetworkMode);
        Assert.Equal("adapter-1", fixedDevices.NetworkInterfaceId);
    }

    [Fact]
    public void Capabilities_KeepPrimaryMetricAcrossIsolatedMissingSamples()
    {
        var stabilizer = new GpuPresentationStabilizer();
        StableGpuPresentation initial = stabilizer.Update("NVIDIA", Reading("gpu-0", 70, 40));

        Assert.Equal(GpuPrimaryMetric.Temperature, initial.PrimaryMetric);
        Assert.True(initial.Capabilities.HasFlag(GpuMetricCapabilities.Temperature));
        Assert.True(initial.Capabilities.HasFlag(GpuMetricCapabilities.Usage));

        StableGpuPresentation firstMiss = stabilizer.Update("NVIDIA", Reading("gpu-0", null, 45));
        StableGpuPresentation secondMiss = stabilizer.Update("NVIDIA", Reading("gpu-0", null, 50));

        Assert.Equal(GpuPrimaryMetric.Temperature, firstMiss.PrimaryMetric);
        Assert.Equal(GpuPrimaryMetric.Temperature, secondMiss.PrimaryMetric);
        Assert.True(secondMiss.Capabilities.HasFlag(GpuMetricCapabilities.Temperature));

        StableGpuPresentation thirdMiss = stabilizer.Update("NVIDIA", Reading("gpu-0", null, 55));
        Assert.False(thirdMiss.Capabilities.HasFlag(GpuMetricCapabilities.Temperature));
        Assert.Equal(GpuPrimaryMetric.Usage, thirdMiss.PrimaryMetric);
    }

    [Fact]
    public void RemovedCapability_RequiresConfirmationBeforeRestoringPrimaryMetric()
    {
        var stabilizer = new GpuPresentationStabilizer();
        stabilizer.Update("NVIDIA", Reading("gpu-0", 70, 40));
        for (int index = 0; index < GpuPresentationStabilizer.MissingSamplesBeforeRemoval; index++)
            stabilizer.Update("NVIDIA", Reading("gpu-0", null, 40));

        StableGpuPresentation firstReturn = stabilizer.Update("NVIDIA", Reading("gpu-0", 71, 40));
        StableGpuPresentation confirmedReturn = stabilizer.Update("NVIDIA", Reading("gpu-0", 72, 40));

        Assert.False(firstReturn.Capabilities.HasFlag(GpuMetricCapabilities.Temperature));
        Assert.Equal(GpuPrimaryMetric.Usage, firstReturn.PrimaryMetric);
        Assert.True(confirmedReturn.Capabilities.HasFlag(GpuMetricCapabilities.Temperature));
        Assert.Equal(GpuPrimaryMetric.Temperature, confirmedReturn.PrimaryMetric);
    }

    [Fact]
    public void EmptyReadings_RetainCapabilitiesWithoutInventingSensorValues()
    {
        var stabilizer = new GpuPresentationStabilizer();
        stabilizer.Update("NVIDIA", Reading("gpu-0", 70, 40));

        StableGpuPresentation firstMiss = stabilizer.Update("NVIDIA", GpuReading.Empty);
        StableGpuPresentation secondMiss = stabilizer.Update("NVIDIA", GpuReading.Empty);

        Assert.Equal(GpuPrimaryMetric.Temperature, firstMiss.PrimaryMetric);
        Assert.Equal(GpuPrimaryMetric.Temperature, secondMiss.PrimaryMetric);
        Assert.Null(GpuReading.Empty.Temperature);
        Assert.Null(GpuReading.Empty.Usage);

        StableGpuPresentation thirdMiss = stabilizer.Update("NVIDIA", GpuReading.Empty);
        Assert.Equal(GpuMetricCapabilities.None, thirdMiss.Capabilities);
        Assert.Equal(GpuPrimaryMetric.None, thirdMiss.PrimaryMetric);
    }

    [Fact]
    public void DeviceChange_ImmediatelyResetsCapabilitiesForTheNewIdentity()
    {
        var stabilizer = new GpuPresentationStabilizer();
        stabilizer.Update("NVIDIA", Reading("gpu-0", 70, 40));

        StableGpuPresentation changed = stabilizer.Update("NVIDIA", Reading("gpu-1", null, 60));

        Assert.Equal(GpuMetricCapabilities.Usage, changed.Capabilities);
        Assert.Equal(GpuPrimaryMetric.Usage, changed.PrimaryMetric);
    }

    [Fact]
    public void ProviderRefresh_IsSafeBeforeInitializationAndAfterDisposal()
    {
        IGpuMonitor[] monitors =
        [
            new NvidiaGpuMonitor(),
            new AmdGpuMonitor(),
            new WindowsGpuCounterMonitor()
        ];

        foreach (IGpuMonitor monitor in monitors)
        {
            monitor.RequestDeviceRefresh();
            monitor.RequestDeviceRefresh();
            monitor.Dispose();
            monitor.RequestDeviceRefresh();
        }
    }

    [Fact]
    public void RefreshSessions_ReinitializesAndRemovesOnlyUnacceptedFailures()
    {
        var recovered = new FakeGpuMonitor(initialized: true, initializeResult: true);
        var retryable = new FakeGpuMonitor(initialized: true, initializeResult: false);
        var rejected = new FakeGpuMonitor(initialized: false, initializeResult: false);
        var monitors = new List<IGpuMonitor> { recovered, retryable, rejected };

        HardwareMonitorService.RefreshGpuMonitorSessions(monitors);

        Assert.Equal(2, monitors.Count);
        Assert.Contains(recovered, monitors);
        Assert.Contains(retryable, monitors);
        Assert.DoesNotContain(rejected, monitors);
        Assert.Equal(1, recovered.RefreshRequests);
        Assert.Equal(1, recovered.InitializeRequests);
        Assert.False(recovered.Disposed);
        Assert.False(retryable.Disposed);
        Assert.True(rejected.Disposed);
    }

    private static GpuReading Reading(
        string deviceIdentifier,
        float? temperature,
        float? usage) => new()
        {
            DeviceIdentifier = deviceIdentifier,
            DeviceName = deviceIdentifier,
            Temperature = temperature,
            Usage = usage
        };

    private sealed class FakeGpuMonitor : IGpuMonitor
    {
        private readonly bool _initializeResult;

        public FakeGpuMonitor(bool initialized, bool initializeResult)
        {
            Initialized = initialized;
            _initializeResult = initializeResult;
        }

        public bool Initialized { get; private set; }
        public bool IsHealthy => Initialized;
        public string VendorName => "Fake";
        public IReadOnlyList<GpuDeviceInfo> AvailableDevices => [];
        public int RefreshRequests { get; private set; }
        public int InitializeRequests { get; private set; }
        public bool Disposed { get; private set; }

        public bool TryInitialize()
        {
            InitializeRequests++;
            if (_initializeResult)
                Initialized = true;
            return _initializeResult;
        }

        public void SetPreferredDevice(string? deviceIdentifier)
        {
        }

        public void RequestDeviceRefresh() => RefreshRequests++;

        public GpuReading Read() => GpuReading.Empty;

        public void Dispose()
        {
            Disposed = true;
            Initialized = false;
        }
    }
}
