using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingEventReporterExactCoverageTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task PreEligibility_AllPublicOperationsAreRejectedWithoutEmission()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportUpdatedAsync().AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportSkippedAsync().AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportFailedAsync().AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportLogAsync(ProcessingLogLevel.Trace, "detail").AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.BeginActivityAsync("work").AsTask());
        await session.DetermineEligibilityAsync(0);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.DetermineEligibilityAsync(0).AsTask());
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined) }, reporter.Events.Select(x => x.GetType()).ToArray());
    }

    [TestMethod]
    public async Task CompletedFinishBeforeEligibility_IsRejectedWithoutTerminal()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed)).AsTask());
        CollectionAssert.AreEqual(new[] { typeof(RunStarted) }, reporter.Events.Select(x => x.GetType()).ToArray());
    }

    [TestMethod]
    public async Task PreCountCancellationAndFailure_EmitExactStartToFinishSequences()
    {
        var reporter = new RecordingProcessingEventReporter();
        var cancelledRequest = Request();
        var cancelled = await reporter.OpenRunAsync(cancelledRequest, Start);
        await cancelled.FinishAsync(Result(cancelledRequest, 0, 0, 0, 0, ProcessingRunOutcome.Cancelled));
        var failedRequest = Request();
        var failed = await reporter.OpenRunAsync(failedRequest, Start);
        await failed.FinishAsync(Result(failedRequest, 0, 0, 0, 0, ProcessingRunOutcome.Failed));
        foreach (var request in new[] { cancelledRequest, failedRequest })
        {
            CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(RunFinished) }, reporter.EventsFor(request).Select(x => x.GetType()).ToArray());
        }
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, ((RunFinished)reporter.EventsFor(cancelledRequest)[^1]).Result.Outcome);
        Assert.AreEqual(ProcessingRunOutcome.Failed, ((RunFinished)reporter.EventsFor(failedRequest)[^1]).Result.Outcome);
    }

    [TestMethod]
    public async Task AfterFinish_AllPublicOperationsAreRejectedWithoutEmission()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(0);
        var activity = await session.BeginActivityAsync("work");
        await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.DetermineEligibilityAsync(0).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportUpdatedAsync().AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportSkippedAsync().AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportFailedAsync().AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportLogAsync(ProcessingLogLevel.Information, "late").AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.BeginActivityAsync("late").AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed)).AsTask());
        await activity.DisposeAsync();
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(ActivityStarted), typeof(ActivityEnded), typeof(RunFinished) }, reporter.Events.Select(x => x.GetType()).ToArray());
    }

    [TestMethod]
    public void Payloads_RejectNullBlankEmptyNegativeAndOverflowValues()
    {
        var request = Request();
        Assert.ThrowsExactly<ArgumentNullException>(() => new RunStarted(null!, Start));
        Assert.ThrowsExactly<ArgumentNullException>(() => new EligibilityDetermined(null!, 0));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ProgressChanged(request, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new RunFinished(request, null!));
        Assert.ThrowsExactly<ArgumentException>(() => new ActivityStarted(request, Guid.NewGuid(), " "));
        Assert.ThrowsExactly<ArgumentException>(() => new ActivityEnded(request, Guid.Empty));
        foreach (var progress in new[] { new[] { -1L, 0L, 0L, 0L }, new[] { 0L, -1L, 0L, 0L }, new[] { 0L, 0L, -1L, 0L }, new[] { 0L, 0L, 0L, -1L } })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProcessingProgress(progress[0], progress[1], progress[2], progress[3]));
        }
        Assert.ThrowsExactly<OverflowException>(() => new ProcessingProgress(long.MaxValue, long.MaxValue, 1, 0));
    }

    [TestMethod]
    public void LogPayloads_AcceptEveryDefinedLevelAndRejectInvalid()
    {
        var request = Request();
        foreach (var level in new[] { ProcessingLogLevel.Trace, ProcessingLogLevel.Information, ProcessingLogLevel.Warning, ProcessingLogLevel.Error })
        {
            Assert.AreEqual(level, new LogEmitted(request, level, "plain").Level);
        }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LogEmitted(request, (ProcessingLogLevel)99, "plain"));
    }

    [TestMethod]
    public async Task MixedDispositions_EmitCoherentMonotonicSnapshotsAndCompletedHandledFailure()
    {
        var reporter = new RecordingProcessingEventReporter(); var request = Request(); var session = await reporter.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(3); await session.ReportUpdatedAsync(); await session.ReportSkippedAsync(); await session.ReportFailedAsync();
        await session.FinishAsync(Result(request, 3, 1, 1, 1, ProcessingRunOutcome.Completed));
        var snapshots = reporter.Events.OfType<ProgressChanged>().Select(x => x.Progress).ToArray();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, snapshots.Select(x => x.ProcessedCount).ToArray());
        CollectionAssert.AreEqual(new long[] { 1, 1, 1 }, snapshots.Select(x => x.UpdatedCount).ToArray());
        CollectionAssert.AreEqual(new long[] { 0, 1, 1 }, snapshots.Select(x => x.SkippedCount).ToArray());
        CollectionAssert.AreEqual(new long[] { 0, 0, 1 }, snapshots.Select(x => x.FailedCount).ToArray());
        Assert.AreEqual(ProcessingRunOutcome.Completed, ((RunFinished)reporter.Events[^1]).Result.Outcome);
    }

    [TestMethod]
    public async Task FatalRunFailure_DoesNotAddPerAssetFailure()
    {
        var reporter = new RecordingProcessingEventReporter(); var request = Request(); var session = await reporter.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(1); await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Failed));
        var result = ((RunFinished)reporter.Events[^1]).Result;
        Assert.AreEqual(0, result.FailedCount); Assert.AreEqual("fatal", result.FailureMessage);
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForSessionGate_EmitsNothing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reporter = new RecordingProcessingEventReporter { BeforeAcceptAsync = async (e, _) => { if (e is LogEmitted) { entered.TrySetResult(); await release.Task; } } };
        var request = Request(); var session = await reporter.OpenRunAsync(request, Start); await session.DetermineEligibilityAsync(0);
        var holding = session.ReportLogAsync(ProcessingLogLevel.Information, "holding").AsTask(); await entered.Task;
        using var cts = new CancellationTokenSource(); var waiting = session.ReportLogAsync(ProcessingLogLevel.Information, "cancelled", cts.Token).AsTask(); cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => waiting); release.TrySetResult(); await holding;
        Assert.AreEqual(1, reporter.Events.OfType<LogEmitted>().Count());
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForBoundedCapacity_EmitsNothingBeforeLinearization()
    {
        var reporter = new RecordingProcessingEventReporter(); var request = Request(); var session = await reporter.OpenRunAsync(request, Start); await session.DetermineEligibilityAsync(0); reporter.SetCapacity(0);
        using var cts = new CancellationTokenSource(); var reporting = session.ReportLogAsync(ProcessingLogLevel.Information, "cancelled", cts.Token).AsTask(); cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => reporting); Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count());
    }

    [TestMethod]
    public async Task UpdatedAfterWriteThenCancellation_RetainsAcceptedProgressThroughPostAcceptanceHook()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reporter = new RecordingProcessingEventReporter { AfterAcceptAsync = async (e, _) => { if (e is ProgressChanged) { accepted.TrySetResult(); await release.Task; } } };
        var request = Request(); var session = await reporter.OpenRunAsync(request, Start); await session.DetermineEligibilityAsync(1);
        using var cts = new CancellationTokenSource(); var reporting = session.ReportUpdatedAsync().AsTask(); await accepted.Task; cts.Cancel(); Assert.IsTrue(cts.IsCancellationRequested); release.TrySetResult(); await reporting;
        await session.FinishAsync(Result(request, 1, 1, 0, 0, ProcessingRunOutcome.Cancelled));
        Assert.AreEqual(1, ((RunFinished)reporter.Events[^1]).Result.UpdatedCount);
    }

    [TestMethod]
    public async Task CommittedDisposition_WaitingForSessionGate_IsRetainedAfterCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reporter = new RecordingProcessingEventReporter
        {
            BeforeAcceptAsync = async (processingEvent, _) =>
            {
                if (processingEvent is LogEmitted)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            }
        };
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(1);
        var holding = session.ReportLogAsync(ProcessingLogLevel.Information, "holding").AsTask();
        await entered.Task;
        var disposition = session.ReportUpdatedAsync().AsTask();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.IsTrue(cancellation.IsCancellationRequested);
        release.TrySetResult();
        await Task.WhenAll(holding, disposition);
        await session.FinishAsync(Result(request, 1, 1, 0, 0, ProcessingRunOutcome.Cancelled));
        Assert.AreEqual(1, ((RunFinished)reporter.Events[^1]).Result.UpdatedCount);
    }

    [TestMethod]
    public async Task CommittedDisposition_WaitingForBoundedCapacity_IsRetainedAfterCancellation()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(1);
        reporter.SetCapacity(0);
        var disposition = session.ReportUpdatedAsync().AsTask();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.IsTrue(cancellation.IsCancellationRequested);
        reporter.ReleaseCapacity();
        await disposition;
        reporter.ReleaseCapacity();
        await session.FinishAsync(Result(request, 1, 1, 0, 0, ProcessingRunOutcome.Cancelled));
        Assert.AreEqual(1, ((RunFinished)reporter.Events[^1]).Result.UpdatedCount);
    }

    [TestMethod]
    public async Task BoundedCapacity_BlocksBeforeAcceptanceUntilReleased()
    {
        var reporter = new RecordingProcessingEventReporter(); var request = Request(); var session = await reporter.OpenRunAsync(request, Start); await session.DetermineEligibilityAsync(0); reporter.SetCapacity(0);
        var reporting = session.ReportLogAsync(ProcessingLogLevel.Information, "backpressured").AsTask(); Assert.IsFalse(reporting.IsCompleted); Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count());
        reporter.ReleaseCapacity(); await reporting; Assert.AreEqual(1, reporter.Events.OfType<LogEmitted>().Count());
    }

    [TestMethod]
    public async Task ConcurrentSessions_InterleaveWithoutCrossContamination()
    {
        var reporter = new RecordingProcessingEventReporter(); var firstRequest = Request(); var secondRequest = Request();
        var first = await reporter.OpenRunAsync(firstRequest, Start); var second = await reporter.OpenRunAsync(secondRequest, Start);
        await Task.WhenAll(first.DetermineEligibilityAsync(2).AsTask(), second.DetermineEligibilityAsync(1).AsTask());
        await Task.WhenAll(first.ReportUpdatedAsync().AsTask(), first.ReportSkippedAsync().AsTask(), second.ReportFailedAsync().AsTask());
        await Task.WhenAll(first.FinishAsync(Result(firstRequest, 2, 1, 1, 0, ProcessingRunOutcome.Completed)).AsTask(), second.FinishAsync(Result(secondRequest, 1, 0, 0, 1, ProcessingRunOutcome.Completed)).AsTask());
        Assert.AreEqual(2, ((RunFinished)reporter.EventsFor(firstRequest)[^1]).Result.ProcessedCount); Assert.AreEqual(1, ((RunFinished)reporter.EventsFor(secondRequest)[^1]).Result.FailedCount);
    }

    [TestMethod]
    public async Task CancellationUnwindAndDuplicateDispose_EmitOneNonCancelledActivityEnd()
    {
        var reporter = new RecordingProcessingEventReporter(); var request = Request(); var session = await reporter.OpenRunAsync(request, Start); await session.DetermineEligibilityAsync(0); var activity = await session.BeginActivityAsync("unwind");
        using var cts = new CancellationTokenSource(); cts.Cancel(); await activity.DisposeAsync(); await activity.DisposeAsync(); await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Cancelled));
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(ActivityStarted), typeof(ActivityEnded), typeof(RunFinished) }, reporter.Events.Select(x => x.GetType()).ToArray());
    }

    [TestMethod]
    public async Task Finish_ClosesActivitiesInStartOrderBeforeTerminal()
    {
        var reporter = new RecordingProcessingEventReporter(); var request = Request(); var session = await reporter.OpenRunAsync(request, Start); await session.DetermineEligibilityAsync(0);
        var first = await session.BeginActivityAsync("first"); var second = await session.BeginActivityAsync("second"); await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        var ids = reporter.Events.OfType<ActivityStarted>().Select(x => x.ActivityId).ToArray(); CollectionAssert.AreEqual(ids, reporter.Events.OfType<ActivityEnded>().Select(x => x.ActivityId).ToArray()); Assert.IsInstanceOfType<RunFinished>(reporter.Events[^1]); await first.DisposeAsync(); await second.DisposeAsync();
    }

    [TestMethod]
    [DataRow(nameof(RunStarted))]
    [DataRow(nameof(EligibilityDetermined))]
    [DataRow(nameof(ProgressChanged))]
    [DataRow(nameof(ActivityStarted))]
    [DataRow(nameof(ActivityEnded))]
    [DataRow(nameof(LogEmitted))]
    [DataRow(nameof(RunFinished))]
    public async Task ReporterFaultAtEveryEventKind_BreaksSessionWithoutRecursiveEvents(string eventName)
    {
        var reporter = new RecordingProcessingEventReporter { FailureFactory = e => e.GetType().Name == eventName ? new InvalidOperationException("sink failed") : null }; var request = Request();
        if (eventName == nameof(RunStarted))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reporter.OpenRunAsync(request, Start).AsTask());
            Assert.AreEqual(0, reporter.Events.Count);
            return;
        }

        var session = await reporter.OpenRunAsync(request, Start);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            if (eventName == nameof(EligibilityDetermined))
            {
                await session.DetermineEligibilityAsync(0);
            }
            else
            {
                await session.DetermineEligibilityAsync(eventName == nameof(ProgressChanged) ? 1 : 0);
                if (eventName == nameof(ProgressChanged))
                {
                    await session.ReportUpdatedAsync();
                }
                else if (eventName == nameof(ActivityStarted))
                {
                    await session.BeginActivityAsync("work");
                }
                else if (eventName == nameof(ActivityEnded))
                {
                    var activity = await session.BeginActivityAsync("work");
                    await activity.DisposeAsync();
                }
                else if (eventName == nameof(LogEmitted))
                {
                    await session.ReportLogAsync(ProcessingLogLevel.Error, "failure");
                }
                else
                {
                    await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
                }
            }
        });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportLogAsync(ProcessingLogLevel.Information, "late").AsTask());
    }

    [TestMethod]
    public async Task CompatibilityFixtures_MapDiagnosticsWithoutRuntimeWiring()
    {
        var reporter = new RecordingProcessingEventReporter(); var request = Request(); var session = await reporter.OpenRunAsync(request, Start); await session.DetermineEligibilityAsync(2);
        await CompatibilityFixture.ResolvedThenWriteFailsAsync(session, "Asset 7: City, State, Country", "Asset 7 [write]: database failed");
        await CompatibilityFixture.ExistingUiWarningAsync(session, "Asset 8: no country found, skipping.");
        await CompatibilityFixture.NoCityLoggerOnlyAsync(session); await session.FinishAsync(Result(request, 2, 0, 1, 1, ProcessingRunOutcome.Completed));
        var logs = reporter.Events.OfType<LogEmitted>().ToArray(); CollectionAssert.AreEqual(new[] { ProcessingLogLevel.Trace, ProcessingLogLevel.Error, ProcessingLogLevel.Warning }, logs.Select(x => x.Level).ToArray()); CollectionAssert.AreEqual(new[] { "Asset 7: City, State, Country", "Asset 7 [write]: database failed", "Asset 8: no country found, skipping." }, logs.Select(x => x.Message).ToArray());
        var finalProgress = reporter.Events.OfType<ProgressChanged>().Last().Progress; Assert.AreEqual(1, finalProgress.SkippedCount); Assert.AreEqual(1, finalProgress.FailedCount);
    }

    [TestMethod]
    public void DiagnosticPayloads_ArePlainAndTransportNeutral()
    {
        var forbidden = new[] { typeof(Exception), typeof(CancellationToken), typeof(Delegate) }; var types = new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(ProgressChanged), typeof(ProcessingProgress), typeof(ActivityStarted), typeof(ActivityEnded), typeof(LogEmitted), typeof(RunFinished), typeof(ProcessingRunResult) };
        foreach (var type in types) { Assert.IsFalse(type.GetProperties().Any(p => forbidden.Contains(p.PropertyType) || new[] { "Version", "Sequence", "Timestamp", "Envelope", "ExitCode", "StackTrace" }.Contains(p.Name))); }
        Assert.AreEqual(ProcessingLogLevel.Trace, (ProcessingLogLevel)typeof(ProcessingEventDiagnosticVocabulary).GetField(nameof(ProcessingEventDiagnosticVocabulary.ResolvedLocationDetailLevel))!.GetRawConstantValue()!);
        Assert.AreEqual(ProcessingLogLevel.Warning, (ProcessingLogLevel)typeof(ProcessingEventDiagnosticVocabulary).GetField(nameof(ProcessingEventDiagnosticVocabulary.ExistingUiWarningLevel))!.GetRawConstantValue()!);
        Assert.AreEqual(ProcessingLogLevel.Error, (ProcessingLogLevel)typeof(ProcessingEventDiagnosticVocabulary).GetField(nameof(ProcessingEventDiagnosticVocabulary.ExistingUiErrorLevel))!.GetRawConstantValue()!);
        Assert.IsFalse((bool)typeof(ProcessingEventDiagnosticVocabulary).GetField(nameof(ProcessingEventDiagnosticVocabulary.LoggerOnlyDiagnosticsProduceEvents))!.GetRawConstantValue()!);
    }

    private static ProcessingRunRequest Request() => new(Guid.NewGuid(), ProcessingRunTrigger.Manual);
    private static ProcessingRunResult Result(ProcessingRunRequest request, long processed, long updated, long skipped, long failed, ProcessingRunOutcome outcome) => new(request, Start, Start, processed, updated, skipped, failed, outcome, outcome == ProcessingRunOutcome.Failed ? "fatal" : null);

    private static class CompatibilityFixture
    {
        public static async Task ResolvedThenWriteFailsAsync(IProcessingRunEventSession session, string trace, string error) { await session.ReportLogAsync(ProcessingEventDiagnosticVocabulary.ResolvedLocationDetailLevel, trace); await session.ReportLogAsync(ProcessingEventDiagnosticVocabulary.ExistingUiErrorLevel, error); await session.ReportFailedAsync(); }
        public static ValueTask ExistingUiWarningAsync(IProcessingRunEventSession session, string warning) => session.ReportLogAsync(ProcessingEventDiagnosticVocabulary.ExistingUiWarningLevel, warning);
        public static ValueTask NoCityLoggerOnlyAsync(IProcessingRunEventSession session) => session.ReportSkippedAsync();
    }
}
