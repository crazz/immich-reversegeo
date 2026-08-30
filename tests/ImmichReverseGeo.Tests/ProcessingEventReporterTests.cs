using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingEventReporterTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Session_EmitsOrderedLifecycleAndAccounting()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);

        await session.DetermineEligibilityAsync(2);
        await session.ReportUpdatedAsync();
        await session.ReportSkippedAsync();
        await session.FinishAsync(Result(request, 2, 1, 1, 0, ProcessingRunOutcome.Completed));

        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(ProgressChanged), typeof(ProgressChanged), typeof(RunFinished) }, reporter.Events.Select(x => x.GetType()).ToArray());
        Assert.AreEqual(2, ((ProgressChanged)reporter.Events[3]).Progress.ProcessedCount);
    }

    [TestMethod]
    public async Task Session_RejectsPreEligibilityAndPostFinishOperations()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportUpdatedAsync().AsTask());
        await session.DetermineEligibilityAsync(0);
        await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportLogAsync(ProcessingLogLevel.Information, "late").AsTask());
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(RunFinished) }, reporter.Events.Select(x => x.GetType()).ToArray());
    }

    [TestMethod]
    public async Task Session_AllowsPreCountCancelledTerminalOnly()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Cancelled));

        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(RunFinished) }, reporter.Events.Select(x => x.GetType()).ToArray());
    }

    [TestMethod]
    public async Task Session_RejectsDispositionsBeyondEligibility()
    {
        var reporter = new RecordingProcessingEventReporter();
        var session = await reporter.OpenRunAsync(Request(), Start);
        await session.DetermineEligibilityAsync(0);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportUpdatedAsync().AsTask());
        Assert.AreEqual(0, reporter.Events.OfType<ProgressChanged>().Count());
    }

    [TestMethod]
    public async Task ActivityScopes_AreUniqueAndFinishClosesThem()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(0);
        var first = await session.BeginActivityAsync("download");
        var second = await session.BeginActivityAsync("download");
        await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        await first.DisposeAsync();
        await second.DisposeAsync();

        var starts = reporter.Events.OfType<ActivityStarted>().ToArray();
        var ends = reporter.Events.OfType<ActivityEnded>().ToArray();
        Assert.AreEqual(2, starts.Length);
        Assert.AreNotEqual(starts[0].ActivityId, starts[1].ActivityId);
        CollectionAssert.AreEquivalent(starts.Select(x => x.ActivityId).ToArray(), ends.Select(x => x.ActivityId).ToArray());
        Assert.IsInstanceOfType<RunFinished>(reporter.Events[^1]);
    }

    [TestMethod]
    public async Task ReporterFault_BreaksSessionWithoutRecursiveFinish()
    {
        var reporter = new RecordingProcessingEventReporter { FailureFactory = processingEvent => processingEvent is EligibilityDetermined ? new InvalidOperationException("sink failed") : null };
        var session = await reporter.OpenRunAsync(Request(), Start);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.DetermineEligibilityAsync(1).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportUpdatedAsync().AsTask());
        CollectionAssert.AreEqual(new[] { typeof(RunStarted) }, reporter.Events.Select(x => x.GetType()).ToArray());
    }

    [TestMethod]
    public async Task ConcurrentDispositions_AreLinearizedWithoutLoss()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(20);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => session.ReportSkippedAsync().AsTask()));
        await session.FinishAsync(Result(request, 20, 0, 20, 0, ProcessingRunOutcome.Completed));

        var snapshots = reporter.Events.OfType<ProgressChanged>().ToArray();
        Assert.AreEqual(20, snapshots.Length);
        CollectionAssert.AreEqual(Enumerable.Range(1, 20).Select(x => (long)x).ToArray(), snapshots.Select(x => x.Progress.ProcessedCount).ToArray());
    }

    [TestMethod]
    public async Task CancellationBeforeAcceptance_EmitsNothingAndBreaksSession()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reporter = new RecordingProcessingEventReporter
        {
            BeforeAcceptAsync = async (processingEvent, _) =>
            {
                if (processingEvent is LogEmitted)
                {
                    entered.TrySetResult();
                    await gate.Task;
                }
            }
        };
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(0);
        using var cancellation = new CancellationTokenSource();
        var logging = session.ReportLogAsync(ProcessingLogLevel.Information, "cancelled", cancellation.Token).AsTask();
        await entered.Task;
        cancellation.Cancel();
        gate.SetResult();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => logging);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportLogAsync(ProcessingLogLevel.Information, "accepted").AsTask());

        Assert.AreEqual(0, reporter.Events.OfType<LogEmitted>().Count());
    }

    [TestMethod]
    public async Task Session_RejectsEveryPreEligibilityOperationAndDuplicateLifecycle()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportLogAsync(ProcessingLogLevel.Trace, "detail").AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.BeginActivityAsync("work").AsTask());
        await session.DetermineEligibilityAsync(0);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.DetermineEligibilityAsync(0).AsTask());
        await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed)).AsTask());
    }

    [TestMethod]
    public async Task PreCountFailureAndMismatchedResultsAreConstrained()
    {
        var reporter = new RecordingProcessingEventReporter();
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => session.FinishAsync(Result(Request(), 0, 0, 0, 0, ProcessingRunOutcome.Failed)).AsTask());
        await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Failed));
        CollectionAssert.AreEqual(new[] { typeof(RunStarted), typeof(RunFinished) }, reporter.Events.Select(x => x.GetType()).ToArray());
    }

    [TestMethod]
    public async Task HandledFailureCompletesAndFatalFailureDoesNotIncrementIt()
    {
        var reporter = new RecordingProcessingEventReporter();
        var completedRequest = Request();
        var completed = await reporter.OpenRunAsync(completedRequest, Start);
        await completed.DetermineEligibilityAsync(1);
        await completed.ReportFailedAsync();
        await completed.FinishAsync(Result(completedRequest, 1, 0, 0, 1, ProcessingRunOutcome.Completed));
        var failedRequest = Request();
        var failed = await reporter.OpenRunAsync(failedRequest, Start);
        await failed.DetermineEligibilityAsync(0);
        await failed.FinishAsync(Result(failedRequest, 0, 0, 0, 0, ProcessingRunOutcome.Failed));
        Assert.AreEqual(1, ((RunFinished)reporter.EventsFor(completedRequest)[^1]).Result.FailedCount);
        Assert.AreEqual(0, ((RunFinished)reporter.EventsFor(failedRequest)[^1]).Result.FailedCount);
    }

    [TestMethod]
    public async Task SessionsAreIsolatedAndReporterCapacityBackpressures()
    {
        var reporter = new RecordingProcessingEventReporter();
        var firstRequest = Request();
        var secondRequest = Request();
        var first = await reporter.OpenRunAsync(firstRequest, Start);
        var second = await reporter.OpenRunAsync(secondRequest, Start);
        await first.DetermineEligibilityAsync(1);
        await second.DetermineEligibilityAsync(0);
        reporter.SetCapacity(0);
        var delayed = first.ReportUpdatedAsync().AsTask();
        Assert.IsFalse(delayed.IsCompleted);
        reporter.ReleaseCapacity();
        await delayed;
        reporter.ReleaseCapacity();
        await first.FinishAsync(Result(firstRequest, 1, 1, 0, 0, ProcessingRunOutcome.Completed));
        reporter.ReleaseCapacity();
        await second.FinishAsync(Result(secondRequest, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        Assert.AreEqual(4, reporter.EventsFor(firstRequest).Count);
        Assert.AreEqual(3, reporter.EventsFor(secondRequest).Count);
    }

    [TestMethod]
    public async Task NoOpReporterAndActivityDisposalAreSafe()
    {
        var request = Request();
        var session = await NoOpProcessingEventReporter.Instance.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(0);
        var activity = await session.BeginActivityAsync("work");
        await activity.DisposeAsync();
        await activity.DisposeAsync();
        await session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
    }

    [TestMethod]
    [DataRow("RunStarted")]
    [DataRow("ProgressChanged")]
    [DataRow("ActivityStarted")]
    [DataRow("ActivityEnded")]
    [DataRow("LogEmitted")]
    [DataRow("RunFinished")]
    public async Task ReporterFaultAtEveryEventKindBreaksTheSession(string eventType)
    {
        var reporter = new RecordingProcessingEventReporter { FailureFactory = processingEvent => processingEvent.GetType().Name == eventType ? new InvalidOperationException("sink failed") : null };
        var request = Request();
        if (eventType == nameof(RunStarted))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reporter.OpenRunAsync(request, Start).AsTask());
            return;
        }

        var session = await reporter.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(eventType == nameof(ProgressChanged) ? 1 : 0);
        if (eventType == nameof(ProgressChanged))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportUpdatedAsync().AsTask());
        }
        else if (eventType == nameof(ActivityStarted))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.BeginActivityAsync("work").AsTask());
        }
        else if (eventType == nameof(ActivityEnded))
        {
            var reporter2 = new RecordingProcessingEventReporter { FailureFactory = processingEvent => processingEvent is ActivityEnded ? new InvalidOperationException("sink failed") : null };
            var activitySession = await reporter2.OpenRunAsync(Request(), Start);
            await activitySession.DetermineEligibilityAsync(0);
            var activity = await activitySession.BeginActivityAsync("work");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => activity.DisposeAsync().AsTask());
            return;
        }
        else if (eventType == nameof(LogEmitted))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportLogAsync(ProcessingLogLevel.Error, "failure").AsTask());
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed)).AsTask());
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReportLogAsync(ProcessingLogLevel.Information, "late").AsTask());
    }

    [TestMethod]
    public void DiagnosticVocabularyAndPayloadsRemainPlainAndTransportNeutral()
    {
        Assert.AreEqual(ProcessingLogLevel.Trace, (ProcessingLogLevel)typeof(ProcessingEventDiagnosticVocabulary).GetField(nameof(ProcessingEventDiagnosticVocabulary.ResolvedLocationDetailLevel))!.GetRawConstantValue()!);
        Assert.AreEqual(ProcessingLogLevel.Warning, (ProcessingLogLevel)typeof(ProcessingEventDiagnosticVocabulary).GetField(nameof(ProcessingEventDiagnosticVocabulary.ExistingUiWarningLevel))!.GetRawConstantValue()!);
        Assert.AreEqual(ProcessingLogLevel.Error, (ProcessingLogLevel)typeof(ProcessingEventDiagnosticVocabulary).GetField(nameof(ProcessingEventDiagnosticVocabulary.ExistingUiErrorLevel))!.GetRawConstantValue()!);
        Assert.IsFalse((bool)typeof(ProcessingEventDiagnosticVocabulary).GetField(nameof(ProcessingEventDiagnosticVocabulary.LoggerOnlyDiagnosticsProduceEvents))!.GetRawConstantValue()!);
        var forbidden = new[] { typeof(Exception), typeof(CancellationToken), typeof(Delegate) };
        var eventTypes = new[] { typeof(RunStarted), typeof(EligibilityDetermined), typeof(ProgressChanged), typeof(ActivityStarted), typeof(ActivityEnded), typeof(LogEmitted), typeof(RunFinished) };
        foreach (var eventType in eventTypes)
        {
            Assert.IsFalse(eventType.GetProperties().Any(property => forbidden.Contains(property.PropertyType)));
            Assert.IsFalse(eventType.GetProperties().Any(property => new[] { "Version", "Sequence", "Timestamp", "Envelope", "ExitCode" }.Contains(property.Name)));
            Assert.IsFalse(eventType.GetCustomAttributes(inherit: false).Any(attribute => attribute.GetType().Namespace?.Contains("Json", StringComparison.Ordinal) == true));
        }
    }

    [TestMethod]
    public async Task FinishBlockedOnActivityEndMakesConcurrentDisposeLocalNoOp()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reporter = new RecordingProcessingEventReporter { BeforeAcceptAsync = async (processingEvent, _) => { if (processingEvent is ActivityEnded) { entered.TrySetResult(); await release.Task; } } };
        var request = Request();
        var session = await reporter.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(0);
        var activity = await session.BeginActivityAsync("work");
        var finish = session.FinishAsync(Result(request, 0, 0, 0, 0, ProcessingRunOutcome.Completed)).AsTask();
        await entered.Task;
        var dispose = activity.DisposeAsync().AsTask();
        Assert.IsFalse(dispose.IsCompleted);
        release.SetResult();
        await Task.WhenAll(finish, dispose);
        Assert.AreEqual(1, reporter.Events.OfType<ActivityEnded>().Count());
        Assert.IsInstanceOfType<RunFinished>(reporter.Events[^1]);
    }

    [TestMethod]
    public async Task EqualValueDifferentRequestInstanceIsRejected()
    {
        var request = Request();
        var equalValueDifferentInstance = new ProcessingRunRequest(request.RunId, request.Trigger);
        var session = await NoOpProcessingEventReporter.Instance.OpenRunAsync(request, Start);
        await session.DetermineEligibilityAsync(0);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => session.FinishAsync(Result(equalValueDifferentInstance, 0, 0, 0, 0, ProcessingRunOutcome.Completed)).AsTask());
        Assert.ThrowsExactly<ArgumentException>(() => new RunFinished(request, Result(equalValueDifferentInstance, 0, 0, 0, 0, ProcessingRunOutcome.Completed)));
    }

    [TestMethod]
    public void EventPayloads_ValidateIdentityAndPlainValues()
    {
        var request = Request();
        Assert.ThrowsExactly<ArgumentException>(() => new RunStarted(request, Start.ToOffset(TimeSpan.FromHours(1))));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new EligibilityDetermined(request, -1));
        Assert.ThrowsExactly<ArgumentException>(() => new ActivityStarted(request, Guid.Empty, "work"));
        Assert.ThrowsExactly<ArgumentException>(() => new LogEmitted(request, ProcessingLogLevel.Trace, " "));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LogEmitted(request, (ProcessingLogLevel)99, "message"));
        Assert.ThrowsExactly<ArgumentException>(() => new ProcessingProgress(1, 0, 0, 0));
    }

    private static ProcessingRunRequest Request() => new(Guid.NewGuid(), ProcessingRunTrigger.Manual);

    private static ProcessingRunResult Result(ProcessingRunRequest request, long processed, long updated, long skipped, long failed, ProcessingRunOutcome outcome) =>
        new(request, Start, Start, processed, updated, skipped, failed, outcome, outcome == ProcessingRunOutcome.Failed ? "fatal" : null);
}
