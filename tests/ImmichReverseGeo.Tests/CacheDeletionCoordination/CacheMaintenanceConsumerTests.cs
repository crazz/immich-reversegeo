using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.CacheDeletionCoordination;

[TestClass]
[TestCategory("Change52")]
public sealed class CacheMaintenanceConsumerTests
{
    [TestMethod]
    public async Task RealMaintenanceOwner_IsRetainedAcrossAllWorkerProducerRejections()
    {
        await using var workerCoordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        CacheMaintenanceAdmissionResult.Reserved maintenance =
            Assert.IsInstanceOfType<CacheMaintenanceAdmissionResult.Reserved>(
                workerCoordinator.TryReserveCacheMaintenance(
                    CacheMaintenanceRequestOrigin.GeoBoundariesPage));
        ExclusiveHeavyOwnerSnapshot ownerBefore = workerCoordinator.ActiveOwner!;
        var processingExecutor = new NeverProcessingExecutor();
        var processingState = new ProcessingState();
        await using var processing = new ProcessingRunCoordinator(
            processingState,
            new ProcessingStateEventReporter(processingState),
            AlwaysHasWorkScheduledRunGate.Instance,
            ProcessingRunBackendTestScopeFactory.Create(processingExecutor),
            NullLogger<ProcessingRunCoordinator>.Instance,
            workerCoordinator);
        var lookupClient = new NeverLookupClient();
        await using var lookup = new CoordinateLookupPageController(
            workerCoordinator,
            lookupClient,
            new FixedLookupSettings(),
            Guid.NewGuid,
            static () => { });
        var cacheClient = new NeverCacheMutationClient();
        await using var cacheMutation = new CacheMutationPageController(
            workerCoordinator,
            cacheClient,
            Guid.NewGuid,
            static () => Task.CompletedTask,
            static () => Task.CompletedTask);

        try
        {
            ProcessingRunAdmissionResult processingResult = await processing.TriggerManualAsync();
            await lookup.SubmitAsync(new CoordinateLookupSubmission(47.4, 8.5, true, false, false));
            await cacheMutation.RefreshAsync(CacheMutationSource.Overture, "USA");

            Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning, processingResult);
            Assert.AreEqual(CoordinateLookupPagePhase.Busy, lookup.State.Phase);
            StringAssert.Contains(lookup.State.Status, "cache maintenance");
            Assert.AreEqual(CacheMutationPagePhase.Busy, cacheMutation.State.Phase);
            StringAssert.Contains(cacheMutation.State.Status, "Cache maintenance");
            Assert.IsInstanceOfType<ExclusiveHeavyOwnerBusyMetadata.CacheMaintenance>(
                cacheMutation.State.BusyOwner);
            Assert.AreEqual(0, processingExecutor.Calls);
            Assert.AreEqual(0, lookupClient.Starts);
            Assert.AreEqual(0, cacheClient.Starts);
            Assert.AreEqual(ownerBefore, workerCoordinator.ActiveOwner,
                "producer rejection must retain the same maintenance owner");
        }
        finally
        {
            await maintenance.Reservation.DisposeAsync();
        }

        Assert.IsNull(workerCoordinator.ActiveOwner);
    }

    private sealed class NeverProcessingExecutor : IProcessingRunExecutor
    {
        internal int Calls { get; private set; }

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new AssertFailedException("Busy processing admission must not reach the child backend.");
        }
    }

    private sealed class FixedLookupSettings : ICoordinateLookupSettingsSnapshotProvider
    {
        public ValueTask<CoordinateLookupCityResolverOverrides> GetAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CoordinateLookupCityResolverOverrides(null, []));
        }
    }

    private sealed class NeverLookupClient : ICoordinateLookupWorkerClient
    {
        internal int Starts { get; private set; }

        public ValueTask<CoordinateLookupWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CoordinateLookupRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken)
        {
            Starts++;
            throw new AssertFailedException("Busy lookup admission must not start a child client.");
        }
    }

    private sealed class NeverCacheMutationClient : ICacheMutationWorkerClient
    {
        internal int Starts { get; private set; }

        public ValueTask<CacheMutationWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CacheMutationRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken)
        {
            Starts++;
            throw new AssertFailedException("Busy cache refresh admission must not start a child client.");
        }
    }
}
