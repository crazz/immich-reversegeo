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
