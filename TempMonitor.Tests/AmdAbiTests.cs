using Xunit;

namespace TempMonitor.Tests;

public sealed class AmdAbiTests
{
    [Fact]
    public void ManagedAdlLayouts_MatchTheSupportedWindowsX64Abi()
    {
        Assert.True(AmdGpuMonitor.ValidateAbi());
    }
}
