using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Text;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ChildWorkerLaunching;

[TestClass]
public sealed partial class ChildWorkerLaunchingTests
{
    [TestMethod]
    public void StandardErrorTailCopiesInputAndExposesReadOnlySnapshot()
    {
        var input = new byte[] { 1, 2, 3 };
        var tail = new ChildWorkerStandardErrorTail(input, 3, false, false);
        input[0] = 9;
        var callerCopy = tail.Bytes.ToArray();
        callerCopy[1] = 8;

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, tail.Bytes.ToArray(), "tail-private-copy-and-read-only-view");
    }

    [TestMethod]
    public async Task LaunchAsync_InvalidOptionsFailBeforeProcessCreation()
    {
        var factory = new RecordingFactory { Process = new ByteProcess(90) };
        var invalid = new ChildWorkerLauncherOptions { TimeProvider = new FixedTimeProvider(DateTimeOffset.UnixEpoch), ReadyTimeout = TimeSpan.Zero };

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => new ChildWorkerLauncher(factory).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), invalid, CancellationToken.None).AsTask());

        Assert.AreEqual(0, factory.StartCalls, "invalid-options-no-start");
    }

    [TestMethod]
    public async Task LaunchAsync_NullTimeProviderNamesOptionsAndFailsBeforeProcessCreation()
    {
        var factory = new RecordingFactory { Process = new ByteProcess(91) };
        var invalid = new ChildWorkerLauncherOptions { TimeProvider = null! };

        var exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => new ChildWorkerLauncher(factory).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), invalid, CancellationToken.None).AsTask());

        Assert.AreEqual("options", exception.ParamName, "null-time-provider: actual-launch-options-parameter");
        Assert.AreEqual(0, factory.StartCalls, "null-time-provider: no-start");
    }

    [TestMethod]
    public async Task LaunchAsync_PreCancelled_DoesNotCallFactory()
    {
        var factory = new RecordingFactory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new ChildWorkerLauncher(factory).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), TestOptions(), cancellation.Token).AsTask());

        Assert.AreEqual(0, factory.StartCalls, "pre-cancel-factory-calls");
    }

    [TestMethod]
    [DataRow("start-exception", true)]
    [DataRow("null-return", false)]
    public async Task LaunchAsync_StartExceptionOrNull_ReturnsStartFailedWithoutChildEffects(string scenario, bool throwsOnStart)
    {
        var factory = new RecordingFactory
        {
            StartException = throwsOnStart ? new InvalidOperationException("start failure") : null
        };
        var sink = new RecordingSink();

        var result = await new ChildWorkerLauncher(factory).LaunchAsync(
            CreateInvocation(), CreateRequest(), sink, TestOptions(), CancellationToken.None);

        var failure = Assert.IsInstanceOfType<ChildWorkerLaunchResult.StartFailed>(result, $"{scenario}: start-failed-result");
        Assert.AreEqual(ChildWorkerStartFailureCategory.ProcessStartFailed, failure.Category, $"{scenario}: start-failed-category");
        Assert.AreEqual(1, factory.StartCalls, $"{scenario}: start-one-attempt");
        Assert.IsNull(factory.Process, $"{scenario}: no-started-child");
        Assert.AreEqual(0, sink.AcceptCalls, $"{scenario}: no-sink-effects");
    }

    [TestMethod]
    [DataRow("process-id", false)]
    [DataRow("standard-input", false)]
    [DataRow("standard-output", false)]
    [DataRow("standard-error", true)]
    public async Task LaunchAsync_PostStartSetupFailureDisposesChildAndReturnsSafeStartFailed(
        string failingGetter,
        bool disposeThrows)
    {
        var process = new SetupFailingProcess(failingGetter, disposeThrows);
        var sink = new RecordingSink();
        var factory = new RecordingFactory { Process = process };

        var result = await new ChildWorkerLauncher(factory).LaunchAsync(
            CreateInvocation(), CreateRequest(), sink, TestOptions(), CancellationToken.None);

        var failure = Assert.IsInstanceOfType<ChildWorkerLaunchResult.StartFailed>(result, $"{failingGetter}: no-started-session-reference");
        Assert.AreEqual(ChildWorkerStartFailureCategory.ProcessStartFailed, failure.Category, $"{failingGetter}: safe-typed-failure");
        Assert.AreEqual(1, factory.StartCalls, $"{failingGetter}: factory-returned-one-child");
        Assert.AreEqual(1, process.DisposeCalls, $"{failingGetter}: child-cleanup-attempted-once");
        Assert.AreEqual(0, process.WaitCalls, $"{failingGetter}: observers-never-started");
        Assert.AreEqual(0, sink.AcceptCalls, $"{failingGetter}: no-callback-escaped");
    }

    [TestMethod]
    public async Task LaunchAsync_CapturesInvocationDescriptorIdentityAndActivatesOwnedObserversOnce()
    {
        var trace = new List<string>();
        var process = new RecordingProcess(417, 0, trace);
        var factory = new RecordingFactory { Process = process };
        var invocation = CreateInvocation();
        var request = CreateRequest();
        var observerArming = new ChildWorkerObserverArmingAcknowledgements();
        var launcher = new ChildWorkerLauncher(factory, () => observerArming);

        var launch = launcher.LaunchAsync(
            invocation, request, new RecordingSink(), TestOptions(), CancellationToken.None).AsTask();
        var launchOrdering = launch.ContinueWith(
            _ => observerArming.StandardOutput.IsCompletedSuccessfully
                && observerArming.StandardError.IsCompletedSuccessfully
                && observerArming.Exit.IsCompletedSuccessfully,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await observerArming.All;
        var result = await launch;
        Assert.IsTrue(await launchOrdering, "launch-remains-pending-until-all-three-observers-armed");
        var started = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-result");
        await Task.WhenAll(process.StandardOutput.ReadStarted, process.StandardError.ReadStarted, process.WaitStarted);
        Assert.AreSame(invocation.Descriptor, factory.Descriptor, "exact-descriptor-identity");
        AssertExpectedWorkerDescriptor(factory.Descriptor!);
        Assert.AreEqual(417, started.Session.ProcessId, "session-pid");
        Assert.AreEqual(request.RunId, started.Session.RunId, "session-run-id");
        CollectionAssert.AreEquivalent(new[] { "stdout-read", "stderr-read", "exit-wait" }, trace, "all-owned-observers-activated");
        Assert.AreEqual(1, process.StandardOutput.ReadCalls, "stdout-read-once");
        Assert.AreEqual(1, process.StandardError.ReadCalls, "stderr-read-once");
        Assert.AreEqual(1, process.WaitCalls, "exit-wait-once");

        process.CompleteStreams();
        process.Exit();
        await started.Session.DisposeAsync();
    }

    [TestMethod]
    public async Task LaunchAsync_PreBufferedReadyReturnsOwnerAndStartsEveryObserverOnceBeforeSinkRelease()
    {
        var request = CreateRequest();
        var process = new ByteProcess(418);
        process.StandardOutput.Write(ProtocolBytes(ReadyFrame()));
        var sink = new RecordingSink { BlockCall = 1 };
        var observerArming = new ChildWorkerObserverArmingAcknowledgements();
        var launch = new ChildWorkerLauncher(
            new RecordingFactory { Process = process },
            () => observerArming).LaunchAsync(
                CreateInvocation(), request, sink, TestOptions(), CancellationToken.None).AsTask();
        var launchCompletionObservation = launch.ContinueWith(
            _ => (
                StandardOutputAcknowledged: observerArming.StandardOutput.IsCompletedSuccessfully,
                StandardErrorAcknowledged: observerArming.StandardError.IsCompletedSuccessfully,
                ExitAcknowledged: observerArming.Exit.IsCompletedSuccessfully,
                StandardOutputReadStarted: process.StandardOutput.ReadStarted.Task.IsCompletedSuccessfully,
                StandardErrorReadStarted: process.StandardError.ReadStarted.Task.IsCompletedSuccessfully,
                ExitWaitStarted: process.WaitStarted.Task.IsCompletedSuccessfully,
                StandardOutputReadCalls: process.StandardOutput.ReadCalls,
                StandardErrorReadCalls: process.StandardError.ReadCalls,
                ExitWaitCalls: process.WaitCalls),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var result = await launch;
        var observedAtLaunchCompletion = await launchCompletionObservation;
        var started = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "pre-buffered-ready: started-owner-returned");
        await sink.BlockedCallEntered;

        Assert.IsTrue(launch.IsCompletedSuccessfully, "pre-buffered-ready: launch-returned-before-sink-release");
        Assert.IsTrue(observedAtLaunchCompletion.StandardOutputAcknowledged, "pre-buffered-ready: stdout-acknowledged-at-launch-completion");
        Assert.IsTrue(observedAtLaunchCompletion.StandardErrorAcknowledged, "pre-buffered-ready: stderr-acknowledged-at-launch-completion");
        Assert.IsTrue(observedAtLaunchCompletion.ExitAcknowledged, "pre-buffered-ready: exit-acknowledged-at-launch-completion");
        Assert.IsTrue(observedAtLaunchCompletion.StandardOutputReadStarted, "pre-buffered-ready: actual-stdout-read-started-at-launch-completion");
        Assert.IsTrue(observedAtLaunchCompletion.StandardErrorReadStarted, "pre-buffered-ready: actual-stderr-read-started-at-launch-completion");
        Assert.IsTrue(observedAtLaunchCompletion.ExitWaitStarted, "pre-buffered-ready: actual-exit-wait-started-at-launch-completion");
        Assert.AreEqual(1, observedAtLaunchCompletion.StandardOutputReadCalls, "pre-buffered-ready: exactly-one-stdout-read-at-launch-completion");
        Assert.AreEqual(1, observedAtLaunchCompletion.StandardErrorReadCalls, "pre-buffered-ready: exactly-one-stderr-read-at-launch-completion");
        Assert.AreEqual(1, observedAtLaunchCompletion.ExitWaitCalls, "pre-buffered-ready: exactly-one-exit-wait-at-launch-completion");
        Assert.IsFalse(started.Session.Startup.IsCompleted, "pre-buffered-ready: gated-ready-not-yet-accepted");

        sink.ReleaseBlockedCall();
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await started.Session.Startup, "pre-buffered-ready: startup-settles-after-release");
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await started.Session.DisposeAsync();
    }


    [TestMethod]
    public async Task LaunchAsync_ReusedSingletonLaunchesDistinctConcurrentSessionsWithIndependentObservers()
    {
        var firstRequest = new ProcessingRunRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), ProcessingRunTrigger.Manual);
        var secondRequest = new ProcessingRunRequest(Guid.Parse("22222222-2222-2222-2222-222222222222"), ProcessingRunTrigger.Manual);
        var firstProcess = new ByteProcess(419);
        var secondProcess = new ByteProcess(420);
        firstProcess.StandardOutput.Write(ProtocolBytes(ReadyFrame()));
        secondProcess.StandardOutput.Write(ProtocolBytes(ReadyFrame()));
        var factory = new QueuedRecordingFactory(firstProcess, secondProcess);
        var observerFactory = new QueuedObserverArmingFactory();
        var launcher = new ChildWorkerLauncher(factory, observerFactory.Create);
        var firstSink = new RecordingSink { BlockCall = 1 };
        var secondSink = new RecordingSink { BlockCall = 1 };

        var firstLaunch = launcher.LaunchAsync(
            CreateInvocation(), firstRequest, firstSink, TestOptions(), CancellationToken.None).AsTask();
        var secondLaunch = launcher.LaunchAsync(
            CreateInvocation(), secondRequest, secondSink, TestOptions(), CancellationToken.None).AsTask();
        await factory.AllStartsEntered;
        factory.ReleaseStarts();

        var results = await Task.WhenAll(firstLaunch, secondLaunch);
        var firstStarted = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(results[0], "singleton-reuse: first-started-result");
        var secondStarted = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(results[1], "singleton-reuse: second-started-result");
        var firstObserverArming = observerFactory.Instances[0];
        var secondObserverArming = observerFactory.Instances[1];

        Assert.AreEqual(2, factory.StartCalls, "singleton-reuse: factory-called-once-per-session");
        Assert.AreEqual(2, observerFactory.CreateCalls, "singleton-reuse: observer-arming-created-once-per-session");
        Assert.AreNotSame(firstProcess, secondProcess, "singleton-reuse: distinct-process-instances");
        Assert.AreNotSame(firstStarted.Session, secondStarted.Session, "singleton-reuse: distinct-session-instances");
        Assert.AreNotSame(firstObserverArming, secondObserverArming, "singleton-reuse: distinct-observer-arming-instances");
        Assert.AreNotSame(firstObserverArming.StandardOutput, secondObserverArming.StandardOutput, "singleton-reuse: distinct-stdout-arming-acknowledgements");
        Assert.AreNotSame(firstObserverArming.StandardError, secondObserverArming.StandardError, "singleton-reuse: distinct-stderr-arming-acknowledgements");
        Assert.AreNotSame(firstObserverArming.Exit, secondObserverArming.Exit, "singleton-reuse: distinct-exit-arming-acknowledgements");
        Assert.AreNotSame(firstStarted.Session.Startup, secondStarted.Session.Startup, "singleton-reuse: distinct-startup-lifecycles");
        Assert.AreNotSame(firstStarted.Session.Completion, secondStarted.Session.Completion, "singleton-reuse: distinct-completion-lifecycles");
        Assert.AreEqual(419, firstStarted.Session.ProcessId, "singleton-reuse: first-distinct-process-id");
        Assert.AreEqual(420, secondStarted.Session.ProcessId, "singleton-reuse: second-distinct-process-id");
        Assert.AreEqual(firstRequest.RunId, firstStarted.Session.RunId, "singleton-reuse: first-distinct-run-id");
        Assert.AreEqual(secondRequest.RunId, secondStarted.Session.RunId, "singleton-reuse: second-distinct-run-id");
        Assert.AreNotEqual(firstStarted.Session.RunId, secondStarted.Session.RunId, "singleton-reuse: sessions-do-not-share-run-id");
        await Task.WhenAll(firstSink.BlockedCallEntered, secondSink.BlockedCallEntered);

        AssertObserverStartedExactlyOnce(firstProcess, firstObserverArming, "singleton-reuse: first");
        AssertObserverStartedExactlyOnce(secondProcess, secondObserverArming, "singleton-reuse: second");

        firstSink.ReleaseBlockedCall();
        secondSink.ReleaseBlockedCall();
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await firstStarted.Session.Startup, "singleton-reuse: first-startup-settled");
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await secondStarted.Session.Startup, "singleton-reuse: second-startup-settled");
        firstProcess.StandardOutput.Complete();
        firstProcess.StandardError.Complete();
        firstProcess.Exit(0);
        secondProcess.StandardOutput.Complete();
        secondProcess.StandardError.Complete();
        secondProcess.Exit(0);
        await Task.WhenAll(firstStarted.Session.DisposeAsync().AsTask(), secondStarted.Session.DisposeAsync().AsTask());

        AssertDisposedExactlyOnce(firstProcess, "singleton-reuse: first");
        AssertDisposedExactlyOnce(secondProcess, "singleton-reuse: second");
    }


    [TestMethod]
    [DataRow("stdout")]
    [DataRow("stderr")]
    [DataRow("exit")]
    public void ObserverArmingAcknowledgements_DuplicateAcknowledgementFaults(string observer)
    {
        var acknowledgements = new ChildWorkerObserverArmingAcknowledgements();
        Action acknowledge = observer switch
        {
            "stdout" => acknowledgements.AcknowledgeStandardOutput,
            "stderr" => acknowledgements.AcknowledgeStandardError,
            "exit" => acknowledgements.AcknowledgeExit,
            _ => throw new AssertFailedException($"Unexpected observer '{observer}'.")
        };

        acknowledge();

        Assert.ThrowsExactly<InvalidOperationException>(
            acknowledge,
            $"{observer}: duplicate-acknowledgement-faults");
    }

    [TestMethod]
    [DataRow("stdout")]
    [DataRow("stderr")]
    [DataRow("exit")]
    public async Task LaunchAsync_AlreadyCompletedExitUsesCommonConsumptionAuthorityBeforeStartupCanCommit(
        string delayedObserver)
    {
        var observerArming = new ChildWorkerObserverArmingAcknowledgements();
        using var process = new ObserverArmingProbeProcess(
            observerArming,
            delayedObserver,
            [(byte)'{'],
            rawExitCode: 73);
        var launch = new ChildWorkerLauncher(
            new RecordingFactory { Process = process },
            () => observerArming).LaunchAsync(
                CreateInvocation(),
                CreateRequest(),
                new RecordingSink(),
                TestOptions(),
                CancellationToken.None).AsTask();
        var delayedInvocation = delayedObserver switch
        {
            "stdout" => process.StandardOutput.FirstInvocationReturned,
            "stderr" => process.StandardError.FirstInvocationReturned,
            "exit" => process.ExitInvocationReturned,
            _ => throw new AssertFailedException($"Unexpected delayed observer '{delayedObserver}'.")
        };
        var delayedArming = delayedObserver switch
        {
            "stdout" => observerArming.StandardOutput,
            "stderr" => observerArming.StandardError,
            "exit" => observerArming.Exit,
            _ => throw new AssertFailedException($"Unexpected delayed observer '{delayedObserver}'.")
        };

        try
        {
            await Task.WhenAll(
                process.StandardOutput.InvocationEntered,
                process.StandardError.InvocationEntered,
                process.ExitInvocationEntered);

            Assert.IsFalse(delayedInvocation.IsCompleted, $"{delayedObserver}: completed-exit-authority-gated-invocation-not-returned");
            Assert.IsFalse(delayedArming.IsCompleted, $"{delayedObserver}: completed-exit-authority-gated-observer-not-armed");
            Assert.IsFalse(observerArming.All.IsCompleted, $"{delayedObserver}: completed-exit-authority-all-observers-not-armed");
            Assert.IsFalse(launch.IsCompleted, $"{delayedObserver}: completed-exit-authority-owner-not-returned");
            Assert.IsFalse(process.StandardOutput.FirstResultConsumed.IsCompleted, $"{delayedObserver}: completed-exit-authority-stdout-result-not-consumed");
            Assert.IsFalse(process.StandardError.FirstResultConsumed.IsCompleted, $"{delayedObserver}: completed-exit-authority-stderr-result-not-consumed");

            process.ReleaseDelayedInvocation();

            var started = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(
                await launch,
                $"{delayedObserver}: completed-exit-authority-owner-returned-after-all-invocations");
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.PreReadyExit>(
                await started.Session.WaitForStartupAsync(),
                $"{delayedObserver}: completed-exit-authority-startup-commits-through-common-gate");
            process.StandardOutput.Complete();
            var completion = await started.Session.WaitForCompletionAsync();
            Assert.IsTrue(completion.ExitObserved, $"{delayedObserver}: completed-exit-authority-raw-exit-observed-after-release");
            Assert.AreEqual(73, completion.ExitCode, $"{delayedObserver}: completed-exit-authority-exact-raw-exit-after-release");
            await started.Session.DisposeAsync();
        }
        finally
        {
            process.ReleaseDelayedInvocation();
            process.StandardOutput.Complete();
        }
    }

    [TestMethod]
    [DataRow("stdout")]
    [DataRow("stderr")]
    [DataRow("exit")]
    public async Task LaunchAsync_ArmsEveryObserverBeforeConsumingSynchronousResults(
        string delayedObserver)
    {
        var observerArming = new ChildWorkerObserverArmingAcknowledgements();
        using var process = new ObserverArmingProbeProcess(
            observerArming,
            delayedObserver,
            ProtocolBytes(ReadyFrame()));
        var sink = new ObserverArmingProbeSink();
        var launch = new ChildWorkerLauncher(
            new RecordingFactory { Process = process },
            () => observerArming).LaunchAsync(
                CreateInvocation(),
                CreateRequest(),
                sink,
                TestOptions(),
                CancellationToken.None).AsTask();
        var launchCompletionObservation = launch.ContinueWith(
            _ => new
            {
                StandardOutputReturned = process.StandardOutput.FirstInvocationReturned.IsCompletedSuccessfully,
                StandardErrorReturned = process.StandardError.FirstInvocationReturned.IsCompletedSuccessfully,
                ExitReturned = process.ExitInvocationReturned.IsCompletedSuccessfully,
                StandardOutputArmed = observerArming.StandardOutput.IsCompletedSuccessfully,
                StandardErrorArmed = observerArming.StandardError.IsCompletedSuccessfully,
                ExitArmed = observerArming.Exit.IsCompletedSuccessfully,
                StandardOutputCalls = process.StandardOutput.ReadCalls,
                StandardErrorCalls = process.StandardError.ReadCalls,
                ExitCalls = process.WaitCalls
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await Task.WhenAll(
                process.StandardOutput.InvocationEntered,
                process.StandardError.InvocationEntered,
                process.ExitInvocationEntered);
            await Task.WhenAll(
                delayedObserver == "stdout" ? Task.CompletedTask : observerArming.StandardOutput,
                delayedObserver == "stderr" ? Task.CompletedTask : observerArming.StandardError,
                delayedObserver == "exit" ? Task.CompletedTask : observerArming.Exit);

            var delayedReturned = delayedObserver switch
            {
                "stdout" => process.StandardOutput.FirstInvocationReturned,
                "stderr" => process.StandardError.FirstInvocationReturned,
                "exit" => process.ExitInvocationReturned,
                _ => throw new AssertFailedException($"Unexpected delayed observer '{delayedObserver}'.")
            };
            var delayedArmed = delayedObserver switch
            {
                "stdout" => observerArming.StandardOutput,
                "stderr" => observerArming.StandardError,
                "exit" => observerArming.Exit,
                _ => throw new AssertFailedException($"Unexpected delayed observer '{delayedObserver}'.")
            };

            Assert.IsFalse(delayedReturned.IsCompleted, $"{delayedObserver}: invocation-return-is-gated");
            Assert.IsFalse(delayedArmed.IsCompleted, $"{delayedObserver}: cannot-arm-before-invocation-returns");
            Assert.IsFalse(launch.IsCompleted, $"{delayedObserver}: launch-remains-pending-before-effective-arming");
            Assert.IsFalse(process.StandardOutput.FirstResultConsumed.IsCompleted, $"{delayedObserver}: stdout-first-result-not-consumed-before-all-armed");
            Assert.IsFalse(process.StandardError.FirstResultConsumed.IsCompleted, $"{delayedObserver}: stderr-first-result-not-consumed-before-all-armed");
            Assert.IsFalse(sink.Entered.IsCompleted, $"{delayedObserver}: sink-not-entered-before-all-armed");
            Assert.IsFalse(process.StandardOutput.ArmingWasAcknowledgedAtEntry, "stdout: invocation-precedes-acknowledgement");
            Assert.IsFalse(process.StandardError.ArmingWasAcknowledgedAtEntry, "stderr: invocation-precedes-acknowledgement");
            Assert.IsFalse(process.ExitArmingWasAcknowledgedAtEntry, "exit: invocation-precedes-acknowledgement");

            process.ReleaseDelayedInvocation();

            await Task.WhenAll(
                process.StandardOutput.FirstInvocationReturned,
                process.StandardError.FirstInvocationReturned,
                process.ExitInvocationReturned,
                observerArming.All);
            var result = await launch;
            var observedAtLaunchCompletion = await launchCompletionObservation;
            Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, $"{delayedObserver}: started-owner-returned");
            await Task.WhenAll(
                process.StandardOutput.FirstResultConsumed,
                process.StandardError.FirstResultConsumed,
                sink.Entered);

            Assert.IsTrue(observedAtLaunchCompletion.StandardOutputReturned, "stdout: invocation-returned-before-launch-completion");
            Assert.IsTrue(observedAtLaunchCompletion.StandardErrorReturned, "stderr: invocation-returned-before-launch-completion");
            Assert.IsTrue(observedAtLaunchCompletion.ExitReturned, "exit: invocation-returned-before-launch-completion");
            Assert.IsTrue(observedAtLaunchCompletion.StandardOutputArmed, "stdout: armed-before-launch-completion");
            Assert.IsTrue(observedAtLaunchCompletion.StandardErrorArmed, "stderr: armed-before-launch-completion");
            Assert.IsTrue(observedAtLaunchCompletion.ExitArmed, "exit: armed-before-launch-completion");
            Assert.AreEqual(1, observedAtLaunchCompletion.StandardOutputCalls, "stdout: initial-read-exactly-once-at-launch-completion");
            Assert.AreEqual(1, observedAtLaunchCompletion.StandardErrorCalls, "stderr: initial-read-exactly-once-at-launch-completion");
            Assert.AreEqual(1, observedAtLaunchCompletion.ExitCalls, "exit: wait-exactly-once-at-launch-completion");
        }
        finally
        {
            process.ReleaseDelayedInvocation();
            sink.Release();
            process.StandardOutput.Complete();
            process.Exit(0);
            var cleanupResult = await launch;
            if (cleanupResult is ChildWorkerLaunchResult.Started started)
            {
                await started.Session.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task LaunchAsync_CancellationAfterProcessCreationReturnsOwnedSessionAndFactoryUsesNone()
    {
        using var cancellation = new CancellationTokenSource();
        var process = new RecordingProcess(91, 0, new List<string>());
        var factory = new RecordingFactory { Process = process, AfterProcessCreated = cancellation.Cancel };

        var result = await new ChildWorkerLauncher(factory).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), TestOptions(), cancellation.Token);

        var started = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-owner-after-cancellation");
        Assert.AreEqual(CancellationToken.None, factory.CancellationToken, "factory-cancellation-token");
        Assert.AreEqual(1, factory.StartCalls, "factory-one-attempt");
        process.CompleteStreams();
        process.Exit();
        await started.Session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ConcurrentDisposeAsyncSharesOneGatedLifecycleAndDisposesOnce()
    {
        var trace = new List<string>();
        var process = new RecordingProcess(92, 0, trace);
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-result").Session;

        var firstDispose = session.DisposeAsync().AsTask();
        var secondDispose = session.DisposeAsync().AsTask();
        Assert.AreSame(firstDispose, secondDispose, "shared-dispose-task");
        Assert.IsFalse(firstDispose.IsCompleted, "completion-remains-gated");

        process.CompleteStreams();
        process.Exit();
        await firstDispose;

        Assert.AreEqual(1, process.StandardInput.DisposeCalls, "stdin-disposed-once");
        Assert.AreEqual(1, process.StandardOutput.DisposeCalls, "stdout-disposed-once");
        Assert.AreEqual(1, process.StandardError.DisposeCalls, "stderr-disposed-once");
        Assert.AreEqual(1, process.DisposeCalls, "process-disposed-once");
        Assert.AreEqual(1, process.StandardOutput.ReadCalls, "stdout-no-duplicate-reads");
        Assert.AreEqual(1, process.StandardError.ReadCalls, "stderr-no-duplicate-reads");
        Assert.AreEqual(1, process.WaitCalls, "exit-no-duplicate-waits");
    }

    [TestMethod]
    public void SystemFactory_UsesLiteralDescriptorAndPureEnvironmentPolicy()
    {
        var descriptor = new ChildProcessStartDescriptor(
            "/literal/worker-host",
            ["/literal/ImmichReverseGeo.Web.dll", "--internal-worker", "literal argument with spaces"],
            "/literal/working-directory",
            ChildProcessEnvironmentPolicy.InheritCurrentAndRemoveReservedProtocolVersion);

        var startInfo = SystemChildProcessFactory.CreateStartInfo(descriptor);

        Assert.AreEqual("/literal/worker-host", startInfo.FileName, "literal-executable");
        Assert.AreEqual("/literal/working-directory", startInfo.WorkingDirectory, "literal-working-directory");
        CollectionAssert.AreEqual(
            new[] { "/literal/ImmichReverseGeo.Web.dll", "--internal-worker", "literal argument with spaces" },
            startInfo.ArgumentList.ToArray(),
            "literal-arguments");
        Assert.IsTrue(startInfo.RedirectStandardInput, "redirect-stdin");
        Assert.IsTrue(startInfo.RedirectStandardOutput, "redirect-stdout");
        Assert.IsTrue(startInfo.RedirectStandardError, "redirect-stderr");
        Assert.IsFalse(startInfo.UseShellExecute, "shell-disabled");
        Assert.IsTrue(startInfo.CreateNoWindow, "no-window");

        var removeReserved = new Dictionary<string, string?>
        {
            ["IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION"] = "reserved",
            ["KEEP_ME"] = "retained"
        };
        SystemChildProcessFactory.ApplyEnvironmentPolicy(
            removeReserved,
            ChildProcessEnvironmentPolicy.InheritCurrentAndRemoveReservedProtocolVersion);
        Assert.IsFalse(removeReserved.ContainsKey("IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION"), "reserved-removed");
        Assert.AreEqual("retained", removeReserved["KEEP_ME"], "unrelated-retained-after-remove");

        var retainReserved = new Dictionary<string, string?>
        {
            ["IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION"] = "reserved",
            ["KEEP_ME"] = "retained"
        };
        SystemChildProcessFactory.ApplyEnvironmentPolicy(retainReserved, ChildProcessEnvironmentPolicy.InheritCurrent);
        Assert.AreEqual("reserved", retainReserved["IMMICH_REVERSEGEO_INTERNAL_WORKER_PROTOCOL_VERSION"], "reserved-retained");
        Assert.AreEqual("retained", retainReserved["KEEP_ME"], "unrelated-retained");
    }

    [TestMethod]
    public async Task Session_PreReadyCleanEndOfStreamPrecedesIncompleteProtocolObservation()
    {
        var process = new ByteProcess(810);
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-session").Session;

        process.StandardOutput.Complete();
        var startup = await session.Startup;
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.PreReadyEndOfStream>(startup, "pre-ready-eof-startup");
        Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation, "incomplete-stream-observation");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_DrainsBothStreamsBeforeCompletionAndDeliversReadyOnce()
    {
        var process = new ByteProcess(811);
        var sink = new RecordingSink();
        var request = CreateRequest();
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), request, sink, TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-session").Session;

        process.StandardOutput.Write(ProtocolBytes(ReadyFrame()));
        process.StandardError.Write(new byte[8193]);
        await sink.FirstAccepted;
        process.Exit(7);
        Assert.IsFalse(session.Completion.IsCompleted, "exit-does-not-complete-before-eof");
        process.StandardOutput.Complete();
        Assert.IsFalse(session.Completion.IsCompleted, "stdout-eof-does-not-complete-before-stderr-eof");
        process.StandardError.Complete();

        var completion = await session.Completion;
        Assert.AreEqual(1, sink.AcceptCalls, "ready-delivered-once");
        Assert.AreEqual(7, completion.ExitCode, "raw-exit-retained");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "stdout-eof-finality");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "stderr-eof-finality");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ReadFaultOnOneStreamDoesNotAbandonOtherStreamOrExit()
    {
        var process = new ByteProcess(814);
        var sink = new RecordingSink();
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), sink, TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-session").Session;

        process.StandardOutput.Fail();
        await session.Startup;
        process.StandardError.Write(new byte[2049]);
        process.Exit(3);
        Assert.IsFalse(session.Completion.IsCompleted, "stderr-remains-armed-after-stdout-fault");
        process.StandardError.Complete();
        var completion = await session.Completion;

        Assert.IsInstanceOfType<ChildWorkerStartupObservation.PreReadyReadFailed>(completion.Startup, "stdout-read-failure: startup-transport-fact");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.ReadFailed>(completion.StandardOutputFinality, "stdout-read-failure: only-stream-finality-channel");
        Assert.IsNull(completion.FirstProtocolObservation, "stdout-read-failure: no-protocol-or-sink-observation");
        Assert.AreEqual(0, sink.AcceptCalls, "stdout-read-failure: no-sink-delivery");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality, "stderr-eof-after-stdout-fault");
        Assert.AreEqual(3, completion.ExitCode, "exit-observed-after-stream-fault");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ReadyWritesOneCanonicalExecuteFrameThenFlushesAndLeavesInputOpen()
    {
        var process = new ByteProcess(815);
        var request = CreateRequest();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        var options = new ChildWorkerLauncherOptions { TimeProvider = clock };
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), request, new RecordingSink(), options, CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-session").Session;

        process.StandardOutput.Write(ProtocolBytes(ReadyFrame()));
        var startup = await session.Startup;
        var expected = Encoding.UTF8.GetBytes("{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"controller-to-worker\",\"category\":\"request\",\"type\":\"execute\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"trigger\":\"manual\"}}\n");
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(startup, "ready-accepted-after-flush");
        CollectionAssert.AreEqual(expected, process.StandardInput.ToArray(), "literal-execute-json-utf8-lf");
        CollectionAssert.AreEqual(new[] { "write", "flush" }, process.StandardInput.Trace, "write-before-flush");
        Assert.AreEqual(1, process.StandardInput.WriteCalls, "execute-one-full-write");
        Assert.AreEqual(1, process.StandardInput.FlushCalls, "execute-one-flush");
        Assert.AreEqual(0, process.StandardInput.DisposeCalls, "stdin-remains-open-before-session-disposal");

        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow("write", true, false)]
    [DataRow("flush", false, true)]
    public async Task Session_ExecuteTransportFailuresRemainDistinctAndNeverRetry(string label, bool throwAfterWrite, bool throwOnFlush)
    {
        var process = new ByteProcess(817);
        process.StandardInput.ThrowAfterWrite = throwAfterWrite;
        process.StandardInput.ThrowOnFlush = throwOnFlush;
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, $"{label}: started-session").Session;

        process.StandardOutput.Write(ProtocolBytes(ReadyFrame()));
        var startup = await session.Startup;

        if (throwAfterWrite)
        {
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.RequestWriteFailed>(startup, $"{label}: write-finality");
            Assert.AreEqual(0, process.StandardInput.FlushCalls, $"{label}: no-flush-after-write-failure");
        }
        else
        {
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.RequestFlushFailed>(startup, $"{label}: flush-finality");
            Assert.AreEqual(1, process.StandardInput.FlushCalls, $"{label}: one-flush");
        }

        Assert.AreEqual(1, process.StandardInput.WriteCalls, $"{label}: one-write-no-retry");
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_StandardErrorReadFaultDoesNotAbandonOutputOrExit()
    {
        var process = new ByteProcess(821);
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-session").Session;

        process.StandardError.Fail();
        process.Exit(4);
        Assert.IsFalse(session.Completion.IsCompleted, "stdout-remains-armed-after-stderr-fault");
        process.StandardOutput.Complete();
        var completion = await session.Completion;

        Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality, "stdout-eof-after-stderr-fault");
        Assert.IsInstanceOfType<ChildWorkerStreamFinality.ReadFailed>(completion.StandardErrorFinality, "stderr-read-failure");
        Assert.AreEqual(4, completion.ExitCode, "exit-observed-after-stderr-fault");
        await session.DisposeAsync();
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(65535, false)]
    [DataRow(65536, false)]
    [DataRow(65537, true)]
    public async Task Session_StandardErrorTailRetainsExactFinalBytes(int length, bool truncated)
    {
        var process = new ByteProcess(816);
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-session").Session;
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();

        process.StandardError.Write(bytes);
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        Assert.AreEqual((long)length, completion.StandardErrorTail.TotalBytes, "stderr-total");
        Assert.AreEqual(truncated, completion.StandardErrorTail.IsTruncated, "stderr-truncated");
        Assert.IsFalse(completion.StandardErrorTail.TotalBytesSaturated, "stderr-not-saturated");
        CollectionAssert.AreEqual(bytes.Skip(Math.Max(0, length - 65536)).ToArray(), completion.StandardErrorTail.Bytes.ToArray(), "stderr-final-suffix");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_CancelledStartupWaitDoesNotAlterReusableLifecycle()
    {
        var process = new ByteProcess(818);
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-session").Session;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => session.WaitForStartupAsync(cancellation.Token),
            "cancelled-wait-only");
        await Task.WhenAll(process.StandardOutput.ReadStarted.Task, process.StandardError.ReadStarted.Task, process.WaitStarted.Task);
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "cancelled-wait-no-stdin-write");
        Assert.AreEqual(1, process.StandardOutput.ReadCalls, "cancelled-wait-no-extra-stdout-read");
        Assert.AreEqual(1, process.StandardError.ReadCalls, "cancelled-wait-no-extra-stderr-read");
        Assert.AreEqual(1, process.WaitCalls, "cancelled-wait-no-extra-exit-wait");

        process.StandardOutput.Write(ProtocolBytes(ReadyFrame()));
        var startup = await session.WaitForStartupAsync();
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(startup, "later-wait-ready");
        Assert.AreEqual(1, process.StandardInput.WriteCalls, "later-wait-one-execute");
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_CancelledCompletionWaitDoesNotAlterReusableLifecycle()
    {
        var process = new ByteProcess(820);
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), new RecordingSink(), TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-session").Session;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => session.WaitForCompletionAsync(cancellation.Token),
            "cancelled-completion-wait-only");
        await Task.WhenAll(process.StandardOutput.ReadStarted.Task, process.StandardError.ReadStarted.Task, process.WaitStarted.Task);
        Assert.AreEqual(1, process.StandardOutput.ReadCalls, "cancelled-completion-no-extra-stdout-read");
        Assert.AreEqual(1, process.StandardError.ReadCalls, "cancelled-completion-no-extra-stderr-read");
        Assert.AreEqual(1, process.WaitCalls, "cancelled-completion-no-extra-exit-wait");

        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.WaitForCompletionAsync();
        Assert.AreEqual(0, completion.ExitCode, "later-completion-wait-exit");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_DisposeBeforeBufferedReady_SuppressesSinkAndExecute()
    {
        var process = new ByteProcess(819);
        var sink = new RecordingSink();
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), sink, TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "started-session").Session;

        var disposal = session.DisposeAsync().AsTask();
        process.StandardOutput.Write(ProtocolBytes(ReadyFrame()));
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        await disposal;

        Assert.AreEqual(0, sink.AcceptCalls, "disposed-ready-no-sink-callback");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "disposed-ready-no-execute-write");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "disposed-ready-no-execute-flush");
        Assert.AreEqual(1, process.StandardInput.DisposeCalls, "disposed-ready-stdin-once");
    }

    [TestMethod]
    public async Task Session_ValidPrefixThenProtocolFailureRetainsPrefixAndNeverRetriesExecute()
    {
        var process = new ByteProcess(822);
        var request = CreateRequest();
        var sink = new RecordingSink();
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), request, sink, TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "prefix-failure-started").Session;

        process.StandardOutput.Write(ProtocolBytes(ReadyFrame()));
        await sink.FirstAccepted;
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(await session.Startup);
        process.StandardOutput.Write(ProtocolBytes(RunStartedFrame(request.RunId) + "{\n"));
        process.StandardError.Write(Encoding.UTF8.GetBytes("stderr-after-prefix"));
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(9);
        var completion = await session.Completion;

        Assert.AreEqual(2, sink.AcceptCalls, "valid-prefix-delivered-exactly-once");
        Assert.AreEqual(1, process.StandardInput.WriteCalls, "prefix-failure-no-second-execute-write");
        Assert.AreEqual(1, process.StandardInput.FlushCalls, "prefix-failure-no-second-execute-flush");
        var first = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation, "prefix-failure-first-fact");
        Assert.AreEqual(WorkerProtocolFailureCode.MalformedJson, first.Failure.Code, "prefix-failure-code");
        Assert.AreEqual(9, completion.ExitCode, "prefix-failure-raw-exit");
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("stderr-after-prefix"), completion.StandardErrorTail.Bytes.ToArray(), "prefix-failure-stderr-drained");
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_WrongNonReadyRunIdIsNeverDelivered()
    {
        var process = new ByteProcess(823);
        var sink = new RecordingSink();
        var result = await new ChildWorkerLauncher(new RecordingFactory { Process = process }).LaunchAsync(
            CreateInvocation(), CreateRequest(), sink, TestOptions(), CancellationToken.None);
        var session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "wrong-run-id-started").Session;

        process.StandardOutput.Write(ProtocolBytes(RunStartedFrame(Guid.Parse("22222222-2222-2222-2222-222222222222"))));
        process.StandardOutput.Complete();
        process.StandardError.Complete();
        process.Exit(0);
        var completion = await session.Completion;

        var failure = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation, "wrong-run-id-first-failure");
        Assert.AreEqual(WorkerProtocolFailureCode.InvalidCorrelation, failure.Failure.Code, "wrong-run-id-failure-code");
        Assert.AreEqual(0, sink.AcceptCalls, "wrong-run-id-invalid-never-delivered");
        Assert.AreEqual(0, process.StandardInput.WriteCalls, "wrong-run-id-no-execute-write");
        Assert.AreEqual(0, process.StandardInput.FlushCalls, "wrong-run-id-no-execute-flush");
        await session.DisposeAsync();
    }

    private static byte[] ProtocolBytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string ReadyFrame() => "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-29T12:00:00.0000000Z\",\"runId\":null,\"payload\":{}}\n";

    private static string RunStartedFrame(Guid runId) => $"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"run-started\",\"sequence\":2,\"timestampUtc\":\"2026-08-29T12:00:01.0000000Z\",\"runId\":\"{runId:D}\",\"payload\":{{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-08-29T12:00:01.0000000Z\"}}}}\n";

    private static string EligibilityFrame(Guid runId) => $"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"eligibility-determined\",\"sequence\":3,\"timestampUtc\":\"2026-08-29T12:00:02.0000000Z\",\"runId\":\"{runId:D}\",\"payload\":{{\"eligibleCount\":0}}}}\n";

    private static string CompletedFrame(Guid runId) => $"{{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"completed\",\"sequence\":4,\"timestampUtc\":\"2026-08-29T12:00:03.0000000Z\",\"runId\":\"{runId:D}\",\"payload\":{{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-08-29T12:00:01.0000000Z\",\"endedAtUtc\":\"2026-08-29T12:00:03.0000000Z\",\"processedCount\":0,\"updatedCount\":0,\"skippedCount\":0,\"failedCount\":0,\"failureMessage\":null}}}}\n";

    private static WorkerInvocation CreateInvocation()
    {
        var resolution = WorkerInvocation.Resolve(new WorkerCommandRuntimeFacts(
            WorkerInvocation.TrustedWebAssemblyIdentity,
            "/usr/bin/dotnet",
            WorkerTargetObservation.File,
            WorkerInvocation.TrustedWebAssemblyIdentity,
            "/app/ImmichReverseGeo.Web.dll",
            WorkerTargetObservation.File,
            "/app",
            WorkerTargetObservation.Directory,
            WorkerPathSemantics.Unix));
        return Assert.IsInstanceOfType<WorkerCommandInvocationResolution.Success>(resolution).Invocation;
    }

    private static void AssertExpectedWorkerDescriptor(ChildProcessStartDescriptor descriptor)
    {
        Assert.AreEqual("/usr/bin/dotnet", descriptor.ExecutablePath, "worker-descriptor-executable");
        CollectionAssert.AreEqual(
            new[] { "/app/ImmichReverseGeo.Web.dll", "--internal-worker" },
            descriptor.Arguments.ToArray(),
            "worker-descriptor-arguments");
        Assert.AreEqual("/app", descriptor.WorkingDirectory, "worker-descriptor-working-directory");
        Assert.AreEqual(
            ChildProcessEnvironmentPolicy.InheritCurrentAndRemoveReservedProtocolVersion,
            descriptor.EnvironmentPolicy,
            "worker-descriptor-environment-policy");
    }

    private static ProcessingRunRequest CreateRequest() => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), ProcessingRunTrigger.Manual);

    private static ChildWorkerLauncherOptions TestOptions() => new()
    {
        TimeProvider = new FixedTimeProvider(DateTimeOffset.UnixEpoch),
        ReadyTimeout = Timeout.InfiniteTimeSpan
    };


    private static void AssertObserverStartedExactlyOnce(
        ByteProcess process,
        ChildWorkerObserverArmingAcknowledgements observerArming,
        string label)
    {
        Assert.IsTrue(observerArming.StandardOutput.IsCompletedSuccessfully, $"{label}: stdout-arming-acknowledged");
        Assert.IsTrue(observerArming.StandardError.IsCompletedSuccessfully, $"{label}: stderr-arming-acknowledged");
        Assert.IsTrue(observerArming.Exit.IsCompletedSuccessfully, $"{label}: exit-arming-acknowledged");
        Assert.IsTrue(process.StandardOutput.ReadStarted.Task.IsCompletedSuccessfully, $"{label}: stdout-read-started-before-started-result");
        Assert.IsTrue(process.StandardError.ReadStarted.Task.IsCompletedSuccessfully, $"{label}: stderr-read-started-before-started-result");
        Assert.IsTrue(process.WaitStarted.Task.IsCompletedSuccessfully, $"{label}: exit-wait-started-before-started-result");
        Assert.AreEqual(1, process.StandardOutput.ReadCalls, $"{label}: exactly-one-stdout-read");
        Assert.AreEqual(1, process.StandardError.ReadCalls, $"{label}: exactly-one-stderr-read");
        Assert.AreEqual(1, process.WaitCalls, $"{label}: exactly-one-exit-wait");
    }

    private static void AssertDisposedExactlyOnce(ByteProcess process, string label)
    {
        Assert.AreEqual(1, process.StandardInput.DisposeCalls, $"{label}: stdin-disposed-once");
        Assert.AreEqual(1, process.StandardOutput.DisposeCalls, $"{label}: stdout-disposed-once");
        Assert.AreEqual(1, process.StandardError.DisposeCalls, $"{label}: stderr-disposed-once");
        Assert.AreEqual(1, process.DisposeCalls, $"{label}: process-disposed-once");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly TaskCompletionSource _firstCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        internal int CreateCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal TimeSpan? LastDueTime { get; private set; }
        internal TimeSpan? LastPeriod { get; private set; }
        internal Task TimerCreated => _firstCreated.Task;
        internal Task FirstDisposed => _firstDisposed.Task;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _now.UtcTicks;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                CreateCalls++;
                LastDueTime = dueTime;
                LastPeriod = period;
                var timer = new ManualTimer(this, callback, state, _now + dueTime);
                _timers.Add(timer);
                _firstCreated.TrySetResult();
                return timer;
            }
        }

        internal void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += elapsed;
                due = _timers.Where(timer => !timer.IsDisposed && timer.DueAt <= _now).ToList();
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private void RecordDisposal()
        {
            lock (_gate)
            {
                DisposeCalls++;
            }

            _firstDisposed.TrySetResult();
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt) : ITimer
        {
            private int _disposed;
            internal DateTimeOffset DueAt { get; private set; } = dueAt;
            internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueAt = owner.GetUtcNow() + dueTime;
                return !IsDisposed;
            }

            public void Dispose()
            {
                owner.RecordDisposal();
                Interlocked.Exchange(ref _disposed, 1);
            }

            public ValueTask DisposeAsync()
            {
                owner.RecordDisposal();
                Interlocked.Exchange(ref _disposed, 1);
                return ValueTask.CompletedTask;
            }

            internal void Fire()
            {
                if (!IsDisposed)
                {
                    callback(state);
                }
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => now.UtcTicks;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            return new NeverTimer();
        }

        private sealed class NeverTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingGetUtcNowTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Deterministic serialization clock failure.");
        public override long GetTimestamp() => 0;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            throw new AssertFailedException("The infinite ready timeout must not create a timer.");
        }
    }

    private sealed class ObserverArmingProbeProcess : IChildProcess, IDisposable
    {
        private readonly ChildWorkerObserverArmingAcknowledgements _observerArming;
        private readonly string _delayedObserver;
        private readonly ManualResetEventSlim _releaseDelayedInvocation = new(false);
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exitInvocationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exitInvocationReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waitCalls;
        private int _exitArmingWasAcknowledgedAtEntry;

        internal ObserverArmingProbeProcess(
            ChildWorkerObserverArmingAcknowledgements observerArming,
            string delayedObserver,
            byte[] standardOutputFirstResult,
            int? rawExitCode = null)
        {
            _observerArming = observerArming;
            _delayedObserver = delayedObserver;
            StandardInput = new RecordingInputStream();
            StandardOutput = new ObserverArmingProbeStream(
                observerArming.StandardOutput,
                delayedObserver == "stdout",
                _releaseDelayedInvocation,
                standardOutputFirstResult);
            StandardError = new ObserverArmingProbeStream(
                observerArming.StandardError,
                delayedObserver == "stderr",
                _releaseDelayedInvocation,
                []);
            if (rawExitCode is int exitCode)
            {
                _exit.SetResult(exitCode);
            }
        }

        public int ProcessId => 419;
        internal RecordingInputStream StandardInput { get; }
        internal ObserverArmingProbeStream StandardOutput { get; }
        internal ObserverArmingProbeStream StandardError { get; }
        internal int WaitCalls => Volatile.Read(ref _waitCalls);
        internal bool ExitArmingWasAcknowledgedAtEntry => Volatile.Read(ref _exitArmingWasAcknowledgedAtEntry) != 0;
        internal Task ExitInvocationEntered => _exitInvocationEntered.Task;
        internal Task ExitInvocationReturned => _exitInvocationReturned.Task;
        Stream IChildProcess.StandardInput => StandardInput;
        Stream IChildProcess.StandardOutput => StandardOutput;
        Stream IChildProcess.StandardError => StandardError;

        public Task<int> WaitForExitAsync()
        {
            Interlocked.Increment(ref _waitCalls);
            if (_observerArming.Exit.IsCompleted)
            {
                Interlocked.Exchange(ref _exitArmingWasAcknowledgedAtEntry, 1);
            }

            _exitInvocationEntered.TrySetResult();
            if (_delayedObserver == "exit")
            {
                _releaseDelayedInvocation.Wait();
            }

            _exitInvocationReturned.TrySetResult();
            return _exit.Task;
        }

        public ChildProcessExitState GetExitState()
            => _exit.Task.IsCompletedSuccessfully ? ChildProcessExitState.Exited : ChildProcessExitState.Alive;

        public ChildProcessKillOutcome KillProcessTree()
            => GetExitState() == ChildProcessExitState.Exited ? ChildProcessKillOutcome.AlreadyExited : ChildProcessKillOutcome.Failed;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Dispose() => _releaseDelayedInvocation.Dispose();

        internal void ReleaseDelayedInvocation() => _releaseDelayedInvocation.Set();
        internal void Exit(int exitCode) => _exit.TrySetResult(exitCode);
    }

    private sealed class ObserverArmingProbeStream : Stream
    {
        private readonly Task _observerArming;
        private readonly bool _delayFirstInvocation;
        private readonly ManualResetEventSlim _releaseFirstInvocation;
        private readonly byte[] _firstResult;
        private readonly TaskCompletionSource<int> _remainingReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _invocationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstInvocationReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstResultConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCalls;
        private int _armingWasAcknowledgedAtEntry;

        internal ObserverArmingProbeStream(
            Task observerArming,
            bool delayFirstInvocation,
            ManualResetEventSlim releaseFirstInvocation,
            byte[] firstResult)
        {
            _observerArming = observerArming;
            _delayFirstInvocation = delayFirstInvocation;
            _releaseFirstInvocation = releaseFirstInvocation;
            _firstResult = firstResult;
        }

        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal bool ArmingWasAcknowledgedAtEntry => Volatile.Read(ref _armingWasAcknowledgedAtEntry) != 0;
        internal Task InvocationEntered => _invocationEntered.Task;
        internal Task FirstInvocationReturned => _firstInvocationReturned.Task;
        internal Task FirstResultConsumed => _firstResultConsumed.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _readCalls);
            if (call != 1)
            {
                return new ValueTask<int>(_remainingReads.Task);
            }

            if (_observerArming.IsCompleted)
            {
                Interlocked.Exchange(ref _armingWasAcknowledgedAtEntry, 1);
            }

            _invocationEntered.TrySetResult();
            if (_delayFirstInvocation)
            {
                _releaseFirstInvocation.Wait(cancellationToken);
            }

            if (_firstResult.Length > buffer.Length)
            {
                throw new InvalidOperationException("The deterministic first result exceeded the supplied read buffer.");
            }

            _firstResult.CopyTo(buffer);
            _firstInvocationReturned.TrySetResult();
            return new ValueTask<int>(
                new SignaledSynchronousReadResult(_firstResult.Length, _firstResultConsumed),
                0);
        }

        public override ValueTask DisposeAsync()
        {
            Complete();
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        internal void Complete() => _remainingReads.TrySetResult(0);
    }

    private sealed class SignaledSynchronousReadResult(
        int result,
        TaskCompletionSource consumed) : System.Threading.Tasks.Sources.IValueTaskSource<int>
    {
        public int GetResult(short token)
        {
            consumed.TrySetResult();
            return result;
        }

        public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token) =>
            System.Threading.Tasks.Sources.ValueTaskSourceStatus.Succeeded;

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags) =>
            continuation(state);
    }

    private sealed class ObserverArmingProbeSink : IWorkerProtocolEventSink
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Entered => _entered.Task;

        public async ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }

        internal void Release() => _release.TrySetResult();
    }

    private sealed class RecordingSink : IWorkerProtocolEventSink
    {
        private readonly object _gate = new();
        private readonly List<WorkerProtocolEvent> _events = [];
        private readonly List<CancellationToken> _tokens = [];
        private readonly TaskCompletionSource _firstAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blockedCallEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseBlockedCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCallbacks;
        private int _acceptCalls;
        private int _maximumConcurrency;

        internal int? BlockCall { get; init; }
        internal int? FailCall { get; init; }
        internal int AcceptCalls => Volatile.Read(ref _acceptCalls);
        internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        internal Task FirstAccepted => _firstAccepted.Task;
        internal Task BlockedCallEntered => _blockedCallEntered.Task;
        internal IReadOnlyList<WorkerProtocolEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        internal IReadOnlyList<CancellationToken> Tokens
        {
            get
            {
                lock (_gate)
                {
                    return _tokens.ToArray();
                }
            }
        }

        internal void ReleaseBlockedCall() => _releaseBlockedCall.TrySetResult();

        public async ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _acceptCalls);
            var active = Interlocked.Increment(ref _activeCallbacks);
            lock (_gate)
            {
                _maximumConcurrency = Math.Max(_maximumConcurrency, active);
                _events.Add(@event);
                _tokens.Add(cancellationToken);
            }

            _firstAccepted.TrySetResult();
            try
            {
                if (BlockCall == call)
                {
                    _blockedCallEntered.TrySetResult();
                    await _releaseBlockedCall.Task.ConfigureAwait(false);
                }

                if (FailCall == call)
                {
                    throw new InvalidOperationException("Deterministic sink failure.");
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeCallbacks);
            }
        }
    }

    private sealed class RecordingFactory : IChildProcessFactory
    {
        internal int StartCalls { get; private set; }
        internal ChildProcessStartDescriptor? Descriptor { get; private set; }
        internal CancellationToken CancellationToken { get; private set; }
        internal IChildProcess? Process { get; init; }
        internal Exception? StartException { get; init; }
        internal Action? AfterProcessCreated { get; init; }

        public ValueTask<IChildProcess?> StartAsync(ChildProcessStartDescriptor descriptor, CancellationToken cancellationToken)
        {
            StartCalls++;
            Descriptor = descriptor;
            CancellationToken = cancellationToken;
            if (StartException is not null)
            {
                throw StartException;
            }

            AfterProcessCreated?.Invoke();
            return ValueTask.FromResult(Process);
        }
    }


    private sealed class QueuedRecordingFactory : IChildProcessFactory
    {
        private readonly object _gate = new();
        private readonly Queue<IChildProcess> _processes;
        private readonly TaskCompletionSource _allStartsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStarts = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startCalls;

        internal QueuedRecordingFactory(params IChildProcess[] processes)
        {
            _processes = new Queue<IChildProcess>(processes);
        }

        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal Task AllStartsEntered => _allStartsEntered.Task;

        internal void ReleaseStarts() => _releaseStarts.TrySetResult();

        public async ValueTask<IChildProcess?> StartAsync(ChildProcessStartDescriptor descriptor, CancellationToken cancellationToken)
        {
            IChildProcess process;
            lock (_gate)
            {
                process = _processes.Dequeue();
                if (Interlocked.Increment(ref _startCalls) == 2)
                {
                    _allStartsEntered.TrySetResult();
                }
            }

            await _releaseStarts.Task.ConfigureAwait(false);
            return process;
        }
    }

    private sealed class QueuedObserverArmingFactory
    {
        private readonly object _gate = new();
        private readonly List<ChildWorkerObserverArmingAcknowledgements> _instances = [];
        private int _createCalls;

        internal int CreateCalls => Volatile.Read(ref _createCalls);
        internal IReadOnlyList<ChildWorkerObserverArmingAcknowledgements> Instances
        {
            get
            {
                lock (_gate)
                {
                    return _instances.ToArray();
                }
            }
        }

        internal ChildWorkerObserverArmingAcknowledgements Create()
        {
            var observerArming = new ChildWorkerObserverArmingAcknowledgements();
            lock (_gate)
            {
                _instances.Add(observerArming);
            }

            Interlocked.Increment(ref _createCalls);
            return observerArming;
        }
    }


    private sealed class SetupFailingProcess(string failingGetter, bool disposeThrows) : IChildProcess
    {
        private readonly MemoryStream _standardInput = new();
        private readonly MemoryStream _standardOutput = new();
        private readonly MemoryStream _standardError = new();

        public int ProcessId => Get("process-id", 417);
        Stream IChildProcess.StandardInput => Get("standard-input", _standardInput);
        Stream IChildProcess.StandardOutput => Get("standard-output", _standardOutput);
        Stream IChildProcess.StandardError => Get("standard-error", _standardError);
        internal int WaitCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public Task<int> WaitForExitAsync()
        {
            WaitCalls++;
            return Task.FromResult(0);
        }

        public ChildProcessExitState GetExitState() => ChildProcessExitState.Exited;
        public ChildProcessKillOutcome KillProcessTree() => ChildProcessKillOutcome.AlreadyExited;

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (disposeThrows)
            {
                throw new InvalidOperationException("Deterministic cleanup failure.");
            }

            return ValueTask.CompletedTask;
        }

        private T Get<T>(string getter, T value)
        {
            if (failingGetter == getter)
            {
                throw new InvalidOperationException("Deterministic post-start setup failure.");
            }

            return value;
        }
    }

    private sealed class RecordingProcess : IChildProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _trace;
        private bool _exitWaitRecorded;

        internal RecordingProcess(int processId, int exitCode, List<string> trace)
        {
            ProcessId = processId;
            ExitCode = exitCode;
            _trace = trace;
            StandardInput = new RecordingInputStream();
            StandardOutput = new GatedReadStream("stdout-read", trace);
            StandardError = new GatedReadStream("stderr-read", trace);
        }

        internal int ExitCode { get; }
        internal int WaitCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal Task WaitStarted => _waitStarted.Task;
        internal RecordingInputStream StandardInput { get; }
        internal GatedReadStream StandardOutput { get; }
        internal GatedReadStream StandardError { get; }
        Stream IChildProcess.StandardInput => StandardInput;
        Stream IChildProcess.StandardOutput => StandardOutput;
        Stream IChildProcess.StandardError => StandardError;
        public int ProcessId { get; }

        public Task<int> WaitForExitAsync()
        {
            WaitCalls++;
            lock (_trace)
            {
                if (!_exitWaitRecorded)
                {
                    _trace.Add("exit-wait");
                    _exitWaitRecorded = true;
                }
            }

            _waitStarted.TrySetResult();
            return _exit.Task;
        }

        public ChildProcessExitState GetExitState()
            => _exit.Task.IsCompletedSuccessfully ? ChildProcessExitState.Exited : ChildProcessExitState.Alive;

        public ChildProcessKillOutcome KillProcessTree()
            => GetExitState() == ChildProcessExitState.Exited ? ChildProcessKillOutcome.AlreadyExited : ChildProcessKillOutcome.Failed;

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }

        internal void CompleteStreams()
        {
            StandardOutput.Complete();
            StandardError.Complete();
        }

        internal void Exit() => _exit.TrySetResult(ExitCode);
    }

    private sealed class RecordingInputStream : MemoryStream
    {
        internal int DisposeCalls { get; private set; }
        internal int WriteCalls { get; private set; }
        internal int FlushCalls { get; private set; }
        internal bool ThrowAfterWrite { get; set; }
        internal bool ThrowOnFlush { get; set; }
        internal int? FailWriteAfterBytes { get; set; }
        internal List<string> Trace { get; } = [];

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            Trace.Add("write");
            if (FailWriteAfterBytes is int retainedBytes)
            {
                base.Write(buffer.Span[..retainedBytes]);
                throw new IOException("Deterministic prefix write failure.");
            }

            var write = base.WriteAsync(buffer, cancellationToken);
            if (ThrowAfterWrite)
            {
                throw new IOException();
            }

            return write;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            Trace.Add("flush");
            if (ThrowOnFlush)
            {
                throw new IOException();
            }

            return base.FlushAsync(cancellationToken);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return base.DisposeAsync();
        }
    }

    private sealed class GatedReadStream : Stream
    {
        private readonly string _readTrace;
        private readonly List<string> _trace;
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal GatedReadStream(string readTrace, List<string> trace)
        {
            _readTrace = readTrace;
            _trace = trace;
        }

        internal int ReadCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal Task ReadStarted => _readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            lock (_trace)
            {
                _trace.Add(_readTrace);
            }

            _readStarted.TrySetResult();
            return new ValueTask<int>(_read.Task);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        internal void Complete() => _read.TrySetResult(0);
    }

    private sealed class ThrowingSink : IWorkerProtocolEventSink
    {
        internal int AcceptCalls { get; private set; }

        public ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
        {
            AcceptCalls++;
            throw new InvalidOperationException();
        }
    }

    private sealed class ByteProcess : IChildProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _physicalExitConfirmed;
        internal void ConfirmPhysicalExitWithoutCode() => Volatile.Write(ref _physicalExitConfirmed, 1);

        internal ByteProcess(int processId)
        {
            ProcessId = processId;
            StandardInput = new RecordingInputStream();
            StandardOutput = new ByteReadStream();
            StandardError = new ByteReadStream();
        }

        public int ProcessId { get; }
        internal RecordingInputStream StandardInput { get; }
        internal ByteReadStream StandardOutput { get; }
        internal ByteReadStream StandardError { get; }
        internal int WaitCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Stream IChildProcess.StandardInput => StandardInput;
        Stream IChildProcess.StandardOutput => StandardOutput;
        Stream IChildProcess.StandardError => StandardError;
        public Task<int> WaitForExitAsync()
        {
            WaitCalls++;
            WaitStarted.TrySetResult();
            return _exit.Task;
        }

        public ChildProcessExitState GetExitState()
        {
            if (Volatile.Read(ref _physicalExitConfirmed) != 0 || _exit.Task.IsCompletedSuccessfully)
            {
                return ChildProcessExitState.Exited;
            }

            return _exit.Task.IsFaulted ? ChildProcessExitState.Unavailable : ChildProcessExitState.Alive;
        }

        public ChildProcessKillOutcome KillProcessTree()
            => GetExitState() == ChildProcessExitState.Exited ? ChildProcessKillOutcome.AlreadyExited : ChildProcessKillOutcome.Failed;

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }

        internal void Exit(int exitCode) => _exit.TrySetResult(exitCode);
        internal void FailExit() => _exit.TrySetException(new InvalidOperationException("Deterministic exit observation failure."));
    }

    private sealed class ByteReadStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        private readonly object _consumptionGate = new();
        private readonly List<(long Target, TaskCompletionSource Completion)> _consumptionWaiters = [];
        private readonly TaskCompletionSource _readsReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _current;
        private int _offset;
        private Exception? _failure;
        private int _readCalls;
        private long _consumedBytes;
        private bool _readsPaused;

        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal int DisposeCalls { get; private set; }
        internal long ConsumedBytes => Interlocked.Read(ref _consumedBytes);
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        internal void PauseReads() => _readsPaused = true;
        internal void ReleaseReads() => _readsReleased.TrySetResult();

        internal void Write(byte[] bytes)
        {
            _chunks.Writer.WriteAsync(bytes).AsTask().GetAwaiter().GetResult();
        }

        internal ValueTask WriteAsyncForTest(byte[] bytes) => _chunks.Writer.WriteAsync(bytes);

        internal async Task FeedAsync(ReadOnlyMemory<byte> bytes, int chunkSize)
        {
            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                var count = Math.Min(chunkSize, bytes.Length - offset);
                await _chunks.Writer.WriteAsync(bytes.Slice(offset, count).ToArray()).ConfigureAwait(false);
            }
        }

        internal Task WaitForConsumedAsync(long target)
        {
            lock (_consumptionGate)
            {
                if (_consumedBytes >= target)
                {
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _consumptionWaiters.Add((target, completion));
                return completion.Task;
            }
        }

        internal void Complete() => _chunks.Writer.TryComplete();
        internal void Fail() => _chunks.Writer.TryComplete(new IOException("Deterministic stream read failure."));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCalls);
            ReadStarted.TrySetResult();
            if (_readsPaused)
            {
                await _readsReleased.Task.ConfigureAwait(false);
            }

            if (_failure is not null)
            {
                throw _failure;
            }

            if (_current is null || _offset == _current.Length)
            {
                try
                {
                    _current = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                }
                catch (ChannelClosedException exception) when (exception.InnerException is not null)
                {
                    _failure = exception.InnerException;
                    throw _failure;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            RecordConsumption(count);
            return count;
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }

        private void RecordConsumption(int count)
        {
            List<TaskCompletionSource> completed = [];
            lock (_consumptionGate)
            {
                _consumedBytes += count;
                for (var index = _consumptionWaiters.Count - 1; index >= 0; index--)
                {
                    if (_consumedBytes >= _consumptionWaiters[index].Target)
                    {
                        completed.Add(_consumptionWaiters[index].Completion);
                        _consumptionWaiters.RemoveAt(index);
                    }
                }
            }

            foreach (var completion in completed)
            {
                completion.TrySetResult();
            }
        }
    }
}
