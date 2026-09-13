using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixPipeTests
{
    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets, InternalWorkerProtocolVersion.V1, false, false)]
    [DataRow(WorkerJobKind.ProcessAssets, InternalWorkerProtocolVersion.V1, true, false)]
    [DataRow(WorkerJobKind.ProcessAssets, InternalWorkerProtocolVersion.V2, false, false)]
    [DataRow(WorkerJobKind.ProcessAssets, InternalWorkerProtocolVersion.V2, true, false)]
    [DataRow(WorkerJobKind.CoordinateLookup, InternalWorkerProtocolVersion.V2, false, false)]
    [DataRow(WorkerJobKind.CoordinateLookup, InternalWorkerProtocolVersion.V2, true, false)]
    [DataRow(WorkerJobKind.CacheMutation, InternalWorkerProtocolVersion.V2, false, false)]
    [DataRow(WorkerJobKind.CacheMutation, InternalWorkerProtocolVersion.V2, true, false)]
    [DataRow(WorkerJobKind.ProcessAssets, InternalWorkerProtocolVersion.V1, false, true)]
    [DataRow(WorkerJobKind.ProcessAssets, InternalWorkerProtocolVersion.V2, true, true)]
    [DataRow(WorkerJobKind.CoordinateLookup, InternalWorkerProtocolVersion.V2, false, true)]
    [DataRow(WorkerJobKind.CacheMutation, InternalWorkerProtocolVersion.V2, true, true)]
    public async Task ConcurrentFullPipes_KeepTrailingEvidenceAndBothDrainsBeforeClassification(
        WorkerJobKind kind, InternalWorkerProtocolVersion version, bool outputFirst, bool lateCorruption)
    {
        var control = new MatrixExecutionControl(MatrixProjectionPause.FirstPipeLog);
        string fault = lateCorruption ? "lossless-pipe-corruption" : "lossless-pipe-burst";
        var host = await ProcessFailureMatrixHost.CreateAsync(fault, protocolVersion: version, control: control);
        try
        {
            Task run = host.RunAsync(kind);
            var lease = await MatrixWait.ForAsync(host.Launcher.Started, "pipes/native-launch");
            var session = lease.Session!;
            var tap = host.Launcher.Tap(lease);
            await tap.WaitForLogAsync("matrix:pipes-armed");
            await File.WriteAllTextAsync(Path.Combine(lease.Root, "stdout.release"), "release");
            await MatrixWait.ForAsync(control.ProjectionEntered.Task, "pipes/first-lossless-projection-held");
            await MatrixWait.ForAsync(session.WaitForEventDeliveryBackpressureAsync(CancellationToken.None), "pipes/full-lossless-fifo");
            Assert.AreEqual(WorkerEventDeliveryPolicy.DefaultLosslessCapacity, session.EventDeliveryObservation!.FifoHighWater);
            Assert.IsFalse(lease.HasExited);
            Assert.IsFalse(File.Exists(Path.Combine(lease.Root, "stderr-drained.marker")));

            // Stderr is independently released while stdout is still backpressured.
            await File.WriteAllTextAsync(Path.Combine(lease.Root, "stderr.release"), "release");
            await MatrixFileSignal.WaitAsync(lease.Root, "stderr-drained.marker", "pipes/independent-stderr-flood-drained");
            Assert.IsFalse(control.ProjectionRelease.Task.IsCompleted);
            Assert.IsFalse(run.IsCompleted);
            Assert.IsNotNull(host.Coordinator.ActiveOwner);
            control.ProjectionRelease.TrySetResult();
            await tap.WaitForLogAsync("matrix:pipes-drained");

            // Both wrappers hold actual bytes returned by their native pipe read.
            control.Output.Arm();
            control.Error.Arm();
            await File.WriteAllTextAsync(Path.Combine(lease.Root, "terminal.release"), "release");
            Assert.IsTrue(await MatrixWait.ForAsync(control.Output.Held, "pipes/trailing-stdout-bytes-held") > 0);
            Assert.IsTrue(await MatrixWait.ForAsync(control.Error.Held, "pipes/trailing-stderr-bytes-held") > 0);
            await MatrixWait.ForAsync(session.PhysicalExitConfirmed, "pipes/real-exit-before-trailing-evidence");
            Assert.IsTrue(lease.HasExited);
            Assert.IsFalse(session.Completion.IsCompleted);
            Assert.IsFalse(tap.Events.Any(e => e.Payload is WorkerJobTerminalPayload));
            Assert.IsFalse(host.Logs.Entries.Any(e => e.Event.Id is 6640 or 6641));
            Assert.AreEqual(0, host.Releases);
            Assert.AreEqual(0, lease.ProcessDisposeCalls);
            if (outputFirst)
            {
                control.Output.Release();
                await tap.WaitForTerminalAsync();
            }
            else
            {
                control.Error.Release();
            }
            Assert.IsFalse(session.Completion.IsCompleted, "A single drained pipe cannot finalize the session.");
            Assert.IsFalse(host.Logs.Entries.Any(e => e.Event.Id == 6641));
            Assert.IsNotNull(host.Coordinator.ActiveOwner);
            control.Output.Release();
            control.Error.Release();
            await MatrixWait.ForAsync(run, "pipes/controller-finality-after-both-drains");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "pipes/raw-terminal-exit-and-both-eof");
            Assert.AreEqual(4, raw.ExitCode);
            if (lateCorruption)
            {
                var violation = Assert.IsInstanceOfType<ImmichReverseGeo.Web.ChildWorkerLaunching.ChildWorkerProtocolObservation.ProtocolFailure>(
                    raw.FirstProtocolObservation);
                Assert.AreEqual(ImmichReverseGeo.Core.WorkerProtocol.WorkerProtocolFailureCode.InvalidLifecycle, violation.Failure.Code);
                Assert.IsFalse(tap.Events.Any(e => e.Payload is WorkerJobLogPayload { Message: "matrix-secret-51.501,-0.142-password-payload" }));
            }
            else
            {
                Assert.IsNull(raw.FirstProtocolObservation);
            }
            Assert.AreEqual(WorkerJobTerminalOutcome.Failed, ((WorkerJobTerminalPayload)raw.JobTerminal!.Payload).Outcome);
            Assert.AreEqual(262_177L + Encoding.UTF8.GetByteCount("matrix:stderr-trailing\n"), raw.StandardErrorTail.TotalBytes);
            Assert.AreEqual(65_536, raw.StandardErrorTail.Bytes.Length);
            Assert.IsTrue(raw.StandardErrorTail.IsTruncated);
            Assert.IsFalse(raw.StandardErrorTail.TotalBytesSaturated);
            Assert.IsTrue(raw.StandardErrorTail.Text.EndsWith("fixture-stderr-suffix\nmatrix:stderr-trailing\n", StringComparison.Ordinal));
            Assert.AreEqual(WorkerEventDeliveryFinality.Terminal, raw.EventDelivery!.Finality);
            Assert.AreEqual(kind == WorkerJobKind.ProcessAssets ? 4102L : 4101L, raw.EventDelivery.AcceptedLossless);
            Assert.AreEqual(raw.EventDelivery.AcceptedLossless, raw.EventDelivery.DeliveredLossless);
            Assert.AreEqual(0L, raw.EventDelivery.AcceptedSnapshots);
            Assert.AreEqual(0L, raw.EventDelivery.ReplacedSnapshots);
            Assert.IsTrue(raw.EventDelivery.EnqueueWaits > 0, "Lossless backpressure alone requires one6650.");
            Assert.AreEqual(0L, raw.EventDelivery.AbandonedItems);
            Assert.AreEqual(0L, raw.EventDelivery.StaleRejected);
            await host.JoinTelemetryAsync(lease);
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, kind,
                new(fault, "drain", MatrixRawExit.ExactManaged, 4, true,
                    MatrixTerminalAuthority.AcceptedWorkerTerminal, "failed", lateCorruption ? "protocol-failed" : "worker-failed",
                    lateCorruption ? "invalid-lifecycle" : null,
                    lateCorruption ? [6610, 6611, 6612, 6630, 6640, 6641, 6650] : [6610, 6611, 6612, 6640, 6641, 6650]));
            await ProcessFailureMatrixTelemetry.AssertCoalescingAsync(host.Logs, lease, raw.EventDelivery, tap.NotificationOwnerObservation!);
            Assert.AreEqual(1, host.Releases);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
        }
        finally
        {
            control.ReleaseAll();
            await host.DisposeAsync();
        }
        AssertClean(host);
    }

    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1)]
    [DataRow(InternalWorkerProtocolVersion.V2)]
    public async Task ReplaceablePressure_EmitsOneExactAggregateAndFreezesEachOwnersCount(InternalWorkerProtocolVersion version)
    {
        var control = new MatrixExecutionControl(MatrixProjectionPause.Eligibility);
        var host = await ProcessFailureMatrixHost.CreateAsync("replaceable-pressure", protocolVersion: version, control: control);
        try
        {
            Task first = host.RunAsync(WorkerJobKind.ProcessAssets);
            var lease = await MatrixWait.ForAsync(host.Launcher.Started, "snapshots/first-native-launch");
            var session = lease.Session!;
            var tap = host.Launcher.Tap(lease);
            await MatrixWait.ForAsync(control.ProjectionEntered.Task, "snapshots/eligibility-projection-held");
            await MatrixWait.ForAsync(session.EventDeliveryIntakeClosed!, "snapshots/all-input-accepted");
            await MatrixWait.ForAsync(session.PhysicalExitConfirmed, "snapshots/native-exit-before-projection");
            Assert.IsFalse(first.IsCompleted);
            Assert.IsNotNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(4000L, session.EventDeliveryObservation!.AcceptedSnapshots);
            Assert.AreEqual(3999L, session.EventDeliveryObservation.ReplacedSnapshots);
            Assert.AreEqual(4L, session.EventDeliveryObservation.AcceptedLossless);
            control.ProjectionRelease.TrySetResult();
            await MatrixWait.ForAsync(first, "snapshots/first-controller-finality");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "snapshots/first-raw-finality");
            Assert.AreEqual(0, raw.ExitCode);
            Assert.AreEqual(1L, raw.EventDelivery!.DeliveredSnapshots);
            Assert.AreEqual(4L, raw.EventDelivery.DeliveredLossless);
            Assert.AreEqual(ProcessingRunOutcome.Completed, host.Reporter.GetFinalizationReceipt(lease.Request)!.Result.Outcome);
            await host.JoinTelemetryAsync(lease);
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, WorkerJobKind.ProcessAssets,
                new("replaceable-pressure", "projection", MatrixRawExit.ExactManaged, 0, true,
                    MatrixTerminalAuthority.AcceptedWorkerTerminal, "completed", "completed", null,
                    [6610, 6611, 6612, 6640, 6641, 6650]));
            var owner = tap.NotificationOwnerObservation!;
            await ProcessFailureMatrixTelemetry.AssertCoalescingAsync(host.Logs, lease, raw.EventDelivery, owner);
            long frozenCount = await owner.FinalOrdinaryDispatched;
            Assert.AreEqual(1, host.Releases);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);

            await MatrixWait.ForAsync(host.RunAsync(WorkerJobKind.ProcessAssets), "snapshots/next-independent-owner-finality");
            var next = host.Launcher.Leases.Last();
            Assert.AreNotEqual(lease.Request.RunId, next.Request.RunId);
            Assert.AreNotEqual(lease.ProcessId, next.ProcessId);
            Assert.AreEqual(2, host.Releases);
            Assert.AreEqual(frozenCount, owner.OrdinaryDispatched);
            Assert.AreEqual(frozenCount, await owner.FinalOrdinaryDispatched);
            Assert.AreNotSame(owner, host.Launcher.Tap(next).NotificationOwnerObservation);
            await host.JoinTelemetryAsync(next);
            await ProcessFailureMatrixTelemetry.AssertCoalescingAsync(host.Logs, lease, raw.EventDelivery, owner);
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
        }
        finally
        {
            control.ReleaseAll();
            await host.DisposeAsync();
        }
        AssertClean(host);
    }

    private static void AssertClean(ProcessFailureMatrixHost host)
    {
        Assert.IsFalse(Directory.Exists(host.Root));
        Assert.IsNull(host.Coordinator.ActiveOwner);
        foreach (var lease in host.Launcher.Leases)
        {
            Assert.IsTrue(lease.HasExited);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.IsFalse(lease.ForcedCleanup);
            Assert.IsFalse(lease.IsRegistered);
            Assert.IsFalse(Directory.Exists(lease.Root));
        }
    }
}
