using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ScheduledChildWorkerExecution;

[TestClass]
[TestCategory("Change35")]
[DoNotParallelize]
public sealed class CoordinatorScheduledGateTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task ScheduledNoWork_DetectsBeforeIdentityAdmissionOrStateLifecycle()
    {
        var gate = new SignalGate();
        await using var fixture = ScheduledCoordinatorFixture.Create(gate);
        ProcessingStateSnapshot before = Snapshot(fixture.State);

        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync();
        await gate.Entered.Task.WaitAsync(Bound);

        Assert.IsNull(fixture.Coordinator.ActiveRequest, "detection publishes no processing identity");
        Assert.IsNull(fixture.WorkerCoordinator.Snapshot.ActiveOwner, "detection owns no worker admission");
        Assert.AreEqual(before, Snapshot(fixture.State), "detection mutates no ProcessingState observation");
        gate.Decide(false);

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(Bound));
        Assert.AreEqual(1, gate.CallCount);
        Assert.AreEqual(before, Snapshot(fixture.State), "normal no-work leaves ProcessingState unchanged");
        Assert.AreEqual(0, fixture.Launcher.CallCount);
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount);
        AssertNoChildBoundary(fixture, "detector-no-work");
    }

    [TestMethod]
    public async Task ManualRun_BypassesScheduledGate()
    {
        var gate = new SignalGate();
        await using var fixture = ScheduledCoordinatorFixture.Create(gate, ProcessFixturePlan.NoWork);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        await fixture.Launcher.WaitForLaunchCountAsync(1).WaitAsync(Bound);
        await fixture.Launcher.Leases.Single().CompleteAsync().WaitAsync(Bound);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        Assert.AreEqual(0, gate.CallCount, "manual runs do not invoke the scheduled-only detector");
        Assert.AreEqual(1, fixture.Launcher.CallCount, "manual run uses the child backend");
    }

    [TestMethod]
    public async Task ScheduledDetector_MatchingCancellationCreatesNoIdentityAdmissionOrStateLifecycle()
    {
        var gate = new SignalGate();
        await using var fixture = ScheduledCoordinatorFixture.Create(gate);
        using var callerCancellation = new CancellationTokenSource();

        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync(callerCancellation.Token);
        ProcessingStateSnapshot before = Snapshot(fixture.State);
        await gate.Entered.Task.WaitAsync(Bound);
        callerCancellation.Cancel();
        gate.Fail(new OperationCanceledException(callerCancellation.Token));

        OperationCanceledException failure = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => scheduled.WaitAsync(Bound));
        Assert.AreEqual(callerCancellation.Token, failure.CancellationToken);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsNull(fixture.WorkerCoordinator.Snapshot.ActiveOwner);
        Assert.AreEqual(before, Snapshot(fixture.State));
        AssertNoChildBoundary(fixture, "detector-cancellation");
    }

    [TestMethod]
    public async Task ScheduledGate_ForeignCancellationIsSafelyFailedLocallyWithoutLeakingDetailsOrDispatching()
    {
        using var foreignCancellation = new CancellationTokenSource();
        foreignCancellation.Cancel();
        var gate = new SignalGate();
        await using var fixture = ScheduledCoordinatorFixture.Create(gate);

        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync();
        await gate.Entered.Task.WaitAsync(Bound);
        ProcessingStateSnapshot before = Snapshot(fixture.State);
        gate.Fail(new OperationCanceledException("db-password=not-for-ui", foreignCancellation.Token));

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(Bound));
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsNull(fixture.WorkerCoordinator.Snapshot.ActiveOwner);
        Assert.AreEqual(before, Snapshot(fixture.State));
        Assert.AreEqual(0, fixture.Launcher.CallCount, "foreign cancellation does not launch a child");
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount, "foreign cancellation resolves no forbidden graph");
        AssertNoChildBoundary(fixture, "foreign-cancellation");
    }

    [TestMethod]
    public async Task ScheduledGate_UnexpectedFailureIsSafelyFailedLocallyWithoutDispatching()
    {
        var gate = new SignalGate();
        await using var fixture = ScheduledCoordinatorFixture.Create(gate);

        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync();
        await gate.Entered.Task.WaitAsync(Bound);
        ProcessingStateSnapshot before = Snapshot(fixture.State);
        gate.Fail(new InvalidOperationException("database connection password=not-for-ui"));

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(Bound));
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsNull(fixture.WorkerCoordinator.Snapshot.ActiveOwner);
        Assert.AreEqual(before, Snapshot(fixture.State));
        Assert.AreEqual(0, fixture.Launcher.CallCount, "unexpected failure does not launch a child");
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount, "unexpected failure resolves no forbidden graph");
        AssertNoChildBoundary(fixture, "unexpected-detector-failure");
    }

    [TestMethod]
    [TestCategory("Change41")]
    public async Task ScheduledEligibility_HostShutdownWinsDispatchClaimWithoutResolvingAChild()
    {
        var gate = new SignalGate();
        var dispatch = new DispatchClaimGate();
        await using var fixture = ScheduledCoordinatorFixture.CreateWithObserver(
            gate,
            dispatch,
            ProcessFixturePlan.NoWork);
        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync();
        Exception? scheduledFailure = null;
        var childScopeCreationAttempts = -1;

        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            gate.Decide(true);
            await dispatch.Entered.Task.WaitAsync(Bound);

            Task shutdown = fixture.Coordinator.BeginShutdown();
            await dispatch.CancellationObserved.Task.WaitAsync(Bound);

            Assert.AreEqual(
                ProcessingRunAdmissionResult.Stopping,
                await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
                "shutdown closes admission before the child dispatch claim");
            Assert.AreEqual(0, fixture.ChildScopeCreationAttempts, "blocked claim has not created a child scope");

            dispatch.Release.TrySetResult();
            Task childLaunch = fixture.Launcher.WaitForLaunchCountAsync(1);
            Task firstOutcome = await Task.WhenAny(scheduled, childLaunch).WaitAsync(Bound);
            if (ReferenceEquals(firstOutcome, childLaunch))
            {
                await childLaunch.WaitAsync(Bound);
                await fixture.Launcher.Leases.Single().CompleteAsync().WaitAsync(Bound);
            }

            childScopeCreationAttempts = fixture.ChildScopeCreationAttempts;
            try
            {
                await scheduled.WaitAsync(Bound);
            }
            catch (Exception failure)
            {
                scheduledFailure = failure;
            }

            await shutdown.WaitAsync(Bound);
        }
        finally
        {
            dispatch.Release.TrySetResult();
            if (fixture.Launcher.Leases.Count == 1)
            {
                await fixture.Launcher.Leases[0].CompleteAsync().WaitAsync(Bound);
            }
        }

        Assert.AreEqual(0, childScopeCreationAttempts, "shutdown winner creates no child scope");
        Assert.AreEqual(0, fixture.ChildBackendResolutionAttempts, "shutdown winner resolves no child backend");
        Assert.AreEqual(0, fixture.Launcher.CallCount, "shutdown winner starts no child process");
        Assert.IsInstanceOfType<OperationCanceledException>(
            scheduledFailure,
            "the shutdown-fenced scheduled request preserves its cancellation boundary");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "shutdown cleanup releases the exact request");
        Assert.IsFalse(fixture.State.IsRunning, "shutdown cleanup rolls back pending state");
        Assert.IsNull(fixture.State.LastRunCompleted, "host shutdown does not invent a normal terminal");
        Assert.IsNull(fixture.State.LastError, "host shutdown does not invent a failure terminal");
    }

    [TestMethod]
    [TestCategory("Change41")]
    public async Task ScheduledEligibility_CallerCancellationWinsDispatchClaimAndFinalizesLocally()
    {
        var gate = new SignalGate();
        var dispatch = new DispatchClaimGate();
        await using var fixture = ScheduledCoordinatorFixture.CreateWithObserver(gate, dispatch);
        using var callerCancellation = new CancellationTokenSource();
        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync(callerCancellation.Token);

        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            gate.Decide(true);
            await dispatch.Entered.Task.WaitAsync(Bound);
            callerCancellation.Cancel();

            dispatch.Release.TrySetResult();
            var failure = await Assert.ThrowsAsync<OperationCanceledException>(
                () => scheduled.WaitAsync(Bound));
            Assert.AreEqual(callerCancellation.Token, failure.CancellationToken);
        }
        finally
        {
            dispatch.Release.TrySetResult();
        }

        Assert.AreEqual(0, fixture.ChildScopeCreationAttempts, "cancel-before-claim creates no child scope");
        Assert.AreEqual(0, fixture.ChildBackendResolutionAttempts, "cancel-before-claim resolves no child backend");
        Assert.AreEqual(0, fixture.Launcher.CallCount, "cancel-before-claim starts no child process");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "local cancellation releases the exact request");
        Assert.IsFalse(fixture.State.IsRunning, "local cancellation reaches idle");
        Assert.IsNotNull(fixture.State.LastRunCompleted, "local cancellation retains its established terminal");
        Assert.IsNull(fixture.State.LastError, "local cancellation adds no failure");
    }

    private static void AssertNoChildBoundary(ScheduledCoordinatorFixture fixture, string scenario)
    {
        Assert.AreEqual(0, fixture.ChildScopeCreationAttempts, scenario + "-no-child-scope");
        Assert.AreEqual(0, fixture.ChildBackendResolutionAttempts, scenario + "-no-child-backend-resolution");
        Assert.AreEqual(0, fixture.ChildScopeDisposeAttempts, scenario + "-no-child-scope-disposal");
    }

    private static ProcessingStateSnapshot Snapshot(ProcessingState state) => new(
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

    private sealed record ProcessingStateSnapshot(
        bool IsRunning,
        long TotalUnprocessed,
        long Processed,
        long Skipped,
        long Errors,
        string? LastError,
        string? CurrentActivity,
        DateTime? LastRunStarted,
        DateTime? LastRunCompleted,
        string Logs);

    internal sealed record ProcessFixturePlan(string Scenario, bool Capture, string[] Options)
    {
        internal static ProcessFixturePlan NoWork { get; } = new("no-work", true, Array.Empty<string>());
    }

    internal sealed class ScheduledCoordinatorFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        private ScheduledCoordinatorFixture(
            string root,
            ServiceProvider provider,
            ProcessFixtureLauncher launcher,
            ResolutionGuard guard,
            ProcessingRunCoordinator coordinator,
            ConnectedChildBackendScopeFactory childBoundary)
        {
            _root = root;
            _provider = provider;
            Launcher = launcher;
            Guard = guard;
            Coordinator = coordinator;
            WorkerCoordinator = provider.GetRequiredService<WorkerJobCoordinator>();
            ChildBoundary = childBoundary;
            Reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
            State = provider.GetRequiredService<ProcessingState>();
            Trigger = provider.GetRequiredService<IScheduledRunTrigger>();
        }

        internal ProcessFixtureLauncher Launcher { get; }
        internal ResolutionGuard Guard { get; }
        internal ConnectedChildBackendScopeFactory ChildBoundary { get; }
        internal int ForbiddenResolutionCount => Guard.Count;
        internal int ChildScopeCreationAttempts => ChildBoundary.ScopeCreationAttempts;
        internal int ChildBackendResolutionAttempts => ChildBoundary.BackendResolutionAttempts;
        internal int ChildScopeDisposeAttempts => ChildBoundary.ScopeDisposeAttempts;
        internal ProcessingRunCoordinator Coordinator { get; }
        internal WorkerJobCoordinator WorkerCoordinator { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal ProcessingState State { get; }
        internal IScheduledRunTrigger Trigger { get; }

        internal static ScheduledCoordinatorFixture Create(SignalGate gate, params ProcessFixturePlan[] plans)
        {
            return CreateCore(gate, observer: null, plans);
        }

        internal static ScheduledCoordinatorFixture CreateWithObserver(
            SignalGate gate,
            IProcessingRunCoordinatorObserver observer,
            params ProcessFixturePlan[] plans)
        {
            return CreateCore(gate, observer, plans);
        }

        private static ScheduledCoordinatorFixture CreateCore(
            SignalGate gate,
            IProcessingRunCoordinatorObserver? observer,
            ProcessFixturePlan[] plans)
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change35-scheduled", Guid.NewGuid().ToString("N"));
            var services = new ServiceCollection();
            var launcher = new ProcessFixtureLauncher(plans);
            var guard = new ResolutionGuard();
            ConnectedChildBackendScopeFactory? childBoundary = null;
            try
            {
                services.AddWebComposition(ApplicationCompositionContext.Create(
                    CompositionEnvironment.Development,
                    root,
                    Path.Combine(root, "data"),
                    Path.Combine(root, "config")));
                services.RemoveAll<IScheduledRunWorkGate>();
                services.AddSingleton<IScheduledRunWorkGate>(gate);
                services.RemoveAll<IWorkerCommandInvocationBuilder>();
                services.AddSingleton<IWorkerCommandInvocationBuilder, FixtureInvocationBuilder>();
                services.RemoveAll<IChildWorkerLauncher>();
                services.AddSingleton<IChildWorkerLauncher>(launcher);
                services.RemoveAll<AdministrativeAreaResolverService>();
                services.AddSingleton<AdministrativeAreaResolverService>(_ => guard.Reject<AdministrativeAreaResolverService>(nameof(AdministrativeAreaResolverService)));
                services.RemoveAll<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>();
                services.AddSingleton<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>(_ =>
                    guard.Reject<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>("Overture"));
                services.RemoveAll<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>();
                services.AddSingleton<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>(_ =>
                    guard.Reject<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>("Gadm"));
                services.RemoveAll<ProcessingRunCoordinator>();
                services.AddSingleton(sp =>
                {
                    childBoundary = new ConnectedChildBackendScopeFactory(
                        sp.GetRequiredService<IServiceScopeFactory>());
                    return new ProcessingRunCoordinator(
                        sp.GetRequiredService<ProcessingState>(),
                        sp.GetRequiredService<ProcessingStateEventReporter>(),
                        sp.GetRequiredService<IScheduledRunWorkGate>(),
                        childBoundary,
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingRunCoordinator>.Instance,
                        Guid.NewGuid,
                        observer,
                        sp.GetRequiredService<WorkerJobCoordinator>());
                });
                var provider = services.BuildServiceProvider(validateScopes: true);
                var coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
                return new ScheduledCoordinatorFixture(
                    root,
                    provider,
                    launcher,
                    guard,
                    coordinator,
                    childBoundary ?? throw new AssertFailedException("coordinator child scope boundary was not captured"));
            }
            catch
            {
                launcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        internal Task<ScheduledTriggerResult> TriggerScheduledAsync(CancellationToken cancellationToken = default)
        {
            return Trigger.TriggerScheduledAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception> failures = [];
            try
            {
                await Launcher.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                await Coordinator.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                await _provider.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("Scheduled coordinator fixture cleanup failed.", failures);
            }
        }
    }

    internal sealed class ConnectedChildBackendScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        internal int ScopeCreationAttempts { get; private set; }
        internal int BackendResolutionAttempts { get; private set; }
        internal int ScopeDisposeAttempts { get; private set; }

        public IServiceScope CreateScope()
        {
            ScopeCreationAttempts++;
            return new ConnectedScope(this, inner.CreateScope());
        }

        private sealed class ConnectedScope(
            ConnectedChildBackendScopeFactory owner,
            IServiceScope innerScope) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider { get; } =
                new ConnectedServiceProvider(owner, innerScope.ServiceProvider);

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

        private sealed class ConnectedServiceProvider(
            ConnectedChildBackendScopeFactory owner,
            IServiceProvider innerProvider) : IServiceProvider
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

    internal sealed class SignalGate : IScheduledRunWorkGate
    {
        private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CallCount { get; private set; }
        internal CancellationToken Token { get; private set; }

        internal static SignalGate Decided(bool decision)
        {
            var gate = new SignalGate();
            gate.Decide(decision);
            return gate;
        }

        internal static SignalGate Faulted(Exception failure)
        {
            var gate = new SignalGate();
            gate._decision.TrySetException(failure);
            return gate;
        }

        public Task<bool> HasWorkAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            Token = cancellationToken;
            Entered.TrySetResult();
            return _decision.Task;
        }

        internal void Decide(bool hasWork)
        {
            _decision.TrySetResult(hasWork);
        }

        internal void Fail(Exception failure)
        {
            _decision.TrySetException(failure);
        }
    }

    private sealed class DispatchClaimGate : IProcessingRunCoordinatorObserver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask BeforeChildDispatchClaimAsync(ProcessingRunRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }

        public void BeforeRequestCancellation(ProcessingRunRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            CancellationObserved.TrySetResult();
        }
    }

    internal sealed class ProcessFixtureLauncher(IEnumerable<ProcessFixturePlan> plans) : IChildWorkerLauncher, IAsyncDisposable
    {
        private readonly Queue<ProcessFixturePlan> _plans = new(plans);
        private readonly List<WorkerProcessFixtureLease> _leases = [];
        private readonly object _gate = new();
        private TaskCompletionSource<int> _launchesChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completedLaunchCount;
        private Exception? _launchFailure;

        internal IReadOnlyList<WorkerProcessFixtureLease> Leases => _leases;
        internal int CallCount { get; private set; }

        public async ValueTask<ChildWorkerLaunchResult> LaunchAsync(
            WorkerInvocation invocation,
            WorkerJobDispatch dispatch,
            IWorkerJobEventSink eventSink,
            ChildWorkerLauncherOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            var processAssets = Assert.IsInstanceOfType<ProcessAssetsWorkerJobDispatch>(dispatch);
            ProcessingRunRequest request = processAssets.Request.ProcessingRequest;
            ArgumentNullException.ThrowIfNull(eventSink);
            if (_plans.Count == 0)
            {
                throw new InvalidOperationException("The fixture launcher received an unplanned child run.");
            }

            ProcessFixturePlan plan = _plans.Dequeue();
            var lease = new WorkerProcessFixtureLease { Request = request, LauncherOptions = options };
            _leases.Add(lease);
            CallCount++;
            try
            {
                var forwardingSink = new ForwardingEventSink(
                    eventSink,
                    new ProcessAssetsWorkerJobEventSink(request, lease.Sink));
                ChildWorkerSession session = await lease.LaunchAsync(
                    plan.Scenario,
                    dispatch,
                    forwardingSink,
                    invocation.ProtocolVersion,
                    plan.Capture,
                    plan.Options);
                SignalLaunchCompleted();
                return new ChildWorkerLaunchResult.Started(session);
            }
            catch (Exception failure)
            {
                SignalLaunchFailed(failure);
                await WorkerProcessFixtureLease.ReapAsync(_leases);
                throw;
            }
        }

        internal async Task WaitForLaunchCountAsync(int expected)
        {
            while (true)
            {
                Task<int> changed;
                lock (_gate)
                {
                    if (_launchFailure is not null)
                    {
                        throw new InvalidOperationException("A fixture child launch failed.", _launchFailure);
                    }

                    if (_completedLaunchCount >= expected)
                    {
                        return;
                    }

                    changed = _launchesChanged.Task;
                }

                await changed.ConfigureAwait(false);
            }
        }

        private void SignalLaunchCompleted()
        {
            TaskCompletionSource<int> changed;
            int count;
            lock (_gate)
            {
                count = ++_completedLaunchCount;
                changed = _launchesChanged;
                _launchesChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            changed.TrySetResult(count);
        }

        private void SignalLaunchFailed(Exception failure)
        {
            TaskCompletionSource<int> changed;
            int count;
            lock (_gate)
            {
                _launchFailure = failure;
                count = _completedLaunchCount;
                changed = _launchesChanged;
                _launchesChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            changed.TrySetResult(count);
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(WorkerProcessFixtureLease.ReapAsync(_leases));
        }

        private sealed class ForwardingEventSink(IWorkerJobEventSink inner, IWorkerJobEventSink recording) : IWorkerJobEventSink
        {
            public async ValueTask AcceptAsync(WorkerJobOutputMessage message, CancellationToken cancellationToken)
            {
                await inner.AcceptAsync(message, cancellationToken);
                await recording.AcceptAsync(message, cancellationToken);
            }
        }
    }

    internal sealed class ResolutionGuard
    {
        internal int Count { get; private set; }

        internal T Reject<T>(string service)
        {
            Count++;
            throw new AssertFailedException($"Unexpected resolution of {service} for a local scheduled gate path.");
        }
    }

    internal sealed class FixtureInvocationBuilder : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build()
        {
            var facts = new WorkerCommandRuntimeFacts(
                WorkerInvocation.TrustedWebAssemblyIdentity,
                "/fixture/dotnet",
                WorkerTargetObservation.File,
                WorkerInvocation.TrustedWebAssemblyIdentity,
                "/fixture/ImmichReverseGeo.Web.dll",
                WorkerTargetObservation.File,
                "/fixture",
                WorkerTargetObservation.Directory,
                WorkerPathSemantics.Unix);
            return WorkerInvocation.Resolve(facts);
        }
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
}
