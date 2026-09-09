using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.WorkerJobArbitration;

[TestClass]
[TestCategory("Change50")]
public sealed class WorkerJobCoordinatorTests
{
    [TestMethod]
    public async Task ProcessAssetsAndLookup_ContendForOneExclusiveHeavySlot()
    {
        await using var coordinator = CreateCoordinator();
        ProcessAssetsWorkerJobDispatch processing = ProcessingDispatch(ProcessingRunTrigger.Manual);
        CoordinateLookupWorkerJobDispatch lookup = LookupDispatch();

        WorkerJobAdmissionResult.Admitted admitted = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
            coordinator.TryAdmit(processing));
        WorkerJobAdmissionResult.Busy busy = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(
            coordinator.TryAdmit(lookup));

        Assert.AreEqual(WorkerJobKind.ProcessAssets, busy.ActiveJob.JobKind);
        Assert.AreEqual(WorkerJobCapabilityFamily.Processing, busy.ActiveJob.CapabilityFamily);
        Assert.AreEqual(WorkerJobRequestOrigin.Manual, busy.ActiveJob.Origin);
        Assert.IsTrue(busy.ActiveJob.IsCancellable);
        Assert.AreEqual(WorkerJobLifecycle.Admitted, busy.ActiveJob.Lifecycle);
        await admitted.Lease.DisposeAsync();

        WorkerJobAdmissionResult.Admitted lookupAdmission =
            Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(coordinator.TryAdmit(lookup));
        await lookupAdmission.Lease.DisposeAsync();
    }

    [TestMethod]
    public async Task ParallelAdmissionRace_ProducesOneOwnerAndNoReservationQueue()
    {
        await using var coordinator = CreateCoordinator();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WorkerJobAdmissionResult>[] attempts = Enumerable.Range(0, 32)
            .Select(index => Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                return coordinator.TryAdmit(index % 2 == 0
                    ? ProcessingDispatch(ProcessingRunTrigger.Manual, Guid.NewGuid())
                    : LookupDispatch(Guid.NewGuid()));
            }))
            .ToArray();

        start.TrySetResult();
        WorkerJobAdmissionResult[] results = await Task.WhenAll(attempts);
        WorkerJobAdmissionResult.Admitted[] admittedResults =
            results.OfType<WorkerJobAdmissionResult.Admitted>().ToArray();
        Assert.HasCount(1, admittedResults);
        WorkerJobAdmissionResult.Admitted admitted = admittedResults[0];
        Assert.AreEqual(31, results.OfType<WorkerJobAdmissionResult.Busy>().Count());

        await admitted.Lease.DisposeAsync();
        Assert.IsNull(coordinator.Snapshot.ActiveJob);
    }

    [TestMethod]
    public async Task FinalizingOwner_HoldsAdmissionUntilExplicitRelease()
    {
        await using var coordinator = CreateCoordinator();
        WorkerJobAdmissionResult.Admitted admitted = Admit(coordinator, LookupDispatch());

        Assert.IsNotNull(coordinator.ActiveOwner?.AdmittedAtUtc);
        Assert.IsNull(coordinator.ActiveOwner?.StartedAtUtc);
        Assert.IsNull(coordinator.ActiveOwner?.ChildProcessId);

        Assert.IsTrue(admitted.Lease.TryAdvance(admitted.Lease.Context, WorkerJobLifecycle.Starting));
        Assert.IsNotNull(coordinator.ActiveOwner?.StartedAtUtc);
        Assert.IsNull(coordinator.ActiveOwner?.ChildProcessId, "starting without a child has no PID");
        Assert.IsTrue(admitted.Lease.TryAdvance(
            admitted.Lease.Context,
            WorkerJobLifecycle.Running,
            childProcessId: 4321));
        Assert.AreEqual(4321, coordinator.ActiveOwner?.ChildProcessId);
        Assert.IsTrue(admitted.Lease.TryAdvance(admitted.Lease.Context, WorkerJobLifecycle.Finalizing));

        WorkerJobAdmissionResult.Busy busy = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(
            coordinator.TryAdmit(ProcessingDispatch(ProcessingRunTrigger.Scheduled)));
        Assert.AreEqual(WorkerJobLifecycle.Finalizing, busy.ActiveJob.Lifecycle);

        await admitted.Lease.DisposeAsync();
        WorkerJobAdmissionResult.Admitted next = Admit(
            coordinator,
            ProcessingDispatch(ProcessingRunTrigger.Scheduled));
        await next.Lease.DisposeAsync();
    }

    [TestMethod]
    public async Task ShutdownBeforeOwnerBinding_WaitsForBindStopAndFinalRelease()
    {
        await using var coordinator = CreateCoordinator();
        WorkerJobAdmissionResult.Admitted admitted = Admit(coordinator, LookupDispatch());
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task shutdown = coordinator.BeginShutdown();

        Assert.IsFalse(shutdown.IsCompleted);
        Assert.IsTrue(admitted.Lease.IsStopRequested);
        Assert.IsTrue(admitted.Lease.TryBindOwnerStop(admitted.Lease.Context, () =>
        {
            _ = coordinator.Snapshot;
            stopEntered.TrySetResult();
            return Task.CompletedTask;
        }));
        await stopEntered.Task;
        Assert.IsFalse(shutdown.IsCompleted, "shutdown-must-retain-owner-until-final-release");
        Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(
            coordinator.TryAdmit(ProcessingDispatch(ProcessingRunTrigger.Manual)));

        await admitted.Lease.DisposeAsync();
        await shutdown;
    }

    [TestMethod]
    public async Task StartupFailureBeforeOwnerBinding_ReleasesShutdownWithoutCallback()
    {
        await using var coordinator = CreateCoordinator();
        WorkerJobAdmissionResult.Admitted admitted = Admit(
            coordinator,
            ProcessingDispatch(ProcessingRunTrigger.Manual));

        Task shutdown = coordinator.BeginShutdown();
        Assert.IsFalse(shutdown.IsCompleted);

        await admitted.Lease.DisposeAsync();
        await shutdown;
        Assert.IsFalse(admitted.Lease.TryBindOwnerStop(
            admitted.Lease.Context,
            static () => Task.CompletedTask));
    }

    [TestMethod]
    public async Task BoundOwnerStop_CanReenterCoordinatorWithoutLockInversion()
    {
        await using var coordinator = CreateCoordinator();
        WorkerJobAdmissionResult.Admitted admitted = Admit(coordinator, LookupDispatch());
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bindingProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(admitted.Lease.TryBindOwnerStop(admitted.Lease.Context, () =>
        {
            Assert.IsFalse(coordinator.Snapshot.IsAccepting);
            Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(
                coordinator.TryAdmit(ProcessingDispatch(ProcessingRunTrigger.Manual)));
            callbackEntered.TrySetResult();
            try
            {
                bool duplicateBinding = Task.Run(() => admitted.Lease.TryBindOwnerStop(
                        admitted.Lease.Context,
                        static () => Task.CompletedTask))
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .GetAwaiter()
                    .GetResult();
                bindingProbe.TrySetResult(duplicateBinding);
            }
            catch (Exception failure)
            {
                bindingProbe.TrySetException(failure);
            }

            return releaseCallback.Task;
        }));

        Task shutdown = coordinator.BeginShutdown();
        try
        {
            await callbackEntered.Task;
            bool duplicateBinding = await bindingProbe.Task;
            Assert.IsFalse(duplicateBinding, "binding lock is available while the owner callback is active");
            Assert.IsFalse(shutdown.IsCompleted);
        }
        finally
        {
            releaseCallback.TrySetResult();
            await admitted.Lease.DisposeAsync();
        }

        await shutdown;
    }

    [TestMethod]
    public async Task ThrowingOwnerStop_ShutdownWaitsForReleaseThenPreservesFailure()
    {
        var coordinator = CreateCoordinator();
        WorkerJobAdmissionResult.Admitted admitted = Admit(coordinator, LookupDispatch());
        int stopCalls = 0;
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(admitted.Lease.TryBindOwnerStop(admitted.Lease.Context, () =>
        {
            Interlocked.Increment(ref stopCalls);
            callbackEntered.TrySetResult();
            throw new InvalidOperationException("owner-stop-failure");
        }));

        Task shutdown = coordinator.BeginShutdown();
        try
        {
            await callbackEntered.Task;
            Assert.IsFalse(shutdown.IsCompleted, "stop failure must remain joined to final release");
        }
        finally
        {
            await admitted.Lease.DisposeAsync();
        }

        InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => shutdown);
        Assert.AreEqual("owner-stop-failure", failure.Message);
        Assert.AreEqual(1, stopCalls);
    }

    [TestMethod]
    public async Task RepeatedShutdown_InvokesOwnerStopOnceAndKeepsFenceClosed()
    {
        await using var coordinator = CreateCoordinator();
        WorkerJobAdmissionResult.Admitted admitted = Admit(coordinator, LookupDispatch());
        int stopCalls = 0;
        Assert.IsTrue(admitted.Lease.TryBindOwnerStop(admitted.Lease.Context, () =>
        {
            Interlocked.Increment(ref stopCalls);
            return Task.CompletedTask;
        }));

        Task first = coordinator.BeginShutdown();
        Task second = coordinator.BeginShutdown();
        Assert.AreSame(first, second);
        await admitted.Lease.DisposeAsync();
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, stopCalls);
        Assert.IsFalse(coordinator.Snapshot.IsAccepting);
        Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(coordinator.TryAdmit(LookupDispatch()));
    }

    [TestMethod]
    public async Task StaleOrWrongContext_CannotBindStopOrChangeLifecycle()
    {
        await using var coordinator = CreateCoordinator();
        WorkerJobAdmissionResult.Admitted admitted = Admit(coordinator, LookupDispatch());
        var wrong = new WorkerJobContext(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            WorkerJobKind.CoordinateLookup,
            WorkerJobRequestOrigin.Manual);

        Assert.IsFalse(admitted.Lease.TryBindOwnerStop(wrong, static () => Task.CompletedTask));
        Assert.IsFalse(admitted.Lease.TryAdvance(wrong, WorkerJobLifecycle.Running, 123));
        Assert.IsFalse(admitted.Lease.TryAdvance(
            admitted.Lease.Context,
            WorkerJobLifecycle.Running,
            childProcessId: null));
        Assert.IsTrue(admitted.Lease.TryAdvance(admitted.Lease.Context, WorkerJobLifecycle.Starting));
        Assert.IsFalse(admitted.Lease.TryAdvance(admitted.Lease.Context, WorkerJobLifecycle.Admitted));

        await admitted.Lease.DisposeAsync();
        Assert.IsFalse(admitted.Lease.TryAdvance(admitted.Lease.Context, WorkerJobLifecycle.Finalizing));

        WorkerJobAdmissionResult.Admitted next = Admit(
            coordinator,
            ProcessingDispatch(ProcessingRunTrigger.Manual));
        await admitted.Lease.DisposeAsync();
        Assert.AreEqual(next.Lease.Context.JobId, coordinator.ActiveOwner?.JobId, "stale release cannot clear a replacement owner");
        await next.Lease.DisposeAsync();
    }

    [TestMethod]
    public async Task PublicDiagnostics_ExcludeJobIdentityAndProcessId()
    {
        await using var coordinator = CreateCoordinator();
        WorkerJobAdmissionResult.Admitted admitted = Admit(coordinator, LookupDispatch());
        Assert.IsTrue(admitted.Lease.TryAdvance(
            admitted.Lease.Context,
            WorkerJobLifecycle.Running,
            childProcessId: 9988));

        WorkerJobArbitrationDiagnosticSnapshot snapshot = coordinator.Snapshot;
        Assert.IsNotNull(snapshot.ActiveJob);
        string[] propertyNames = snapshot.ActiveJob.GetType().GetProperties()
            .Select(static property => property.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(propertyNames, "JobId");
        CollectionAssert.DoesNotContain(propertyNames, "ChildProcessId");
        CollectionAssert.DoesNotContain(propertyNames, "ProcessId");
        Assert.AreEqual(admitted.Lease.Context.JobId, coordinator.ActiveOwner?.JobId);
        Assert.AreEqual(9988, coordinator.ActiveOwner?.ChildProcessId);

        await admitted.Lease.DisposeAsync();
    }

    [TestMethod]
    public async Task IndependentWebProcessCoordinators_EachAdmitAndReportOnlyTheirLocalOwner()
    {
        await using var first = CreateCoordinator();
        await using var second = CreateCoordinator();
        WorkerJobAdmissionResult.Admitted firstAdmission = Admit(first, LookupDispatch());
        WorkerJobAdmissionResult.Admitted secondAdmission = Admit(second, LookupDispatch());

        try
        {
            Assert.AreNotEqual(firstAdmission.Lease.Context.JobId, secondAdmission.Lease.Context.JobId);
            Assert.AreEqual(WorkerJobKind.CoordinateLookup, first.Snapshot.ActiveJob?.JobKind);
            Assert.AreEqual(WorkerJobKind.CoordinateLookup, second.Snapshot.ActiveJob?.JobKind);
            Assert.AreEqual(firstAdmission.Lease.Context.JobId, first.ActiveOwner?.JobId);
            Assert.AreEqual(secondAdmission.Lease.Context.JobId, second.ActiveOwner?.JobId);
        }
        finally
        {
            await firstAdmission.Lease.DisposeAsync();
            await secondAdmission.Lease.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task UnknownOrInconsistentDescriptor_IsRejectedWithoutOpeningASecondAuthority()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new WorkerJobCoordinator([]));
        Assert.ThrowsExactly<InvalidOperationException>(() => new WorkerJobCoordinator(
            [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.ProcessAssets]));
        var inconsistent = new WorkerJobDescriptor(
            WorkerJobKind.CacheMutation,
            typeof(FakeCacheMutationRequest),
            typeof(FakeCacheMutationResult),
            new WorkerJobArbitrationMetadata(
                WorkerJobCapabilityFamily.CacheMaintenance,
                WorkerJobResourceClass.GeodataMutation,
                IsHeavy: true,
                IsCancellable: true,
                IsGeodataBearing: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => new WorkerJobCoordinator([inconsistent]));

        await using var coordinator = CreateCoordinator();
        var canonicalMetadataButUnregistered = new WorkerJobDescriptor(
            WorkerJobKind.CacheMutation,
            typeof(FakeCacheMutationRequest),
            typeof(FakeCacheMutationResult),
            new WorkerJobArbitrationMetadata(
                WorkerJobCapabilityFamily.CacheMaintenance,
                WorkerJobResourceClass.ExclusiveHeavyWorker,
                IsHeavy: true,
                IsCancellable: true,
                IsGeodataBearing: true));
        WorkerJobAdmissionResult result = coordinator.TryAdmit(
            new FakeCacheMutationDispatch(canonicalMetadataButUnregistered));
        Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(result);
        Assert.IsNull(coordinator.Snapshot.ActiveJob);
    }

    [TestMethod]
    public void RegisteredDescriptors_AreTheImmutableProcessAndLookupSet()
    {
        CollectionAssert.AreEqual(
            new[] { WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup },
            WorkerJobDescriptors.Registered.ToArray());
        ICollection<WorkerJobDescriptor> collection =
            Assert.IsInstanceOfType<ICollection<WorkerJobDescriptor>>(WorkerJobDescriptors.Registered);
        Assert.IsTrue(collection.IsReadOnly);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            collection.Add(WorkerJobDescriptors.ProcessAssets));
    }

    private static WorkerJobCoordinator CreateCoordinator()
    {
        return new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
    }

    private static WorkerJobAdmissionResult.Admitted Admit(
        WorkerJobCoordinator coordinator,
        WorkerJobDispatch dispatch)
    {
        return Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(coordinator.TryAdmit(dispatch));
    }

    private static ProcessAssetsWorkerJobDispatch ProcessingDispatch(
        ProcessingRunTrigger trigger,
        Guid? runId = null)
    {
        return new ProcessAssetsWorkerJobDispatch(new ProcessingRunRequest(runId ?? Guid.NewGuid(), trigger));
    }

    private static CoordinateLookupWorkerJobDispatch LookupDispatch(Guid? jobId = null)
    {
        return new CoordinateLookupWorkerJobDispatch(jobId ?? Guid.NewGuid(), LookupRequest());
    }

    private static CoordinateLookupRequest LookupRequest()
    {
        return new CoordinateLookupRequest(
            47.3769,
            8.5417,
            includeAirportInfrastructure: true,
            includeLiveOverturePlaces: false,
            preferGadmAdministrativeAreas: false,
            new CoordinateLookupCityResolverOverrides(null, []));
    }

    private sealed record FakeCacheMutationRequest : IWorkerJobRequest;
    private sealed record FakeCacheMutationResult : IWorkerJobResult;

    private sealed record FakeCacheMutationDispatch(WorkerJobDescriptor DescriptorValue)
        : WorkerJobDispatch(
            new WorkerJobContext(Guid.NewGuid(), WorkerJobKind.CacheMutation, WorkerJobRequestOrigin.Manual),
            DescriptorValue);
}
