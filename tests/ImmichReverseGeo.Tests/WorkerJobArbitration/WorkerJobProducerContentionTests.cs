using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerJobArbitration;

[TestClass]
[TestCategory("Change50")]
public sealed class WorkerJobProducerContentionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset Now =
        new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ActiveLookup_RejectsManualAndScheduledProcessingWithoutStoppingOrLaunchingIt()
    {
        await using var fixture = Fixture.Create();
        StateSnapshot before = StateSnapshot.Capture(fixture.State);
        Task lookup = fixture.Lookup.SubmitAsync(Submission());
        LookupSession session = await fixture.LookupWorker.Started.Task.WaitAsync(Bound);

        try
        {
            Assert.AreEqual(WorkerJobKind.CoordinateLookup, fixture.WorkerCoordinator.Snapshot.ActiveOwner.RequireWorker().JobKind);
            Assert.AreEqual(WorkerJobLifecycle.Running, fixture.WorkerCoordinator.Snapshot.ActiveOwner.RequireWorker().Lifecycle);
            Assert.AreEqual(before, StateSnapshot.Capture(fixture.State));

            Assert.AreEqual(
                ProcessingRunAdmissionResult.AlreadyRunning,
                await fixture.Processing.TriggerManualAsync().WaitAsync(Bound));
            Assert.AreEqual(before, StateSnapshot.Capture(fixture.State), "manual Busy creates no processing projection");

            Assert.AreEqual(
                ScheduledTriggerResult.RejectedAlreadyRunning,
                await ((IScheduledRunTrigger)fixture.Processing)
                    .TriggerScheduledAsync(CancellationToken.None)
                    .WaitAsync(Bound));
            Assert.AreEqual(1, fixture.Detector.CallCount);
            Assert.AreEqual(0, fixture.ProcessingBackend.Calls);
            Assert.AreEqual(1, fixture.LookupWorker.Starts);
            Assert.AreEqual(0, session.StopCalls, "processing contention does not preempt Lookup");
            Assert.AreEqual(WorkerJobKind.CoordinateLookup, fixture.WorkerCoordinator.Snapshot.ActiveOwner.RequireWorker().JobKind);
            Assert.AreEqual(
                1,
                fixture.State.GetRecentLog().Count(line => line.EndsWith(
                    "Scheduled run skipped because another background job is already using the heavy worker.",
                    StringComparison.Ordinal)),
                "scheduled contention records only safe cross-kind copy");
        }
        finally
        {
            session.Complete(new CoordinateLookupWorkerOutcome.Completed(Result(fixture.LookupWorker.Request!)));
            await lookup.WaitAsync(Bound);
        }

        Assert.IsNull(fixture.WorkerCoordinator.Snapshot.ActiveOwner);
        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Processing.TriggerManualAsync().WaitAsync(Bound));
        await fixture.ProcessingBackend.Entered.Task.WaitAsync(Bound);
        fixture.ProcessingBackend.Release.TrySetResult();
        await fixture.Processing.WaitForActiveRunAsync().WaitAsync(Bound);
        Assert.AreEqual(1, fixture.ProcessingBackend.Calls, "later manual request uses the released slot once");
    }

    [TestMethod]
    public async Task ActiveScheduledProcessing_MakesLookupBusyWithoutPreemptionOrSecondLaunch()
    {
        await using var fixture = Fixture.Create();
        Task<ScheduledTriggerResult> scheduled = ((IScheduledRunTrigger)fixture.Processing)
            .TriggerScheduledAsync(CancellationToken.None);
        await fixture.ProcessingBackend.Entered.Task.WaitAsync(Bound);
        ProcessingRunRequest active = fixture.Processing.ActiveRequest!;

        try
        {
            Assert.AreEqual(WorkerJobKind.ProcessAssets, fixture.WorkerCoordinator.Snapshot.ActiveOwner.RequireWorker().JobKind);
            Assert.AreEqual(active.RunId, fixture.WorkerCoordinator.ActiveOwner.RequireWorker().JobId);

            await fixture.Lookup.SubmitAsync(Submission()).WaitAsync(Bound);

            Assert.AreEqual(CoordinateLookupPagePhase.Busy, fixture.Lookup.State.Phase);
            StringAssert.Contains(fixture.Lookup.State.Status, "another background job");
            Assert.AreEqual(0, fixture.LookupWorker.Starts, "Busy Lookup starts no external worker");
            Assert.AreSame(active, fixture.Processing.ActiveRequest, "Lookup does not preempt scheduled processing");
            Assert.IsFalse(scheduled.IsCompleted);
        }
        finally
        {
            fixture.ProcessingBackend.Release.TrySetResult();
        }

        Assert.AreEqual(
            ScheduledTriggerResult.AcceptedAfterTerminal,
            await scheduled.WaitAsync(Bound));
        Assert.IsNull(fixture.WorkerCoordinator.Snapshot.ActiveOwner);
        Assert.AreEqual(1, fixture.ProcessingBackend.Calls);
    }

    [TestMethod]
    [DataRow(StopEntryPoint.ManualCancel)]
    [DataRow(StopEntryPoint.ExplicitStop)]
    [DataRow(StopEntryPoint.ScheduledToken)]
    [DataRow(StopEntryPoint.SharedShutdown)]
    [DataRow(StopEntryPoint.HostShutdown)]
    public async Task ProcessAssetsStopEntryPoint_PublishesStoppingBeforeHeldCleanup(
        StopEntryPoint entryPoint)
    {
        await using var fixture = Fixture.Create();
        using var scheduledCancellation = new CancellationTokenSource();
        Task? scheduled = null;
        Task? settlement = null;

        if (entryPoint == StopEntryPoint.ScheduledToken)
        {
            scheduled = ((IScheduledRunTrigger)fixture.Processing)
                .TriggerScheduledAsync(scheduledCancellation.Token);
        }
        else
        {
            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await fixture.Processing.TriggerManualAsync().WaitAsync(Bound));
        }

        await fixture.ProcessingBackend.Entered.Task.WaitAsync(Bound);
        Assert.AreEqual(
            WorkerJobLifecycle.Starting,
            fixture.WorkerCoordinator.Snapshot.ActiveOwner.RequireWorker().Lifecycle);

        switch (entryPoint)
        {
            case StopEntryPoint.ManualCancel:
                Assert.IsTrue(fixture.Processing.CancelActiveRun());
                break;
            case StopEntryPoint.ExplicitStop:
                settlement = fixture.Processing.StopActiveRun();
                Assert.IsNotNull(settlement);
                break;
            case StopEntryPoint.ScheduledToken:
                scheduledCancellation.Cancel();
                break;
            case StopEntryPoint.SharedShutdown:
                settlement = fixture.WorkerCoordinator.BeginShutdown();
                break;
            case StopEntryPoint.HostShutdown:
                settlement = fixture.Processing.BeginShutdown();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entryPoint));
        }

        await fixture.ProcessingBackend.CancellationObserved.Task.WaitAsync(Bound);
        Assert.AreEqual(
            WorkerJobLifecycle.Stopping,
            fixture.WorkerCoordinator.Snapshot.ActiveOwner.RequireWorker().Lifecycle,
            "the shared owner must publish Stopping before cancellation reaches the backend");
        Assert.IsFalse(fixture.Processing.WaitForActiveRunAsync().IsCompleted);
        if (settlement is not null)
        {
            Assert.IsFalse(settlement.IsCompleted);
        }

        fixture.ProcessingBackend.Release.TrySetResult();
        await fixture.Processing.WaitForActiveRunAsync().WaitAsync(Bound);
        if (settlement is not null)
        {
            await settlement.WaitAsync(Bound);
        }

        if (scheduled is not null)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await scheduled.WaitAsync(Bound));
        }

        Assert.IsNull(fixture.WorkerCoordinator.Snapshot.ActiveOwner);
    }

    [TestMethod]
    public async Task ActiveCacheMaintenance_RejectsScheduledProcessingWithGenericHeavyWorkerCopy()
    {
        WorkerJobDescriptor cacheDescriptor = CacheDescriptor();
        await using var fixture = Fixture.Create(cacheDescriptor);
        var admitted = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
            fixture.WorkerCoordinator.TryAdmit(new CacheDispatch(cacheDescriptor)));
        try
        {
            Assert.AreEqual(
                ScheduledTriggerResult.RejectedAlreadyRunning,
                await ((IScheduledRunTrigger)fixture.Processing)
                    .TriggerScheduledAsync(CancellationToken.None)
                    .WaitAsync(Bound));
            Assert.AreEqual(1, fixture.Detector.CallCount);
            Assert.AreEqual(0, fixture.ProcessingBackend.Calls);
            Assert.AreEqual(
                1,
                fixture.State.GetRecentLog().Count(line => line.EndsWith(
                    "Scheduled run skipped because another background job is already using the heavy worker.",
                    StringComparison.Ordinal)));
        }
        finally
        {
            await admitted.Lease.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task HostStoppingNotification_SeesSharedFenceClosedForReentrantLookup()
    {
        await using var fixture = Fixture.Create();
        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Processing.TriggerManualAsync().WaitAsync(Bound));
        await fixture.ProcessingBackend.Entered.Task.WaitAsync(Bound);
        WorkerJobAdmissionResult? reentrantAdmission = null;
        int stoppingObservation = 0;
        void OnChanged()
        {
            if (fixture.WorkerCoordinator.Snapshot.ActiveOwner is not
                    ExclusiveHeavyOwnerBusyMetadata.Worker
                    {
                        Job.Lifecycle: WorkerJobLifecycle.Stopping
                    }
                || Interlocked.CompareExchange(ref stoppingObservation, 1, 0) != 0)
            {
                return;
            }

            reentrantAdmission = fixture.WorkerCoordinator.TryAdmit(
                new CoordinateLookupWorkerJobDispatch(Guid.NewGuid(), Request(Submission())));
        }

        fixture.WorkerCoordinator.Changed += OnChanged;
        Task? shutdown = null;
        try
        {
            shutdown = fixture.Processing.BeginShutdown();
            Assert.AreEqual(1, stoppingObservation);
            Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(reentrantAdmission);
            fixture.ProcessingBackend.Release.TrySetResult();
            await shutdown.WaitAsync(Bound);
        }
        finally
        {
            fixture.WorkerCoordinator.Changed -= OnChanged;
            fixture.ProcessingBackend.Release.TrySetResult();
            if (shutdown is not null)
            {
                await shutdown.WaitAsync(Bound);
            }
        }
    }

    private static CoordinateLookupSubmission Submission() =>
        new(47.3769, 8.5417, true, false, false);

    private static CoordinateLookupRequest Request(CoordinateLookupSubmission submission) =>
        new(
            submission.Latitude,
            submission.Longitude,
            submission.IncludeAirportInfrastructure,
            submission.IncludeLiveOverturePlaces,
            submission.PreferGadmAdministrativeAreas,
            new CoordinateLookupCityResolverOverrides(null, []));

    private static CoordinateLookupResult Result(CoordinateLookupRequest request)
    {
        CoordinateLookupSourceResult skipped = new(
            CoordinateLookupSourceState.Skipped,
            null,
            null,
            null,
            [],
            [],
            null,
            null,
            null,
            null);
        return new CoordinateLookupResult(
            request,
            Now,
            Now.AddSeconds(1),
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.NoMatch,
                null,
                null,
                null,
                null,
                null),
            skipped,
            skipped,
            skipped,
            skipped,
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupProfileSummary(
                null,
                ["locality"],
                CoordinateLookupTieBreak.SmallestArea),
            ["No country matched."],
            new CoordinateLookupFinalLocation(null, null, null));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Fixture(
            ServiceProvider provider,
            ProcessingState state,
            WorkerJobCoordinator workerCoordinator,
            ProcessingRunCoordinator processing,
            CoordinateLookupPageController lookup,
            ImmediateDetector detector,
            GatedProcessingBackend processingBackend,
            LookupWorkerClient lookupWorker)
        {
            _provider = provider;
            State = state;
            WorkerCoordinator = workerCoordinator;
            Processing = processing;
            Lookup = lookup;
            Detector = detector;
            ProcessingBackend = processingBackend;
            LookupWorker = lookupWorker;
        }

        internal ProcessingState State { get; }
        internal WorkerJobCoordinator WorkerCoordinator { get; }
        internal ProcessingRunCoordinator Processing { get; }
        internal CoordinateLookupPageController Lookup { get; }
        internal ImmediateDetector Detector { get; }
        internal GatedProcessingBackend ProcessingBackend { get; }
        internal LookupWorkerClient LookupWorker { get; }

        internal static Fixture Create(WorkerJobDescriptor? additionalDescriptor = null)
        {
            var services = new ServiceCollection();
            var processingBackend = new GatedProcessingBackend();
            services.AddSingleton<ProcessingState>();
            services.AddSingleton<ProcessingStateEventReporter>();
            services.AddScoped<IChildProcessingRunBackend>(_ => processingBackend);
            ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
            ProcessingState state = provider.GetRequiredService<ProcessingState>();
            WorkerJobDescriptor[] descriptors = additionalDescriptor is null
                ? [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup]
                : [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup, additionalDescriptor];
            var workerCoordinator = new WorkerJobCoordinator(descriptors);
            var detector = new ImmediateDetector();
            var processing = new ProcessingRunCoordinator(
                state,
                provider.GetRequiredService<ProcessingStateEventReporter>(),
                detector,
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ProcessingRunCoordinator>.Instance,
                Guid.NewGuid,
                observer: null,
                workerCoordinator);
            var lookupWorker = new LookupWorkerClient();
            var lookup = new CoordinateLookupPageController(
                workerCoordinator,
                lookupWorker,
                new SettingsProvider(),
                Guid.NewGuid,
                static () => { });
            return new Fixture(
                provider,
                state,
                workerCoordinator,
                processing,
                lookup,
                detector,
                processingBackend,
                lookupWorker);
        }

        public async ValueTask DisposeAsync()
        {
            LookupWorker.Session?.Complete(new CoordinateLookupWorkerOutcome.Cancelled());
            ProcessingBackend.Release.TrySetResult();
            await Lookup.DisposeAsync();
            await Processing.BeginShutdown().WaitAsync(Bound);
            await _provider.DisposeAsync();
        }
    }

    private sealed class ImmediateDetector : IProcessingWorkDetector
    {
        internal int CallCount { get; private set; }

        public Task<ProcessingWorkDetectionResult> DetectAsync(ProcessingWorkDetectionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(ProcessingWorkDetectorStub.Result(true));
        }
    }

    private sealed class GatedProcessingBackend : IChildProcessingRunBackend
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await Release.Task;
                throw;
            }
            var session = await reporter.OpenRunAsync(request, Now, CancellationToken.None);
            await session.DetermineEligibilityAsync(0, CancellationToken.None);
            var result = new ProcessingRunResult(
                request,
                Now,
                Now.AddSeconds(1),
                0,
                0,
                0,
                0,
                ProcessingRunOutcome.Completed,
                null);
            await session.FinishAsync(result);
            return result;
        }
    }

    private static WorkerJobDescriptor CacheDescriptor() => new(
        WorkerJobKind.CacheMutation,
        typeof(CacheRequest),
        typeof(CacheResult),
        new WorkerJobArbitrationMetadata(
            WorkerJobCapabilityFamily.CacheMaintenance,
            WorkerJobResourceClass.ExclusiveHeavyWorker,
            IsHeavy: true,
            IsCancellable: true,
            IsGeodataBearing: true));

    public enum StopEntryPoint
    {
        ManualCancel,
        ExplicitStop,
        ScheduledToken,
        SharedShutdown,
        HostShutdown
    }

    private sealed record CacheRequest : IWorkerJobRequest;
    private sealed record CacheResult : IWorkerJobResult;

    private sealed record CacheDispatch : WorkerJobDispatch
    {
        internal CacheDispatch(WorkerJobDescriptor descriptor)
            : base(
                new WorkerJobContext(
                    Guid.NewGuid(),
                    WorkerJobKind.CacheMutation,
                    WorkerJobRequestOrigin.Manual),
                descriptor)
        {
        }
    }

    private sealed class LookupWorkerClient : ICoordinateLookupWorkerClient
    {
        internal TaskCompletionSource<LookupSession> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Starts { get; private set; }
        internal CoordinateLookupRequest? Request { get; private set; }
        internal LookupSession? Session { get; private set; }

        public ValueTask<CoordinateLookupWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CoordinateLookupRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken)
        {
            _ = eventSink;
            cancellationToken.ThrowIfCancellationRequested();
            Starts++;
            Request = request;
            Session = new LookupSession(admission.Context.JobId);
            Started.TrySetResult(Session);
            return ValueTask.FromResult<CoordinateLookupWorkerStartResult>(
                new CoordinateLookupWorkerStartResult.Started(Session, ChildProcessId: 4242));
        }
    }

    private sealed class LookupSession(Guid jobId) : ICoordinateLookupWorkerSession
    {
        private readonly TaskCompletionSource<CoordinateLookupWorkerOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid JobId { get; } = jobId;
        public WorkerJobKind JobKind => WorkerJobKind.CoordinateLookup;
        public InternalWorkerProtocolVersion ProtocolVersion => InternalWorkerProtocolVersion.V2;
        public bool IsCancellable => true;
        public Task<CoordinateLookupWorkerOutcome> Completion => _completion.Task;
        internal int StopCalls { get; private set; }

        public async Task RequestStopAsync()
        {
            StopCalls++;
            await _completion.Task;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void Complete(CoordinateLookupWorkerOutcome outcome)
        {
            _completion.TrySetResult(outcome);
        }
    }

    private sealed class SettingsProvider : ICoordinateLookupSettingsSnapshotProvider
    {
        public ValueTask<CoordinateLookupCityResolverOverrides> GetAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CoordinateLookupCityResolverOverrides(null, []));
        }
    }

    private sealed record StateSnapshot(
        bool IsRunning,
        long Total,
        long Processed,
        long Skipped,
        long Errors,
        string? LastError,
        string? Activity,
        DateTime? Started,
        DateTime? Completed,
        string Logs)
    {
        internal static StateSnapshot Capture(ProcessingState state) => new(
            state.IsRunning,
            state.TotalUnprocessed,
            state.ProcessedThisRun,
            state.SkippedThisRun,
            state.ErrorsThisRun,
            state.LastError,
            state.CurrentActivity,
            state.LastRunStarted,
            state.LastRunCompleted,
            string.Join("\n", state.GetRecentLog()));
    }
}
