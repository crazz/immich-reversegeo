using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixHostShutdownTests
{
    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets, false)]
    [DataRow(WorkerJobKind.ProcessAssets, true)]
    [DataRow(WorkerJobKind.CoordinateLookup, false)]
    [DataRow(WorkerJobKind.CoordinateLookup, true)]
    [DataRow(WorkerJobKind.CacheMutation, false)]
    [DataRow(WorkerJobKind.CacheMutation, true)]
    public async Task ActualGenericHostShutdown_FencesAdmissionAndWaitsForChildDrainsAndOwnerRelease(WorkerJobKind kind, bool lateDiagnostic)
    {
        var clock = new MatrixClock();
        string fault = lateDiagnostic ? "unresponsive" : "unresponsive-quiet";
        var host = await ProcessFailureMatrixHost.CreateAsync(fault, clock, startHost: true);
        try
        {
            Task run = host.RunAsync(kind);
            var lease = await MatrixWait.ForAsync(host.Launcher.Started, "shutdown/native-launch");
            await host.Launcher.Tap(lease).WaitForLogAsync("matrix:armed");
            Task shutdown = host.StopHostAsync();
            Assert.IsFalse(((IWorkerJobArbitrationDiagnostics)host.Coordinator).Snapshot.IsAccepting);
            Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(
                host.Coordinator.TryAdmit(ProcessFailureMatrixFixtureTests.Dispatch(lease, kind)));
            await clock.WaitForOneShotAsync(TimeSpan.FromSeconds(10), "shutdown/exact-grace-timer");
            await MatrixFileSignal.WaitAsync(lease.Root, "matrix-cancel-observed.marker", "shutdown/child-received-cancel");
            if (lateDiagnostic)
            {
                // The child marker precedes its diagnostic write. Settle the
                // intended projection before making the kill deadline eligible.
                if (kind == WorkerJobKind.ProcessAssets)
                {
                    await host.Launcher.Tap(lease).WaitForLogAsync("matrix:cancel-observed");
                }
                else
                {
                    var rejection = await MatrixWait.ForAsync(lease.Session!.FirstTerminalPreventingObservation,
                        "shutdown/disposed-page-rejected-late-diagnostic");
                    Assert.IsInstanceOfType<ChildWorkerFaultContainmentReason.SinkFailure>(rejection.Reason);
                    await MatrixWait.ForAsync(lease.Session.EventDeliveryIntakeClosed!,
                        "shutdown/rejected-projection-closed-intake");
                }
            }
            clock.Advance(TimeSpan.FromMilliseconds(9_999));
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.IsNotNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(0, host.Releases);
            Assert.IsFalse(host.Logs.Entries.Any(e => e.Event.Id == 6641));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await MatrixWait.ForAsync(shutdown, "shutdown/actual-host-stop-finality");
            Assert.IsTrue(lease.HasExited);
            Assert.AreEqual(1, lease.TreeKillCalls);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.IsTrue(lease.Session!.Completion.IsCompletedSuccessfully);
            Assert.IsTrue(lease.Session.Settlement.IsCompletedSuccessfully);
            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(1, host.Releases);
            await MatrixWait.ForAsync(run, "shutdown/controller-attempt-finality");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "shutdown/captured-exit-and-drains");
            Assert.IsNull(raw.JobTerminal);
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
            Assert.AreEqual(1, host.Launcher.Leases.Length, "Shutdown must not enqueue or retry the active job.");

            // A harness-only join, after Host.StopAsync has independently finished.
            await host.JoinTelemetryAsync(lease);
            // Page disposal rejects late projection. The existing classifier gives
            // that retained failure priority over the subsequently successful kill.
            bool projectionFailure = lateDiagnostic && kind != WorkerJobKind.ProcessAssets;
            if (projectionFailure)
            {
                Assert.IsInstanceOfType<ChildWorkerProtocolObservation.SinkFailure>(raw.FirstProtocolObservation);
                Assert.AreEqual(ImmichReverseGeo.Web.WorkerEventDelivery.WorkerEventDeliveryFinality.ProjectionFailed,
                    raw.EventDelivery!.Finality);
                Assert.AreEqual(1L, raw.EventDelivery.StaleRejected);
            }
            else
            {
                Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(raw.FirstProtocolObservation);
            }

            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, kind,
                new(fault, "host-shutdown", MatrixRawExit.PresentPlatformRaw, null, true,
                    MatrixTerminalAuthority.None, null, projectionFailure ? "infrastructure-failed" : "forced-stop",
                    projectionFailure ? null : "invalid-lifecycle",
                    projectionFailure ? [6610, 6611, 6612, 6620, 6622, 6623, 6641] : [6610, 6611, 6612, 6620, 6622, 6623, 6630, 6641],
                    ForcedStop: true));
            Assert.AreEqual(raw.ExitCode, host.Logs.Entries.Single(e => e.Event.Id == 6641)["exit_code"]);
            Assert.AreEqual(10_000L, host.Logs.Entries.Single(e => e.Event.Id == 6622)["grace_elapsed_ms"]);
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.AreEqual(0, clock.ActiveTimerCount);
        foreach (var lease in host.Launcher.Leases)
        {
            Assert.IsFalse(lease.ForcedCleanup);
            Assert.IsFalse(lease.IsRegistered);
            Assert.IsFalse(Directory.Exists(lease.Root));
        }

        Assert.IsFalse(Directory.Exists(host.Root));
    }
}
