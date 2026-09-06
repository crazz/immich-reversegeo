using ImmichReverseGeo.Core.Models;
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
    public async Task ScheduledLocalBusy_RejectsBeforeAnotherGateRequestOrStateLifecycle()
    {
        var gate = new SignalGate();
        await using var fixture = ScheduledCoordinatorFixture.Create(gate);

        Task<ScheduledTriggerResult> admitted = fixture.TriggerScheduledAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            ProcessingRunRequest request = fixture.Coordinator.ActiveRequest
                ?? throw new AssertFailedException("admitted scheduled request was not published before detection");

            Assert.IsTrue(fixture.State.IsRunning, "pending is published before detection");
            Assert.IsTrue(fixture.Reporter.IsArmed(request), "the exact admitted request is armed before detection");

            ScheduledTriggerResult rejected = await fixture.TriggerScheduledAsync().WaitAsync(Bound);

            Assert.AreEqual(ScheduledTriggerResult.RejectedAlreadyRunning, rejected, "local busy result");
            Assert.AreEqual(1, gate.CallCount, "local busy does not invoke a second detector");
            Assert.AreSame(request, fixture.Coordinator.ActiveRequest, "local busy does not replace the admitted request");
            CollectionAssert.AreEqual(
                new[] { "Scheduled run skipped because a processing pass is already in progress." },
                LogMessages(fixture.State),
                "local busy uses the established control-plane log without a new state lifecycle");
        }
        finally
        {
            gate.Decide(false);
            await admitted.WaitAsync(Bound);
        }

        Assert.AreEqual(0, fixture.Launcher.CallCount, "empty admission does not dispatch a child");
        AssertNoChildBoundary(fixture, "local-busy-empty-admission");
    }

    [TestMethod]
    public async Task ScheduledNoWork_CompletesLocallyWithoutBackendOrFinalizationReceipt()
    {
        var gate = new SignalGate();
        await using var fixture = ScheduledCoordinatorFixture.Create(gate);

        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync();
        await gate.Entered.Task.WaitAsync(Bound);
        ProcessingRunRequest request = fixture.Coordinator.ActiveRequest
            ?? throw new AssertFailedException("empty request was not active during detection");
        Assert.IsTrue(fixture.Reporter.IsArmed(request), "empty request is armed before its gate decision");
        gate.Decide(false);

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(Bound));
        Assert.AreEqual(1, gate.CallCount, "one detector invocation per admitted occurrence");
        Assert.AreEqual(0, fixture.Launcher.CallCount, "no child launch for local no-work");
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount, "no backend, launcher, executor, or geodata resolution for local no-work");
        Assert.IsNull(fixture.Reporter.GetFinalizationReceipt(request), "local no-work creates no ProcessingRunResult receipt");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "matching local handle is released");
        Assert.IsFalse(fixture.State.IsRunning, "local no-work reaches idle");
        Assert.AreEqual(0L, fixture.State.TotalUnprocessed, "local no-work projects zero eligibility");
        Assert.AreEqual(0, fixture.State.ProcessedThisRun, "local no-work resets processed count");
        Assert.AreEqual(0, fixture.State.SkippedThisRun, "local no-work resets skipped count");
        Assert.AreEqual(0, fixture.State.ErrorsThisRun, "local no-work resets error count");
        Assert.IsNull(fixture.State.LastError, "local no-work clears last error");
        Assert.IsNotNull(fixture.State.LastRunStarted, "local no-work records a start timestamp");
        Assert.IsNotNull(fixture.State.LastRunCompleted, "local no-work records a completion timestamp");
        CollectionAssert.AreEqual(
            new[]
            {
                "Run started — nothing to process, all assets already have location data.",
                "Run complete. Processed=0 Skipped=0 Errors=0"
            },
            LogMessages(fixture.State),
            "local no-work presentation order");
        AssertNoChildBoundary(fixture, "local-no-work");
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
    public async Task ScheduledCaller_CancellationWaitsForBlockedGateLocalCleanupBeforeReleasingAdmission()
    {
        var gate = new SignalGate();
        await using var fixture = ScheduledCoordinatorFixture.Create(gate);
        using var callerCancellation = new CancellationTokenSource();

        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync(callerCancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            ProcessingRunRequest request = fixture.Coordinator.ActiveRequest
                ?? throw new AssertFailedException("blocked gate request was not active");

            callerCancellation.Cancel();
            await Task.Yield();
            Assert.IsFalse(scheduled.IsCompleted, "caller cancellation cannot release admission while the gate remains blocked");
            Assert.AreSame(request, fixture.Coordinator.ActiveRequest, "blocked gate retains its exact admission handle");
        }
        finally
        {
            gate.Decide(false);
            try
            {
                await scheduled.WaitAsync(Bound);
            }
            catch (OperationCanceledException)
            {
                // The public scheduled trigger may preserve caller cancellation after it has awaited cleanup.
            }
        }

        Assert.IsNull(fixture.Coordinator.ActiveRequest, "caller cancellation releases only after local cleanup");
        Assert.IsFalse(fixture.State.IsRunning, "caller cancellation leaves the local state idle");
        Assert.IsNull(fixture.State.LastError, "matching detector cancellation does not project an error");
        CollectionAssert.Contains(LogMessages(fixture.State), "Run cancelled.", "matching detector cancellation presentation");
        Assert.AreEqual(0, fixture.Launcher.CallCount, "blocked cancellation never dispatches a child");
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount, "blocked cancellation resolves no forbidden graph");
        AssertNoChildBoundary(fixture, "blocked-caller-cancellation");
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
        ProcessingRunRequest request = fixture.Coordinator.ActiveRequest
            ?? throw new AssertFailedException("foreign cancellation request was not active");
        Assert.IsTrue(fixture.Reporter.IsArmed(request), "foreign cancellation request is armed before its gate failure");
        gate.Fail(new OperationCanceledException("db-password=not-for-ui", foreignCancellation.Token));

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(Bound));
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "foreign cancellation releases the matching local handle");
        Assert.IsFalse(fixture.State.IsRunning, "foreign cancellation returns the state to idle");
        Assert.AreEqual("Fatal: Scheduled work detection failed.", fixture.State.LastError, "foreign cancellation uses bounded safe detail");
        Assert.IsFalse(LogMessages(fixture.State).Any(line => line.Contains("db-password", StringComparison.Ordinal)), "foreign cancellation detail never reaches the UI log");
        Assert.IsNull(fixture.Reporter.GetFinalizationReceipt(request), "foreign cancellation creates no worker finalization receipt");
        Assert.AreEqual(0, fixture.Launcher.CallCount, "foreign cancellation does not launch a child");
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount, "foreign cancellation resolves no forbidden graph");
        AssertNoChildBoundary(fixture, "foreign-cancellation");
    }

    [TestMethod]
    public async Task ScheduledGate_ActiveCancellationClassifiesAForeignTokenCancellationAsCancelled()
    {
        using var foreignCancellation = new CancellationTokenSource();
        foreignCancellation.Cancel();
        var gate = new SignalGate();
        await using var fixture = ScheduledCoordinatorFixture.Create(gate);
        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync();
        ScheduledTriggerResult result = default;
        ProcessingRunRequest? request = null;

        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            request = fixture.Coordinator.ActiveRequest
                ?? throw new AssertFailedException("active cancellation request was not published before detection");
            Assert.IsTrue(fixture.Reporter.IsArmed(request), "active cancellation request is armed before the gate fault");
            Assert.IsTrue(fixture.Coordinator.CancelActiveRun(), "active cancellation claims the admitted scheduled handle");
        }
        finally
        {
            gate.Fail(new OperationCanceledException("foreign-token", foreignCancellation.Token));
            result = await scheduled.WaitAsync(Bound);
        }

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, result, "active cancellation closes the accepted scheduled occurrence");
        Assert.IsNotNull(request, "the exact admitted request was captured");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "active cancellation releases the matching handle");
        Assert.IsFalse(fixture.State.IsRunning, "active cancellation returns state to idle");
        Assert.IsNull(fixture.State.LastError, "active cancellation adds no detector failure error");
        CollectionAssert.AreEqual(
            new[]
            {
                "Run cancelled.",
                "Run complete. Processed=0 Skipped=0 Errors=0"
            },
            LogMessages(fixture.State),
            "active cancellation uses the pre-eligibility cancellation presentation");
        Assert.IsNull(fixture.Reporter.GetFinalizationReceipt(request!), "active cancellation creates no worker finalization receipt");
        Assert.AreEqual(0, fixture.Launcher.CallCount, "active cancellation does not launch a child");
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount, "active cancellation resolves no forbidden graph");
        AssertNoChildBoundary(fixture, "active-cancellation");
    }

    [TestMethod]
    public async Task ScheduledGate_UnexpectedFailureIsSafelyFailedLocallyWithoutDispatching()
    {
        var gate = new SignalGate();
        await using var fixture = ScheduledCoordinatorFixture.Create(gate);

        Task<ScheduledTriggerResult> scheduled = fixture.TriggerScheduledAsync();
        await gate.Entered.Task.WaitAsync(Bound);
        ProcessingRunRequest request = fixture.Coordinator.ActiveRequest
            ?? throw new AssertFailedException("unexpected failure request was not active");
        Assert.IsTrue(fixture.Reporter.IsArmed(request), "unexpected failure request is armed before its gate failure");
        gate.Fail(new InvalidOperationException("database connection password=not-for-ui"));

        Assert.AreEqual(ScheduledTriggerResult.AcceptedAfterTerminal, await scheduled.WaitAsync(Bound));
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "unexpected failure releases the matching local handle");
        Assert.IsFalse(fixture.State.IsRunning, "unexpected failure returns the state to idle");
        Assert.AreEqual("Fatal: Scheduled work detection failed.", fixture.State.LastError, "unexpected failure uses the same bounded safe detail");
        Assert.IsFalse(LogMessages(fixture.State).Any(line => line.Contains("password", StringComparison.Ordinal)), "unexpected failure detail never reaches the UI log");
        Assert.IsNull(fixture.Reporter.GetFinalizationReceipt(request), "unexpected failure creates no worker finalization receipt");
        Assert.AreEqual(0, fixture.Launcher.CallCount, "unexpected failure does not launch a child");
        Assert.AreEqual(0, fixture.ForbiddenResolutionCount, "unexpected failure resolves no forbidden graph");
        AssertNoChildBoundary(fixture, "unexpected-detector-failure");
    }

    private static void AssertNoChildBoundary(ScheduledCoordinatorFixture fixture, string scenario)
    {
        Assert.AreEqual(0, fixture.ChildScopeCreationAttempts, scenario + "-no-child-scope");
        Assert.AreEqual(0, fixture.ChildBackendResolutionAttempts, scenario + "-no-child-backend-resolution");
        Assert.AreEqual(0, fixture.ChildScopeDisposeAttempts, scenario + "-no-child-scope-disposal");
    }

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
        internal ProcessingStateEventReporter Reporter { get; }
        internal ProcessingState State { get; }
        internal IScheduledRunTrigger Trigger { get; }

        internal static ScheduledCoordinatorFixture Create(SignalGate gate, params ProcessFixturePlan[] plans)
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
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingRunCoordinator>.Instance);
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
            ProcessingRunRequest request,
            IWorkerProtocolEventSink eventSink,
            ChildWorkerLauncherOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            ArgumentNullException.ThrowIfNull(request);
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
                var forwardingSink = new ForwardingEventSink(eventSink, lease.Sink);
                ChildWorkerSession session = await lease.LaunchAsync(plan.Scenario, forwardingSink, plan.Capture, plan.Options);
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

        private sealed class ForwardingEventSink(IWorkerProtocolEventSink inner, IWorkerProtocolEventSink recording) : IWorkerProtocolEventSink
        {
            public async ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
            {
                await inner.AcceptAsync(@event, cancellationToken);
                await recording.AcceptAsync(@event, cancellationToken);
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
