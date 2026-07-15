using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TempMonitor;

public enum WidgetTheme
{
    Dark,
    Light
}

public enum WidgetMetric
{
    Cpu,
    Gpu,
    Ram,
    Vram,
    Upload,
    Download
}

public enum GpuDisplayMetric
{
    Auto,
    Temperature,
    Usage,
    Power
}

public enum MemoryDisplayMode
{
    Used,
    Percentage
}

public enum NetworkDisplayUnit
{
    Auto,
    BytesPerSecond,
    BitsPerSecond
}

public enum NetworkSelectionMode
{
    Auto,
    Aggregate,
    Fixed
}

public enum AlertPresentation
{
    ColorOnly,
    TrayNotification,
    Flash
}

public enum FullscreenBehavior
{
    StayVisible,
    Hide,
    Dim
}

public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 2;

    [JsonIgnore]
    internal bool IsReadOnlyDueToUnsupportedConfig { get; set; }
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public double Top { get; set; } = 100;
    public double Left { get; set; } = 100;
    public bool IsDockedRight { get; set; } = true;
    public bool IsLocked { get; set; }
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowVram { get; set; } = true;
    public bool ShowUpload { get; set; } = true;
    public bool ShowDownload { get; set; } = true;
    public List<WidgetMetric> MetricOrder { get; set; } =
    [
        WidgetMetric.Cpu,
        WidgetMetric.Gpu,
        WidgetMetric.Ram,
        WidgetMetric.Vram,
        WidgetMetric.Upload,
        WidgetMetric.Download
    ];
    public GpuDisplayMetric GpuDisplayMetric { get; set; } = GpuDisplayMetric.Auto;
    public MemoryDisplayMode RamDisplayMode { get; set; } = MemoryDisplayMode.Used;
    public MemoryDisplayMode VramDisplayMode { get; set; } = MemoryDisplayMode.Used;
    public NetworkDisplayUnit NetworkDisplayUnit { get; set; } = NetworkDisplayUnit.Auto;
    public WidgetTheme Theme { get; set; } = WidgetTheme.Dark;
    public double WidgetOpacity { get; set; } = 0.78;
    public bool AlwaysOnTop { get; set; } = true;
    public FullscreenBehavior FullscreenBehavior { get; set; } = FullscreenBehavior.StayVisible;
    public int DelayedStartSeconds { get; set; }
    public bool EnableGlobalHotkey { get; set; }
    public bool TrackTopGpuProcess { get; set; }
    public bool EnableAlerts { get; set; } = true;
    public AlertPresentation AlertPresentation { get; set; } = AlertPresentation.ColorOnly;
    public int CpuUsageAlertThreshold { get; set; } = 95;
    public int GpuTemperatureAlertThreshold { get; set; } = 85;
    public int RamUsageAlertThreshold { get; set; } = 90;
    public int AlertSustainSeconds { get; set; } = 3;
    public int AlertHysteresis { get; set; } = 5;
    public int AlertCooldownSeconds { get; set; } = 60;
    public int SamplingIntervalSeconds { get; set; } = 1;
    public bool EnableAdaptiveSampling { get; set; } = true;
    public bool HasCompletedOnboarding { get; set; } = true;
    public string? PreferredGpuProvider { get; set; }
    public string? PreferredGpuDeviceIdentifier { get; set; }
    public NetworkSelectionMode NetworkSelectionMode { get; set; } = NetworkSelectionMode.Auto;
    public string? PreferredNetworkInterfaceId { get; set; }

    internal void Normalize()
    {
        if (SchemaVersion <= CurrentSchemaVersion)
            SchemaVersion = CurrentSchemaVersion;
        if (!double.IsFinite(Top)) Top = 100;
        if (!double.IsFinite(Left)) Left = 100;
        if (!Enum.IsDefined(Theme)) Theme = WidgetTheme.Dark;
        if (!Enum.IsDefined(GpuDisplayMetric)) GpuDisplayMetric = GpuDisplayMetric.Auto;
        if (!Enum.IsDefined(RamDisplayMode)) RamDisplayMode = MemoryDisplayMode.Used;
        if (!Enum.IsDefined(VramDisplayMode)) VramDisplayMode = MemoryDisplayMode.Used;
        if (!Enum.IsDefined(NetworkDisplayUnit)) NetworkDisplayUnit = NetworkDisplayUnit.Auto;
        if (!Enum.IsDefined(NetworkSelectionMode)) NetworkSelectionMode = NetworkSelectionMode.Auto;
        if (!Enum.IsDefined(AlertPresentation)) AlertPresentation = AlertPresentation.ColorOnly;
        if (!Enum.IsDefined(FullscreenBehavior)) FullscreenBehavior = FullscreenBehavior.StayVisible;

        WidgetOpacity = double.IsFinite(WidgetOpacity)
            ? Math.Clamp(WidgetOpacity, 0.50, 0.95)
            : 0.78;
        DelayedStartSeconds = Math.Clamp(DelayedStartSeconds, 0, 60);
        SamplingIntervalSeconds = SamplingIntervalSeconds is 1 or 2 or 5
            ? SamplingIntervalSeconds
            : 1;
        CpuUsageAlertThreshold = Math.Clamp(CpuUsageAlertThreshold, 50, 100);
        GpuTemperatureAlertThreshold = Math.Clamp(GpuTemperatureAlertThreshold, 40, 120);
        RamUsageAlertThreshold = Math.Clamp(RamUsageAlertThreshold, 50, 100);
        AlertSustainSeconds = Math.Clamp(AlertSustainSeconds, 0, 60);
        AlertHysteresis = Math.Clamp(AlertHysteresis, 1, 20);
        AlertCooldownSeconds = Math.Clamp(AlertCooldownSeconds, 0, 3600);

        PreferredGpuProvider = NormalizeIdentifier(PreferredGpuProvider, 64);
        PreferredGpuDeviceIdentifier = NormalizeIdentifier(PreferredGpuDeviceIdentifier, 512);
        PreferredNetworkInterfaceId = NormalizeIdentifier(PreferredNetworkInterfaceId, 512);
        if (NetworkSelectionMode == NetworkSelectionMode.Fixed &&
            string.IsNullOrWhiteSpace(PreferredNetworkInterfaceId))
        {
            NetworkSelectionMode = NetworkSelectionMode.Auto;
        }

        NormalizeMetricOrder();
    }

    internal bool TryMigrateFrom(int sourceSchemaVersion)
    {
        if (sourceSchemaVersion <= 0 || sourceSchemaVersion > CurrentSchemaVersion)
            return false;

        if (sourceSchemaVersion < 2)
        {
            // Existing installations should not see first-run help after an upgrade.
            HasCompletedOnboarding = true;
            // Older versions only supported immediate flashing. The safer new default
            // keeps the rule enabled while reducing disruption.
            AlertPresentation = AlertPresentation.ColorOnly;
        }

        Normalize();
        return true;
    }

    internal AppConfig Clone()
    {
        var clone = (AppConfig)MemberwiseClone();
        clone.MetricOrder = MetricOrder is null ? [] : [.. MetricOrder];
        return clone;
    }

    internal bool IsMetricVisible(WidgetMetric metric) => metric switch
    {
        WidgetMetric.Cpu => ShowCpu,
        WidgetMetric.Gpu => ShowGpu,
        WidgetMetric.Ram => ShowRam,
        WidgetMetric.Vram => ShowVram,
        WidgetMetric.Upload => ShowUpload,
        WidgetMetric.Download => ShowDownload,
        _ => false
    };

    internal void SetMetricVisible(WidgetMetric metric, bool visible)
    {
        switch (metric)
        {
            case WidgetMetric.Cpu:
                ShowCpu = visible;
                break;
            case WidgetMetric.Gpu:
                ShowGpu = visible;
                break;
            case WidgetMetric.Ram:
                ShowRam = visible;
                break;
            case WidgetMetric.Vram:
                ShowVram = visible;
                break;
            case WidgetMetric.Upload:
                ShowUpload = visible;
                break;
            case WidgetMetric.Download:
                ShowDownload = visible;
                break;
        }
    }

    private void NormalizeMetricOrder()
    {
        MetricOrder ??= [];
        var normalized = new List<WidgetMetric>(Enum.GetValues<WidgetMetric>().Length);
        foreach (WidgetMetric metric in MetricOrder)
        {
            if (Enum.IsDefined(metric) && !normalized.Contains(metric))
                normalized.Add(metric);
        }

        foreach (WidgetMetric metric in Enum.GetValues<WidgetMetric>())
        {
            if (!normalized.Contains(metric))
                normalized.Add(metric);
        }

        MetricOrder = normalized;

        // An empty widget is very hard to recover through its own context menu.
        if (!MetricOrder.Any(IsMetricVisible))
            ShowCpu = true;
    }

    private static string? NormalizeIdentifier(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string normalized = new(value
            .Trim()
            .Where(character => !char.IsControl(character))
            .Take(maximumLength)
            .ToArray());
        return normalized.Length == 0 ? null : normalized;
    }
}

internal static class AppPaths
{
    private const string AppDirectoryName = "gxTempMonitor";

    public static string DataDirectory { get; } = BuildDataDirectory();
    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public static string LogPath => Path.Combine(DataDirectory, "TempMonitor.log");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);

    public static string? GetLegacyConfigPath()
    {
        string executablePath = Environment.ProcessPath ?? string.Empty;
        string? executableDirectory = Path.GetDirectoryName(executablePath);
        return string.IsNullOrWhiteSpace(executableDirectory)
            ? null
            : Path.Combine(executableDirectory, "config.json");
    }

    private static string BuildDataDirectory()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Path.GetTempPath();

        return Path.Combine(localAppData, AppDirectoryName);
    }
}

internal static class ConfigStore
{
    private const int MaxConfigBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load()
    {
        string path = AppPaths.ConfigPath;
        bool isLegacyPath = false;
        if (!File.Exists(path))
        {
            string? legacyPath = AppPaths.GetLegacyConfigPath();
            if (!string.IsNullOrWhiteSpace(legacyPath) && File.Exists(legacyPath))
            {
                path = legacyPath;
                isLegacyPath = true;
            }
        }

        AppConfig config = LoadFromPath(path);
        if (isLegacyPath && !config.IsReadOnlyDueToUnsupportedConfig)
            TrySave(config);

        return config;
    }

    internal static AppConfig LoadFromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (!File.Exists(path))
                return new AppConfig { HasCompletedOnboarding = false };
            if (new FileInfo(path).Length > MaxConfigBytes)
                return CreateReadOnlyFallback();

            string json = File.ReadAllText(path);
            int sourceSchemaVersion = ReadSchemaVersion(json);
            AppConfig config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                ?? new AppConfig();
            if (!config.TryMigrateFrom(sourceSchemaVersion))
                return CreateReadOnlyFallback();

            return config;
        }
        catch (JsonException)
        {
            return CreateReadOnlyFallback();
        }
        catch (IOException)
        {
            return CreateReadOnlyFallback();
        }
        catch (UnauthorizedAccessException)
        {
            return CreateReadOnlyFallback();
        }
        catch (System.Security.SecurityException)
        {
            return CreateReadOnlyFallback();
        }
    }

    public static bool TrySave(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.IsReadOnlyDueToUnsupportedConfig ||
            config.SchemaVersion > AppConfig.CurrentSchemaVersion)
            return false;
        config.Normalize();

        string? temporaryPath = null;
        try
        {
            AppPaths.EnsureDataDirectory();
            temporaryPath = Path.Combine(
                AppPaths.DataDirectory,
                $"config.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(config, JsonOptions));

            if (File.Exists(AppPaths.ConfigPath))
                File.Replace(temporaryPath, AppPaths.ConfigPath, null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, AppPaths.ConfigPath);

            temporaryPath = null;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (System.Security.SecurityException)
                {
                }
            }
        }
    }

    internal static string SerializeForExport(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.IsReadOnlyDueToUnsupportedConfig ||
            config.SchemaVersion > AppConfig.CurrentSchemaVersion)
            throw new InvalidOperationException("An unsupported configuration cannot be exported by this version.");
        AppConfig normalized = config.Clone();
        normalized.Normalize();
        normalized.HasCompletedOnboarding = true;
        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    internal static bool TryParseImport(string json, out AppConfig config)
    {
        config = new AppConfig();
        if (string.IsNullOrWhiteSpace(json) ||
            System.Text.Encoding.UTF8.GetByteCount(json) > MaxConfigBytes)
        {
            return false;
        }

        try
        {
            int sourceSchemaVersion = ReadSchemaVersion(json);
            AppConfig? parsed = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (parsed == null) return false;

            if (!parsed.TryMigrateFrom(sourceSchemaVersion))
                return false;
            parsed.HasCompletedOnboarding = true;
            config = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AppConfig CreateReadOnlyFallback() => new()
    {
        IsReadOnlyDueToUnsupportedConfig = true
    };

    private static int ReadSchemaVersion(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty(nameof(AppConfig.SchemaVersion), out JsonElement versionElement) &&
            versionElement.TryGetInt32(out int version) && version > 0)
        {
            return version;
        }

        return 1;
    }
}
