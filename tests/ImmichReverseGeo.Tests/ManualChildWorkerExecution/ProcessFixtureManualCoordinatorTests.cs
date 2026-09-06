using System.Collections.Concurrent;
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

namespace ImmichReverseGeo.Tests.ManualChildWorkerExecution;

[TestClass]
[TestCategory("Change34")]
[DoNotParallelize]
public sealed class ProcessFixtureManualCoordinatorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public static IEnumerable<object[]> TerminalModes()
    {
        yield return ["success", ProcessingRunOutcome.Completed, true, Array.Empty<string>()];
        yield return ["no-work", ProcessingRunOutcome.Completed, true, Array.Empty<string>()];
        yield return ["pre-ready-crash", ProcessingRunOutcome.Failed, false, new[] { "--exit-code", "42" }];
        yield return ["post-ready-crash", ProcessingRunOutcome.Failed, true, new[] { "--exit-code", "42" }];
        yield return ["malformed", ProcessingRunOutcome.Failed, true, new[] { "--malformed-kind", "json" }];
        yield return ["oversize", ProcessingRunOutcome.Failed, true, Array.Empty<string>()];
        yield return ["unknown", ProcessingRunOutcome.Failed, true, new[] { "--unknown-kind", "type" }];
        yield return ["invalid-sequence", ProcessingRunOutcome.Failed, true, new[] { "--sequence-fault", "replay" }];
        yield return ["terminal-mismatch", ProcessingRunOutcome.Completed, true, new[] { "--terminal", "completed", "--exit-code", "3" }];
        yield return ["terminal-mismatch", ProcessingRunOutcome.Failed, true, new[] { "--terminal", "failed", "--exit-code", "3" }];
        yield return ["stderr-flood", ProcessingRunOutcome.Completed, true, new[] { "--stderr-bytes", "262145" }];
        yield return ["raw-exit", ProcessingRunOutcome.Failed, false, new[] { "--exit-code", "3" }];
        yield return ["raw-exit", ProcessingRunOutcome.Failed, false, new[] { "--exit-code", "42" }];
    }

    [TestMethod]
    [DynamicData(nameof(TerminalModes))]
    public async Task ManualChild_RealFixtureTerminalModeFinalizesOnceReleasesAndRetriggers(
        string scenario,
        ProcessingRunOutcome expectedOutcome,
        bool capturesExecute,
        string[] options)
    {
        await using var fixture = ProcessFixtureHost.Create(
            new ProcessFixturePlan(scenario, capturesExecute, options),
            ProcessFixturePlan.NoWork);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound), $"{scenario}-admitted");
        await fixture.Launcher.Entered.Task.WaitAsync(Bound);

        ProcessingRunRequest first = fixture.Launcher.Requests.Single();
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        WorkerProcessFixtureLease firstLease = fixture.Launcher.Leases.Single();
        await firstLease.CompleteAsync().WaitAsync(Bound);

        ProcessingRunFinalizationReceipt firstReceipt = fixture.Reporter.GetFinalizationReceipt(first)
            ?? throw new AssertFailedException($"{scenario}-missing-finalization-receipt");
        Assert.AreEqual(expectedOutcome, firstReceipt.Result.Outcome, $"{scenario}-terminal-outcome");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, $"{scenario}-matching-handle-released");
        Assert.IsFalse(fixture.State.IsRunning, $"{scenario}-state-idle");
        Assert.IsNull(fixture.State.CurrentActivity, $"{scenario}-activity-cleaned");
        Assert.AreEqual(1, fixture.Launcher.CallCount, $"{scenario}-one-launch");
        Assert.AreEqual(0, fixture.UnselectedResolutionCount, $"{scenario}-selected-child-only");
        if (capturesExecute)
        {
            firstLease.AssertExactCapture();
        }
        else
        {
            Assert.AreEqual(0, firstLease.WrittenInput.Length, $"{scenario}-no-execute-before-readiness");
        }

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound), $"{scenario}-retrigger-admitted");
        await fixture.Launcher.WaitForLaunchCountAsync(2).WaitAsync(Bound);
        ProcessingRunRequest second = fixture.Launcher.Requests.Last();
        Assert.AreNotEqual(Guid.Empty, second.RunId, $"{scenario}-second-id-nonempty");
        Assert.AreNotEqual(first.RunId, second.RunId, $"{scenario}-second-id-different");
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        Assert.AreEqual(2, fixture.Launcher.CallCount, $"{scenario}-one-launch-per-admitted-run");
        Assert.AreEqual(0, fixture.UnselectedResolutionCount, $"{scenario}-retrigger-selected-child-only");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, $"{scenario}-retrigger-matching-release");
    }

    [TestMethod]
    public async Task ManualChild_RealFixtureCooperativeCancelUsesOneSessionAndRetriggersAfterDrainage()
    {
        await using var fixture = ProcessFixtureHost.Create(
            new ProcessFixturePlan("cooperative-cancel", true, Array.Empty<string>()),
            ProcessFixturePlan.NoWork);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        await fixture.Launcher.Entered.Task.WaitAsync(Bound);
        WorkerProcessFixtureLease firstLease = fixture.Launcher.Leases.Single();
        ProcessingRunRequest first = fixture.Launcher.Requests.Single();
        await firstLease.Sink.WaitForAsync(@event => @event.Payload is LogEmittedPayload log
            && log.Message == $"fixture:cooperative-cancel:{first.RunId:D}").WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunAdmissionResult.AlreadyRunning,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        Assert.AreEqual(1, fixture.Launcher.CallCount, "A rejected duplicate must not launch another child.");
        Assert.AreEqual(0, fixture.UnselectedResolutionCount, "A rejected duplicate must not resolve an unselected service.");

        Task stop = fixture.Coordinator.StopActiveRun()
            ?? throw new AssertFailedException("The cooperative child must expose an active Stop operation.");
        await stop.WaitAsync(Bound);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        await firstLease.CompleteAsync().WaitAsync(Bound);

        ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(first)
            ?? throw new AssertFailedException("The cooperative child must commit one finalization receipt.");
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, receipt.Result.Outcome);
        Assert.AreEqual(1, fixture.Launcher.CallCount);
        Assert.AreEqual(0, firstLease.TreeKillCalls, "A cooperative terminal must not require forced termination.");
        Assert.AreEqual(0, fixture.UnselectedResolutionCount);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        await fixture.Launcher.WaitForLaunchCountAsync(2).WaitAsync(Bound);
        Assert.AreNotEqual(first.RunId, fixture.Launcher.Requests.Last().RunId);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        Assert.AreEqual(0, fixture.UnselectedResolutionCount);
    }

    [TestMethod]
    public async Task ManualChild_RealFixtureUnresponsiveStopUsesFakeGraceThenOneForcedKill()
    {
        var clock = new ProcessFixtureTimeProvider();
        await using var fixture = ProcessFixtureHost.Create(
            new ProcessFixturePlan("unresponsive", true, Array.Empty<string>()),
            timeProvider: clock);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound));
        await fixture.Launcher.Entered.Task.WaitAsync(Bound);
        WorkerProcessFixtureLease lease = fixture.Launcher.Leases.Single();
        ProcessingRunRequest request = fixture.Launcher.Requests.Single();
        await lease.Sink.WaitForAsync(@event => @event.Payload is LogEmittedPayload log
            && log.Message == $"fixture:unresponsive:{request.RunId:D}").WaitAsync(Bound);

        int timerGeneration = clock.CreateCalls;
        Task stop = fixture.Coordinator.StopActiveRun()
            ?? throw new AssertFailedException("The unresponsive child must expose an active Stop operation.");
        await clock.WaitForTimerCreatedAsync(timerGeneration + 1).WaitAsync(Bound);
        clock.Advance(TimeSpan.FromSeconds(10));
        await stop.WaitAsync(Bound);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        await lease.CompleteAsync().WaitAsync(Bound);

        ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)
            ?? throw new AssertFailedException("The forced child termination must commit a finalization receipt.");
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, receipt.Result.Outcome);
        Assert.AreEqual(1, lease.TreeKillCalls, "The shared grace deadline must issue one tree kill.");
        Assert.AreEqual(0, fixture.UnselectedResolutionCount);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsFalse(fixture.State.IsRunning);
    }

    private sealed record ProcessFixturePlan(string Scenario, bool Capture, string[] Options)
    {
        internal static ProcessFixturePlan NoWork { get; } = new("no-work", true, Array.Empty<string>());
    }

    private sealed class ProcessFixtureHost : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        private ProcessFixtureHost(
            string root,
            ServiceProvider provider,
            ProcessFixtureLauncher launcher,
            UnselectedResolutionGuard unselectedResolutionGuard)
        {
            _root = root;
            _provider = provider;
            Launcher = launcher;
            UnselectedResolutionGuard = unselectedResolutionGuard;
            Coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
            Reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
            State = provider.GetRequiredService<ProcessingState>();
        }

        internal ProcessFixtureLauncher Launcher { get; }
        internal UnselectedResolutionGuard UnselectedResolutionGuard { get; }
        internal int UnselectedResolutionCount => UnselectedResolutionGuard.Count;
        internal ProcessingRunCoordinator Coordinator { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal ProcessingState State { get; }

        internal static ProcessFixtureHost Create(params ProcessFixturePlan[] plans)
        {
            return Create(plans, TimeProvider.System);
        }

        internal static ProcessFixtureHost Create(ProcessFixturePlan first, TimeProvider timeProvider)
        {
            return Create([first], timeProvider);
        }

        private static ProcessFixtureHost Create(ProcessFixturePlan[] plans, TimeProvider timeProvider)
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change34-process", Guid.NewGuid().ToString("N"));
            var services = new ServiceCollection();
            var launcher = new ProcessFixtureLauncher(plans);
            var unselectedResolutionGuard = new UnselectedResolutionGuard();
            try
            {
                services.AddWebComposition(ApplicationCompositionContext.Create(
                    CompositionEnvironment.Development,
                    root,
                    Path.Combine(root, "data"),
                    Path.Combine(root, "config")),
                    ProcessingBackendKind.ChildWorker);
                services.RemoveAll<IWorkerCommandInvocationBuilder>();
                services.AddSingleton<IWorkerCommandInvocationBuilder, FixtureInvocationBuilder>();
                services.RemoveAll<IChildWorkerLauncher>();
                services.AddSingleton<IChildWorkerLauncher>(launcher);
                services.RemoveAll<IProcessingRunExecutor>();
                services.AddSingleton<IProcessingRunExecutor>(_ =>
                    unselectedResolutionGuard.Reject<IProcessingRunExecutor>(nameof(IProcessingRunExecutor)));
                services.RemoveAllKeyed<IProcessingRunBackend>(ProcessingBackendKind.InProcess);
                services.AddKeyedScoped<IProcessingRunBackend>(
                    ProcessingBackendKind.InProcess,
                    (_, _) => unselectedResolutionGuard.Reject<IProcessingRunBackend>(
                        nameof(ProcessingBackendKind.InProcess)));
                services.RemoveAll<AdministrativeAreaResolverService>();
                services.AddSingleton<AdministrativeAreaResolverService>(_ =>
                    unselectedResolutionGuard.Reject<AdministrativeAreaResolverService>(
                        nameof(AdministrativeAreaResolverService)));
                services.RemoveAll<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>();
                services.AddSingleton<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>(_ =>
                    unselectedResolutionGuard.Reject<ImmichReverseGeo.Overture.Services.OvertureDivisionsService>(
                        nameof(ImmichReverseGeo.Overture.Services.OvertureDivisionsService)));
                services.RemoveAll<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>();
                services.AddSingleton<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>(_ =>
                    unselectedResolutionGuard.Reject<ImmichReverseGeo.Gadm.Services.GadmDivisionsService>(
                        nameof(ImmichReverseGeo.Gadm.Services.GadmDivisionsService)));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
                return new ProcessFixtureHost(
                    root,
                    services.BuildServiceProvider(validateScopes: true),
                    launcher,
                    unselectedResolutionGuard);
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
                throw new AggregateException("Manual child process fixture cleanup failed.", failures);
            }
        }
    }

    private sealed class ProcessFixtureLauncher(IEnumerable<ProcessFixturePlan> plans) : IChildWorkerLauncher, IAsyncDisposable
    {
        private readonly Queue<ProcessFixturePlan> _plans = new(plans);
        private readonly List<WorkerProcessFixtureLease> _leases = [];
        private readonly List<ProcessingRunRequest> _requests = [];
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _launchGate = new();
        private TaskCompletionSource<int> _launchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;
        private int _completedLaunchCount;
        private Exception? _launchFailure;

        internal TaskCompletionSource Entered => _entered;
        internal IReadOnlyList<WorkerProcessFixtureLease> Leases => _leases;
        internal IReadOnlyList<ProcessingRunRequest> Requests => _requests;
        internal int CallCount
        {
            get
            {
                lock (_launchGate)
                {
                    return _callCount;
                }
            }
        }

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
            _requests.Add(request);
            lock (_launchGate)
            {
                _callCount++;
            }
            try
            {
                var forwardingSink = new ForwardingEventSink(eventSink, lease.Sink);
                ChildWorkerSession session = await lease.LaunchAsync(plan.Scenario, forwardingSink, plan.Capture, plan.Options);
                _entered.TrySetResult();
                SignalLaunchCompleted();
                return new ChildWorkerLaunchResult.Started(session);
            }
            catch (Exception exception)
            {
                _entered.TrySetResult();
                SignalLaunchFailed(exception);
                throw;
            }
        }

        internal async Task WaitForLaunchCountAsync(int expectedCount)
        {
            while (true)
            {
                Task<int> completed;
                lock (_launchGate)
                {
                    if (_launchFailure is not null)
                    {
                        throw new InvalidOperationException("A fixture child launch failed.", _launchFailure);
                    }

                    if (_completedLaunchCount >= expectedCount)
                    {
                        return;
                    }

                    completed = _launchCompleted.Task;
                }

                await completed.ConfigureAwait(false);
            }
        }

        private void SignalLaunchCompleted()
        {
            TaskCompletionSource<int> completed;
            int completedCount;
            lock (_launchGate)
            {
                completedCount = ++_completedLaunchCount;
                completed = _launchCompleted;
                _launchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            completed.TrySetResult(completedCount);
        }

        private void SignalLaunchFailed(Exception exception)
        {
            TaskCompletionSource<int> completed;
            int completedCount;
            lock (_launchGate)
            {
                _launchFailure = exception;
                completedCount = _completedLaunchCount;
                completed = _launchCompleted;
                _launchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            completed.TrySetResult(completedCount);
        }

        public async ValueTask DisposeAsync()
        {
            await WorkerProcessFixtureLease.ReapAsync(_leases);
        }

        private sealed class ForwardingEventSink(
            IWorkerProtocolEventSink inner,
            IWorkerProtocolEventSink recording) : IWorkerProtocolEventSink
        {
            public async ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
            {
                await inner.AcceptAsync(@event, cancellationToken);
                await recording.AcceptAsync(@event, cancellationToken);
            }
        }
    }

    private sealed class UnselectedResolutionGuard
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);

        internal T Reject<T>(string service)
        {
            Interlocked.Increment(ref _count);
            throw new AssertFailedException($"Child-worker composition must not resolve {service}.");
        }
    }

    private sealed class FixtureInvocationBuilder : IWorkerCommandInvocationBuilder
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

    private sealed class ProcessFixtureTimeProvider : TimeProvider
    {
        private readonly ConcurrentQueue<ManualTimer> _timers = new();
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        private readonly object _timerGate = new();
        private TaskCompletionSource<int> _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _createCalls;

        internal int CreateCalls
        {
            get
            {
                lock (_timerGate)
                {
                    return _createCalls;
                }
            }
        }

        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _now.Ticks;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime, period);
            _timers.Enqueue(timer);
            lock (_timerGate)
            {
                _createCalls++;
                TaskCompletionSource<int> created = _timerCreated;
                _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
                created.TrySetResult(_createCalls);
            }
            return timer;
        }

        internal async Task WaitForTimerCreatedAsync(int expectedCount)
        {
            while (true)
            {
                Task<int> created;
                lock (_timerGate)
                {
                    if (_createCalls >= expectedCount)
                    {
                        return;
                    }

                    created = _timerCreated.Task;
                }

                await created.ConfigureAwait(false);
            }
        }

        internal void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (ManualTimer timer in _timers)
            {
                timer.FireIfDue(elapsed);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _dueTime;
            private bool _disposed;

            internal ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _callback = callback;
                _state = state;
                _dueTime = dueTime;
                _ = period;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _dueTime = dueTime;
                return !_disposed;
            }

            public void Dispose()
            {
                _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void FireIfDue(TimeSpan elapsed)
            {
                if (!_disposed && elapsed >= _dueTime)
                {
                    _callback(_state);
                    _dueTime = Timeout.InfiniteTimeSpan;
                }
            }
        }
    }
}
