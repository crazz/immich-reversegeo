using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.ChildWorkerCancellation;

[TestClass]
[TestCategory("Change28")]
public sealed class SessionLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task NaturalExit_SettlesAndRepeatedDisposeReusesCleanup()
    {
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync();

        fixture.Process.Exit(0);
        ChildWorkerCompletionObservation raw =
            await fixture.Session.WaitForCompletionAsync().WaitAsync(TestTimeout);
        ChildWorkerCompletionObservation settled =
            await fixture.Session.Settlement.WaitAsync(TestTimeout);

        Assert.AreSame(raw, settled);
        Assert.IsTrue(raw.ExitObserved);
        Assert.AreEqual(0, raw.ExitCode);
        AssertResourcesDisposedOnce(fixture);

        await fixture.Session.DisposeAsync();
        await fixture.Session.DisposeAsync();

        AssertResourcesDisposedOnce(fixture);
        Assert.AreEqual(0, fixture.Process.KillCalls);
        Assert.AreEqual(0L, fixture.Clock.TimerGeneration);
        Assert.AreEqual(0, fixture.Clock.ActiveTimerCount);
    }

    [TestMethod]
    public async Task TerminalWhileAdmittedCancelWriteBlocked_CompletesCanonicalCancelBeforeOneInputClose()
    {
        var input = new SessionInputStream { BlockWriteCall = 2 };
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync(input: input);
        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);

        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await input.SecondWrite.WaitAsync(TestTimeout);

        SessionTestSupport.EmitCompletedTerminal(fixture);
        await fixture.Sink.TerminalAccepted.WaitAsync(TestTimeout);

        Assert.AreEqual(0, input.DisposeCalls, "terminal-race: admitted-cancel-write-retains-writer-before-close");
        Assert.IsFalse(stop.IsCompleted, "terminal-race: terminal-does-not-cancel-admitted-write");

        input.ReleaseBlockedWrite();
        await input.SecondFlush.WaitAsync(TestTimeout);
        await input.DisposeStarted.WaitAsync(TestTimeout);

        Assert.AreEqual(2, input.Frames.Count, "terminal-race: canonical-execute-and-cancel-both-line-terminated");
        Assert.AreEqual(2, input.FlushCalls, "terminal-race: canonical-cancel-flushes-before-close");
        Assert.AreEqual(1, input.DisposeCalls, "terminal-race: one-physical-close");

        fixture.Process.Exit(0);
        ChildWorkerCancellationResult result = await stop.WaitAsync(TestTimeout);

        Assert.AreEqual(ChildWorkerCancelDeliveryPhase.Flushed, result.Facts.DeliveryPhase, "terminal-race: normal-terminal-does-not-cancel-admitted-input-token");
        Assert.AreEqual(0, fixture.Process.KillCalls, "terminal-race: no-terminal-owned-kill");
        Assert.AreEqual(WorkerProtocolV1.CompletedType, result.Completion.Terminal!.Type, "terminal-race: accepted-terminal-preserved");
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task CancelAfterAcceptedTerminal_UsesInputClosedPhaseWithoutExtraWrite()
    {
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync();
        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);
        SessionTestSupport.EmitCompletedTerminal(fixture);
        await fixture.Sink.TerminalAccepted.WaitAsync(TestTimeout);
        await fixture.Input.DisposeStarted.WaitAsync(TestTimeout);

        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await fixture.Session.WaitForCancellationDeliveryAsync().WaitAsync(TestTimeout);

        Assert.AreEqual(ChildWorkerCancelDeliveryPhase.InputClosed, fixture.Session.CancellationFacts!.DeliveryPhase, "post-terminal-cancel: input-closed-phase");
        Assert.AreEqual(1, fixture.Input.WriteCalls, "post-terminal-cancel: no-extra-cancel-bytes");
        Assert.AreEqual(1, fixture.Input.FlushCalls, "post-terminal-cancel: no-extra-cancel-flush");
        Assert.AreEqual(1, fixture.Input.DisposeCalls, "post-terminal-cancel: one-terminal-owned-close");

        fixture.Process.Exit(0);
        ChildWorkerCancellationResult result = await stop.WaitAsync(TestTimeout);

        Assert.AreEqual(0, fixture.Process.KillCalls, "post-terminal-cancel: no-kill");
        Assert.AreEqual(WorkerProtocolV1.CompletedType, result.Completion.Terminal!.Type, "post-terminal-cancel: terminal-preserved");
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task FaultedExitObserver_LeavesRawCompletionObservableWhileStopOwnsLiveProcess()
    {
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync(
            killOutcome: ChildProcessKillOutcome.Requested,
            exitOnKill: true);
        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);

        fixture.Process.FailExitObservation();
        fixture.Process.CompleteStreams();

        ChildWorkerCompletionObservation raw =
            await fixture.Session.WaitForCompletionAsync().WaitAsync(TestTimeout);
        Assert.IsFalse(raw.ExitObserved);
        Assert.IsNull(raw.ExitCode);
        Assert.IsFalse(fixture.Session.Settlement.IsCompleted);
        Assert.AreEqual(0, fixture.Process.DisposeCalls);

        long timerGeneration = fixture.Clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await fixture.Clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        Assert.IsFalse(stop.IsCompleted);
        Assert.AreSame(raw, await fixture.Session.WaitForCompletionAsync().WaitAsync(TestTimeout));

        fixture.Clock.Advance(ChildWorkerCancellationPolicy.Grace);

        ChildWorkerCancellationResult result = await stop.WaitAsync(TestTimeout);
        Assert.AreSame(raw, result.Completion);
        Assert.IsTrue(result.Facts.GraceExpired);
        Assert.IsTrue(result.Facts.KillAttempted);
        Assert.AreEqual(ChildProcessKillOutcome.Requested, result.Facts.KillOutcome);
        Assert.AreEqual(1, fixture.Process.KillCalls);
        AssertResourcesDisposedOnce(fixture);
    }

    private static void AssertResourcesDisposedOnce(
        SessionTestSupport.SessionFixture fixture)
    {
        Assert.AreEqual(1, fixture.Input.DisposeCalls, "stdin dispose");
        Assert.AreEqual(1, fixture.Process.StandardOutputSource.DisposeCalls, "stdout dispose");
        Assert.AreEqual(1, fixture.Process.StandardErrorSource.DisposeCalls, "stderr dispose");
        Assert.AreEqual(1, fixture.Process.DisposeCalls, "process dispose");
    }
}
