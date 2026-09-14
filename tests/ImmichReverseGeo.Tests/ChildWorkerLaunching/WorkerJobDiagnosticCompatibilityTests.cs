using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    [TestMethod]
    [TestCategory("Change47")]
    public async Task V1DiagnosticCompatibility_LongOutputAndFailedTerminalRemainAuthoritative()
    {
        await using VersionParityFixture fixture = await CreateVersionParityFixtureAsync(
            ImmichReverseGeo.Core.WorkerJobs.InternalWorkerProtocolVersion.V1,
            9701);
        DateTimeOffset startedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1);
        DateTimeOffset eventAtUtc = startedAtUtc.AddSeconds(1);
        DateTimeOffset endedAtUtc = startedAtUtc.AddSeconds(2);
        string activity = new('a', 257);
        string diagnostic = new('d', 257);
        string failure = new('f', 257);
        DiagnosticBoundaryText nearLimit = DiagnosticBoundaryTextFactory.CreateLargestV1Log(
            fixture.Request,
            9,
            eventAtUtc);
        Assert.IsGreaterThanOrEqualTo(
            WorkerProtocolV1.MaxMessageBytes - 8,
            nearLimit.V1FrameBytes,
            "v1-diagnostic-near-original-frame-ceiling");

        var terminal = new ProcessingRunResult(
            fixture.Request,
            startedAtUtc,
            endedAtUtc,
            2,
            1,
            0,
            1,
            ProcessingRunOutcome.Failed,
            failure);
        WorkerProtocolEvent[] events =
        [
            WorkerProtocolMapper.Ready(1, DateTimeOffset.UnixEpoch),
            WorkerProtocolMapper.Map(new RunStarted(fixture.Request, startedAtUtc), 2),
            WorkerProtocolMapper.Map(new EligibilityDetermined(fixture.Request, 2), 3, eventAtUtc),
            WorkerProtocolMapper.Map(new ActivityStarted(fixture.Request, fixture.Request.RunId, activity), 4, eventAtUtc),
            WorkerProtocolMapper.Map(new LogEmitted(fixture.Request, ProcessingLogLevel.Information, diagnostic), 5, eventAtUtc),
            WorkerProtocolMapper.Map(new ActivityEnded(fixture.Request, fixture.Request.RunId), 6, eventAtUtc),
            WorkerProtocolMapper.Map(new ProgressChanged(fixture.Request, new ProcessingProgress(1, 1, 0, 0)), 7, eventAtUtc),
            WorkerProtocolMapper.Map(new ProgressChanged(fixture.Request, new ProcessingProgress(2, 1, 0, 1)), 8, eventAtUtc),
            WorkerProtocolMapper.Map(new LogEmitted(fixture.Request, ProcessingLogLevel.Information, nearLimit.Value), 9, eventAtUtc),
            WorkerProtocolMapper.Map(new RunFinished(fixture.Request, terminal), 10)
        ];

        fixture.Process.StandardOutput.Write(FrameV1(events[0]));
        await fixture.Session.Startup.WaitAsync(VersionParityBound);
        foreach (WorkerProtocolEvent @event in events.Skip(1))
        {
            fixture.Process.StandardOutput.Write(FrameV1(@event));
        }

        fixture.Process.StandardOutput.Complete();
        fixture.Process.StandardError.Complete();
        fixture.Process.Exit(0);

        ProcessingRunResult result = await fixture.Result.WaitAsync(VersionParityBound);
        string protocolObservation = fixture.Finalizer.Evidence?.Completion?.FirstProtocolObservation is
            ChildWorkerProtocolObservation.ProtocolFailure protocolFailure
                ? $"{protocolFailure.Failure.Code}/{protocolFailure.Failure.Detail}/{protocolFailure.Failure.Diagnostic}"
                : fixture.Finalizer.Evidence?.Completion?.FirstProtocolObservation?.ToString() ?? "none";
        Assert.AreEqual(
            WorkerRunAuthority.CommittedReceipt,
            fixture.Finalizer.Decision?.Authority,
            "v1-diagnostic-terminal-authority observation=" + protocolObservation);
        Assert.AreEqual(ProcessingRunOutcome.Failed, result.Outcome, "v1-diagnostic-terminal-outcome");
        Assert.AreEqual(2, result.ProcessedCount, "v1-diagnostic-terminal-processed");
        Assert.AreEqual(1, result.UpdatedCount, "v1-diagnostic-terminal-updated");
        Assert.AreEqual(1, result.FailedCount, "v1-diagnostic-terminal-failed");
        Assert.AreEqual("Fatal: " + failure, fixture.State.LastError, "v1-diagnostic-exact-failure-presentation");
        Assert.IsTrue(
            fixture.State.GetRecentLog().Any(line => line.EndsWith(diagnostic, StringComparison.Ordinal)),
            "v1-diagnostic-257-log-exact");
        Assert.IsTrue(
            fixture.State.GetRecentLog().Any(line => line.EndsWith(nearLimit.Value, StringComparison.Ordinal)),
            "v1-diagnostic-near-limit-log-exact");
        Assert.IsTrue(
            fixture.Finalizer.Decision?.Anomalies.HasFlag(WorkerRunAnomaly.TerminalExitMismatch) == true,
            "v1-diagnostic-contradictory-exit-anomaly");
        Assert.AreEqual(fixture.Request.RunId, fixture.Finalizer.Evidence?.JobId, "v1-diagnostic-one-identity");
        Assert.IsNull(fixture.State.CurrentActivity, "v1-diagnostic-activity-closed");
        AssertFinality(fixture, 1);
    }
}
