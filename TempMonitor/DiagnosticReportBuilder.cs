using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TempMonitor;

internal static class DiagnosticReportBuilder
{
    public static string Build(
        HardwareSnapshot snapshot,
        int samplingIntervalMilliseconds,
        int historyCount,
        IReadOnlyCollection<string> activeIssueKeys)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activeIssueKeys);

        Assembly assembly = typeof(HardwareMonitorService).Assembly;
        string version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";

        var builder = new StringBuilder(1024);
        builder.AppendLine("gxTempMonitor privacy-safe diagnostic report");
        Append(builder, "Generated (UTC)", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Append(builder, "App version", NormalizeSingleLine(version));
        Append(builder, "OS", NormalizeSingleLine(RuntimeInformation.OSDescription));
        Append(builder, "OS architecture", RuntimeInformation.OSArchitecture.ToString());
        Append(builder, "Process architecture", RuntimeInformation.ProcessArchitecture.ToString());
        Append(builder, ".NET", NormalizeSingleLine(RuntimeInformation.FrameworkDescription));
        builder.AppendLine();

        builder.AppendLine("System");
        Append(builder, "CPU", NormalizeSingleLine(snapshot.CpuName));
        Append(builder, "Logical processors", snapshot.LogicalProcessorCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, "CPU architecture", NormalizeSingleLine(snapshot.CpuArchitecture));
        Append(builder, "System uptime", FormatDuration(snapshot.SystemUptime));
        Append(builder, "Battery", FormatBattery(snapshot));
        Append(builder, "AC power", FormatAcPower(snapshot.IsOnAcPower));
        Append(builder, "System drive", snapshot.HasSystemDriveData
            ? FormattableString.Invariant($"{snapshot.SystemDriveAvailableGb:0.0} GiB available / {snapshot.SystemDriveTotalGb:0.0} GiB total")
            : "unavailable");
        builder.AppendLine();

        builder.AppendLine("Active providers and capabilities");
        Append(builder, "GPU provider", NormalizeSingleLine(snapshot.GpuProviderName));
        Append(builder, "GPU device", FormatGpuDevice(snapshot.GpuDeviceName));
        Append(builder, "CPU usage", Available(snapshot.HasCpuUsage));
        Append(builder, "CPU temperature", "not collected by design");
        Append(builder, "GPU usage", Available(HasGpuCapability(snapshot, GpuMetricCapabilities.Usage)));
        Append(builder, "GPU temperature", Available(HasGpuCapability(snapshot, GpuMetricCapabilities.Temperature)));
        Append(builder, "GPU power", Available(HasGpuCapability(snapshot, GpuMetricCapabilities.Power)));
        Append(builder, "GPU VRAM used", Available(HasGpuCapability(snapshot, GpuMetricCapabilities.VramUsed)));
        Append(builder, "GPU VRAM total", Available(HasGpuCapability(snapshot, GpuMetricCapabilities.VramTotal)));
        Append(builder, "RAM", Available(snapshot.HasRamData));
        Append(builder, "Network throughput", Available(snapshot.HasNetworkData));
        builder.AppendLine();

        builder.AppendLine("Sampler");
        Append(builder, "Configured interval", FormattableString.Invariant($"{samplingIntervalMilliseconds / 1000d:0.###} s"));
        Append(builder, "Latest collection duration", FormattableString.Invariant($"{snapshot.SamplingDurationMilliseconds:0.###} ms"));
        Append(builder, "Buffered history samples", historyCount.ToString(CultureInfo.InvariantCulture));
        Append(builder, "Active issue keys", activeIssueKeys.Count == 0
            ? "none"
            : string.Join(", ", activeIssueKeys.Select(SanitizeIssueKey).Distinct(StringComparer.Ordinal).Order()));
        try
        {
            using Process process = Process.GetCurrentProcess();
            Append(builder, "Process working set", FormattableString.Invariant($"{process.WorkingSet64 / 1024d / 1024d:0.0} MiB"));
            Append(builder, "Process handles", process.HandleCount.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Append(builder, "Process working set", "unavailable");
            Append(builder, "Process handles", "unavailable");
        }
        builder.AppendLine();

        builder.AppendLine("Privacy");
        builder.AppendLine("User names, machine names, IP addresses, adapter identifiers, file paths,");
        builder.AppendLine("process names, logs, and application data are intentionally excluded.");
        return builder.ToString();
    }

    internal static string SanitizeIssueKey(string key)
    {
        if (key.StartsWith("network-interface:", StringComparison.Ordinal))
            return "network-interface:[redacted]";

        string normalized = NormalizeSingleLine(key, 96);
        if (normalized == "unavailable")
            return normalized;

        var builder = new StringBuilder(normalized.Length);
        foreach (char character in normalized)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '[' or ']'
                ? character
                : '_');
        }

        return builder.ToString();
    }

    internal static string NormalizeSingleLine(string? value, int maximumLength = 160)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unavailable";

        string normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength] + "…";
    }

    private static string FormatGpuDevice(string? deviceName)
    {
        string normalized = NormalizeSingleLine(deviceName);
        if (normalized.Contains("luid_", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows GPU (adapter identifier redacted)";
        }

        return normalized;
    }

    private static string Available(bool available) => available ? "available" : "unavailable";

    private static bool HasGpuCapability(
        HardwareSnapshot snapshot,
        GpuMetricCapabilities capability) =>
        (snapshot.GpuCapabilities & capability) != 0;

    private static string FormatBattery(HardwareSnapshot snapshot) => snapshot.IsBatteryPresent switch
    {
        false => "not present",
        true when snapshot.BatteryChargePercent.HasValue =>
            FormattableString.Invariant($"{snapshot.BatteryChargePercent.Value:0}%"),
        true => "present; charge unavailable",
        _ => "unavailable"
    };

    private static string FormatAcPower(bool? isOnAcPower) => isOnAcPower switch
    {
        true => "connected",
        false => "disconnected",
        _ => "unavailable"
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            return "unavailable";

        int totalHours = (int)Math.Min(int.MaxValue, Math.Floor(duration.TotalHours));
        return $"{totalHours}h {duration.Minutes}m {duration.Seconds}s";
    }

    private static void Append(StringBuilder builder, string label, string value) =>
        builder.Append(label).Append(": ").AppendLine(value);
}
