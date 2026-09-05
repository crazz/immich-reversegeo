using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerShutdown;

[TestClass]
[TestCategory("Change29")]
public sealed class CoordinatorShutdownTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task IdleLifecycleSignals_ShareOneTaskAndPermanentlyFenceAdmission()
    {
        using var lifetime = new TestLifetime();
        var observer = new ShutdownObserver();
        var executor = new GatedExecutor();
        var fixture = new CoordinatorFixture(executor, observer, lifetime);
        await fixture.Coordinator.StartAsync(CancellationToken.None);

        lifetime.StopApplication();
        lifetime.StopApplication();
        var shutdown = fixture.Coordinator.BeginShutdown();
        using var cancelledHostWait = new CancellationTokenSource();
        cancelledHostWait.Cancel();
        var hostedStop = fixture.Coordinator.StopAsync(cancelledHostWait.Token);
        var asyncDisposal = fixture.Coordinator.DisposeAsync().AsTask();

        Assert.AreSame(shutdown, hostedStop);
        Assert.AreSame(shutdown, asyncDisposal);
        await shutdown.WaitAsync(Bound);
        fixture.Coordinator.Dispose();

        Assert.AreEqual(ProcessingRunAdmissionResult.Stopping,
            await fixture.Coordinator.TriggerManualAsync());
        Assert.AreEqual(0, fixture.IdentityCalls);
        Assert.AreEqual(0, executor.CallCount);
        Assert.AreEqual(1, observer.StartedCount);
        Assert.AreEqual(1, observer.StoppingCount);
        Assert.AreEqual(1, observer.StoppedCount);
    }

    [TestMethod]
    public async Task LifetimeFence_PrecedesCallbacksAndHostTokenCannotAbandonCapturedRun()
    {
        using var lifetime = new TestLifetime();
        var observer = new ShutdownObserver(pauseStopGate: true);
        var executor = new GatedExecutor();
        var fixture = new CoordinatorFixture(executor, observer, lifetime);
        await fixture.Coordinator.StartAsync(CancellationToken.None);
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync());
        await executor.Entered.Task.WaitAsync(Bound);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = executor.Token.Register(
            () => cancellationObserved.TrySetResult());

        var lifetimeNotification = Task.Run(lifetime.StopApplication);
        await lifetimeNotification.WaitAsync(Bound);
        var shutdown = fixture.Coordinator.BeginShutdown();
        await observer.StopGateEntered.Task.WaitAsync(Bound);

        try
        {
            Assert.IsFalse(executor.Token.IsCancellationRequested,
                "The post-fence observer gate must run before cancellation callbacks.");
            Assert.AreEqual(ProcessingRunAdmissionResult.Stopping,
                await fixture.Coordinator.TriggerManualAsync());
            Assert.AreEqual(1, fixture.IdentityCalls);
            Assert.AreEqual(1, executor.CallCount);

            using var expiredHostWait = new CancellationTokenSource();
            expiredHostWait.Cancel();
            Assert.AreSame(shutdown, fixture.Coordinator.StopAsync(expiredHostWait.Token));
            Assert.IsFalse(shutdown.IsCompleted,
                "Host wait cancellation cannot complete or cancel owned shutdown.");
        }
        finally
        {
            observer.ReleaseStopGate.TrySetResult();
        }

        await cancellationObserved.Task.WaitAsync(Bound);
        Assert.IsFalse(shutdown.IsCompleted,
            "The captured executor still owns cleanup after cancellation delivery.");

        executor.Cancel();
        await shutdown.WaitAsync(Bound);

        Assert.IsFalse(shutdown.IsCanceled);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsNull(fixture.State.LastRunCompleted);
        Assert.IsNull(fixture.State.LastError);
        Assert.IsFalse(fixture.State.GetRecentLog().Any(
            line => line.Contains("Run complete.", StringComparison.Ordinal)));
        Assert.AreEqual(1, observer.StoppingCount);
        Assert.AreEqual(1, observer.StoppedCount);
    }

    [TestMethod]
    public async Task ShutdownDuringFinalCleanup_JoinsThePublishedHandleWithoutLateCancellation()
    {
        var observer = new ShutdownObserver(pauseDetach: true);
        var executor = new GatedExecutor();
        var fixture = new CoordinatorFixture(executor, observer);
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync());
        await executor.Entered.Task.WaitAsync(Bound);

        executor.Complete();
        await observer.DetachEntered.Task.WaitAsync(Bound);

        var shutdown = fixture.Coordinator.BeginShutdown();
        try
        {
            Assert.AreSame(shutdown, fixture.Coordinator.StopAsync(CancellationToken.None));
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.IsFalse(executor.Token.IsCancellationRequested,
                "Cleanup already closed the exact handle's Stop claims.");
        }
        finally
        {
            observer.ReleaseDetach.TrySetResult();
        }

        await shutdown.WaitAsync(Bound);

        Assert.IsFalse(executor.Token.IsCancellationRequested);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsNull(fixture.State.LastRunCompleted);
        Assert.IsNull(fixture.State.LastError);
    }

    [TestMethod]
    public async Task ShutdownDuringPendingNotification_PreventsDispatchAndRollsBackWithoutFatalState()
    {
        var pendingEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePending = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new GatedExecutor();
        var fixture = new CoordinatorFixture(executor);
        fixture.State.OnChanged += () =>
        {
            if (!fixture.State.IsRunning)
            {
                return;
            }

            pendingEntered.TrySetResult();
            releasePending.Task.GetAwaiter().GetResult();
        };

        var admission = Task.Run(fixture.Coordinator.TriggerManualAsync);
        await pendingEntered.Task.WaitAsync(Bound);
        var shutdown = fixture.Coordinator.BeginShutdown();

        try
        {
            Assert.AreEqual(1, fixture.IdentityCalls);
            Assert.AreEqual(0, executor.CallCount);
            Assert.AreEqual(ProcessingRunAdmissionResult.Stopping,
                await fixture.Coordinator.TriggerManualAsync());
        }
        finally
        {
            releasePending.TrySetResult();
        }

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await admission.WaitAsync(Bound));
        await shutdown.WaitAsync(Bound);

        Assert.AreEqual(0, executor.CallCount);
        Assert.IsFalse(fixture.State.IsRunning);
        Assert.IsNull(fixture.State.LastRunCompleted);
        Assert.IsNull(fixture.State.LastError);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
    }

    [TestMethod]
    public async Task StartFailure_FencesAndJoinsTheSameActiveCleanupBeforeRethrowing()
    {
        var startupFailure = new InvalidOperationException("coordinator-start-failed");
        var observer = new ShutdownObserver(startFailure: startupFailure);
        var executor = new GatedExecutor();
        var fixture = new CoordinatorFixture(executor, observer);
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync());
        await executor.Entered.Task.WaitAsync(Bound);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = executor.Token.Register(
            () => cancellationObserved.TrySetResult());

        var startup = fixture.Coordinator.StartAsync(CancellationToken.None);
        await cancellationObserved.Task.WaitAsync(Bound);
        Assert.IsFalse(startup.IsCompleted,
            "Startup failure must retain the captured run through cleanup.");
        Assert.AreEqual(ProcessingRunAdmissionResult.Stopping,
            await fixture.Coordinator.TriggerManualAsync());

        executor.Cancel();
        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await startup.WaitAsync(Bound));

        Assert.AreSame(startupFailure, observed);
        Assert.AreSame(fixture.Coordinator.BeginShutdown(),
            fixture.Coordinator.StopAsync(CancellationToken.None));
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsNull(fixture.State.LastRunCompleted);
        Assert.IsNull(fixture.State.LastError);
        Assert.AreEqual(1, executor.CallCount);
    }

    [TestMethod]
    public async Task SynchronousDisposal_JoinsTheSamePendingShutdownOperation()
    {
        var executor = new GatedExecutor();
        var fixture = new CoordinatorFixture(executor);
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync());
        await executor.Entered.Task.WaitAsync(Bound);

        var synchronousDisposal = Task.Run(fixture.Coordinator.Dispose);
        var shutdown = fixture.Coordinator.BeginShutdown();
        await executor.CancellationObserved.Task.WaitAsync(Bound);
        Assert.IsFalse(synchronousDisposal.IsCompleted);
        Assert.AreSame(shutdown,
            fixture.Coordinator.StopAsync(new CancellationToken(canceled: true)));

        executor.Cancel();
        await synchronousDisposal.WaitAsync(Bound);
        await shutdown.WaitAsync(Bound);

        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.AreEqual(1, executor.CallCount);
    }

    [TestMethod]
    public async Task LifecycleCallbackFailure_IsObservedAfterCapturedCleanupOnSharedTask()
    {
        var callbackFailure = new InvalidOperationException("coordinator-stopping-failed");
        var observer = new ShutdownObserver(stopFailure: callbackFailure);
        var executor = new GatedExecutor();
        var fixture = new CoordinatorFixture(executor, observer);
        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync());
        await executor.Entered.Task.WaitAsync(Bound);

        var shutdown = fixture.Coordinator.BeginShutdown();
        Assert.AreSame(shutdown,
            fixture.Coordinator.StopAsync(CancellationToken.None));
        await executor.CancellationObserved.Task.WaitAsync(Bound);
        executor.Cancel();

        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await shutdown.WaitAsync(Bound));

        Assert.AreSame(callbackFailure, observed);
        Assert.IsNull(fixture.Coordinator.ActiveRequest);
        Assert.IsNull(fixture.State.LastRunCompleted);
        Assert.IsNull(fixture.State.LastError);
        Assert.AreEqual(1, observer.StoppingCount);
        Assert.AreEqual(1, observer.StoppedCount);
    }

    private sealed class CoordinatorFixture
    {
        private int _identityCalls;

        internal CoordinatorFixture(
            GatedExecutor executor,
            IProcessingRunCoordinatorObserver? observer = null,
            IHostApplicationLifetime? lifetime = null)
        {
            Executor = executor;
            Coordinator = new ProcessingRunCoordinator(
                State,
                new ProcessingStateEventReporter(State),
                executor,
                NullLogger<ProcessingRunCoordinator>.Instance,
                CreateRunId,
                observer,
                lifetime);
        }

        internal ProcessingState State { get; } = new();
        internal GatedExecutor Executor { get; }
        internal ProcessingRunCoordinator Coordinator { get; }
        internal int IdentityCalls => Volatile.Read(ref _identityCalls);

        private Guid CreateRunId()
        {
            Interlocked.Increment(ref _identityCalls);
            return Guid.NewGuid();
        }
    }

    private sealed class GatedExecutor : IProcessingRunExecutor
    {
        private readonly TaskCompletionSource<ProcessingRunResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProcessingRunRequest? Request { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Request = request;
            Token = cancellationToken;
            cancellationToken.Register(
                () => CancellationObserved.TrySetResult());
            Interlocked.Increment(ref _callCount);
            Entered.TrySetResult();
            return _completion.Task;
        }

        internal void Cancel()
        {
            if (Request is null)
            {
                throw new InvalidOperationException("No processing request was dispatched.");
            }

            _completion.TrySetException(new OperationCanceledException(Token));
        }

        internal void Complete()
        {
            var request = Request
                ?? throw new InvalidOperationException("No processing request was dispatched.");
            _completion.TrySetResult(new ProcessingRunResult(
                request,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                processedCount: 0,
                updatedCount: 0,
                skippedCount: 0,
                failedCount: 0,
                ProcessingRunOutcome.Completed,
                failureMessage: null));
        }
    }

    private sealed class ShutdownObserver(
        bool pauseStopGate = false,
        bool pauseDetach = false,
        Exception? startFailure = null,
        Exception? stopFailure = null) : IProcessingRunCoordinatorObserver
    {
        private int _startedCount;
        private int _stoppingCount;
        private int _stoppedCount;

        internal TaskCompletionSource StopGateEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseStopGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DetachEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDetach { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int StartedCount => Volatile.Read(ref _startedCount);
        internal int StoppingCount => Volatile.Read(ref _stoppingCount);
        internal int StoppedCount => Volatile.Read(ref _stoppedCount);

        public async ValueTask BeforeAdmissionGateAsync(
            ProcessingRunAdmissionAttempt attempt)
        {
            if (attempt != ProcessingRunAdmissionAttempt.Stop || !pauseStopGate)
            {
                return;
            }

            StopGateEntered.TrySetResult();
            await ReleaseStopGate.Task.ConfigureAwait(false);
        }

        public void CoordinatorStarted()
        {
            Interlocked.Increment(ref _startedCount);
            if (startFailure is not null)
            {
                throw startFailure;
            }
        }

        public void CoordinatorStopping()
        {
            Interlocked.Increment(ref _stoppingCount);
            if (stopFailure is not null)
            {
                throw stopFailure;
            }
        }

        public void CoordinatorStopped()
        {
            Interlocked.Increment(ref _stoppedCount);
        }

        public async ValueTask BeforeDetachAsync(
            ProcessingRunRequest request,
            CancellationToken activeToken)
        {
            if (!pauseDetach)
            {
                return;
            }

            DetachEntered.TrySetResult();
            await ReleaseDetach.Task.ConfigureAwait(false);
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
        }

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
