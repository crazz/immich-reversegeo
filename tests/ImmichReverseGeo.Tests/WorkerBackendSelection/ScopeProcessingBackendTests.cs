using System.Collections.Concurrent;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerBackendSelection;

[TestClass]
[TestCategory("Change33")]
public sealed class ScopeProcessingBackendTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Coordinator_SelectionBuildsOnlyItsScopedDependencyGraph()
    {
        foreach (var backend in new[] { ProcessingBackendKind.InProcess, ProcessingBackendKind.ChildWorker })
        {
            await using var provider = CreateScopedBackendProvider(out var recorder, out var counters);
            recorder.EnqueueRun(autoRelease: true, autoDispose: true);
            var coordinator = CreateCoordinator(provider, backend, Guid.NewGuid);

            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync(), backend + "-admission");
            await coordinator.WaitForActiveRunAsync().WaitAsync(TestTimeout);

            Assert.AreEqual(1, counters.BackendCount(backend), backend + "-selected-adapter-constructed");
            Assert.AreEqual(0, counters.BackendCount(Other(backend)), backend + "-unselected-adapter-not-constructed");
            Assert.AreEqual(
                backend == ProcessingBackendKind.InProcess ? 1 : 0,
                counters.InProcessExecutorConstructed,
                backend + "-executor-graph");
            Assert.AreEqual(
                backend == ProcessingBackendKind.InProcess ? 1 : 0,
                counters.GeodataConstructed,
                backend + "-geodata-graph");
            Assert.AreEqual(
                backend == ProcessingBackendKind.ChildWorker ? 1 : 0,
                counters.ChildCommandConstructed,
                backend + "-command-graph");
            Assert.AreEqual(
                backend == ProcessingBackendKind.ChildWorker ? 1 : 0,
                counters.ChildLauncherConstructed,
                backend + "-launcher-graph");
            Assert.AreEqual(
                backend == ProcessingBackendKind.ChildWorker ? 1 : 0,
                counters.ChildBridgeConstructed,
                backend + "-bridge-graph");
        }
    }

    [TestMethod]
    public async Task Coordinator_SelectedScopedBackendDisposesBeforeExactHandleReleaseAndLaterRunGetsNewScope()
    {
        await using var provider = CreateScopedBackendProvider(out var recorder, out var counters);
        var firstRun = recorder.EnqueueRun(autoRelease: false, autoDispose: false);
        var secondRun = recorder.EnqueueRun(autoRelease: false, autoDispose: true);
        var ids = new Queue<Guid>(new[]
        {
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000002")
        });
        var coordinator = CreateCoordinator(provider, ProcessingBackendKind.InProcess, ids.Dequeue);

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

        Assert.AreEqual(2, counters.BackendCount(ProcessingBackendKind.InProcess), "one-adapter-per-admitted-run");
        Assert.AreEqual(2, recorder.BackendInstances.Count, "two-scoped-adapter-instances");
        Assert.AreNotSame(recorder.BackendInstances[0], recorder.BackendInstances[1], "later-run-uses-new-scope");
    }

    [TestMethod]
    public async Task Coordinator_BackendResolutionFailureDisposesCreatedScopeBeforeReturningToIdle()
    {
        var recorder = new ScopeFailureRecorder();
        var services = CreateFailureServices(recorder);
        services.AddKeyedScoped<IProcessingRunBackend, ScopeResolutionFailureBackend>(ProcessingBackendKind.InProcess);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var coordinator = CreateCoordinator(provider, ProcessingBackendKind.InProcess, Guid.NewGuid);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.TriggerManualAsync().WaitAsync(TestTimeout));

        Assert.AreEqual(1, recorder.ProbeConstructed, "resolution-probe-constructed");
        Assert.AreEqual(1, recorder.ProbeDisposed, "resolution-scope-disposed");
        Assert.IsNull(coordinator.ActiveRequest, "resolution-failure-releases-handle");
    }

    [TestMethod]
    public async Task Coordinator_BackendExecutionFailureDisposesRunScopeBeforeReturningToIdle()
    {
        var recorder = new ScopeFailureRecorder();
        var services = CreateFailureServices(recorder);
        services.AddKeyedScoped<IProcessingRunBackend, ScopeExecutionFailureBackend>(ProcessingBackendKind.InProcess);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var coordinator = CreateCoordinator(provider, ProcessingBackendKind.InProcess, Guid.NewGuid);

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
        services.AddScoped<ScopeInProcessExecutorGraph>();
        services.AddScoped<ScopeChildCommandProbe>();
        services.AddScoped<ScopeChildLauncherProbe>();
        services.AddScoped<ScopeChildBridgeProbe>();
        services.AddScoped<ScopeChildWorkerGraph>();
        services.AddKeyedScoped<IProcessingRunBackend, ScopeInProcessBackend>(ProcessingBackendKind.InProcess);
        services.AddKeyedScoped<IProcessingRunBackend, ScopeChildWorkerBackend>(ProcessingBackendKind.ChildWorker);
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
        ProcessingBackendKind backend,
        Func<Guid> createRunId)
    {
        return new ProcessingRunCoordinator(
            provider.GetRequiredService<ProcessingState>(),
            provider.GetRequiredService<ProcessingStateEventReporter>(),
            global::ImmichReverseGeo.Tests.AlwaysHasWorkScheduledRunGate.Instance,
            new TemporaryProcessingBackendSelection(backend),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProcessingRunCoordinator>.Instance,
            createRunId);
    }

    private static ProcessingBackendKind Other(ProcessingBackendKind backend)
    {
        return backend == ProcessingBackendKind.InProcess
            ? ProcessingBackendKind.ChildWorker
            : ProcessingBackendKind.InProcess;
    }

    private sealed class ScopeActivationCounters
    {
        private readonly ConcurrentDictionary<ProcessingBackendKind, int> _backends = new();
        private int _inProcessExecutorConstructed;
        private int _geodataConstructed;
        private int _childCommandConstructed;
        private int _childLauncherConstructed;
        private int _childBridgeConstructed;

        public int InProcessExecutorConstructed => Volatile.Read(ref _inProcessExecutorConstructed);
        public int GeodataConstructed => Volatile.Read(ref _geodataConstructed);
        public int ChildCommandConstructed => Volatile.Read(ref _childCommandConstructed);
        public int ChildLauncherConstructed => Volatile.Read(ref _childLauncherConstructed);
        public int ChildBridgeConstructed => Volatile.Read(ref _childBridgeConstructed);

        public void Constructed(ProcessingBackendKind backend) => _backends.AddOrUpdate(backend, 1, (_, count) => count + 1);
        public int BackendCount(ProcessingBackendKind backend) => _backends.TryGetValue(backend, out var count) ? count : 0;
        public void InProcessExecutorCreated() => Interlocked.Increment(ref _inProcessExecutorConstructed);
        public void GeodataCreated() => Interlocked.Increment(ref _geodataConstructed);
        public void ChildCommandCreated() => Interlocked.Increment(ref _childCommandConstructed);
        public void ChildLauncherCreated() => Interlocked.Increment(ref _childLauncherConstructed);
        public void ChildBridgeCreated() => Interlocked.Increment(ref _childBridgeConstructed);
    }

    private sealed class ScopeGeodataProbe
    {
        public ScopeGeodataProbe(ScopeActivationCounters counters) => counters.GeodataCreated();
    }

    private sealed class ScopeInProcessExecutorGraph
    {
        public ScopeInProcessExecutorGraph(ScopeActivationCounters counters, ScopeGeodataProbe geodata)
        {
            _ = geodata;
            counters.InProcessExecutorCreated();
        }
    }

    private sealed class ScopeChildCommandProbe
    {
        public ScopeChildCommandProbe(ScopeActivationCounters counters) => counters.ChildCommandCreated();
    }

    private sealed class ScopeChildLauncherProbe
    {
        public ScopeChildLauncherProbe(ScopeActivationCounters counters) => counters.ChildLauncherCreated();
    }

    private sealed class ScopeChildBridgeProbe
    {
        public ScopeChildBridgeProbe(ScopeActivationCounters counters) => counters.ChildBridgeCreated();
    }

    private sealed class ScopeChildWorkerGraph
    {
        public ScopeChildWorkerGraph(
            ScopeChildCommandProbe command,
            ScopeChildLauncherProbe launcher,
            ScopeChildBridgeProbe bridge)
        {
            _ = command;
            _ = launcher;
            _ = bridge;
        }
    }

    private abstract class ScopeBackendBase : IProcessingRunBackend, IAsyncDisposable
    {
        private readonly ScopeBackendRecorder _recorder;
        private readonly ProcessingBackendKind _backend;
        private ScopeRun? _run;

        protected ScopeBackendBase(ScopeBackendRecorder recorder, ScopeActivationCounters counters, ProcessingBackendKind backend)
        {
            _recorder = recorder;
            _backend = backend;
            counters.Constructed(backend);
            recorder.BackendCreated(this);
        }

        public async Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            _run = _recorder.TakeRun(_backend);
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

    private sealed class ScopeInProcessBackend : ScopeBackendBase
    {
        public ScopeInProcessBackend(
            ScopeBackendRecorder recorder,
            ScopeActivationCounters counters,
            ScopeInProcessExecutorGraph executorGraph)
            : base(recorder, counters, ProcessingBackendKind.InProcess)
        {
            _ = executorGraph;
        }
    }

    private sealed class ScopeChildWorkerBackend : ScopeBackendBase
    {
        public ScopeChildWorkerBackend(
            ScopeBackendRecorder recorder,
            ScopeActivationCounters counters,
            ScopeChildWorkerGraph childGraph)
            : base(recorder, counters, ProcessingBackendKind.ChildWorker)
        {
            _ = childGraph;
        }
    }

    private sealed class ScopeBackendRecorder
    {
        private readonly ConcurrentQueue<ScopeRun> _plannedRuns = new();
        private readonly List<ScopeBackendBase> _backendInstances = [];
        private readonly object _backendGate = new();

        public IReadOnlyList<ScopeBackendBase> BackendInstances
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

        public ScopeRun TakeRun(ProcessingBackendKind backend)
        {
            _ = backend;
            if (!_plannedRuns.TryDequeue(out var run))
            {
                throw new InvalidOperationException("No scoped backend run was planned.");
            }

            return run;
        }

        public void BackendCreated(ScopeBackendBase backend)
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

    private sealed class ScopeFailureProbe : IAsyncDisposable
    {
        private readonly ScopeFailureRecorder _recorder;

        public ScopeFailureProbe(ScopeFailureRecorder recorder)
        {
            _recorder = recorder;
            recorder.Constructed();
        }

        public ValueTask DisposeAsync()
        {
            _recorder.Disposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScopeResolutionFailureBackend : IProcessingRunBackend
    {
        public ScopeResolutionFailureBackend(ScopeFailureProbe probe)
        {
            _ = probe;
            throw new InvalidOperationException("planned keyed backend resolution failure");
        }

        public Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ScopeExecutionFailureBackend(ScopeFailureProbe probe) : IProcessingRunBackend
    {
        public Task<ProcessingRunResult> ExecuteAsync(ProcessingRunRequest request, IProcessingEventReporter reporter, CancellationToken cancellationToken)
        {
            _ = probe;
            _ = request;
            _ = reporter;
            _ = cancellationToken;
            throw new InvalidOperationException("planned backend execution failure");
        }
    }
}
