using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change30")]
public sealed class WorkerEventStateBridgeFinalizationTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 5, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddSeconds(1);

    [TestMethod]
    public void TryFinalize_RejectsStaleIdentityAndMemoizesTheFirstWinner()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        var stale = new ProcessingRunRequest(request.RunId, request.Trigger);
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));

        var staleAttempt = reporter.TryFinalize(
            stale,
            Result(stale, ProcessingRunOutcome.Completed),
            ProcessingRunFinalizationOrigin.ControlPlane);

        Assert.AreEqual(ProcessingRunFinalizationDisposition.RejectedBeforeCommit, staleAttempt.Disposition);
        Assert.IsNull(staleAttempt.Receipt);
        Assert.IsNull(reporter.GetFinalizationReceipt(stale));
        Assert.IsTrue(state.IsRunning);

        var winnerResult = Result(request, ProcessingRunOutcome.Completed);
        var winner = reporter.TryFinalize(request, winnerResult, ProcessingRunFinalizationOrigin.ControlPlane);
        Assert.AreEqual(ProcessingRunFinalizationDisposition.Committed, winner.Disposition);
        Assert.IsNotNull(winner.Receipt);
        Assert.AreSame(request, winner.Receipt.Request);
        Assert.AreSame(winnerResult, winner.Receipt.Result);
        Assert.AreEqual(ProcessingRunFinalizationOrigin.ControlPlane, winner.Receipt.Origin);
        Assert.AreSame(winner.Receipt, reporter.GetFinalizationReceipt(request));
        Assert.IsNull(reporter.GetFinalizationReceipt(stale));
        var snapshot = Snapshot(state);

        var loser = reporter.TryFinalize(
            request,
            Result(request, ProcessingRunOutcome.Failed),
            ProcessingRunFinalizationOrigin.WorkerTerminal);

        Assert.AreEqual(ProcessingRunFinalizationDisposition.ExistingWinner, loser.Disposition);
        Assert.AreSame(winner.Receipt, loser.Receipt);
        Assert.AreEqual(snapshot, Snapshot(state));
        Assert.AreEqual(1, Messages(state).Count(message => message.StartsWith("Run complete.", StringComparison.Ordinal)));

        var replacement = Request();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(replacement));
        Assert.IsNull(reporter.GetFinalizationReceipt(request));
        Assert.IsFalse(reporter.TryAppendPostTerminalDiagnostic(winner.Receipt, "stale receipt"));
    }

    [TestMethod]
    public async Task CreateAbnormalResult_UsesExactAcceptedStartAndProgressAsync()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        var fallbackStartedAt = StartedAt.AddMinutes(-1);
        var endedAt = StartedAt.AddMinutes(1);
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        Assert.IsTrue(await reporter.TryProjectAsync(new RunStarted(request, StartedAt), CancellationToken.None));
        Assert.IsTrue(await reporter.TryProjectAsync(new EligibilityDetermined(request, 9), CancellationToken.None));
        Assert.IsTrue(await reporter.TryProjectAsync(
            new ProgressChanged(request, new ProcessingProgress(6, 3, 2, 1)),
            CancellationToken.None));

        var result = reporter.CreateAbnormalResult(
            request,
            ProcessingRunOutcome.Failed,
            fallbackStartedAt,
            endedAt,
            "safe projection failure");

        Assert.AreSame(request, result.Request);
        Assert.AreEqual(StartedAt, result.StartedAtUtc);
        Assert.AreEqual(endedAt, result.EndedAtUtc);
        Assert.AreEqual(6L, result.ProcessedCount);
        Assert.AreEqual(3L, result.UpdatedCount);
        Assert.AreEqual(2L, result.SkippedCount);
        Assert.AreEqual(1L, result.FailedCount);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome);
        Assert.AreEqual("safe projection failure", result.FailureMessage);
        var equivalent = new ProcessingRunRequest(request.RunId, request.Trigger);
        Assert.ThrowsExactly<InvalidOperationException>(() => reporter.CreateAbnormalResult(
            equivalent,
            ProcessingRunOutcome.Failed,
            fallbackStartedAt,
            endedAt,
            "stale"));
    }

    [TestMethod]
    public void CreateAbnormalResult_PreStartUsesAdmissionFallbackAndNoPriorRunCounters()
    {
        var state = new ProcessingState();
        state.StartRun(9);
        state.ApplyProgress(4, 3, 2);
        state.CompleteRun();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        var fallbackStartedAt = StartedAt.AddMinutes(-1);
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));

        var result = reporter.CreateAbnormalResult(
            request,
            ProcessingRunOutcome.Cancelled,
            fallbackStartedAt,
            EndedAt,
            null);

        Assert.AreEqual(fallbackStartedAt, result.StartedAtUtc);
        Assert.AreEqual(0L, result.ProcessedCount);
        Assert.AreEqual(0L, result.UpdatedCount);
        Assert.AreEqual(0L, result.SkippedCount);
        Assert.AreEqual(0L, result.FailedCount);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, result.Outcome);
        Assert.IsNull(result.FailureMessage);
    }

    [TestMethod]
    public async Task NormalTerminalAndControlPlaneFinalizer_RaceThroughOneReceiptGateAsync()
    {
        var terminalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalizerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state, processingEvent =>
        {
            if (processingEvent is RunFinished)
            {
                terminalEntered.TrySetResult();
                releaseTerminal.Task.GetAwaiter().GetResult();
            }
        });
        var request = Request();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        var workerResult = Result(request, ProcessingRunOutcome.Completed);
        var controlResult = Result(request, ProcessingRunOutcome.Failed);

        var worker = Task.Run(async () =>
            await reporter.TryProjectAsync(new RunFinished(request, workerResult), CancellationToken.None));
        await terminalEntered.Task.WaitAsync(BridgeTestCase.Bound);
        var control = Task.Run(() =>
        {
            finalizerStarted.TrySetResult();
            return reporter.TryFinalize(request, controlResult, ProcessingRunFinalizationOrigin.ControlPlane);
        });
        await finalizerStarted.Task.WaitAsync(BridgeTestCase.Bound);
        var completedBeforeRelease = control.IsCompleted;
        releaseTerminal.TrySetResult();
        Assert.IsTrue(await worker.WaitAsync(BridgeTestCase.Bound));
        var controlAttempt = await control.WaitAsync(BridgeTestCase.Bound);

        Assert.IsFalse(completedBeforeRelease, "The competing finalizer must wait behind the receipt owner.");
        Assert.AreEqual(ProcessingRunFinalizationDisposition.ExistingWinner, controlAttempt.Disposition);
        Assert.IsNotNull(controlAttempt.Receipt);
        Assert.AreSame(workerResult, controlAttempt.Receipt.Result);
        Assert.AreEqual(ProcessingRunFinalizationOrigin.WorkerTerminal, controlAttempt.Receipt.Origin);
        Assert.IsNull(state.LastError);
        Assert.AreEqual(0L, state.ErrorsThisRun);
        Assert.AreEqual(1, Messages(state).Count(message => message.StartsWith("Run complete.", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(ProcessingRunOutcome.Completed, false)]
    [DataRow(ProcessingRunOutcome.Cancelled, false)]
    [DataRow(ProcessingRunOutcome.Failed, false)]
    [DataRow(ProcessingRunOutcome.Completed, true)]
    [DataRow(ProcessingRunOutcome.Cancelled, true)]
    [DataRow(ProcessingRunOutcome.Failed, true)]
    public async Task TerminalCallbackFailure_LeavesTheRecordedOutcomeCanonicalAsync(
        ProcessingRunOutcome outcome,
        bool failStateObserver)
    {
        var failure = new InvalidOperationException(failStateObserver ? "state observer detail" : "projection hook detail");
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state, processingEvent =>
        {
            if (!failStateObserver && processingEvent is RunFinished)
            {
                throw failure;
            }
        });
        var request = Request();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        Assert.IsTrue(await reporter.TryProjectAsync(new EligibilityDetermined(request, 1), CancellationToken.None));
        var activityId = Guid.NewGuid();
        Assert.IsTrue(await reporter.TryProjectAsync(new ActivityStarted(request, activityId, "owned activity"), CancellationToken.None));
        Action observer = () => throw failure;
        if (failStateObserver)
        {
            state.OnChanged += observer;
        }

        var result = Result(request, outcome);
        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            reporter.TryProjectAsync(new RunFinished(request, result), CancellationToken.None).AsTask());
        state.OnChanged -= observer;

        Assert.AreSame(failure, observed);
        var receipt = reporter.GetFinalizationReceipt(request);
        Assert.IsNotNull(receipt);
        Assert.AreSame(result, receipt.Result);
        Assert.AreEqual(ProcessingRunFinalizationOrigin.WorkerTerminal, receipt.Origin);
        Assert.IsFalse(state.IsRunning);
        Assert.IsNull(state.CurrentActivity);
        Assert.IsNotNull(state.LastRunCompleted);
        Assert.AreEqual(outcome == ProcessingRunOutcome.Failed ? 1L : 0L, state.ErrorsThisRun);
        Assert.AreEqual(outcome == ProcessingRunOutcome.Failed ? "Fatal: terminal failure" : null, state.LastError);
        var messages = Messages(state);
        Assert.AreEqual(1, messages.Count(message => message.StartsWith("Run complete.", StringComparison.Ordinal)));
        Assert.AreEqual(outcome == ProcessingRunOutcome.Cancelled ? 1 : 0, messages.Count(message => message == "Run cancelled."));
        Assert.AreEqual(outcome == ProcessingRunOutcome.Failed ? 1 : 0, messages.Count(message => message == "[ERROR] Fatal: terminal failure"));
        var snapshot = Snapshot(state);

        var replay = reporter.TryFinalize(request, Result(request, ProcessingRunOutcome.Failed), ProcessingRunFinalizationOrigin.ControlPlane);
        Assert.AreEqual(ProcessingRunFinalizationDisposition.ExistingWinner, replay.Disposition);
        Assert.AreSame(receipt, replay.Receipt);
        Assert.AreEqual(snapshot, Snapshot(state));
    }

    [TestMethod]
    public async Task ControlPlaneFinalization_CleansOwnedActivityAndPreservesItsReceiptAsync()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        Assert.IsTrue(await reporter.TryProjectAsync(new EligibilityDetermined(request, 1), CancellationToken.None));
        Assert.IsTrue(await reporter.TryProjectAsync(
            new ActivityStarted(request, Guid.NewGuid(), "owned activity"),
            CancellationToken.None));
        var result = reporter.CreateAbnormalResult(
            request,
            ProcessingRunOutcome.Failed,
            StartedAt,
            EndedAt,
            "safe control-plane failure");
        var observerFailure = new InvalidOperationException("private observer detail");
        Action observer = () => throw observerFailure;
        state.OnChanged += observer;

        var observed = Assert.ThrowsExactly<InvalidOperationException>(() =>
            reporter.TryFinalize(request, result, ProcessingRunFinalizationOrigin.ControlPlane));
        state.OnChanged -= observer;

        Assert.AreSame(observerFailure, observed);
        var receipt = reporter.GetFinalizationReceipt(request);
        Assert.IsNotNull(receipt);
        Assert.AreSame(result, receipt.Result);
        Assert.AreEqual(ProcessingRunFinalizationOrigin.ControlPlane, receipt.Origin);
        Assert.IsFalse(state.IsRunning);
        Assert.IsNull(state.CurrentActivity);
        Assert.AreEqual(1L, state.ErrorsThisRun);
        Assert.AreEqual("Fatal: safe control-plane failure", state.LastError);
        Assert.AreEqual(1, Messages(state).Count(message => message == "[ERROR] Fatal: safe control-plane failure"));
        Assert.AreEqual(1, Messages(state).Count(message => message.StartsWith("Run complete.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PostTerminalDiagnostic_IsExactReceiptBoundedAndIdempotent()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        var result = Result(request, ProcessingRunOutcome.Completed);
        var committed = reporter.TryFinalize(request, result, ProcessingRunFinalizationOrigin.ControlPlane);
        Assert.IsNotNull(committed.Receipt);
        var counterfeit = new ProcessingRunFinalizationReceipt(request, result, ProcessingRunFinalizationOrigin.ControlPlane);
        var terminalSnapshot = Snapshot(state);
        var terminalSummary = Messages(state).Single(message => message.StartsWith("Run complete.", StringComparison.Ordinal));

        Assert.IsFalse(reporter.TryAppendPostTerminalDiagnostic(counterfeit, "counterfeit"));
        Assert.ThrowsExactly<ArgumentException>(() => reporter.TryAppendPostTerminalDiagnostic(committed.Receipt, new string('x', 257)));
        Action brokenObserver = () => throw new InvalidOperationException("private observer detail");
        state.OnChanged += brokenObserver;
        Assert.IsTrue(reporter.TryAppendPostTerminalDiagnostic(committed.Receipt, "process exit contradicted the committed terminal"));
        state.OnChanged -= brokenObserver;
        Assert.IsFalse(reporter.TryAppendPostTerminalDiagnostic(committed.Receipt, "duplicate"));

        Assert.AreEqual(terminalSnapshot.Running, state.IsRunning);
        Assert.AreEqual(terminalSnapshot.Processed, state.ProcessedThisRun);
        Assert.AreEqual(terminalSnapshot.Skipped, state.SkippedThisRun);
        Assert.AreEqual(terminalSnapshot.Errors, state.ErrorsThisRun);
        Assert.AreEqual(terminalSnapshot.Completed, state.LastRunCompleted);
        Assert.AreEqual(terminalSnapshot.Error, state.LastError);
        Assert.AreEqual(terminalSummary, Messages(state).Single(message => message.StartsWith("Run complete.", StringComparison.Ordinal)));
        Assert.AreEqual(1, Messages(state).Count(message => message == "[WARN] process exit contradicted the committed terminal"));
    }

    [TestMethod]
    public async Task SemanticRejection_NeverRetainsATerminalReplayCandidateAsync()
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(0);
        var terminal = test.Frame(new RunFinished(test.Request, test.Result(ProcessingRunOutcome.Completed)), test.NextSequence + 1);

        await Assert.ThrowsExactlyAsync<WorkerEventStateBridgeException>(
            () => test.Bridge.AcceptAsync(terminal, CancellationToken.None).AsTask());

        Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.EventRejected>(test.Bridge.FirstObservation);
        Assert.IsNotInstanceOfType<WorkerEventStateBridgeObservation.TerminalProjectionNotCommitted>(test.Bridge.FirstObservation);
        Assert.IsNull(test.Adapter.GetFinalizationReceipt(test.Request));
    }

    [TestMethod]
    public async Task PreviewValidTerminalWithDefiniteStateRejection_RetainsOnlyTheExactCandidateAsync()
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(0);
        Assert.IsTrue(test.Adapter.Abandon(test.Request, new InvalidOperationException("prior projection failure")));
        var result = test.Result(ProcessingRunOutcome.Completed);
        var before = test.Snapshot();

        var failure = await Assert.ThrowsExactlyAsync<WorkerEventStateBridgeException>(
            () => test.Bridge.AcceptAsync(test.Frame(new RunFinished(test.Request, result)), CancellationToken.None).AsTask());

        Assert.IsNull(failure.InnerException);
        var observation = Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.TerminalProjectionNotCommitted>(
            test.Bridge.FirstObservation);
        Assert.AreEqual(result, observation.Candidate);
        Assert.AreSame(test.Request, observation.Candidate.Request);
        Assert.IsNull(test.Adapter.GetFinalizationReceipt(test.Request));
        Assert.AreEqual(before, test.Snapshot());
    }

    [TestMethod]
    [DataRow(ProcessingRunOutcome.Completed)]
    [DataRow(ProcessingRunOutcome.Cancelled)]
    [DataRow(ProcessingRunOutcome.Failed)]
    public async Task TerminalProjectionUnknownResponse_RetainsNoReplayCandidateAndReceiptWinsAsync(
        ProcessingRunOutcome outcome)
    {
        var terminalFailure = new InvalidOperationException("private terminal callback detail");
        await using var test = new BridgeTestCase(beforeProjection: processingEvent =>
        {
            if (processingEvent is RunFinished)
            {
                throw terminalFailure;
            }
        });
        await test.BeginAsync(0);
        var result = test.Result(outcome);

        var failure = await Assert.ThrowsExactlyAsync<WorkerEventStateBridgeException>(
            () => test.Bridge.AcceptAsync(test.Frame(new RunFinished(test.Request, result)), CancellationToken.None).AsTask());

        Assert.IsNull(failure.InnerException);
        Assert.IsFalse(failure.ToString().Contains(terminalFailure.Message, StringComparison.Ordinal));
        Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.ProjectionResponseIndeterminate>(test.Bridge.FirstObservation);
        Assert.IsNotInstanceOfType<WorkerEventStateBridgeObservation.TerminalProjectionNotCommitted>(test.Bridge.FirstObservation);
        var receipt = test.Adapter.GetFinalizationReceipt(test.Request);
        Assert.IsNotNull(receipt);
        Assert.AreEqual(result, receipt.Result);
        Assert.AreEqual(outcome, receipt.Result.Outcome);
        Assert.IsFalse(test.State.IsRunning);
        Assert.AreEqual(outcome == ProcessingRunOutcome.Failed ? 1L : 0L, test.State.ErrorsThisRun);
        Assert.AreEqual(1, test.Logs.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)));
    }

    private static ProcessingRunRequest Request()
        => new(Guid.NewGuid(), ProcessingRunTrigger.Manual);

    private static ProcessingRunResult Result(
        ProcessingRunRequest request,
        ProcessingRunOutcome outcome)
        => new(
            request,
            StartedAt,
            EndedAt,
            0,
            0,
            0,
            0,
            outcome,
            outcome == ProcessingRunOutcome.Failed ? "terminal failure" : null);

    private static string[] Messages(ProcessingState state)
        => state.GetRecentLog().Select(line => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..]).ToArray();

    private static StateSnapshot Snapshot(ProcessingState state)
        => new(
            state.IsRunning,
            state.ProcessedThisRun,
            state.SkippedThisRun,
            state.ErrorsThisRun,
            state.LastRunCompleted,
            state.LastError,
            state.CurrentActivity,
            string.Join('\n', state.GetRecentLog()));

    private sealed record StateSnapshot(
        bool Running,
        long Processed,
        long Skipped,
        long Errors,
        DateTime? Completed,
        string? Error,
        string? Activity,
        string Log);
}
