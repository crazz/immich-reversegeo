using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerShutdown;

[TestClass]
[TestCategory("Change29")]
public sealed class ShutdownSessionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task PreReadyUserStopThenShutdown_PreservesOriginalDeadlineAndJoinsEachDrain()
    {
        var clock = new CancellationTestClock(DateTimeOffset.UnixEpoch);
        var context = await ShutdownCoordinatorContext.CreateAsync(clock);
        var input = new SessionInputStream();
        var process = new ShutdownDrainProcess(input);
        var session = await ChildWorkerSession.CreateAsync(
            process,
            context.Request,
            new SessionRecordingSink(),
            new ChildWorkerLauncherOptions
            {
                TimeProvider = clock,
                ReadyTimeout = Timeout.InfiniteTimeSpan
            },
            new ChildWorkerObserverArmingAcknowledgements());

        try
        {
            Assert.IsTrue(context.Coordinator.TryAttachChildSession(context.Request, session));

            var timerGeneration = clock.TimerGeneration;
            var userStop = context.Coordinator.StopActiveRun();
            Assert.IsNotNull(userStop);
            await clock.WaitForTimerCreatedAsync(timerGeneration)
                .WaitAsync(Bound);

            Assert.AreEqual(DateTimeOffset.UnixEpoch, session.CancellationFacts!.FirstStopAtUtc);
            Assert.AreEqual(
                DateTimeOffset.UnixEpoch + ChildWorkerCancellationPolicy.Grace,
                session.CancellationFacts.DeadlineUtc);
            Assert.IsFalse(session.CancellationFacts.RequestAccepted);
            Assert.AreEqual(0, input.WriteCalls);
            Assert.AreEqual(0, input.FlushCalls);

            clock.Advance(TimeSpan.FromSeconds(4));
            using var cancelledHostWait = new CancellationTokenSource();
            cancelledHostWait.Cancel();
            var shutdown = context.Coordinator.BeginShutdown();
            Assert.AreSame(
                shutdown,
                context.Coordinator.StopAsync(cancelledHostWait.Token),
                "The host token must only wait on the exact memoized shutdown task.");
            context.Invocation.CompleteAsCancelled();

            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, process.KillCalls);
            Assert.IsFalse(shutdown.IsCompleted);

            clock.Advance(TimeSpan.FromSeconds(1));
            await process.KillObserved.Task.WaitAsync(Bound);
            await session.WaitForCancellationDeliveryAsync().WaitAsync(Bound);
            Assert.AreEqual(1, process.KillCalls);
            Assert.AreEqual(0, process.DisposeCalls);
            Assert.AreEqual(0, input.DisposeCalls);
            Assert.IsFalse(shutdown.IsCompleted);

            process.ExitWithoutCompletingStreams(137);
            await process.ExitObserved.Task.WaitAsync(Bound);
            Assert.IsFalse(shutdown.IsCompleted,
                "Physical process exit must not bypass either output drain.");
            Assert.AreEqual(0, process.DisposeCalls);

            process.StandardOutputSource.Complete();
            await process.StandardOutputSource.EndOfStreamObserved.WaitAsync(Bound);
            Assert.IsFalse(shutdown.IsCompleted,
                "The first EOF must not bypass the still-open stderr drain.");
            Assert.AreEqual(0, process.DisposeCalls);

            process.StandardErrorSource.Complete();
            await process.StandardErrorSource.EndOfStreamObserved.WaitAsync(Bound);

            var completion = await session.Completion.WaitAsync(Bound);
            await userStop.WaitAsync(Bound);
            await shutdown.WaitAsync(Bound);

            var facts = session.CancellationFacts!;
            Assert.AreEqual(DateTimeOffset.UnixEpoch, facts.FirstStopAtUtc);
            Assert.AreEqual(
                DateTimeOffset.UnixEpoch + ChildWorkerCancellationPolicy.Grace,
                facts.DeadlineUtc);
            Assert.IsFalse(facts.RequestAccepted);
            Assert.AreEqual(
                ChildWorkerCancelDeliveryPhase.DeadlineElapsed,
                facts.DeliveryPhase);
            Assert.AreEqual(
                ChildWorkerCancellationExitRace.BeforeControl,
                facts.ExitRace);
            Assert.IsTrue(facts.GraceExpired);
            Assert.IsTrue(facts.KillAttempted);
            Assert.AreEqual(ChildProcessKillOutcome.Requested, facts.KillOutcome);
            Assert.AreEqual(0, input.WriteCalls);
            Assert.AreEqual(0, input.FlushCalls);
            Assert.IsTrue(completion.ExitObserved);
            Assert.AreEqual(137, completion.ExitCode);
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
                completion.StandardOutputFinality);
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
                completion.StandardErrorFinality);
            Assert.IsNull(completion.Terminal);
            Assert.AreEqual(1, process.DisposeCalls);
            Assert.AreEqual(1, input.DisposeCalls);
            Assert.AreEqual(1, process.StandardOutputSource.DisposeCalls);
            Assert.AreEqual(1, process.StandardErrorSource.DisposeCalls);
            Assert.AreEqual(0, clock.ActiveTimerCount);
            Assert.AreEqual(1, clock.TimerDisposeCalls, "Infinite readiness allocates no timer; the shared grace timer is disposed once.");
            Assert.IsNull(context.Coordinator.ActiveRequest);
            Assert.IsTrue(context.State.IsRunning, "Shutdown preserves the armed nonterminal projection for block 30 finalization.");
            Assert.IsNull(context.State.LastRunCompleted);
            Assert.IsNull(context.State.LastError);

            await session.DisposeAsync();
            Assert.AreEqual(1, process.DisposeCalls);
        }
        finally
        {
            var cleanup = context.Coordinator.BeginShutdown();
            context.Invocation.CompleteAsCancelled();
            process.ExitWithoutCompletingStreams(137);
            process.StandardOutputSource.Complete();
            process.StandardErrorSource.Complete();
            await cleanup.WaitAsync(Bound);
            await session.DisposeAsync();
        }
    }

    private sealed class ShutdownCoordinatorContext
    {
        private ShutdownCoordinatorContext(
            ProcessingState state,
            ShutdownInvocation invocation,
            ProcessingRunCoordinator coordinator)
        {
            State = state;
            Invocation = invocation;
            Coordinator = coordinator;
        }

        internal ProcessingState State { get; }
        internal ShutdownInvocation Invocation { get; }
        internal ProcessingRunCoordinator Coordinator { get; }
        internal ProcessingRunRequest Request
            => Invocation.Request
                ?? throw new AssertFailedException(
                    "The coordinator did not publish its admitted request.");

        internal static async Task<ShutdownCoordinatorContext> CreateAsync(
            TimeProvider clock)
        {
            var state = new ProcessingState();
            var invocation = new ShutdownInvocation();
            var coordinator = new ProcessingRunCoordinator(
                state,
                new ProcessingStateEventReporter(state),
                new ShutdownExecutor(invocation),
                NullLogger<ProcessingRunCoordinator>.Instance,
                Guid.NewGuid,
                observer: null,
                applicationLifetime: null,
                timeProvider: clock);

            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await coordinator.TriggerManualAsync());
            await invocation.Entered.Task.WaitAsync(Bound);
            Assert.AreSame(invocation.Request, coordinator.ActiveRequest);
            return new ShutdownCoordinatorContext(state, invocation, coordinator);
        }
    }

    private sealed class ShutdownExecutor(ShutdownInvocation invocation)
        : IProcessingRunExecutor
    {
        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            invocation.Request = request;
            invocation.Token = cancellationToken;
            invocation.Entered.TrySetResult();
            return invocation.Completion.Task;
        }
    }

    private sealed class ShutdownInvocation
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ProcessingRunResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProcessingRunRequest? Request { get; set; }
        internal CancellationToken Token { get; set; }

        internal void CompleteAsCancelled()
        {
            Completion.TrySetException(new OperationCanceledException(Token));
        }
    }

    private sealed class ShutdownDrainProcess(SessionInputStream input)
        : IChildProcess
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exitState;
        private int _disposeCalls;
        private int _killCalls;

        public int ProcessId => 2901;
        public Stream StandardInput { get; } = input;
        public Stream StandardOutput => StandardOutputSource;
        public Stream StandardError => StandardErrorSource;
        internal ShutdownDrainStream StandardOutputSource { get; } = new();
        internal ShutdownDrainStream StandardErrorSource { get; } = new();
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int KillCalls => Volatile.Read(ref _killCalls);
        internal TaskCompletionSource KillObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ExitObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> WaitForExitAsync() => _exit.Task;

        public ChildProcessExitState GetExitState()
            => Volatile.Read(ref _exitState) == 0
                ? ChildProcessExitState.Alive
                : ChildProcessExitState.Exited;

        public ChildProcessKillOutcome KillProcessTree()
        {
            Interlocked.Increment(ref _killCalls);
            KillObserved.TrySetResult();
            return GetExitState() == ChildProcessExitState.Exited
                ? ChildProcessKillOutcome.AlreadyExited
                : ChildProcessKillOutcome.Requested;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }

        internal void ExitWithoutCompletingStreams(int code)
        {
            if (Interlocked.Exchange(ref _exitState, 1) == 0)
            {
                _exit.TrySetResult(code);
                ExitObserved.TrySetResult();
            }
        }
    }

    private sealed class ShutdownDrainStream : Stream
    {
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _endOfStreamObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCalls;

        internal Task EndOfStreamObserved => _endOfStreamObserved.Task;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _endOfStreamObserved.TrySetResult();
            return 0;
        }

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            _completed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        internal void Complete() => _completed.TrySetResult();

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
