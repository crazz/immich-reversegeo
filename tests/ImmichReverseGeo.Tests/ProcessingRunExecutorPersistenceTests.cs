using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change14")]
public class ProcessingRunExecutorPersistenceTests
{
    public static IEnumerable<object[]> PersistenceFailureCases =>
    [
        ["ordinary-update"],
        ["ordinary-insert"]
    ];

    [TestMethod]
    [DynamicData(nameof(PersistenceFailureCases))]
    public async Task ExecuteAsync_PersistenceFailureMatrix_RecordsNoFalseEffectOrSuccessDisposition(string caseId)
    {
        var failed = ExecutorFixture.Asset(1);
        var peer = ExecutorFixture.Asset(2);
        var fixture = new ExecutorFixture().EnableReporter().EnableCount(2).EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.Config.Processing.MaxDegreeOfParallelism = 1;
        fixture.SetPages([failed, peer], []);
        fixture.ResolveBehavior = (asset, session, token) =>
        {
            if (asset.Id == failed.Id && caseId == "ordinary-insert")
            {
                return Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(null);
            }

            return Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(
                ExecutorFixture.Resolution(new("Country", "State", asset.Id == peer.Id ? "PeerCity" : "FailedCity")));
        };
        fixture.WriteBehavior = (assetId, geo, token) => assetId == failed.Id
            ? Task.FromException(new InvalidOperationException("update failure"))
            : Task.CompletedTask;
        fixture.AddSkippedBehavior = assetId => Task.FromException(new InvalidOperationException("skipped-insert failure"));

        var result = await fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, CancellationToken.None).WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result);

        ExecutorAssertions.Completed(result, 2, 1, 0, 1);
        Assert.AreEqual(peer.Id, fixture.Writes.Single().AssetId);
        Assert.AreEqual(0, fixture.SkippedWrites.Count);
        Assert.AreEqual(2, fixture.Reporter.Events.OfType<ProgressChanged>().Count());
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<LogEmitted>().Count(item => item.Level == ProcessingLogLevel.Error));
        Assert.IsFalse(fixture.Reporter.Events.OfType<ProgressChanged>().Any(item => item.Progress.SkippedCount > 0));
    }

    public static IEnumerable<object[]> ActiveCancellationCases =>
    [
        ["cancel-during-update-before-success"],
        ["cancel-during-insert-before-success"]
    ];

    [TestMethod]
    [DynamicData(nameof(ActiveCancellationCases))]
    public async Task ExecuteAsync_ActiveCancellationDuringPersistenceBeforeSuccess_LeavesAssetUncommittedAndUncounted(string caseId)
    {
        var asset = ExecutorFixture.Asset(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assetTokenCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var fixture = new ExecutorFixture().EnableReporter().EnableCount().EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.SetPages([asset], []);
        fixture.ResolveBehavior = (current, session, token) =>
        {
            token.Register(() => assetTokenCancelled.TrySetResult());
            return caseId == "cancel-during-insert-before-success"
                ? Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(null)
                : Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(ExecutorFixture.Resolution(new("Country", "State", "City")));
        };
        fixture.WriteBehavior = async (assetId, geo, token) =>
        {
            entered.TrySetResult();
            await TaskCompletionSourceTask(token).ConfigureAwait(false);
        };
        fixture.AddSkippedBehavior = async assetId =>
        {
            entered.TrySetResult();
            await assetTokenCancelled.Task.WaitAsync(ExecutorFixture.Bound).ConfigureAwait(false);
            throw new OperationCanceledException(cancellation.Token);
        };

        var execution = fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token);
        await entered.Task.WaitAsync(ExecutorFixture.Bound);
        cancellation.Cancel();
        var result = await execution.WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result, runToken: cancellation.Token);

        Assert.AreEqual(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled, result.Outcome);
        ExecutorAssertions.Counts(result, 0, 0, 0, 0);
        Assert.AreEqual(0, fixture.Writes.Count);
        Assert.AreEqual(0, fixture.SkippedWrites.Count);
        Assert.AreEqual(0, fixture.Reporter.Events.OfType<ProgressChanged>().Count());
    }

    public static IEnumerable<object[]> CommittedCancellationCases =>
    [
        ["cancel-after-update-effect"],
        ["cancel-after-insert-effect"]
    ];

    [TestMethod]
    [DynamicData(nameof(CommittedCancellationCases))]
    public async Task ExecuteAsync_CancellationAfterSuccessfulPersistence_PublishesAndCountsCommittedEffect(string caseId)
    {
        var asset = ExecutorFixture.Asset(1);
        var dispositionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisposition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var fixture = new ExecutorFixture().EnableReporter().EnableCount().EnableSnapshots().EnablePages().EnableAdmin();
        if (caseId == "cancel-after-insert-effect")
        {
            fixture.EnableSkippedInsert();
        }
        else
        {
            fixture.EnableWrite();
        }
        fixture.SetPages([asset], []);
        fixture.ResolveBehavior = (current, session, token) => caseId == "cancel-after-insert-effect"
            ? Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(null)
            : Task.FromResult<ImmichReverseGeo.Web.Services.AdministrativeAreaResolution?>(ExecutorFixture.Resolution(new("Country", "State", "City")));
        fixture.EventBehavior = async (processingEvent, token) =>
        {
            if (processingEvent is ProgressChanged)
            {
                Assert.IsFalse(token.CanBeCanceled);
                dispositionEntered.TrySetResult();
                await releaseDisposition.Task.WaitAsync(ExecutorFixture.Bound).ConfigureAwait(false);
            }
        };

        var execution = fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token);
        await dispositionEntered.Task.WaitAsync(ExecutorFixture.Bound);
        Assert.AreEqual(1, caseId == "cancel-after-insert-effect" ? fixture.SkippedWrites.Count : fixture.Writes.Count);
        cancellation.Cancel();
        releaseDisposition.TrySetResult();
        var result = await execution.WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify(caseId, result, runToken: cancellation.Token);

        Assert.AreEqual(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled, result.Outcome);
        ExecutorAssertions.Counts(result, 1, caseId == "cancel-after-update-effect" ? 1 : 0, caseId == "cancel-after-insert-effect" ? 1 : 0, 0);
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<ProgressChanged>().Count());
    }

    [TestMethod]
    public async Task ExecuteAsync_CancellationAfterCommittedHandledFailure_PublishesAndCountsFailedDisposition()
    {
        var asset = ExecutorFixture.Asset(1);
        var dispositionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisposition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var fixture = new ExecutorFixture().EnableReporter().EnableCount().EnableSnapshots().EnablePages().EnableAdmin().EnableWrite();
        fixture.SetPages([asset], []);
        fixture.ResolveBehavior = (current, session, token) => throw new InvalidOperationException("admin failure");
        fixture.EventBehavior = async (processingEvent, token) =>
        {
            if (processingEvent is ProgressChanged)
            {
                Assert.IsFalse(token.CanBeCanceled);
                dispositionEntered.TrySetResult();
                await releaseDisposition.Task.WaitAsync(ExecutorFixture.Bound).ConfigureAwait(false);
            }
        };

        var execution = fixture.Executor.ExecuteAsync(fixture.Request, fixture.Reporter, cancellation.Token);
        await dispositionEntered.Task.WaitAsync(ExecutorFixture.Bound);
        Assert.AreEqual(1, fixture.Reporter.Events.OfType<LogEmitted>().Count(item => item.Level == ProcessingLogLevel.Error));
        cancellation.Cancel();
        releaseDisposition.TrySetResult();
        var result = await execution.WaitAsync(ExecutorFixture.Bound);
        fixture.AssertTerminal(result);
        fixture.Verify("cancel-after-handled-failed-decision", result, runToken: cancellation.Token);

        Assert.AreEqual(ImmichReverseGeo.Core.Models.ProcessingRunOutcome.Cancelled, result.Outcome);
        ExecutorAssertions.Counts(result, 1, 0, 0, 1);
        Assert.AreEqual(0, fixture.Writes.Count);
        Assert.AreEqual(0, fixture.SkippedWrites.Count);
    }

    private static async Task TaskCompletionSourceTask(CancellationToken token)
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await never.Task.WaitAsync(ExecutorFixture.Bound, token).ConfigureAwait(false);
    }
}
