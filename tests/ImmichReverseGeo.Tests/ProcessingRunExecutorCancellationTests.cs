using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorCancellationTests
{
    public static IEnumerable<object[]> CountCases =>
    [
        ["count-token-pre-cancelled"],
        ["cancel-during-count"]
    ];

    [TestMethod]
    [DynamicData(nameof(CountCases))]
    public async Task ExecuteAsync_CountCancellationRows_ObservePreCancelledOrDuringCountTokenWithoutEligibility(string caseId)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ExecutorFixture().EnableReporter();
        fixture.CountBehavior = async token =>
        {
            entered.TrySetResult();
            token.ThrowIfCancellationRequested();
            var never = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            return await never.Task.WaitAsync(ExecutorFixture.Bound, token).ConfigureAwait(false);
        };
        if (caseId == "count-token-pre-cancelled")
        {
            cancellation.Cancel();
        }
        else
        {
            Assert.AreEqual("cancel-during-count", caseId);
        }

        var execution = fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token);
        await entered.Task.WaitAsync(ExecutorFixture.Bound);
        if (!cancellation.IsCancellationRequested)
        {
            cancellation.Cancel();
        }

        var result = await execution.WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result, runToken: cancellation.Token);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        ExecutorAssertions.Counts(result, 0, 0, 0, 0);
        Assert.AreEqual(1, fixture.CountCalls);
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<EligibilityDetermined>().Count());
        Assert.AreEqual(0, fixture.ConfigCalls);
        Assert.AreEqual(0, fixture.BatchCalls);
    }

    public static IEnumerable<object[]> BoundaryCases =>
    [
        ["cancel-skipped-snapshot"],
        ["cancel-config-snapshot"],
        ["cancel-batch"],
        ["cancel-admin"],
        ["cancel-airport"]
    ];

    [TestMethod]
    [DynamicData(nameof(BoundaryCases))]
    public async Task ExecuteAsync_ActiveCancellationBoundaryMatrix_StopsBeforeTargetDispositionAndRetainsPriorWork(string caseId)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ExecutorFixture.Asset(1);
        var target = ExecutorFixture.Asset(2);
        var prior = caseId is "cancel-batch" or "cancel-admin" or "cancel-airport";
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(prior ? 2 : 1);
        if (caseId != "cancel-skipped-snapshot")
        {
            fixture.EnableSnapshots();
        }
        if (prior)
        {
            fixture.EnablePages().EnableAdmin().EnableWrite();
        }
        fixture.Config.Processing.UseAirportInfrastructure = caseId == "cancel-airport";
        fixture.Config.Processing.BatchSize = 1;
        if (caseId == "cancel-skipped-snapshot")
        {
            fixture.SkippedBehavior = async () =>
            {
                entered.TrySetResult();
                await Never(cancellation.Token).ConfigureAwait(false);
                return [];
            };
        }
        else if (caseId == "cancel-config-snapshot")
        {
            fixture.ConfigBehavior = async () =>
            {
                entered.TrySetResult();
                await Never(cancellation.Token).ConfigureAwait(false);
                return fixture.Config;
            };
        }
        else
        {
            fixture.SetPages([first], [target], []);
            fixture.BatchBehavior = async (cursor, size, call, token) =>
            {
                if (caseId == "cancel-batch" && call == 2)
                {
                    entered.TrySetResult();
                    await Never(token).ConfigureAwait(false);
                }

                return call switch
                {
                    1 => [first],
                    2 => [target],
                    _ => []
                };
            };
            fixture.ResolveBehavior = async (asset, session, token) =>
            {
                if (asset.Id == target.Id && caseId == "cancel-admin")
                {
                    entered.TrySetResult();
                    await Never(token).ConfigureAwait(false);
                }

                return ExecutorFixture.Resolution(new("Country", "State", $"City-{asset.Latitude}"));
            };
            fixture.AirportBehavior = async (asset, token) =>
            {
                if (asset.Id == target.Id)
                {
                    entered.TrySetResult();
                    await Never(token).ConfigureAwait(false);
                }

                return ExecutorFixture.EmptyAirport();
            };
        }

        var execution = fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token);
        await entered.Task.WaitAsync(ExecutorFixture.Bound);
        cancellation.Cancel();
        var result = await execution.WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result, runToken: cancellation.Token);

        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        ExecutorAssertions.Counts(result, prior ? 1 : 0, prior ? 1 : 0, 0, 0);
        if (prior)
        {
            Assert.AreEqual(first.Id, fixture.Writes.Single().AssetId);
        }
        Assert.IsFalse(fixture.Writes.Any(item => item.AssetId == target.Id));
        Assert.IsFalse(fixture.Reporter.Events.OfType<ProgressChanged>().Any(item => item.Progress.ProcessedCount > 1));
    }

    public static IEnumerable<object[]> BetweenCases =>
    [
        ["cancel-between-batches"],
        ["cancel-during-delay"]
    ];

    [TestMethod]
    [DynamicData(nameof(BetweenCases))]
    public async Task ExecuteAsync_CancellationBetweenBatchesOrDuringDelay_PreventsNextFetchAndRetainsPriorDispositions(string caseId)
    {
        using var cancellation = new CancellationTokenSource();
        var first = ExecutorFixture.Asset(1);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(2).EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.Config.Processing.BatchDelayMs = caseId == "cancel-during-delay" ? 11 : 0;
        fixture.SetPages([first], [ExecutorFixture.Asset(2)], []);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (caseId == "cancel-during-delay")
        {
            fixture.DelayBehavior = async (delay, token) =>
            {
                delayEntered.TrySetResult();
                await Never(token).ConfigureAwait(false);
            };
        }
        fixture.EventBehavior = (processingEvent, token) =>
        {
            if (caseId == "cancel-between-batches" && processingEvent is ProgressChanged)
            {
                cancellation.Cancel();
            }

            return ValueTask.CompletedTask;
        };

        var execution = fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token);
        if (caseId == "cancel-during-delay")
        {
            await delayEntered.Task.WaitAsync(ExecutorFixture.Bound);
            cancellation.Cancel();
        }

        var result = await execution.WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result, runToken: cancellation.Token);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        ExecutorAssertions.Counts(result, 1, 1, 0, 0);
        Assert.AreEqual(1, fixture.BatchCalls);
        Assert.AreEqual(first.Id, fixture.Writes.Single().AssetId);
    }

    private static async Task Never(CancellationToken token)
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await never.Task.WaitAsync(ExecutorFixture.Bound, token).ConfigureAwait(false);
    }
}
