using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Composition;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerCommandInvocation;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ManualChildWorkerExecution;

[TestClass]
[TestCategory("Change34")]
[DoNotParallelize]
public sealed class FailureControlPlaneTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ManualChild_ReadyTimeoutAtExactBoundaryFailsOnceWithoutFallbackAndAllowsRetrigger()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        var timers = new ReadinessAndGraceClock(clock);
        await using var fixture = FailureFixture.Create(timers);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "ready-timeout-admitted");
        await fixture.Launcher.FirstEntered.Task.WaitAsync(Bound);
        await timers.ReadyTimerCreated.Task.WaitAsync(Bound);

        clock.Advance(TimeSpan.FromMilliseconds(29_999));
        Assert.IsFalse(
            fixture.Launcher.First.Session.WaitForStartupAsync().IsCompleted,
            "ready-timeout-not-before-boundary");
        Assert.AreEqual(0, fixture.Launcher.First.Input.WriteCalls, "ready-timeout-no-early-execute");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyTimedOut>(
            await fixture.Launcher.First.Session.WaitForStartupAsync().WaitAsync(Bound),
            "ready-timeout-exact-boundary");
        await timers.GraceTimerCreated.Task.WaitAsync(Bound);
        clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Launcher.First.Process.KillObserved.Task.WaitAsync(Bound);
        Assert.AreEqual(0, fixture.Launcher.First.Input.WriteCalls, "ready-timeout-no-execute-at-boundary");
        Assert.AreEqual(0, fixture.Launcher.First.Input.FlushCalls, "ready-timeout-no-flush-at-boundary");

        ProcessingRunRequest firstRequest = fixture.Launcher.First.Request!;
        fixture.Launcher.First.Process.Exit(143);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        ProcessingRunFinalizationReceipt firstReceipt = fixture.Reporter.GetFinalizationReceipt(firstRequest)!;
        Assert.AreEqual(ProcessingRunOutcome.Failed, firstReceipt.Result.Outcome, "ready-timeout-failed-once");
        Assert.AreEqual(1, fixture.Launcher.CallCount, "ready-timeout-one-child");
        Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "ready-timeout-one-summary");
        Assert.IsFalse(fixture.State.IsRunning, "ready-timeout-activities-cleared");
        Assert.IsFalse(fixture.State.LastError!.Contains("Synthetic", StringComparison.Ordinal), "ready-timeout-safe-error");

        int retainedLogCount = fixture.State.RecentLog.Count;
        await fixture.Launcher.First.EventSink!.AcceptAsync(
            ProcessAssetsWorkerJobProjection.MapV1(
                firstRequest,
                WorkerProtocolMapper.Ready(9, clock.GetUtcNow())),
            CancellationToken.None);
        Assert.AreEqual(retainedLogCount, fixture.State.RecentLog.Count, "ready-timeout-late-event-ignored");

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "ready-timeout-retrigger-accepted-after-cleanup");
        await fixture.Launcher.SecondEntered.Task.WaitAsync(Bound);
        Assert.AreNotEqual(firstRequest.RunId, fixture.Launcher.Second.Request!.RunId, "ready-timeout-retrigger-new-run-id");

        fixture.Launcher.Second.Process.Exit(1);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ManualChild_ExecuteWriteOrFlushFailureFinalizesOriginalRunWithoutRetry(bool writeFails)
    {
        var firstFlushAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = FailureFixture.Create(
            inputFactory: () => new SessionInputStream
            {
                FaultWriteCall = writeFails ? 1 : 0,
                FaultFlushCall = writeFails ? 0 : 1,
                FlushBoundary = call =>
                {
                    if (call == 1)
                    {
                        firstFlushAttempted.TrySetResult();
                    }
                }
            });

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "execute-transport-admitted");
        await fixture.Launcher.FirstEntered.Task.WaitAsync(Bound);

        ProcessingRunRequest firstRequest = fixture.Launcher.First.Request!;
        fixture.Launcher.First.Process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
        await fixture.Launcher.First.Input.FirstWrite.WaitAsync(Bound);
        if (!writeFails)
        {
            await firstFlushAttempted.Task.WaitAsync(Bound);
        }

        fixture.Launcher.First.Process.Exit(1);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        ProcessingRunFinalizationReceipt firstReceipt = fixture.Reporter.GetFinalizationReceipt(firstRequest)!;
        Assert.AreEqual(ProcessingRunOutcome.Failed, firstReceipt.Result.Outcome, "execute-transport-failed");
        Assert.AreEqual(1, fixture.Launcher.First.Input.WriteCalls, "execute-transport-one-write");
        Assert.AreEqual(writeFails ? 0 : 1, fixture.Launcher.First.Input.FlushCalls, "execute-transport-one-flush-attempt");
        Assert.AreEqual(1, fixture.Launcher.CallCount, "execute-transport-no-retry");
        Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "execute-transport-one-summary");
        Assert.IsFalse(fixture.State.IsRunning, "execute-transport-activities-cleared");
        Assert.IsFalse(fixture.State.LastError!.Contains("Synthetic", StringComparison.Ordinal), "execute-transport-safe-error");

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "execute-transport-retrigger-after-cleanup");
        await fixture.Launcher.SecondEntered.Task.WaitAsync(Bound);
        Assert.AreNotEqual(firstRequest.RunId, fixture.Launcher.Second.Request!.RunId, "execute-transport-retrigger-new-run-id");

        fixture.Launcher.Second.Process.Exit(1);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ManualChild_RepeatedStopUsesOneOperationAndBlocksRetriggerUntilDrained(bool stopBeforeExecuteFlush)
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        await using var fixture = FailureFixture.Create(clock);
        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "stop-latch-admitted");
        await fixture.Launcher.FirstEntered.Task.WaitAsync(Bound);

        ProcessingRunRequest firstRequest = fixture.Launcher.First.Request!;
        long timerGeneration = clock.TimerGeneration;
        Task? firstStop = null;
        if (stopBeforeExecuteFlush)
        {
            firstStop = fixture.Coordinator.StopActiveRun();
            Assert.IsNotNull(firstStop, "stop-latch-before-execute-stop-task");
        }

        fixture.Launcher.First.Process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
        await fixture.Launcher.First.Session!.ExecuteRequestAccepted.WaitAsync(Bound);

        firstStop ??= fixture.Coordinator.StopActiveRun();
        Task activeStop = firstStop ?? throw new InvalidOperationException("The active Stop operation was not created.");
        Task secondStop = fixture.Coordinator.StopActiveRun()!;
        Assert.AreSame(activeStop, secondStop, "stop-latch-one-shared-operation");
        await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(Bound);
        await fixture.Launcher.First.Input.SecondFlush.WaitAsync(Bound);
        Assert.AreEqual(2, fixture.Launcher.First.Input.WriteCalls, "stop-latch-execute-and-one-cancel-write");
        Assert.AreEqual(2, fixture.Launcher.First.Input.FlushCalls, "stop-latch-execute-and-one-cancel-flush");

        Assert.AreEqual(
            ProcessingRunAdmissionResult.AlreadyRunning,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "stop-latch-retrigger-blocked-before-drainage");
        Assert.AreEqual(1, fixture.Launcher.CallCount, "stop-latch-no-replacement-before-drainage");

        EmitCancelledLifecycleAfterReady(fixture.Launcher.First.Process, firstRequest);
        fixture.Launcher.First.Process.Exit(130);
        await activeStop.WaitAsync(Bound);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(firstRequest)!;
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, receipt.Result.Outcome, "stop-latch-cancelled");
        Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "stop-latch-one-summary");
        Assert.IsFalse(fixture.State.IsRunning, "stop-latch-activities-cleared");

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "stop-latch-retrigger-after-drainage");
        await fixture.Launcher.SecondEntered.Task.WaitAsync(Bound);
        Assert.AreNotEqual(firstRequest.RunId, fixture.Launcher.Second.Request!.RunId, "stop-latch-retrigger-new-run-id");

        fixture.Launcher.Second.Process.Exit(1);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ManualChild_SelectedRunScopeCleanupRetainsTerminalAndBlocksRetriggerUntilSettled(
        bool throwAfterDisposalGate)
    {
        var scopeDisposal = new ScopeDisposalControl(throwAfterDisposalGate);
        await using var fixture = FailureFixture.Create(scopeDisposal: scopeDisposal);
        try
        {
            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
                "scope-cleanup-admitted");
            await fixture.Launcher.FirstEntered.Task.WaitAsync(Bound);

            ProcessingRunRequest firstRequest = fixture.Launcher.First.Request!;
            EmitCompletedLifecycle(fixture.Launcher.First.Process, firstRequest);
            fixture.Launcher.First.Process.Exit(0);
            await scopeDisposal.FirstDisposeStarted.Task.WaitAsync(Bound);

            ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(firstRequest)!;
            Assert.AreEqual(ProcessingRunOutcome.Completed, receipt.Result.Outcome, "scope-cleanup-terminal-committed");
            Assert.AreSame(firstRequest, fixture.Coordinator.ActiveRequest, "scope-cleanup-handle-retained");
            Assert.IsFalse(fixture.Coordinator.WaitForActiveRunAsync().IsCompleted, "scope-cleanup-wait-not-released");
            Assert.AreEqual(1, scopeDisposal.CreatedCount, "scope-cleanup-one-selected-scope");
            Assert.AreEqual(1, scopeDisposal.DisposeStartedCount, "scope-cleanup-disposal-started-once");
            Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "scope-cleanup-one-summary-before-release");

            Assert.AreEqual(
                ProcessingRunAdmissionResult.AlreadyRunning,
                await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
                "scope-cleanup-retrigger-blocked");
            Assert.AreEqual(1, fixture.Launcher.CallCount, "scope-cleanup-no-duplicate-child");
            Assert.AreEqual(1, scopeDisposal.CreatedCount, "scope-cleanup-no-duplicate-scope");

            scopeDisposal.ReleaseFirstDispose();
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            Assert.IsNull(fixture.Coordinator.ActiveRequest, "scope-cleanup-handle-released-after-settlement");
            Assert.AreEqual(1, scopeDisposal.DisposeSettledCount, "scope-cleanup-first-disposal-settled");
            Assert.AreSame(receipt, fixture.Reporter.GetFinalizationReceipt(firstRequest), "scope-cleanup-original-receipt-retained");
            Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "scope-cleanup-one-summary-after-release");

            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
                "scope-cleanup-retrigger-accepted");
            await fixture.Launcher.SecondEntered.Task.WaitAsync(Bound);
            Assert.AreNotEqual(firstRequest.RunId, fixture.Launcher.Second.Request!.RunId, "scope-cleanup-retrigger-new-run-id");

            fixture.Launcher.Second.Process.Exit(1);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
            Assert.AreEqual(2, scopeDisposal.CreatedCount, "scope-cleanup-new-scope-per-run");
            Assert.AreEqual(2, scopeDisposal.DisposeSettledCount, "scope-cleanup-retrigger-scope-settled");
        }
        finally
        {
            scopeDisposal.ReleaseFirstDispose();
            fixture.Launcher.ExitAll();
        }
    }

    private static void EmitCompletedLifecycle(
        SessionTestProcess process,
        ProcessingRunRequest request)
    {
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.LifecycleCategory,
            WorkerProtocolV1.RunStartedType,
            2,
            SessionTestSupport.Start,
            request.RunId,
            new RunStartedPayload("manual", SessionTestSupport.Start))));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.LifecycleCategory,
            WorkerProtocolV1.EligibilityDeterminedType,
            3,
            SessionTestSupport.Start,
            request.RunId,
            new EligibilityDeterminedPayload(0))));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.TerminalCategory,
            WorkerProtocolV1.CompletedType,
            4,
            SessionTestSupport.Start,
            request.RunId,
            new CompletedPayload("manual", SessionTestSupport.Start, SessionTestSupport.Start, 0, 0, 0, 0))));
    }

    private static void EmitCancelledLifecycleAfterReady(
        SessionTestProcess process,
        ProcessingRunRequest request)
    {
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.LifecycleCategory,
            WorkerProtocolV1.RunStartedType,
            2,
            SessionTestSupport.Start,
            request.RunId,
            new RunStartedPayload("manual", SessionTestSupport.Start))));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.LifecycleCategory,
            WorkerProtocolV1.EligibilityDeterminedType,
            3,
            SessionTestSupport.Start,
            request.RunId,
            new EligibilityDeterminedPayload(0))));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.TerminalCategory,
            WorkerProtocolV1.CancelledType,
            4,
            SessionTestSupport.Start,
            request.RunId,
            new CancelledPayload("manual", SessionTestSupport.Start, SessionTestSupport.Start, 0, 0, 0, 0))));
    }

    private sealed class ReadinessAndGraceClock(CancellationTestClock inner) : TimeProvider
    {
        internal TaskCompletionSource ReadyTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource GraceTimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            // This fixture uses the default 30-second ready and 10-second grace
            // policy. The 100-ms UI timers cannot satisfy either acknowledgement.
            if (dueTime == TimeSpan.FromSeconds(30))
            {
                ReadyTimerCreated.TrySetResult();
            }
            else if (dueTime == TimeSpan.FromSeconds(10))
            {
                GraceTimerCreated.TrySetResult();
            }

            return timer;
        }
    }

    private sealed class FailureFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        private FailureFixture(string root, ServiceProvider provider, FailureLauncher launcher)
        {
            _root = root;
            _provider = provider;
            Launcher = launcher;
            Coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
            Reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
            State = provider.GetRequiredService<ProcessingState>();
        }

        internal FailureLauncher Launcher { get; }
        internal ProcessingRunCoordinator Coordinator { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal ProcessingState State { get; }

        internal static FailureFixture Create(
            TimeProvider? timeProvider = null,
            Func<SessionInputStream>? inputFactory = null,
            ScopeDisposalControl? scopeDisposal = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change34-failures", Guid.NewGuid().ToString("N"));
            var services = new ServiceCollection();
            var launcher = new FailureLauncher(inputFactory ?? (() => new SessionInputStream()));
            try
            {
                services.AddWebComposition(ApplicationCompositionContext.Create(
                    CompositionEnvironment.Development,
                    root,
                    Path.Combine(root, "data"),
                    Path.Combine(root, "config")));
                if (timeProvider is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(timeProvider);
                }

                services.RemoveAll<IWorkerCommandInvocationBuilder>();
                services.AddSingleton<IWorkerCommandInvocationBuilder, ImmediateInvocationBuilder>();
                services.RemoveAll<IChildWorkerLauncher>();
                services.AddSingleton<IChildWorkerLauncher>(launcher);
                if (scopeDisposal is not null)
                {
                    services.RemoveAll<IChildProcessingRunBackend>();
                    services.AddSingleton(scopeDisposal);
                    services.AddScoped<ScopeDisposalSentinel>();
                    services.AddScoped<IChildProcessingRunBackend>(
                        sp =>
                        {
                            _ = sp.GetRequiredService<ScopeDisposalSentinel>();
                            return new ChildWorkerProcessingRunBackend(
                                sp.GetRequiredService<WorkerRunControlPlane>(),
                                sp.GetRequiredService<ProcessingRunCoordinator>());
                        });
                }

                return new FailureFixture(root, services.BuildServiceProvider(validateScopes: true), launcher);
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

        public async ValueTask DisposeAsync()
        {
            Launcher.ExitAll();
            await Coordinator.DisposeAsync();
            await _provider.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FailureLauncher(Func<SessionInputStream> inputFactory) : IChildWorkerLauncher
    {
        private readonly Func<SessionInputStream> _inputFactory = inputFactory;
        private readonly List<FailureLaunch> _launches = [];

        internal TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CallCount => _launches.Count;
        internal FailureLaunch First => _launches[0];
        internal FailureLaunch Second => _launches[1];

        public async ValueTask<ChildWorkerLaunchResult> LaunchAsync(
            WorkerInvocation invocation,
            WorkerJobDispatch dispatch,
            IWorkerJobEventSink eventSink,
            ChildWorkerLauncherOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            var processAssets = Assert.IsInstanceOfType<ProcessAssetsWorkerJobDispatch>(dispatch);
            ProcessingRunRequest request = processAssets.Request.ProcessingRequest;
            var input = _inputFactory();
            var process = new SessionTestProcess(input, ChildProcessKillOutcome.Requested, exitOnKill: false);
            ChildWorkerSession session = await ChildWorkerSession.CreateAsync(
                process,
                dispatch,
                eventSink,
                options,
                new ChildWorkerObserverArmingAcknowledgements(),
                invocation.ProtocolVersion);
            var launch = new FailureLaunch(request, input, process, session, eventSink);
            _launches.Add(launch);
            if (_launches.Count == 1)
            {
                FirstEntered.TrySetResult();
            }
            else if (_launches.Count == 2)
            {
                SecondEntered.TrySetResult();
            }

            return new ChildWorkerLaunchResult.Started(session);
        }

        internal void ExitAll()
        {
            foreach (FailureLaunch launch in _launches)
            {
                launch.Process.Exit(1);
            }
        }
    }

    private sealed record FailureLaunch(
        ProcessingRunRequest Request,
        SessionInputStream Input,
        SessionTestProcess Process,
        ChildWorkerSession Session,
        IWorkerJobEventSink EventSink);

    private sealed class ScopeDisposalControl(bool throwAfterFirstGate)
    {
        private int _createdCount;
        private int _disposeStartedCount;
        private int _disposeSettledCount;

        internal TaskCompletionSource FirstDisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowFirstDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CreatedCount => Volatile.Read(ref _createdCount);
        internal int DisposeStartedCount => Volatile.Read(ref _disposeStartedCount);
        internal int DisposeSettledCount => Volatile.Read(ref _disposeSettledCount);

        internal int CreateScopeOrdinal() => Interlocked.Increment(ref _createdCount);

        internal async ValueTask DisposeScopeAsync(int ordinal)
        {
            Interlocked.Increment(ref _disposeStartedCount);
            if (ordinal == 1)
            {
                FirstDisposeStarted.TrySetResult();
                await AllowFirstDispose.Task.ConfigureAwait(false);
            }

            Interlocked.Increment(ref _disposeSettledCount);
            if (ordinal == 1 && throwAfterFirstGate)
            {
                throw new IOException("Synthetic scoped disposal failure.");
            }
        }

        internal void ReleaseFirstDispose() => AllowFirstDispose.TrySetResult();
    }

    private sealed class ScopeDisposalSentinel : IAsyncDisposable
    {
        private readonly ScopeDisposalControl _control;
        private readonly int _ordinal;

        public ScopeDisposalSentinel(ScopeDisposalControl control)
        {
            _control = control;
            _ordinal = control.CreateScopeOrdinal();
        }

        public ValueTask DisposeAsync() => _control.DisposeScopeAsync(_ordinal);
    }

    private sealed class ImmediateInvocationBuilder : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build()
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
}
