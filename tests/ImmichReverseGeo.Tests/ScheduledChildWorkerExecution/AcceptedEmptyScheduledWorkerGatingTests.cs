using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.ScheduledChildWorkerExecution;

[TestClass]
[TestCategory("Change36")]
[DoNotParallelize]
public sealed class AcceptedEmptyScheduledWorkerGatingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task AcceptedEmptySchedule_FinalizesLocallyWithoutMaterializingTheWorkerGraph()
    {
        ProcessingRunCoordinator? coordinator = null;
        var detector = new CapturingNoWorkDetector(() => coordinator?.ActiveRequest);
        var cancellationFactory = new TrackingCancellationFactory();
        ConnectedWorkerBackendScopeFactory? backendScopeFactory = null;
        var forbiddenDependencies = new ForbiddenDependencySentinels();
        Guid admittedRunId = Guid.Parse("0f3fd626-a086-4a22-a2f4-b1ec8b9d1d45");
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddProcessingControlPlaneServices();
        services.RemoveAll<IScheduledRunWorkGate>();
        services.AddSingleton<IScheduledRunWorkGate>(detector);
        services.RemoveAll<IChildProcessingRunBackend>();
        services.AddScoped<IChildProcessingRunBackend>(
            _ => forbiddenDependencies.Fail<IChildProcessingRunBackend>("child backend"));
        services.RemoveAll<IChildWorkerLauncher>();
        services.AddSingleton<IChildWorkerLauncher>(_ => forbiddenDependencies.Fail<IChildWorkerLauncher>("child launcher"));
        services.AddSingleton<IChildProcessFactory>(_ => forbiddenDependencies.Fail<IChildProcessFactory>("child process factory"));
        services.RemoveAll<IWorkerCommandInvocationBuilder>();
        services.AddSingleton<IWorkerCommandInvocationBuilder>(_ => forbiddenDependencies.Fail<IWorkerCommandInvocationBuilder>("worker command builder"));
        services.RemoveAll<WorkerEventStateBridgeFactory>();
        services.AddSingleton<WorkerEventStateBridgeFactory>(_ => forbiddenDependencies.Fail<WorkerEventStateBridgeFactory>("worker event bridge"));
        services.AddSingleton<IProcessingRunExecutor>(_ => forbiddenDependencies.Fail<IProcessingRunExecutor>("in-process executor"));
        services.AddSingleton<ImmichDbRepository>(_ => forbiddenDependencies.Fail<ImmichDbRepository>("asset batch repository"));
        services.AddSingleton<SkippedAssetsRepository>(_ => forbiddenDependencies.Fail<SkippedAssetsRepository>("skipped-assets repository"));
        services.AddSingleton<ConfigService>(_ => forbiddenDependencies.Fail<ConfigService>("configuration service"));
        services.AddSingleton<AdministrativeAreaResolverService>(_ => forbiddenDependencies.Fail<AdministrativeAreaResolverService>("administrative resolver"));
        services.AddSingleton<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>(_ =>
            forbiddenDependencies.Fail<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>("Overture divisions"));
        services.AddSingleton<ImmichReverseGeo.Overture.Services.OverturePlacesService>(_ =>
            forbiddenDependencies.Fail<ImmichReverseGeo.Overture.Services.OverturePlacesService>("Overture places and airport data"));
        services.AddSingleton<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>(_ =>
            forbiddenDependencies.Fail<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>("GADM divisions"));
        services.RemoveAll<ProcessingRunCoordinator>();
        services.AddSingleton(sp =>
        {
            backendScopeFactory = new ConnectedWorkerBackendScopeFactory(
                sp.GetRequiredService<IServiceScopeFactory>());
            return new ProcessingRunCoordinator(
                sp.GetRequiredService<ProcessingState>(),
                sp.GetRequiredService<ProcessingStateEventReporter>(),
                sp.GetRequiredService<IScheduledRunWorkGate>(),
                backendScopeFactory,
                NullLogger<ProcessingRunCoordinator>.Instance,
                () => admittedRunId,
                cancellationFactory,
                observer: null,
                applicationLifetime: null,
                timeProvider: sp.GetRequiredService<TimeProvider>());
        });
        using var provider = services.BuildServiceProvider(validateScopes: true);
        ProcessingState state = provider.GetRequiredService<ProcessingState>();
        ProcessingStateEventReporter reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
        var transitions = new StateTransitionObserver(state);
        var ownedCoordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
        await using var coordinatorLifetime = ownedCoordinator;
        coordinator = ownedCoordinator;
        var connectedBackendScopeFactory = backendScopeFactory
            ?? throw new AssertFailedException("the coordinator did not receive its connected backend scope boundary");
        using var stopping = new CancellationTokenSource();
        state.OnChanged += transitions.Capture;

        DateTime before = DateTime.UtcNow;
        Task<ScheduledTriggerResult> scheduled = ((IScheduledRunTrigger)ownedCoordinator).TriggerScheduledAsync(stopping.Token);
        try
        {
            await detector.Entered.Task.WaitAsync(Bound);

            ProcessingRunRequest request = ownedCoordinator.ActiveRequest
                ?? throw new AssertFailedException("the admitted scheduled request was not published before detection");
            Assert.AreEqual(admittedRunId, request.RunId, "the detector receives the exact admitted request");
            Assert.AreEqual(ProcessingRunTrigger.Scheduled, request.Trigger, "the admitted request retains scheduled identity");
            Assert.IsTrue(state.IsRunning, "pending state is published before detection");
            Assert.IsNull(state.LastRunStarted, "pending has not fabricated a run start");
            CollectionAssert.AreEqual(Array.Empty<string>(), LogMessages(state), "pending precedes local zero-run logs");
            Assert.IsTrue(reporter.IsArmed(request), "the exact admitted request is armed before detection");
            Assert.AreEqual(1, detector.CallCount, "one detector call is admitted");
            Assert.AreSame(request, detector.Request, "the detector observes the admitted request identity");
            Assert.AreSame(request, cancellationFactory.Request, "the cancellation owner belongs to the admitted request");
            Assert.AreEqual(stopping.Token, cancellationFactory.LinkedToken, "the scheduled stopping token is linked into the admitted owner");
            Assert.AreEqual(cancellationFactory.Token, detector.Token, "the detector receives the coordinator-owned cancellation token");
            Assert.IsFalse(detector.Token.IsCancellationRequested, "the normal empty decision is not cancellation");

            detector.ReleaseNoWork();
            Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(Bound));
            DateTime after = DateTime.UtcNow;
            DateTime started = state.LastRunStarted
                ?? throw new AssertFailedException("local empty finalization did not record a zero start timestamp");
            DateTime completed = state.LastRunCompleted
                ?? throw new AssertFailedException("local empty finalization did not record a zero completion timestamp");

            Assert.AreEqual(1, detector.CallCount, "the normal empty outcome does not retry detection");
            StateSnapshot[] snapshots = transitions.Snapshots;
            int pendingIndex = FindStage(snapshots, 0,
                snapshot => snapshot.IsRunning
                    && snapshot.Started is null
                    && snapshot.Completed is null,
                "pending before local start");
            int zeroStartIndex = FindStage(snapshots, pendingIndex + 1,
                snapshot => snapshot.IsRunning
                    && snapshot.TotalUnprocessed == 0
                    && snapshot.Started == started
                    && snapshot.Completed is null,
                "started zero eligibility");
            int startLogIndex = FindStage(snapshots, zeroStartIndex + 1,
                snapshot => snapshot.Logs.SequenceEqual(
                    ["Run started — nothing to process, all assets already have location data."]),
                "nothing-to-process log");
            int completedIndex = FindStage(snapshots, startLogIndex + 1,
                snapshot => !snapshot.IsRunning
                    && snapshot.Started == started
                    && snapshot.Completed == completed,
                "terminal idle completion");
            _ = FindStage(snapshots, completedIndex + 1,
                snapshot => snapshot.Logs.SequenceEqual(
                    [
                        "Run started — nothing to process, all assets already have location data.",
                        "Run complete. Processed=0 Skipped=0 Errors=0"
                    ]),
                "zero summary log");

            Assert.AreEqual(0, connectedBackendScopeFactory.ScopeCreationAttempts,
                "the child backend scope is never created after a normal no-work decision");
            Assert.AreEqual(0, connectedBackendScopeFactory.BackendResolutionAttempts,
                "the child backend is never resolved");
            Assert.AreEqual(0, connectedBackendScopeFactory.ScopeDisposeAttempts,
                "no backend scope exists to dispose");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("child backend"), "no child backend resolution");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("child launcher"), "no child launcher construction");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("child process factory"), "no child process start boundary construction");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("worker command builder"), "no worker command construction");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("worker event bridge"), "no worker event bridge construction");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("in-process executor"), "no in-process executor construction");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("asset batch repository"), "no asset batch repository access");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("skipped-assets repository"), "no SQLite skipped-assets access");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("configuration service"), "no configuration access");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("administrative resolver"), "no resolver construction");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("Overture divisions"), "no country-index or Overture divisions construction");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("Overture places and airport data"), "no Overture airport data construction");
            Assert.AreEqual(0, forbiddenDependencies.ResolutionCount("GADM divisions"), "no GADM construction");
            Assert.IsNull(reporter.GetFinalizationReceipt(request),
                "local empty finalization creates no worker ProcessingRunResult receipt");

            Assert.AreEqual(0L, state.TotalUnprocessed, "local empty finalization projects zero eligibility");
            Assert.AreEqual(0, state.ProcessedThisRun, "local empty finalization has no worker processed count");
            Assert.AreEqual(0, state.SkippedThisRun, "local empty finalization has no skipped work");
            Assert.AreEqual(0, state.ErrorsThisRun, "local empty finalization has no worker error");
            Assert.IsNull(state.LastError, "local empty finalization has no failure presentation");
            Assert.IsNull(state.CurrentActivity, "local empty finalization leaves no activity");
            Assert.IsTrue(started >= before && started <= after,
                "the recorded zero start is bounded by this admitted operation");
            Assert.IsTrue(completed >= started && completed <= after,
                "the recorded zero completion follows its start within this admitted operation");
            CollectionAssert.AreEqual(
                new[]
                {
                    "Run started — nothing to process, all assets already have location data.",
                    "Run complete. Processed=0 Skipped=0 Errors=0"
                },
                LogMessages(state),
                "the local empty lifecycle reports the established zero-run suffixes in order");

            Assert.IsFalse(reporter.IsArmed(request), "the exact local finalizer releases its armed callback");
            Assert.IsNull(ownedCoordinator.ActiveRequest, "the matching request handle is released after cleanup");
            Assert.IsFalse(state.IsRunning, "the accepted empty schedule reaches terminal idle");
            Assert.AreEqual(1, cancellationFactory.DisposeCount, "the admitted cancellation owner is disposed exactly once");
            Assert.IsFalse(cancellationFactory.CancelCalled, "the accepted empty schedule is not presented as cancelled");
        }
        finally
        {
            state.OnChanged -= transitions.Capture;
            detector.ReleaseNoWork();
            try
            {
                await scheduled.WaitAsync(Bound);
            }
            catch
            {
                // The assertion failure is the useful test failure; this only guarantees cleanup.
            }
        }

        Assert.AreEqual(0, connectedBackendScopeFactory.ScopeCreationAttempts,
            "the post-cleanup lazy boundary still proves no backend, command, launcher, protocol, bridge, executor, or heavy graph materialized");
        Assert.AreEqual(0, connectedBackendScopeFactory.BackendResolutionAttempts,
            "the post-cleanup lazy boundary still proves no child backend resolution");
        Assert.AreEqual(0, forbiddenDependencies.TotalResolutionCount,
            "the post-cleanup registered worker and heavy-service sentinels remain untouched");
    }

    private static string[] LogMessages(ProcessingState state)
    {
        return state.GetRecentLog()
            .Select(line =>
            {
                Assert.IsTrue(line.Length >= 11 && line[0] == '[' && line[9] == ']' && line[10] == ' ',
                    $"Expected timestamped processing log entry, got '{line}'.");
                return line[11..];
            })
            .ToArray();
    }

    private static int FindStage(
        StateSnapshot[] snapshots,
        int startIndex,
        Func<StateSnapshot, bool> predicate,
        string description)
    {
        for (int index = startIndex; index < snapshots.Length; index++)
        {
            if (predicate(snapshots[index]))
            {
                return index;
            }
        }

        throw new AssertFailedException($"Did not observe the required '{description}' state stage in order.");
    }

    private sealed class CapturingNoWorkDetector(Func<ProcessingRunRequest?> activeRequest) : IScheduledRunWorkGate
    {
        private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CallCount { get; private set; }
        internal ProcessingRunRequest? Request { get; private set; }
        internal CancellationToken Token { get; private set; }

        public Task<bool> HasWorkAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            Request = activeRequest()
                ?? throw new AssertFailedException("the coordinator did not publish an active request before scheduled detection");
            Token = cancellationToken;
            Entered.TrySetResult(true);
            return _decision.Task;
        }

        internal void ReleaseNoWork()
        {
            _decision.TrySetResult(false);
        }
    }

    private sealed class TrackingCancellationFactory : IProcessingRunCancellationFactory
    {
        private TrackingCancellation? _created;

        internal ProcessingRunRequest? Request { get; private set; }
        internal CancellationToken LinkedToken { get; private set; }
        internal CancellationToken Token => _created?.Token
            ?? throw new AssertFailedException("the scheduled cancellation owner was not created");
        internal int DisposeCount => _created?.DisposeCount ?? 0;
        internal bool CancelCalled => _created?.CancelCalled ?? false;

        public IProcessingRunCancellation Create(ProcessingRunRequest request, CancellationToken linkedToken)
        {
            Request = request;
            LinkedToken = linkedToken;
            _created = new TrackingCancellation(linkedToken);
            return _created;
        }
    }

    private sealed class TrackingCancellation : IProcessingRunCancellation
    {
        private readonly CancellationTokenSource _source;

        internal TrackingCancellation(CancellationToken linkedToken)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
            Token = _source.Token;
        }

        internal int DisposeCount { get; private set; }
        internal bool CancelCalled { get; private set; }
        public CancellationToken Token { get; }

        public void Cancel()
        {
            CancelCalled = true;
            _source.Cancel();
        }

        public void Dispose()
        {
            DisposeCount++;
            _source.Dispose();
        }
    }

    private sealed class StateTransitionObserver(ProcessingState state)
    {
        private readonly object _gate = new();
        private readonly List<StateSnapshot> _snapshots = [];

        internal StateSnapshot[] Snapshots
        {
            get
            {
                lock (_gate)
                {
                    return [.. _snapshots];
                }
            }
        }

        internal void Capture()
        {
            lock (_gate)
            {
                _snapshots.Add(new StateSnapshot(
                    state.IsRunning,
                    state.TotalUnprocessed,
                    state.LastRunStarted,
                    state.LastRunCompleted,
                    LogMessages(state)));
            }
        }
    }

    private sealed record StateSnapshot(
        bool IsRunning,
        long TotalUnprocessed,
        DateTime? Started,
        DateTime? Completed,
        string[] Logs);

    private sealed class ConnectedWorkerBackendScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        internal int ScopeCreationAttempts { get; private set; }
        internal int BackendResolutionAttempts { get; private set; }
        internal int ScopeDisposeAttempts { get; private set; }

        public IServiceScope CreateScope()
        {
            ScopeCreationAttempts++;
            return new ConnectedScope(this, inner.CreateScope());
        }

        private sealed class ConnectedScope(ConnectedWorkerBackendScopeFactory owner, IServiceScope innerScope) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider { get; } = new ConnectedServiceProvider(owner, innerScope.ServiceProvider);

            public void Dispose()
            {
                owner.ScopeDisposeAttempts++;
                innerScope.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                owner.ScopeDisposeAttempts++;
                if (innerScope is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    innerScope.Dispose();
                }
            }
        }

        private sealed class ConnectedServiceProvider(ConnectedWorkerBackendScopeFactory owner, IServiceProvider innerProvider) : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IChildProcessingRunBackend))
                {
                    owner.BackendResolutionAttempts++;
                }

                return innerProvider.GetService(serviceType);
            }
        }
    }

    private sealed class ForbiddenDependencySentinels
    {
        private readonly Dictionary<string, int> _resolutionCounts = [];

        internal int TotalResolutionCount => _resolutionCounts.Values.Sum();

        internal int ResolutionCount(string service)
        {
            return _resolutionCounts.GetValueOrDefault(service);
        }

        internal T Fail<T>(string service)
        {
            _resolutionCounts[service] = ResolutionCount(service) + 1;
            throw new AssertFailedException($"The accepted-empty path must not resolve {service}.");
        }
    }
}
