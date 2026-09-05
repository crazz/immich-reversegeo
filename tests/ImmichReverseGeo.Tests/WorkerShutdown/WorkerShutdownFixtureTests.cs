using System.Globalization;
using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerShutdown;

[TestClass]
[TestCategory("Change29")]
public sealed class WorkerShutdownFixtureTests
{
    private const int StandardErrorFloodBytes = 262_177;

    [TestMethod]
    public async Task CooperativeWorker_ShutdownSharesHostWaitAndPreservesCancelledTerminal()
    {
        var clock = new CancellationTestClock();
        var context = await FixtureCoordinatorContext.CreateAsync(clock);
        var run = await FixtureRun.AttachAsync(context, clock, "cooperative-cancel");

        await using (run)
        {
            await run.Sink.WaitForMarkerAsync("cooperative-cancel");
            var timerGeneration = clock.TimerGeneration;

            var shutdown = context.Coordinator.BeginShutdown();
            Assert.AreSame(shutdown, context.Coordinator.BeginShutdown());

            using var cancelledHostWait = new CancellationTokenSource();
            cancelledHostWait.Cancel();
            Assert.AreSame(
                shutdown,
                context.Coordinator.StopAsync(cancelledHostWait.Token),
                "The host token must not replace or cancel the owned shutdown task.");

            await context.Invocation.CancellationObserved.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(WorkerProcessFixtureLease.Watchdog);
            context.Invocation.CompleteAsCancelled();

            var completion = await run.Session.Completion.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await shutdown.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            var facts = run.Session.CancellationFacts
                ?? throw new AssertFailedException("Shutdown must expose the exact session cancellation facts.");
            Assert.IsTrue(facts.RequestAccepted);
            Assert.IsFalse(facts.GraceExpired);
            Assert.IsFalse(facts.KillAttempted);
            Assert.IsNull(facts.KillOutcome);
            Assert.AreEqual(0, run.Lease.TreeKillCalls);
            Assert.AreEqual(WorkerProtocolV1.CancelledType, completion.Terminal?.Type);
            Assert.AreEqual(130, completion.ExitCode);
            Assert.IsTrue(run.Bridge.IsTerminal);
            Assert.IsNull(run.Bridge.FirstObservation);
            Assert.IsFalse(context.State.IsRunning);
            Assert.IsNotNull(context.State.LastRunCompleted);
            Assert.IsNull(context.State.LastError);
            AssertRawFinality(run.Lease, completion);
            AssertControllerInput(run.Lease, WorkerProtocolV1.ExecuteType, WorkerProtocolV1.CancelType);
            await AssertStoppedControlPlaneAsync(context);
        }

        AssertReleasedWithoutEmergencyCleanup(run);
    }

    [TestMethod]
    public async Task UnresponsiveWorker_ShutdownUsesOneGraceDeadlineAndOneTreeKill()
    {
        var clock = new CancellationTestClock();
        var context = await FixtureCoordinatorContext.CreateAsync(clock);
        var run = await FixtureRun.AttachAsync(context, clock, "unresponsive");

        await using (run)
        {
            await run.Sink.WaitForMarkerAsync("unresponsive");
            var timerGeneration = clock.TimerGeneration;
            var shutdown = context.Coordinator.BeginShutdown();
            await context.Invocation.CancellationObserved.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await run.Sink.WaitForMarkerAsync("cancel-observed");
            context.Invocation.CompleteAsCancelled();

            Assert.IsFalse(shutdown.IsCompleted);
            Assert.AreSame(context.Request, context.Coordinator.ActiveRequest);
            Assert.AreEqual(0, run.Lease.ProcessDisposeCalls);

            clock.Advance(ChildWorkerCancellationPolicy.Grace);
            var killOutcome = await run.Lease.TreeKillObserved.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.AreEqual(ChildProcessKillOutcome.Requested, killOutcome);

            var completion = await run.Session.Completion.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await shutdown.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            var facts = run.Session.CancellationFacts
                ?? throw new AssertFailedException("Shutdown must expose the exact session cancellation facts.");
            Assert.AreEqual(
                ChildWorkerCancellationPolicy.Grace,
                facts.DeadlineUtc - facts.FirstStopAtUtc);
            Assert.IsTrue(facts.GraceExpired);
            Assert.IsTrue(facts.KillAttempted);
            Assert.AreEqual(ChildProcessKillOutcome.Requested, facts.KillOutcome);
            Assert.AreEqual(1, run.Lease.TreeKillCalls);
            Assert.IsNull(completion.Terminal);
            Assert.IsNotNull(completion.ExitCode);
            Assert.IsFalse(run.Bridge.IsTerminal);
            Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.NonterminalDisposed>(
                run.Bridge.FirstObservation);
            Assert.IsNull(context.State.LastRunCompleted);
            Assert.IsNull(context.State.LastError);
            AssertRawFinality(run.Lease, completion);
            AssertControllerInput(run.Lease, WorkerProtocolV1.ExecuteType, WorkerProtocolV1.CancelType);
            Assert.AreEqual(0, clock.ActiveTimerCount);
            await AssertStoppedControlPlaneAsync(context);
        }

        AssertReleasedWithoutEmergencyCleanup(run);
    }

    [TestMethod]
    public async Task CompletedStderrFlood_ShutdownPreservesTerminalAndExactTrailingDrain()
    {
        var clock = new CancellationTestClock();
        var settlementObserver = new ChildSettlementGate();
        var context = await FixtureCoordinatorContext.CreateAsync(clock, settlementObserver);
        var run = await FixtureRun.AttachAsync(
            context,
            clock,
            "stderr-flood",
            null,
            "stderr-flood",
            "--stderr-bytes",
            StandardErrorFloodBytes.ToString(CultureInfo.InvariantCulture));

        await using (run)
        {
            try
            {
                await run.Sink.GatedMarkerEntered.WaitAsync(
                    WorkerProcessFixtureLease.Watchdog);
                Assert.AreEqual("fixture-activity", context.State.CurrentActivity);

                var shutdown = context.Coordinator.BeginShutdown();
                await context.Invocation.CancellationObserved.Task.WaitAsync(
                    WorkerProcessFixtureLease.Watchdog);
                context.Invocation.CompleteAsCancelled();
                await settlementObserver.Entered.WaitAsync(
                    WorkerProcessFixtureLease.Watchdog);

                Assert.AreEqual(
                    "fixture-activity",
                    context.State.CurrentActivity,
                    "Shutdown-owned failure cleanup must retain projected activity until child settlement.");
                Assert.IsFalse(shutdown.IsCompleted,
                    "Shutdown must retain the session while accepted output is still draining.");
                Assert.AreEqual(0, run.Lease.ProcessDisposeCalls);

                settlementObserver.Release();
                run.Sink.ReleaseGatedMarker();
                var completion = await run.Session.Completion.WaitAsync(
                    WorkerProcessFixtureLease.Watchdog);
                await shutdown.WaitAsync(WorkerProcessFixtureLease.Watchdog);

                Assert.AreEqual(0, run.Lease.TreeKillCalls);
                Assert.AreEqual(WorkerProtocolV1.CompletedType, completion.Terminal?.Type);
                Assert.AreEqual(0, completion.ExitCode);
                Assert.IsTrue(run.Bridge.IsTerminal);
                Assert.IsNull(run.Bridge.FirstObservation);
                Assert.IsFalse(context.State.IsRunning);
                Assert.IsNull(context.State.CurrentActivity);
                Assert.IsNotNull(context.State.LastRunCompleted);
                Assert.IsNull(context.State.LastError);
                Assert.AreEqual(1L, context.State.ProcessedThisRun);
                AssertExactStandardErrorTail(completion);
                AssertRawFinality(run.Lease, completion);
                AssertControllerInputWithOptionalCancel(run.Lease);
                await AssertStoppedControlPlaneAsync(context);
            }
            finally
            {
                settlementObserver.Release();
                run.Sink.ReleaseGatedMarker();
            }
        }

        AssertReleasedWithoutEmergencyCleanup(run);
    }

    [TestMethod]
    public async Task SessionStartedAfterShutdownCapture_JoinsOriginalDeadlineAndExactHandle()
    {
        var clock = new CancellationTestClock(DateTimeOffset.UnixEpoch);
        var context = await FixtureCoordinatorContext.CreateAsync(clock);

        var shutdown = context.Coordinator.BeginShutdown();
        await context.Invocation.CancellationObserved.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        Assert.AreSame(context.Request, context.Coordinator.ActiveRequest);
        clock.Advance(TimeSpan.FromSeconds(4));

        var run = await FixtureRun.AttachAsync(context, clock, "cooperative-cancel");
        await using (run)
        {
            await run.Sink.WaitForMarkerAsync("cooperative-cancel");
            var completion = await run.Session.Completion.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            context.Invocation.CompleteAsCancelled();
            await shutdown.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            var facts = run.Session.CancellationFacts
                ?? throw new AssertFailedException("The late-published session must join the captured Stop.");
            Assert.AreEqual(DateTimeOffset.UnixEpoch, facts.FirstStopAtUtc);
            Assert.AreEqual(
                DateTimeOffset.UnixEpoch + ChildWorkerCancellationPolicy.Grace,
                facts.DeadlineUtc);
            Assert.IsFalse(facts.GraceExpired);
            Assert.IsFalse(facts.KillAttempted);
            Assert.AreEqual(0, run.Lease.TreeKillCalls);
            Assert.AreEqual(WorkerProtocolV1.CancelledType, completion.Terminal?.Type);
            Assert.AreEqual(130, completion.ExitCode);
            Assert.AreSame(shutdown, context.Coordinator.BeginShutdown());
            AssertRawFinality(run.Lease, completion);
            AssertControllerInput(run.Lease, WorkerProtocolV1.ExecuteType, WorkerProtocolV1.CancelType);
            await AssertStoppedControlPlaneAsync(context);
        }

        AssertReleasedWithoutEmergencyCleanup(run);
    }

    [TestMethod]
    public async Task InjectedPermissionDenied_RetainsLiveFixtureOwnershipUntilEmergencyReap()
    {
        var clock = new CancellationTestClock();
        var context = await FixtureCoordinatorContext.CreateAsync(clock);
        var run = await FixtureRun.AttachAsync(
            context,
            clock,
            "unresponsive",
            ChildProcessKillOutcome.PermissionDenied);

        Task shutdown;
        await using (run)
        {
            await run.Sink.WaitForMarkerAsync("unresponsive");
            var timerGeneration = clock.TimerGeneration;
            shutdown = context.Coordinator.BeginShutdown();
            await context.Invocation.CancellationObserved.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await run.Sink.WaitForMarkerAsync("cancel-observed");
            await run.Session.WaitForCancellationDeliveryAsync();
            context.Invocation.CompleteAsCancelled();

            clock.Advance(ChildWorkerCancellationPolicy.Grace);
            var killOutcome = await run.Lease.TreeKillObserved.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.AreEqual(ChildProcessKillOutcome.PermissionDenied, killOutcome);
            Assert.AreEqual(1, run.Lease.TreeKillCalls);
            Assert.IsFalse(run.Lease.HasExited);
            Assert.IsFalse(run.Session.Settlement.IsCompleted);
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.AreSame(context.Request, context.Coordinator.ActiveRequest);
            Assert.AreEqual(0, run.Lease.ProcessDisposeCalls);
            Assert.IsFalse(run.Lease.ForcedCleanup);
            Assert.IsFalse(run.Bridge.IsTerminal);
            Assert.IsNull(context.State.LastRunCompleted);
            Assert.IsNull(context.State.LastError);
        }

        await shutdown.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        var facts = run.Session.CancellationFacts
            ?? throw new AssertFailedException("The failed platform escalation fact must remain observable.");
        Assert.AreEqual(
            ChildWorkerCancellationPolicy.Grace,
            facts.DeadlineUtc - facts.FirstStopAtUtc);
        Assert.IsTrue(facts.GraceExpired);
        Assert.IsTrue(facts.KillAttempted);
        Assert.AreEqual(ChildProcessKillOutcome.PermissionDenied, facts.KillOutcome);
        Assert.IsTrue(run.Lease.ForcedCleanup,
            "Only the fixture lease's exact native-process reaper may end this injected failure case.");
        Assert.AreEqual(1, run.Lease.TreeKillCalls);
        Assert.AreEqual(1, run.Lease.ProcessDisposeCalls);
        Assert.IsTrue(run.Lease.HasExited);
        Assert.AreEqual(0, clock.ActiveTimerCount);
        Assert.IsFalse(run.Lease.IsRegistered);
        Assert.IsFalse(Directory.Exists(run.Lease.Root));
        Assert.IsNull(context.Coordinator.ActiveRequest);
        Assert.IsNull(context.State.LastRunCompleted);
        Assert.IsNull(context.State.LastError);
        Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.NonterminalDisposed>(
            run.Bridge.FirstObservation);
    }

    private static async Task AssertStoppedControlPlaneAsync(FixtureCoordinatorContext context)
    {
        Assert.IsNull(context.Coordinator.ActiveRequest);
        Assert.AreEqual(
            ProcessingRunAdmissionResult.Stopping,
            await context.Coordinator.TriggerManualAsync());
        Assert.AreEqual(1, context.Executor.InvocationCount);
    }

    private static void AssertRawFinality(
        WorkerProcessFixtureLease lease,
        ChildWorkerCompletionObservation completion)
    {
        Assert.IsTrue(lease.HasExited, "The exact fixture process must have exited.");
        Assert.IsTrue(completion.ExitObserved);
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
            completion.StandardOutputFinality);
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(
            completion.StandardErrorFinality);
        Assert.AreEqual(lease.Request.RunId, completion.RunId);
        Assert.AreEqual(lease.ProcessId, completion.ProcessId);
        Assert.AreEqual(1, lease.ProcessDisposeCalls);
        Assert.IsFalse(lease.ForcedCleanup,
            "Successful shutdown must finish before the fixture lease's emergency cleanup.");
    }

    private static void AssertControllerInput(
        WorkerProcessFixtureLease lease,
        params string[] expectedTypes)
    {
        var lines = Encoding.UTF8
            .GetString(lease.WrittenInput.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(expectedTypes.Length, lines.Length);

        for (var index = 0; index < lines.Length; index++)
        {
            var parsed = WorkerProtocolCodec.ParseControllerInput(
                Encoding.UTF8.GetBytes(lines[index]));
            Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
            Assert.AreEqual(index + 1L, parsed.Message!.Sequence);
            Assert.AreEqual(lease.Request.RunId, parsed.Message.RunId);
            Assert.AreEqual(expectedTypes[index], parsed.Message.Type);
        }
    }

    private static void AssertControllerInputWithOptionalCancel(
        WorkerProcessFixtureLease lease)
    {
        var lines = Encoding.UTF8
            .GetString(lease.WrittenInput.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsTrue(lines.Length is 1 or 2,
            "The terminal/exit race may suppress cancel but must never duplicate it.");

        for (var index = 0; index < lines.Length; index++)
        {
            var parsed = WorkerProtocolCodec.ParseControllerInput(
                Encoding.UTF8.GetBytes(lines[index]));
            Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
            Assert.AreEqual(index + 1L, parsed.Message!.Sequence);
            Assert.AreEqual(lease.Request.RunId, parsed.Message.RunId);
            Assert.AreEqual(
                index == 0 ? WorkerProtocolV1.ExecuteType : WorkerProtocolV1.CancelType,
                parsed.Message.Type);
        }
    }

    private static void AssertExactStandardErrorTail(
        ChildWorkerCompletionObservation completion)
    {
        var prefix = Encoding.ASCII.GetBytes("fixture-stderr-prefix\n");
        var suffix = Encoding.ASCII.GetBytes("\nfixture-stderr-suffix\n");
        var bodyLength = StandardErrorFloodBytes - prefix.Length - suffix.Length;
        var expected = new byte[65_536];

        for (var index = 0; index < expected.Length; index++)
        {
            var position = StandardErrorFloodBytes - expected.Length + index;
            expected[index] = position < prefix.Length + bodyLength
                ? (byte)('a' + (position - prefix.Length) % 26)
                : suffix[position - prefix.Length - bodyLength];
        }

        Assert.AreEqual(StandardErrorFloodBytes, completion.StandardErrorTail.TotalBytes);
        Assert.IsTrue(completion.StandardErrorTail.IsTruncated);
        Assert.IsFalse(completion.StandardErrorTail.TotalBytesSaturated);
        CollectionAssert.AreEqual(expected, completion.StandardErrorTail.Bytes.ToArray());
    }

    private static void AssertReleasedWithoutEmergencyCleanup(FixtureRun run)
    {
        Assert.IsFalse(run.Lease.IsRegistered);
        Assert.IsFalse(Directory.Exists(run.Lease.Root));
        Assert.AreEqual(1, run.Lease.ProcessDisposeCalls);
        Assert.IsFalse(run.Lease.ForcedCleanup);
    }

    private sealed class FixtureCoordinatorContext
    {
        private FixtureCoordinatorContext(
            ProcessingState state,
            ProcessingStateEventReporter reporter,
            FixtureExecutor executor,
            FixtureInvocation invocation,
            ProcessingRunCoordinator coordinator)
        {
            State = state;
            Reporter = reporter;
            Executor = executor;
            Invocation = invocation;
            Coordinator = coordinator;
        }

        internal ProcessingState State { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal FixtureExecutor Executor { get; }
        internal FixtureInvocation Invocation { get; }
        internal ProcessingRunCoordinator Coordinator { get; }
        internal ProcessingRunRequest Request
            => Invocation.Request
                ?? throw new AssertFailedException("The coordinator did not publish its admitted request.");

        internal static async Task<FixtureCoordinatorContext> CreateAsync(
            TimeProvider clock,
            IProcessingRunCoordinatorObserver? observer = null)
        {
            var state = new ProcessingState();
            var reporter = new ProcessingStateEventReporter(state);
            var invocation = new FixtureInvocation();
            var executor = new FixtureExecutor(invocation);
            var coordinator = new ProcessingRunCoordinator(
                state,
                reporter,
                executor,
                NullLogger<ProcessingRunCoordinator>.Instance,
                Guid.NewGuid,
                observer,
                applicationLifetime: null,
                timeProvider: clock);

            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await coordinator.TriggerManualAsync());
            await invocation.Entered.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.AreSame(invocation.Request, coordinator.ActiveRequest);
            return new FixtureCoordinatorContext(
                state,
                reporter,
                executor,
                invocation,
                coordinator);
        }
    }

    private sealed class ChildSettlementGate : IProcessingRunCoordinatorObserver
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        public ValueTask BeforeChildSettlementAsync(
            ProcessingRunRequest request,
            CancellationToken activeToken)
        {
            _entered.TrySetResult();
            return new ValueTask(_release.Task);
        }

        internal void Release() => _release.TrySetResult();
    }

    private sealed class FixtureExecutor(FixtureInvocation invocation) : IProcessingRunExecutor
    {
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            invocation.Request = request;
            invocation.Token = cancellationToken;
            _ = cancellationToken.Register(
                static state => ((FixtureInvocation)state!).CancellationObserved.TrySetResult(),
                invocation);
            invocation.Entered.TrySetResult();
            return invocation.Completion.Task;
        }
    }

    private sealed class FixtureInvocation
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } =
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

    private sealed class RecordingBridgeSink(
        ProcessingRunRequest request,
        WorkerEventStateBridge bridge) : IWorkerProtocolEventSink
    {
        private readonly FixtureEventSink _recording = new();
        private readonly TaskCompletionSource _gatedMarkerEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gatedMarkerRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _gatedMarker;

        internal Task GatedMarkerEntered => _gatedMarkerEntered.Task;

        internal void GateMarker(string marker)
        {
            _gatedMarker = $"fixture:{marker}:{request.RunId:D}";
        }

        internal void ReleaseGatedMarker()
        {
            _gatedMarkerRelease.TrySetResult();
        }

        internal Task<WorkerProtocolEvent> WaitForMarkerAsync(string marker)
        {
            return _recording.WaitForAsync(@event =>
                @event.Payload is LogEmittedPayload log
                && log.Message == $"fixture:{marker}:{request.RunId:D}");
        }

        public async ValueTask AcceptAsync(
            WorkerProtocolEvent @event,
            CancellationToken cancellationToken)
        {
            if (@event.Payload is LogEmittedPayload log
                && log.Message == _gatedMarker)
            {
                _gatedMarkerEntered.TrySetResult();
                await _gatedMarkerRelease.Task.ConfigureAwait(false);
            }

            await bridge.AcceptAsync(@event, cancellationToken);
            await _recording.AcceptAsync(@event, cancellationToken);
        }
    }

    private sealed class FixtureRun : IAsyncDisposable
    {
        private int _disposeStarted;

        private FixtureRun(
            FixtureCoordinatorContext context,
            WorkerProcessFixtureLease lease,
            ChildWorkerSession session,
            WorkerEventStateBridge bridge,
            RecordingBridgeSink sink)
        {
            Context = context;
            Lease = lease;
            Session = session;
            Bridge = bridge;
            Sink = sink;
        }

        internal FixtureCoordinatorContext Context { get; }
        internal WorkerProcessFixtureLease Lease { get; }
        internal ChildWorkerSession Session { get; }
        internal WorkerEventStateBridge Bridge { get; }
        internal RecordingBridgeSink Sink { get; }

        internal static async Task<FixtureRun> AttachAsync(
            FixtureCoordinatorContext context,
            CancellationTestClock clock,
            string scenario,
            ChildProcessKillOutcome? killOutcomeOverride = null,
            string? gatedMarker = null,
            params string[] options)
        {
            var bridge = new WorkerEventStateBridgeFactory(context.Reporter)
                .Create(context.Request);
            var sink = new RecordingBridgeSink(context.Request, bridge);
            if (gatedMarker is not null)
            {
                sink.GateMarker(gatedMarker);
            }

            var lease = new WorkerProcessFixtureLease
            {
                Request = context.Request,
                LauncherOptions = new ChildWorkerLauncherOptions
                {
                    TimeProvider = clock
                },
                TreeKillOutcomeOverride = killOutcomeOverride
            };

            try
            {
                var session = await lease.LaunchAsync(
                    scenario,
                    sink,
                    capture: true,
                    options);
                Assert.IsTrue(
                    context.Coordinator.TryAttachChildSession(
                        context.Request,
                        session,
                        bridge),
                    "The exact successfully started fixture session must attach to its captured handle.");
                return new FixtureRun(context, lease, session, bridge, sink);
            }
            catch
            {
                var shutdown = context.Coordinator.BeginShutdown();
                context.Invocation.CompleteAsCancelled();
                sink.ReleaseGatedMarker();
                await lease.DisposeAsync().AsTask().WaitAsync(
                    WorkerProcessFixtureLease.Watchdog);
                await bridge.DisposeAsync();
                await shutdown.WaitAsync(WorkerProcessFixtureLease.Watchdog);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            var shutdown = Context.Coordinator.BeginShutdown();
            Context.Invocation.CompleteAsCancelled();
            Sink.ReleaseGatedMarker();
            await Lease.DisposeAsync().AsTask().WaitAsync(
                WorkerProcessFixtureLease.Watchdog);
            await shutdown.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await Bridge.DisposeAsync();
        }
    }
}
