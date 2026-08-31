using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorSnapshotsAndPagingTests
{
    [TestMethod]
    public async Task ExecuteAsync_KeysetPages_UseEveryExactFinalRowCursorThenOneEmptySentinel()
    {
        var first = ExecutorFixture.Asset(1);
        var second = ExecutorFixture.Asset(2);
        var third = ExecutorFixture.Asset(3);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(3).EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.Config.Processing.BatchSize = 2;
        fixture.SetPages([first, second], [third], []);

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);

        ExecutorAssertions.Completed(result, 3, 3, 0, 0);
        CollectionAssert.AreEqual(
            new[]
            {
                AssetCursor.Initial,
                new AssetCursor(second.CreatedAt, second.Id),
                new AssetCursor(third.CreatedAt, third.Id)
            },
            fixture.Cursors.ToArray());
        CollectionAssert.AreEqual(new[] { 2, 2, 2 }, fixture.BatchSizes.ToArray());
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id, third.Id }, fixture.Writes.Select(item => item.AssetId).ToArray());
        Assert.AreEqual(3, fixture.BatchCalls);
        Assert.AreEqual(2, fixture.Reporter.Events.OfType<LogEmitted>().Count(item => item.Level == ProcessingLogLevel.Information));
    }

    [TestMethod]
    public async Task ExecuteAsync_DelayOccursAfterEveryNonEmptyBatchJoinAndNeverAfterEmptySentinel()
    {
        var first = ExecutorFixture.Asset(1);
        var second = ExecutorFixture.Asset(2);
        var delayOne = new AsyncGate();
        var delayTwo = new AsyncGate();
        var secondBatchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var emptyBatchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(2).EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.Config.Processing.BatchDelayMs = 11;
        fixture.BatchBehavior = (cursor, size, call, token) =>
        {
            if (call == 2)
            {
                secondBatchEntered.TrySetResult();
            }
            else if (call == 3)
            {
                emptyBatchEntered.TrySetResult();
            }

            return Task.FromResult(call switch
            {
                1 => new List<AssetRecord> { first },
                2 => new List<AssetRecord> { second },
                _ => []
            });
        };
        var delayCall = 0;
        fixture.DelayBehavior = (delay, token) => Interlocked.Increment(ref delayCall) == 1
            ? delayOne.EnterAsync(token)
            : delayTwo.EnterAsync(token);

        var execution = fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None);
        await delayOne.Entered.WaitAsync(ExecutorFixture.Bound);
        Assert.AreEqual(1, fixture.Writes.Count);
        Assert.IsFalse(secondBatchEntered.Task.IsCompleted);
        delayOne.Release();
        await secondBatchEntered.Task.WaitAsync(ExecutorFixture.Bound);
        await delayTwo.Entered.WaitAsync(ExecutorFixture.Bound);
        Assert.AreEqual(2, fixture.Writes.Count);
        Assert.IsFalse(emptyBatchEntered.Task.IsCompleted);
        delayTwo.Release();
        await emptyBatchEntered.Task.WaitAsync(ExecutorFixture.Bound);
        var result = await execution.WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);

        ExecutorAssertions.Completed(result, 2, 2, 0, 0);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(11), TimeSpan.FromMilliseconds(11) }, fixture.Delays.ToArray());
        Assert.AreEqual(3, fixture.BatchCalls);
    }

    public static IEnumerable<object[]> EligibilityCases =>
    [
        ["eligibility-lower-with-suppression"],
        ["eligibility-higher"],
        ["positive-immediate-empty"]
    ];

    [TestMethod]
    [DynamicData(nameof(EligibilityCases))]
    public async Task ExecuteAsync_EligibilityDivergence_PreservesCountSnapshotAndTerminatesOnlyAtEmptyBatch(string caseId)
    {
        var suppressed = ExecutorFixture.Asset(3);
        var updated = ExecutorFixture.Asset(1);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount().EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        long expectedProcessed;
        long expectedEligibility;
        if (caseId == "eligibility-lower-with-suppression")
        {
            expectedEligibility = 1;
            expectedProcessed = 1;
            fixture.Eligible = expectedEligibility;
            fixture.SkippedIds.Add(suppressed.Id);
            fixture.SetPages([suppressed, updated], []);
        }
        else if (caseId == "eligibility-higher")
        {
            expectedEligibility = 3;
            expectedProcessed = 1;
            fixture.Eligible = expectedEligibility;
            fixture.SetPages([updated], []);
        }
        else
        {
            Assert.AreEqual("positive-immediate-empty", caseId);
            expectedEligibility = 2;
            expectedProcessed = 0;
            fixture.Eligible = expectedEligibility;
            fixture.SetPages([]);
        }

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result);

        ExecutorAssertions.Completed(result, expectedProcessed, expectedProcessed, 0, 0);
        Assert.AreEqual(expectedEligibility, fixture.Reporter.Events.OfType<EligibilityDetermined>().Single().EligibleCount);
        Assert.AreEqual(expectedProcessed == 0 ? 1 : 2, fixture.BatchCalls);
        if (caseId == "eligibility-lower-with-suppression")
        {
            Assert.IsFalse(fixture.Resolutions.Any(item => item.AssetId == suppressed.Id));
            Assert.IsFalse(fixture.Writes.Any(item => item.AssetId == suppressed.Id));
        }
    }
}
