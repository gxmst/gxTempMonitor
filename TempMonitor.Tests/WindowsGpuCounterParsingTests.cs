using Xunit;

namespace TempMonitor.Tests;

public sealed class WindowsGpuCounterParsingTests
{
    private const string FirstProcess =
        "pid_26052_luid_0x00000000_0x00010D92_phys_0_eng_7_engtype_3D";
    private const string SecondProcess =
        "pid_9120_luid_0x00000000_0x00010D92_phys_0_eng_7_engtype_3D";

    [Fact]
    public void EngineGroupKey_GroupsDifferentProcessesOnTheSamePhysicalEngine()
    {
        Assert.True(WindowsGpuCounterMonitor.TryGetEngineGroupKey(FirstProcess, out string first));
        Assert.True(WindowsGpuCounterMonitor.TryGetEngineGroupKey(SecondProcess, out string second));

        Assert.Equal("luid_0x00000000_0x00010D92_phys_0_eng_7", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AdapterGroupKey_ExtractsLuidAndPhysicalAdapter()
    {
        Assert.True(WindowsGpuCounterMonitor.TryGetAdapterGroupKey(FirstProcess, out string key));
        Assert.Equal("luid_0x00000000_0x00010D92_phys_0", key);
    }

    [Theory]
    [InlineData("pid_123_eng_0_engtype_3D")]
    [InlineData("pid_123_luid_bad_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_123_luid_0x00000000_0x00000001_phys_0_engtype_3D")]
    public void EngineGroupKey_RejectsMalformedInstances(string instance)
    {
        Assert.False(WindowsGpuCounterMonitor.TryGetEngineGroupKey(instance, out _));
    }
}
