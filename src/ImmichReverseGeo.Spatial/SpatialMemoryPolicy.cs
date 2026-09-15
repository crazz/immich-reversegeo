using System;
using System.Globalization;
using System.IO;

namespace ImmichReverseGeo.Spatial;

internal static class SpatialMemoryPolicy
{
    internal const long MaximumPreparedWkbBytes = 32L * 1024 * 1024;

    internal static long DefaultBudget => BudgetFor(
        ReadContainerLimit("/sys/fs/cgroup/memory.max")
        ?? ReadContainerLimit("/sys/fs/cgroup/memory/memory.limit_in_bytes")
        ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    private static long? ReadContainerLimit(string path)
    {
        try
        {
            var value = File.ReadAllText(path).Trim();
            // cgroup v2 uses "max" and v1 uses a near-long.MaxValue sentinel
            // for an unlimited group. Neither is a physical memory allowance.
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
                && bytes > 0 && bytes < long.MaxValue / 2
                ? bytes
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static long BudgetFor(long availableBytes)
    {
        return availableBytes > 0
            ? Math.Min(availableBytes / 4, 1024L * 1024 * 1024)
            : 128L * 1024 * 1024;
    }

    internal static bool TryEstimate(long wkbBytes, out long retained, out long preparation)
    {
        retained = 0;
        preparation = 0;
        if (wkbBytes < 0)
        {
            return false;
        }

        try
        {
            // Covers decoded coordinate objects, ring/collection overhead and the
            // initialized interval index. Separate headroom covers WKB and validation.
            // Accounting is deliberately conservative; it is not a hard RSS limit.
            retained = checked(wkbBytes * 16 + 2 * 1024 * 1024);
            preparation = checked(wkbBytes * 12 + 65536);
            _ = checked(retained + preparation);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static bool TrySelect(long wkbBytes, long budget, out GeometryMemoryEstimate estimate)
    {
        estimate = default;
        if (wkbBytes < 0 || budget < 0)
        {
            return false;
        }

        if (wkbBytes <= MaximumPreparedWkbBytes
            && TryEstimate(wkbBytes, out var retained, out var construction)
            && retained + construction <= budget)
        {
            estimate = new GeometryMemoryEstimate(false, retained, 0, construction);
            return true;
        }

        try
        {
            // Include packed coordinates, ring objects and materialized coordinate
            // arrays. Distance workspace remains reserved for each compact entry;
            // construction headroom covers WKB, parsing and validity checking.
            estimate = new GeometryMemoryEstimate(true,
                checked(wkbBytes * 6 + 2 * 1024 * 1024),
                checked(wkbBytes * 4 + 65536),
                checked(wkbBytes * 14 + 65536));
            return estimate.ReservationBytes <= budget;
        }
        catch (OverflowException)
        {
            estimate = default;
            return false;
        }
    }
}

internal readonly record struct GeometryMemoryEstimate(
    bool Compact, long RetainedBytes, long EvaluationWorkspaceBytes, long ConstructionBytes)
{
    internal long EntryBytes => checked(RetainedBytes + EvaluationWorkspaceBytes);
    internal long ReservationBytes => checked(EntryBytes + ConstructionBytes);
}
