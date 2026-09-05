using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change28")]
public sealed class CoordinatorStopTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void StopWhileIdle_IsANoOpAndCreatesNoRunIdentity()
    {
        var executor = new GatedExecutor();
        var coordinator = CreateCoordinator(new ProcessingState(), executor);

        Assert.IsNull(coordinator.StopActiveRun());
        Assert.IsFalse(coordinator.CancelActiveRun());
        Assert.IsNull(coordinator.ActiveRequest);
        Assert.AreEqual(0, executor.InvocationCount);
    }

    [TestMethod]
    public async Task PromptStop_ConcurrentCallersShareSettlementWithoutCompletingState()
    {
        var state = new ProcessingState();
        var firstInvocation = new GatedInvocation();
        var executor = new GatedExecutor(firstInvocation);
        var coordinator = CreateCoordinator(state, executor);
        await coordinator.TriggerManualAsync();
        await firstInvocation.Entered.Task.WaitAsync(Bound);

        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        using var registration = firstInvocation.Token.Register(() =>
        {
            Interlocked.Increment(ref callbackCount);
            cancellationEntered.TrySetResult();
            releaseCancellation.Task.GetAwaiter().GetResult();
        });

        var firstStop = coordinator.StopActiveRun();
        Assert.IsNotNull(firstStop);
        try
        {
            await cancellationEntered.Task.WaitAsync(Bound);

            var repeatedStop = coordinator.StopActiveRun();
            Assert.AreSame(firstStop, repeatedStop);
            Assert.AreSame(firstStop, coordinator.WaitForActiveRunAsync());
            Assert.IsTrue(coordinator.CancelActiveRun(), "The compatibility call must join the claimed stop.");
            Assert.IsTrue(state.IsRunning, "Stop acceptance must not synthesize terminal state.");
            Assert.IsNull(state.LastRunCompleted);
            Assert.IsFalse(firstStop.IsCompleted, "Settlement still owns execution and cleanup.");
        }
        finally
        {
            releaseCancellation.TrySetResult();
            firstInvocation.Completion.TrySetException(new OperationCanceledException(firstInvocation.Token));
            await firstStop.WaitAsync(Bound);
        }

        Assert.AreEqual(1, callbackCount);
        Assert.IsFalse(state.IsRunning);
    }

    [TestMethod]
    public async Task LegacyStopFirst_StartsAttachedDeadlineAndPromptStopJoinsItsSettlement()
    {
        var clock = new CancellationTestClock(DateTimeOffset.UnixEpoch);
        var invocation = new GatedInvocation();
        var coordinator = CreateCoordinator(
            new ProcessingState(),
            new GatedExecutor(invocation),
            clock);
        await coordinator.TriggerManualAsync();
        await invocation.Entered.Task.WaitAsync(Bound);
        var request = invocation.Request!;

        var process = new SessionTestProcess(
            new SessionInputStream(),
            ChildProcessKillOutcome.Requested,
            exitOnKill: false);
        var session = await CreateSessionAsync(process, request, clock);
        Assert.IsTrue(coordinator.TryAttachChildSession(request, session));
        var timerGeneration = clock.TimerGeneration;

        var cancellationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        using var registration = invocation.Token.Register(() =>
        {
            Interlocked.Increment(ref callbackCount);
            cancellationEntered.TrySetResult();
            releaseCancellation.Task.GetAwaiter().GetResult();
        });

        var legacyStop = Task.Run(coordinator.CancelActiveRun);
        Task promptStop;
        try
        {
            await cancellationEntered.Task.WaitAsync(Bound);
            await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(Bound);

            promptStop = coordinator.StopActiveRun()
                ?? throw new AssertFailedException("The active run must accept prompt Stop.");
            Assert.AreSame(promptStop, coordinator.StopActiveRun());
            Assert.AreSame(promptStop, coordinator.WaitForActiveRunAsync());
            Assert.AreEqual(DateTimeOffset.UnixEpoch, session.CancellationFacts!.FirstStopAtUtc);

            invocation.Completion.TrySetException(
                new OperationCanceledException(invocation.Token));
            clock.Advance(ChildWorkerCancellationPolicy.Grace);
            await process.KillObserved.Task.WaitAsync(Bound);

            Assert.AreEqual(1, process.KillCalls);
            Assert.IsFalse(promptStop.IsCompleted);
            Assert.IsFalse(legacyStop.IsCompleted);

            process.Exit(130);
            await session.Settlement.WaitAsync(Bound);
            Assert.AreEqual(1, process.DisposeCalls);
            Assert.IsFalse(promptStop.IsCompleted,
                "Coordinator settlement must retain the owned cancellation source until its callback returns.");
        }
        finally
        {
            process.Exit(130);
            releaseCancellation.TrySetResult();
            invocation.Completion.TrySetException(
                new OperationCanceledException(invocation.Token));
            await session.DisposeAsync();
        }

        Assert.IsTrue(await legacyStop.WaitAsync(Bound));
        await promptStop.WaitAsync(Bound);
        Assert.AreEqual(1, callbackCount);
        Assert.AreEqual(1, process.KillCalls);
        Assert.AreEqual(1, process.DisposeCalls);
        Assert.IsNull(coordinator.ActiveRequest);
    }

    [TestMethod]
    public async Task SettledOldStop_CannotCancelOrDetachTheRetriggeredRun()
    {
        var state = new ProcessingState();
        var firstInvocation = new GatedInvocation();
        var secondInvocation = new GatedInvocation();
        var executor = new GatedExecutor(firstInvocation, secondInvocation);
        var coordinator = CreateCoordinator(state, executor);

        await coordinator.TriggerManualAsync();
        await firstInvocation.Entered.Task.WaitAsync(Bound);
        var oldStop = coordinator.StopActiveRun();
        Assert.IsNotNull(oldStop);
        firstInvocation.Completion.TrySetException(new OperationCanceledException(firstInvocation.Token));
        await oldStop.WaitAsync(Bound);

        Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await coordinator.TriggerManualAsync());
        await secondInvocation.Entered.Task.WaitAsync(Bound);
        Assert.IsFalse(secondInvocation.Token.IsCancellationRequested);
        Assert.AreNotEqual(firstInvocation.Request!.RunId, secondInvocation.Request!.RunId);
        Assert.AreSame(secondInvocation.Request, coordinator.ActiveRequest);

        secondInvocation.Completion.TrySetException(new InvalidOperationException("second-run-test-complete"));
        await coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        Assert.IsFalse(secondInvocation.Token.IsCancellationRequested);
    }

    [TestMethod]
    public async Task StopBeforeAttachment_UsesTheFirstDeadlineAndRetainsTheExactLiveSessionUntilExit()
    {
        var clock = new CancellationTestClock(DateTimeOffset.UnixEpoch);
        var state = new ProcessingState();
        var invocation = new GatedInvocation();
        var executor = new GatedExecutor(invocation);
        var coordinator = CreateCoordinator(state, executor, clock);
        await coordinator.TriggerManualAsync();
        await invocation.Entered.Task.WaitAsync(Bound);
        var request = invocation.Request!;

        var stop = coordinator.StopActiveRun();
        Assert.IsNotNull(stop);
        clock.Advance(TimeSpan.FromSeconds(4));

        var input = new SessionInputStream();
        var process = new SessionTestProcess(input, ChildProcessKillOutcome.Requested, exitOnKill: false);
        var session = await ChildWorkerSession.CreateAsync(
            process,
            request,
            new SessionRecordingSink(),
            new ChildWorkerLauncherOptions
            {
                TimeProvider = clock,
                ReadyTimeout = Timeout.InfiniteTimeSpan
            },
            new ChildWorkerObserverArmingAcknowledgements());

        try
        {
            Assert.IsTrue(coordinator.TryAttachChildSession(request, session));
            Assert.IsFalse(coordinator.TryAttachChildSession(request, session), "One handle owns one exact session generation.");
            Assert.AreEqual(DateTimeOffset.UnixEpoch, session.CancellationFacts!.FirstStopAtUtc);
            Assert.AreEqual(DateTimeOffset.UnixEpoch + ChildWorkerCancellationPolicy.Grace, session.CancellationFacts.DeadlineUtc);

            invocation.Completion.TrySetException(new OperationCanceledException(invocation.Token));
            Assert.IsFalse(stop.IsCompleted, "Executor completion cannot detach a still-live attached child.");
            Assert.AreSame(request, coordinator.ActiveRequest);
            Assert.AreEqual(0, process.DisposeCalls);

            process.Exit(130);
            await stop.WaitAsync(Bound);
            Assert.IsNull(coordinator.ActiveRequest);
            Assert.AreEqual(1, process.DisposeCalls);
            await session.DisposeAsync();
            Assert.AreEqual(1, process.DisposeCalls);
        }
        finally
        {
            process.Exit(130);
            await session.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task EquivalentSessionRequest_IsRejectedWithoutClaimingTheAttachment()
    {
        var clock = new CancellationTestClock(DateTimeOffset.UnixEpoch);
        var invocation = new GatedInvocation();
        var coordinator = CreateCoordinator(new ProcessingState(), new GatedExecutor(invocation), clock);
        await coordinator.TriggerManualAsync();
        await invocation.Entered.Task.WaitAsync(Bound);
        var request = invocation.Request!;
        var equivalentRequest = new ProcessingRunRequest(request.RunId, ProcessingRunTrigger.Scheduled);

        var rejectedProcess = new SessionTestProcess(new SessionInputStream(), ChildProcessKillOutcome.Requested, exitOnKill: false);
        var rejectedSession = await CreateSessionAsync(rejectedProcess, equivalentRequest, clock);
        var acceptedProcess = new SessionTestProcess(new SessionInputStream(), ChildProcessKillOutcome.Requested, exitOnKill: false);
        var acceptedSession = await CreateSessionAsync(acceptedProcess, request, clock);

        try
        {
            Assert.IsFalse(coordinator.TryAttachChildSession(request, rejectedSession));
            Assert.IsNull(rejectedSession.CancellationFacts);
            Assert.IsTrue(coordinator.TryAttachChildSession(request, acceptedSession), "A rejected impostor must not consume attachment ownership.");

            var stop = coordinator.StopActiveRun();
            Assert.IsNotNull(stop);
            Assert.IsNotNull(acceptedSession.CancellationFacts);
            Assert.IsNull(rejectedSession.CancellationFacts);

            invocation.Completion.TrySetException(new OperationCanceledException(invocation.Token));
            acceptedProcess.Exit(130);
            await stop.WaitAsync(Bound);
        }
        finally
        {
            rejectedProcess.Exit(0);
            acceptedProcess.Exit(130);
            await rejectedSession.DisposeAsync();
            await acceptedSession.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StopAfterCleanupOwnsTheHandle_JoinsSettlementWithoutDispatchingLateCancellation()
    {
        var invocation = new GatedInvocation();
        var cleanup = new CleanupGate();
        var coordinator = CreateCoordinator(
            new ProcessingState(),
            new GatedExecutor(invocation),
            observer: cleanup);
        await coordinator.TriggerManualAsync();
        await invocation.Entered.Task.WaitAsync(Bound);

        invocation.Completion.TrySetException(new InvalidOperationException("execution-is-already-terminal"));
        await cleanup.Entered.Task.WaitAsync(Bound);

        var stop = coordinator.StopActiveRun();
        Assert.IsNotNull(stop);
        Assert.AreSame(stop, coordinator.StopActiveRun());
        Assert.IsFalse(invocation.Token.IsCancellationRequested, "Cleanup already owns the terminal execution and must reject late cancellation dispatch.");
        Assert.IsFalse(stop.IsCompleted);

        cleanup.Release.TrySetResult();
        await stop.WaitAsync(Bound);
        Assert.IsNull(coordinator.ActiveRequest);
        Assert.IsFalse(invocation.Token.IsCancellationRequested);
    }

    private static async Task<ChildWorkerSession> CreateSessionAsync(
        SessionTestProcess process,
        ProcessingRunRequest request,
        TimeProvider clock)
    {
        return await ChildWorkerSession.CreateAsync(
            process,
            request,
            new SessionRecordingSink(),
            new ChildWorkerLauncherOptions
            {
                TimeProvider = clock,
                ReadyTimeout = Timeout.InfiniteTimeSpan
            },
            new ChildWorkerObserverArmingAcknowledgements()).ConfigureAwait(false);
    }

    private static ProcessingRunCoordinator CreateCoordinator(
        ProcessingState state,
        GatedExecutor executor,
        TimeProvider? timeProvider = null,
        IProcessingRunCoordinatorObserver? observer = null)
    {
        return new ProcessingRunCoordinator(
            state,
            new ProcessingStateEventReporter(state),
            executor,
            NullLogger<ProcessingRunCoordinator>.Instance,
            Guid.NewGuid,
            observer,
            applicationLifetime: null,
            timeProvider: timeProvider);
    }

    private sealed class GatedExecutor(params GatedInvocation[] invocations) : IProcessingRunExecutor
    {
        private readonly Queue<GatedInvocation> _invocations = new(invocations);
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<ProcessingRunResult> ExecuteAsync(
            ProcessingRunRequest request,
            IProcessingEventReporter reporter,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            var invocation = _invocations.Dequeue();
            invocation.Request = request;
            invocation.Token = cancellationToken;
            invocation.Entered.TrySetResult();
            return invocation.Completion.Task;
        }
    }

    private sealed class GatedInvocation
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ProcessingRunResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProcessingRunRequest? Request { get; set; }
        internal CancellationToken Token { get; set; }
    }

    private sealed class CleanupGate : IProcessingRunCoordinatorObserver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask BeforeDetachAsync(ProcessingRunRequest request, CancellationToken activeToken)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
    }
}
