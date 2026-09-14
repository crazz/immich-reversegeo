using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.CacheDeletionCoordination;

[TestClass]
[TestCategory("Change52")]
public sealed class CacheMaintenanceCoordinatorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task MaintenanceReservation_BlocksEveryRegisteredWorkerWithSafeNonWorkerOwner()
    {
        await using var coordinator = CreateCoordinator();
        CacheMaintenanceAdmissionResult.Reserved reserved =
            Assert.IsInstanceOfType<CacheMaintenanceAdmissionResult.Reserved>(
                coordinator.TryReserveCacheMaintenance(CacheMaintenanceRequestOrigin.GeoBoundariesPage));
        try
        {
            foreach (WorkerJobDispatch dispatch in WorkerDispatches())
            {
                WorkerJobAdmissionResult.Busy busy =
                    Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(coordinator.TryAdmit(dispatch));
                var owner = Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.CacheMaintenance>(
                    busy.ActiveOwner);
                Assert.AreEqual(CacheMaintenanceRequestOrigin.GeoBoundariesPage, owner.Operation.Origin);
                Assert.IsFalse(busy.ActiveOwner is ExclusiveHeavyOwnerBusyMetadata.Worker,
                    "maintenance must not fabricate worker metadata");
            }

            var exactOwner = Assert.IsInstanceOfType<ExclusiveHeavyOwnerSnapshot.CacheMaintenance>(
                coordinator.ActiveOwner);
            Assert.AreEqual(CacheMaintenanceRequestOrigin.GeoBoundariesPage, exactOwner.Operation.Origin);
            CollectionAssert.AreEqual(
                new[] { nameof(CacheMaintenanceBusyMetadata.AdmittedAtUtc), nameof(CacheMaintenanceBusyMetadata.Origin) },
                exactOwner.Operation.GetType().GetProperties().Select(property => property.Name).Order().ToArray());
            CollectionAssert.AreEqual(
                new[] { typeof(IAsyncDisposable) },
                typeof(ICacheMaintenanceReservation).GetInterfaces());
            Assert.HasCount(0, typeof(ICacheMaintenanceReservation).GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly));
            Assert.HasCount(0, typeof(ICacheMaintenanceReservation).GetProperties(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly));
        }
        finally
        {
            await reserved.Reservation.DisposeAsync();
            await reserved.Reservation.DisposeAsync();
        }
        Assert.IsNull(coordinator.ActiveOwner);
    }

    [TestMethod]
    public async Task EveryRegisteredWorker_BlocksMaintenanceWithExistingWorkerMetadata()
    {
        foreach (WorkerJobDispatch dispatch in WorkerDispatches())
        {
            await using var coordinator = CreateCoordinator();
            WorkerJobAdmissionResult.Admitted admitted =
                Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(coordinator.TryAdmit(dispatch));
            try
            {
                CacheMaintenanceAdmissionResult.Busy busy =
                    Assert.IsInstanceOfType<CacheMaintenanceAdmissionResult.Busy>(
                        coordinator.TryReserveCacheMaintenance(CacheMaintenanceRequestOrigin.GeoBoundariesPage));
                var owner = Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.Worker>(busy.ActiveOwner);
                Assert.AreEqual(dispatch.Context.JobId, admitted.Lease.Context.JobId);
                Assert.AreEqual(dispatch.Context.JobKind, owner.Job.JobKind);
                Assert.AreEqual(dispatch.Context.Origin, owner.Job.Origin);
                Assert.AreSame(dispatch.Descriptor, admitted.Lease.Descriptor);
            }
            finally
            {
                await admitted.Lease.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task ShutdownWhileMaintenanceOwnsSlot_FencesWorkerAndWaitsOnlyForRelease()
    {
        await using var coordinator = CreateCoordinator();
        CacheMaintenanceAdmissionResult.Reserved reserved =
            Assert.IsInstanceOfType<CacheMaintenanceAdmissionResult.Reserved>(
                coordinator.TryReserveCacheMaintenance(CacheMaintenanceRequestOrigin.GeoBoundariesPage));
        Task shutdown = coordinator.BeginShutdown();
        Task repeatedShutdown = coordinator.BeginShutdown();
        try
        {
            WorkerJobAdmissionResult.Unavailable unavailable =
                Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(
                    coordinator.TryAdmit(WorkerDispatches().First()));
            Assert.AreEqual("worker-admission-stopped", unavailable.Code,
                "shutdown must take priority over an active-owner busy response");
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.AreSame(shutdown, repeatedShutdown, "repeated shutdown must join the same completion");
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerSnapshot.CacheMaintenance>(coordinator.ActiveOwner);
            Assert.IsFalse(coordinator.Snapshot.IsAccepting);
        }
        finally
        {
            await reserved.Reservation.DisposeAsync();
            await shutdown;
        }
        Assert.IsNull(coordinator.ActiveOwner);
    }

    [TestMethod]
    public async Task ConcurrentWorkerAndMaintenanceAdmission_HasOneFirstWinnerAndNoQueue()
    {
        await using var coordinator = CreateCoordinator();
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);
        Task<WorkerJobAdmissionResult> worker = Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            return coordinator.TryAdmit(WorkerDispatches().First());
        });
        Task<CacheMaintenanceAdmissionResult> maintenance = Task.Run(() =>
        {
            ready.Signal();
            start.Wait();
            return coordinator.TryReserveCacheMaintenance(CacheMaintenanceRequestOrigin.GeoBoundariesPage);
        });
        WorkerJobAdmissionResult? workerResult = null;
        CacheMaintenanceAdmissionResult? maintenanceResult = null;
        try
        {
            Assert.IsTrue(ready.Wait(Bound), "both admission contenders must reach the named start barrier");
            start.Set();
            workerResult = await worker.WaitAsync(Bound);
            maintenanceResult = await maintenance.WaitAsync(Bound);
            bool workerWon = workerResult is WorkerJobAdmissionResult.Admitted;
            bool maintenanceWon = maintenanceResult is CacheMaintenanceAdmissionResult.Reserved;
            Assert.AreNotEqual(workerWon, maintenanceWon, "exactly one contender must acquire the slot");
            Assert.IsTrue(
                workerResult is WorkerJobAdmissionResult.Busy ||
                maintenanceResult is CacheMaintenanceAdmissionResult.Busy,
                "the loser must fail fast as busy");
        }
        finally
        {
            start.Set();
            try
            {
                workerResult ??= await worker.WaitAsync(Bound);
            }
            catch
            {
                // Preserve the original assertion or task failure after draining the other contender.
            }

            try
            {
                maintenanceResult ??= await maintenance.WaitAsync(Bound);
            }
            catch
            {
                // Preserve the original assertion or task failure after releasing any acquired capability.
            }

            if (workerResult is WorkerJobAdmissionResult.Admitted admitted)
            {
                await admitted.Lease.DisposeAsync();
            }

            if (maintenanceResult is CacheMaintenanceAdmissionResult.Reserved reserved)
            {
                await reserved.Reservation.DisposeAsync();
            }
        }

        Assert.IsNull(coordinator.ActiveOwner);
        CacheMaintenanceAdmissionResult.Reserved reuse =
            Assert.IsInstanceOfType<CacheMaintenanceAdmissionResult.Reserved>(
                coordinator.TryReserveCacheMaintenance(CacheMaintenanceRequestOrigin.GeoBoundariesPage));
        await reuse.Reservation.DisposeAsync();
        Assert.IsNull(coordinator.ActiveOwner, "losing admission is not queued for later ownership");
    }

    private static WorkerJobCoordinator CreateCoordinator() =>
        new(WorkerJobDescriptors.Registered);

    private static IEnumerable<WorkerJobDispatch> WorkerDispatches()
    {
        yield return new ProcessAssetsWorkerJobDispatch(
            new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual));
        yield return new CoordinateLookupWorkerJobDispatch(
            Guid.NewGuid(),
            new CoordinateLookupRequest(
                1,
                2,
                false,
                false,
                false,
                new CoordinateLookupCityResolverOverrides(null, [])));
        yield return new CacheMutationWorkerJobDispatch(
            Guid.NewGuid(),
            new CacheMutationRequest(
                CacheMutationSource.Overture,
                CacheMutationOperation.Refresh,
                "USA"));
    }
}
