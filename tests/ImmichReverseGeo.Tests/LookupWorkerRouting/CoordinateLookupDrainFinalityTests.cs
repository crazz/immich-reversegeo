using System.Threading.Channels;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.LookupWorkerRouting;

[TestClass]
[TestCategory("Change49")]
public sealed class CoordinateLookupDrainFinalityTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly Guid JobId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [TestMethod]
    public async Task DecoratedLease_ForwardsShutdownStopBindingAndRelease()
    {
        await using var innerAdmission = new WorkerJobCoordinator(
            [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup]);
        var admission = new RecordingAdmissionGate(innerAdmission);
        var admitted = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
            admission.TryAdmit(new CoordinateLookupWorkerJobDispatch(JobId, Request())));
        var stopObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int stopCalls = 0;
        Assert.IsTrue(admitted.Lease.TryBindOwnerStop(
            admitted.Lease.Context,
            () =>
            {
                Interlocked.Increment(ref stopCalls);
                stopObserved.TrySetResult();
                return Task.CompletedTask;
            }));

        Task shutdown = innerAdmission.BeginShutdown();
        try
        {
            await stopObserved.Task.WaitAsync(Bound);
            Assert.IsFalse(shutdown.IsCompleted, "shared shutdown joins the decorated lease release");
            Assert.AreEqual(1, stopCalls);
        }
        finally
        {
            await admitted.Lease.DisposeAsync();
            await shutdown.WaitAsync(Bound);
        }

        Assert.AreEqual(1, admission.LeaseDisposeCount);
    }

    [TestMethod]
    public async Task ActualSession_TerminalThenCancelAndDisposeHoldAdmissionUntilDrain()
    {
        var process = new ControlledProcess();
        var factory = new ControlledProcessFactory(process);
        await using var innerAdmission = new WorkerJobCoordinator(
            [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup]);
        var admission = new RecordingAdmissionGate(innerAdmission);
        var worker = new ObservingWorkerClient(new CoordinateLookupWorkerClient(
            new V2InvocationBuilder(),
            new ChildWorkerLauncher(factory),
            TimeProvider.System));
        CoordinateLookupPageController? controller = null;
        var terminalSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CoordinateLookupResult expectedResult = Result();
        var expectedTerminal = new WorkerJobTerminalPayload(
            WorkerJobTerminalOutcome.Completed,
            expectedResult.StartedAtUtc,
            expectedResult.EndedAtUtc,
            null,
            expectedResult,
            null);
        controller = new CoordinateLookupPageController(
            admission,
            worker,
            new SettingsProvider(),
            () => JobId,
            () =>
            {
                if (controller?.State.TerminalObserved == true)
                {
                    terminalSeen.TrySetResult();
                }
            });
        Task run = controller.SubmitAsync(Submission());
        Task dispose = Task.CompletedTask;
        try
        {
            await factory.Started.WaitAsync(Bound);
            process.StandardOutput.Enqueue(Frame(WorkerJobProtocolCodec.Serialize(
                WorkerJobProtocolMapper.Ready(
                    1,
                    expectedResult.StartedAtUtc.AddSeconds(-1),
                    new WorkerJobReadyPayload([WorkerJobKind.CoordinateLookup])))));
            process.StandardOutput.Enqueue(Frame(WorkerJobProtocolCodec.Serialize(
                WorkerJobProtocolMapper.JobStarted(
                    new WorkerJobContext(
                        JobId,
                        WorkerJobKind.CoordinateLookup,
                        WorkerJobRequestOrigin.Manual),
                    "manual",
                    expectedResult.StartedAtUtc,
                    2))));
            process.StandardOutput.Enqueue(Frame(WorkerJobProtocolCodec.Serialize(
                WorkerJobProtocolMapper.Terminal(
                    new WorkerJobContext(
                        JobId,
                        WorkerJobKind.CoordinateLookup,
                        WorkerJobRequestOrigin.Manual),
                    expectedTerminal,
                    3))));

            await terminalSeen.Task.WaitAsync(Bound);
            Assert.IsTrue(controller.State.TerminalObserved);
            Assert.IsFalse(controller.State.FormControlsEnabled);
            Assert.IsFalse(run.IsCompleted);

            process.Exit(0);
            await process.ExitObserved.WaitAsync(Bound);
            Assert.IsFalse(run.IsCompleted);
            Assert.AreEqual(0, admission.LeaseDisposeCount);
            await controller.CancelAsync();
            Assert.IsFalse(run.IsCompleted);
            Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(
                admission.TryAdmit(new CoordinateLookupWorkerJobDispatch(
                    Guid.NewGuid(),
                    Request())));

            dispose = controller.DisposeAsync().AsTask();
            Assert.IsFalse(dispose.IsCompleted);
            Assert.IsInstanceOfType<WorkerJobAdmissionResult.Busy>(
                admission.TryAdmit(new CoordinateLookupWorkerJobDispatch(
                    Guid.NewGuid(),
                    Request())));
        }
        finally
        {
            process.Exit(0);
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            await Task.WhenAll(run, dispose).WaitAsync(Bound);
            await controller.DisposeAsync().AsTask().WaitAsync(Bound);
        }

        var completed = Assert.IsInstanceOfType<CoordinateLookupWorkerOutcome.Completed>(
            await worker.Session!.Completion.WaitAsync(Bound));
        CollectionAssert.AreEqual(
            WorkerJobProtocolCodec.Serialize(WorkerJobProtocolMapper.Terminal(
                new WorkerJobContext(
                    JobId,
                    WorkerJobKind.CoordinateLookup,
                    WorkerJobRequestOrigin.Manual),
                expectedTerminal,
                3)),
            WorkerJobProtocolCodec.Serialize(WorkerJobProtocolMapper.Terminal(
                new WorkerJobContext(
                    JobId,
                    WorkerJobKind.CoordinateLookup,
                    WorkerJobRequestOrigin.Manual),
                new WorkerJobTerminalPayload(
                    expectedTerminal.Outcome,
                    expectedTerminal.StartedAtUtc,
                    expectedTerminal.EndedAtUtc,
                    null,
                    completed.Result,
                    null),
                3)));
        Assert.IsTrue(controller.State.TerminalObserved);
        Assert.AreNotEqual(CoordinateLookupPagePhase.Cancelled, controller.State.Phase);
        Assert.AreNotEqual(CoordinateLookupPagePhase.Failed, controller.State.Phase);
        Assert.AreEqual(1, admission.LeaseDisposeCount);
        Assert.AreEqual(1, process.DisposeCount);
        var reused = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
            admission.TryAdmit(new CoordinateLookupWorkerJobDispatch(
                Guid.NewGuid(),
                Request())));
        await reused.Lease.DisposeAsync();
    }

    [TestMethod]
    public async Task ActualSession_WrongCurrentJobEventIsContainedAndClassifiedAsCorrelationFailure()
    {
        var process = new ControlledProcess();
        var factory = new ControlledProcessFactory(process);
        await using var admission = new WorkerJobCoordinator(
            [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup]);
        var readySeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CoordinateLookupPageController? controller = null;
        controller = new CoordinateLookupPageController(
            admission,
            new CoordinateLookupWorkerClient(
                new V2InvocationBuilder(),
                new ChildWorkerLauncher(factory),
                TimeProvider.System),
            new SettingsProvider(),
            () => JobId,
            () =>
            {
                if (controller?.State.Status == "Lookup worker is ready.")
                {
                    readySeen.TrySetResult();
                }
            });
        await using (controller)
        {

            Task run = controller.SubmitAsync(Submission());
            try
            {
                await factory.Started.WaitAsync(Bound);
                DateTimeOffset now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
                process.StandardOutput.Enqueue(Frame(WorkerJobProtocolCodec.Serialize(
                    WorkerJobProtocolMapper.Ready(
                        1,
                        now,
                        new WorkerJobReadyPayload([WorkerJobKind.CoordinateLookup])))));
                await readySeen.Task.WaitAsync(Bound);
                process.StandardOutput.Enqueue(Frame(WorkerJobProtocolCodec.Serialize(
                    WorkerJobProtocolMapper.JobStarted(
                        new WorkerJobContext(
                            Guid.Parse("99999999-8888-7777-6666-555555555555"),
                            WorkerJobKind.CoordinateLookup,
                            WorkerJobRequestOrigin.Manual),
                        "manual",
                        now.AddSeconds(1),
                        2))));
                await process.InputClosedForContainment.WaitAsync(Bound);
            }
            finally
            {
                process.StandardOutput.Complete();
                process.StandardError.Complete();
                process.Exit(6);
                await run.WaitAsync(Bound);
            }

            Assert.AreEqual(CoordinateLookupPagePhase.Failed, controller.State.Phase);
            StringAssert.Contains(controller.State.Error!, "lookup-correlation");
            Assert.IsNull(controller.State.Result);
            Assert.AreEqual(1, process.DisposeCount);
            var reused = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
                admission.TryAdmit(new CoordinateLookupWorkerJobDispatch(
                    Guid.NewGuid(),
                    Request())));
            await reused.Lease.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ActualSession_GraceExpiryRequestedKillWithoutTerminalCancelsCleansOnceAndAllowsReuse()
    {
        var clock = new CancellationTestClock(
            new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        var process = new ControlledProcess(
            ChildProcessKillOutcome.Requested,
            exitOnKill: true);
        var factory = new ControlledProcessFactory(process);
        await using var innerAdmission = new WorkerJobCoordinator(
            [WorkerJobDescriptors.ProcessAssets, WorkerJobDescriptors.CoordinateLookup]);
        var admission = new RecordingAdmissionGate(innerAdmission);
        var worker = new ObservingWorkerClient(new CoordinateLookupWorkerClient(
            new V2InvocationBuilder(),
            new ChildWorkerLauncher(factory),
            clock));
        var controller = new CoordinateLookupPageController(
            admission,
            worker,
            new SettingsProvider(),
            () => JobId,
            () => { });
        Task run = controller.SubmitAsync(Submission());
        Task cancel = Task.CompletedTask;
        try
        {
            await factory.Started.WaitAsync(Bound);
            process.StandardOutput.Enqueue(Frame(WorkerJobProtocolCodec.Serialize(
                WorkerJobProtocolMapper.Ready(
                    1,
                    clock.GetUtcNow(),
                    new WorkerJobReadyPayload([WorkerJobKind.CoordinateLookup])))));
            ObservedSession session = await worker.SessionStarted.WaitAsync(Bound);
            WorkerJobControllerMessage execute = ParseControllerFrame(
                await process.StandardInput.FirstFrameWritten.WaitAsync(Bound));
            Assert.AreEqual(WorkerJobProtocolV2.ExecuteType, execute.Type);
            Assert.AreEqual(JobId, execute.JobId);
            Assert.AreEqual(WorkerJobKind.CoordinateLookup, execute.JobKind);
            Assert.IsInstanceOfType<CoordinateLookupExecutePayload>(execute.Payload);
            long timerGeneration = clock.TimerGeneration;

            cancel = controller.CancelAsync();
            WorkerJobControllerMessage cancelFrame = ParseControllerFrame(
                await process.StandardInput.SecondFrameWritten.WaitAsync(Bound));
            Assert.AreEqual(WorkerJobProtocolV2.ControlCategory, cancelFrame.Category);
            Assert.AreEqual(WorkerJobProtocolV2.CancelType, cancelFrame.Type);
            Assert.AreEqual(JobId, cancelFrame.JobId);
            Assert.AreEqual(WorkerJobKind.CoordinateLookup, cancelFrame.JobKind);
            Assert.IsInstanceOfType<WorkerJobCancelPayload>(cancelFrame.Payload);
            Assert.IsGreaterThan(timerGeneration, clock.TimerGeneration);
            Assert.AreEqual(CoordinateLookupPagePhase.CancelRequested, controller.State.Phase);
            Assert.IsFalse(run.IsCompleted);
            Assert.AreEqual(0, process.KillCalls);
            Assert.AreEqual(0, admission.LeaseDisposeCount);

            clock.Advance(ChildWorkerCancellationPolicy.Grace);
            await process.KillObserved.WaitAsync(Bound);
            await process.ExitObserved.WaitAsync(Bound);
            Assert.AreEqual(
                new DateTimeOffset(2026, 9, 9, 0, 0, 10, TimeSpan.Zero),
                clock.GetUtcNow());
            Assert.AreEqual(1, process.KillCalls);
            Assert.AreEqual(ChildProcessKillOutcome.Requested, process.LastKillOutcome);
            Assert.IsFalse(run.IsCompleted, "physical exit does not replace stream finality");
            Assert.IsFalse(controller.State.FormControlsEnabled);
            Assert.AreEqual(0, admission.LeaseDisposeCount);

            process.StandardOutput.Complete();
            process.StandardError.Complete();
            await Task.WhenAll(run, cancel).WaitAsync(Bound);

            Assert.AreEqual(CoordinateLookupPagePhase.Cancelled, controller.State.Phase);
            Assert.IsNull(controller.State.Result);
            Assert.AreEqual(1, session.RequestStopCount);
            Assert.AreEqual(1, session.DisposeCount);
            Assert.AreEqual(1, admission.LeaseDisposeCount);
            Assert.AreEqual(1, process.DisposeCount);
            Assert.AreEqual(1, process.StandardInput.DisposeCount);
            Assert.AreEqual(1, process.StandardOutput.DisposeCount);
            Assert.AreEqual(1, process.StandardError.DisposeCount);

            var reused = Assert.IsInstanceOfType<WorkerJobAdmissionResult.Admitted>(
                admission.TryAdmit(new CoordinateLookupWorkerJobDispatch(
                    Guid.NewGuid(),
                    Request())));
            await reused.Lease.DisposeAsync();
        }
        finally
        {
            process.Exit(137);
            process.StandardOutput.Complete();
            process.StandardError.Complete();
            await Task.WhenAll(run, cancel).WaitAsync(Bound);
            await controller.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    private static CoordinateLookupSubmission Submission() =>
        new(47.4, 8.5, false, false, false);

    private static byte[] Frame(byte[] payload) => [.. payload, (byte)'\n'];

    private static WorkerJobControllerMessage ParseControllerFrame(byte[] frame)
    {
        Assert.IsGreaterThan(0, frame.Length);
        Assert.AreEqual((byte)'\n', frame[^1]);
        WorkerJobControllerParseResult parsed =
            WorkerJobProtocolCodec.ParseControllerInput(frame.AsSpan(0, frame.Length - 1));
        Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
        return parsed.Message!;
    }

    private static CoordinateLookupRequest Request() =>
        new(
            47.4,
            8.5,
            false,
            false,
            false,
            new CoordinateLookupCityResolverOverrides(null, []));

    private static CoordinateLookupResult Result()
    {
        DateTimeOffset started = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        CoordinateLookupSourceResult disabled = new(
            CoordinateLookupSourceState.Disabled,
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
            Request(),
            started,
            started.AddSeconds(1),
            new CoordinateLookupCountryResult(
                CoordinateLookupCountryStatus.NoMatch,
                null,
                null,
                null,
                null,
                null),
            disabled,
            disabled,
            disabled,
            disabled,
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupAdministrativeResult(null, null),
            new CoordinateLookupProfileSummary(
                null,
                ["locality"],
                CoordinateLookupTieBreak.SmallestArea),
            ["complete"],
            new CoordinateLookupFinalLocation(null, null, null));
    }

    private sealed class SettingsProvider : ICoordinateLookupSettingsSnapshotProvider
    {
        public ValueTask<CoordinateLookupCityResolverOverrides> GetAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new CoordinateLookupCityResolverOverrides(null, []));
    }

    private sealed class V2InvocationBuilder : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build() => Build(
            InternalWorkerProtocolVersion.V2);

        public WorkerCommandInvocationResolution Build(
            InternalWorkerProtocolVersion protocolVersion)
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
            return WorkerInvocation.Resolve(facts, protocolVersion);
        }
    }

    private sealed class ControlledProcessFactory(
        ControlledProcess process) : IChildProcessFactory
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        public ValueTask<IChildProcess?> StartAsync(
            ChildProcessStartDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            return ValueTask.FromResult<IChildProcess?>(process);
        }
    }

    private sealed class ControlledProcess : IChildProcess
    {
        private readonly ChildProcessKillOutcome _killOutcome;
        private readonly bool _exitOnKill;
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _killObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ControlledProcess(
            ChildProcessKillOutcome killOutcome = ChildProcessKillOutcome.Failed,
            bool exitOnKill = false)
        {
            _killOutcome = killOutcome;
            _exitOnKill = exitOnKill;
            StandardInput = new ObservedWriteStream();
            StandardOutput = new ControlledReadStream();
            StandardError = new ControlledReadStream();
        }

        public int ProcessId => 49;
        internal Task ExitObserved => _exit.Task;
        internal Task KillObserved => _killObserved.Task;
        internal int KillCalls { get; private set; }
        internal ChildProcessKillOutcome? LastKillOutcome { get; private set; }
        internal int DisposeCount { get; private set; }
        internal ObservedWriteStream StandardInput { get; }
        internal Task InputClosedForContainment => StandardInput.Closed;
        internal ControlledReadStream StandardOutput { get; }
        internal ControlledReadStream StandardError { get; }
        Stream IChildProcess.StandardInput => StandardInput;
        Stream IChildProcess.StandardOutput => StandardOutput;
        Stream IChildProcess.StandardError => StandardError;

        public Task<int> WaitForExitAsync() => _exit.Task;

        public ChildProcessExitState GetExitState() =>
            _exit.Task.IsCompletedSuccessfully
                ? ChildProcessExitState.Exited
                : ChildProcessExitState.Alive;

        public ChildProcessKillOutcome KillProcessTree()
        {
            KillCalls++;
            ChildProcessKillOutcome outcome = GetExitState() == ChildProcessExitState.Exited
                ? ChildProcessKillOutcome.AlreadyExited
                : _killOutcome;
            LastKillOutcome = outcome;
            _killObserved.TrySetResult();
            if (_exitOnKill && outcome == ChildProcessKillOutcome.Requested)
            {
                Exit(137);
            }

            return outcome;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        internal void Exit(int exitCode)
        {
            _exit.TrySetResult(exitCode);
        }
    }

    private sealed class ObservedWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<byte[]> _firstFrameWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<byte[]> _secondFrameWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;

        internal Task Closed => _closed.Task;
        internal Task<byte[]> FirstFrameWritten => _firstFrameWritten.Task;
        internal Task<byte[]> SecondFrameWritten => _secondFrameWritten.Task;
        internal int DisposeCount { get; private set; }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            ObserveFrame(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
            _closed.TrySetResult();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _closed.TrySetResult();
        }

        private void ObserveFrame(ReadOnlyMemory<byte> frame)
        {
            int writeCount = Interlocked.Increment(ref _writeCount);
            if (writeCount == 1)
            {
                _firstFrameWritten.TrySetResult(frame.ToArray());
            }
            else if (writeCount == 2)
            {
                _secondFrameWritten.TrySetResult(frame.ToArray());
            }
        }
    }

    private sealed class ObservingWorkerClient(
        ICoordinateLookupWorkerClient inner) : ICoordinateLookupWorkerClient
    {
        private readonly TaskCompletionSource<ObservedSession> _sessionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ObservedSession? Session { get; private set; }
        internal Task<ObservedSession> SessionStarted => _sessionStarted.Task;

        public async ValueTask<CoordinateLookupWorkerStartResult> StartAsync(
            IWorkerJobAdmissionLease admission,
            CoordinateLookupRequest request,
            IWorkerJobEventSink eventSink,
            CancellationToken cancellationToken)
        {
            CoordinateLookupWorkerStartResult result = await inner.StartAsync(
                admission,
                request,
                eventSink,
                cancellationToken);
            if (result is CoordinateLookupWorkerStartResult.Started started)
            {
                Session = new ObservedSession(started.Session);
                _sessionStarted.TrySetResult(Session);
                return new CoordinateLookupWorkerStartResult.Started(Session);
            }

            return result;
        }
    }

    private sealed class ObservedSession(
        ICoordinateLookupWorkerSession inner) : ICoordinateLookupWorkerSession
    {
        public Guid JobId => inner.JobId;
        public WorkerJobKind JobKind => inner.JobKind;
        public InternalWorkerProtocolVersion ProtocolVersion => inner.ProtocolVersion;
        public bool IsCancellable => inner.IsCancellable;
        public Task<CoordinateLookupWorkerOutcome> Completion => inner.Completion;
        internal int RequestStopCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public async Task RequestStopAsync()
        {
            RequestStopCount++;
            await inner.RequestStopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await inner.DisposeAsync();
        }
    }

    private sealed class RecordingAdmissionGate(
        IWorkerJobAdmissionGate inner) : IWorkerJobAdmissionGate
    {
        private RecordingLease? _lease;
        internal int LeaseDisposeCount => _lease?.DisposeCount ?? 0;

        public WorkerJobAdmissionResult TryAdmit(WorkerJobDispatch dispatch)
        {
            WorkerJobAdmissionResult result = inner.TryAdmit(dispatch);
            if (result is WorkerJobAdmissionResult.Admitted admitted)
            {
                _lease = new RecordingLease(admitted.Lease);
                return new WorkerJobAdmissionResult.Admitted(_lease);
            }

            return result;
        }

        public CacheMaintenanceAdmissionResult TryReserveCacheMaintenance(
            CacheMaintenanceRequestOrigin origin) =>
            inner.TryReserveCacheMaintenance(origin);
    }

    private sealed class RecordingLease(
        IWorkerJobAdmissionLease inner) : IWorkerJobAdmissionLease
    {
        public WorkerJobContext Context => inner.Context;
        public WorkerJobDescriptor Descriptor => inner.Descriptor;
        public bool IsStopRequested => inner.IsStopRequested;
        internal int DisposeCount { get; private set; }

        public bool TryBindOwnerStop(
            WorkerJobContext context,
            Func<Task> requestStopAsync) =>
            inner.TryBindOwnerStop(context, requestStopAsync);

        public bool TryAdvance(
            WorkerJobContext context,
            WorkerJobLifecycle lifecycle,
            int? childProcessId = null) =>
            inner.TryAdvance(context, lifecycle, childProcessId);

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await inner.DisposeAsync();
        }
    }

    private sealed class ControlledReadStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        private byte[]? _current;
        private int _offset;
        internal int DisposeCount { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void Enqueue(byte[] bytes)
        {
            Assert.IsTrue(_chunks.Writer.TryWrite(bytes));
        }

        internal void Complete()
        {
            _chunks.Writer.TryComplete();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }

                if (_chunks.Reader.TryRead(out byte[]? next))
                {
                    _current = next;
                    _offset = 0;
                }
            }

            int count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            Complete();
            return ValueTask.CompletedTask;
        }
    }
}
