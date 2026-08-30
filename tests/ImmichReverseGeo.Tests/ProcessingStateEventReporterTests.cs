using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingStateEventReporterTests
{
    [TestMethod]
    public async Task Arm_RejectsOverlappingRequestWithoutStateMutation()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var first = Request();
        var second = Request();
        var notifications = 0;
        state.OnChanged += () => notifications++;

        Assert.IsTrue(reporter.Arm(first));
        Assert.IsFalse(reporter.Arm(second));

        Assert.AreEqual(0, notifications);
        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual(0, state.GetRecentLog().Count);
    }

    [TestMethod]
    public async Task Eligibility_ResetsProjectionAndRetainsCompletionAndLogs()
    {
        var state = CompletedPriorState();
        var priorCompletion = state.LastRunCompleted;
        var priorLog = state.GetRecentLog().ToArray();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        var session = await OpenAsync(reporter, request);

        Assert.AreEqual(7L, state.TotalUnprocessed);
        Assert.AreEqual(1L, state.ProcessedThisRun);
        Assert.AreEqual("prior error", state.LastError);
        await session.Session.DetermineEligibilityAsync(3);

        Assert.IsTrue(state.IsRunning);
        Assert.AreEqual(3L, state.TotalUnprocessed);
        Assert.AreEqual(0L, state.ProcessedThisRun);
        Assert.AreEqual(0L, state.SkippedThisRun);
        Assert.AreEqual(0L, state.ErrorsThisRun);
        Assert.IsNull(state.LastError);
        Assert.AreEqual(priorCompletion, state.LastRunCompleted);
        CollectionAssert.AreEqual(priorLog.Append(state.GetRecentLog().Last()).ToArray(), state.GetRecentLog().ToArray());
        AssertLogSuffixes(state, "Run started. 3 assets to process.");
    }

    [TestMethod]
    public async Task TerminalBeforeEligibility_RetainsPendingSnapshotForCancellationAndFailure()
    {
        var state = CompletedPriorState();
        var priorStart = state.LastRunStarted;
        var priorTotal = state.TotalUnprocessed;
        var priorProcessed = state.ProcessedThisRun;
        var priorSkipped = state.SkippedThisRun;
        var priorErrors = state.ErrorsThisRun;
        var priorError = state.LastError;
        var reporter = new ProcessingStateEventReporter(state);
        var cancelled = Request();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(cancelled));
        var cancelledSession = await OpenAsync(reporter, cancelled);

        await cancelledSession.Session.FinishAsync(Result(cancelledSession, 0, 0, 0, 0, ProcessingRunOutcome.Cancelled));

        AssertRetainedPendingSnapshot(state, priorStart, priorTotal, priorProcessed, priorSkipped, priorErrors, priorError);
        AssertLogSuffixes(state, "Run cancelled.", "Run complete. Processed=1 Skipped=1 Errors=1");

        var failed = Request();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(failed));
        var failedSession = await OpenAsync(reporter, failed);
        await failedSession.Session.FinishAsync(Result(failedSession, 0, 0, 0, 0, ProcessingRunOutcome.Failed, "count failed"));

        Assert.AreEqual(priorStart, state.LastRunStarted);
        Assert.AreEqual(priorTotal, state.TotalUnprocessed);
        Assert.AreEqual(priorProcessed, state.ProcessedThisRun);
        Assert.AreEqual(priorSkipped, state.SkippedThisRun);
        Assert.AreEqual(priorErrors + 1, state.ErrorsThisRun);
        Assert.AreEqual("Fatal: count failed", state.LastError);
        AssertLogSuffixes(state, "[ERROR] Fatal: count failed", "Run complete. Processed=1 Skipped=1 Errors=2");
    }

    [TestMethod]
    public async Task ProgressAndAllLogLevels_MapCountersAndDiagnosticsExactlyOnce()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        Assert.IsTrue(reporter.Arm(request));
        var session = await OpenEligibleAsync(reporter, request, 5);

        await session.Session.ReportLogAsync(ProcessingLogLevel.Trace, "resolution detail");
        await session.Session.ReportLogAsync(ProcessingLogLevel.Information, "informational detail");
        await session.Session.ReportLogAsync(ProcessingLogLevel.Warning, "skip warning");
        await session.Session.ReportLogAsync(ProcessingLogLevel.Error, "write failed");
        await session.Session.ReportUpdatedAsync();
        await session.Session.ReportSkippedAsync();
        await session.Session.ReportFailedAsync();

        Assert.AreEqual(1L, state.ProcessedThisRun);
        Assert.AreEqual(1L, state.SkippedThisRun);
        Assert.AreEqual(1L, state.ErrorsThisRun);
        Assert.AreEqual("write failed", state.LastError);
        AssertLogSuffixes(state, "resolution detail", "informational detail", "[WARN] skip warning", "[ERROR] write failed");
        Assert.AreEqual(1, state.GetRecentLog().Count(line => line.EndsWith("[ERROR] write failed", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(ProcessingRunOutcome.Completed)]
    [DataRow(ProcessingRunOutcome.Cancelled)]
    [DataRow(ProcessingRunOutcome.Failed)]
    public async Task Terminal_OrdersActivityOutcomeCompletionAndSummary(ProcessingRunOutcome outcome)
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        Assert.IsTrue(reporter.Arm(request));
        var session = await OpenEligibleAsync(reporter, request, 1);
        await session.Session.ReportUpdatedAsync();
        await using var activity = await session.Session.BeginActivityAsync("Cache download");
        var observations = new List<(bool Running, string? Activity, string? LastLog)>();
        state.OnChanged += () => observations.Add((state.IsRunning, state.CurrentActivity, state.GetRecentLog().LastOrDefault()));

        await session.Session.FinishAsync(Result(session, 1, 1, 0, 0, outcome, outcome == ProcessingRunOutcome.Failed ? "fatal" : null));

        var activityCleared = observations.FindIndex(x => x.Activity is null && x.Running && x.LastLog?.EndsWith("Run started. 1 assets to process.", StringComparison.Ordinal) == true);
        var outcomeLine = outcome switch
        {
            ProcessingRunOutcome.Cancelled => "Run cancelled.",
            ProcessingRunOutcome.Failed => "[ERROR] Fatal: fatal",
            _ => null
        };
        var completionLog = outcomeLine ?? "Run started. 1 assets to process.";
        var completion = observations.FindIndex(x => !x.Running && x.LastLog?.EndsWith(completionLog, StringComparison.Ordinal) == true);
        var summary = observations.FindIndex(x => x.LastLog?.EndsWith($"Run complete. Processed=1 Skipped=0 Errors={(outcome == ProcessingRunOutcome.Failed ? 1 : 0)}", StringComparison.Ordinal) == true);
        Assert.IsTrue(activityCleared >= 0);
        if (outcomeLine is not null)
        {
            var outcomeIndex = observations.FindIndex(x => x.LastLog?.EndsWith(outcomeLine, StringComparison.Ordinal) == true);
            Assert.IsTrue(outcomeIndex > activityCleared);
            Assert.IsTrue(completion > outcomeIndex);
        }
        else
        {
            Assert.AreEqual(-1, observations.FindIndex(x => x.LastLog?.EndsWith("Run cancelled.", StringComparison.Ordinal) == true));
            Assert.AreEqual(-1, observations.FindIndex(x => x.LastLog?.Contains("Fatal:", StringComparison.Ordinal) == true));
            Assert.IsTrue(completion > activityCleared);
        }

        Assert.IsTrue(summary > completion);
    }

    [TestMethod]
    public async Task AcceptedObservableProjections_NotifySynchronouslyForEveryProjectionKind()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        Assert.IsTrue(reporter.Arm(request));
        var session = await OpenAsync(reporter, request);
        var notifications = 0;
        state.OnChanged += () => notifications++;

        await AssertNotifiesAsync(() => session.Session.DetermineEligibilityAsync(1));
        await AssertNotifiesAsync(session.Session.ReportUpdatedAsync);
        await AssertNotifiesAsync(() => session.Session.ReportLogAsync(ProcessingLogLevel.Trace, "trace"));
        await AssertNotifiesAsync(() => session.Session.ReportLogAsync(ProcessingLogLevel.Information, "information"));
        await AssertNotifiesAsync(() => session.Session.ReportLogAsync(ProcessingLogLevel.Warning, "warning"));
        await AssertNotifiesAsync(() => session.Session.ReportLogAsync(ProcessingLogLevel.Error, "error"));
        notifications = 0;
        var activity = await session.Session.BeginActivityAsync("activity");
        Assert.IsTrue(notifications > 0);
        notifications = 0;
        await activity.DisposeAsync();
        Assert.IsTrue(notifications > 0);
        await AssertNotifiesAsync(() => session.Session.FinishAsync(Result(session, 1, 1, 0, 0, ProcessingRunOutcome.Completed)));

        async ValueTask AssertNotifiesAsync(Func<ValueTask> project)
        {
            notifications = 0;
            await project();
            Assert.IsTrue(notifications > 0);
        }
    }

    [TestMethod]
    public async Task Activities_UseIdsForDuplicatesOutOfOrderEndsAndLateEnds()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var first = Request();
        Assert.IsTrue(reporter.Arm(first));
        var session = await OpenEligibleAsync(reporter, first, 2);
        var sameLabelOne = await session.Session.BeginActivityAsync("Same label");
        var sameLabelTwo = await session.Session.BeginActivityAsync("Same label");
        var distinct = await session.Session.BeginActivityAsync("Distinct");

        await distinct.DisposeAsync();
        Assert.AreEqual("Same label", state.CurrentActivity);
        await sameLabelOne.DisposeAsync();
        Assert.AreEqual("Same label", state.CurrentActivity);
        await sameLabelOne.DisposeAsync();
        Assert.AreEqual("Same label", state.CurrentActivity);
        await session.Session.FinishAsync(Result(session, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        Assert.IsNull(state.CurrentActivity);
        await sameLabelTwo.DisposeAsync();

        var second = Request();
        Assert.IsTrue(reporter.Arm(second));
        var later = await OpenEligibleAsync(reporter, second, 1);
        await using var current = await later.Session.BeginActivityAsync("Same label");
        Assert.AreEqual("Same label", state.CurrentActivity);
        await sameLabelTwo.DisposeAsync();
        Assert.AreEqual("Same label", state.CurrentActivity);
    }

    [TestMethod]
    public async Task Correlation_InvalidAndTerminalEvents_AreIgnoredWithoutNotifications()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var current = Request();
        var wrong = Request();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(current));
        var notifications = 0;
        state.OnChanged += () => notifications++;

        var wrongSession = await OpenAsync(reporter, wrong);
        await wrongSession.Session.DetermineEligibilityAsync(1);
        await wrongSession.Session.ReportLogAsync(ProcessingLogLevel.Error, "wrong run");
        await wrongSession.Session.ReportUpdatedAsync();
        await wrongSession.Session.FinishAsync(Result(wrongSession, 1, 1, 0, 0, ProcessingRunOutcome.Completed));

        Assert.AreEqual(0, notifications);
        Assert.IsTrue(state.IsRunning);
        Assert.AreEqual(0, state.GetRecentLog().Count);

        var currentSession = await OpenEligibleAsync(reporter, current, 1);
        await currentSession.Session.FinishAsync(Result(currentSession, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        var snapshot = Snapshot(state);
        notifications = 0;

        var duplicateTerminal = await OpenAsync(reporter, current);
        await duplicateTerminal.Session.DetermineEligibilityAsync(1);
        await duplicateTerminal.Session.FinishAsync(Result(duplicateTerminal, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        Assert.AreEqual(0, notifications);
        AssertSnapshot(snapshot, state);

        var laterRequest = Request();
        Assert.IsTrue(reporter.Arm(laterRequest));
        var laterSession = await OpenEligibleAsync(reporter, laterRequest, 1);
        snapshot = Snapshot(state);
        notifications = 0;
        var stale = await OpenAsync(reporter, current);
        await stale.Session.DetermineEligibilityAsync(1);
        await stale.Session.ReportLogAsync(ProcessingLogLevel.Warning, "stale");

        Assert.AreEqual(0, notifications);
        AssertSnapshot(snapshot, state);
        await laterSession.Session.FinishAsync(Result(laterSession, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
    }

    [TestMethod]
    public async Task AcceptedEvents_NotifySynchronously_AndLogsCapAtNewestHundred()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        Assert.IsTrue(reporter.Arm(request));
        var session = await OpenEligibleAsync(reporter, request, 101);
        var notifications = 0;
        state.OnChanged += () => notifications++;

        for (var index = 1; index <= 101; index++)
        {
            var before = notifications;
            await session.Session.ReportLogAsync(index % 2 == 0 ? ProcessingLogLevel.Warning : ProcessingLogLevel.Information, $"line-{index:D3}");
            Assert.IsTrue(notifications > before);
        }

        var log = state.GetRecentLog();
        Assert.AreEqual(100, log.Count);
        Assert.IsTrue(log.First().EndsWith("line-002", StringComparison.Ordinal));
        Assert.IsTrue(log.Last().EndsWith("line-101", StringComparison.Ordinal));
        Assert.IsTrue(log.Single(line => line.EndsWith("line-002", StringComparison.Ordinal)).EndsWith("[WARN] line-002", StringComparison.Ordinal));
        Assert.IsTrue(log.Single(line => line.EndsWith("line-003", StringComparison.Ordinal)).EndsWith("line-003", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnknownAndPostTerminalRawEvents_AreNoOpsWithoutNotifications()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        Assert.IsTrue(reporter.Arm(request));
        var opened = await OpenEligibleAsync(reporter, request, 1);
        var notifications = 0;
        state.OnChanged += () => notifications++;

        await AcceptRawAsync(reporter, new ActivityEnded(request, Guid.NewGuid()));
        Assert.AreEqual(0, notifications);
        await opened.Session.FinishAsync(Result(opened, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        var snapshot = Snapshot(state);
        notifications = 0;
        var terminal = Result(opened, 0, 0, 0, 0, ProcessingRunOutcome.Completed);
        var postTerminal = new ProcessingEvent[]
        {
            new RunStarted(request, opened.StartedAtUtc), new EligibilityDetermined(request, 1),
            new ProgressChanged(request, new ProcessingProgress(1, 1, 0, 0)),
            new ActivityStarted(request, Guid.NewGuid(), "late"), new ActivityEnded(request, Guid.NewGuid()),
            new LogEmitted(request, ProcessingLogLevel.Information, "late"), new RunFinished(request, terminal)
        };
        foreach (var processingEvent in postTerminal)
        {
            await AcceptRawAsync(reporter, processingEvent);
        }

        Assert.AreEqual(0, notifications);
        AssertSnapshot(snapshot, state);
    }

    [TestMethod]
    public async Task StaleRawEventsOfEveryKind_AreNoOpsWithoutNotifications()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var stale = Request();
        var current = Request();
        Assert.IsTrue(reporter.Arm(current));
        var currentSession = await OpenEligibleAsync(reporter, current, 1);
        var snapshot = Snapshot(state);
        var notifications = 0;
        state.OnChanged += () => notifications++;
        var now = DateTimeOffset.UtcNow;
        var staleResult = new ProcessingRunResult(stale, now, now, 0, 0, 0, 0, ProcessingRunOutcome.Cancelled, null);
        foreach (var processingEvent in new ProcessingEvent[]
        {
            new RunStarted(stale, now), new EligibilityDetermined(stale, 1), new ProgressChanged(stale, new ProcessingProgress(1, 1, 0, 0)),
            new ActivityStarted(stale, Guid.NewGuid(), "stale"), new ActivityEnded(stale, Guid.NewGuid()),
            new LogEmitted(stale, ProcessingLogLevel.Trace, "stale"), new RunFinished(stale, staleResult)
        })
        {
            await AcceptRawAsync(reporter, processingEvent);
        }

        Assert.AreEqual(0, notifications);
        AssertSnapshot(snapshot, state);
        await currentSession.Session.FinishAsync(Result(currentSession, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
    }

    [TestMethod]
    public async Task Abandon_IsExactRequestCorrelatedIdempotentAndCleansActivities()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        var stale = Request();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        var opened = await OpenEligibleAsync(reporter, request, 2);
        var activity = await opened.Session.BeginActivityAsync("active scope");

        Assert.IsFalse(reporter.Abandon(stale, new InvalidOperationException("stale fault")));
        Assert.IsTrue(state.IsRunning);
        Assert.AreEqual("active scope", state.CurrentActivity);

        Assert.IsTrue(reporter.Abandon(request, new InvalidOperationException("projection failed")));
        Assert.IsFalse(reporter.Abandon(request, new InvalidOperationException("duplicate")));
        Assert.IsFalse(state.IsRunning);
        Assert.IsNull(state.CurrentActivity);
        Assert.AreEqual(1L, state.ErrorsThisRun);
        Assert.AreEqual("Fatal: projection failed", state.LastError);
        AssertLogSuffixes(state, "[ERROR] Fatal: projection failed", "Run complete. Processed=0 Skipped=0 Errors=1");

        var laterRequest = Request();
        Assert.IsTrue(reporter.Arm(laterRequest));
        var later = await OpenEligibleAsync(reporter, laterRequest, 0);
        await activity.DisposeAsync();
        Assert.IsNull(state.CurrentActivity);
        await later.Session.FinishAsync(Result(later, 0, 0, 0, 0, ProcessingRunOutcome.Completed));
    }

    [TestMethod]
    public async Task ActivityProjectionFault_AbandonClearsAdapterAndSessionScopes()
    {
        var state = new ProcessingState();
        var failEnd = true;
        var reporter = new ProcessingStateEventReporter(state, processingEvent =>
        {
            if (failEnd && processingEvent is ActivityEnded)
            {
                failEnd = false;
                throw new InvalidOperationException("activity projection failed");
            }
        });
        var request = Request();
        Assert.IsTrue(reporter.Arm(request));
        var opened = await OpenEligibleAsync(reporter, request, 1);
        var activity = await opened.Session.BeginActivityAsync("faulting activity");

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await activity.DisposeAsync());
        Assert.IsTrue(reporter.Abandon(request, failure));

        Assert.IsFalse(state.IsRunning);
        Assert.IsNull(state.CurrentActivity);
        Assert.AreEqual("Fatal: activity projection failed", state.LastError);
        AssertLogSuffixes(state, "[ERROR] Fatal: activity projection failed", "Run complete. Processed=0 Skipped=0 Errors=1");
    }

    [TestMethod]
    public async Task HandledFailureThenTerminalFailure_PreservesDomainCountAndAddsOneLegacyFatal()
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        var request = Request();
        Assert.IsTrue(reporter.Arm(request));
        var opened = await OpenEligibleAsync(reporter, request, 1);
        await opened.Session.ReportLogAsync(ProcessingLogLevel.Error, "handled failure");
        await opened.Session.ReportFailedAsync();
        var result = Result(opened, 1, 0, 0, 1, ProcessingRunOutcome.Failed, "terminal failure");

        await opened.Session.FinishAsync(result);

        Assert.AreEqual(1L, result.FailedCount);
        Assert.AreEqual(2L, state.ErrorsThisRun);
        Assert.AreEqual("Fatal: terminal failure", state.LastError);
        AssertLogSuffixes(state, "[ERROR] handled failure", "[ERROR] Fatal: terminal failure", "Run complete. Processed=0 Skipped=0 Errors=2");
        Assert.IsTrue(state.GetRecentLog().Last().EndsWith("Run complete. Processed=0 Skipped=0 Errors=2", StringComparison.Ordinal));
    }

    private static async ValueTask AcceptRawAsync(ProcessingStateEventReporter reporter, ProcessingEvent processingEvent)
    {
        var method = typeof(ProcessingStateEventReporter).GetMethod("AcceptAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((ValueTask)method.Invoke(reporter, [processingEvent, CancellationToken.None])!);
    }

    private static ProcessingState CompletedPriorState()
    {
        var state = new ProcessingState();
        state.StartRun(7);
        state.IncrementProcessed();
        state.IncrementSkipped();
        state.IncrementError("prior error");
        state.AppendLog("prior log");
        state.CompleteRun();
        return state;
    }

    private static ProcessingRunRequest Request() => new(Guid.NewGuid(), ProcessingRunTrigger.Manual);

    private sealed record OpenedSession(IProcessingRunEventSession Session, ProcessingRunRequest Request, DateTimeOffset StartedAtUtc);

    private static async Task<OpenedSession> OpenAsync(ProcessingStateEventReporter reporter, ProcessingRunRequest request)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var session = await reporter.OpenRunAsync(request, startedAtUtc);
        return new OpenedSession(session, request, startedAtUtc);
    }

    private static async Task<OpenedSession> OpenEligibleAsync(ProcessingStateEventReporter reporter, ProcessingRunRequest request, long eligible)
    {
        var opened = await OpenAsync(reporter, request);
        await opened.Session.DetermineEligibilityAsync(eligible);
        return opened;
    }

    private static ProcessingRunResult Result(OpenedSession session, long processed, long updated, long skipped, long failed, ProcessingRunOutcome outcome, string? failure = null)
    {
        return new ProcessingRunResult(session.Request, session.StartedAtUtc, session.StartedAtUtc, processed, updated, skipped, failed, outcome, failure);
    }

    private static void AssertRetainedPendingSnapshot(ProcessingState state, DateTime? start, long total, long processed, long skipped, long errors, string? error)
    {
        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual(start, state.LastRunStarted);
        Assert.AreEqual(total, state.TotalUnprocessed);
        Assert.AreEqual(processed, state.ProcessedThisRun);
        Assert.AreEqual(skipped, state.SkippedThisRun);
        Assert.AreEqual(errors, state.ErrorsThisRun);
        Assert.AreEqual(error, state.LastError);
    }

    private static (bool Running, long Total, long Processed, long Skipped, long Errors, DateTime? Started, DateTime? Completed, string? Error, string? Activity, string[] Log) Snapshot(ProcessingState state)
    {
        return (state.IsRunning, state.TotalUnprocessed, state.ProcessedThisRun, state.SkippedThisRun, state.ErrorsThisRun, state.LastRunStarted, state.LastRunCompleted, state.LastError, state.CurrentActivity, state.GetRecentLog().ToArray());
    }

    private static void AssertSnapshot((bool Running, long Total, long Processed, long Skipped, long Errors, DateTime? Started, DateTime? Completed, string? Error, string? Activity, string[] Log) expected, ProcessingState state)
    {
        var actual = Snapshot(state);
        Assert.AreEqual(expected.Running, actual.Running);
        Assert.AreEqual(expected.Total, actual.Total);
        Assert.AreEqual(expected.Processed, actual.Processed);
        Assert.AreEqual(expected.Skipped, actual.Skipped);
        Assert.AreEqual(expected.Errors, actual.Errors);
        Assert.AreEqual(expected.Started, actual.Started);
        Assert.AreEqual(expected.Completed, actual.Completed);
        Assert.AreEqual(expected.Error, actual.Error);
        Assert.AreEqual(expected.Activity, actual.Activity);
        CollectionAssert.AreEqual(expected.Log, actual.Log);
    }

    private static void AssertLogSuffixes(ProcessingState state, params string[] messages)
    {
        var log = state.GetRecentLog();
        var previous = -1;
        foreach (var message in messages)
        {
            var index = log.ToList().FindIndex(line => line.EndsWith(message, StringComparison.Ordinal));
            Assert.IsTrue(index > previous, $"Expected '{message}' after prior message.");
            previous = index;
        }
    }
}