using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.WorkerFailureRecovery;

[TestClass]
[TestCategory("Change30")]
public sealed class ChildWorkerSessionContainmentTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ReadyTimeout_PublishesFaultTimestampWithoutStartingContainment()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        long timerGeneration = clock.TimerGeneration;
        SessionTestSupport.SessionFixture fixture = await CreateAsync(
            clock: clock,
            readyTimeout: TimeSpan.FromSeconds(3));

        await clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        clock.Advance(TimeSpan.FromSeconds(3));

        ChildWorkerTerminalPreventingObservation observation =
            await fixture.Session.FirstTerminalPreventingObservation.WaitAsync(TestTimeout);

        Assert.AreEqual(
            SessionTestSupport.Start + TimeSpan.FromSeconds(3),
            observation.ObservedAt.FirstStopAtUtc);
        Assert.AreSame(clock, observation.ObservedAt.Clock);
        Assert.IsTrue(
            observation.Reason is ChildWorkerFaultContainmentReason.ReadyTimedOut);
        Assert.IsNull(fixture.Session.CancellationFacts);
        Assert.AreEqual(0, fixture.Process.KillCalls);
        Assert.AreEqual(0, fixture.Input.WriteCalls);

        fixture.Process.Exit(1);
        await fixture.Session.Settlement.WaitAsync(TestTimeout);
        AssertResourcesDisposedOnce(fixture);
        Assert.AreEqual(1, clock.TimerDisposeCalls);
    }

    [TestMethod]
    public async Task MissingTerminal_PreservesTypedProtocolFailureWithoutAutomaticContainment()
    {
        SessionTestSupport.SessionFixture fixture = await CreateAsync();
        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        WorkerProtocolEvent started = WorkerProtocolMapper.Map(
            new RunStarted(fixture.Request, fixture.Clock.GetUtcNow()),
            2);
        fixture.Process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(started));
        fixture.Process.StandardOutputSource.Complete();

        ChildWorkerTerminalPreventingObservation observation =
            await fixture.Session.FirstTerminalPreventingObservation.WaitAsync(TestTimeout);

        Assert.AreEqual(
            SessionTestSupport.Start + TimeSpan.FromSeconds(2),
            observation.ObservedAt.FirstStopAtUtc);
        Assert.IsTrue(
            observation.Reason is ChildWorkerFaultContainmentReason.ProtocolFailure);
        var protocol =
            (ChildWorkerFaultContainmentReason.ProtocolFailure)observation.Reason;
        Assert.AreEqual(
            WorkerProtocolFailureCode.InvalidLifecycle,
            protocol.Failure.Code);
        Assert.AreEqual(
            WorkerProtocolFailureDetail.MissingTerminal,
            protocol.Failure.Detail);
        Assert.IsNull(fixture.Session.CancellationFacts);
        Assert.AreEqual(0, fixture.Process.KillCalls);

        fixture.Process.Exit(2);
        await fixture.Session.Settlement.WaitAsync(TestTimeout);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task ContainmentFirstThenStop_SharesTaskAndNeverWritesCancel()
    {
        SessionTestSupport.SessionFixture fixture = await CreateAsync(
            exitOnKill: true);
        var reason = ChildWorkerFaultContainmentReason.ReadyTimedOut.Instance;
        ChildWorkerStopRequest marker =
            ChildWorkerStopRequest.Capture(fixture.Clock);

        long timerGeneration = fixture.Clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> containment =
            fixture.Session.RequestTermination(
                new ChildWorkerTerminationRequest(
                    marker,
                    ChildWorkerTerminationIntent.FaultContainment,
                    reason));
        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();

        Assert.AreSame(containment, stop);
        await fixture.Clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        await fixture.Session
            .WaitForCancellationDeliveryAsync()
            .WaitAsync(TestTimeout);

        Assert.AreEqual(0, fixture.Input.WriteCalls);
        Assert.AreEqual(ChildWorkerCancelDeliveryPhase.InputClosed,
            fixture.Session.CancellationFacts!.DeliveryPhase);

        fixture.Clock.Advance(ChildWorkerCancellationPolicy.Grace);
        ChildWorkerCancellationResult result =
            await containment.WaitAsync(TestTimeout);

        Assert.AreEqual(
            ChildWorkerTerminationIntent.FaultContainment,
            result.Facts.FirstIntent);
        Assert.AreSame(reason, result.Facts.FirstContainmentReason);
        Assert.IsFalse(result.Facts.RequestAccepted);
        Assert.AreEqual(ChildWorkerCancelDeliveryPhase.InputClosed,
            result.Facts.DeliveryPhase);
        Assert.IsTrue(result.Facts.GraceExpired);
        Assert.AreEqual(1, fixture.Process.KillCalls);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task StopFirstThenContainment_ClosesBlockedCancelWithoutRestartingDeadline()
    {
        var input = new SessionInputStream
        {
            BlockWriteCall = 2
        };
        SessionTestSupport.SessionFixture fixture =
            await CreateAsync(input: input);
        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);

        long timerGeneration = fixture.Clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await fixture.Clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        await input.SecondWrite.WaitAsync(TestTimeout);

        var reason = ChildWorkerFaultContainmentReason.SinkFailure.Instance;
        Task<ChildWorkerCancellationResult> containment =
            fixture.Session.RequestTermination(
                new ChildWorkerTerminationRequest(
                    ChildWorkerStopRequest.Capture(fixture.Clock),
                    ChildWorkerTerminationIntent.FaultContainment,
                    reason));

        Assert.AreSame(stop, containment);
        await fixture.Session
            .WaitForCancellationDeliveryAsync()
            .WaitAsync(TestTimeout);
        Assert.AreEqual(timerGeneration + 1, fixture.Clock.TimerGeneration);
        Assert.AreEqual(1, input.Frames.Count);

        fixture.Process.Exit(3);
        ChildWorkerCancellationResult result =
            await stop.WaitAsync(TestTimeout);

        Assert.AreEqual(ChildWorkerTerminationIntent.Stop, result.Facts.FirstIntent);
        Assert.AreSame(reason, result.Facts.FirstContainmentReason);
        Assert.AreEqual(
            ChildWorkerCancelDeliveryPhase.WriteFailed,
            result.Facts.DeliveryPhase);
        Assert.IsFalse(result.Facts.GraceExpired);
        Assert.AreEqual(0, fixture.Process.KillCalls);
        AssertResourcesDisposedOnce(fixture);
        Assert.AreEqual(1, fixture.Clock.TimerDisposeCalls);
    }

    [TestMethod]
    public async Task ContainmentKillFailure_RetainsOwnershipUntilPhysicalExit()
    {
        SessionTestSupport.SessionFixture fixture = await CreateAsync(
            killOutcome: ChildProcessKillOutcome.PermissionDenied);
        var reason =
            ChildWorkerFaultContainmentReason.StandardOutputReadFailed.Instance;

        long timerGeneration = fixture.Clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> containment =
            fixture.Session.RequestTermination(
                new ChildWorkerTerminationRequest(
                    ChildWorkerStopRequest.Capture(fixture.Clock),
                    ChildWorkerTerminationIntent.FaultContainment,
                    reason));
        await fixture.Clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        await fixture.Session
            .WaitForCancellationDeliveryAsync()
            .WaitAsync(TestTimeout);

        fixture.Clock.Advance(ChildWorkerCancellationPolicy.Grace);
        await fixture.Process.KillObserved.Task.WaitAsync(TestTimeout);

        Assert.IsFalse(fixture.Session.EvidenceFinality.IsCompleted);
        Assert.IsFalse(fixture.Session.Settlement.IsCompleted);
        Assert.IsFalse(containment.IsCompleted);
        Assert.AreEqual(0, fixture.Process.DisposeCalls);
        Assert.AreEqual(
            ChildProcessKillOutcome.PermissionDenied,
            fixture.Session.CancellationFacts!.KillOutcome);

        fixture.Process.Exit(4);
        ChildWorkerCancellationResult result =
            await containment.WaitAsync(TestTimeout);

        Assert.AreEqual(
            ChildProcessKillOutcome.PermissionDenied,
            result.Facts.KillOutcome);
        Assert.AreEqual(1, fixture.Process.KillCalls);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task KnownExit_ContainmentUsesNoTimerOrKill()
    {
        SessionTestSupport.SessionFixture fixture = await CreateAsync();
        long timerGeneration = fixture.Clock.TimerGeneration;
        fixture.Process.Exit(5);

        var reason =
            ChildWorkerFaultContainmentReason.ExitObservationFailed.Instance;
        Task<ChildWorkerCancellationResult> containment =
            fixture.Session.RequestTermination(
                new ChildWorkerTerminationRequest(
                    ChildWorkerStopRequest.Capture(fixture.Clock),
                    ChildWorkerTerminationIntent.FaultContainment,
                    reason));

        ChildWorkerCancellationResult result =
            await containment.WaitAsync(TestTimeout);

        Assert.AreEqual(timerGeneration, fixture.Clock.TimerGeneration);
        Assert.AreEqual(
            ChildWorkerTerminationIntent.FaultContainment,
            result.Facts.FirstIntent);
        Assert.AreSame(reason, result.Facts.FirstContainmentReason);
        Assert.AreEqual(
            ChildWorkerCancelDeliveryPhase.AlreadyExited,
            result.Facts.DeliveryPhase);
        Assert.AreEqual(
            ChildWorkerCancellationExitRace.BeforeControl,
            result.Facts.ExitRace);
        Assert.IsFalse(result.Facts.KillAttempted);
        Assert.AreEqual(0, fixture.Process.KillCalls);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task EvidenceGate_HoldsOwnedResourcesAfterEvidenceFinality()
    {
        var gate = new ChildWorkerEvidenceFinalityGate();
        SessionTestSupport.SessionFixture fixture =
            await CreateAsync(evidenceFinalityGate: gate);

        fixture.Process.Exit(6);
        ChildWorkerCompletionObservation evidence =
            await fixture.Session.EvidenceFinality.WaitAsync(TestTimeout);

        Assert.IsTrue(evidence.ExitObserved);
        Assert.AreEqual(6, evidence.ExitCode);
        Assert.IsFalse(fixture.Session.Settlement.IsCompleted);
        Assert.AreEqual(0, fixture.Input.DisposeCalls);
        Assert.AreEqual(0, fixture.Process.DisposeCalls);

        gate.Release();
        gate.Release();
        ChildWorkerCompletionObservation settled =
            await fixture.Session.Settlement.WaitAsync(TestTimeout);

        Assert.AreSame(evidence, settled);
        AssertResourcesDisposedOnce(fixture);
    }




    [TestMethod]
    public async Task ExecuteRequestAccepted_CompletesOnlyAfterCanonicalFlush()
    {
        var input = new SessionInputStream
        {
            BlockWriteCall = 1
        };
        SessionTestSupport.SessionFixture fixture =
            await CreateAsync(input: input);
        Task accepted = fixture.Session.ExecuteRequestAccepted;

        SessionTestSupport.EmitReady(fixture);
        await input.FirstWrite.WaitAsync(TestTimeout);

        Assert.IsFalse(accepted.IsCompleted);
        Assert.AreSame(accepted, fixture.Session.ExecuteRequestAccepted);

        input.ReleaseBlockedWrite();
        await accepted.WaitAsync(TestTimeout);

        Assert.AreEqual(1, input.WriteCalls);
        Assert.AreEqual(1, input.FlushCalls);
        Assert.IsNull(fixture.Session.CancellationFacts);

        fixture.Process.Exit(10);
        await fixture.Session.Settlement.WaitAsync(TestTimeout);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task FirstTerminationRequest_RetainsExactFirstRequestAndIntent()
    {
        SessionTestSupport.SessionFixture fixture = await CreateAsync();
        fixture.Process.Exit(11);

        Task<ChildWorkerTerminationRequest> firstRequestObservation =
            fixture.Session.FirstTerminationRequest;
        var first = new ChildWorkerTerminationRequest(
            ChildWorkerStopRequest.Capture(fixture.Clock),
            ChildWorkerTerminationIntent.Shutdown);
        Task<ChildWorkerCancellationResult> firstOperation =
            fixture.Session.RequestTermination(first);
        ChildWorkerTerminationRequest observed =
            await firstRequestObservation.WaitAsync(TestTimeout);

        var reason = ChildWorkerFaultContainmentReason.SinkFailure.Instance;
        var later = new ChildWorkerTerminationRequest(
            ChildWorkerStopRequest.Capture(fixture.Clock),
            ChildWorkerTerminationIntent.FaultContainment,
            reason);
        Task<ChildWorkerCancellationResult> laterOperation =
            fixture.Session.RequestTermination(later);
        ChildWorkerCancellationResult result =
            await firstOperation.WaitAsync(TestTimeout);

        Assert.AreSame(first, observed);
        Assert.AreSame(
            firstRequestObservation,
            fixture.Session.FirstTerminationRequest);
        Assert.AreSame(firstOperation, laterOperation);
        Assert.AreEqual(
            ChildWorkerTerminationIntent.Shutdown,
            result.Facts.FirstIntent);
        Assert.AreSame(reason, result.Facts.FirstContainmentReason);
        Assert.IsTrue(fixture.Session.PhysicalExitConfirmed.IsCompletedSuccessfully);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task NaturalExit_IncompletePhaseTasksDoNotDelayRawCompletion()
    {
        SessionTestSupport.SessionFixture fixture = await CreateAsync();
        Task accepted = fixture.Session.ExecuteRequestAccepted;
        Task<ChildWorkerTerminationRequest> termination =
            fixture.Session.FirstTerminationRequest;

        fixture.Process.Exit(12);
        ChildWorkerCompletionObservation raw =
            await fixture.Session.WaitForCompletionAsync().WaitAsync(TestTimeout);
        await fixture.Session.Settlement.WaitAsync(TestTimeout);

        Assert.IsFalse(accepted.IsCompleted);
        Assert.IsFalse(termination.IsCompleted);
        Assert.IsTrue(fixture.Session.PhysicalExitConfirmed.IsCompletedSuccessfully);
        Assert.IsTrue(raw.ExitObserved);
        Assert.AreEqual(12, raw.ExitCode);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task FaultPublication_UtcFailureUsesSentinelAndPreservesRawCompletion()
    {
        var clock = new FaultPublicationTimeProvider(
            throwOnTimestamp: false);
        (
            SessionInputStream input,
            SessionTestProcess process,
            ChildWorkerSession session) = await CreateWithTimeProviderAsync(clock);
        process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(
                WorkerProtocolMapper.Ready(1, DateTimeOffset.UnixEpoch)));

        ChildWorkerStartupObservation startup =
            await session.WaitForStartupAsync().WaitAsync(TestTimeout);
        ChildWorkerTerminalPreventingObservation observation =
            await session.FirstTerminalPreventingObservation.WaitAsync(TestTimeout);

        Assert.IsTrue(
            startup is ChildWorkerStartupObservation.RequestSerializationFailed);
        Assert.IsTrue(
            observation.Reason is
                ChildWorkerFaultContainmentReason.RequestSerializationFailed);
        Assert.AreEqual(41, observation.ObservedAt.FirstStopTimestamp);
        Assert.AreEqual(DateTimeOffset.UnixEpoch,
            observation.ObservedAt.FirstStopAtUtc);
        Assert.IsTrue(observation.ObservedAt.UtcObservationFailed);
        Assert.IsNull(session.CancellationFacts);
        Assert.AreEqual(0, input.WriteCalls);

        process.Exit(8);
        Task<ChildWorkerCancellationResult> containment =
            session.RequestTermination(
                new ChildWorkerTerminationRequest(
                    observation.ObservedAt,
                    ChildWorkerTerminationIntent.FaultContainment,
                    observation.Reason));
        ChildWorkerCancellationResult result =
            await containment.WaitAsync(TestTimeout);

        Assert.IsTrue(result.Facts.FirstStopUtcObservationFailed);
        Assert.IsTrue(result.Completion.ExitObserved);
        Assert.AreEqual(8, result.Completion.ExitCode);
        Assert.AreEqual(1, process.DisposeCalls);
    }

    [TestMethod]
    public async Task FaultPublication_MonotonicFailureFaultsOnlyObservationTask()
    {
        var clock = new FaultPublicationTimeProvider(
            throwOnTimestamp: true);
        (
            SessionInputStream input,
            SessionTestProcess process,
            ChildWorkerSession session) = await CreateWithTimeProviderAsync(clock);
        process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(
                WorkerProtocolMapper.Ready(1, DateTimeOffset.UnixEpoch)));

        ChildWorkerStartupObservation startup =
            await session.WaitForStartupAsync().WaitAsync(TestTimeout);
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await session.FirstTerminalPreventingObservation
                .WaitAsync(TestTimeout));

        Assert.IsTrue(
            startup is ChildWorkerStartupObservation.RequestSerializationFailed);
        Assert.AreEqual(
            "The terminal-preventing observation timestamp was unavailable.",
            exception.Message);
        Assert.IsNull(exception.InnerException);
        Assert.AreEqual(0, input.WriteCalls);

        process.Exit(9);
        ChildWorkerCompletionObservation completion =
            await session.WaitForCompletionAsync().WaitAsync(TestTimeout);
        await session.Settlement.WaitAsync(TestTimeout);

        Assert.IsTrue(completion.ExitObserved);
        Assert.AreEqual(9, completion.ExitCode);
        Assert.AreEqual(1, process.DisposeCalls);
    }

    private static async Task<(
        SessionInputStream Input,
        SessionTestProcess Process,
        ChildWorkerSession Session)> CreateWithTimeProviderAsync(
            TimeProvider timeProvider)
    {
        var input = new SessionInputStream();
        var process = new SessionTestProcess(
            input,
            ChildProcessKillOutcome.Requested,
            exitOnKill: false);
        ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
            process,
            SessionTestSupport.CreateRequest(),
            new SessionRecordingSink(),
            new ChildWorkerLauncherOptions
            {
                TimeProvider = timeProvider,
                ReadyTimeout = Timeout.InfiniteTimeSpan
            },
            new ChildWorkerObserverArmingAcknowledgements());
        return (input, process, session);
    }

    private sealed class FaultPublicationTimeProvider(
        bool throwOnTimestamp) : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
            => throw new InvalidOperationException(
                "Synthetic UTC observation failure.");

        public override long GetTimestamp()
            => throwOnTimestamp
                ? throw new InvalidOperationException(
                    "Synthetic monotonic observation failure.")
                : 41;
    }

    [TestMethod]
    public async Task ObserverArmingFailure_DoesNotStartProcess()
    {
        var factory = new LauncherProcessFactory(null);
        var launcher = new ChildWorkerLauncher(
            factory,
            static () => throw new InvalidOperationException(
                "Synthetic observer factory failure."));

        ChildWorkerLaunchResult result = await launcher.LaunchDescriptorAsync(
            CreateDescriptor(),
            SessionTestSupport.CreateRequest(),
            new SessionRecordingSink(),
            new ChildWorkerLauncherOptions
            {
                TimeProvider = new CancellationTestClock(),
                ReadyTimeout = Timeout.InfiniteTimeSpan
            },
            CancellationToken.None);

        Assert.IsTrue(result is ChildWorkerLaunchResult.StartFailed);
        Assert.AreEqual(0, factory.StartCalls);
    }

    [TestMethod]
    public async Task HostilePostStartAccessor_ReturnsOwnedSessionAndRetainsItAfterKillFailure()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var process = new HostileAccessorProcess();
        var factory = new LauncherProcessFactory(process);
        var launcher = new ChildWorkerLauncher(factory);

        ChildWorkerLaunchResult launch = await launcher.LaunchDescriptorAsync(
            CreateDescriptor(),
            SessionTestSupport.CreateRequest(),
            new SessionRecordingSink(),
            new ChildWorkerLauncherOptions
            {
                TimeProvider = clock,
                ReadyTimeout = Timeout.InfiniteTimeSpan
            },
            CancellationToken.None);
        var started =
            Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(launch);
        ChildWorkerSession session = started.Session;

        ChildWorkerStartupObservation startup =
            await session.WaitForStartupAsync().WaitAsync(TestTimeout);
        ChildWorkerTerminalPreventingObservation observation =
            await session.FirstTerminalPreventingObservation.WaitAsync(TestTimeout);

        Assert.IsTrue(
            startup is ChildWorkerStartupObservation.PostStartSetupFailed);
        Assert.IsTrue(
            observation.Reason is
                ChildWorkerFaultContainmentReason.PostStartSetupFailed);
        Assert.AreEqual(1, factory.StartCalls);
        Assert.AreEqual(0, session.ProcessId);

        long timerGeneration = clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> containment =
            session.RequestTermination(
                new ChildWorkerTerminationRequest(
                    observation.ObservedAt,
                    ChildWorkerTerminationIntent.FaultContainment,
                    observation.Reason));
        await clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        clock.Advance(ChildWorkerCancellationPolicy.Grace);
        await process.KillObserved.Task.WaitAsync(TestTimeout);

        Assert.IsFalse(containment.IsCompleted);
        Assert.IsFalse(session.Settlement.IsCompleted);
        Assert.AreEqual(0, process.DisposeCalls);

        process.Exit(7);
        ChildWorkerCancellationResult result =
            await containment.WaitAsync(TestTimeout);

        Assert.AreEqual(
            ChildWorkerTerminationIntent.FaultContainment,
            result.Facts.FirstIntent);
        Assert.AreSame(observation.Reason, result.Facts.FirstContainmentReason);
        Assert.AreEqual(
            ChildProcessKillOutcome.PermissionDenied,
            result.Facts.KillOutcome);
        Assert.AreEqual(1, process.KillCalls);
        Assert.AreEqual(1, process.StandardOutputSource.DisposeCalls);
        Assert.AreEqual(1, process.StandardErrorSource.DisposeCalls);
        Assert.AreEqual(1, process.DisposeCalls);
    }

    private static ChildProcessStartDescriptor CreateDescriptor()
        => new(
            "fixture-worker",
            [],
            "/fixture",
            ChildProcessEnvironmentPolicy.InheritCurrent);

    private sealed class LauncherProcessFactory(IChildProcess? process)
        : IChildProcessFactory
    {
        private int _startCalls;

        internal int StartCalls => Volatile.Read(ref _startCalls);

        public ValueTask<IChildProcess?> StartAsync(
            ChildProcessStartDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCalls);
            return ValueTask.FromResult(process);
        }
    }

    private sealed class HostileAccessorProcess : IChildProcess
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exitState;
        private int _disposeCalls;
        private int _killCalls;

        public int ProcessId => throw new InvalidOperationException(
            "Synthetic process identifier failure.");
        public Stream StandardInput => throw new InvalidOperationException(
            "Synthetic standard-input failure.");
        public Stream StandardOutput => StandardOutputSource;
        public Stream StandardError => StandardErrorSource;
        internal SessionOutputStream StandardOutputSource { get; } = new();
        internal SessionOutputStream StandardErrorSource { get; } = new();
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int KillCalls => Volatile.Read(ref _killCalls);
        internal TaskCompletionSource KillObserved { get; } =
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
            return ChildProcessKillOutcome.PermissionDenied;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ValueTask.CompletedTask;
        }

        internal void Exit(int exitCode)
        {
            if (Interlocked.Exchange(ref _exitState, 1) != 0)
            {
                return;
            }

            _exit.TrySetResult(exitCode);
            StandardOutputSource.Complete();
            StandardErrorSource.Complete();
        }
    }

    private static async Task<SessionTestSupport.SessionFixture> CreateAsync(
        CancellationTestClock? clock = null,
        SessionInputStream? input = null,
        ChildProcessKillOutcome killOutcome = ChildProcessKillOutcome.Requested,
        bool exitOnKill = false,
        TimeSpan? readyTimeout = null,
        ChildWorkerEvidenceFinalityGate? evidenceFinalityGate = null)
    {
        clock ??= new CancellationTestClock(SessionTestSupport.Start);
        input ??= new SessionInputStream();
        var process = new SessionTestProcess(input, killOutcome, exitOnKill);
        var request = SessionTestSupport.CreateRequest();
        var sink = new SessionRecordingSink();
        var session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            sink,
            new ChildWorkerLauncherOptions
            {
                TimeProvider = clock,
                ReadyTimeout = readyTimeout ?? Timeout.InfiniteTimeSpan,
                EvidenceFinalityGate = evidenceFinalityGate
            },
            new ChildWorkerObserverArmingAcknowledgements());

        return new SessionTestSupport.SessionFixture(
            clock,
            input,
            process,
            request,
            sink,
            session);
    }

    private static void AssertResourcesDisposedOnce(
        SessionTestSupport.SessionFixture fixture)
    {
        Assert.AreEqual(1, fixture.Input.DisposeCalls);
        Assert.AreEqual(1, fixture.Process.StandardOutputSource.DisposeCalls);
        Assert.AreEqual(1, fixture.Process.StandardErrorSource.DisposeCalls);
        Assert.AreEqual(1, fixture.Process.DisposeCalls);
    }
}
