namespace TempMonitor;

internal readonly record struct NetworkInterfaceInfo(
    string InterfaceId,
    string DisplayName);

internal sealed record MonitoringSelectionOptions(
    string? GpuProvider,
    string? GpuDeviceIdentifier,
    NetworkSelectionMode NetworkMode,
    string? NetworkInterfaceId)
{
    public static MonitoringSelectionOptions Default { get; } = new(
        null,
        null,
        NetworkSelectionMode.Auto,
        null);

    public static MonitoringSelectionOptions Create(
        string? gpuProvider,
        string? gpuDeviceIdentifier,
        NetworkSelectionMode networkMode,
        string? networkInterfaceId)
    {
        string? normalizedGpuProvider = Normalize(gpuProvider);
        string? normalizedGpuDeviceIdentifier = Normalize(gpuDeviceIdentifier);
        if (normalizedGpuProvider == null || normalizedGpuDeviceIdentifier == null)
        {
            normalizedGpuProvider = null;
            normalizedGpuDeviceIdentifier = null;
        }

        NetworkSelectionMode normalizedNetworkMode = Enum.IsDefined(networkMode)
            ? networkMode
            : NetworkSelectionMode.Auto;
        string? normalizedNetworkInterfaceId = Normalize(networkInterfaceId);
        if (normalizedNetworkMode == NetworkSelectionMode.Fixed && normalizedNetworkInterfaceId == null)
            normalizedNetworkMode = NetworkSelectionMode.Auto;
        if (normalizedNetworkMode != NetworkSelectionMode.Fixed)
            normalizedNetworkInterfaceId = null;

        return new MonitoringSelectionOptions(
            normalizedGpuProvider,
            normalizedGpuDeviceIdentifier,
            normalizedNetworkMode,
            normalizedNetworkInterfaceId);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
