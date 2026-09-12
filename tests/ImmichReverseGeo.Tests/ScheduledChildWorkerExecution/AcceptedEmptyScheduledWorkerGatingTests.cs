using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.ScheduledChildWorkerExecution;

[TestClass]
[TestCategory("Change50")]
[DoNotParallelize]
public sealed class AcceptedEmptyScheduledWorkerGatingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task DetectorNoWork_LeavesIdentityAdmissionStateAndBackendAtZero()
    {
        var detector = new SignalDetector();
        await using var fixture = Fixture.Create(detector);
        StateSnapshot before = StateSnapshot.Capture(fixture.State);

        Task<ScheduledTriggerResult> scheduled = fixture.Trigger.TriggerScheduledAsync(CancellationToken.None);
        await detector.Entered.Task.WaitAsync(Bound);

        Assert.AreEqual(1, detector.CallCount);
        Assert.AreEqual(0, fixture.IdentityCalls);
        Assert.AreEqual(0, fixture.CancellationFactory.CreateCalls);
        Assert.AreEqual(0, fixture.Observer.AdmissionCalls);
        Assert.AreEqual(0, fixture.Backend.ResolutionCalls);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsNull(fixture.WorkerCoordinator.Snapshot.ActiveOwner);
        Assert.AreEqual(before, StateSnapshot.Capture(fixture.State));

        detector.Decide(false);
        Assert.AreEqual(
            ScheduledTriggerResult.AcceptedAfterTerminal,
            await scheduled.WaitAsync(Bound));
        Assert.AreEqual(0, fixture.IdentityCalls);
        Assert.AreEqual(0, fixture.CancellationFactory.CreateCalls);
        Assert.AreEqual(0, fixture.Observer.AdmissionCalls);
        Assert.AreEqual(0, fixture.Backend.ResolutionCalls);
        Assert.AreEqual(before, StateSnapshot.Capture(fixture.State));
        Assert.AreEqual(1, fixture.Logger.InformationCount);
    }

    [TestMethod]
    public async Task DetectorFailure_UsesBoundedLoggerAndCreatesNoRunArtifacts()
    {
        var detector = new SignalDetector();
        await using var fixture = Fixture.Create(detector);
        StateSnapshot before = StateSnapshot.Capture(fixture.State);
        Task<ScheduledTriggerResult> scheduled = fixture.Trigger.TriggerScheduledAsync(CancellationToken.None);
        await detector.Entered.Task.WaitAsync(Bound);

        detector.Fail(new InvalidOperationException("database-password-must-not-escape"));

        Assert.AreEqual(
            ScheduledTriggerResult.AcceptedAfterTerminal,
            await scheduled.WaitAsync(Bound));
        Assert.AreEqual(0, fixture.IdentityCalls);
        Assert.AreEqual(0, fixture.CancellationFactory.CreateCalls);
        Assert.AreEqual(0, fixture.Observer.AdmissionCalls);
        Assert.AreEqual(0, fixture.Backend.ResolutionCalls);
        Assert.IsNull(fixture.WorkerCoordinator.Snapshot.ActiveOwner);
        Assert.AreEqual(before, StateSnapshot.Capture(fixture.State));
        Assert.AreEqual(1, fixture.Logger.ErrorCount);
        Assert.IsFalse(fixture.Logger.Messages.Any(static message =>
            message.Contains("database-password", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DetectorCancellation_UsesPreflightTokenAndCreatesNoRunArtifacts()
    {
        var detector = new SignalDetector();
        await using var fixture = Fixture.Create(detector);
        StateSnapshot before = StateSnapshot.Capture(fixture.State);
        using var stopping = new CancellationTokenSource();
        Task<ScheduledTriggerResult> scheduled = fixture.Trigger.TriggerScheduledAsync(stopping.Token);
        await detector.Entered.Task.WaitAsync(Bound);

        stopping.Cancel();

        OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(
            () => scheduled.WaitAsync(Bound));
        Assert.AreEqual(stopping.Token, failure.CancellationToken);
        Assert.AreNotEqual(stopping.Token, detector.Token, "detector receives the linked preflight token");
        Assert.AreEqual(0, fixture.IdentityCalls);
        Assert.AreEqual(0, fixture.CancellationFactory.CreateCalls);
        Assert.AreEqual(0, fixture.Observer.AdmissionCalls);
        Assert.AreEqual(0, fixture.Backend.ResolutionCalls);
        Assert.IsNull(fixture.WorkerCoordinator.Snapshot.ActiveOwner);
        Assert.AreEqual(before, StateSnapshot.Capture(fixture.State));
    }

    [TestMethod]
    public async Task AdmittedCancellationFactoryFailure_ReleasesSlotAndDoesNotResolveBackend()
    {
        var services = new ServiceCollection();
        var backend = new BackendBoundary();
        services.AddSingleton<ProcessingState>();
        services.AddSingleton<ProcessingStateEventReporter>();
        services.AddScoped<IChildProcessingRunBackend>(_ => backend.Resolve());
        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        var workerCoordinator = new WorkerJobCoordinator(
            [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup]);
        var cancellationFactory = new ThrowingCancellationFactory();
        var coordinator = new ProcessingRunCoordinator(
            provider.GetRequiredService<ProcessingState>(),
            provider.GetRequiredService<ProcessingStateEventReporter>(),
            new ImmediateDetector(true),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProcessingRunCoordinator>.Instance,
            Guid.NewGuid,
            cancellationFactory,
            observer: null,
            workerCoordinator);
        IWorkerJobAdmissionLease? reuse = null;
        try
        {
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => ((IScheduledRunTrigger)coordinator).TriggerScheduledAsync(CancellationToken.None));
            Assert.AreEqual("cancellation-factory-startup", failure.Message);
            Assert.AreEqual(1, cancellationFactory.CreateCalls);
            Assert.AreEqual(0, backend.ResolutionCalls);
            Assert.IsNull(workerCoordinator.Snapshot.ActiveOwner);

            reuse = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
                workerCoordinator.TryAdmit(LookupDispatch())).Lease;
        }
        finally
        {
            if (reuse is not null)
            {
                await reuse.DisposeAsync();
            }

            await coordinator.BeginShutdown().WaitAsync(Bound);
        }
    }

    [TestMethod]
    public async Task ScheduledRegistrationAndCancellationDisposalFailure_PreservesStartupFailureAndReleasesSlot()
    {
        var services = new ServiceCollection();
        var backend = new BackendBoundary();
        services.AddSingleton<ProcessingState>();
        services.AddSingleton<ProcessingStateEventReporter>();
        services.AddScoped<IChildProcessingRunBackend>(_ => backend.Resolve());
        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        var workerCoordinator = new WorkerJobCoordinator(WorkerJobDescriptors.Registered);
        var cancellationFactory = new RegistrationAndDisposalFaultCancellationFactory();
        var coordinator = new ProcessingRunCoordinator(
            provider.GetRequiredService<ProcessingState>(),
            provider.GetRequiredService<ProcessingStateEventReporter>(),
            new ImmediateDetector(true),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProcessingRunCoordinator>.Instance,
            Guid.NewGuid,
            cancellationFactory,
            observer: null,
            workerCoordinator);
        IWorkerJobAdmissionLease? reuse = null;
        try
        {
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => ((IScheduledRunTrigger)coordinator).TriggerScheduledAsync(CancellationToken.None));
            Assert.AreEqual("scheduled-registration-startup", failure.Message);
            Assert.AreEqual(1, cancellationFactory.Cancellation.DisposeCalls);
            Assert.AreEqual(0, backend.ResolutionCalls);
            Assert.IsNull(workerCoordinator.Snapshot.ActiveOwner);

            reuse = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
                workerCoordinator.TryAdmit(LookupDispatch())).Lease;
        }
        finally
        {
            if (reuse is not null)
            {
                await reuse.DisposeAsync();
            }

            await coordinator.BeginShutdown().WaitAsync(Bound);
        }
    }

    [TestMethod]
    public async Task PreflightShutdown_ClosesSharedFenceBeforeThrowingCancellationCallback()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProcessingState>();
        services.AddSingleton<ProcessingStateEventReporter>();
        services.AddScoped<IChildProcessingRunBackend>(_ =>
            throw new AssertFailedException("preflight shutdown must not resolve a backend"));
        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        var workerCoordinator = new WorkerJobCoordinator(
            [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup]);
        var detector = new ReentrantCancellationDetector(workerCoordinator);
        var coordinator = new ProcessingRunCoordinator(
            provider.GetRequiredService<ProcessingState>(),
            provider.GetRequiredService<ProcessingStateEventReporter>(),
            detector,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProcessingRunCoordinator>.Instance,
            Guid.NewGuid,
            new TrackingCancellationFactory(),
            observer: null,
            workerCoordinator);

        Task<ScheduledTriggerResult> scheduled =
            ((IScheduledRunTrigger)coordinator).TriggerScheduledAsync(CancellationToken.None);
        Task? shutdown = null;
        try
        {
            await detector.Entered.Task.WaitAsync(Bound);
            shutdown = coordinator.BeginShutdown();
            await detector.CallbackEntered.Task.WaitAsync(Bound);

            Assert.IsInstanceOfType<WorkerJobAdmissionResult.Unavailable>(detector.LookupAdmission);
            await Assert.ThrowsAsync<OperationCanceledException>(() => scheduled.WaitAsync(Bound));
            await Assert.ThrowsExactlyAsync<AggregateException>(() => shutdown.WaitAsync(Bound));
            Assert.IsNull(workerCoordinator.Snapshot.ActiveOwner);
        }
        finally
        {
            shutdown ??= coordinator.BeginShutdown();
            await ObserveCompletionAsync(scheduled);
            await ObserveCompletionAsync(shutdown);
        }
    }

    [TestMethod]
    [TestCategory("Change57")]
    public async Task DetectionRequestPrecedesIdentityAndCapturesTheLinkedTokenWithoutRunMetadata()
    {
        var detector = new GatedProcessingWorkDetector();
        await using var fixture = Fixture.Create(detector);
        using var caller = new CancellationTokenSource();
        StateSnapshot before = StateSnapshot.Capture(fixture.State);
        Task<ScheduledTriggerResult> scheduled = fixture.Trigger.TriggerScheduledAsync(caller.Token);
        GatedProcessingWorkDetector.Invocation invocation = await detector.NextAsync().WaitAsync(Bound);
        try
        {
            Assert.AreEqual(ProcessingRunTrigger.Scheduled, invocation.Request.Trigger);
            Assert.AreSame(ProcessingWorkDetectionSnapshot.Current, invocation.Request.Snapshot);
            Assert.AreNotEqual(caller.Token, invocation.Token, "The existing linked preflight token is forwarded.");
            Assert.AreEqual(0, fixture.IdentityCalls);
            Assert.AreEqual(0, fixture.Observer.AdmissionCalls);
            Assert.IsNull(fixture.Coordinator.ActiveRequest);
            Assert.AreEqual(before, StateSnapshot.Capture(fixture.State));
            caller.Cancel();
            Assert.IsTrue(invocation.Token.IsCancellationRequested, "The captured token is linked to this caller, not a detached fake token.");
            invocation.Cancel();
            OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(() => scheduled.WaitAsync(Bound));
            Assert.AreEqual(caller.Token, failure.CancellationToken);
            Assert.AreEqual(1, detector.Calls.Length);
            Assert.AreEqual(0, fixture.CancellationFactory.CreateCalls);
            Assert.AreEqual(0, fixture.Backend.ResolutionCalls);
            Assert.AreEqual(before, StateSnapshot.Capture(fixture.State));
        }
        finally
        {
            invocation.Completion.TrySetCanceled(invocation.Token);
            try
            {
                await scheduled.WaitAsync(Bound);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected terminal outcome, including assertion-failure cleanup.
            }
        }
    }

    [TestMethod]
    [TestCategory("Change57")]
    [DataRow(false, 0, false)]
    [DataRow(false, 1, false)]
    [DataRow(false, 1, true)]
    [DataRow(true, 0, false)]
    [DataRow(true, 1, false)]
    [DataRow(true, 1, true)]
    public async Task OnlyHasWorkSelectsNoWorkOrAdmissionContention(bool hasWork, int implementationKind, bool fallback)
    {
        var kind = (ProcessingWorkDetectorKind)implementationKind;
        var detector = ProcessingWorkDetectorStub.Scripted(ProcessingWorkDetectorStub.Result(hasWork, kind, fallback));
        await using var fixture = Fixture.Create(detector, allowAdmissionAttempt: hasWork);
        await using var owner = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
            fixture.WorkerCoordinator.TryAdmit(LookupDispatch())).Lease;
        var activeOwner = fixture.WorkerCoordinator.Snapshot.ActiveOwner;
        StateSnapshot before = StateSnapshot.Capture(fixture.State);

        ScheduledTriggerResult result = await fixture.Trigger.TriggerScheduledAsync(CancellationToken.None).WaitAsync(Bound);

        Assert.AreEqual(hasWork ? ScheduledTriggerResult.RejectedAlreadyRunning : ScheduledTriggerResult.AcceptedAfterTerminal, result);
        Assert.AreEqual(1, detector.Calls.Length);
        Assert.AreEqual(hasWork ? 1 : 0, fixture.Observer.AdmissionCalls);
        Assert.AreEqual(activeOwner, fixture.WorkerCoordinator.Snapshot.ActiveOwner, "Detection/admission loss cannot replace the existing owner.");
        Assert.AreEqual(0, fixture.CancellationFactory.CreateCalls);
        Assert.AreEqual(0, fixture.Backend.ResolutionCalls);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        if (!hasWork)
        {
            Assert.AreEqual(0, fixture.IdentityCalls);
            Assert.AreEqual(before, StateSnapshot.Capture(fixture.State));
            Assert.AreEqual(1, fixture.Logger.InformationCount);
        }
        else
        {
            Assert.IsFalse(fixture.State.IsRunning);
            Assert.AreEqual(before.Started, StateSnapshot.Capture(fixture.State).Started);
        }
    }

    private static CoordinateLookupWorkerJobDispatch LookupDispatch() => new(
        Guid.NewGuid(),
        new CoordinateLookupRequest(
            47.3769,
            8.5417,
            includeAirportInfrastructure: true,
            includeLiveOverturePlaces: false,
            preferGadmAdministrativeAreas: false,
            new CoordinateLookupCityResolverOverrides(null, [])));

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Bound);
        }
        catch
        {
            // The test assertions above own the expected cancellation/failure shape.
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private int _identityCalls;

        private Fixture(
            ServiceProvider provider,
            ProcessingState state,
            WorkerJobCoordinator workerCoordinator,
            ProcessingRunCoordinator coordinator,
            TrackingCancellationFactory cancellationFactory,
            AdmissionObserver observer,
            RecordingLogger logger,
            BackendBoundary backend)
        {
            _provider = provider;
            State = state;
            WorkerCoordinator = workerCoordinator;
            Coordinator = coordinator;
            Trigger = coordinator;
            CancellationFactory = cancellationFactory;
            Observer = observer;
            Logger = logger;
            Backend = backend;
        }

        internal ProcessingState State { get; }
        internal WorkerJobCoordinator WorkerCoordinator { get; }
        internal ProcessingRunCoordinator Coordinator { get; }
        internal IScheduledRunTrigger Trigger { get; }
        internal TrackingCancellationFactory CancellationFactory { get; }
        internal AdmissionObserver Observer { get; }
        internal RecordingLogger Logger { get; }
        internal BackendBoundary Backend { get; }
        internal int IdentityCalls => Volatile.Read(ref _identityCalls);

        internal static Fixture Create(IProcessingWorkDetector detector, bool allowAdmissionAttempt = false)
        {
            var services = new ServiceCollection();
            var backend = new BackendBoundary();
            services.AddSingleton<ProcessingState>();
            services.AddSingleton<ProcessingStateEventReporter>();
            services.AddScoped<IChildProcessingRunBackend>(_ => backend.Resolve());
            ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
            ProcessingState state = provider.GetRequiredService<ProcessingState>();
            ProcessingStateEventReporter reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
            var workerCoordinator = new WorkerJobCoordinator(
                [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup]);
            var cancellationFactory = new TrackingCancellationFactory();
            var observer = new AdmissionObserver { AllowAdmissionAttempt = allowAdmissionAttempt };
            var logger = new RecordingLogger();
            Fixture? fixture = null;
            var coordinator = new ProcessingRunCoordinator(
                state,
                reporter,
                detector,
                provider.GetRequiredService<IServiceScopeFactory>(),
                logger,
                () =>
                {
                    Interlocked.Increment(ref fixture!._identityCalls);
                    return Guid.NewGuid();
                },
                cancellationFactory,
                observer,
                workerCoordinator);
            fixture = new Fixture(
                provider,
                state,
                workerCoordinator,
                coordinator,
                cancellationFactory,
                observer,
                logger,
                backend);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    private sealed class SignalDetector : IProcessingWorkDetector
    {
        private readonly TaskCompletionSource<ProcessingWorkDetectionResult> _decision =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CallCount { get; private set; }
        internal CancellationToken Token { get; private set; }

        public Task<ProcessingWorkDetectionResult> DetectAsync(ProcessingWorkDetectionRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            Token = cancellationToken;
            cancellationToken.Register(() => _decision.TrySetCanceled(cancellationToken));
            Entered.TrySetResult();
            return _decision.Task;
        }

        internal void Decide(bool hasWork) => _decision.TrySetResult(ProcessingWorkDetectorStub.Result(hasWork));
        internal void Fail(Exception failure) => _decision.TrySetException(failure);
    }

    private sealed class ImmediateDetector(bool hasWork) : IProcessingWorkDetector
    {
        public Task<ProcessingWorkDetectionResult> DetectAsync(ProcessingWorkDetectionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ProcessingWorkDetectorStub.Result(hasWork));
        }
    }

    private sealed class ReentrantCancellationDetector(
        WorkerJobCoordinator workerCoordinator) : IProcessingWorkDetector
    {
        private readonly TaskCompletionSource<ProcessingWorkDetectionResult> _decision =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CallbackEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal WorkerJobAdmissionResult? LookupAdmission { get; private set; }

        public Task<ProcessingWorkDetectionResult> DetectAsync(ProcessingWorkDetectionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.Register(() =>
            {
                LookupAdmission = workerCoordinator.TryAdmit(LookupDispatch());
                _decision.TrySetCanceled(cancellationToken);
                CallbackEntered.TrySetResult();
                throw new InvalidOperationException("preflight-cancellation-callback");
            });
            Entered.TrySetResult();
            return _decision.Task;
        }
    }

    private sealed class TrackingCancellationFactory : IProcessingRunCancellationFactory
    {
        internal int CreateCalls { get; private set; }

        public IProcessingRunCancellation Create(ProcessingRunRequest request, CancellationToken linkedToken)
        {
            CreateCalls++;
            throw new AssertFailedException("detector-only outcomes must not create a run cancellation owner");
        }
    }

    private sealed class ThrowingCancellationFactory : IProcessingRunCancellationFactory
    {
        internal int CreateCalls { get; private set; }

        public IProcessingRunCancellation Create(
            ProcessingRunRequest request,
            CancellationToken linkedToken)
        {
            CreateCalls++;
            throw new InvalidOperationException("cancellation-factory-startup");
        }
    }

    private sealed class RegistrationAndDisposalFaultCancellationFactory : IProcessingRunCancellationFactory
    {
        internal RegistrationAndDisposalFaultCancellation Cancellation { get; } = new();

        public IProcessingRunCancellation Create(
            ProcessingRunRequest request,
            CancellationToken linkedToken)
        {
            _ = request;
            _ = linkedToken;
            return Cancellation;
        }
    }

    private sealed class RegistrationAndDisposalFaultCancellation : IProcessingRunCancellation
    {
        internal int DisposeCalls { get; private set; }
        public CancellationToken Token =>
            throw new InvalidOperationException("scheduled-registration-startup");

        public void Cancel()
        {
        }

        public void Dispose()
        {
            DisposeCalls++;
            throw new ApplicationException("cancellation-disposal-cleanup");
        }
    }

    private sealed class AdmissionObserver : IProcessingRunCoordinatorObserver
    {
        internal bool AllowAdmissionAttempt { get; init; }
        internal int AdmissionCalls { get; private set; }

        public ValueTask BeforeAdmissionGateAsync(ProcessingRunAdmissionAttempt attempt)
        {
            if (attempt == ProcessingRunAdmissionAttempt.Stop)
            {
                return ValueTask.CompletedTask;
            }

            AdmissionCalls++;
            if (AllowAdmissionAttempt)
            {
                return ValueTask.CompletedTask;
            }
            throw new AssertFailedException("detector-only outcomes must not reach admission");
        }
    }

    private sealed class BackendBoundary
    {
        internal int ResolutionCalls { get; private set; }

        internal IChildProcessingRunBackend Resolve()
        {
            ResolutionCalls++;
            throw new AssertFailedException("detector-only outcomes must not resolve the child backend");
        }
    }

    private sealed class RecordingLogger : ILogger<ProcessingRunCoordinator>
    {
        internal List<string> Messages { get; } = [];
        internal int InformationCount { get; private set; }
        internal int ErrorCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, null);
            Messages.Add(message);
            if (logLevel == LogLevel.Information)
            {
                InformationCount++;
            }
            else if (logLevel >= LogLevel.Error)
            {
                ErrorCount++;
            }
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
