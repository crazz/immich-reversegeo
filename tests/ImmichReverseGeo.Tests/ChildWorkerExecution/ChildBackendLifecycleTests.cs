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
using System.Text;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ChildWorkerExecution;

[TestClass]
[TestCategory("Change33")]
public sealed class ChildBackendLifecycleTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task WebComposition_ChildBackendKeepsCommittedTerminalAndExactHandleUntilProcessFinality()
    {
        await using var fixture = WebChildBackendFixture.Create(new ImmediateInvocationBuilder());
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "child-terminal-admission");
        await fixture.Launcher.WaitUntilEnteredAsync();
        ProcessingRunRequest request = fixture.Launcher.Request!;
        void ObserveCommit()
        {
            if (fixture.Reporter.GetFinalizationReceipt(request) is not null)
            {
                committed.TrySetResult();
            }
        }

        fixture.State.OnChanged += ObserveCommit;
        try
        {
            EmitTerminalSequence(fixture.Launcher.Process!, request, WorkerProtocolV1.CompletedType);
            await committed.Task.WaitAsync(Bound);

            ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)!;
            Assert.AreEqual(ProcessingRunOutcome.Completed, receipt.Result.Outcome, "child-terminal-outcome");
            Assert.IsFalse(fixture.Launcher.Process!.GetExitState() == ChildProcessExitState.Exited, "child-terminal-arrives-before-exit");
            Assert.AreSame(request, fixture.Coordinator.ActiveRequest, "child-terminal-retains-exact-handle");
            Assert.IsFalse(fixture.Coordinator.WaitForActiveRunAsync().IsCompleted, "child-terminal-waits-for-finality");
            Assert.AreEqual(1, fixture.Launcher.CallCount, "child-terminal-one-launch");

            fixture.Launcher.Process.Exit(0);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            Assert.IsNull(fixture.Coordinator.ActiveRequest, "child-terminal-releases-after-finality");
            Assert.IsFalse(fixture.State.IsRunning, "child-terminal-returns-idle");
            Assert.AreSame(receipt, fixture.Reporter.GetFinalizationReceipt(request), "child-terminal-single-receipt");
        }
        finally
        {
            fixture.State.OnChanged -= ObserveCommit;
            fixture.Launcher.Process?.Exit(0);
        }
    }

    [TestMethod]
    public async Task WebComposition_ChildBackendStopBeforeReadyUsesOneChildSessionAndReturnsCancelled()
    {
        var builder = new GatedInvocationBuilder();
        await using var fixture = WebChildBackendFixture.Create(builder);
        Task<ProcessingRunAdmissionResult> dispatch = Task.Run(fixture.Coordinator.TriggerManualAsync);

        await builder.Entered.Task.WaitAsync(Bound);
        Task stop = fixture.Coordinator.StopActiveRun()!;
        try
        {
            builder.Release.TrySetResult();
            Assert.AreEqual(ProcessingRunAdmissionResult.Accepted, await dispatch.WaitAsync(Bound), "child-stop-admission");
            await fixture.Launcher.WaitUntilEnteredAsync();

            ProcessingRunRequest request = fixture.Launcher.Request!;
            SessionTestProcess process = fixture.Launcher.Process!;
            EmitTerminalSequence(process, request, WorkerProtocolV1.CancelledType);
            process.Exit(130);

            await stop.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)!;
            Assert.AreEqual(ProcessingRunOutcome.Cancelled, receipt.Result.Outcome, "child-stop-cancelled-result");
            Assert.AreEqual(1, fixture.Launcher.CallCount, "child-stop-one-session");
            Assert.IsFalse(fixture.State.IsRunning, "child-stop-returns-idle");
        }
        finally
        {
            builder.Release.TrySetResult();
            fixture.Launcher.Process?.Exit(130);
        }
    }

    [TestMethod]
    public async Task WebComposition_ChildResolutionAndStartFailuresRemainOnTheChildBackend()
    {
        foreach (var scenario in new[] { "resolution", "start" })
        {
            await using var fixture = scenario == "resolution"
                ? WebChildBackendFixture.Create(new FailingInvocationBuilder())
                : WebChildBackendFixture.Create(new ImmediateInvocationBuilder(), startFailure: true);

            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
                scenario + "-admission");
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            Assert.AreEqual(scenario == "resolution" ? 0 : 1, fixture.Launcher.CallCount, scenario + "-one-or-zero-launch");
            Assert.IsFalse(fixture.State.IsRunning, scenario + "-returns-idle");
            StringAssert.StartsWith(fixture.State.LastError!, "Fatal:", scenario + "-one-classified-terminal");
        }
    }

    [TestMethod]
    public async Task WebComposition_ChildPostReadyCrashWithNoTerminalReturnsOneFailureWithoutFallback()
    {
        await using var fixture = WebChildBackendFixture.Create(new ImmediateInvocationBuilder());

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "child-crash-admission");
        await fixture.Launcher.WaitUntilEnteredAsync();
        ProcessingRunRequest request = fixture.Launcher.Request!;
        SessionTestProcess process = fixture.Launcher.Process!;
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.LifecycleCategory,
            WorkerProtocolV1.RunStartedType,
            2,
            SessionTestSupport.Start,
            request.RunId,
            new RunStartedPayload("manual", SessionTestSupport.Start))));
        await fixture.Launcher.Session!.ExecuteRequestAccepted.WaitAsync(Bound);
        process.Exit(42);

        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)!;
        Assert.AreEqual(ProcessingRunOutcome.Failed, receipt.Result.Outcome, "child-crash-classified-failed");
        Assert.AreEqual(1, fixture.Launcher.CallCount, "child-crash-no-replacement-launch");
        Assert.IsFalse(fixture.State.IsRunning, "child-crash-idle");
        Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "child-crash-one-terminal");
    }

    [TestMethod]
    public async Task WebComposition_ChildProtocolFailureUsesOneClassifiedFailureWithoutFallbackOrReplacement()
    {
        await using var fixture = WebChildBackendFixture.Create(new ImmediateInvocationBuilder());

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "child-protocol-admission");
        await fixture.Launcher.WaitUntilEnteredAsync();
        ProcessingRunRequest request = fixture.Launcher.Request!;
        SessionTestProcess process = fixture.Launcher.Process!;
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
        await fixture.Launcher.Session!.ExecuteRequestAccepted.WaitAsync(Bound);
        process.StandardOutputSource.Enqueue(Encoding.UTF8.GetBytes("{not-json}\n"));
        await fixture.Launcher.Session.FirstTerminalPreventingObservation.WaitAsync(Bound);
        process.Exit(42);

        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)!;
        Assert.AreEqual(ProcessingRunOutcome.Failed, receipt.Result.Outcome, "child-protocol-classified-failed");
        Assert.AreEqual(1, fixture.Launcher.CallCount, "child-protocol-no-replacement-launch");
        Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "child-protocol-one-terminal");
    }

    [TestMethod]
    public async Task WebComposition_ChildProjectionFailureUsesOneClassifiedFailureWithoutFallbackOrReplacement()
    {
        await using var fixture = WebChildBackendFixture.Create(new ImmediateInvocationBuilder());
        var projectionCallback = 0;
        void FailFirstProjection()
        {
            if (Interlocked.Exchange(ref projectionCallback, 1) == 0)
            {
                throw new InvalidOperationException("Synthetic projection callback failure.");
            }
        }

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "child-projection-admission");
        await fixture.Launcher.WaitUntilEnteredAsync();
        ProcessingRunRequest request = fixture.Launcher.Request!;
        SessionTestProcess process = fixture.Launcher.Process!;
        fixture.State.OnChanged += FailFirstProjection;
        try
        {
            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
            Assert.IsInstanceOfType<ChildWorkerStartupObservation.ReadyAccepted>(
                await fixture.Launcher.Session!.Startup.WaitAsync(Bound),
                "child-projection-ready");
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
            await fixture.Launcher.Session!.FirstTerminalPreventingObservation.WaitAsync(Bound);
        }
        finally
        {
            fixture.State.OnChanged -= FailFirstProjection;
        }

        process.Exit(0);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)!;
        Assert.AreEqual(ProcessingRunOutcome.Failed, receipt.Result.Outcome, "child-projection-classified-failed");
        Assert.AreEqual(1, fixture.Launcher.CallCount, "child-projection-no-replacement-launch");
        Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "child-projection-one-terminal");
    }

    [TestMethod]
    public async Task WebComposition_ChildKillRejectionRetainsHandleUntilPhysicalExitThenFailsOnce()
    {
        var clock = new CancellationTestClock(SessionTestSupport.Start);
        await using var fixture = WebChildBackendFixture.Create(
            new ImmediateInvocationBuilder(),
            timeProvider: clock,
            killOutcome: ChildProcessKillOutcome.PermissionDenied,
            exitOnKill: false);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "child-kill-admission");
        await fixture.Launcher.WaitUntilEnteredAsync();
        ProcessingRunRequest request = fixture.Launcher.Request!;
        SessionTestProcess process = fixture.Launcher.Process!;
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
        await fixture.Launcher.Session!.ExecuteRequestAccepted.WaitAsync(Bound);

        long timerGeneration = clock.TimerGeneration;
        Task stop = fixture.Coordinator.StopActiveRun()!;
        try
        {
            await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(Bound);
            clock.Advance(ChildWorkerCancellationPolicy.Grace);
            await process.KillObserved.Task.WaitAsync(Bound);

            Assert.IsFalse(stop.IsCompleted, "child-kill-rejection-stop-waits-for-exit");
            Assert.AreSame(request, fixture.Coordinator.ActiveRequest, "child-kill-rejection-retains-handle");
            Assert.IsFalse(fixture.Coordinator.WaitForActiveRunAsync().IsCompleted, "child-kill-rejection-not-idle-before-exit");

            process.Exit(143);
            await stop.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        }
        finally
        {
            process.Exit(143);
        }

        ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)!;
        Assert.AreEqual(ProcessingRunOutcome.Failed, receipt.Result.Outcome, "child-kill-rejection-classified-failed");
        Assert.AreEqual(1, process.KillCalls, "child-kill-rejection-one-containment-attempt");
        Assert.AreEqual(1, fixture.Launcher.CallCount, "child-kill-rejection-no-replacement-launch");
        Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "child-kill-rejection-one-terminal");
    }

    private static void EmitTerminalSequence(
        SessionTestProcess process,
        ProcessingRunRequest request,
        string terminalType)
    {
        DateTimeOffset at = SessionTestSupport.Start;
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, at)));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.LifecycleCategory,
            WorkerProtocolV1.RunStartedType,
            2,
            at,
            request.RunId,
            new RunStartedPayload("manual", at))));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.LifecycleCategory,
            WorkerProtocolV1.EligibilityDeterminedType,
            3,
            at,
            request.RunId,
            new EligibilityDeterminedPayload(0))));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.TerminalCategory,
            terminalType,
            4,
            at,
            request.RunId,
            terminalType == WorkerProtocolV1.CompletedType
                ? new CompletedPayload("manual", at, at, 0, 0, 0, 0)
                : new CancelledPayload("manual", at, at, 0, 0, 0, 0))));
    }

    private sealed class WebChildBackendFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        private WebChildBackendFixture(
            string root,
            ServiceProvider provider,
            ControlledSessionLauncher launcher)
        {
            _root = root;
            _provider = provider;
            Launcher = launcher;
            Coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
            Reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
            State = provider.GetRequiredService<ProcessingState>();
        }

        internal ControlledSessionLauncher Launcher { get; }
        internal ProcessingRunCoordinator Coordinator { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal ProcessingState State { get; }

        internal static WebChildBackendFixture Create(
            IWorkerCommandInvocationBuilder builder,
            bool startFailure = false,
            TimeProvider? timeProvider = null,
            ChildProcessKillOutcome killOutcome = ChildProcessKillOutcome.Requested,
            bool exitOnKill = true)
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change33", Guid.NewGuid().ToString("N"));
            var launcher = new ControlledSessionLauncher(startFailure, killOutcome, exitOnKill);
            try
            {
                var services = new ServiceCollection();
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
                services.AddSingleton(builder);
                services.RemoveAll<IChildWorkerLauncher>();
                services.AddSingleton<IChildWorkerLauncher>(launcher);
                return new WebChildBackendFixture(
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

        public async ValueTask DisposeAsync()
        {
            Launcher.Process?.Exit(143);
            await Coordinator.DisposeAsync();
            await _provider.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class ControlledSessionLauncher : IChildWorkerLauncher
    {
        private readonly bool _startFailure;
        private readonly ChildProcessKillOutcome _killOutcome;
        private readonly bool _exitOnKill;

        internal ControlledSessionLauncher(
            bool startFailure,
            ChildProcessKillOutcome killOutcome,
            bool exitOnKill)
        {
            _startFailure = startFailure;
            _killOutcome = killOutcome;
            _exitOnKill = exitOnKill;
        }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProcessingRunRequest? Request { get; private set; }
        internal SessionTestProcess? Process { get; private set; }
        internal ChildWorkerSession? Session { get; private set; }
        internal ChildWorkerObserverArmingAcknowledgements? ObserverArming { get; private set; }
        internal int CallCount { get; private set; }

        internal async Task WaitUntilEnteredAsync()
        {
            try
            {
                await Entered.Task.WaitAsync(Bound);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"The child launch was not published before the phase bound. {DescribeObserverArming(ObserverArming)}",
                    exception);
            }
        }

        public async ValueTask<ChildWorkerLaunchResult> LaunchAsync(
            WorkerInvocation invocation,
            ProcessingRunRequest request,
            IWorkerProtocolEventSink eventSink,
            ChildWorkerLauncherOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            CallCount++;
            Request = request;
            if (_startFailure)
            {
                Entered.TrySetResult();
                return new ChildWorkerLaunchResult.StartFailed(ChildWorkerStartFailureCategory.ProcessStartFailed);
            }

            Process = new SessionTestProcess(
                new SessionInputStream(),
                _killOutcome,
                _exitOnKill);
            var observerArming = new ChildWorkerObserverArmingAcknowledgements();
            ObserverArming = observerArming;
            Session = await ChildWorkerSession.CreateAsync(
                Process,
                request,
                eventSink,
                options,
                observerArming);
            Entered.TrySetResult();
            return new ChildWorkerLaunchResult.Started(Session);
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

    private sealed class ImmediateInvocationBuilder : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build() => ValidResolution();
    }

    private sealed class GatedInvocationBuilder : IWorkerCommandInvocationBuilder
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkerCommandInvocationResolution Build()
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return ValidResolution();
        }
    }

    private sealed class FailingInvocationBuilder : IWorkerCommandInvocationBuilder
    {
        public WorkerCommandInvocationResolution Build()
        {
            return WorkerCommandInvocationResolution.Fail(WorkerCommandInvocationFailureCategory.ProcessPathUnavailable);
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
