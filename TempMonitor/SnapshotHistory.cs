using System;

namespace TempMonitor;

internal static class SnapshotHistory
{
    /// <summary>
    /// Selects an inclusive time range and, when necessary, samples it evenly while
    /// preserving both endpoints. A one-point request returns the newest sample.
    /// </summary>
    public static HardwareSnapshot[] SelectRange(
        IReadOnlyList<HardwareSnapshot> snapshots,
        DateTime? startInclusive,
        DateTime? endInclusive,
        int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (maxPoints <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPoints), "The maximum point count must be positive.");
        if (startInclusive.HasValue && endInclusive.HasValue && startInclusive > endInclusive)
            throw new ArgumentException("The history start time must not be later than its end time.");

        return SelectCore(
            snapshots,
            snapshot => (!startInclusive.HasValue || snapshot.Timestamp >= startInclusive.Value) &&
                        (!endInclusive.HasValue || snapshot.Timestamp <= endInclusive.Value),
            maxPoints);
    }

    internal static HardwareSnapshot[] SelectMonotonicRange(
        IReadOnlyList<HardwareSnapshot> snapshots,
        long startInclusive,
        long endInclusive,
        int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (maxPoints <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPoints), "The maximum point count must be positive.");
        if (startInclusive > endInclusive)
            throw new ArgumentException("The monotonic history start must not be later than its end.");

        return SelectCore(
            snapshots,
            snapshot => snapshot.MonotonicTimestamp >= startInclusive &&
                        snapshot.MonotonicTimestamp <= endInclusive,
            maxPoints);
    }

    private static HardwareSnapshot[] SelectCore(
        IReadOnlyList<HardwareSnapshot> snapshots,
        Func<HardwareSnapshot, bool> isIncluded,
        int maxPoints)
    {
        int selectedCount = 0;
        HardwareSnapshot? newest = null;
        for (int index = 0; index < snapshots.Count; index++)
        {
            HardwareSnapshot snapshot = snapshots[index];
            if (!isIncluded(snapshot))
                continue;

            selectedCount++;
            newest = snapshot;
        }

        if (selectedCount == 0)
            return [];
        if (maxPoints == 1)
            return [newest!];

        int resultLength = Math.Min(selectedCount, maxPoints);
        var result = new HardwareSnapshot[resultLength];
        int selectedIndex = 0;
        int targetIndex = 0;
        int nextSourceIndex = GetSourceIndex(targetIndex, selectedCount, resultLength);

        for (int index = 0; index < snapshots.Count && targetIndex < resultLength; index++)
        {
            HardwareSnapshot snapshot = snapshots[index];
            if (!isIncluded(snapshot))
                continue;

            if (selectedIndex == nextSourceIndex)
            {
                result[targetIndex++] = snapshot;
                if (targetIndex < resultLength)
                    nextSourceIndex = GetSourceIndex(targetIndex, selectedCount, resultLength);
            }

            selectedIndex++;
        }

        return result;
    }

    private static int GetSourceIndex(int targetIndex, int selectedCount, int resultLength)
    {
        if (selectedCount <= resultLength)
            return targetIndex;

        return (int)Math.Round(
            targetIndex * (double)(selectedCount - 1) / (resultLength - 1),
            MidpointRounding.AwayFromZero);
    }
}
