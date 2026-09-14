using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
public sealed class ProcessFailureMatrixStartupTests
{
    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets)]
    [DataRow(WorkerJobKind.CoordinateLookup)]
    [DataRow(WorkerJobKind.CacheMutation)]
    public async Task LiveWorkerWithoutReady_TimesOutThenContainsBeforeReleasingOwner(WorkerJobKind kind)
    {
        var clock = new MatrixClock();
        var host = await ProcessFailureMatrixHost.CreateAsync("never-ready", clock);
        try
        {
            Task run = host.RunAsync(kind);
            var lease = await MatrixWait.ForAsync(host.Launcher.Started, "readiness/native-launch");
            await MatrixFileSignal.WaitAsync(lease.Root, "matrix-entered.marker", "readiness/child-entered-never-ready");
            await clock.WaitForOneShotAsync(TimeSpan.FromSeconds(30), "readiness/exact-production-timer");
            Assert.IsFalse(lease.Session!.Startup.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(29_999));
            Assert.IsFalse(lease.Session.Startup.IsCompleted);
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.AreEqual(0, host.Releases);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyTimedOut>(
                await MatrixWait.ForAsync(lease.Session.Startup, "readiness/30000ms-timeout"));
            await clock.WaitForOneShotAsync(TimeSpan.FromSeconds(10), "readiness/containment-grace-timer");
            Assert.IsFalse(run.IsCompleted);
            Assert.IsNotNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(0, host.Releases);
            clock.Advance(TimeSpan.FromSeconds(10));
            await MatrixWait.ForAsync(run, "readiness/contained-controller-finality");
            var raw = await MatrixWait.ForAsync(lease.CompleteAsync(), "readiness/exit-and-drains");
            await host.JoinTelemetryAsync(lease);
            Assert.IsNull(raw.JobTerminal);
            Assert.IsFalse(raw.AcceptedRunStarted);
            Assert.AreEqual(0, lease.WrittenInput.Length, "Ready was never accepted, so neither execute nor cancel is written.");
            Assert.AreEqual(0, host.Launcher.Tap(lease).Events.Length);
            Assert.AreEqual(1, lease.TreeKillCalls);
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
            Assert.AreEqual(1, host.Releases);
            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
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
            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, kind,
                new("never-ready", "readiness-timeout", MatrixRawExit.PresentPlatformRaw, null, false,
                    MatrixTerminalAuthority.None, null, "startup-failed", "invalid-lifecycle",
                    [6610, 6611, 6620, 6622, 6623, 6630, 6641], ForcedStop: true));
            Assert.AreEqual(raw.ExitCode, host.Logs.Entries.Single(e => e.Event.Id == 6641)["exit_code"]);
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

    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets)]
    [DataRow(WorkerJobKind.CoordinateLookup)]
    [DataRow(WorkerJobKind.CacheMutation)]
    public async Task MissingExecutable_UsesRealOsStartFailureWithoutFabricatingProcessEvidence(WorkerJobKind kind)
    {
        var host = await ProcessFailureMatrixHost.CreateAsync("spawn-failure");
        try
        {
            await host.RunAsync(kind);
            var lease = host.Launcher.Leases.Single();
            Assert.IsNull(lease.Session);
            Assert.IsNull(lease.ProcessId);
            Assert.AreEqual(0, lease.ProcessDisposeCalls);
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.AreEqual(0, lease.WrittenInput.Length);
            Assert.AreEqual(0, host.Launcher.Tap(lease).Events.Length);
            Assert.IsFalse(File.Exists(lease.CapturePath));
            Assert.IsFalse(Directory.EnumerateFiles(lease.Root).Any());
            Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
            Assert.IsFalse(Directory.EnumerateFiles(host.Root, "*", SearchOption.AllDirectories).Any());
            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(1, host.Releases);
            switch (kind)
            {
                case WorkerJobKind.ProcessAssets:
                    Assert.AreEqual(ProcessingRunOutcome.Failed, host.Reporter.GetFinalizationReceipt(lease.Request)!.Result.Outcome);
                    break;
                case WorkerJobKind.CoordinateLookup:
                    Assert.AreEqual(CoordinateLookupPagePhase.Unavailable, host.Lookup.State.Phase);
                    Assert.IsFalse(host.Lookup.State.HasAdmittedOperation);
                    break;
                case WorkerJobKind.CacheMutation:
                    Assert.AreEqual(CacheMutationPagePhase.Unavailable, host.Cache.State.Phase);
                    Assert.IsFalse(host.Cache.State.HasAdmittedOperation);
                    break;
            }

            ProcessFailureMatrixTelemetry.AssertCatalog(host.Logs, lease, kind,
                new("spawn-failure", "os-start", MatrixRawExit.Absent, null, false, MatrixTerminalAuthority.None,
                    null, "startup-failed", null, [6610, 6641],
                    Child: MatrixProbe.NotApplicable, Streams: MatrixProbe.NotApplicable));
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
