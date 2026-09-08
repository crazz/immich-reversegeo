using System.Collections.Concurrent;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ChildWorkerExecution;

[TestClass]
[TestCategory("Change33")]
public sealed class ScheduledBackendCancellationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    [TestCategory("Change35")]
    public async Task ScheduledChild_PreCancelledTokenFinalizesLocallyWithoutLaunchAndThrowsCallerTokenAfterCleanup()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        await using var fixture = ScheduledChildFixture.Create(new ImmediateInvocationBuilder(), clock);
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        Task<ScheduledTriggerResult> scheduled =
            ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(stopping.Token);

        OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(
            () => scheduled.WaitAsync(Bound));
        Assert.AreEqual(stopping.Token, failure.CancellationToken, "pre-cancel-preserves-caller-token");
        Assert.AreEqual(0, fixture.Launcher.CallCount, "pre-cancel-no-child");
        Assert.IsFalse(fixture.State.IsRunning, "pre-cancel-returns-idle");
        Assert.IsNull(fixture.State.LastError, "pre-cancel-no-error");
        Assert.IsNull(fixture.Coordinator.ActiveRequest, "pre-cancel-releases-after-cleanup");
        IReadOnlyList<string> logs = fixture.State.GetRecentLog();
        Assert.AreEqual(1, logs.Count(line => line.EndsWith("Run cancelled.", StringComparison.Ordinal)), "pre-cancel-one-cancellation");
        Assert.AreEqual(1, logs.Count(line => line.EndsWith("Run complete. Processed=0 Skipped=0 Errors=0", StringComparison.Ordinal)), "pre-cancel-one-summary");
    }

    [TestMethod]
    public async Task ScheduledChild_CancellationDuringCommandResolutionStopsSessionAttachedLater()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var builder = new GatedInvocationBuilder();
        await using var fixture = ScheduledChildFixture.Create(builder, clock);
        using var stopping = new CancellationTokenSource();
        ScheduledChildLaunch? launch = null;
        Task<ScheduledTriggerResult> scheduled = Task.Run(
            () => ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(stopping.Token));

        try
        {
            await builder.Entered.Task.WaitAsync(Bound);
            ProcessingRunRequest admitted = fixture.Coordinator.ActiveRequest!;
            stopping.Cancel();

            Assert.AreSame(admitted, fixture.Coordinator.ActiveRequest, "resolution-cancel-retains-handle");
            Assert.IsTrue(fixture.State.IsRunning, "resolution-cancel-remains-pending");
            builder.Release.TrySetResult();
            launch = await fixture.Launcher.NextAsync();
            ChildWorkerTerminationRequest termination =
                await launch.Session.FirstTerminationRequest.WaitAsync(Bound);

            Assert.AreSame(admitted, launch.Request, "resolution-cancel-attaches-exact-request");
            Assert.AreEqual(ChildWorkerTerminationIntent.Stop, termination.Intent, "resolution-cancel-stop-intent");
            Assert.AreEqual(SessionTestSupport.Start, termination.Deadline.FirstStopAtUtc, "resolution-cancel-retains-first-deadline");

            await ReadyAndAcceptAsync(launch);
            await launch.Input.SecondFlush.WaitAsync(Bound);
            EmitTerminal(launch, ProcessingRunOutcome.Cancelled);
            launch.Process.Exit(130);

            OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(
                () => scheduled.WaitAsync(Bound));
            Assert.AreEqual(stopping.Token, failure.CancellationToken, "resolution-cancel-preserves-caller-token");
            Assert.AreEqual(1, fixture.Launcher.CallCount, "resolution-cancel-one-child");
            Assert.AreEqual(1, launch.Process.DisposeCalls, "resolution-cancel-process-reaped-once");
            Assert.IsFalse(fixture.State.IsRunning, "resolution-cancel-returns-idle");
        }
        finally
        {
            builder.Release.TrySetResult();
            launch?.Process.Exit(130);
        }
    }

    [TestMethod]
    public async Task ScheduledChild_CancellationAfterExecuteUsesOneGraceOwnerAndJoinsManualStop()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        await using var fixture = ScheduledChildFixture.Create(new ImmediateInvocationBuilder(), clock);
        using var stopping = new CancellationTokenSource();
        ScheduledChildLaunch? launch = null;
        Task<ScheduledTriggerResult> scheduled =
            ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(stopping.Token);

        try
        {
            launch = await fixture.Launcher.NextAsync();
            await ReadyAndAcceptAsync(launch);
            long timerGeneration = clock.TimerGeneration;
            stopping.Cancel();
            ChildWorkerTerminationRequest termination =
                await launch.Session.FirstTerminationRequest.WaitAsync(Bound);
            await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(Bound);

            Task promptStop = fixture.Coordinator.StopActiveRun()
                ?? throw new AssertFailedException("The scheduled child must retain its active handle.");
            Assert.AreSame(promptStop, fixture.Coordinator.WaitForActiveRunAsync(), "accepted-cancel-manual-stop-joins");
            Assert.AreEqual(timerGeneration + 1, clock.TimerGeneration, "accepted-cancel-one-grace-timer");
            Assert.AreEqual(ChildWorkerTerminationIntent.Stop, termination.Intent, "accepted-cancel-stop-intent");
            Assert.AreEqual(SessionTestSupport.Start, termination.Deadline.FirstStopAtUtc, "accepted-cancel-first-deadline");

            await launch.Input.SecondFlush.WaitAsync(Bound);
            EmitTerminal(launch, ProcessingRunOutcome.Cancelled);
            await fixture.WaitForReceiptAsync(launch.Request).WaitAsync(Bound);
            Assert.IsFalse(promptStop.IsCompleted, "accepted-cancel-stop-waits-for-exit");
            launch.Process.Exit(130);

            await promptStop.WaitAsync(Bound);
            OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(
                () => scheduled.WaitAsync(Bound));
            Assert.AreEqual(stopping.Token, failure.CancellationToken, "accepted-cancel-preserves-caller-token");
            Assert.AreEqual(1, fixture.Launcher.CallCount, "accepted-cancel-one-child");
            Assert.AreEqual(ChildWorkerTerminationIntent.Stop, launch.Session.CancellationFacts!.FirstIntent, "accepted-cancel-one-owner");
            Assert.IsFalse(launch.Session.CancellationFacts.GraceExpired, "accepted-cancel-exits-within-grace");
            Assert.AreEqual(0, launch.Process.KillCalls, "accepted-cancel-no-containment-before-grace");
        }
        finally
        {
            launch?.Process.Exit(130);
        }
    }

    [TestMethod]
    public async Task ScheduledChild_OldCancellationCallbackSettlesBeforeReplacementAdmission()
    {
        var clock = new BlockingTimestampClock(SessionTestSupport.Start);
        await using var fixture = ScheduledChildFixture.Create(new ImmediateInvocationBuilder(), clock);
        using var firstStopping = new CancellationTokenSource();
        ScheduledChildLaunch? first = null;
        ScheduledChildLaunch? second = null;
        Task? cancellation = null;

        Task<ScheduledTriggerResult> firstScheduled =
            ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(firstStopping.Token);
        try
        {
            first = await fixture.Launcher.NextAsync();
            await ReadyAndAcceptAsync(first);
            clock.BlockNextTimestamp();
            cancellation = Task.Run(firstStopping.Cancel);
            await clock.TimestampBlocked.WaitAsync(Bound);

            EmitTerminal(first, ProcessingRunOutcome.Cancelled);
            first.Process.Exit(130);
            Assert.AreEqual(
                ProcessingRunAdmissionResult.AlreadyRunning,
                await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
                "old-callback-blocks-handle-release");
            Assert.AreSame(first.Request, fixture.Coordinator.ActiveRequest, "old-callback-retains-exact-handle");

            clock.ReleaseTimestamp();
            await cancellation.WaitAsync(Bound);
            OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(
                () => firstScheduled.WaitAsync(Bound));
            Assert.AreEqual(firstStopping.Token, failure.CancellationToken, "old-callback-preserves-first-token");

            Task<ScheduledTriggerResult> replacement =
                ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None);
            second = await fixture.Launcher.NextAsync();
            await ReadyAndAcceptAsync(second);
            EmitTerminal(second, ProcessingRunOutcome.Completed);
            second.Process.Exit(0);

            Assert.AreEqual(
                ScheduledTriggerResult.AcceptedAfterTerminal,
                await replacement.WaitAsync(Bound),
                "replacement-completes-without-old-callback");
            Assert.AreNotSame(first.Request, second.Request, "replacement-has-new-handle");
            Assert.IsNull(second.Session.CancellationFacts, "old-callback-cannot-stop-replacement");
            Assert.AreEqual(1, second.Input.Frames.Count, "replacement-receives-only-execute-command");
            Assert.AreEqual(2, fixture.Launcher.CallCount, "replacement-one-child-per-run");
        }
        finally
        {
            clock.ReleaseTimestamp();
            first?.Process.Exit(130);
            second?.Process.Exit(0);
            if (cancellation is not null)
            {
                await cancellation.WaitAsync(Bound);
            }
        }
    }

    [TestMethod]
    public async Task FixtureDispose_ExitsRawProcessCreatedBeforeSessionPublication()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var fixture = ScheduledChildFixture.Create(
            new ImmediateInvocationBuilder(),
            clock,
            gateSessionCreation: true);
        Task<ScheduledTriggerResult> scheduled =
            ((IScheduledRunTrigger)fixture.Coordinator).TriggerScheduledAsync(CancellationToken.None);
        SessionTestProcess? process = null;
        Task? disposal = null;

        try
        {
            await fixture.Launcher.SessionCreationEntered.Task.WaitAsync(Bound);
            process = fixture.Launcher.RawProcess!;
            disposal = fixture.DisposeAsync().AsTask();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            fixture.Launcher.ReleaseSessionCreation.TrySetResult();
            (process ?? fixture.Launcher.RawProcess)?.Exit(143);
            disposal ??= fixture.DisposeAsync().AsTask();
            try
            {
                await disposal.WaitAsync(Bound);
            }
            finally
            {
                try
                {
                    await scheduled.WaitAsync(Bound);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        Assert.AreEqual(ChildProcessExitState.Exited, process!.GetExitState(), "fixture-dispose-exits-unpublished-process");
        Assert.AreEqual(1, process.DisposeCalls, "fixture-dispose-reaps-unpublished-process-once");
    }

    private static async Task ReadyAndAcceptAsync(ScheduledChildLaunch launch)
    {
        await launch.ReadyTimerCreated.WaitAsync(Bound);
        launch.Process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
        await launch.Session.ExecuteRequestAccepted.WaitAsync(Bound);
    }

    private static void EmitTerminal(
        ScheduledChildLaunch launch,
        ProcessingRunOutcome outcome)
    {
        var request = launch.Request;
        var result = new ProcessingRunResult(
            request,
            SessionTestSupport.Start,
            SessionTestSupport.Start,
            0,
            0,
            0,
            0,
            outcome,
            null);
        launch.Process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolMapper.Map(
                new RunStarted(request, SessionTestSupport.Start),
                2,
                SessionTestSupport.Start)));
        launch.Process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolMapper.Map(
                new EligibilityDetermined(request, 0),
                3,
                SessionTestSupport.Start)));
        launch.Process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolMapper.Map(
                new RunFinished(request, result),
                4,
                SessionTestSupport.Start)));
    }

    private sealed class ScheduledChildFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        private ScheduledChildFixture(
            string root,
            ServiceProvider provider,
            ScheduledChildLauncher launcher)
        {
            _root = root;
            _provider = provider;
            Launcher = launcher;
            Coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
            Reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
            State = provider.GetRequiredService<ProcessingState>();
        }

        internal ScheduledChildLauncher Launcher { get; }
        internal ProcessingRunCoordinator Coordinator { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal ProcessingState State { get; }

        internal static ScheduledChildFixture Create(
            IWorkerCommandInvocationBuilder builder,
            TimeProvider clock,
            bool gateSessionCreation = false)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "immich-reversegeo-change33-scheduled",
                Guid.NewGuid().ToString("N"));
            var launcher = new ScheduledChildLauncher(gateSessionCreation);
            try
            {
                var services = new ServiceCollection();
                services.AddWebComposition(
                    ApplicationCompositionContext.Create(
                        CompositionEnvironment.Development,
                        root,
                        Path.Combine(root, "data"),
                        Path.Combine(root, "config")));
                services.RemoveAll<IScheduledRunWorkGate>();
                services.AddSingleton<IScheduledRunWorkGate>(global::ImmichReverseGeo.Tests.AlwaysHasWorkScheduledRunGate.Instance);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
                services.RemoveAll<IWorkerCommandInvocationBuilder>();
                services.AddSingleton(builder);
                services.RemoveAll<IChildWorkerLauncher>();
                services.AddSingleton<IChildWorkerLauncher>(launcher);
                return new ScheduledChildFixture(
                    root,
                    services.BuildServiceProvider(validateScopes: true),
                    launcher);
            }
            catch
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        internal async Task<ProcessingRunFinalizationReceipt> WaitForReceiptAsync(
            ProcessingRunRequest request)
        {
            var observed = new TaskCompletionSource<ProcessingRunFinalizationReceipt>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe()
            {
                ProcessingRunFinalizationReceipt? receipt = Reporter.GetFinalizationReceipt(request);
                if (receipt is not null)
                {
                    observed.TrySetResult(receipt);
                }
            }

            State.OnChanged += Observe;
            try
            {
                Observe();
                return await observed.Task.ConfigureAwait(false);
            }
            finally
            {
                State.OnChanged -= Observe;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Launcher.ExitAll();
            await Coordinator.DisposeAsync().AsTask().WaitAsync(Bound);
            await _provider.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class ScheduledChildLauncher : IChildWorkerLauncher
    {
        private readonly bool _gateSessionCreation;
        private readonly ConcurrentQueue<ScheduledChildLaunch> _launches = new();
        private readonly ConcurrentBag<ScheduledChildLaunch> _allLaunches = [];
        private readonly ConcurrentBag<SessionTestProcess> _ownedProcesses = [];
        private readonly SemaphoreSlim _available = new(0);
        private int _callCount;

        internal ScheduledChildLauncher(bool gateSessionCreation = false)
        {
            _gateSessionCreation = gateSessionCreation;
        }

        internal int CallCount => Volatile.Read(ref _callCount);
        internal TaskCompletionSource SessionCreationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSessionCreation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SessionTestProcess? RawProcess { get; private set; }
        internal ChildWorkerObserverArmingAcknowledgements? ObserverArming { get; private set; }

        public async ValueTask<ChildWorkerLaunchResult> LaunchAsync(
            WorkerInvocation invocation,
            ProcessingRunRequest request,
            IWorkerProtocolEventSink eventSink,
            ChildWorkerLauncherOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            var input = new SessionInputStream();
            var process = new SessionTestProcess(
                input,
                ChildProcessKillOutcome.Requested,
                exitOnKill: false);
            _ownedProcesses.Add(process);
            RawProcess = process;
            if (_gateSessionCreation)
            {
                SessionCreationEntered.TrySetResult();
                await ReleaseSessionCreation.Task.ConfigureAwait(false);
            }

            Task readyTimerCreated = options.TimeProvider switch
            {
                CancellationTestClock clock => clock.WaitForTimerCreatedAsync(clock.TimerGeneration),
                BlockingTimestampClock clock => clock.WaitForTimerCreatedAsync(clock.TimerGeneration),
                _ => throw new InvalidOperationException("The scheduled cancellation fixture requires a signaling clock.")
            };
            var observerArming = new ChildWorkerObserverArmingAcknowledgements();
            ObserverArming = observerArming;
            ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
                process,
                request,
                eventSink,
                options,
                observerArming);
            var launch = new ScheduledChildLaunch(
                request,
                input,
                process,
                session,
                readyTimerCreated);
            _allLaunches.Add(launch);
            _launches.Enqueue(launch);
            _available.Release();
            return new ChildWorkerLaunchResult.Started(session);
        }

        internal async Task<ScheduledChildLaunch> NextAsync()
        {
            try
            {
                await _available.WaitAsync().WaitAsync(Bound).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"The child launch was not published before the phase bound. {DescribeObserverArming(ObserverArming)}",
                    exception);
            }

            return _launches.TryDequeue(out ScheduledChildLaunch? launch)
                ? launch
                : throw new InvalidOperationException("The launch signal had no matching session.");
        }

        internal void ExitAll()
        {
            foreach (SessionTestProcess process in _ownedProcesses)
            {
                process.Exit(143);
            }
        }

        private static string DescribeObserverArming(
            ChildWorkerObserverArmingAcknowledgements? observerArming)
        {
            return $"Observer arming: stdout={DescribeTask(observerArming?.StandardOutput)}, "
                + $"stderr={DescribeTask(observerArming?.StandardError)}, "
                + $"exit={DescribeTask(observerArming?.Exit)}.";
        }

        private static string DescribeTask(Task? task)
        {
            if (task is null)
            {
                return "not-created";
            }

            if (!task.IsCompleted)
            {
                return "pending";
            }

            if (task.IsCompletedSuccessfully)
            {
                return "complete";
            }

            return task.IsCanceled ? "canceled" : "faulted";
        }
    }

    private sealed record ScheduledChildLaunch(
        ProcessingRunRequest Request,
        SessionInputStream Input,
        SessionTestProcess Process,
        ChildWorkerSession Session,
        Task ReadyTimerCreated);

    private sealed class ImmediateInvocationBuilder : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build() => ValidResolution();
    }

    private sealed class GatedInvocationBuilder : IWorkerCommandInvocationBuilder
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkerCommandInvocationResolution Build()
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return ValidResolution();
        }
    }

    private sealed class BlockingTimestampClock(DateTimeOffset start) : TimeProvider
    {
        private readonly CancellationTestClock _inner = new(start);
        private readonly TaskCompletionSource _timestampBlocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _timestampRelease = new(false);
        private int _blockNextTimestamp;

        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
        internal Task TimestampBlocked => _timestampBlocked.Task;

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override long GetTimestamp()
        {
            if (Interlocked.Exchange(ref _blockNextTimestamp, 0) != 0)
            {
                _timestampBlocked.TrySetResult();
                _timestampRelease.Wait();
            }

            return _inner.GetTimestamp();
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            return _inner.CreateTimer(callback, state, dueTime, period);
        }

        internal long TimerGeneration => _inner.TimerGeneration;

        internal Task WaitForTimerCreatedAsync(long afterGeneration)
        {
            return _inner.WaitForTimerCreatedAsync(afterGeneration);
        }

        internal void BlockNextTimestamp()
        {
            Volatile.Write(ref _blockNextTimestamp, 1);
        }

        internal void ReleaseTimestamp()
        {
            _timestampRelease.Set();
        }
    }

    private static WorkerCommandInvocationResolution ValidResolution()
    {
        var facts = new WorkerCommandRuntimeFacts(
            WorkerInvocation.TrustedWebAssemblyIdentity,
            "/fixture/dotnet",
            WorkerTargetObservation.File,
            WorkerInvocation.TrustedWebAssemblyIdentity,
            "/fixture/ImmichReverseGeo.Web.dll",
            WorkerTargetObservation.File,
            "/fixture",
            WorkerTargetObservation.Directory,
            WorkerPathSemantics.Unix);
        return WorkerInvocation.Resolve(facts);
    }
}
