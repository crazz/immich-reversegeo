using System;
using System.Globalization;
using System.IO;

namespace ImmichReverseGeo.Spatial;

internal static class SpatialMemoryPolicy
{
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
}
