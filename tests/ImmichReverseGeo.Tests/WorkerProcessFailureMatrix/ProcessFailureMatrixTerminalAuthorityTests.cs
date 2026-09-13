using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixTerminalAuthorityTests
{
    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1, false)]
    [DataRow(InternalWorkerProtocolVersion.V2, false)]
    [DataRow(InternalWorkerProtocolVersion.V1, true)]
    [DataRow(InternalWorkerProtocolVersion.V2, true)]
    public async Task CompletedReceipt_SurvivesLaterProtocolFailureWithoutLosingAbnormalFinality(
        InternalWorkerProtocolVersion version, bool contradictoryExit)
    {
        string fault = contradictoryExit ? "completed-contradictory-exit" : "completed-late-protocol";
        var host = await ProcessFailureMatrixHost.CreateAsync(fault, protocolVersion: version);
        try
        {
            Task run = host.RunAsync(WorkerJobKind.ProcessAssets);
            var lease = await MatrixWait.ForAsync(host.Launcher.Started, "terminal-authority/native-launch");
            var acceptedTerminal = host.Launcher.Tap(lease).WaitForTerminalAsync();
            var firstFailure = lease.Session!.FirstTerminalPreventingObservation;
            Task first = await MatrixWait.ForAsync(Task.WhenAny(acceptedTerminal, firstFailure), "terminal-authority/accept-or-fault");
            if (first == firstFailure)
            {
                var observed = await firstFailure;
                string detail = observed.Reason is ChildWorkerFaultContainmentReason.ProtocolFailure protocol
                    ? $"{protocol.Failure.Code}/{protocol.Failure.Detail}: {protocol.Failure.Diagnostic}"
                    : observed.Reason.GetType().Name;
                throw new AssertFailedException($"Terminal fixture was rejected before acceptance: {detail}; "
                    + string.Join(",", host.Launcher.Tap(lease).Events.Select(e => e.Type)));
            }

            var terminal = await acceptedTerminal;
            var before = host.Reporter.GetFinalizationReceipt(lease.Request);
            Assert.IsNotNull(before);
            Assert.AreEqual(ProcessingRunOutcome.Completed, before.Result.Outcome);
            Assert.AreEqual(WorkerJobTerminalOutcome.Completed, ((WorkerJobTerminalPayload)terminal.Payload).Outcome);
            Assert.IsFalse(run.IsCompleted);
            Assert.IsFalse(lease.HasExited);
            Assert.IsNotNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(0, host.Releases);
            Assert.IsFalse(host.Logs.Entries.Any(e => e.Event.Id is 6630 or 6641));
            var completedAt = host.State.LastRunCompleted;
            var startedAt = host.State.LastRunStarted;
            var counts = (host.State.ProcessedThisRun, host.State.SkippedThisRun, host.State.ErrorsThisRun);
            var activity = host.State.CurrentActivity;

            // The child cannot emit the late fault until the committed receipt has
            // been inspected. A fixed owned file releases its real stdout writer.
            await File.WriteAllTextAsync(Path.Combine(lease.Root, "late-fault.release"), "release");
            await MatrixWait.ForAsync(run, "terminal-authority/abnormal-finality");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "terminal-authority/both-drains");
            await host.JoinTelemetryAsync(lease);
            Assert.AreEqual(contradictoryExit ? 4 : 0, raw.ExitCode);
            if (contradictoryExit)
            {
                Assert.IsNull(raw.FirstProtocolObservation, "A contradictory raw exit is not a protocol-frame violation.");
            }
            else
            {
                Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(raw.FirstProtocolObservation);
            }
            Assert.AreSame(before, host.Reporter.GetFinalizationReceipt(lease.Request), "A late fault never commits another receipt.");
            Assert.AreEqual(completedAt, host.State.LastRunCompleted);
            Assert.AreEqual(startedAt, host.State.LastRunStarted);
            Assert.AreEqual(counts, (host.State.ProcessedThisRun, host.State.SkippedThisRun, host.State.ErrorsThisRun));
            Assert.AreEqual(activity, host.State.CurrentActivity);
            Assert.AreEqual(1, host.Launcher.Tap(lease).Events.Count(e => e.Payload is WorkerJobTerminalPayload));
            Assert.IsFalse(host.Launcher.Tap(lease).Events.Any(e => e.Payload is WorkerJobLogPayload));
            Assert.AreEqual(1, host.Releases);
            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, WorkerJobKind.ProcessAssets,
                new(fault, "after-committed-terminal", MatrixRawExit.ExactManaged, contradictoryExit ? 4 : 0, true,
                    MatrixTerminalAuthority.AcceptedWorkerTerminal, "completed",
                    contradictoryExit ? "terminal-exit-mismatch" : "protocol-failed",
                    contradictoryExit ? null : "invalid-lifecycle",
                    contradictoryExit ? [6610, 6611, 6612, 6640, 6641] : [6610, 6611, 6612, 6640, 6630, 6641]));
        }
        finally
        {
            await host.DisposeAsync();
        }

        foreach (var lease in host.Launcher.Leases)
        {
            Assert.IsFalse(lease.ForcedCleanup);
            Assert.IsFalse(lease.IsRegistered);
            Assert.IsFalse(Directory.Exists(lease.Root));
        }

        Assert.IsFalse(Directory.Exists(host.Root));
    }
}
