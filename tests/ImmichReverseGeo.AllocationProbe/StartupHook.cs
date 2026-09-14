using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class StartupHook
{
    public static void Initialize()
    {
        long started = 0;
        long allocatedBefore = 0;
        var pauseBefore = GC.GetTotalPauseDuration();
        var collectionsBefore = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
            var pauseAfter = GC.GetTotalPauseDuration();
            var collectionsAfter = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
            Console.Error.WriteLine("SPATIAL_ALLOCATION_MEASUREMENT " + JsonSerializer.Serialize(new
            {
                scope = "process-wide managed allocations: after hook setup before Main through ProcessExit; includes startup, all async work and cleanup; excludes hook setup, native allocations and report serialization",
                allocatedBefore, allocatedAfter, allocatedBytes = allocatedAfter - allocatedBefore,
                collectionsBefore, collectionsAfter, gcPauseSeconds = (pauseAfter - pauseBefore).TotalSeconds, elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                serverGc = GCSettings.IsServerGC, runtime = RuntimeInformation.FrameworkDescription,
                availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64
            }));
        };
        // Keep probe setup and reporting allocations outside the measured scope.
        started = Stopwatch.GetTimestamp();
        allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    }
}
