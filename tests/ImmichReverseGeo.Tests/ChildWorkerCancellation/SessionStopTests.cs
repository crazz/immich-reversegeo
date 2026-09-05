using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.ChildWorkerCancellation;

[TestClass]
[TestCategory("Change28")]
public sealed class SessionStopTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task RepeatedStop_AfterAcceptedExecute_SharesOneCancelAndSettlement()
    {
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync();
        SessionTestSupport.EmitReady(fixture);
        ChildWorkerStartupObservation startup =
            await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);
        Assert.IsTrue(startup is ChildWorkerStartupObservation.ReadyAccepted);

        long timerGeneration = fixture.Clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> first = fixture.Session.RequestStop();
        Task<ChildWorkerCancellationResult> second = fixture.Session.RequestStop();

        Assert.AreSame(first, second, "one exact-session Stop task");
        await fixture.Clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        await fixture.Input.SecondFlush.WaitAsync(TestTimeout);

        AssertCanonicalExecuteAndCancel(fixture);

        fixture.Process.Exit(130);
        ChildWorkerCancellationResult result = await first.WaitAsync(TestTimeout);

        Assert.AreEqual(SessionTestSupport.Start, result.Facts.FirstStopAtUtc);
        Assert.AreEqual(
            SessionTestSupport.Start + ChildWorkerCancellationPolicy.Grace,
            result.Facts.DeadlineUtc);
        Assert.IsTrue(result.Facts.RequestAccepted);
        Assert.AreEqual(ChildWorkerCancelDeliveryPhase.Flushed, result.Facts.DeliveryPhase);
        Assert.AreEqual(ChildWorkerCancellationExitRace.None, result.Facts.ExitRace);
        Assert.IsFalse(result.Facts.GraceExpired);
        Assert.IsFalse(result.Facts.KillAttempted);
        Assert.IsNull(result.Facts.KillOutcome);
        Assert.IsTrue(result.Completion.ExitObserved);
        Assert.AreEqual(130, result.Completion.ExitCode);

        await fixture.Session.DisposeAsync();
        await fixture.Session.DisposeAsync();
        AssertResourcesDisposedOnce(fixture);
        Assert.AreEqual(0, fixture.Clock.ActiveTimerCount);
        Assert.AreEqual(1, fixture.Clock.TimerDisposeCalls);
    }

    [TestMethod]
    public async Task StopBeforeReady_LatchesUntilExecuteFlushThenSendsOneCancel()
    {
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync();
        long timerGeneration = fixture.Clock.TimerGeneration;

        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await fixture.Clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);

        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        SessionTestSupport.EmitReady(fixture);
        await fixture.Input.SecondFlush.WaitAsync(TestTimeout);

        AssertCanonicalExecuteAndCancel(fixture);
        fixture.Process.Exit(130);

        ChildWorkerCancellationResult result = await stop.WaitAsync(TestTimeout);
        Assert.AreEqual(SessionTestSupport.Start, result.Facts.FirstStopAtUtc);
        Assert.AreEqual(
            SessionTestSupport.Start + ChildWorkerCancellationPolicy.Grace,
            result.Facts.DeadlineUtc);
        Assert.AreEqual(ChildWorkerCancelDeliveryPhase.Flushed, result.Facts.DeliveryPhase);
        Assert.IsFalse(result.Facts.GraceExpired);
        Assert.AreEqual(0, fixture.Process.KillCalls);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task RequestStop_ReturnsBeforeSynchronousWriteAndDeadlineStillKills()
    {
        var input = new SessionInputStream
        {
            SynchronousBlockWriteCall = 2
        };
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync(
            input: input,
            killOutcome: ChildProcessKillOutcome.Requested,
            exitOnKill: true);
        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);

        long timerGeneration = fixture.Clock.TimerGeneration;
        Task<Task<ChildWorkerCancellationResult>> invocation = Task.Factory.StartNew(
            () => fixture.Session.RequestStop(),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        Task<ChildWorkerCancellationResult> stop;
        try
        {
            stop = await invocation.WaitAsync(TestTimeout);
            await fixture.Clock
                .WaitForTimerCreatedAsync(timerGeneration)
                .WaitAsync(TestTimeout);
            await input.SynchronousWriteEntered.WaitAsync(TestTimeout);

            Assert.IsFalse(stop.IsCompleted);
            fixture.Clock.Advance(ChildWorkerCancellationPolicy.Grace);
            await fixture.Process.KillObserved.Task.WaitAsync(TestTimeout);
            Assert.AreEqual(1, fixture.Process.KillCalls);
            Assert.IsFalse(stop.IsCompleted, "blocked write remains owned after escalation");
        }
        finally
        {
            input.ReleaseSynchronousWrite();
        }

        ChildWorkerCancellationResult result = await stop.WaitAsync(TestTimeout);
        Assert.IsTrue(result.Facts.GraceExpired);
        Assert.IsTrue(result.Facts.KillAttempted);
        Assert.AreEqual(ChildProcessKillOutcome.Requested, result.Facts.KillOutcome);
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    [DataRow("write-fault")]
    [DataRow("flush-fault")]
    [DataRow("exit-during-write")]
    [DataRow("exit-during-flush")]
    public async Task CancelIoBoundary_RecordsExactFactsAndNeverRetries(string scenario)
    {
        var input = new SessionInputStream();
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync(
            input: input,
            killOutcome: ChildProcessKillOutcome.Requested,
            exitOnKill: true);
        bool exitsDuringIo = scenario.StartsWith("exit-", StringComparison.Ordinal);
        bool failsWrite = scenario is "write-fault" or "exit-during-write";
        input.FaultWriteCall = failsWrite ? 2 : 0;
        input.FaultFlushCall = failsWrite ? 0 : 2;
        input.WriteBoundary = call =>
        {
            if (call == 2 && scenario == "exit-during-write")
            {
                fixture.Process.Exit(130);
            }
        };
        input.FlushBoundary = call =>
        {
            if (call == 2 && scenario == "exit-during-flush")
            {
                fixture.Process.Exit(130);
            }
        };

        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);
        long timerGeneration = fixture.Clock.TimerGeneration;
        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        await fixture.Clock
            .WaitForTimerCreatedAsync(timerGeneration)
            .WaitAsync(TestTimeout);
        await input.SecondWrite.WaitAsync(TestTimeout);
        if (!failsWrite)
        {
            await input.SecondFlush.WaitAsync(TestTimeout);
        }

        ChildWorkerCancelDeliveryPhase expectedPhase = failsWrite
            ? ChildWorkerCancelDeliveryPhase.WriteFailed
            : ChildWorkerCancelDeliveryPhase.FlushFailed;
        await fixture.Session
            .WaitForCancellationDeliveryAsync()
            .WaitAsync(TestTimeout);

        if (!exitsDuringIo)
        {
            fixture.Clock.Advance(ChildWorkerCancellationPolicy.Grace);
            await fixture.Process.KillObserved.Task.WaitAsync(TestTimeout);
        }

        ChildWorkerCancellationResult result = await stop.WaitAsync(TestTimeout);
        Assert.AreEqual(expectedPhase, result.Facts.DeliveryPhase);
        Assert.AreEqual(
            scenario switch
            {
                "exit-during-write" => ChildWorkerCancellationExitRace.DuringWrite,
                "exit-during-flush" => ChildWorkerCancellationExitRace.DuringFlush,
                _ => ChildWorkerCancellationExitRace.None
            },
            result.Facts.ExitRace);
        Assert.AreEqual(exitsDuringIo ? 0 : 1, fixture.Process.KillCalls);
        Assert.AreEqual(2, input.WriteCalls, "execute plus one cancel write attempt");
        Assert.AreEqual(failsWrite ? 1 : 2, input.FlushCalls);
        Assert.AreEqual(failsWrite ? 1 : 2, input.Frames.Count);
        Assert.AreSame(stop, fixture.Session.RequestStop());
        AssertResourcesDisposedOnce(fixture);
    }

    [TestMethod]
    public async Task CancelledStopWait_DoesNotCancelSharedStopOperation()
    {
        SessionTestSupport.SessionFixture fixture = await SessionTestSupport.CreateAsync();
        SessionTestSupport.EmitReady(fixture);
        await fixture.Session.WaitForStartupAsync().WaitAsync(TestTimeout);

        Task<ChildWorkerCancellationResult> stop = fixture.Session.RequestStop();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Session.WaitForStopAsync(cancellation.Token));
        Assert.AreSame(stop, fixture.Session.RequestStop());
        Assert.IsFalse(stop.IsCompleted);

        await fixture.Input.SecondFlush.WaitAsync(TestTimeout);
        fixture.Process.Exit(130);
        await stop.WaitAsync(TestTimeout);

        Assert.AreEqual(0, fixture.Process.KillCalls);
        AssertResourcesDisposedOnce(fixture);
    }

    private static void AssertCanonicalExecuteAndCancel(
        SessionTestSupport.SessionFixture fixture)
    {
        IReadOnlyList<byte[]> frames = fixture.Input.Frames;
        Assert.AreEqual(2, frames.Count, "one execute and one cancel frame");

        WorkerProtocolControllerParseResult execute =
            WorkerProtocolCodec.ParseControllerInput(frames[0]);
        WorkerProtocolControllerParseResult cancel =
            WorkerProtocolCodec.ParseControllerInput(frames[1]);

        Assert.IsTrue(execute.IsSuccess);
        Assert.AreEqual(WorkerProtocolV1.RequestCategory, execute.Message!.Category);
        Assert.AreEqual(WorkerProtocolV1.ExecuteType, execute.Message.Type);
        Assert.AreEqual(1L, execute.Message.Sequence);
        Assert.AreEqual(fixture.Request, ((ExecuteRequestPayload)execute.Message.Payload).Request);

        Assert.IsTrue(cancel.IsSuccess);
        Assert.AreEqual(WorkerProtocolV1.ControlCategory, cancel.Message!.Category);
        Assert.AreEqual(WorkerProtocolV1.CancelType, cancel.Message.Type);
        Assert.AreEqual(2L, cancel.Message.Sequence);
        Assert.AreEqual(fixture.Request.RunId, cancel.Message.RunId);
        Assert.IsTrue(cancel.Message.Payload is CancelControlPayload);
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
