using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.DatabaseMaintenance;

[TestClass]
[TestCategory("Change54")]
public sealed class DatabaseMaintenanceProducerContentionTests
{
    [TestMethod]
    public async Task ActualProducers_RecognizeDatabaseMaintenanceWithoutLaunchingOrMutating()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        DatabaseMaintenanceAdmissionResult.Admitted maintenance =
            Assert.IsInstanceOfType<DatabaseMaintenanceAdmissionResult.Admitted>(
                coordinator.TryReserveDatabaseMaintenance(
                    DatabaseMaintenanceRequestOrigin.ResetGeoDataPage));
        var lookupWorker = new ForbiddenLookupWorker();
        var lookup = new CoordinateLookupPageController(
            coordinator,
            lookupWorker,
            new ImmediateLookupSettings(),
            Guid.NewGuid,
            static () => { });
        var cacheWorker = new ForbiddenCacheWorker();
        int cacheReloads = 0;
        var cache = new CacheMutationPageController(
            coordinator,
            cacheWorker,
            Guid.NewGuid,
            static () => Task.CompletedTask,
            () =>
            {
                cacheReloads++;
                return Task.CompletedTask;
            });
        var deletionFileSystem = new ForbiddenDeletionFileSystem();
        var deletion = CreateDeletionCommand(coordinator, deletionFileSystem);
        var maintenanceReleased = false;
        try
        {
            await lookup.SubmitAsync(new CoordinateLookupSubmission(
                47.3769,
                8.5417,
                IncludeAirportInfrastructure: true,
                IncludeLiveOverturePlaces: false,
                PreferGadmAdministrativeAreas: false));
            await cache.RefreshAsync(CacheMutationSource.Overture, "CHE");
            CacheDeletionOperationResult deletionResult = await deletion.DeleteAsync(
                new CacheDeletionTarget(CacheMutationSource.Overture, "CHE"));

            Assert.AreEqual(CoordinateLookupPagePhase.Busy, lookup.State.Phase);
            StringAssert.Contains(lookup.State.Status, "database maintenance");
            Assert.AreEqual(0, lookupWorker.StartCalls);
            Assert.AreEqual(CacheMutationPagePhase.Busy, cache.State.Phase);
            StringAssert.Contains(cache.State.Status, "Database maintenance");
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                cache.State.BusyOwner);
            Assert.AreEqual(0, cacheWorker.StartCalls);
            Assert.AreEqual(0, cacheReloads);
            Assert.AreEqual(CacheDeletionOperationDisposition.Busy, deletionResult.Disposition);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                deletionResult.BusyOwner);
            Assert.AreEqual(0, deletionFileSystem.Calls);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                coordinator.Snapshot.ActiveOwner);

            await maintenance.Reservation.DisposeAsync();
            maintenanceReleased = true;

            await lookup.SubmitAsync(new CoordinateLookupSubmission(
                47.3769,
                8.5417,
                IncludeAirportInfrastructure: true,
                IncludeLiveOverturePlaces: false,
                PreferGadmAdministrativeAreas: false));
            await cache.RefreshAsync(CacheMutationSource.Overture, "CHE");
            CacheDeletionOperationResult afterReleaseDeletion = await deletion.DeleteAsync(
                new CacheDeletionTarget(CacheMutationSource.Overture, "CHE"));

            Assert.AreEqual(CoordinateLookupPagePhase.Unavailable, lookup.State.Phase);
            Assert.AreEqual(1, lookupWorker.StartCalls);
            Assert.AreEqual(CacheMutationPagePhase.Unavailable, cache.State.Phase);
            Assert.AreEqual(1, cacheWorker.StartCalls);
            Assert.AreEqual(1, cacheReloads);
            Assert.AreEqual(CacheDeletionOperationDisposition.Completed, afterReleaseDeletion.Disposition);
            Assert.AreEqual(1, afterReleaseDeletion.MissingCount);
            Assert.AreEqual(1, deletionFileSystem.Calls);
            Assert.IsNull(coordinator.Snapshot.ActiveOwner);
        }
        finally
        {
            await Task.WhenAll(
                lookup.DisposeAsync().AsTask(),
                cache.DisposeAsync().AsTask());
            if (!maintenanceReleased)
            {
                await maintenance.Reservation.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task ProcessAssets_NoWorkPreflightStaysAheadOfDatabaseOwner_ThenRealWorkIsBusy()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProcessingState>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        ProcessingState state = provider.GetRequiredService<ProcessingState>();
        var reporter = new ProcessingStateEventReporter(state);
        var detector = new MutableWorkGate(false);
        await using var workerCoordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        int identityCalls = 0;
        var processing = new ProcessingRunCoordinator(
            state,
            reporter,
            detector,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProcessingRunCoordinator>.Instance,
            () =>
            {
                identityCalls++;
                return Guid.NewGuid();
            },
            workerCoordinator);
        DatabaseMaintenanceAdmissionResult.Admitted maintenance =
            Assert.IsInstanceOfType<DatabaseMaintenanceAdmissionResult.Admitted>(
                workerCoordinator.TryReserveDatabaseMaintenance(
                    DatabaseMaintenanceRequestOrigin.DataPage));
        try
        {
            Assert.AreEqual(
                ScheduledTriggerResult.AcceptedAfterTerminal,
                await ((IScheduledRunTrigger)processing).TriggerScheduledAsync(CancellationToken.None));
            Assert.AreEqual(0, identityCalls);
            Assert.IsFalse(state.IsRunning);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                workerCoordinator.Snapshot.ActiveOwner);

            detector.HasWork = true;
            Assert.AreEqual(
                ProcessingRunAdmissionResult.AlreadyRunning,
                await processing.TriggerManualAsync());
            Assert.AreEqual(1, identityCalls);
            Assert.IsFalse(state.IsRunning);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                workerCoordinator.Snapshot.ActiveOwner);
        }
        finally
        {
            await maintenance.Reservation.DisposeAsync();
            await processing.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CacheInventoryRead_RemainsUnreservedWhileDatabaseMaintenanceOwnsTheSlot()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        DatabaseMaintenanceAdmissionResult.Admitted maintenance =
            Assert.IsInstanceOfType<DatabaseMaintenanceAdmissionResult.Admitted>(
                coordinator.TryReserveDatabaseMaintenance(
                    DatabaseMaintenanceRequestOrigin.DataPage));
        var scanner = new RecordingInventoryScanner();
        await using var inventory = new CacheInventoryService(scanner);
        try
        {
            CacheInventorySnapshot snapshot = await inventory.GetSnapshotAsync();

            Assert.AreEqual(1, scanner.ScanCalls);
            Assert.AreEqual(0L, snapshot.Generation);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                coordinator.Snapshot.ActiveOwner);
        }
        finally
        {
            await maintenance.Reservation.DisposeAsync();
        }
    }

    private static CacheDeletionCommand CreateDeletionCommand(
        IWorkerJobAdmissionGate admission,
        ICacheDeletionFileSystem fileSystem)
    {
        string data = Path.Combine(AppContext.BaseDirectory, "data");
        return new CacheDeletionCommand(
            admission,
            fileSystem,
            new StorageOptions(Path.Combine(Path.GetTempPath(), "change54-unused"), data),
            CountryCodeService.CreateForTest(data),
            NullLogger<CacheDeletionCommand>.Instance);
    }

    private sealed class MutableWorkGate(bool hasWork) : IProcessingWorkDetector
    {
        internal bool HasWork { get; set; } = hasWork;

        public Task<ProcessingWorkDetectionResult> DetectAsync(ProcessingWorkDetectionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ProcessingWorkDetectorStub.Result(HasWork));
        }
    }

    private sealed class ImmediateLookupSettings : ICoordinateLookupSettingsSnapshotProvider
    {
        public ValueTask<CoordinateLookupCityResolverOverrides> GetAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CoordinateLookupCityResolverOverrides(null, []));
    }

    private sealed class ForbiddenLookupWorker : ICoordinateLookupWorkerClient
    {
        internal int StartCalls { get; private set; }

        public ValueTask<CoordinateLookupWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CoordinateLookupRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            return ValueTask.FromResult<CoordinateLookupWorkerStartResult>(
                new CoordinateLookupWorkerStartResult.Unavailable(
                    "controlled-unavailable",
                    "Controlled unavailable lookup."));
        }
    }

    private sealed class ForbiddenCacheWorker : ICacheMutationWorkerClient
    {
        internal int StartCalls { get; private set; }

        public ValueTask<CacheMutationWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CacheMutationRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            return ValueTask.FromResult<CacheMutationWorkerStartResult>(
                new CacheMutationWorkerStartResult.Unavailable(
                    "controlled-unavailable",
                    "Controlled unavailable cache mutation."));
        }
    }

    private sealed class ForbiddenDeletionFileSystem : ICacheDeletionFileSystem
    {
        internal int Calls { get; private set; }

        public ValueTask<CacheDeletionFileInspection> InspectAsync(
            string sourceRoot,
            string finalPath)
        {
            Calls++;
            return ValueTask.FromResult(CacheDeletionFileInspection.Missing);
        }

        public ValueTask DeleteAsync(string finalPath)
        {
            Calls++;
            throw new AssertFailedException("Busy cache deletion must not mutate storage.");
        }
    }

    private sealed class RecordingInventoryScanner : ICacheInventoryStorageScanner
    {
        internal int ScanCalls { get; private set; }

        public Task<CacheInventorySnapshot> ScanAsync(
            long generation,
            CancellationToken cancellationToken)
        {
            ScanCalls++;
            return Task.FromResult(new CacheInventorySnapshot(
                generation,
                DateTimeOffset.UnixEpoch,
                ImmutableArray<CacheInventorySourceSnapshot>.Empty));
        }

        public Task<CacheInventoryExactResult> ScanExactAsync(
            CacheMutationSource source,
            string iso3,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("The inventory listing must not request an exact scan.");
    }
}
