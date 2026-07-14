using Xunit;

namespace TempMonitor.Tests;

public sealed class AppConfigTests
{
    [Fact]
    public void Normalize_ClampsUntrustedValues()
    {
        var config = new AppConfig
        {
            Top = double.NaN,
            Left = double.PositiveInfinity,
            WidgetOpacity = 5,
            DelayedStartSeconds = int.MaxValue,
            SamplingIntervalSeconds = 3,
            Theme = (WidgetTheme)999
        };

        config.Normalize();

        Assert.Equal(100, config.Top);
        Assert.Equal(100, config.Left);
        Assert.Equal(0.95, config.WidgetOpacity);
        Assert.Equal(60, config.DelayedStartSeconds);
        Assert.Equal(1, config.SamplingIntervalSeconds);
        Assert.Equal(WidgetTheme.Dark, config.Theme);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Normalize_PreservesSupportedSamplingIntervals(int seconds)
    {
        var config = new AppConfig { SamplingIntervalSeconds = seconds };

        config.Normalize();

        Assert.Equal(seconds, config.SamplingIntervalSeconds);
    }
}
