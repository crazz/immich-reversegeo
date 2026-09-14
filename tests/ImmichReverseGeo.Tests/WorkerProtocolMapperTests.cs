using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class WorkerProtocolMapperTests
{
    [TestMethod]
    public void Map_Ready_UsesLiteralProtocolFacts()
    {
        var mapped = WorkerProtocolMapper.Ready(1, WorkerProtocolV1TestData.Start);

        Assert.AreEqual("lifecycle", mapped.Category);
        Assert.AreEqual("ready", mapped.Type);
        Assert.AreEqual(1L, mapped.Sequence);
        Assert.AreEqual(WorkerProtocolV1TestData.Start, mapped.TimestampUtc);
        Assert.IsNull(mapped.RunId);
        Assert.IsInstanceOfType<ReadyPayload>(mapped.Payload);
    }

    [TestMethod]
    public void Map_RunStarted_ConvertsEveryTriggerAndPreservesSourceTimestamp()
    {
        AssertRunStarted(ProcessingRunTrigger.Manual, "manual", 2);
        AssertRunStarted(ProcessingRunTrigger.Scheduled, "scheduled", 3);
        AssertRunStarted(ProcessingRunTrigger.RunOnce, "run-once", 4);
    }

    [TestMethod]
    public void Map_NonterminalEvents_PreserveLiteralEnvelopeAndPayloadFacts()
    {
        var request = new ProcessingRunRequest(WorkerProtocolV1TestData.RunId, ProcessingRunTrigger.RunOnce);
        var eligibility = WorkerProtocolMapper.Map(new EligibilityDetermined(request, long.MaxValue), 5, WorkerProtocolV1TestData.Midpoint);
        AssertMapped(eligibility, "lifecycle", "eligibility-determined", 5, WorkerProtocolV1TestData.Midpoint, request.RunId);
        Assert.AreEqual(long.MaxValue, ((EligibilityDeterminedPayload)eligibility.Payload).EligibleCount);

        var progress = WorkerProtocolMapper.Map(new ProgressChanged(request, new ProcessingProgress(3, 1, 1, 1)), 6, WorkerProtocolV1TestData.Midpoint);
        AssertMapped(progress, "progress", "progress-changed", 6, WorkerProtocolV1TestData.Midpoint, request.RunId);
        var progressPayload = (ProgressChangedPayload)progress.Payload;
        Assert.AreEqual(3L, progressPayload.ProcessedCount);
        Assert.AreEqual(1L, progressPayload.UpdatedCount);
        Assert.AreEqual(1L, progressPayload.SkippedCount);
        Assert.AreEqual(1L, progressPayload.FailedCount);

        var activityStarted = WorkerProtocolMapper.Map(new ActivityStarted(request, WorkerProtocolV1TestData.ActivityId, "cache download"), 7, WorkerProtocolV1TestData.Midpoint);
        AssertMapped(activityStarted, "activity", "activity-started", 7, WorkerProtocolV1TestData.Midpoint, request.RunId);
        var activityStartedPayload = (ActivityStartedPayload)activityStarted.Payload;
        Assert.AreEqual(WorkerProtocolV1TestData.ActivityId, activityStartedPayload.ActivityId);
        Assert.AreEqual("cache download", activityStartedPayload.Label);

        var activityEnded = WorkerProtocolMapper.Map(new ActivityEnded(request, WorkerProtocolV1TestData.ActivityId), 8, WorkerProtocolV1TestData.Midpoint);
        AssertMapped(activityEnded, "activity", "activity-ended", 8, WorkerProtocolV1TestData.Midpoint, request.RunId);
        Assert.AreEqual(WorkerProtocolV1TestData.ActivityId, ((ActivityEndedPayload)activityEnded.Payload).ActivityId);

        AssertLog(ProcessingLogLevel.Trace, "trace", 9);
        AssertLog(ProcessingLogLevel.Information, "information", 10);
        AssertLog(ProcessingLogLevel.Warning, "warning", 11);
        AssertLog(ProcessingLogLevel.Error, "error", 12);
    }

    [TestMethod]
    public void Map_AllTerminalOutcomes_PreserveEveryFixedTerminalFact()
    {
        var runId = Guid.Parse("89abcdef-0123-4567-89ab-cdef01234567");
        var startedAtUtc = new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var endedAtUtc = new DateTimeOffset(2027, 1, 2, 3, 9, 5, TimeSpan.Zero);
        AssertTerminal(ProcessingRunTrigger.Manual, ProcessingRunOutcome.Completed, "completed", "manual", 41, runId, startedAtUtc, endedAtUtc, 6, 2, 3, 1, null);
        AssertTerminal(ProcessingRunTrigger.Scheduled, ProcessingRunOutcome.Cancelled, "cancelled", "scheduled", 42, runId, startedAtUtc, endedAtUtc, 5, 0, 4, 1, null);
        AssertTerminal(ProcessingRunTrigger.RunOnce, ProcessingRunOutcome.Failed, "failed", "run-once", 43, runId, startedAtUtc, endedAtUtc, 4, 3, 0, 1, "fatal lookup failure");
    }

    private static void AssertRunStarted(ProcessingRunTrigger trigger, string expectedTrigger, long sequence)
    {
        var request = new ProcessingRunRequest(WorkerProtocolV1TestData.RunId, trigger);
        var mapped = WorkerProtocolMapper.Map(new RunStarted(request, WorkerProtocolV1TestData.Start), sequence, WorkerProtocolV1TestData.End);
        AssertMapped(mapped, "lifecycle", "run-started", sequence, WorkerProtocolV1TestData.Start, request.RunId);
        var payload = (RunStartedPayload)mapped.Payload;
        Assert.AreEqual(expectedTrigger, payload.Trigger);
        Assert.AreEqual(WorkerProtocolV1TestData.Start, payload.StartedAtUtc);
    }

    private static void AssertLog(ProcessingLogLevel level, string expectedLevel, long sequence)
    {
        var request = new ProcessingRunRequest(WorkerProtocolV1TestData.RunId, ProcessingRunTrigger.Manual);
        var mapped = WorkerProtocolMapper.Map(new LogEmitted(request, level, "message"), sequence, WorkerProtocolV1TestData.Midpoint);
        AssertMapped(mapped, "diagnostic", "log-emitted", sequence, WorkerProtocolV1TestData.Midpoint, request.RunId);
        var payload = (LogEmittedPayload)mapped.Payload;
        Assert.AreEqual(expectedLevel, payload.Level);
        Assert.AreEqual("message", payload.Message);
    }

    private static void AssertTerminal(ProcessingRunTrigger trigger, ProcessingRunOutcome outcome, string expectedType, string expectedTrigger, long sequence, Guid runId, DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc, long processed, long updated, long skipped, long failed, string? failureMessage)
    {
        var request = new ProcessingRunRequest(runId, trigger);
        var result = new ProcessingRunResult(request, startedAtUtc, endedAtUtc, processed, updated, skipped, failed, outcome, failureMessage);
        var mapped = WorkerProtocolMapper.Map(new RunFinished(request, result), sequence);
        AssertMapped(mapped, "terminal", expectedType, sequence, endedAtUtc, runId);
        var payload = (TerminalPayload)mapped.Payload;
        Assert.AreEqual(expectedTrigger, payload.Trigger);
        Assert.AreEqual(startedAtUtc, payload.StartedAtUtc);
        Assert.AreEqual(endedAtUtc, payload.EndedAtUtc);
        Assert.AreEqual(processed, payload.ProcessedCount);
        Assert.AreEqual(updated, payload.UpdatedCount);
        Assert.AreEqual(skipped, payload.SkippedCount);
        Assert.AreEqual(failed, payload.FailedCount);
        Assert.AreEqual(failureMessage, payload.FailureMessage);
    }

    private static void AssertMapped(WorkerProtocolEvent mapped, string category, string type, long sequence, DateTimeOffset timestampUtc, Guid? runId)
    {
        Assert.AreEqual(category, mapped.Category);
        Assert.AreEqual(type, mapped.Type);
        Assert.AreEqual(sequence, mapped.Sequence);
        Assert.AreEqual(timestampUtc, mapped.TimestampUtc);
        Assert.AreEqual(runId, mapped.RunId);
    }
}
