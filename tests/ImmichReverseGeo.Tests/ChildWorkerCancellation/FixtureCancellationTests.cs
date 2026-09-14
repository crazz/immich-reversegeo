using System.Text;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Tests.ChildWorkerCancellation;

[TestClass]
[TestCategory("Change28")]
public sealed class FixtureCancellationTests
{
    [TestMethod]
    public async Task CooperativeFixture_StopPreservesCancelledTerminalAnd130WithoutReaperOrKill()
    {
        var clock = new CancellationTestClock();
        var lease = CreateLease(clock);
        await using (lease)
        {
            var session = await lease.LaunchAsync("cooperative-cancel");
            await WaitForMarkerAsync(lease, "cooperative-cancel");
            var stop = session.RequestStop();
            Assert.AreSame(stop, session.RequestStop(), "Repeated Stop must join the exact owned task.");
            var result = await stop.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            Assert.AreEqual(ChildWorkerCancellationPolicy.Grace, result.Facts.DeadlineUtc - result.Facts.FirstStopAtUtc);
            Assert.IsTrue(result.Facts.RequestAccepted);
            Assert.IsFalse(result.Facts.GraceExpired);
            Assert.IsFalse(result.Facts.KillAttempted);
            Assert.IsNull(result.Facts.KillOutcome);
            Assert.AreEqual(WorkerProtocolV1.CancelledType, result.Completion.Terminal?.Type);
            Assert.AreEqual(130, result.Completion.ExitCode);
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.IsFalse(lease.ForcedCleanup, "The emergency reaper must not produce the successful result.");
            AssertFinality(lease, result.Completion);
            AssertCanonicalExecuteAndCancel(lease);
            Assert.AreEqual(0, clock.ActiveTimerCount);
            await session.DisposeAsync();
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
        }

        Assert.IsFalse(lease.IsRegistered);
        Assert.IsFalse(Directory.Exists(lease.Root));
        Assert.AreEqual(1, lease.ProcessDisposeCalls);
    }

    [TestMethod]
    public async Task UnresponsiveFixture_OneDeadlineKillsExactProcessAndDrainsBeforeRelease()
    {
        var clock = new CancellationTestClock();
        var lease = CreateLease(clock);
        await using (lease)
        {
            var session = await lease.LaunchAsync("unresponsive");
            await WaitForMarkerAsync(lease, "unresponsive");
            var generation = clock.TimerGeneration;
            var stop = session.RequestStop();
            Assert.AreSame(stop, session.RequestStop());
            await clock.WaitForTimerCreatedAsync(generation).WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await WaitForMarkerAsync(lease, "cancel-observed");
            Assert.IsFalse(lease.HasExited);
            Assert.IsFalse(stop.IsCompleted);
            Assert.AreEqual(0, lease.TreeKillCalls);
            Assert.AreEqual(0, lease.ProcessDisposeCalls);

            clock.Advance(ChildWorkerCancellationPolicy.Grace);
            var kill = await lease.TreeKillObserved.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.AreEqual(ChildProcessKillOutcome.Requested, kill);
            var result = await stop.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            Assert.IsTrue(result.Facts.GraceExpired);
            Assert.IsTrue(result.Facts.KillAttempted);
            Assert.AreEqual(ChildProcessKillOutcome.Requested, result.Facts.KillOutcome);
            Assert.AreEqual(1, lease.TreeKillCalls);
            Assert.IsNull(result.Completion.Terminal, "Forced termination must not fabricate a terminal.");
            Assert.IsNotNull(result.Completion.ExitCode, "The actual platform status must be retained.");
            AssertFinality(lease, result.Completion);
            AssertCanonicalExecuteAndCancel(lease);
            Assert.IsFalse(lease.ForcedCleanup, "Session escalation must finish before emergency fixture cleanup.");
            Assert.AreEqual(0, clock.ActiveTimerCount);
            await session.DisposeAsync();
            Assert.AreEqual(1, lease.ProcessDisposeCalls);
        }

        Assert.IsFalse(lease.IsRegistered);
        Assert.IsFalse(Directory.Exists(lease.Root));
        Assert.AreEqual(1, lease.ProcessDisposeCalls);
    }

    private static WorkerProcessFixtureLease CreateLease(TimeProvider clock)
        => new() { LauncherOptions = new ChildWorkerLauncherOptions { TimeProvider = clock } };

    private static Task<WorkerProtocolEvent> WaitForMarkerAsync(WorkerProcessFixtureLease lease, string marker)
        => lease.Sink.WaitForAsync(e => e.Payload is LogEmittedPayload log
            && log.Message == $"fixture:{marker}:{lease.Request.RunId:D}");

    private static void AssertFinality(WorkerProcessFixtureLease lease, ChildWorkerCompletionObservation completion)
    {
        Assert.IsTrue(lease.HasExited, "The exact fixture process must have exited before cleanup succeeds.");
        Assert.IsTrue(completion.ExitObserved);
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality);
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality);
        Assert.AreEqual(lease.Request.RunId, completion.RunId);
        Assert.AreEqual(lease.ProcessId, completion.ProcessId);
        Assert.AreEqual(1, lease.ProcessDisposeCalls, "Stop completion includes owned adapter disposal.");
    }

    private static void AssertCanonicalExecuteAndCancel(WorkerProcessFixtureLease lease)
    {
        var lines = Encoding.UTF8.GetString(lease.WrittenInput.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(2, lines.Length, "One execute and one cancel must be the entire controller input.");
        for (var index = 0; index < lines.Length; index++)
        {
            var parsed = WorkerProtocolCodec.ParseControllerInput(Encoding.UTF8.GetBytes(lines[index]));
            Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
            Assert.AreEqual(index + 1L, parsed.Message!.Sequence);
            Assert.AreEqual(lease.Request.RunId, parsed.Message.RunId);
            Assert.AreEqual(index == 0 ? WorkerProtocolV1.ExecuteType : WorkerProtocolV1.CancelType, parsed.Message.Type);
        }
    }
}
