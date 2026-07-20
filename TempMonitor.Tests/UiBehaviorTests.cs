using System.Globalization;
using Xunit;

namespace TempMonitor.Tests;

public sealed class UiBehaviorTests
{
    [Fact]
    public void FormatSpeed_UsesOneDecimalPlaceInEveryUnitRange()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("512.0B", UiHelper.FormatSpeed(512));
            Assert.Equal("1.5K", UiHelper.FormatSpeed(1536));
            Assert.Equal("5.7M", UiHelper.FormatSpeed(5.67f * 1024 * 1024));
            Assert.Equal("250.3M", UiHelper.FormatSpeed(250.34f * 1024 * 1024));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void DashboardAlertBrush_RespectsEnableFlagThresholdAndHysteresis()
    {
        var config = new AppConfig
        {
            EnableAlerts = true,
            AlertHysteresis = 5
        };

        Assert.Same(
            UiHelper.NormalBrush,
            DashboardWindow.ResolveConfiguredAlertBrush(config, 84, 90));
        Assert.Same(
            UiHelper.WarningBrush,
            DashboardWindow.ResolveConfiguredAlertBrush(config, 85, 90));
        Assert.Same(
            UiHelper.CriticalBrush,
            DashboardWindow.ResolveConfiguredAlertBrush(config, 90, 90));

        config.EnableAlerts = false;
        Assert.Same(
            UiHelper.NormalBrush,
            DashboardWindow.ResolveConfiguredAlertBrush(config, 100, 90));
    }

    [Fact]
    public void DashboardTransition_DoesNotCollapseViewThatBecameCurrentAgain()
    {
        var firstView = new object();
        var secondView = new object();

        Assert.False(DashboardWindow.ShouldCollapseAfterTransition(firstView, firstView));
        Assert.True(DashboardWindow.ShouldCollapseAfterTransition(secondView, firstView));
    }

    [Theory]
    [InlineData(FullscreenBehavior.StayVisible, false)]
    [InlineData(FullscreenBehavior.Hide, true)]
    [InlineData(FullscreenBehavior.Dim, true)]
    public void FullscreenMonitoring_RunsOnlyForActiveBehaviors(
        FullscreenBehavior behavior,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldMonitorFullscreen(behavior));
    }

    [Theory]
    [InlineData(0.50, 0.56)]
    [InlineData(0.95, 1.00)]
    public void IdleContentOpacity_StaysWithinWpfOpacityRange(
        double widgetOpacity,
        double expected)
    {
        Assert.Equal(expected, MainWindow.GetIdleContentOpacity(widgetOpacity), precision: 6);
    }
}
