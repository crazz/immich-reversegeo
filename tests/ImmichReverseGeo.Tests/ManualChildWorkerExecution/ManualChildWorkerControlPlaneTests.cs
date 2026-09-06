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

namespace ImmichReverseGeo.Tests.ManualChildWorkerExecution;

[TestClass]
[TestCategory("Change34")]
[DoNotParallelize]
public sealed class ManualChildWorkerControlPlaneTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ManualChild_StopObservedDuringPendingTargetsTheExactSessionAndLeavesNoFallback()
    {
        await using var fixture = ManualChildFixture.Create();
        Task? stop = null;
        void StopWhenPending()
        {
            stop ??= fixture.Coordinator.StopActiveRun();
        }

        fixture.State.OnChanged += StopWhenPending;
        try
        {
            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
                "pending-stop-admission");
            await fixture.Launcher.Entered.Task.WaitAsync(Bound);

            ProcessingRunRequest request = fixture.Launcher.Request!;
            Assert.IsNotNull(stop, "pending-stop-operation");
            Assert.AreSame(request, fixture.Coordinator.ActiveRequest, "pending-stop-exact-active-request");
            Assert.IsTrue(fixture.Reporter.IsArmed(request), "pending-stop-matching-reporter-arm");
            Assert.AreEqual(1, fixture.Launcher.CallCount, "pending-stop-one-child-launch");

            EmitLifecycle(fixture.Launcher.Process!, request, WorkerProtocolV1.CancelledType, 0);
            fixture.Launcher.Process!.Exit(130);

            await stop!.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)!;
            Assert.AreEqual(ProcessingRunOutcome.Cancelled, receipt.Result.Outcome, "pending-stop-cancelled-terminal");
            Assert.IsNull(fixture.Coordinator.ActiveRequest, "pending-stop-matching-release");
            Assert.IsFalse(fixture.State.IsRunning, "pending-stop-idle-after-finality");
            Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "pending-stop-one-summary");
        }
        finally
        {
            fixture.State.OnChanged -= StopWhenPending;
            fixture.Launcher.Process?.Exit(130);
        }
    }

    [TestMethod]
    public async Task ManualChild_ReadyAndRunStartedStayTransportOnlyUntilEligibilityProjectsState()
    {
        await using var fixture = ManualChildFixture.Create();

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "state-timing-admission");
        await fixture.Launcher.Entered.Task.WaitAsync(Bound);

        ProcessingRunRequest request = fixture.Launcher.Request!;
        SessionTestProcess process = fixture.Launcher.Process!;
        var changes = 0;
        var eligibilityProjected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void RecordChange()
        {
            Interlocked.Increment(ref changes);
            if (fixture.State.LastRunStarted is not null)
            {
                eligibilityProjected.TrySetResult();
            }
        }
        fixture.State.OnChanged += RecordChange;
        try
        {
            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
            await fixture.Launcher.Session!.Startup.WaitAsync(Bound);
            Assert.IsTrue(fixture.State.IsRunning, "ready-retains-pending-state");
            Assert.IsNull(fixture.State.LastRunStarted, "ready-does-not-start-visible-run");
            Assert.AreEqual(0, Volatile.Read(ref changes), "ready-does-not-notify-state");

            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.LifecycleCategory,
                WorkerProtocolV1.RunStartedType,
                2,
                SessionTestSupport.Start,
                request.RunId,
                new RunStartedPayload("manual", SessionTestSupport.Start))));
            await fixture.Launcher.RunStartedAccepted.WaitAsync(Bound);
            Assert.IsTrue(fixture.State.IsRunning, "run-started-retains-pending-state");
            Assert.IsNull(fixture.State.LastRunStarted, "run-started-does-not-start-visible-run");
            Assert.AreEqual(0, Volatile.Read(ref changes), "run-started-does-not-notify-state");

            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.LifecycleCategory,
                WorkerProtocolV1.EligibilityDeterminedType,
                3,
                SessionTestSupport.Start,
                request.RunId,
                new EligibilityDeterminedPayload(0))));
            await eligibilityProjected.Task.WaitAsync(Bound);

            Assert.IsTrue(fixture.State.RecentLog.Any(line => line.Contains("nothing to process", StringComparison.Ordinal)), "zero-work-projected-from-worker");
            Assert.IsTrue(Volatile.Read(ref changes) > 0, "eligibility-starts-visible-state");

            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.TerminalCategory,
                WorkerProtocolV1.CompletedType,
                4,
                SessionTestSupport.Start,
                request.RunId,
                new CompletedPayload("manual", SessionTestSupport.Start, SessionTestSupport.Start, 0, 0, 0, 0))));
            process.Exit(0);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            Assert.IsFalse(fixture.State.IsRunning, "zero-work-terminal-idle");
            Assert.AreEqual(0L, fixture.State.ProcessedThisRun, "ui-processed-is-updated-count");
            Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "zero-work-one-summary");
        }
        finally
        {
            fixture.State.OnChanged -= RecordChange;
            process.Exit(0);
        }
    }


    [TestMethod]
    public async Task ManualChild_AdmissionRetainsOneExactRequestAndSilentlyRejectsDuplicateBeforeReadiness()
    {
        await using var fixture = ManualChildFixture.Create();

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "admission-first-accepted");
        await fixture.Launcher.Entered.Task.WaitAsync(Bound);

        ProcessingRunRequest request = fixture.Launcher.Request!;
        SessionTestProcess process = fixture.Launcher.Process!;
        var duplicateChanges = 0;
        void RecordDuplicateChange()
        {
            Interlocked.Increment(ref duplicateChanges);
        }
        fixture.State.OnChanged += RecordDuplicateChange;
        try
        {
            int logCount = fixture.State.RecentLog.Count;

            Assert.AreNotEqual(Guid.Empty, request.RunId, "admission-nonempty-run-id");
            Assert.AreSame(request, fixture.Coordinator.ActiveRequest, "admission-exact-active-request");
            Assert.IsTrue(fixture.Reporter.IsArmed(request), "admission-matching-reporter");
            Assert.IsFalse(fixture.Launcher.Session!.Startup.IsCompleted, "admission-returned-before-readiness");

            Assert.AreEqual(
                ProcessingRunAdmissionResult.AlreadyRunning,
                await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
                "admission-duplicate-rejected");

            Assert.AreEqual(1, fixture.Launcher.CallCount, "admission-duplicate-no-second-child-launch");
            Assert.AreSame(request, fixture.Launcher.Request, "admission-duplicate-retains-exact-request");
            Assert.AreSame(request, fixture.Coordinator.ActiveRequest, "admission-duplicate-no-replacement-request");
            Assert.AreEqual(0, Volatile.Read(ref duplicateChanges), "admission-duplicate-no-state-mutation");
            Assert.AreEqual(logCount, fixture.State.RecentLog.Count, "admission-duplicate-silent");

            EmitLifecycle(process, request, WorkerProtocolV1.CancelledType, 0);
            process.Exit(130);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        }
        finally
        {
            fixture.State.OnChanged -= RecordDuplicateChange;
            process.Exit(130);
        }
    }

    [TestMethod]
    public async Task ManualChild_SelectedControlPlaneProjectsDistinctProgressActivitiesAndTypedLogs()
    {
        await using var fixture = ManualChildFixture.Create();

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "projection-admission");
        await fixture.Launcher.Entered.Task.WaitAsync(Bound);

        ProcessingRunRequest request = fixture.Launcher.Request!;
        SessionTestProcess process = fixture.Launcher.Process!;
        const long total = 29;
        const long processed = 19;
        const long updated = 7;
        const long skipped = 9;
        const long failed = 3;
        Guid firstActivity = Guid.NewGuid();
        Guid secondActivity = Guid.NewGuid();
        var eligibilityProjected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void RecordEligibility()
        {
            if (fixture.State.TotalUnprocessed == total && fixture.State.LastRunStarted is not null)
            {
                eligibilityProjected.TrySetResult();
            }
        }
        fixture.State.OnChanged += RecordEligibility;
        try
        {
            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
            await fixture.Launcher.Session!.Startup.WaitAsync(Bound);

            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.LifecycleCategory,
                WorkerProtocolV1.RunStartedType,
                2,
                SessionTestSupport.Start,
                request.RunId,
                new RunStartedPayload("manual", SessionTestSupport.Start))));
            await fixture.Launcher.RunStartedAccepted.WaitAsync(Bound);

            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.LifecycleCategory,
                WorkerProtocolV1.EligibilityDeterminedType,
                3,
                SessionTestSupport.Start,
                request.RunId,
                new EligibilityDeterminedPayload(total))));
            await eligibilityProjected.Task.WaitAsync(Bound);

            long sequence = 4;
            for (long currentUpdated = 1; currentUpdated <= updated; currentUpdated++)
            {
                process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                    WorkerProtocolV1.ProgressCategory,
                    WorkerProtocolV1.ProgressChangedType,
                    sequence++,
                    SessionTestSupport.Start,
                    request.RunId,
                    new ProgressChangedPayload(currentUpdated, currentUpdated, 0, 0))));
            }

            for (long currentSkipped = 1; currentSkipped <= skipped; currentSkipped++)
            {
                process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                    WorkerProtocolV1.ProgressCategory,
                    WorkerProtocolV1.ProgressChangedType,
                    sequence++,
                    SessionTestSupport.Start,
                    request.RunId,
                    new ProgressChangedPayload(updated + currentSkipped, updated, currentSkipped, 0))));
            }

            for (long currentFailed = 1; currentFailed <= failed; currentFailed++)
            {
                process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                    WorkerProtocolV1.ProgressCategory,
                    WorkerProtocolV1.ProgressChangedType,
                    sequence++,
                    SessionTestSupport.Start,
                    request.RunId,
                    new ProgressChangedPayload(updated + skipped + currentFailed, updated, skipped, currentFailed))));
            }

            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.ActivityCategory,
                WorkerProtocolV1.ActivityStartedType,
                sequence++,
                SessionTestSupport.Start,
                request.RunId,
                new ActivityStartedPayload(firstActivity, "Country cache"))));
            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.ActivityCategory,
                WorkerProtocolV1.ActivityStartedType,
                sequence++,
                SessionTestSupport.Start,
                request.RunId,
                new ActivityStartedPayload(secondActivity, "Airport fallback"))));
            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.ActivityCategory,
                WorkerProtocolV1.ActivityEndedType,
                sequence++,
                SessionTestSupport.Start,
                request.RunId,
                new ActivityEndedPayload(firstActivity))));
            await fixture.Launcher.ActivityEndedAccepted.WaitAsync(Bound);

            Assert.AreEqual(updated, fixture.State.ProcessedThisRun, "projection-ui-uses-updated-not-aggregate");
            Assert.AreEqual(skipped, fixture.State.SkippedThisRun, "projection-skipped-count");
            Assert.AreEqual(failed, fixture.State.ErrorsThisRun, "projection-error-count");
            Assert.AreEqual("Airport fallback", fixture.State.CurrentActivity, "projection-overlapping-activity-remains-active");

            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.ActivityCategory,
                WorkerProtocolV1.ActivityEndedType,
                sequence++,
                SessionTestSupport.Start,
                request.RunId,
                new ActivityEndedPayload(secondActivity))));
            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.DiagnosticCategory,
                WorkerProtocolV1.LogEmittedType,
                sequence++,
                SessionTestSupport.Start,
                request.RunId,
                new LogEmittedPayload("information", "Selected child projected progress."))));
            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.DiagnosticCategory,
                WorkerProtocolV1.LogEmittedType,
                sequence++,
                SessionTestSupport.Start,
                request.RunId,
                new LogEmittedPayload("warning", "One source required fallback."))));
            process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
                WorkerProtocolV1.TerminalCategory,
                WorkerProtocolV1.CompletedType,
                sequence,
                SessionTestSupport.Start,
                request.RunId,
                new CompletedPayload("manual", SessionTestSupport.Start, SessionTestSupport.Start, processed, updated, skipped, failed))));
            process.Exit(0);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            Assert.AreEqual(total, fixture.State.TotalUnprocessed, "projection-total-count");
            Assert.AreEqual(updated, fixture.State.ProcessedThisRun, "projection-terminal-ui-uses-updated-not-aggregate");
            Assert.AreEqual(skipped, fixture.State.SkippedThisRun, "projection-terminal-skipped-count");
            Assert.AreEqual(failed, fixture.State.ErrorsThisRun, "projection-terminal-error-count");
            Assert.IsFalse(fixture.State.IsRunning, "projection-terminal-idle");
            Assert.IsNull(fixture.State.CurrentActivity, "projection-terminal-no-activity-residue");
            Assert.IsTrue(fixture.State.RecentLog.Any(line => line.Contains("Selected child projected progress.", StringComparison.Ordinal)), "projection-information-log");
            Assert.IsTrue(fixture.State.RecentLog.Any(line => line.Contains("[WARN] One source required fallback.", StringComparison.Ordinal)), "projection-warning-log");
            Assert.AreEqual(1, fixture.State.RecentLog.Count(line => line.Contains("Run complete.", StringComparison.Ordinal)), "projection-one-summary");
            Assert.AreEqual(1, fixture.Launcher.CallCount, "projection-one-selected-child-dispatch");
        }
        finally
        {
            fixture.State.OnChanged -= RecordEligibility;
            process.Exit(0);
        }
    }

    private static void EmitLifecycle(
        SessionTestProcess process,
        ProcessingRunRequest request,
        string terminalType,
        long eligibleCount)
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
            new EligibilityDeterminedPayload(eligibleCount))));
        process.StandardOutputSource.Enqueue(SessionTestSupport.Frame(new WorkerProtocolEvent(
            WorkerProtocolV1.TerminalCategory,
            terminalType,
            4,
            SessionTestSupport.Start,
            request.RunId,
            new CancelledPayload("manual", SessionTestSupport.Start, SessionTestSupport.Start, 0, 0, 0, 0))));
    }

    private sealed class ManualChildFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _provider;

        private ManualChildFixture(string root, ServiceProvider provider, ManualChildLauncher launcher)
        {
            _root = root;
            _provider = provider;
            Launcher = launcher;
            Coordinator = provider.GetRequiredService<ProcessingRunCoordinator>();
            Reporter = provider.GetRequiredService<ProcessingStateEventReporter>();
            State = provider.GetRequiredService<ProcessingState>();
        }

        internal ManualChildLauncher Launcher { get; }
        internal ProcessingRunCoordinator Coordinator { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal ProcessingState State { get; }

        internal static ManualChildFixture Create()
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change34", Guid.NewGuid().ToString("N"));
            var services = new ServiceCollection();
            var launcher = new ManualChildLauncher();
            try
            {
                services.AddWebComposition(ApplicationCompositionContext.Create(
                    CompositionEnvironment.Development,
                    root,
                    Path.Combine(root, "data"),
                    Path.Combine(root, "config")));
                services.RemoveAll<IWorkerCommandInvocationBuilder>();
                services.AddSingleton<IWorkerCommandInvocationBuilder, ManualInvocationBuilder>();
                services.RemoveAll<IChildWorkerLauncher>();
                services.AddSingleton<IChildWorkerLauncher>(launcher);
                return new ManualChildFixture(root, services.BuildServiceProvider(validateScopes: true), launcher);
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
            Launcher.Process?.Exit(0);
            await Coordinator.DisposeAsync();
            await _provider.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class ManualChildLauncher : IChildWorkerLauncher
    {
        private readonly TaskCompletionSource _runStartedAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _activityEndedAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProcessingRunRequest? Request { get; private set; }
        internal SessionTestProcess? Process { get; private set; }
        internal ChildWorkerSession? Session { get; private set; }
        internal int CallCount { get; private set; }
        internal Task RunStartedAccepted => _runStartedAccepted.Task;
        internal Task ActivityEndedAccepted => _activityEndedAccepted.Task;

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
            Process = new SessionTestProcess(new SessionInputStream(), ChildProcessKillOutcome.Requested, exitOnKill: false);
            Session = await ChildWorkerSession.CreateAsync(
                Process,
                request,
                new AwaitedInnerSink(eventSink, _runStartedAccepted, _activityEndedAccepted),
                options,
                new ChildWorkerObserverArmingAcknowledgements());
            Entered.TrySetResult();
            return new ChildWorkerLaunchResult.Started(Session);
        }
    }


    private sealed class AwaitedInnerSink(
        IWorkerProtocolEventSink inner,
        TaskCompletionSource runStartedAccepted,
        TaskCompletionSource activityEndedAccepted) : IWorkerProtocolEventSink
    {
        public async ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken)
        {
            await inner.AcceptAsync(@event, cancellationToken).ConfigureAwait(false);
            if (@event.Type == WorkerProtocolV1.RunStartedType)
            {
                runStartedAccepted.TrySetResult();
            }
            else if (@event.Type == WorkerProtocolV1.ActivityEndedType)
            {
                activityEndedAccepted.TrySetResult();
            }
        }
    }

    private sealed class ManualInvocationBuilder : IWorkerCommandInvocationBuilder
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
