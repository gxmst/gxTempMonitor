using Xunit;

namespace TempMonitor.Tests;

public sealed class CsvAndProcessParsingTests
{
    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x00000001_phys_0", 1234)]
    [InlineData("pid_4", 4)]
    public void TryParseProcessId_ParsesValidGpuCounterInstances(string instance, int expected)
    {
        Assert.True(HardwareMonitorService.TryParseProcessId(instance, out int actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("pid_invalid_luid_0x0_0x1_phys_0")]
    [InlineData("luid_0x00000000_0x00000001_phys_0")]
    [InlineData("pid_0_luid_0x0_0x1_phys_0")]
    public void TryParseProcessId_RejectsInvalidGpuCounterInstances(string instance)
    {
        Assert.False(HardwareMonitorService.TryParseProcessId(instance, out _));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://invalid.example\")")]
    [InlineData("  +1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A2)")]
    public void EscapeSpreadsheetFormula_PrefixesPotentialFormulas(string value)
    {
        Assert.StartsWith("'", HardwareMonitorService.EscapeSpreadsheetFormula(value));
    }

    [Fact]
    public void EscapeCsv_QuotesAndEscapesSpecialCharacters()
    {
        Assert.Equal("\"gpu, \"\"primary\"\"\"", HardwareMonitorService.EscapeCsv("gpu, \"primary\""));
        Assert.Equal("plain", HardwareMonitorService.EscapeCsv("plain"));
    }
}
