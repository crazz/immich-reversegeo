using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
[DoNotParallelize]
public sealed class ProcessFailureMatrixFailurePrecedenceTests
{
    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets)]
    [DataRow(WorkerJobKind.CoordinateLookup)]
    [DataRow(WorkerJobKind.CacheMutation)]
    public async Task EarlierProtocolFault_RemainsFailedWhenLaterUserStopJoinsContainment(WorkerJobKind kind)
    {
        var clock = new MatrixClock();
        var host = await ProcessFailureMatrixHost.CreateAsync("protocol-before-stop", clock);
        try
        {
            var run = host.RunAsync(kind);
            var lease = await host.Launcher.NextLaunchAsync();
            var first = await MatrixWait.ForAsync(lease.Session!.FirstTerminalPreventingObservation,
                "precedence/retained-protocol-before-stop");
            Assert.IsInstanceOfType<ChildWorkerFaultContainmentReason.ProtocolFailure>(first.Reason);
            await clock.WaitForOneShotAsync(TimeSpan.FromSeconds(10), "precedence/containment-grace");
            Assert.AreEqual(ChildWorkerTerminationIntent.FaultContainment, lease.Session.CancellationFacts!.FirstIntent);
            var stop = kind switch
            {
                WorkerJobKind.ProcessAssets => host.Processing.StopActiveRun()!,
                WorkerJobKind.CoordinateLookup => host.Lookup.CancelAsync(),
                _ => host.Cache.CancelAsync()
            };
            clock.Advance(TimeSpan.FromMilliseconds(9_999));
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.IsFalse(run.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await MatrixWait.ForAsync(stop, "precedence/later-stop-joins-original-containment");
            await MatrixWait.ForAsync(run, "precedence/controller-finality");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "precedence/native-exit-and-drains");
            Assert.AreSame(first, await lease.Session.FirstTerminalPreventingObservation);
            Assert.IsNull(raw.JobTerminal);
            Assert.IsFalse(host.Launcher.Tap(lease).Events.Any(e => e.Payload is WorkerJobLogPayload));
            Assert.AreEqual(1, lease.TreeKillCalls);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.AreEqual(1, host.Releases);
            Assert.IsNull(host.Coordinator.ActiveOwner);
            switch (kind)
            {
                case WorkerJobKind.ProcessAssets:
                    Assert.AreEqual(ProcessingRunOutcome.Failed, host.Reporter.GetFinalizationReceipt(lease.Request)!.Result.Outcome);
                    break;
                case WorkerJobKind.CoordinateLookup:
                    Assert.AreEqual(CoordinateLookupPagePhase.Failed, host.Lookup.State.Phase);
                    break;
                case WorkerJobKind.CacheMutation:
                    Assert.AreEqual(CacheMutationPagePhase.Failed, host.Cache.State.Phase);
                    break;
            }
            await host.JoinTelemetryAsync(lease);
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, kind,
                new("protocol-before-stop", "retained-protocol-before-stop", MatrixRawExit.PresentPlatformRaw, null, true,
                    MatrixTerminalAuthority.None, null, "protocol-failed", "invalid-sequence",
                    [6610, 6611, 6612, 6630, 6620, 6622, 6623, 6641], ForcedStop: true));
        }
        finally
        {
            await host.DisposeAsync();
        }
        Assert.AreEqual(0, clock.ActiveTimerCount);
        Assert.IsFalse(Directory.Exists(host.Root));
        foreach (var lease in host.Launcher.Leases)
        {
            Assert.IsFalse(lease.ForcedCleanup);
            Assert.IsFalse(lease.IsRegistered);
            Assert.IsFalse(Directory.Exists(lease.Root));
        }
    }
}
