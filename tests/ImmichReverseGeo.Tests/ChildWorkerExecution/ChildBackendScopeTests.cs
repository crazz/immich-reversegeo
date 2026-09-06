using System.Collections.Concurrent;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.ChildWorkerExecution;

[TestClass]
[TestCategory("Change38")]
public sealed class ChildBackendScopeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Coordinator_BuildsOnlyTheChildScopedDependencyGraph()
    {
        await using var provider = CreateScopedBackendProvider(out var recorder, out var counters);
        recorder.EnqueueRun(autoRelease: true, autoDispose: true);
        var coordinator = CreateCoordinator(provider, Guid.NewGuid);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync(), "admission");
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        Assert.AreEqual(1, counters.BackendConstructed, "child-adapter-constructed");
        Assert.AreEqual(0, counters.ExecutorConstructed, "executor-graph-not-constructed");
        Assert.AreEqual(0, counters.GeodataConstructed, "geodata-graph-not-constructed");
        Assert.AreEqual(1, counters.ChildCommandConstructed, "command-graph");
        Assert.AreEqual(1, counters.ChildLauncherConstructed, "launcher-graph");
        Assert.AreEqual(1, counters.ChildBridgeConstructed, "bridge-graph");
    }

    [TestMethod]
    public async Task Coordinator_ChildScopeDisposesBeforeExactHandleReleaseAndLaterRunGetsNewScope()
    {
        await using var provider = CreateScopedBackendProvider(out var recorder, out var counters);
        var firstRun = recorder.EnqueueRun(autoRelease: false, autoDispose: false);
        var secondRun = recorder.EnqueueRun(autoRelease: false, autoDispose: true);
        var ids = new Queue<Guid>(new[]
        {
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000002")
        });
        var coordinator = CreateCoordinator(provider, ids.Dequeue);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync(), "first-admission");
        await firstRun.Entered.Task.WaitAsync(TestTimeout);
        firstRun.Release.TrySetResult();
        await firstRun.DisposeStarted.Task.WaitAsync(TestTimeout);

        Assert.IsNotNull(coordinator.ActiveRequest, "handle-remains-owned-until-scope-disposal");
        Assert.IsFalse(coordinator.WaitForActiveRunAsync().IsCompleted, "idle-waits-for-async-scope-disposal");
        Assert.AreEqual(0, firstRun.DisposeCompletedCount, "first-scope-not-yet-disposed");

        firstRun.AllowDispose.TrySetResult();
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);
        Assert.IsNull(coordinator.ActiveRequest, "handle-released-after-scope-disposal");
        Assert.AreEqual(1, firstRun.DisposeCompletedCount, "first-scope-disposed");

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync(), "second-admission");
        await secondRun.Entered.Task.WaitAsync(TestTimeout);
        secondRun.Release.TrySetResult();
        await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

        Assert.AreEqual(2, counters.BackendConstructed, "one-child-adapter-per-admitted-run");
        Assert.AreEqual(2, recorder.BackendInstances.Count, "two-scoped-adapter-instances");
        Assert.AreNotSame(recorder.BackendInstances[0], recorder.BackendInstances[1], "later-run-uses-new-scope");
    }

    [TestMethod]
    public async Task Coordinator_ChildBackendResolutionFailureDisposesCreatedScopeBeforeReturningToIdle()
    {
        var recorder = new ScopeFailureRecorder();
        var services = CreateFailureServices(recorder);
        services.AddScoped<IChildProcessingRunBackend, ScopeResolutionFailureBackend>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var coordinator = CreateCoordinator(provider, Guid.NewGuid);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.TriggerManualAsync().WaitAsync(TestTimeout));

        Assert.AreEqual(1, recorder.ProbeConstructed, "resolution-probe-constructed");
        Assert.AreEqual(1, recorder.ProbeDisposed, "resolution-scope-disposed");
        Assert.IsNull(coordinator.ActiveRequest, "resolution-failure-releases-handle");
    }

    [TestMethod]
    public async Task Coordinator_ChildBackendExecutionFailureDisposesRunScopeBeforeReturningToIdle()
    {
        var recorder = new ScopeFailureRecorder();
        var services = CreateFailureServices(recorder);
        services.AddScoped<IChildProcessingRunBackend, ScopeExecutionFailureBackend>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var coordinator = CreateCoordinator(provider, Guid.NewGuid);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.TriggerManualAsync().WaitAsync(TestTimeout));

        Assert.AreEqual(1, recorder.ProbeConstructed, "execution-probe-constructed");
        Assert.AreEqual(1, recorder.ProbeDisposed, "execution-scope-disposed");
        Assert.IsNull(coordinator.ActiveRequest, "execution-failure-releases-handle");
    }

    private static ServiceProvider CreateScopedBackendProvider(
        out ScopeBackendRecorder recorder,
        out ScopeActivationCounters counters)
    {
        var state = new ProcessingState();
        var reporter = new ProcessingStateEventReporter(state);
        recorder = new ScopeBackendRecorder();
        counters = new ScopeActivationCounters();
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddSingleton(reporter);
        services.AddSingleton(recorder);
        services.AddSingleton(counters);
        services.AddScoped<ScopeGeodataProbe>();
        services.AddScoped<ScopeExecutorGraph>();
        services.AddScoped<ScopeChildCommandProbe>();
        services.AddScoped<ScopeChildLauncherProbe>();
        services.AddScoped<ScopeChildBridgeProbe>();
        services.AddScoped<ScopeChildWorkerGraph>();
        services.AddScoped<IChildProcessingRunBackend, ScopeChildBackend>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static ServiceCollection CreateFailureServices(ScopeFailureRecorder recorder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ProcessingState());
        services.AddSingleton<ProcessingStateEventReporter>();
        services.AddSingleton(recorder);
        services.AddScoped<ScopeFailureProbe>();
        return services;
    }

    private static ProcessingRunCoordinator CreateCoordinator(
        ServiceProvider provider,
        Func<Guid> createRunId)
    {
        return new ProcessingRunCoordinator(
            provider.GetRequiredService<ProcessingState>(),
            provider.GetRequiredService<ProcessingStateEventReporter>(),
            global::ImmichReverseGeo.Tests.AlwaysHasWorkScheduledRunGate.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProcessingRunCoordinator>.Instance,
            createRunId);
    }

    private sealed class ScopeActivationCounters
    {
        private int _backendConstructed;
        private int _executorConstructed;
        private int _geodataConstructed;
        private int _childCommandConstructed;
        private int _childLauncherConstructed;
        private int _childBridgeConstructed;

        public int BackendConstructed => Volatile.Read(ref _backendConstructed);
        public int ExecutorConstructed => Volatile.Read(ref _executorConstructed);
        public int GeodataConstructed => Volatile.Read(ref _geodataConstructed);
        public int ChildCommandConstructed => Volatile.Read(ref _childCommandConstructed);
        public int ChildLauncherConstructed => Volatile.Read(ref _childLauncherConstructed);
        public int ChildBridgeConstructed => Volatile.Read(ref _childBridgeConstructed);

        public void BackendCreated() => Interlocked.Increment(ref _backendConstructed);
        public void ExecutorCreated() => Interlocked.Increment(ref _executorConstructed);
        public void GeodataCreated() => Interlocked.Increment(ref _geodataConstructed);
        public void ChildCommandCreated() => Interlocked.Increment(ref _childCommandConstructed);
        public void ChildLauncherCreated() => Interlocked.Increment(ref _childLauncherConstructed);
        public void ChildBridgeCreated() => Interlocked.Increment(ref _childBridgeConstructed);
    }

    private sealed class ScopeGeodataProbe(ScopeActivationCounters counters)
    {
        private readonly int _ = Mark(counters);
        private static int Mark(ScopeActivationCounters counters)
        {
            counters.GeodataCreated();
            return 0;
        }
    }

    private sealed class ScopeExecutorGraph(ScopeActivationCounters counters, ScopeGeodataProbe geodata)
    {
        private readonly ScopeGeodataProbe _geodata = geodata;
        private readonly int _ = Mark(counters);
        private static int Mark(ScopeActivationCounters counters)
        {
            counters.ExecutorCreated();
            return 0;
        }
    }

    private sealed class ScopeChildCommandProbe(ScopeActivationCounters counters)
    {
        private readonly int _ = Mark(counters);
        private static int Mark(ScopeActivationCounters counters)
        {
            counters.ChildCommandCreated();
            return 0;
        }
    }

    private sealed class ScopeChildLauncherProbe(ScopeActivationCounters counters)
    {
        private readonly int _ = Mark(counters);
        private static int Mark(ScopeActivationCounters counters)
        {
            counters.ChildLauncherCreated();
            return 0;
        }
    }

    private sealed class ScopeChildBridgeProbe(ScopeActivationCounters counters)
    {
        private readonly int _ = Mark(counters);
        private static int Mark(ScopeActivationCounters counters)
        {
            counters.ChildBridgeCreated();
            return 0;
        }
    }

    private sealed class ScopeChildWorkerGraph(
        ScopeChildCommandProbe command,
        ScopeChildLauncherProbe launcher,
        ScopeChildBridgeProbe bridge)
    {
        private readonly object[] _dependencies = [command, launcher, bridge];
    }

    private sealed class ScopeChildBackend : IChildProcessingRunBackend, IAsyncDisposable
    {
        private readonly ScopeBackendRecorder _recorder;
        private ScopeRun? _run;

        public ScopeChildBackend(
            ScopeBackendRecorder recorder,
            ScopeActivationCounters counters,
            ScopeChildWorkerGraph childGraph)
        {
            _recorder = recorder;
            _ = childGraph;
            counters.BackendCreated();
            recorder.BackendCreated(this);
        }

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            _run = _recorder.TakeRun();
            _run.Entered.TrySetResult();
            await _run.Release.Task.ConfigureAwait(false);
            var session = await reporter.OpenRunAsync(request, Now, CancellationToken.None).ConfigureAwait(false);
            await session.DetermineEligibilityAsync(0, CancellationToken.None).ConfigureAwait(false);
            var result = new ProcessingRunResult(request, Now, Now, 0, 0, 0, 0, ProcessingRunOutcome.Completed, null);
            await session.FinishAsync(result).ConfigureAwait(false);
            _ = cancellationToken;
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (_run is null)
            {
                return;
            }

            _run.DisposeStarted.TrySetResult();
            await _run.AllowDispose.Task.ConfigureAwait(false);
            _run.MarkDisposed();
        }
    }

    private sealed class ScopeBackendRecorder
    {
        private readonly ConcurrentQueue<ScopeRun> _plannedRuns = new();
        private readonly List<ScopeChildBackend> _backendInstances = [];
        private readonly object _backendGate = new();

        public IReadOnlyList<ScopeChildBackend> BackendInstances
        {
            get
            {
                lock (_backendGate)
                {
                    return _backendInstances.ToArray();
                }
            }
        }

        public ScopeRun EnqueueRun(bool autoRelease, bool autoDispose)
        {
            var run = new ScopeRun(autoRelease, autoDispose);
            _plannedRuns.Enqueue(run);
            return run;
        }

        public ScopeRun TakeRun()
        {
            return _plannedRuns.TryDequeue(out var run)
                ? run
                : throw new InvalidOperationException("No scoped child backend run was planned.");
        }

        public void BackendCreated(ScopeChildBackend backend)
        {
            lock (_backendGate)
            {
                _backendInstances.Add(backend);
            }
        }
    }

    private sealed class ScopeRun
    {
        private int _disposeCompletedCount;

        public ScopeRun(bool autoRelease, bool autoDispose)
        {
            if (autoRelease)
            {
                Release.TrySetResult();
            }

            if (autoDispose)
            {
                AllowDispose.TrySetResult();
            }
        }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCompletedCount => Volatile.Read(ref _disposeCompletedCount);
        public void MarkDisposed() => Interlocked.Increment(ref _disposeCompletedCount);
    }

    private sealed class ScopeFailureRecorder
    {
        private int _probeConstructed;
        private int _probeDisposed;
        public int ProbeConstructed => Volatile.Read(ref _probeConstructed);
        public int ProbeDisposed => Volatile.Read(ref _probeDisposed);
        public void Constructed() => Interlocked.Increment(ref _probeConstructed);
        public void Disposed() => Interlocked.Increment(ref _probeDisposed);
    }

    private sealed class ScopeFailureProbe(ScopeFailureRecorder recorder) : IAsyncDisposable
    {
        private readonly ScopeFailureRecorder _recorder = Mark(recorder);

        private static ScopeFailureRecorder Mark(ScopeFailureRecorder recorder)
        {
            recorder.Constructed();
            return recorder;
        }

        public ValueTask DisposeAsync()
        {
            _recorder.Disposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScopeResolutionFailureBackend : IChildProcessingRunBackend
    {
        public ScopeResolutionFailureBackend(ScopeFailureProbe probe)
        {
            _ = probe;
            throw new InvalidOperationException("planned child backend resolution failure");
        }

        public Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ScopeExecutionFailureBackend(ScopeFailureProbe probe) : IChildProcessingRunBackend
    {
        private readonly ScopeFailureProbe _probe = probe;

        public Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            _ = _probe;
            throw new InvalidOperationException("planned child backend execution failure");
        }
    }
}
