using System;
using System.Collections.Generic;
using System.IO;
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
            Theme = (WidgetTheme)999,
            GpuDisplayMetric = (GpuDisplayMetric)999,
            NetworkSelectionMode = NetworkSelectionMode.Fixed,
            PreferredNetworkInterfaceId = " \r\n ",
            CpuUsageAlertThreshold = 5,
            GpuTemperatureAlertThreshold = 500,
            RamUsageAlertThreshold = 101,
            AlertSustainSeconds = -1,
            AlertHysteresis = 99,
            AlertCooldownSeconds = int.MaxValue,
            MetricOrder =
            [
                WidgetMetric.Gpu,
                WidgetMetric.Gpu,
                (WidgetMetric)999
            ],
            ShowCpu = false,
            ShowGpu = false,
            ShowRam = false,
            ShowVram = false,
            ShowUpload = false,
            ShowDownload = false
        };

        config.Normalize();

        Assert.Equal(100, config.Top);
        Assert.Equal(100, config.Left);
        Assert.Equal(0.95, config.WidgetOpacity);
        Assert.Equal(60, config.DelayedStartSeconds);
        Assert.Equal(1, config.SamplingIntervalSeconds);
        Assert.Equal(WidgetTheme.Dark, config.Theme);
        Assert.Equal(GpuDisplayMetric.Auto, config.GpuDisplayMetric);
        Assert.Equal(NetworkSelectionMode.Auto, config.NetworkSelectionMode);
        Assert.Null(config.PreferredNetworkInterfaceId);
        Assert.Equal(50, config.CpuUsageAlertThreshold);
        Assert.Equal(120, config.GpuTemperatureAlertThreshold);
        Assert.Equal(100, config.RamUsageAlertThreshold);
        Assert.Equal(0, config.AlertSustainSeconds);
        Assert.Equal(20, config.AlertHysteresis);
        Assert.Equal(3600, config.AlertCooldownSeconds);
        Assert.Equal(
            new[]
            {
                WidgetMetric.Gpu,
                WidgetMetric.Cpu,
                WidgetMetric.Ram,
                WidgetMetric.Vram,
                WidgetMetric.Upload,
                WidgetMetric.Download
            },
            config.MetricOrder);
        Assert.True(config.ShowCpu);
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

    [Fact]
    public void MigrateFrom_VersionOneKeepsExistingUsersPastOnboardingAndUsesQuietAlerts()
    {
        var config = new AppConfig
        {
            HasCompletedOnboarding = false,
            AlertPresentation = AlertPresentation.Flash
        };

        Assert.True(config.TryMigrateFrom(1));

        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.True(config.HasCompletedOnboarding);
        Assert.Equal(AlertPresentation.ColorOnly, config.AlertPresentation);
    }

    [Fact]
    public void MigrationAndImport_RejectFutureSchemasWithoutDowngradingThem()
    {
        var config = new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion + 1 };

        Assert.False(config.TryMigrateFrom(config.SchemaVersion));
        Assert.Equal(AppConfig.CurrentSchemaVersion + 1, config.SchemaVersion);
        Assert.False(ConfigStore.TryParseImport(
            "{\"SchemaVersion\":999,\"Theme\":\"Dark\"}",
            out _));
    }

    [Fact]
    public void ExportAndImport_RoundTripsNormalizedSettings()
    {
        var source = new AppConfig
        {
            GpuDisplayMetric = GpuDisplayMetric.Power,
            NetworkSelectionMode = NetworkSelectionMode.Aggregate,
            CpuUsageAlertThreshold = 88,
            MetricOrder = [WidgetMetric.Download, WidgetMetric.Cpu]
        };

        string json = ConfigStore.SerializeForExport(source);

        Assert.True(ConfigStore.TryParseImport(json, out AppConfig imported));
        Assert.Equal(GpuDisplayMetric.Power, imported.GpuDisplayMetric);
        Assert.Equal(NetworkSelectionMode.Aggregate, imported.NetworkSelectionMode);
        Assert.Equal(88, imported.CpuUsageAlertThreshold);
        Assert.Equal(WidgetMetric.Cpu, imported.MetricOrder[1]);
        Assert.True(imported.HasCompletedOnboarding);
    }

    [Fact]
    public void Import_RejectsMalformedAndOversizedJson()
    {
        Assert.False(ConfigStore.TryParseImport("{broken", out _));
        Assert.False(ConfigStore.TryParseImport(new string('x', 300_000), out _));
    }

    [Fact]
    public void Load_OversizedExistingConfigIsReadOnlyAndCannotBeOverwritten()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"gxTempMonitor-oversized-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(path, new byte[(256 * 1024) + 1]);

            AppConfig config = ConfigStore.LoadFromPath(path);

            Assert.True(config.IsReadOnlyDueToUnsupportedConfig);
            Assert.False(ConfigStore.TrySave(config));
            Assert.Equal((256 * 1024) + 1, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"SchemaVersion\":2,\"Theme\":\"FutureTheme\"}")]
    public void Load_UnreadableExistingConfigIsReadOnlyAndCannotBeOverwritten(string original)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"gxTempMonitor-unreadable-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, original);

            AppConfig config = ConfigStore.LoadFromPath(path);

            Assert.True(config.IsReadOnlyDueToUnsupportedConfig);
            Assert.False(ConfigStore.TrySave(config));
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
