using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.ChildWorkerCancellation;

[TestClass]
[TestCategory("Change28")]
public sealed class SessionDeadlineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task GraceExpiry_KillsExactProcessTreeOnceAndWaitsForFinality()
    {
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync(
            killOutcome: ChildProcessKillOutcome.Requested,
            exitOnKill: true);
        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);

        long timerGeneration = fixture.Clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await fixture.Clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        await fixture.Input.SecondFlush.WaitAsync(TestTimeout);

        fixture.Clock.Advance(ChildWorkerCancellationPolicy.Grace);
        await fixture.Process.KillObserved.Task.WaitAsync(TestTimeout);
        ChildWorkerCancellationResult result = await stop.WaitAsync(TestTimeout);

        Assert.IsTrue(result.Facts.GraceExpired);
        Assert.IsTrue(result.Facts.KillAttempted);
        Assert.AreEqual(ChildProcessKillOutcome.Requested, result.Facts.KillOutcome);
        Assert.AreEqual(1, fixture.Process.KillCalls);
        Assert.AreEqual(137, result.Completion.ExitCode);
        AssertResourcesDisposedOnce(fixture);
        Assert.AreEqual(0, fixture.Clock.ActiveTimerCount);
    }

    [TestMethod]
    public async Task KillFailure_RetainsOwnershipUntilLaterPhysicalExit()
    {
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync(
            killOutcome: ChildProcessKillOutcome.PermissionDenied);
        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);

        long timerGeneration = fixture.Clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await fixture.Clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        await fixture.Input.SecondFlush.WaitAsync(TestTimeout);

        fixture.Clock.Advance(ChildWorkerCancellationPolicy.Grace);
        await fixture.Process.KillObserved.Task.WaitAsync(TestTimeout);

        ChildWorkerCancellationFacts pending = fixture.Session.CancellationFacts!;
        Assert.IsTrue(pending.GraceExpired);
        Assert.IsTrue(pending.KillAttempted);
        Assert.AreEqual(ChildProcessKillOutcome.PermissionDenied, pending.KillOutcome);
        Assert.IsFalse(stop.IsCompleted);
        Assert.AreEqual(0, fixture.Process.DisposeCalls);
        Assert.AreEqual(0, fixture.Input.DisposeCalls);

        fixture.Process.Exit(143);
        ChildWorkerCancellationResult result = await stop.WaitAsync(TestTimeout);

        Assert.AreEqual(ChildProcessKillOutcome.PermissionDenied, result.Facts.KillOutcome);
        Assert.AreEqual(1, fixture.Process.KillCalls);
        Assert.AreEqual(143, result.Completion.ExitCode);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task InFlightTimerCallback_IsJoinedBeforeResourceFinality()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start)
        {
            BlockTimerCallbackAfterInvocation = true
        };
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync(
            clock: clock,
            killOutcome: ChildProcessKillOutcome.Requested,
            exitOnKill: true);
        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);

        long timerGeneration = clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(TestTimeout);
        await fixture.Input.SecondFlush.WaitAsync(TestTimeout);

        Task advance = Task.Run(() => clock.Advance(ChildWorkerCancellationPolicy.Grace));
        try
        {
            await clock.TimerCallbackInvoked.WaitAsync(TestTimeout);
            await fixture.Process.KillObserved.Task.WaitAsync(TestTimeout);

            Assert.IsFalse(stop.IsCompleted);
            Assert.AreEqual(0, fixture.Input.DisposeCalls);
            Assert.AreEqual(0, fixture.Process.StandardOutputSource.DisposeCalls);
            Assert.AreEqual(0, fixture.Process.StandardErrorSource.DisposeCalls);
            Assert.AreEqual(0, fixture.Process.DisposeCalls);
        }
        finally
        {
            clock.ReleaseTimerCallback();
        }

        await advance.WaitAsync(TestTimeout);
        await stop.WaitAsync(TestTimeout);
        Assert.AreEqual(1, clock.TimerDisposeCalls);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task BlockedExecuteWrite_DoesNotBlockDeadlineOrTreeKill()
    {
        var input = new SessionInputStream
        {
            BlockWriteCall = 1
        };
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync(
            input: input,
            killOutcome: ChildProcessKillOutcome.Requested,
            exitOnKill: false);
        SessionTestSupport.EmitReady(fixture);
        await input.FirstWrite.WaitAsync(TestTimeout);

        long timerGeneration = fixture.Clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await fixture.Clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);

        fixture.Clock.Advance(ChildWorkerCancellationPolicy.Grace);
        await fixture.Process.KillObserved.Task.WaitAsync(TestTimeout);
        await fixture.Session.WaitForCancellationDeliveryAsync().WaitAsync(TestTimeout);
        Assert.IsFalse(stop.IsCompleted, "A kill request does not prove physical exit.");
        Assert.AreEqual(0, fixture.Process.DisposeCalls);
        fixture.Process.Exit(137);
        ChildWorkerCancellationResult result = await stop.WaitAsync(TestTimeout);

        Assert.IsTrue(result.Facts.GraceExpired);
        Assert.IsFalse(result.Facts.RequestAccepted);
        Assert.AreEqual(ChildWorkerCancelDeliveryPhase.DeadlineElapsed, result.Facts.DeliveryPhase);
        Assert.AreEqual(1, fixture.Process.KillCalls);
        Assert.AreEqual(0, fixture.Input.Frames.Count, "blocked execute never reached stdin");
        AssertResourcesDisposedOnce(fixture);
        Assert.AreEqual(0, fixture.Clock.ActiveTimerCount);
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
