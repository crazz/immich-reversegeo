using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

public sealed partial class ChildWorkerLaunchingTests
{
    [TestMethod]
    public async Task Pumps_BlockedReadyCallbackDoesNotBackpressureLargerThanPipeCapacityStderr()
    {
        const int simulatedPipeCapacity = 8_192;
        var request = CreateRequest();
        var sink = new RecordingSink { BlockCall = 1 };
        var (process, _, session) = await LaunchByteSessionAsync(940, request, sink);
        var stdout = Encoding.UTF8.GetBytes(ReadyFrame() + RunStartedFrame(request.RunId) + EligibilityFrame(request.RunId) + CompletedFrame(request.RunId));
        var stderr = Enumerable.Range(0, simulatedPipeCapacity * 5 + 317).Select(index => (byte)(index % 251)).ToArray();

        var stdoutFeed = process.StandardOutput.FeedAsync(stdout, 1_024);
        await sink.BlockedCallEntered;
        var stderrFeed = process.StandardError.FeedAsync(stderr, 1_024);
        await process.StandardError.WaitForConsumedAsync(stderr.LongLength);
        await stderrFeed;

        Assert.IsFalse(session.Startup.IsCompleted, "dual-stream-backpressure: startup-gated-by-ready-callback");
        Assert.AreEqual(stderr.LongLength, process.StandardError.ConsumedBytes, "dual-stream-backpressure: explicit-stderr-consumption-barrier");
        Assert.IsTrue(stderr.Length > simulatedPipeCapacity, "dual-stream-backpressure: stderr-exceeds-simulated-capacity");

        sink.ReleaseBlockedCall();
        await stdoutFeed;
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, "dual-stream-backpressure: startup-before-exit");
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(completion.Startup, "dual-stream-backpressure: ready-after-release");
        Assert.AreEqual(4, sink.AcceptCalls, "dual-stream-backpressure: full-lifecycle-delivered");
        Assert.AreEqual(1, sink.MaximumConcurrency, "dual-stream-backpressure: serialized-callbacks");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "dual-stream-backpressure: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "dual-stream-backpressure: stderr-finality");
        CollectionAssert.AreEqual(stderr, completion.StandardErrorTail.Bytes.ToArray(), "dual-stream-backpressure: exact-final-stderr-tail");
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("stdout-invalidity")]
    [DataRow("ready-sink-failure")]
    [DataRow("exit-observer-failure")]
    public async Task Pumps_FailurePathsDoNotAbandonEitherBoundedStream(string label)
    {
        var sink = new RecordingSink { FailCall = label == "ready-sink-failure" ? 1 : null };
        var (process, _, session) = await LaunchByteSessionAsync(941, sink: sink);
        var stdout = label == "stdout-invalidity"
            ? Encoding.UTF8.GetBytes("{\n" + ReadyFrame())
            : Encoding.UTF8.GetBytes(ReadyFrame());
        var stderr = Enumerable.Range(0, 24_731).Select(index => (byte)(255 - (index % 251))).ToArray();

        var stdoutFeed = process.StandardOutput.FeedAsync(stdout, 257);
        var stderrFeed = process.StandardError.FeedAsync(stderr, 509);
        await process.StandardOutput.WaitForConsumedAsync(stdout.LongLength);
        await process.StandardError.WaitForConsumedAsync(stderr.LongLength);
        await Task.WhenAll(stdoutFeed, stderrFeed);
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        if (label == "exit-observer-failure")
        {
            process.FailExit();
        }
        else
        {
            process.Exit(31);
        }

        var completion = await session.Completion;
        Assert.AreEqual(stdout.LongLength, process.StandardOutput.ConsumedBytes, $"{label}: explicit-stdout-consumption-barrier");
        Assert.AreEqual(stderr.LongLength, process.StandardError.ConsumedBytes, $"{label}: explicit-stderr-consumption-barrier");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, $"{label}: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, $"{label}: stderr-finality");
        CollectionAssert.AreEqual(stderr, completion.StandardErrorTail.Bytes.ToArray(), $"{label}: exact-stderr-retained");
        if (label == "stdout-invalidity")
        {
            var first = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation, $"{label}: exact-first-fact");
            Assert.AreEqual(WorkerProtocolFailureCode.MalformedJson, first.Failure.Code, $"{label}: exact-code");
            Assert.AreEqual(0, sink.AcceptCalls, $"{label}: invalid-never-delivered");
        }
        else if (label == "ready-sink-failure")
        {
            Assert.IsInstanceOfType<ChildWorkerProtocolObservation.SinkFailure>(completion.FirstProtocolObservation, $"{label}: exact-first-fact");
            Assert.AreEqual(1, sink.AcceptCalls, $"{label}: only-failing-callback");
        }
        else
        {
            Assert.IsFalse(completion.ExitObserved, $"{label}: raw-exit-unavailable-safe");
            Assert.IsNull(completion.ExitCode, $"{label}: no-invented-exit-code");
        }

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReadyDeadline_RecordsExactDueAndPeriodAndCommitsAtThirtySecondBoundary()
    {
        var time = new ManualTimeProvider();
        var options = new ChildWorkerLauncherOptions { TimeProvider = time };
        var (process, _, session) = await LaunchByteSessionAsync(942, options: options);
        await time.TimerCreated;

        Assert.AreEqual(TimeSpan.FromSeconds(30), time.LastDueTime, "ready-deadline: exact-thirty-second-due");
        Assert.AreEqual(Timeout.InfiniteTimeSpan, time.LastPeriod, "ready-deadline: exact-infinite-period");
        time.Advance(TimeSpan.FromMilliseconds(29_999));
        Assert.IsFalse(session.Startup.IsCompleted, "ready-deadline: 29.999-seconds-still-pending");
        time.Advance(TimeSpan.FromMilliseconds(1));
        var startup = await session.Startup;
        await time.FirstDisposed;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyTimedOut>(startup, "ready-deadline: exact-boundary-timeout");
        Assert.AreEqual(1, time.CreateCalls, "ready-deadline: one-timer");
        Assert.AreEqual(1, time.DisposeCalls, "ready-deadline: timer-disposed-once");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "ready-deadline: zero-execute-writes");
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReadyDeadline_AfterBoundaryAlsoTimesOutWithoutExecute()
    {
        var time = new ManualTimeProvider();
        var (process, _, session) = await LaunchByteSessionAsync(943, options: new ChildWorkerLauncherOptions { TimeProvider = time });
        await time.TimerCreated;

        time.Advance(TimeSpan.FromTicks(TimeSpan.FromSeconds(30).Ticks + 1));
        var startup = await session.Startup;
        await time.FirstDisposed;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyTimedOut>(startup, "after-deadline: timeout");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "after-deadline: zero-write");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "after-deadline: zero-flush");
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("ready-commits-first")]
    [DataRow("timeout-commits-first")]
    public async Task ReadyDeadline_ReadyAndTimeoutCommitOrdersHaveOneStartupAuthority(string label)
    {
        var time = new ManualTimeProvider();
        var (process, sink, session) = await LaunchByteSessionAsync(944, options: new ChildWorkerLauncherOptions { TimeProvider = time });
        await time.TimerCreated;

        if (label == "ready-commits-first")
        {
            process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));
            var startup = await session.Startup;
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(startup, $"{label}: exact-startup");
            await time.FirstDisposed;
            time.Advance(TimeSpan.FromSeconds(30));
            Assert.AreEqual(1, process.StandardInput.WriteCalls, $"{label}: exactly-one-write");
            Assert.AreEqual(1, process.StandardInput.FlushCalls, $"{label}: exactly-one-flush");
        }
        else
        {
            time.Advance(TimeSpan.FromSeconds(30));
            var startup = await session.Startup;
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyTimedOut>(startup, $"{label}: exact-startup");
            process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));
            await sink.FirstAccepted;
            Assert.AreEqual(0, process.StandardInput.WriteCalls, $"{label}: no-late-write");
            Assert.AreEqual(0, process.StandardInput.FlushCalls, $"{label}: no-late-flush");
        }

        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;
        Assert.AreSame(await session.Startup, completion.Startup, $"{label}: same-committed-startup-reference");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task ReadyDeadline_PendingReadySinkCallbackAllowsTimeoutToCommitWithoutExecute()
    {
        var time = new ManualTimeProvider();
        var sink = new RecordingSink { BlockCall = 1 };
        var (process, _, session) = await LaunchByteSessionAsync(945, sink: sink, options: new ChildWorkerLauncherOptions { TimeProvider = time });
        await time.TimerCreated;

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));
        await sink.BlockedCallEntered;
        Assert.IsFalse(session.Startup.IsCompleted, "pending-ready-callback: startup-still-pending");
        time.Advance(TimeSpan.FromSeconds(30));
        var startup = await session.Startup;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyTimedOut>(startup, "pending-ready-callback: timeout-wins");
        sink.ReleaseBlockedCall();
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await session.Completion;
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "pending-ready-callback: zero-write-after-release");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "pending-ready-callback: zero-flush-after-release");
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("buffered-ready-before-exit")]
    [DataRow("exit-before-buffered-ready")]
    public async Task Startup_BufferedReadyAndExitCommitOrdersRemainDistinct(string label)
    {
        var process = new ByteProcess(946);
        if (label == "exit-before-buffered-ready")
        {
            process.StandardOutput.PauseReads();
        }

        var sink = new RecordingSink();
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), sink, TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, $"{label}: started").Session;
        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));

        if (label == "buffered-ready-before-exit")
        {
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, $"{label}: ready-startup");
            process.Exit(37);
            Assert.AreEqual(1, process.StandardInput.WriteCalls, $"{label}: execute-written");
        }
        else
        {
            process.Exit(37);
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.PreReadyExit>(await session.Startup, $"{label}: exit-startup");
            process.StandardOutput.ReleaseReads();
            await sink.FirstAccepted;
            Assert.AreEqual(0, process.StandardInput.WriteCalls, $"{label}: execute-not-written");
        }

        process.StandardOutput.Complete();
        process.StandardError.Complete();
        var completion = await session.Completion;
        Assert.AreEqual(37, completion.ExitCode, $"{label}: raw-exit-retained");
        Assert.AreEqual(1, sink.AcceptCalls, $"{label}: ready-delivered-once");
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("complete-ready-before-eof")]
    [DataRow("eof-before-ready")]
    public async Task Startup_CompleteReadyAndEofCommitOrdersRemainDistinct(string label)
    {
        var (process, sink, session) = await LaunchByteSessionAsync(947);

        if (label == "complete-ready-before-eof")
        {
            process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));
            process.StandardOutput.Complete();
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup, $"{label}: ready-startup");
            Assert.AreEqual(1, sink.AcceptCalls, $"{label}: ready-delivered");
            Assert.AreEqual(1, process.StandardInput.WriteCalls, $"{label}: execute-written");
        }
        else
        {
            process.StandardOutput.Complete();
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.PreReadyEndOfStream>(await session.Startup, $"{label}: eof-startup");
            Assert.AreEqual(0, sink.AcceptCalls, $"{label}: no-ready-delivered");
            Assert.AreEqual(0, process.StandardInput.WriteCalls, $"{label}: no-execute");
        }

        process.StandardError.Complete();
        process.Exit(0);
        await session.Completion;
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("pre-ready-exit")]
    [DataRow("pre-ready-read-failed")]
    [DataRow("pre-ready-exit-observation-failed")]
    public async Task Startup_PreReadyExitAndReadFailureAreDistinctWithZeroExecute(string label)
    {
        var (process, _, session) = await LaunchByteSessionAsync(948);

        if (label == "pre-ready-exit")
        {
            process.Exit(41);
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.PreReadyExit>(await session.Startup, $"{label}: exact-startup");
            process.StandardOutput.Complete();
        }
        else if (label == "pre-ready-read-failed")
        {
            process.StandardOutput.Fail();
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.PreReadyReadFailed>(await session.Startup, $"{label}: exact-startup");
            process.Exit(41);
        }
        else
        {
            process.FailExit();
            var startup = await session.Startup;
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.PreReadyExitObservationFailed>(startup, $"{label}: exact-startup");
            Assert.IsNotInstanceOfType<ChildWorkerStartupObservation.PreReadyExit>(startup, $"{label}: no-contradictory-exit-observation");
            process.StandardOutput.Complete();
        }

        process.StandardError.Complete();
        var completion = await session.Completion;
        Assert.AreEqual(0, process.StandardInput.WriteCalls, $"{label}: zero-write");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, $"{label}: zero-flush");
        if (label == "pre-ready-read-failed")
        {
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.ReadFailed>(completion.StandardOutputFinality, $"{label}: stdout-pump-finality");
        }
        else
        {
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, $"{label}: stdout-pump-drained");
        }

        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, $"{label}: stderr-pump-drained");
        if (label == "pre-ready-exit-observation-failed")
        {
            Assert.IsFalse(completion.ExitObserved, $"{label}: exit-not-observed");
            Assert.IsNull(completion.ExitCode, $"{label}: exit-code-unavailable");
            Assert.IsNotInstanceOfType<ChildWorkerStartupObservation.PreReadyExit>(completion.Startup, $"{label}: completion-has-no-contradictory-exit-observation");
        }
        else
        {
            Assert.IsTrue(completion.ExitObserved, $"{label}: exit-observed");
            Assert.AreEqual(41, completion.ExitCode, $"{label}: raw-exit-retained");
        }

        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("eof")]
    [DataRow("sink-failure")]
    [DataRow("disposal")]
    public async Task ReadyDeadline_TimerIsDisposedExactlyOnceForEveryEarlyFinality(string label)
    {
        var time = new ManualTimeProvider();
        var sink = new RecordingSink { FailCall = label == "sink-failure" ? 1 : null };
        var (process, _, session) = await LaunchByteSessionAsync(949, sink: sink, options: new ChildWorkerLauncherOptions { TimeProvider = time });
        Task? disposal = null;

        if (label == "success" || label == "sink-failure")
        {
            process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));
            await session.Startup;
        }
        else if (label == "eof")
        {
            process.StandardOutput.Complete();
            await session.Startup;
        }
        else
        {
            disposal = session.DisposeAsync().AsTask();
            await session.Startup;
        }

        await time.FirstDisposed;
        Assert.AreEqual(1, time.CreateCalls, $"{label}: one-timer-created");
        Assert.AreEqual(1, time.DisposeCalls, $"{label}: one-timer-disposed");

        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        if (disposal is null)
        {
            await session.DisposeAsync();
        }
        else
        {
            await disposal;
        }

        Assert.AreEqual(1, time.DisposeCalls, $"{label}: no-timer-leak-or-double-disposal");
    }

    [TestMethod]
    public async Task Completion_ExitFirstWaitsForTrailingAcceptedTerminalStderrAndBothFinalities()
    {
        var request = CreateRequest();
        var (process, sink, session) = await LaunchByteSessionAsync(950, request);

        process.Exit(53);
        Assert.IsFalse(session.Completion.IsCompleted, "exit-first-trailing: completion-pending-before-streams");
        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame() + RunStartedFrame(request.RunId) + EligibilityFrame(request.RunId) + CompletedFrame(request.RunId)));
        process.StandardError.Write(Encoding.UTF8.GetBytes("exit-first-trailing-stderr"));
        Assert.IsFalse(session.Completion.IsCompleted, "exit-first-trailing: completion-pending-before-eof");
        process.StandardOutput.Complete();
        Assert.IsFalse(session.Completion.IsCompleted, "exit-first-trailing: completion-pending-before-stderr-eof");
        process.StandardError.Complete();
        var completion = await session.Completion;

        CollectionAssert.AreEqual(
            new[] { WorkerProtocolV1.ReadyType, WorkerProtocolV1.RunStartedType, WorkerProtocolV1.EligibilityDeterminedType, WorkerProtocolV1.CompletedType },
            sink.Events.Select(@event => @event.Type).ToArray(),
            "exit-first-trailing: exact-sink-order");
        Assert.AreSame(sink.Events[3], completion.Terminal, "exit-first-trailing: exact-terminal-reference");
        Assert.IsTrue(completion.ExitObserved, "exit-first-trailing: exit-observed");
        Assert.AreEqual(53, completion.ExitCode, "exit-first-trailing: raw-nonzero-code");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "exit-first-trailing: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "exit-first-trailing: stderr-finality");
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("exit-first-trailing-stderr"), completion.StandardErrorTail.Bytes.ToArray(), "exit-first-trailing: exact-stderr");
        Assert.IsNull(completion.FirstProtocolObservation, "exit-first-trailing: no-classification");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Completion_ExitObserverFaultStillAllowsBothPumpsToDrainAndFinalize()
    {
        var (process, sink, session) = await LaunchByteSessionAsync(951);

        process.FailExit();
        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));
        process.StandardError.Write(DrainSentinel.ToArray());
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        var completion = await session.Completion;

        Assert.AreEqual(1, sink.AcceptCalls, "exit-fault: ready-drained-and-delivered");
        Assert.IsFalse(completion.ExitObserved, "exit-fault: raw-exit-unavailable");
        Assert.IsNull(completion.ExitCode, "exit-fault: code-remains-safe-null");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "exit-fault: stdout-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "exit-fault: stderr-finality");
        CollectionAssert.AreEqual(DrainSentinel, completion.StandardErrorTail.Bytes.ToArray(), "exit-fault: stderr-drained");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task WaitCancellation_InFlightStartupAndCompletionCancelOnlyThoseWaitTasks()
    {
        var (process, _, session) = await LaunchByteSessionAsync(952);
        using var startupCancellation = new CancellationTokenSource();
        using var completionCancellation = new CancellationTokenSource();
        var startupWait = session.WaitForStartupAsync(startupCancellation.Token);
        var completionWait = session.WaitForCompletionAsync(completionCancellation.Token);

        Assert.IsFalse(startupWait.IsCompleted, "in-flight-waits: startup-pending-before-cancel");
        Assert.IsFalse(completionWait.IsCompleted, "in-flight-waits: completion-pending-before-cancel");
        startupCancellation.Cancel();
        completionCancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => startupWait);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => completionWait);

        Assert.AreEqual(0, process.StandardInput.DisposeCalls, "in-flight-waits: stdin-unchanged");
        Assert.AreEqual(1, process.StandardOutput.ReadCalls, "in-flight-waits: stdout-pump-unchanged");
        Assert.AreEqual(1, process.StandardError.ReadCalls, "in-flight-waits: stderr-pump-unchanged");
        Assert.AreEqual(1, process.WaitCalls, "in-flight-waits: process-wait-unchanged");
        Assert.AreEqual(0, process.DisposeCalls, "in-flight-waits: process-not-disposed");

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));
        var laterStartup = await session.WaitForStartupAsync();
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var laterCompletion = await session.WaitForCompletionAsync();
        Assert.AreSame(session.Startup.Result, laterStartup, "in-flight-waits: same-startup-result");
        Assert.AreSame(session.Completion.Result, laterCompletion, "in-flight-waits: same-completion-result");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Disposal_SuppressionWinsCallbackAdmission_NoSinkCallbackStarts()
    {
        var request = CreateRequest();
        var sink = new RecordingSink();
        var process = new ByteProcess(960);
        process.StandardOutput.PauseReads();
        var options = new ChildWorkerLauncherOptions { TimeProvider = new ManualTimeProvider(), ReadyTimeout = Timeout.InfiniteTimeSpan };
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), request, sink, options, CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "suppression-first: started").Session;
        var stdout = Encoding.UTF8.GetBytes(ReadyFrame() + RunStartedFrame(request.RunId) + EligibilityFrame(request.RunId) + CompletedFrame(request.RunId));

        process.StandardOutput.Write(stdout);
        await process.StandardOutput.ReadStarted.Task;
        var disposal = session.DisposeAsync().AsTask();

        Assert.AreEqual(1, process.StandardInput.DisposeCalls, "suppression-first: disposal-commit-barrier");
        Assert.AreEqual(0, sink.AcceptCalls, "suppression-first: zero-callbacks-before-release");
        Assert.IsFalse(disposal.IsCompleted, "suppression-first: disposal-awaits-existing-stdout-lifecycle");

        process.StandardOutput.ReleaseReads();
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await disposal;

        Assert.AreEqual(0, sink.AcceptCalls, "suppression-first: buffered-events-never-invoke-sink");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "suppression-first: no-execute-write");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "suppression-first: no-execute-flush");
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.Disposed>(await session.Startup, "suppression-first: startup-disposed");
    }

    [TestMethod]
    public async Task Disposal_CallbackAdmissionWins_StartedCallbackFinishesOnceAndDisposalAwaitsIt()
    {
        var request = CreateRequest();
        var sink = new RecordingSink { BlockCall = 1 };
        var time = new ManualTimeProvider();
        var options = new ChildWorkerLauncherOptions { TimeProvider = time, ReadyTimeout = Timeout.InfiniteTimeSpan };
        var (process, _, session) = await LaunchByteSessionAsync(953, request, sink, options);

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame() + RunStartedFrame(request.RunId) + EligibilityFrame(request.RunId) + CompletedFrame(request.RunId)));
        await sink.BlockedCallEntered;
        var firstDisposal = session.DisposeAsync().AsTask();
        var secondDisposal = session.DisposeAsync().AsTask();

        Assert.AreSame(firstDisposal, secondDisposal, "in-flight-disposal: shared-task");
        Assert.AreEqual(1, process.StandardInput.DisposeCalls, "in-flight-disposal: stdin-closed-once");
        Assert.IsFalse(firstDisposal.IsCompleted, "in-flight-disposal: waits-for-in-flight-callback-and-finality");
        Assert.AreEqual(1, sink.AcceptCalls, "in-flight-disposal: only-entered-callback-started");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "in-flight-disposal: no-execute-write");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "in-flight-disposal: no-execute-flush");
        Assert.AreEqual(0, time.CreateCalls, "in-flight-disposal: no-timer-for-infinite-deadline");

        sink.ReleaseBlockedCall();
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await firstDisposal;

        Assert.AreEqual(1, sink.AcceptCalls, "in-flight-disposal: not-yet-started-callbacks-suppressed");
        Assert.AreEqual(1, process.StandardInput.DisposeCalls, "in-flight-disposal: stdin-single-disposal");
        Assert.AreEqual(1, process.StandardOutput.DisposeCalls, "in-flight-disposal: stdout-single-disposal");
        Assert.AreEqual(1, process.StandardError.DisposeCalls, "in-flight-disposal: stderr-single-disposal");
        Assert.AreEqual(1, process.DisposeCalls, "in-flight-disposal: process-single-disposal");
        Assert.AreEqual(1, process.WaitCalls, "in-flight-disposal: process-wait-single-call");
        Assert.AreEqual(2, process.StandardOutput.ReadCalls, "in-flight-disposal: exact-single-owned-stdout-reader");
        Assert.AreEqual(1, process.StandardError.ReadCalls, "in-flight-disposal: exact-single-owned-stderr-reader");
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.Disposed>(await session.Startup, "in-flight-disposal: startup-settles-once");
    }

    [TestMethod]
    public async Task Execute_RequestSerializationFailureAfterReadySinkAcceptanceDoesNotWriteOrExposeException()
    {
        var process = new ByteProcess(955);
        var sink = new RecordingSink();
        var options = new ChildWorkerLauncherOptions
        {
            TimeProvider = new ThrowingGetUtcNowTimeProvider(),
            ReadyTimeout = Timeout.InfiniteTimeSpan
        };
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), sink, options, CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "serialization-failure: started").Session;

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));
        var startup = await session.Startup;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.RequestSerializationFailed>(startup, "serialization-failure: exact-startup");
        Assert.AreEqual(1, sink.AcceptCalls, "serialization-failure: ready-sink-once");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "serialization-failure: zero-stdin-write");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "serialization-failure: zero-stdin-flush");
        Assert.AreEqual(0, process.StandardInput.Length, "serialization-failure: zero-stdin-bytes");

        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;
        Assert.IsNull(completion.FirstProtocolObservation, "serialization-failure: no-raw-exception-observation");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "serialization-failure: stdout-drained");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "serialization-failure: stderr-drained");
        Assert.IsTrue(completion.ExitObserved, "serialization-failure: exit-settled");
        Assert.AreEqual(0, completion.ExitCode, "serialization-failure: exact-raw-exit");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Execute_PrefixWriteFailureRetainsExactlyNBytesWithNoRetryRemainderOrFlush()
    {
        const int retainedBytes = 17;
        var process = new ByteProcess(954);
        process.StandardInput.FailWriteAfterBytes = retainedBytes;
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), new ChildWorkerLauncherOptions { TimeProvider = clock, ReadyTimeout = Timeout.InfiniteTimeSpan }, CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "prefix-write-failure: started").Session;
        var fullExpected = Encoding.UTF8.GetBytes("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"trigger\":\"manual\"}}\n");

        process.StandardOutput.Write(Encoding.UTF8.GetBytes(ReadyFrame()));
        var startup = await session.Startup;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.RequestWriteFailed>(startup, "prefix-write-failure: exact-startup");
        CollectionAssert.AreEqual(fullExpected[..retainedBytes], process.StandardInput.ToArray(), "prefix-write-failure: exact-retained-prefix");
        Assert.AreEqual(retainedBytes, process.StandardInput.Length, "prefix-write-failure: no-remainder");
        Assert.AreEqual(1, process.StandardInput.WriteCalls, "prefix-write-failure: one-write-no-retry");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "prefix-write-failure: zero-flush");
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await session.DisposeAsync();
    }

    [TestMethod]
    public void SystemAdapter_GeneralFactoryCompileTimeSeam_IsStructurallyClosedWithoutRealProcess()
    {
        var systemFactory = new SystemChildProcessFactory();
        IChildProcessFactory factory = systemFactory;
        Func<ChildProcessStartDescriptor, CancellationToken, ValueTask<IChildProcess?>> startAsync = systemFactory.StartAsync;

        Assert.AreSame(factory, startAsync.Target, "general-factory-seam: exact-delegate-target");
        Assert.AreEqual(nameof(SystemChildProcessFactory.StartAsync), startAsync.Method.Name, "general-factory-seam: exact-method-name");
        Assert.AreEqual(typeof(SystemChildProcessFactory), startAsync.Method.DeclaringType, "general-factory-seam: concrete-method-owner");
    }
}
