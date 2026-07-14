using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TempMonitor;

public enum WidgetTheme
{
    Dark,
    Light
}

public sealed class AppConfig
{
    public double Top { get; set; } = 100;
    public double Left { get; set; } = 100;
    public bool IsDockedRight { get; set; } = true;
    public bool IsLocked { get; set; }
    public bool ShowRam { get; set; } = true;
    public bool ShowVram { get; set; } = true;
    public bool ShowUpload { get; set; } = true;
    public bool ShowDownload { get; set; } = true;
    public WidgetTheme Theme { get; set; } = WidgetTheme.Dark;
    public double WidgetOpacity { get; set; } = 0.78;
    public int DelayedStartSeconds { get; set; }
    public bool EnableGlobalHotkey { get; set; }
    public bool TrackTopGpuProcess { get; set; }
    public bool EnableAlerts { get; set; } = true;
    public int SamplingIntervalSeconds { get; set; } = 1;

    internal void Normalize()
    {
        if (!double.IsFinite(Top)) Top = 100;
        if (!double.IsFinite(Left)) Left = 100;
        if (!Enum.IsDefined(Theme)) Theme = WidgetTheme.Dark;

        WidgetOpacity = double.IsFinite(WidgetOpacity)
            ? Math.Clamp(WidgetOpacity, 0.50, 0.95)
            : 0.78;
        DelayedStartSeconds = Math.Clamp(DelayedStartSeconds, 0, 60);
        SamplingIntervalSeconds = SamplingIntervalSeconds is 1 or 2 or 5
            ? SamplingIntervalSeconds
            : 1;
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
        if (!File.Exists(path))
        {
            string? legacyPath = AppPaths.GetLegacyConfigPath();
            if (!string.IsNullOrWhiteSpace(legacyPath) && File.Exists(legacyPath))
                path = legacyPath;
        }

        try
        {
            if (!File.Exists(path)) return new AppConfig();
            if (new FileInfo(path).Length > MaxConfigBytes) return new AppConfig();

            AppConfig config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions)
                ?? new AppConfig();
            config.Normalize();

            if (!string.Equals(path, AppPaths.ConfigPath, StringComparison.OrdinalIgnoreCase))
                TrySave(config);

            return config;
        }
        catch (JsonException)
        {
            return new AppConfig();
        }
        catch (IOException)
        {
            return new AppConfig();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppConfig();
        }
    }

    public static bool TrySave(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
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
            }
        }
    }
}
