using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.WorkerJobArbitration;

[TestClass]
[TestCategory("Change50")]
[TestCategory("Change51")]
public sealed class WorkerJobCoordinatorContractTests
{
    [TestMethod]
    public async Task RegisteredCacheMutationDispatch_ProducesAdmittedBusyAndStoppedUnavailable()
    {
        WorkerJobDescriptor cacheDescriptor = CacheMutationDescriptor();
        await using var coordinator = CreateCoordinator(cacheDescriptor);
        FakeCacheMutationDispatch firstDispatch = CacheDispatch(cacheDescriptor);
        WorkerJobAdmissionResult.Admitted admitted = Admit(coordinator, firstDispatch);

        try
        {
            Assert.AreSame(cacheDescriptor, admitted.Lease.Descriptor);
            Assert.AreEqual(firstDispatch.Context, admitted.Lease.Context);

            WorkerJobAdmissionResult.Busy busy = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(
                coordinator.TryAdmit(CacheDispatch(cacheDescriptor)));
            WorkerJobBusyMetadata activeJob = busy.ActiveOwner.RequireWorker();
            Assert.AreEqual(WorkerJobKind.CacheMutation, activeJob.JobKind);
            Assert.AreEqual(WorkerJobCapabilityFamily.CacheMaintenance, activeJob.CapabilityFamily);
            Assert.AreEqual(WorkerJobRequestOrigin.Manual, activeJob.Origin);
            Assert.IsTrue(activeJob.IsCancellable);
            Assert.AreEqual(WorkerJobLifecycle.Admitted, activeJob.Lifecycle);
        }
        finally
        {
            await admitted.Lease.DisposeAsync();
        }

        await coordinator.BeginShutdown();
        WorkerJobAdmissionResult.Unavailable unavailable =
            Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(
                coordinator.TryAdmit(CacheDispatch(cacheDescriptor)));
        Assert.AreEqual("worker-admission-stopped", unavailable.Code);
        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
    }

    [TestMethod]
    public async Task CrossKindBarrierRaces_HoldAtMostOneAdmissionAndReuseTheReleasedSlot()
    {
        WorkerJobDescriptor cacheDescriptor = CacheMutationDescriptor();
        await using var coordinator = CreateCoordinator(cacheDescriptor);
        var heldAdmissions = new HeldAdmissionCounter();

        for (int round = 0; round < 32; round++)
        {
            await RunCrossKindBarrierRoundAsync(coordinator, cacheDescriptor, heldAdmissions, round);
        }

        Assert.AreEqual(0, heldAdmissions.Current);
        Assert.AreEqual(1, heldAdmissions.Maximum);

        WorkerJobDispatch[] reuseOrder =
        [
            ProcessingDispatch(),
            LookupDispatch(),
            CacheDispatch(cacheDescriptor)
        ];
        foreach (WorkerJobDispatch dispatch in reuseOrder)
        {
            WorkerJobAdmissionResult.Admitted admitted = Admit(coordinator, dispatch);
            try
            {
                Assert.AreEqual(dispatch.Context, admitted.Lease.Context);
                Assert.AreSame(dispatch.Descriptor, admitted.Lease.Descriptor);
            }
            finally
            {
                await admitted.Lease.DisposeAsync();
            }
        }

        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
    }

    [TestMethod]
    [DataRow(WorkerJobKind.ProcessAssets, WorkerJobKind.CoordinateLookup)]
    [DataRow(WorkerJobKind.ProcessAssets, WorkerJobKind.CacheMutation)]
    [DataRow(WorkerJobKind.CoordinateLookup, WorkerJobKind.ProcessAssets)]
    [DataRow(WorkerJobKind.CoordinateLookup, WorkerJobKind.CacheMutation)]
    [DataRow(WorkerJobKind.CacheMutation, WorkerJobKind.ProcessAssets)]
    [DataRow(WorkerJobKind.CacheMutation, WorkerJobKind.CoordinateLookup)]
    public async Task CanonicalExclusiveWorkerDescriptors_BlockEveryCrossKindPairAndReuse(
        WorkerJobKind ownerKind,
        WorkerJobKind contenderKind)
    {
        await using WorkerJobCoordinator coordinator =
            new(WorkerJobDescriptors.Registered);
        WorkerJobDispatch ownerDispatch = DispatchFor(ownerKind);
        WorkerJobDispatch contenderDispatch = DispatchFor(contenderKind);
        WorkerJobAdmissionResult.Admitted owner = Admit(coordinator, ownerDispatch);

        try
        {
            WorkerJobAdmissionResult.Busy busy =
                Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(
                    coordinator.TryAdmit(contenderDispatch));
            WorkerJobBusyMetadata activeJob = busy.ActiveOwner.RequireWorker();
            Assert.AreEqual(ownerKind, activeJob.JobKind);
            Assert.AreEqual(
                owner.Lease.Descriptor.Arbitration.CapabilityFamily,
                activeJob.CapabilityFamily);
            Assert.AreEqual(
                owner.Lease.Context.Origin,
                activeJob.Origin);
        }
        finally
        {
            await owner.Lease.DisposeAsync();
        }

        WorkerJobAdmissionResult.Admitted reused = Admit(
            coordinator,
            contenderDispatch);
        try
        {
            Assert.AreEqual(contenderKind, reused.Lease.Context.JobKind);
            Assert.AreSame(contenderDispatch.Descriptor, reused.Lease.Descriptor);
        }
        finally
        {
            await reused.Lease.DisposeAsync();
        }

        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
    }

    [TestMethod]
    public Task ObservedOwnerFailure_HoldsAdmissionUntilCallerFinallyReleasesLease()
    {
        return AssertCallerFinallyBoundaryAsync(OwnerBoundary.Failure);
    }

    [TestMethod]
    public Task ObservedOwnerCancellation_HoldsAdmissionUntilCallerFinallyReleasesLease()
    {
        return AssertCallerFinallyBoundaryAsync(OwnerBoundary.Cancellation);
    }

    [TestMethod]
    public async Task ShutdownWithActiveCacheMutation_WaitsForBoundStopAndLeaseRelease()
    {
        WorkerJobDescriptor cacheDescriptor = CacheMutationDescriptor();
        await using var coordinator = CreateCoordinator(cacheDescriptor);
        WorkerJobAdmissionResult.Admitted admitted = Admit(
            coordinator,
            CacheDispatch(cacheDescriptor));
        var stopCallbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStopCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? shutdown = null;

        try
        {
            Assert.IsTrue(admitted.Lease.TryBindOwnerStop(admitted.Lease.Context, () =>
            {
                stopCallbackEntered.TrySetResult();
                return releaseStopCallback.Task;
            }));

            shutdown = coordinator.BeginShutdown();
            Task first = await Task.WhenAny(stopCallbackEntered.Task, shutdown);
            if (ReferenceEquals(first, shutdown))
            {
                await shutdown;
                Assert.Fail("shutdown completed before the bound owner-stop callback entered");
            }

            await stopCallbackEntered.Task;
            Assert.IsTrue(admitted.Lease.IsStopRequested);
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.IsFalse(coordinator.Snapshot.IsAccepting);
            WorkerJobAdmissionResult.Unavailable unavailable =
                Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(
                    coordinator.TryAdmit(CacheDispatch(cacheDescriptor)));
            Assert.AreEqual("worker-admission-stopped", unavailable.Code);
        }
        finally
        {
            releaseStopCallback.TrySetResult();
            await admitted.Lease.DisposeAsync();
            if (shutdown is not null)
            {
                await shutdown;
            }
        }

        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
        Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(
            coordinator.TryAdmit(CacheDispatch(cacheDescriptor)));
    }

    private static async Task RunCrossKindBarrierRoundAsync(
        WorkerJobCoordinator coordinator,
        WorkerJobDescriptor cacheDescriptor,
        HeldAdmissionCounter heldAdmissions,
        int round)
    {
        WorkerJobDispatch[] dispatches =
        [
            ProcessingDispatch(),
            LookupDispatch(),
            CacheDispatch(cacheDescriptor)
        ];
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int readyCount = 0;
        WorkerJobAdmissionResult[] results = [];

        async Task<WorkerJobAdmissionResult> AttemptAsync(WorkerJobDispatch dispatch)
        {
            if (Interlocked.Increment(ref readyCount) == dispatches.Length)
            {
                allReady.TrySetResult();
            }

            await start.Task.ConfigureAwait(false);
            WorkerJobAdmissionResult result = coordinator.TryAdmit(dispatch);
            if (result is WorkerJobAdmissionResult.Admitted)
            {
                heldAdmissions.Acquire();
            }

            return result;
        }

        Task<WorkerJobAdmissionResult>[] attempts = dispatches
            .Select(AttemptAsync)
            .ToArray();
        try
        {
            await allReady.Task;
            Assert.IsTrue(
                attempts.All(static attempt => !attempt.IsCompleted),
                $"round {round}: contenders must remain behind the start gate after readiness");

            start.TrySetResult();
            results = await Task.WhenAll(attempts);
            WorkerJobAdmissionResult.Admitted[] admitted =
                results.OfType<WorkerJobAdmissionResult.Admitted>().ToArray();
            WorkerJobAdmissionResult.Busy[] busy =
                results.OfType<WorkerJobAdmissionResult.Busy>().ToArray();
            Assert.HasCount(1, admitted, $"round {round}: admitted lease count, not process launches");
            Assert.HasCount(2, busy, $"round {round}: no reservation queue");
            Assert.AreEqual(1, heldAdmissions.Current, $"round {round}: concurrently held admissions");
            Assert.IsTrue(heldAdmissions.Maximum <= 1, $"round {round}: maximum held admissions");

            WorkerJobAdmissionResult.Admitted winner = admitted[0];
            foreach (WorkerJobAdmissionResult.Busy rejected in busy)
            {
                WorkerJobBusyMetadata activeJob = rejected.ActiveOwner.RequireWorker();
                Assert.AreEqual(winner.Lease.Context.JobKind, activeJob.JobKind);
                Assert.AreEqual(
                    winner.Lease.Descriptor.Arbitration.CapabilityFamily,
                    activeJob.CapabilityFamily);
            }
        }
        finally
        {
            start.TrySetResult();
            if (results.Length == 0)
            {
                results = await Task.WhenAll(attempts);
            }

            foreach (WorkerJobAdmissionResult.Admitted admitted in
                     results.OfType<WorkerJobAdmissionResult.Admitted>())
            {
                await admitted.Lease.DisposeAsync();
                heldAdmissions.Release();
            }
        }

        Assert.AreEqual(0, heldAdmissions.Current, $"round {round}: release boundary");
        Assert.IsNull(coordinator.Snapshot.ActiveOwner);
    }

    private static async Task AssertCallerFinallyBoundaryAsync(OwnerBoundary boundary)
    {
        WorkerJobDescriptor cacheDescriptor = CacheMutationDescriptor();
        await using var coordinator = CreateCoordinator(cacheDescriptor);
        var boundaryObserved = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwnerFinally = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task owner = RunOwnerBoundaryAsync(
            coordinator,
            cacheDescriptor,
            boundary,
            boundaryObserved,
            releaseOwnerFinally);

        Exception? observedFailure = null;
        Exception? ownerFailure = null;
        try
        {
            Task first = await Task.WhenAny(boundaryObserved.Task, owner);
            if (ReferenceEquals(first, owner))
            {
                await owner;
                Assert.Fail("owner completed before publishing its failure or cancellation boundary");
            }

            observedFailure = await boundaryObserved.Task;
            Assert.IsFalse(owner.IsCompleted, "the caller has not entered its lease-release finally boundary");
            AssertCacheOwnerIsBusy(coordinator, ProcessingDispatch());
            AssertCacheOwnerIsBusy(coordinator, LookupDispatch());
        }
        finally
        {
            releaseOwnerFinally.TrySetResult();
            try
            {
                await owner;
            }
            catch (Exception failure)
            {
                ownerFailure = failure;
            }
        }

        Assert.IsNotNull(observedFailure);
        Assert.IsNotNull(ownerFailure);
        if (boundary == OwnerBoundary.Failure)
        {
            Assert.AreSame(observedFailure, ownerFailure);
            Assert.AreEqual(typeof(InvalidOperationException), ownerFailure!.GetType());
        }
        else
        {
            Assert.IsTrue(observedFailure is OperationCanceledException);
            Assert.IsTrue(ownerFailure is OperationCanceledException);
            Assert.IsTrue(owner.IsCanceled);
        }

        WorkerJobDispatch[] reuseOrder = [ProcessingDispatch(), LookupDispatch()];
        foreach (WorkerJobDispatch dispatch in reuseOrder)
        {
            WorkerJobAdmissionResult.Admitted admitted = Admit(coordinator, dispatch);
            try
            {
                Assert.AreEqual(dispatch.Context.JobKind, admitted.Lease.Context.JobKind);
            }
            finally
            {
                await admitted.Lease.DisposeAsync();
            }
        }
    }

    private static async Task RunOwnerBoundaryAsync(
        WorkerJobCoordinator coordinator,
        WorkerJobDescriptor cacheDescriptor,
        OwnerBoundary boundary,
        TaskCompletionSource<Exception> boundaryObserved,
        TaskCompletionSource releaseOwnerFinally)
    {
        WorkerJobAdmissionResult.Admitted admitted = Admit(
            coordinator,
            CacheDispatch(cacheDescriptor));
        try
        {
            try
            {
                if (boundary == OwnerBoundary.Failure)
                {
                    throw new InvalidOperationException("cache-owner-failure");
                }

                var cancellationToken = new CancellationToken(canceled: true);
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("the cancellation boundary was not observed");
            }
            catch (Exception failure)
            {
                boundaryObserved.TrySetResult(failure);
                await releaseOwnerFinally.Task.ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            await admitted.Lease.DisposeAsync();
        }
    }

    private static void AssertCacheOwnerIsBusy(
        WorkerJobCoordinator coordinator,
        WorkerJobDispatch contender)
    {
        WorkerJobAdmissionResult.Busy busy = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(
            coordinator.TryAdmit(contender));
        Assert.AreEqual(WorkerJobKind.CacheMutation, busy.ActiveOwner.RequireWorker().JobKind);
        Assert.AreEqual(WorkerJobCapabilityFamily.CacheMaintenance, busy.ActiveOwner.RequireWorker().CapabilityFamily);
    }

    private static WorkerJobCoordinator CreateCoordinator(WorkerJobDescriptor cacheDescriptor)
    {
        return new WorkerJobCoordinator(
            [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup, cacheDescriptor]);
    }

    private static WorkerJobAdmissionResult.Admitted Admit(
        WorkerJobCoordinator coordinator,
        WorkerJobDispatch dispatch)
    {
        return Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(coordinator.TryAdmit(dispatch));
    }

    private static ProcessAssetsWorkerJobDispatch ProcessingDispatch()
    {
        return new ProcessAssetsWorkerJobDispatch(
            new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual));
    }

    private static CoordinateLookupWorkerJobDispatch LookupDispatch()
    {
        return new CoordinateLookupWorkerJobDispatch(
            Guid.NewGuid(),
            new CoordinateLookupRequest(
                47.3769,
                8.5417,
                includeAirportInfrastructure: true,
                includeLiveOverturePlaces: false,
                preferGadmAdministrativeAreas: false,
                new CoordinateLookupCityResolverOverrides(null, [])));
    }

    private static WorkerJobDispatch DispatchFor(WorkerJobKind kind)
    {
        return kind switch
        {
            WorkerJobKind.ProcessAssets => ProcessingDispatch(),
            WorkerJobKind.CoordinateLookup => LookupDispatch(),
            WorkerJobKind.CacheMutation => CacheDispatch(WorkerJobDescriptors.CacheMutation),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static WorkerJobDescriptor CacheMutationDescriptor() =>
        WorkerJobDescriptors.CacheMutation;

    private static FakeCacheMutationDispatch CacheDispatch(WorkerJobDescriptor descriptor)
    {
        return new FakeCacheMutationDispatch(descriptor, Guid.NewGuid());
    }

    private enum OwnerBoundary
    {
        Failure,
        Cancellation
    }

    private sealed class HeldAdmissionCounter
    {
        private int _current;
        private int _maximum;

        internal int Current => Volatile.Read(ref _current);
        internal int Maximum => Volatile.Read(ref _maximum);

        internal void Acquire()
        {
            int current = Interlocked.Increment(ref _current);
            int maximum = Volatile.Read(ref _maximum);
            while (current > maximum)
            {
                int observed = Interlocked.CompareExchange(ref _maximum, current, maximum);
                if (observed == maximum)
                {
                    return;
                }

                maximum = observed;
            }
        }

        internal void Release()
        {
            Assert.IsTrue(Interlocked.Decrement(ref _current) >= 0);
        }
    }

    private sealed record FakeCacheMutationRequest : IWorkerJobRequest;
    private sealed record FakeCacheMutationResult : IWorkerJobResult;

    private sealed record FakeCacheMutationDispatch(
        WorkerJobDescriptor DescriptorValue,
        Guid JobId)
        : WorkerJobDispatch(
            new WorkerJobContext(JobId, WorkerJobKind.CacheMutation, WorkerJobRequestOrigin.Manual),
            DescriptorValue);
}
