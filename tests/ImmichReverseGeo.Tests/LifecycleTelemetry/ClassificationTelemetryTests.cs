using System;
using System.Linq;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests.LifecycleTelemetry;

[TestClass]
[TestCategory("Change66")]
public sealed class ClassificationTelemetryTests
{
    private static readonly WorkerJobContext Context = new(Guid.NewGuid(), WorkerJobKind.CoordinateLookup, WorkerJobRequestOrigin.Manual);

    [TestMethod]
    [DataRow((int)WorkerJobTerminalOutcome.Completed, 0, "completed", (int)LogLevel.Information)]
    [DataRow((int)WorkerJobTerminalOutcome.Cancelled, 130, "cancelled", (int)LogLevel.Information)]
    [DataRow((int)WorkerJobTerminalOutcome.Failed, 3, "busy", (int)LogLevel.Warning)]
    [DataRow((int)WorkerJobTerminalOutcome.Failed, 4, "worker-failed", (int)LogLevel.Warning)]
    [DataRow((int)WorkerJobTerminalOutcome.Failed, 5, "infrastructure-failed", (int)LogLevel.Warning)]
    public void AgreeingTerminalTuples_KeepOneReceiptAndOneFinalEvent(int terminal, int exit, string expected, int level)
    {
        using var logs = new RecordingLifecycleLogs();
        var telemetry = Create(logs);
        telemetry.ReadyAccepted();
        telemetry.TerminalAccepted((WorkerJobTerminalOutcome)terminal, 4);
        telemetry.TerminalAccepted((WorkerJobTerminalOutcome)terminal, 4);
        telemetry.Finalized(Raw(exit), WorkerRunFailureCategory.Terminal, WorkerRunAnomaly.None,
            null, false, ChildWorkingSetSummary.NoSample);
        telemetry.Finalized(Raw(exit), WorkerRunFailureCategory.Terminal, WorkerRunAnomaly.None,
            null, false, ChildWorkingSetSummary.NoSample);
        Assert.AreEqual(1, logs.Entries.Count(entry => entry.Event.Id == 6640));
        var final = logs.Entries.Single(entry => entry.Event.Id == 6641);
        Assert.AreEqual(expected, final["process_classification"]);
        Assert.AreEqual((LogLevel)level, final.Level);
        Assert.IsNotNull(final["terminal_outcome"]);
        Assert.AreEqual(true, final["ready_observed"]);
        Assert.AreEqual("managed", final["exit_observation"]);
    }

    [TestMethod]
    public void AgreeingCompletedTerminal_DoesNotHideRetainedProtocolTransportProjectionOrCleanupFailure()
    {
        var cases = new[]
        {
            (Raw(0) with { StandardErrorFinality = ChildWorkerStreamFinality.ReadFailed.Instance }, WorkerRunAnomaly.None, "transport-failed"),
            (Raw(0) with { FirstProtocolObservation = new ChildWorkerProtocolObservation.ProtocolFailure(
                new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidSequence, "secret-coordinates-token-stack")) }, WorkerRunAnomaly.None, "protocol-failed"),
            (Raw(0), WorkerRunAnomaly.ProjectionAfterTerminal, "infrastructure-failed"),
            (Raw(0), WorkerRunAnomaly.CleanupFailure, "infrastructure-failed"),
            (Raw(0), WorkerRunAnomaly.InputTransport, "transport-failed")
        };
        foreach (var (raw, anomaly, expected) in cases)
        {
            using var logs = new RecordingLifecycleLogs();
            var telemetry = Create(logs);
            telemetry.ReadyAccepted();
            telemetry.TerminalAccepted(WorkerJobTerminalOutcome.Completed, 4);
            telemetry.Finalized(raw, WorkerRunFailureCategory.Terminal, anomaly, null, false, ChildWorkingSetSummary.NoSample);
            var final = logs.Entries.Single(entry => entry.Event.Id == 6641);
            Assert.AreEqual("completed", final["terminal_outcome"]);
            Assert.AreEqual(expected, final["process_classification"]);
            Assert.AreEqual(LogLevel.Warning, final.Level);
            Assert.IsFalse(final.Rendered.Contains("secret-coordinates-token-stack", StringComparison.Ordinal));
            Assert.IsNull(final.Exception);
        }
    }

    [TestMethod]
    [DataRow(0, "missing-terminal")]
    [DataRow(3, "protocol-failed")]
    [DataRow(4, "protocol-failed")]
    [DataRow(5, "protocol-failed")]
    [DataRow(130, "protocol-failed")]
    [DataRow(42, "crashed")]
    public void NoTerminal_RawExitNeverInventsManagedAuthority(int exit, string expected)
    {
        var evidence = Evidence(Raw(exit));
        var decision = WorkerJobNoTerminalEvidenceClassifier.Classify(evidence);
        var final = RecordDecision(evidence, decision, ready: true);
        Assert.AreEqual(expected, final["process_classification"]);
        Assert.IsNull(final["terminal_outcome"]);
    }

    [TestMethod]
    public void ExplicitManagedFacts_UseTheExistingClassifierAndRetainEarlierFailurePrecedence()
    {
        var cancellation = new ChildWorkerCancellationFacts(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(10),
            true, ChildWorkerCancelDeliveryPhase.Flushed, ChildWorkerCancellationExitRace.None,
            false, false, null, ChildWorkerTerminationIntent.Stop, null);
        var executed = Evidence(Raw(4)) with { ManagedExit = WorkerProcessExitFact.ExecutionFailure() };
        var cancelled = Evidence(Raw(130)) with { ManagedExit = WorkerProcessExitFact.ShutdownCancelled(), Cancellation = cancellation };
        var preReadyCancelled = cancelled with { Completion = Raw(130) with { Startup = ChildWorkerStartupObservation.PreReadyExit.Instance } };
        foreach (var (evidence, ready, expected) in new[]
        {
            (executed, true, "worker-failed"),
            (cancelled, true, "cancelled"),
            (preReadyCancelled, false, "cancelled"),
            (cancelled with { Completion = Raw(130) with { StandardErrorFinality = ChildWorkerStreamFinality.ReadFailed.Instance } }, true, "transport-failed"),
            (executed with { ProjectedProtocolFailure = new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidCorrelation, "secret") }, true, "protocol-failed")
        })
        {
            var decision = WorkerJobNoTerminalEvidenceClassifier.Classify(evidence);
            var final = RecordDecision(evidence, decision, ready);
            Assert.AreEqual(expected, final["process_classification"]);
            Assert.AreEqual(ready, final["ready_observed"]);
        }
    }

    [TestMethod]
    public void MissingExit_IsUnavailableAndCannotClaimAnObservedTerminalMismatch()
    {
        using var logs = new RecordingLifecycleLogs();
        var telemetry = Create(logs);
        telemetry.ReadyAccepted();
        telemetry.TerminalAccepted(WorkerJobTerminalOutcome.Completed, 4);
        telemetry.Finalized(Raw(null), WorkerRunFailureCategory.Terminal, WorkerRunAnomaly.TerminalExitMismatch,
            null, false, ChildWorkingSetSummary.NoSample);
        var final = logs.Entries.Single(entry => entry.Event.Id == 6641);
        Assert.AreEqual("crashed", final["process_classification"]);
        Assert.AreEqual("unavailable", final["exit_observation"]);
        Assert.IsNull(final["exit_code"]);
        Assert.AreEqual("completed", final["terminal_outcome"]);
    }

    private static RecordingLifecycleLogs.Entry RecordDecision(WorkerJobNoTerminalEvidence evidence,
        WorkerJobNoTerminalDecision decision, bool ready)
    {
        using var logs = new RecordingLifecycleLogs();
        var telemetry = Create(logs);
        if (ready)
        {
            telemetry.ReadyAccepted();
        }
        telemetry.Finalized(evidence.Completion!, decision.Category, decision.Anomalies,
            evidence.Cancellation, false, ChildWorkingSetSummary.NoSample);
        return logs.Entries.Single(entry => entry.Event.Id == 6641);
    }

    private static WorkerJobTelemetry Create(RecordingLifecycleLogs logs)
    {
        var clock = new CancellationTestClock();
        var telemetry = new WorkerJobTelemetry(logs.CreateLogger(LifecycleEventCatalog.Category), clock, Context);
        telemetry.ProcessOwned(42, clock.GetTimestamp());
        return telemetry;
    }

    private static WorkerJobNoTerminalEvidence Evidence(ChildWorkerCompletionObservation raw) => new()
    {
        Context = Context, Completion = raw, LastPhase = WorkerRunTransportPhase.EvidenceFinal,
        IntendedProtocolVersion = InternalWorkerProtocolVersion.V2
    };

    private static ChildWorkerCompletionObservation Raw(int? exit) => new(42, Context.JobId,
        ChildWorkerStartupObservation.ReadyAccepted.Instance, exit is not null, exit,
        ChildWorkerStreamFinality.EndOfStream.Instance, ChildWorkerStreamFinality.EndOfStream.Instance,
        null, null, null, new ChildWorkerStandardErrorTail([], 0, false, false),
        Context.JobKind, InternalWorkerProtocolVersion.V2);
}
