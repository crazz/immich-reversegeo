using System.Collections.Concurrent;
using ImmichReverseGeo.Gadm.Services;

namespace ImmichReverseGeo.Gadm.Tests;

[TestClass]
[TestCategory("Change67")]
public sealed class ProcessFailureMatrixCacheMapTests
{
    [TestMethod]
    public async Task DelayedExactTaskContinuation_CannotRemoveReplacementAndOwnsNoFiles()
    {
        var map = new ConcurrentDictionary<string, Lazy<Task>>();
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new Lazy<Task>(() => releaseOld.Task);
        var replacement = new Lazy<Task>(() => Task.CompletedTask);
        map["CHE"] = old;
        bool removed = true;
        Task delayedCleanup = old.Value.ContinueWith(_ => removed = GadmDivisionCacheService.RemoveExact(map, "CHE", old),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        map["CHE"] = replacement;
        releaseOld.TrySetResult();
        try
        {
            await delayedCleanup.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Assert.Fail("Cache map watchdog expired: old-exact-task-continuation.");
        }
        Assert.IsFalse(removed);
        Assert.AreSame(replacement, map["CHE"]);
        Assert.IsFalse(replacement.IsValueCreated);
        await replacement.Value;
        Assert.IsTrue(GadmDivisionCacheService.RemoveExact(map, "CHE", replacement));
        Assert.IsTrue(map.IsEmpty);
    }
}
