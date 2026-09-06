using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.ScheduledChildWorkerExecution;

[TestClass]
[DoNotParallelize]
[TestCategory("Change35")]
public sealed class LocalFinalizerTests
{
    [TestMethod]
    public void Completed_ProjectsZeroRunAndReleasesExactReporterOwnership()
    {
        var state = CompletedPriorState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = ScheduledRequest();
        var before = DateTime.UtcNow;
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));

        Assert.IsTrue(reporter.TryFinalizePredispatch(
            request,
            ProcessingRunOutcome.Completed,
            safeFailureMessage: null));

        var after = DateTime.UtcNow;
        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual(0L, state.TotalUnprocessed);
        Assert.AreEqual(0L, state.ProcessedThisRun);
        Assert.AreEqual(0L, state.SkippedThisRun);
        Assert.AreEqual(0L, state.ErrorsThisRun);
        Assert.IsNull(state.LastError);
        Assert.IsNotNull(state.LastRunStarted);
        Assert.IsNotNull(state.LastRunCompleted);
        Assert.IsTrue(state.LastRunStarted >= before && state.LastRunStarted <= after);
        Assert.IsTrue(state.LastRunCompleted >= before && state.LastRunCompleted <= after);
        Assert.IsTrue(state.LastRunCompleted >= state.LastRunStarted);
        AssertLogSuffixes(
            state,
            "Run started — nothing to process, all assets already have location data.",
            "Run complete. Processed=0 Skipped=0 Errors=0");
        Assert.IsNull(reporter.GetFinalizationReceipt(request));

        var logsAfterFirstFinalization = state.GetRecentLog().ToArray();
        Assert.IsTrue(reporter.TryFinalizePredispatch(
            request,
            ProcessingRunOutcome.Completed,
            safeFailureMessage: null));
        CollectionAssert.AreEqual(logsAfterFirstFinalization, state.GetRecentLog().ToArray());

        var next = ScheduledRequest();
        Assert.IsTrue(reporter.Arm(next));
        Assert.IsFalse(reporter.TryFinalizePredispatch(
            request,
            ProcessingRunOutcome.Completed,
            safeFailureMessage: null));
        Assert.IsTrue(reporter.Abandon(next, new InvalidOperationException("test cleanup")));
    }

    [TestMethod]
    public void Completed_ReentrantStateObserverKeepsOneLocalFinalizationClaim()
    {
        var state = CompletedPriorState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = ScheduledRequest();
        var staleRequest = ScheduledRequest();
        var terminalAt = DateTimeOffset.UtcNow;
        var competingResult = new ProcessingRunResult(
            request,
            terminalAt,
            terminalAt,
            processedCount: 0,
            updatedCount: 0,
            skippedCount: 0,
            failedCount: 0,
            ProcessingRunOutcome.Failed,
            "reentrant worker failure");
        bool? nestedLocalAccepted = null;
        bool? staleLocalAccepted = null;
        ProcessingRunFinalizationAttempt? competingAttempt = null;
        var observerEntered = false;
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        state.OnChanged += ReenterFinalizersOnce;

        Assert.IsTrue(reporter.TryFinalizePredispatch(
            request,
            ProcessingRunOutcome.Completed,
            safeFailureMessage: null));

        state.OnChanged -= ReenterFinalizersOnce;
        Assert.IsTrue(observerEntered);
        Assert.AreEqual(true, nestedLocalAccepted);
        Assert.AreEqual(false, staleLocalAccepted);
        Assert.IsNotNull(competingAttempt);
        ProcessingRunFinalizationAttempt actualCompetingAttempt = competingAttempt.Value;
        Assert.AreEqual(
            ProcessingRunFinalizationDisposition.RejectedBeforeCommit,
            actualCompetingAttempt.Disposition);
        Assert.IsNull(actualCompetingAttempt.Receipt);
        Assert.IsNull(reporter.GetFinalizationReceipt(request));
        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual(0L, state.TotalUnprocessed);
        Assert.AreEqual(0L, state.ProcessedThisRun);
        Assert.AreEqual(0L, state.SkippedThisRun);
        Assert.AreEqual(0L, state.ErrorsThisRun);
        Assert.IsNull(state.LastError);

        var messages = LogMessages(state);
        Assert.AreEqual(
            1,
            messages.Count(message => message == "Run started — nothing to process, all assets already have location data."));
        Assert.AreEqual(
            1,
            messages.Count(message => message == "Run complete. Processed=0 Skipped=0 Errors=0"));
        Assert.IsFalse(messages.Any(message => message == "Run cancelled."));
        Assert.IsFalse(messages.Any(message => message.Contains("reentrant", StringComparison.Ordinal)));

        Assert.IsTrue(reporter.TryFinalizePredispatch(
            request,
            ProcessingRunOutcome.Completed,
            safeFailureMessage: null));

        return;

        void ReenterFinalizersOnce()
        {
            if (observerEntered)
            {
                return;
            }

            observerEntered = true;
            staleLocalAccepted = reporter.TryFinalizePredispatch(
                staleRequest,
                ProcessingRunOutcome.Completed,
                safeFailureMessage: null);
            nestedLocalAccepted = reporter.TryFinalizePredispatch(
                request,
                ProcessingRunOutcome.Cancelled,
                safeFailureMessage: null);
            competingAttempt = reporter.TryFinalize(
                request,
                competingResult,
                ProcessingRunFinalizationOrigin.WorkerTerminal);
        }
    }

    [TestMethod]
    public void Cancelled_PreservesPreEligibilitySnapshotAndAddsNoError()
    {
        var state = CompletedPriorState();
        var priorStart = state.LastRunStarted;
        var priorTotal = state.TotalUnprocessed;
        var priorProcessed = state.ProcessedThisRun;
        var priorSkipped = state.SkippedThisRun;
        var priorErrors = state.ErrorsThisRun;
        var priorError = state.LastError;
        var reporter = new ProcessingStateEventReporter(state);
        var request = ScheduledRequest();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));

        Assert.IsTrue(reporter.TryFinalizePredispatch(
            request,
            ProcessingRunOutcome.Cancelled,
            safeFailureMessage: null));

        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual(priorStart, state.LastRunStarted);
        Assert.AreEqual(priorTotal, state.TotalUnprocessed);
        Assert.AreEqual(priorProcessed, state.ProcessedThisRun);
        Assert.AreEqual(priorSkipped, state.SkippedThisRun);
        Assert.AreEqual(priorErrors, state.ErrorsThisRun);
        Assert.AreEqual(priorError, state.LastError);
        Assert.IsNotNull(state.LastRunCompleted);
        AssertLogSuffixes(
            state,
            "Run cancelled.",
            $"Run complete. Processed={priorProcessed} Skipped={priorSkipped} Errors={priorErrors}");
        Assert.IsNull(reporter.GetFinalizationReceipt(request));
    }

    [TestMethod]
    public void Failed_UsesSafeDetailAndPreservesPreEligibilityCounts()
    {
        var state = CompletedPriorState();
        var priorStart = state.LastRunStarted;
        var priorTotal = state.TotalUnprocessed;
        var priorProcessed = state.ProcessedThisRun;
        var priorSkipped = state.SkippedThisRun;
        var priorErrors = state.ErrorsThisRun;
        var reporter = new ProcessingStateEventReporter(state);
        var request = ScheduledRequest();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));

        Assert.IsTrue(reporter.TryFinalizePredispatch(
            request,
            ProcessingRunOutcome.Failed,
            "Scheduled work detection failed."));

        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual(priorStart, state.LastRunStarted);
        Assert.AreEqual(priorTotal, state.TotalUnprocessed);
        Assert.AreEqual(priorProcessed, state.ProcessedThisRun);
        Assert.AreEqual(priorSkipped, state.SkippedThisRun);
        Assert.AreEqual(priorErrors + 1, state.ErrorsThisRun);
        Assert.AreEqual("Fatal: Scheduled work detection failed.", state.LastError);
        AssertLogSuffixes(
            state,
            "[ERROR] Fatal: Scheduled work detection failed.",
            $"Run complete. Processed={priorProcessed} Skipped={priorSkipped} Errors={priorErrors + 1}");
        Assert.IsNull(reporter.GetFinalizationReceipt(request));
    }

    [TestMethod]
    public void ProjectionFault_CanUseExistingExactRequestAbandonment()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = ScheduledRequest();
        var projectionFailure = new InvalidOperationException("projection callback failed");
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        state.OnChanged += ThrowProjectionFailure;

        var observed = Assert.ThrowsExactly<InvalidOperationException>(() =>
            reporter.TryFinalizePredispatch(
                request,
                ProcessingRunOutcome.Completed,
                safeFailureMessage: null));

        state.OnChanged -= ThrowProjectionFailure;
        Assert.AreSame(projectionFailure, observed);
        Assert.IsTrue(reporter.Abandon(request, projectionFailure));
        Assert.IsFalse(reporter.TryFinalizePredispatch(
            request,
            ProcessingRunOutcome.Completed,
            safeFailureMessage: null));
        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual("Fatal: projection callback failed", state.LastError);
        Assert.IsTrue(reporter.Arm(ScheduledRequest()));
        return;

        void ThrowProjectionFailure()
        {
            throw projectionFailure;
        }
    }

    private static ProcessingState CompletedPriorState()
    {
        var state = new ProcessingState();
        state.StartRun(7);
        state.IncrementProcessed();
        state.IncrementSkipped();
        state.IncrementError("prior error");
        state.CompleteRun();
        return state;
    }

    private static ProcessingRunRequest ScheduledRequest()
    {
        return new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Scheduled);
    }

    private static void AssertLogSuffixes(ProcessingState state, params string[] expected)
    {
        var actual = LogMessages(state)
            .TakeLast(expected.Length)
            .ToArray();
        CollectionAssert.AreEqual(expected, actual);
    }

    private static string[] LogMessages(ProcessingState state)
    {
        return state.GetRecentLog()
            .Select(line => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..])
            .ToArray();
    }
}
