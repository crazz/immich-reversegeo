using System.Collections.Concurrent;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorParallelismTests
{
    public static IEnumerable<object[]> Cases =>
    [
        ["parallelism-within-one"],
        ["parallelism-within-four"],
        ["parallelism-within-thirty-two"]
    ];

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public async Task ExecuteAsync_ParallelismWithinBounds_ReachesConfiguredMaximumWithoutExceedingIt(string caseId)
    {
        var configured = caseId switch
        {
            "parallelism-within-one" => 1,
            "parallelism-within-four" => 4,
            "parallelism-within-thirty-two" => 32,
            _ => throw new AssertFailedException($"Unknown case {caseId}")
        };
        var assets = Enumerable.Range(1, configured + 1).Select(ExecutorFixture.Asset).ToArray();
        var entered = new ConcurrentDictionary<Guid, byte>();
        var releases = assets.ToDictionary(
            asset => asset.Id,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var configuredEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var additionalEntered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new ConcurrentDictionary<Guid, byte>();
        var dispositionAccepted = assets.ToDictionary(
            asset => asset.Id,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var dispositionAuthorizations = new ConcurrentQueue<Guid>();
        var active = 0;
        var maximum = 0;
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(assets.Length).EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.Config.Processing.MaxDegreeOfParallelism = configured;
        fixture.SetPages(assets, []);
        fixture.ResolveBehavior = async (asset, session, token) =>
        {
            var now = Interlocked.Increment(ref active);
            SetMaximum(ref maximum, now);
            Assert.IsTrue(entered.TryAdd(asset.Id, 0), $"Asset {asset.Id} entered twice.");
            if (entered.Count == configured)
            {
                configuredEntered.TrySetResult();
            }
            else if (entered.Count == configured + 1)
            {
                additionalEntered.TrySetResult(asset.Id);
            }
            await releases[asset.Id].Task.WaitAsync(ExecutorFixture.Bound, token).ConfigureAwait(false);
            Interlocked.Decrement(ref active);
            return ExecutorFixture.Resolution(new("Country", "State", $"City-{asset.Latitude}"));
        };
        fixture.EventBehavior = (processingEvent, token) =>
        {
            if (processingEvent is ProgressChanged)
            {
                Assert.IsTrue(dispositionAuthorizations.TryDequeue(out var assetId), "Every accepted disposition requires an explicit per-asset release authorization.");
                Assert.IsTrue(accepted.TryAdd(assetId, 0), $"Disposition for {assetId} accepted twice.");
                dispositionAccepted[assetId].TrySetResult();
            }
            return ValueTask.CompletedTask;
        };

        var execution = fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None);
        await configuredEntered.Task.WaitAsync(ExecutorFixture.Bound);
        Assert.AreEqual(configured, entered.Count);
        Assert.IsFalse(additionalEntered.Task.IsCompleted, "No N+1 asset may enter before any observed active identity is released.");

        var firstReleased = entered.Keys.First();
        dispositionAuthorizations.Enqueue(firstReleased);
        releases[firstReleased].TrySetResult();
        var additional = await additionalEntered.Task.WaitAsync(ExecutorFixture.Bound);
        await dispositionAccepted[firstReleased].Task.WaitAsync(ExecutorFixture.Bound);
        Assert.AreNotEqual(firstReleased, additional);
        Assert.AreEqual(configured + 1, entered.Count);

        foreach (var assetId in entered.Keys.Where(assetId => assetId != firstReleased).OrderBy(assetId => assetId))
        {
            dispositionAuthorizations.Enqueue(assetId);
            releases[assetId].TrySetResult();
            await dispositionAccepted[assetId].Task.WaitAsync(ExecutorFixture.Bound);
        }

        var result = await execution.WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.MaximumActive = maximum;
        fixture.Verify(caseId, result);
        ExecutorAssertions.Completed(result, assets.Length, assets.Length, 0, 0);
        Assert.AreEqual(configured, maximum);
        CollectionAssert.AreEquivalent(assets.Select(item => item.Id).ToArray(), entered.Keys.ToArray());
        CollectionAssert.AreEquivalent(assets.Select(item => item.Id).ToArray(), accepted.Keys.ToArray());
        CollectionAssert.AreEquivalent(assets.Select(item => item.Id).ToArray(), fixture.Writes.Select(item => item.AssetId).ToArray());
        CollectionAssert.AreEquivalent(
            Enumerable.Range(1, assets.Length).Select(value => (long)value).ToArray(),
            fixture.Reporter.Events.OfType<ProgressChanged>().Select(item => item.Progress.ProcessedCount).ToArray());
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<LogEmitted>().Count());
    }

    private static void SetMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var prior = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (prior == observed)
            {
                return;
            }
            observed = prior;
        }
    }
}
