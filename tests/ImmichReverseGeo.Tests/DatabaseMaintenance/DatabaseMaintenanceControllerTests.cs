using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ImmichReverseGeo.Tests.DatabaseMaintenance;

[TestClass]
[TestCategory("Change54")]
public sealed class DatabaseMaintenanceControllerTests
{
    [TestMethod]
    public async Task UnclassifiedPostgresFailure_ReportsUnconfirmedAndSkipsSqlite()
    {
        var postgres = new RecordingImmichStore
        {
            Failure = new PostgresException(
                "server closed the connection",
                "FATAL",
                "FATAL",
                "57P01")
        };
        var skipped = new RecordingSkippedStore();
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);

        DatabaseMaintenanceResult result = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));

        Assert.AreEqual(DatabaseMaintenanceDisposition.Failed, result.Disposition);
        Assert.AreEqual(DatabaseMaintenanceStageStatus.Failed, result.Postgres.Status);
        Assert.IsFalse(result.Postgres.OutcomeConfirmed);
        Assert.IsNull(result.Postgres.Count);
        Assert.AreEqual("immich-outcome-unconfirmed", result.Postgres.Code);
        Assert.AreEqual(DatabaseMaintenanceStageStatus.NotStarted, result.Skipped.Status);
        Assert.AreEqual(0, skipped.CallCount);
    }

    [TestMethod]
    public async Task SelectedInput_DeduplicatesValidIdsAndReportsInvalidCount()
    {
        Guid first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var postgres = new RecordingImmichStore();
        var skipped = new RecordingSkippedStore();
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);

        DatabaseMaintenanceResult result = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetSelected(
                $"{second}, invalid, {first}\n{second}"));

        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, result.Disposition);
        Assert.AreEqual(1, result.InvalidTokenCount);
        CollectionAssert.AreEqual(new[] { first, second }, postgres.SelectedIds.ToArray());
        CollectionAssert.AreEqual(new[] { first, second }, skipped.RemovedIds.ToArray());
    }

    [TestMethod]
    public async Task UnconfirmedResetAll_IsRejectedBeforeAdmission()
    {
        var postgres = new RecordingImmichStore();
        var skipped = new RecordingSkippedStore();
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);

        DatabaseMaintenanceResult result = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetAll(Confirmed: false));

        Assert.AreEqual(DatabaseMaintenanceDisposition.Validation, result.Disposition);
        Assert.AreEqual(0, postgres.CallCount);
        Assert.AreEqual(0, skipped.CallCount);
        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
    }

    [TestMethod]
    public async Task SqliteFailureAfterPostgresCommit_RetryUsesExactTargetWithoutPostgresReplay()
    {
        Guid requested = Guid.Parse("33333333-3333-3333-3333-333333333333");
        int removeAttempt = 0;
        var postgres = new RecordingImmichStore();
        var skipped = new RecordingSkippedStore
        {
            Remove = ids =>
            {
                removeAttempt++;
                return removeAttempt == 1
                    ? Task.FromException<long>(new IOException("private path"))
                    : Task.FromResult((long)ids.Count);
            }
        };
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);

        DatabaseMaintenanceResult partial = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetSelected(requested.ToString()));

        Assert.AreEqual(DatabaseMaintenanceDisposition.Partial, partial.Disposition);
        Assert.AreEqual(DatabaseMaintenanceStageStatus.Succeeded, partial.Postgres.Status);
        Assert.AreEqual(DatabaseMaintenanceStageStatus.Failed, partial.Skipped.Status);
        Assert.IsNotNull(partial.Retry);
        DatabaseMaintenanceResult retry = await controller.RetrySkippedCleanupAsync(partial.Retry);
        DatabaseMaintenanceResult consumed = await controller.RetrySkippedCleanupAsync(partial.Retry);

        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, retry.Disposition);
        Assert.AreEqual(DatabaseMaintenanceStageStatus.NotStarted, retry.Postgres.Status);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Validation, consumed.Disposition);
        Assert.AreEqual(1, postgres.CallCount);
        Assert.AreEqual(2, skipped.CallCount);
        CollectionAssert.AreEqual(new[] { requested }, skipped.RemovedIds.ToArray());
        Assert.AreEqual(DatabaseMaintenanceDisposition.Partial, partial.Disposition);
    }

    [TestMethod]
    public async Task MatchingValue_IsPassedExactlyAndOnlyReturnedIdsReachSkippedStore()
    {
        Guid returned = Guid.Parse("44444444-4444-4444-4444-444444444444");
        const string exact = "  O'Brien; DROP TABLE asset_exif; --  ";
        var postgres = new RecordingImmichStore
        {
            Matching = (_, _) => Task.FromResult<IReadOnlyList<Guid>>([returned, returned])
        };
        var skipped = new RecordingSkippedStore();
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);

        DatabaseMaintenanceResult result = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetMatching(LocationResetScope.City, exact));

        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, result.Disposition);
        Assert.AreEqual(exact, postgres.MatchingValue);
        Assert.AreEqual(LocationResetScope.City, postgres.MatchingScope);
        CollectionAssert.AreEqual(new[] { returned }, skipped.RemovedIds.ToArray());
        Assert.AreEqual(1L, result.Postgres.Count);
    }

    [TestMethod]
    public async Task ReleaseObserverStartsNewOperation_OnlyOldTrackedTaskIsRemoved()
    {
        var postgres = new RecordingImmichStore();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int clearAttempt = 0;
        var skipped = new RecordingSkippedStore
        {
            Clear = async () =>
            {
                clearAttempt++;
                if (clearAttempt == 2)
                {
                    secondEntered.TrySetResult();
                    await releaseSecond.Task;
                }

                return 0L;
            }
        };
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);
        Task<DatabaseMaintenanceResult>? reentrant = null;
        Task? shutdown = null;
        Exception? primaryFailure = null;
        coordinator.Changed += OnChanged;
        try
        {
            DatabaseMaintenanceResult first = await controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ClearSkipList());
            Assert.IsNotNull(reentrant);
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, first.Disposition);
            Assert.AreEqual(1, controller.TrackedOperationCount);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                coordinator.Snapshot.ActiveOwner);

            shutdown = coordinator.BeginShutdown();
            Assert.IsFalse(shutdown.IsCompleted);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        coordinator.Changed -= OnChanged;
        await ReleaseAndDrainAsync(releaseSecond, primaryFailure, reentrant, shutdown);

        Assert.AreEqual(2, skipped.CallCount);
        Assert.AreEqual(0, controller.TrackedOperationCount);
        Assert.IsNull(coordinator.Snapshot.ActiveOwner);

        void OnChanged()
        {
            if (reentrant is null
                && coordinator.Snapshot.ActiveOwner is null)
            {
                reentrant = controller.ExecuteAsync(
                    new DatabaseMaintenanceRequest.ClearSkipList());
                coordinator.Changed -= OnChanged;
            }
        }
    }

    [TestMethod]
    public async Task ShutdownDuringPostgres_WaitsForExactMaintenanceRelease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postgres = new RecordingImmichStore
        {
            ClearAll = async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return 1L;
            }
        };
        var skipped = new RecordingSkippedStore();
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);

        Task<DatabaseMaintenanceResult>? operation = null;
        Task? shutdown = null;
        DatabaseMaintenanceResult? completed = null;
        Exception? primaryFailure = null;
        try
        {
            operation = controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown = coordinator.BeginShutdown();

            Assert.IsFalse(shutdown.IsCompleted);
            DatabaseMaintenanceResult unavailable = await controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ClearSkipList());
            Assert.AreEqual(DatabaseMaintenanceDisposition.Unavailable, unavailable.Disposition);
            Assert.AreEqual(0, skipped.CallCount);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await ReleaseAndDrainAsync(release, primaryFailure, operation, shutdown);
        completed = operation is null ? null : await operation;

        Assert.IsNotNull(completed);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, completed.Disposition);
        Assert.AreEqual(1, skipped.CallCount);
        Assert.AreEqual(0, controller.TrackedOperationCount);
        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
    }

    [TestMethod]
    public async Task ShutdownObserverTimeout_DoesNotReleaseOrOrphanHeldDatabaseMaintenance()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postgres = new RecordingImmichStore
        {
            ClearAll = async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return 1L;
            }
        };
        var skipped = new RecordingSkippedStore();
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);

        Task<DatabaseMaintenanceResult>? operation = null;
        Task? shutdown = null;
        ExclusiveHeavyOwnerSnapshot? owner = null;
        Exception? primaryFailure = null;
        try
        {
            operation = controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            owner = coordinator.ActiveOwner;
            shutdown = coordinator.BeginShutdown();

            await Assert.ThrowsExactlyAsync<TimeoutException>(
                () => shutdown.WaitAsync(TimeSpan.Zero));
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsNotNull(owner);
            Assert.AreSame(owner, coordinator.ActiveOwner);
            Assert.AreEqual(1, controller.TrackedOperationCount);
            Assert.AreSame(shutdown, coordinator.BeginShutdown());
            Assert.AreEqual(1, postgres.CallCount);
            Assert.AreEqual(0, skipped.CallCount);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await ReleaseAndDrainAsync(release, primaryFailure, operation, shutdown);
        DatabaseMaintenanceResult completed = await operation!;

        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, completed.Disposition);
        Assert.AreEqual(DatabaseMaintenanceStageStatus.Succeeded, completed.Postgres.Status);
        Assert.AreEqual(DatabaseMaintenanceStageStatus.Succeeded, completed.Skipped.Status);
        Assert.AreEqual(1, postgres.CallCount);
        Assert.AreEqual(1, skipped.CallCount);
        Assert.AreEqual(0, controller.TrackedOperationCount);
        Assert.IsNull(coordinator.ActiveOwner);
    }

    [TestMethod]
    public async Task ActiveWorkerOrCacheOwner_RejectsResetBeforeRepositoryCalls()
    {
        var postgres = new RecordingImmichStore();
        var skipped = new RecordingSkippedStore();
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);
        WorkerJobDispatch[] workerOwners =
        {
            new ProcessAssetsWorkerJobDispatch(
                new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual)),
            new CoordinateLookupWorkerJobDispatch(
                Guid.NewGuid(),
                new CoordinateLookupRequest(
                    47.3769,
                    8.5417,
                    false,
                    false,
                    false,
                    new CoordinateLookupCityResolverOverrides(null, []))),
            new CacheMutationWorkerJobDispatch(
                Guid.NewGuid(),
                new CacheMutationRequest(
                    CacheMutationSource.Overture,
                    CacheMutationOperation.Refresh,
                    "CHE"))
        };
        foreach (WorkerJobDispatch ownerDispatch in workerOwners)
        {
            WorkerJobAdmissionResult.Admitted worker =
                Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
                    coordinator.TryAdmit(ownerDispatch));
            try
            {
                DatabaseMaintenanceResult busy = await controller.ExecuteAsync(
                    new DatabaseMaintenanceRequest.ClearSkipList());
                Assert.AreEqual(DatabaseMaintenanceDisposition.Busy, busy.Disposition);
                Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.Worker>(busy.BusyOwner);
                Assert.AreEqual(0, postgres.CallCount);
                Assert.AreEqual(0, skipped.CallCount);
            }
            finally
            {
                await worker.Lease.DisposeAsync();
            }
        }

        CacheMaintenanceAdmissionResult.Reserved cache =
            Assert.IsInstanceOfType<CacheMaintenanceAdmissionResult.Reserved>(
                coordinator.TryReserveCacheMaintenance(CacheMaintenanceRequestOrigin.GeoBoundariesPage));
        try
        {
            DatabaseMaintenanceResult busy = await controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));
            Assert.AreEqual(DatabaseMaintenanceDisposition.Busy, busy.Disposition);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.CacheMaintenance>(busy.BusyOwner);
            Assert.AreEqual(0, postgres.CallCount);
        }
        finally
        {
            await cache.Reservation.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ActiveDatabaseMaintenance_RejectsWorkerCacheAndResetWithoutQueue()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postgres = new RecordingImmichStore();
        var skipped = new RecordingSkippedStore
        {
            Clear = async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return 0L;
            }
        };
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);
        Task<DatabaseMaintenanceResult>? owner = null;
        Exception? primaryFailure = null;
        try
        {
            owner = controller.ExecuteAsync(new DatabaseMaintenanceRequest.ClearSkipList());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var processing = new ProcessAssetsWorkerJobDispatch(
                new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual));
            WorkerJobAdmissionResult.Busy workerBusy =
                Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(coordinator.TryAdmit(processing));
            CacheMaintenanceAdmissionResult.Busy cacheBusy =
                Assert.IsInstanceOfType<CacheMaintenanceAdmissionResult.Busy>(
                    coordinator.TryReserveCacheMaintenance(CacheMaintenanceRequestOrigin.GeoBoundariesPage));
            DatabaseMaintenanceResult resetBusy = await controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));

            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                workerBusy.ActiveOwner);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                cacheBusy.ActiveOwner);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                resetBusy.BusyOwner);
            Assert.AreEqual(DatabaseMaintenanceDisposition.Busy, resetBusy.Disposition);
            Assert.AreEqual(0, postgres.CallCount);
            Assert.AreEqual(1, skipped.CallCount);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await ReleaseAndDrainAsync(release, primaryFailure, owner);

        DatabaseMaintenanceResult reuse = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ClearSkipList());
        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, reuse.Disposition);
    }

    [TestMethod]
    public async Task FrozenResult_RemainsOwnedUntilFinalizationWitnessCompletes()
    {
        var frozen = new TaskCompletionSource<DatabaseMaintenanceResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postgres = new RecordingImmichStore();
        var skipped = new RecordingSkippedStore();
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance,
            async result =>
            {
                frozen.TrySetResult(result);
                await release.Task;
            });
        Task<DatabaseMaintenanceResult>? operation = null;
        Exception? primaryFailure = null;
        try
        {
            operation = controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));
            DatabaseMaintenanceResult immutable = await frozen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, immutable.Disposition);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.DatabaseMaintenance>(
                coordinator.Snapshot.ActiveOwner);
            Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(coordinator.TryAdmit(
                new ProcessAssetsWorkerJobDispatch(
                    new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual))));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await ReleaseAndDrainAsync(release, primaryFailure, operation);

        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
        Assert.AreEqual(0, controller.TrackedOperationCount);
    }

    [TestMethod]
    public async Task FailedRetry_RemainsAvailableUntilSkippedCleanupSucceeds()
    {
        int attempt = 0;
        var postgres = new RecordingImmichStore();
        var skipped = new RecordingSkippedStore
        {
            Clear = () =>
            {
                attempt++;
                return attempt < 3
                    ? Task.FromException<long>(new IOException("private path"))
                    : Task.FromResult(4L);
            }
        };
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);

        DatabaseMaintenanceResult partial = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));
        DatabaseMaintenanceResult failedRetry = await controller.RetrySkippedCleanupAsync(partial.Retry!);
        DatabaseMaintenanceResult completedRetry = await controller.RetrySkippedCleanupAsync(partial.Retry!);

        Assert.AreEqual(DatabaseMaintenanceDisposition.Partial, partial.Disposition);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Failed, failedRetry.Disposition);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, completedRetry.Disposition);
        Assert.AreEqual(DatabaseMaintenanceStageStatus.NotStarted, failedRetry.Postgres.Status);
        Assert.AreEqual(1, postgres.CallCount);
        Assert.AreEqual(3, skipped.CallCount);
    }

    [TestMethod]
    public async Task RetryCapability_RejectsConcurrentAndForeignConsumptionWithoutAnotherAdmission()
    {
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var skipped = new RecordingSkippedStore
        {
            Clear = async () =>
            {
                attempt++;
                if (attempt == 1)
                {
                    throw new IOException("controlled initial failure");
                }

                retryEntered.TrySetResult();
                await releaseRetry.Task;
                return 1L;
            }
        };
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var postgres = new RecordingImmichStore();
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);
        var foreignController = new DatabaseMaintenanceController(
            coordinator,
            new RecordingImmichStore(),
            new RecordingSkippedStore(),
            NullLogger<DatabaseMaintenanceController>.Instance);
        DatabaseMaintenanceResult partial = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));
        Task<DatabaseMaintenanceResult>? activeRetry = null;
        Exception? primaryFailure = null;
        try
        {
            activeRetry = controller.RetrySkippedCleanupAsync(partial.Retry!);
            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            DatabaseMaintenanceResult concurrent = await controller.RetrySkippedCleanupAsync(
                partial.Retry!);
            DatabaseMaintenanceResult foreign = await foreignController.RetrySkippedCleanupAsync(
                partial.Retry!);

            Assert.AreEqual(DatabaseMaintenanceDisposition.Validation, concurrent.Disposition);
            Assert.AreEqual(DatabaseMaintenanceDisposition.Validation, foreign.Disposition);
            Assert.AreEqual(1, controller.TrackedOperationCount);
            Assert.AreEqual(2, skipped.CallCount);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await ReleaseAndDrainAsync(releaseRetry, primaryFailure, activeRetry);
        DatabaseMaintenanceResult completed = await activeRetry!;
        Assert.AreEqual(DatabaseMaintenanceDisposition.Complete, completed.Disposition);

        Assert.AreEqual(1, postgres.CallCount);
        Assert.AreEqual(0, controller.TrackedOperationCount);
    }

    [TestMethod]
    public async Task ValidationPrecedesPermanentShutdownFence()
    {
        var postgres = new RecordingImmichStore();
        var skipped = new RecordingSkippedStore();
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);
        await coordinator.BeginShutdown();

        DatabaseMaintenanceResult unconfirmed = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetAll(Confirmed: false));
        DatabaseMaintenanceResult invalidIds = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetSelected("invalid"));
        DatabaseMaintenanceResult invalidScope = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetMatching((LocationResetScope)999, "value"));
        DatabaseMaintenanceResult blankValue = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetMatching(LocationResetScope.City, "   "));
        DatabaseMaintenanceResult valid = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ClearSkipList());

        Assert.AreEqual(DatabaseMaintenanceDisposition.Validation, unconfirmed.Disposition);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Validation, invalidIds.Disposition);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Validation, invalidScope.Disposition);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Validation, blankValue.Disposition);
        Assert.AreEqual(DatabaseMaintenanceDisposition.Unavailable, valid.Disposition);
        Assert.AreEqual(0, postgres.CallCount);
        Assert.AreEqual(0, skipped.CallCount);
    }

    [TestMethod]
    public async Task DuplicateOldRelease_CannotReleaseNewDatabaseOwner()
    {
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        DatabaseMaintenanceAdmissionResult.Admitted first =
            Assert.IsInstanceOfType<DatabaseMaintenanceAdmissionResult.Admitted>(
                coordinator.TryReserveDatabaseMaintenance(
                    DatabaseMaintenanceRequestOrigin.ResetGeoDataPage));
        await first.Reservation.DisposeAsync();
        DatabaseMaintenanceAdmissionResult.Admitted second =
            Assert.IsInstanceOfType<DatabaseMaintenanceAdmissionResult.Admitted>(
                coordinator.TryReserveDatabaseMaintenance(
                    DatabaseMaintenanceRequestOrigin.DataPage));
        try
        {
            await first.Reservation.DisposeAsync();
            DatabaseMaintenanceOwnerSnapshot active = Assert.IsInstanceOfType<
                ExclusiveHeavyOwnerSnapshot.DatabaseMaintenance>(coordinator.ActiveOwner).Operation;
            Assert.AreEqual(DatabaseMaintenanceRequestOrigin.DataPage, active.Origin);
        }
        finally
        {
            await second.Reservation.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ShutdownAtSkippedBoundary_WaitsForSqliteAndRepeatedShutdownIsSameTask()
    {
        var sqliteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSqlite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? shutdown = null;
        WorkerJobCoordinator? coordinator = new(WorkerJobDescriptors.Registered);
        var postgres = new RecordingImmichStore();
        var skipped = new RecordingSkippedStore
        {
            Clear = async () =>
            {
                shutdown = coordinator!.BeginShutdown();
                sqliteEntered.TrySetResult();
                await releaseSqlite.Task;
                return 2L;
            }
        };
        await using (coordinator)
        {
            var controller = new DatabaseMaintenanceController(
                coordinator,
                postgres,
                skipped,
                NullLogger<DatabaseMaintenanceController>.Instance);
            Task<DatabaseMaintenanceResult>? operation = null;
            Exception? primaryFailure = null;
            try
            {
                operation = controller.ExecuteAsync(
                    new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));
                await sqliteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(shutdown);
                Assert.AreSame(shutdown, coordinator.BeginShutdown());
                Assert.IsFalse(shutdown.IsCompleted);
                Assert.AreEqual(1, postgres.CallCount);
                Assert.AreEqual(1, skipped.CallCount);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }

            await ReleaseAndDrainAsync(releaseSqlite, primaryFailure, operation, shutdown);
        }
    }

    [TestMethod]
    public async Task ShutdownBetweenRepositoryStores_JoinsTheAlreadyAdmittedControllerTask()
    {
        Guid returned = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var sqliteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSqlite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? shutdown = null;
        ExclusiveHeavyOwnerSnapshot? ownerAtFence = null;
        var skippedCallsAtFence = -1;
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var skipped = new RecordingSkippedStore
        {
            Remove = async ids =>
            {
                sqliteEntered.TrySetResult();
                await releaseSqlite.Task;
                return ids.Count;
            }
        };
        var matchingIds = new CallbackReadOnlyList<Guid>([returned], () =>
        {
            ownerAtFence = coordinator.ActiveOwner;
            skippedCallsAtFence = skipped.CallCount;
            shutdown = coordinator.BeginShutdown();
        });
        var postgres = new RecordingImmichStore
        {
            Matching = (_, _) => Task.FromResult<IReadOnlyList<Guid>>(matchingIds)
        };
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);
        Task<DatabaseMaintenanceResult>? operation = null;
        Exception? primaryFailure = null;
        try
        {
            operation = controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ResetMatching(LocationResetScope.State, "Exact"));
            await sqliteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsNotNull(shutdown);
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerSnapshot.DatabaseMaintenance>(ownerAtFence);
            Assert.AreEqual(0, skippedCallsAtFence);
            Assert.AreEqual(1, postgres.CallCount);
            Assert.AreEqual(1, skipped.CallCount);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await ReleaseAndDrainAsync(releaseSqlite, primaryFailure, operation, shutdown);

        Assert.AreEqual(0, controller.TrackedOperationCount);
        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
    }

    [TestMethod]
    public async Task ShutdownBeforeAdmission_ReturnsUnavailableWithoutRepositoryWork()
    {
        var postgres = new RecordingImmichStore();
        var skipped = new RecordingSkippedStore();
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var controller = new DatabaseMaintenanceController(
            coordinator,
            postgres,
            skipped,
            NullLogger<DatabaseMaintenanceController>.Instance);

        await coordinator.BeginShutdown();
        DatabaseMaintenanceResult result = await controller.ExecuteAsync(
            new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));

        Assert.AreEqual(DatabaseMaintenanceDisposition.Unavailable, result.Disposition);
        Assert.AreEqual(0, postgres.CallCount);
        Assert.AreEqual(0, skipped.CallCount);
        Assert.AreEqual(0, controller.TrackedOperationCount);
        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
    }

    [TestMethod]
    public async Task SimultaneousAdmissionAndShutdown_ProducesOneClosedOutcomeWithoutOrphanOwner()
    {
        for (var round = 0; round < 16; round++)
        {
            var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<DatabaseMaintenanceAdmissionResult> admissionTask = Task.Run(async () =>
            {
                await start.Task;
                return coordinator.TryReserveDatabaseMaintenance(
                    DatabaseMaintenanceRequestOrigin.ResetGeoDataPage);
            });
            Task<Task> shutdownTask = Task.Run(async () =>
            {
                await start.Task;
                return coordinator.BeginShutdown();
            });
            start.TrySetResult();
            DatabaseMaintenanceAdmissionResult admission = await admissionTask;
            Task shutdown = await shutdownTask;
            try
            {
                if (admission is DatabaseMaintenanceAdmissionResult.Admitted admitted)
                {
                    Assert.IsFalse(shutdown.IsCompleted);
                    await admitted.Reservation.DisposeAsync();
                }
                else
                {
                    Assert.IsInstanceOfType<DatabaseMaintenanceAdmissionResult.Unavailable>(admission);
                }

                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsNull(coordinator.Snapshot.ActiveOwner, $"round {round}");
            }
            finally
            {
                if (admission is DatabaseMaintenanceAdmissionResult.Admitted admitted)
                {
                    await admitted.Reservation.DisposeAsync();
                }

                await coordinator.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task ProviderAndStorageFailures_ExposeOnlyBoundedSafeCopyAndNullCounts()
    {
        Guid secretId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        string secret = $"Host=private;Password=never-render;/private/data/skipped.db; {secretId}; DROP TABLE asset_exif; at Secret.Stack()";
        (Exception Failure, string Code, bool Confirmed)[] postgresFailures =
        {
            (new PostgresException(secret, "ERROR", "ERROR", "42501"), "immich-permission", true),
            (new PostgresException(secret, "ERROR", "ERROR", "28P01"), "immich-authentication", true),
            (new PostgresException(secret, "ERROR", "ERROR", "25006"), "immich-read-only", true),
            (new PostgresException(secret, "ERROR", "ERROR", "57014"), "immich-statement-cancelled", true),
            (new PostgresException(secret, "ERROR", "ERROR", "P5401"), "immich-statement-failed", true),
            (new PostgresException(secret, "FATAL", "FATAL", "57P01"), "immich-outcome-unconfirmed", false),
            (new TimeoutException(secret), "immich-outcome-unconfirmed", false),
            (new OperationCanceledException(secret), "immich-outcome-unconfirmed", false),
            (new NpgsqlException(secret), "immich-outcome-unconfirmed", false),
            (new UnauthorizedAccessException(secret), "immich-outcome-unconfirmed", false),
            (new IOException(secret), "immich-outcome-unconfirmed", false)
        };
        await using var coordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        foreach ((Exception failure, string code, bool confirmed) in postgresFailures)
        {
            var logger = new RecordingLogger<DatabaseMaintenanceController>();
            var postgres = new RecordingImmichStore { Failure = failure };
            var skipped = new RecordingSkippedStore();
            var controller = new DatabaseMaintenanceController(
                coordinator,
                postgres,
                skipped,
                logger);

            DatabaseMaintenanceResult result = await controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ResetAll(Confirmed: true));

            Assert.AreEqual(DatabaseMaintenanceDisposition.Failed, result.Disposition);
            Assert.AreEqual(code, result.Postgres.Code);
            Assert.AreEqual(confirmed, result.Postgres.OutcomeConfirmed);
            Assert.IsNull(result.Postgres.Count);
            Assert.AreEqual(DatabaseMaintenanceStageStatus.NotStarted, result.Skipped.Status);
            Assert.AreEqual(0, skipped.CallCount);
            AssertSafe(result.Message, secret, secretId);
            AssertSafe(result.Postgres.Message!, secret, secretId);
            Assert.HasCount(1, logger.Messages);
            AssertSafe(logger.Messages.Single(), secret, secretId);
        }

        (Exception Failure, string Code, bool Confirmed)[] skippedFailures =
        {
            (new SqliteException(secret, 8), "skipped-read-only", true),
            (new SqliteException(secret, 5), "skipped-in-use", false),
            (new SqliteException(secret, 6), "skipped-in-use", false),
            (new UnauthorizedAccessException(secret), "skipped-permission", true),
            (new System.Security.SecurityException(secret), "skipped-permission", true),
            (new IOException(secret), "skipped-io", false),
            (new SqliteException(secret, 10), "skipped-io", false),
            (new TimeoutException(secret), "skipped-timeout", false),
            (new OperationCanceledException(secret), "skipped-timeout", false),
            (new InvalidOperationException(secret), "skipped-failed", false)
        };
        foreach ((Exception failure, string code, bool confirmed) in skippedFailures)
        {
            var logger = new RecordingLogger<DatabaseMaintenanceController>();
            var storageFailure = new RecordingSkippedStore
            {
                Clear = () => Task.FromException<long>(failure)
            };
            var controller = new DatabaseMaintenanceController(
                coordinator,
                new RecordingImmichStore(),
                storageFailure,
                logger);

            DatabaseMaintenanceResult result = await controller.ExecuteAsync(
                new DatabaseMaintenanceRequest.ClearSkipList());

            Assert.AreEqual(DatabaseMaintenanceDisposition.Failed, result.Disposition);
            Assert.AreEqual(DatabaseMaintenanceStageStatus.NotStarted, result.Postgres.Status);
            Assert.AreEqual(code, result.Skipped.Code);
            Assert.AreEqual(confirmed, result.Skipped.OutcomeConfirmed);
            Assert.IsNull(result.Skipped.Count);
            AssertSafe(result.Message, secret, secretId);
            AssertSafe(result.Skipped.Message!, secret, secretId);
            Assert.HasCount(1, logger.Messages);
            AssertSafe(logger.Messages.Single(), secret, secretId);
        }
    }

    private static void AssertSafe(string message, string privateValue, Guid privateId)
    {
        Assert.IsFalse(message.Contains(privateValue, StringComparison.Ordinal));
        Assert.IsFalse(message.Contains("Host=private", StringComparison.Ordinal));
        Assert.IsFalse(message.Contains("never-render", StringComparison.Ordinal));
        Assert.IsFalse(message.Contains("/private/data/skipped.db", StringComparison.Ordinal));
        Assert.IsFalse(message.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(message.Contains("Secret.Stack", StringComparison.Ordinal));
        Assert.IsFalse(message.Contains(privateId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.IsLessThanOrEqualTo(256, message.Length);
    }

    private static async Task ReleaseAndDrainAsync(
        TaskCompletionSource release,
        Exception? primaryFailure,
        params Task?[] operations)
    {
        release.TrySetResult();
        Exception? cleanupFailure = null;
        try
        {
            await Task.WhenAll(operations
                .Where(operation => operation is not null)
                .Select(operation => operation!.WaitAsync(TimeSpan.FromSeconds(5))));
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        if (primaryFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(primaryFailure, cleanupFailure);
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private sealed class RecordingImmichStore : IImmichLocationResetStore
    {
        internal Exception? Failure { get; init; }
        internal Func<Task<long>>? ClearAll { get; init; }
        internal Func<LocationResetScope, string, Task<IReadOnlyList<Guid>>>? Matching { get; init; }
        internal int CallCount { get; private set; }
        internal IReadOnlyList<Guid> SelectedIds { get; private set; } = [];
        internal LocationResetScope? MatchingScope { get; private set; }
        internal string? MatchingValue { get; private set; }

        public Task<long> ClearAllLocationDataAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (ClearAll is not null)
            {
                return ClearAll();
            }

            return Failure is null
                ? Task.FromResult(0L)
                : Task.FromException<long>(Failure);
        }

        public Task<IReadOnlyList<Guid>> ClearLocationDataForAssetsAsync(
            IReadOnlyCollection<Guid> assetIds,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            SelectedIds = assetIds.ToArray();
            return Task.FromResult<IReadOnlyList<Guid>>(SelectedIds);
        }

        public Task<IReadOnlyList<Guid>> ClearLocationDataByValueAsync(
            LocationResetScope scope,
            string value,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            MatchingScope = scope;
            MatchingValue = value;
            return Matching?.Invoke(scope, value)
                ?? Task.FromResult<IReadOnlyList<Guid>>([]);
        }
    }

    private sealed class CallbackReadOnlyList<T>(IReadOnlyList<T> values, Action beforeEnumeration)
        : IReadOnlyList<T>
    {
        private int _observed;

        public int Count => values.Count;

        public T this[int index] => values[index];

        public IEnumerator<T> GetEnumerator()
        {
            if (Interlocked.Exchange(ref _observed, 1) == 0)
            {
                beforeEnumeration();
            }

            return values.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class RecordingSkippedStore : ISkippedAssetsMaintenanceStore
    {
        internal int CallCount { get; private set; }
        internal IReadOnlyList<Guid> RemovedIds { get; private set; } = [];
        internal Func<Task<long>>? Clear { get; init; }
        internal Func<IReadOnlyList<Guid>, Task<long>>? Remove { get; init; }

        public Task<long> ClearAllAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Clear?.Invoke() ?? Task.FromResult(0L);
        }

        public Task<long> RemoveAsync(
            IEnumerable<Guid> assetIds,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            RemovedIds = assetIds.ToArray();
            return Remove?.Invoke(RemovedIds)
                ?? Task.FromResult((long)RemovedIds.Count);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
