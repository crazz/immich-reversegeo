using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

[TestClass]
[TestCategory("Change67")]
public sealed class ProcessFailureMatrixAdmissionTests
{
    public static IEnumerable<object[]> BusyRows()
    {
        foreach (var owner in Enum.GetValues<WorkerJobKind>())
        {
            foreach (var contender in Enum.GetValues<WorkerJobKind>())
            {
                yield return [owner, contender];
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(BusyRows))]
    public async Task Busy_RejectsAtTheRealControllerWithoutLaunchingOrReplacingOwner(WorkerJobKind owner, WorkerJobKind contender)
    {
        var host = await ProcessFailureMatrixHost.CreateAsync("domain-failure");
        try
        {
            var admitted = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(host.Coordinator.TryAdmit(Dispatch(owner)));
            await using (admitted.Lease)
            {
                var before = host.Coordinator.ActiveOwner;
                await AssertRejectedAsync(host, contender, unavailable: false);
                Assert.AreEqual(before, host.Coordinator.ActiveOwner);
                Assert.AreEqual(0, host.Releases);
                AssertNoEffects(host);
            }

            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(1, host.Releases);
            await MatrixWait.ForAsync(host.Processing.WaitForActiveRunAsync(), "busy/no-queued-processing");
            AssertNoEffects(host);
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.IsFalse(Directory.Exists(host.Root));
    }

    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets)]
    [DataRow(WorkerJobKind.CoordinateLookup)]
    [DataRow(WorkerJobKind.CacheMutation)]
    public async Task Unavailable_ShutdownFenceRejectsEveryControllerWithoutAChild(WorkerJobKind contender)
    {
        var host = await ProcessFailureMatrixHost.CreateAsync("domain-failure");
        try
        {
            Task shutdown = host.Coordinator.BeginShutdown();
            Assert.IsFalse(((IWorkerJobArbitrationDiagnostics)host.Coordinator).Snapshot.IsAccepting);
            await AssertRejectedAsync(host, contender, unavailable: true);
            await MatrixWait.ForAsync(shutdown, "unavailable/shutdown-finality");
            Assert.IsNull(host.Coordinator.ActiveOwner);
            Assert.AreEqual(0, host.Releases);
            AssertNoEffects(host);
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.IsFalse(Directory.Exists(host.Root));
    }

    internal static async Task AssertRejectedAsync(ProcessFailureMatrixHost host, WorkerJobKind kind, bool unavailable)
    {
        switch (kind)
        {
            case WorkerJobKind.ProcessAssets:
                Assert.AreEqual(unavailable ? ProcessingRunAdmissionResult.Stopping : ProcessingRunAdmissionResult.AlreadyRunning,
                    await MatrixWait.ForAsync(host.Processing.TriggerManualAsync(), "rejected/processing"));
                break;
            case WorkerJobKind.CoordinateLookup:
                await MatrixWait.ForAsync(host.Lookup.SubmitAsync(new(47, 8, false, false, false)), "rejected/lookup");
                Assert.AreEqual(unavailable ? CoordinateLookupPagePhase.Unavailable : CoordinateLookupPagePhase.Busy, host.Lookup.State.Phase);
                Assert.IsFalse(host.Lookup.State.HasAdmittedOperation);
                Assert.IsFalse(host.Lookup.State.TerminalObserved);
                if (unavailable)
                {
                    Assert.IsTrue(Guid.TryParse(host.Lookup.State.JobId, out var attemptedId) && attemptedId != Guid.Empty,
                        "Lookup retains the rejected attempt identity, without admitting an owner or process.");
                }
                else
                {
                    Assert.IsNull(host.Lookup.State.JobId);
                }
                break;
            case WorkerJobKind.CacheMutation:
                await MatrixWait.ForAsync(host.Cache.RefreshAsync(CacheMutationSource.Gadm, "CHE"), "rejected/cache");
                Assert.AreEqual(unavailable ? CacheMutationPagePhase.Unavailable : CacheMutationPagePhase.Busy, host.Cache.State.Phase);
                Assert.IsFalse(host.Cache.State.HasAdmittedOperation);
                Assert.IsFalse(host.Cache.State.TerminalObserved);
                Assert.IsNull(host.Cache.State.JobId);
                break;
        }
    }

    private static void AssertNoEffects(ProcessFailureMatrixHost host)
    {
        Assert.AreEqual(0, host.Launcher.Leases.Length, "Admission must stop before the actual launch boundary.");
        Assert.AreEqual(0, host.Logs.Entries.Length, "No fabricated PID, readiness, terminal, exit or cancel telemetry.");
        Assert.AreEqual(0, host.ForbiddenHeavyResolutions);
        Assert.IsFalse(host.State.IsRunning);
        Assert.IsNull(host.Processing.ActiveRequest);
        Assert.IsNull(host.State.LastRunStarted);
        Assert.IsNull(host.State.LastRunCompleted);
        Assert.AreEqual(0L, host.State.ProcessedThisRun);
        Assert.IsFalse(Directory.EnumerateFiles(host.Root, "*", SearchOption.AllDirectories).Any());
    }

    private static WorkerJobDispatch Dispatch(WorkerJobKind kind)
    {
        Guid id = Guid.NewGuid();
        return kind switch
        {
            WorkerJobKind.ProcessAssets => new ProcessAssetsWorkerJobDispatch(new(id, ProcessingRunTrigger.Manual)),
            WorkerJobKind.CoordinateLookup => new CoordinateLookupWorkerJobDispatch(id,
                new(47, 8, false, false, false, new(null, []))),
            WorkerJobKind.CacheMutation => new CacheMutationWorkerJobDispatch(id,
                new(CacheMutationSource.Gadm, CacheMutationOperation.Refresh, "CHE")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}
