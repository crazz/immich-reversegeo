using ImmichReverseGeo.Core.ApplicationRole;
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
using System.Text;
using System.Text.Json;
using WorkerInvocation = ImmichReverseGeo.Web.WorkerCommandInvocation.WorkerCommandInvocation;

namespace ImmichReverseGeo.Tests.ChildWorkerExecution;

[TestClass]
[TestCategory("Change33")]
public sealed class ChildBackendLifecycleTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [TestMethod]
    [TestCategory("Change47")]
    public async Task ExplicitV1Launch_RemovesAmbientV2AndDefersOneCancelUntilExecuteFlushThenReleasesExactHandle()
    {
        var input = new SessionInputStream { BlockFlushCall = 1 };
        var ambientEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ChildProcessEnvironmentPolicyDetails.ReservedProtocolVersionVariable] = "2",
            ["KEEP_ME"] = "unchanged"
        };
        await using var fixture = WebChildBackendFixture.Create(
            new ImmediateInvocationBuilder(),
            firstInput: input,
            firstEnvironment: ambientEnvironment);
        Task<ProcessingRunAdmissionResult> dispatch = fixture.Coordinator.TriggerManualAsync();
        Task? stop = null;
        try
        {
            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await dispatch.WaitAsync(Bound),
                "v1-ambient-admission");
            await fixture.Launcher.WaitUntilEnteredAsync();
            ProcessingRunRequest request = fixture.Launcher.Request!;
            SessionTestProcess process = fixture.Launcher.Process!;

            process.StandardOutputSource.Enqueue(
                SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
            await input.BlockedFlushEntered.WaitAsync(Bound);

            Assert.IsFalse(fixture.Launcher.Session!.ExecuteRequestAccepted.IsCompleted, "execute-not-accepted-before-flush");
            Assert.AreEqual(1, input.WriteCalls, "only-execute-written-before-flush");
            Assert.AreEqual(1, input.FlushCalls, "execute-flush-is-blocked");
            Assert.AreEqual(1, input.Frames.Count, "no-cancel-frame-before-execute-flush");
            Assert.AreEqual(
                ChildProcessEnvironmentPolicy.InheritCurrentAndRemoveReservedProtocolVersion,
                fixture.Launcher.Descriptor!.EnvironmentPolicy,
                "descriptor-explicit-v1-policy");
            Assert.IsFalse(
                fixture.Launcher.ChildEnvironment!.ContainsKey(
                    ChildProcessEnvironmentPolicyDetails.ReservedProtocolVersionVariable),
                "same-launch-child-environment-removes-ambient-selector");
            Assert.AreEqual("unchanged", fixture.Launcher.ChildEnvironment["KEEP_ME"], "same-launch-unrelated-environment");
            Assert.AreEqual("2", ambientEnvironment[ChildProcessEnvironmentPolicyDetails.ReservedProtocolVersionVariable], "parent-environment-unmutated");

            stop = fixture.Coordinator.StopActiveRun()
                ?? throw new AssertFailedException("the admitted worker did not expose its stop task");
            await fixture.Launcher.Session.FirstTerminationRequest.WaitAsync(Bound);
            Assert.AreEqual(1, input.WriteCalls, "stop-does-not-write-before-execute-flush");

            input.ReleaseBlockedFlush();
            await input.SecondFlush.WaitAsync(Bound);
            Assert.AreEqual(2, input.WriteCalls, "one-execute-one-cancel-write");
            Assert.AreEqual(2, input.FlushCalls, "one-execute-one-cancel-flush");
            Assert.AreSame(stop, fixture.Coordinator.StopActiveRun(), "repeated-stop-joins-one-operation");
            AssertV1ExecuteAndCancelFrames(input.Frames, request);

            var terminalCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveTerminal()
            {
                if (fixture.Reporter.GetFinalizationReceipt(request) is not null)
                {
                    terminalCommitted.TrySetResult();
                }
            }

            fixture.State.OnChanged += ObserveTerminal;
            try
            {
                EmitPostReadyTerminalSequence(process, request, WorkerProtocolV1.CancelledType);
                await terminalCommitted.Task.WaitAsync(Bound);
                Assert.AreSame(request, fixture.Coordinator.ActiveRequest, "terminal-retains-exact-handle-before-process-finality");
                Assert.IsFalse(fixture.Coordinator.WaitForActiveRunAsync().IsCompleted, "terminal-does-not-release-before-stream-and-exit-finality");
            }
            finally
            {
                fixture.State.OnChanged -= ObserveTerminal;
            }

            process.Exit(130);
            await stop.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)!;
            Assert.AreEqual(ProcessingRunOutcome.Cancelled, receipt.Result.Outcome, "authoritative-cancelled-terminal");
            Assert.AreSame(request, receipt.Request, "receipt-retains-admitted-request");
            Assert.IsNull(fixture.Coordinator.ActiveRequest, "exact-handle-released-after-finality");
            Assert.IsFalse(fixture.State.IsRunning, "processing-state-idle-after-finality");
            Assert.AreEqual(1, process.DisposeCalls, "owned-process-disposed-once");
            Assert.AreEqual(0, process.KillCalls, "cooperative-terminal-needs-no-kill");
        }
        finally
        {
            input.ReleaseBlockedFlush();
            fixture.Launcher.Process?.Exit(130);
            if (stop is not null)
            {
                await stop.WaitAsync(Bound);
            }

            await dispatch.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        }
    }

    [TestMethod]
    [TestCategory("Change44")]
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
            await fixture.StatusSink.AcceptedForwarded.Task.WaitAsync(Bound);

            ProcessingRunFinalizationReceipt receipt = fixture.Reporter.GetFinalizationReceipt(request)!;
            Assert.AreEqual(ProcessingRunOutcome.Completed, receipt.Result.Outcome, "child-terminal-outcome");
            Assert.IsFalse(fixture.Launcher.Process!.GetExitState() == ChildProcessExitState.Exited, "child-terminal-arrives-before-exit");
            Assert.AreSame(request, fixture.Coordinator.ActiveRequest, "child-terminal-retains-exact-handle");
            Assert.IsFalse(fixture.Coordinator.WaitForActiveRunAsync().IsCompleted, "child-terminal-waits-for-finality");
            Assert.AreEqual(1, fixture.Launcher.CallCount, "child-terminal-one-launch");
            Assert.AreEqual(ProcessAssetsWorkerState.Running, fixture.Status.Current.Worker);

            fixture.Launcher.Process.Exit(0);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

            Assert.IsNull(fixture.Coordinator.ActiveRequest, "child-terminal-releases-after-finality");
            Assert.IsFalse(fixture.State.IsRunning, "child-terminal-returns-idle");
            Assert.AreSame(receipt, fixture.Reporter.GetFinalizationReceipt(request), "child-terminal-single-receipt");
            Assert.AreEqual(ProcessAssetsWorkerState.Idle, fixture.Status.Current.Worker);
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
    [TestCategory("Change44")]
    public async Task WebComposition_StopWhileAcceptedProjectionIsGatedRemainsCancellingAfterForwarding()
    {
        await using var fixture = WebChildBackendFixture.Create(
            new ImmediateInvocationBuilder(),
            gateAcceptedStatus: true);
        Task<ProcessingRunAdmissionResult> dispatch = fixture.Coordinator.TriggerManualAsync();
        Task? stop = null;
        try
        {
            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await dispatch.WaitAsync(Bound),
                "status-stop-admission");
            await fixture.Launcher.WaitUntilEnteredAsync();
            ProcessingRunRequest request = fixture.Launcher.Request!;
            SessionTestProcess process = fixture.Launcher.Process!;
            Assert.AreEqual(ProcessAssetsWorkerState.Starting, fixture.Status.Current.Worker);

            process.StandardOutputSource.Enqueue(
                SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
            await fixture.Launcher.Session!.ExecuteRequestAccepted.WaitAsync(Bound);
            await fixture.StatusSink.AcceptedEntered.Task.WaitAsync(Bound);
            Assert.AreEqual(ProcessAssetsWorkerState.Starting, fixture.Status.Current.Worker);

            stop = fixture.Coordinator.StopActiveRun()
                ?? throw new AssertFailedException("the admitted worker did not expose its stop task");
            await fixture.Launcher.Session.FirstTerminationRequest.WaitAsync(Bound);
            Assert.AreEqual(ProcessAssetsWorkerState.Cancelling, fixture.Status.Current.Worker);
            long cancellingRevision = fixture.Status.Current.Revision;

            fixture.StatusSink.ReleaseAccepted.TrySetResult();
            await fixture.StatusSink.AcceptedForwarded.Task.WaitAsync(Bound);

            Assert.AreEqual(ProcessAssetsWorkerState.Cancelling, fixture.Status.Current.Worker);
            Assert.AreEqual(cancellingRevision, fixture.Status.Current.Revision);

            EmitPostReadyTerminalSequence(process, request, WorkerProtocolV1.CancelledType);
            process.Exit(130);
            await stop.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
            Assert.AreEqual(ProcessAssetsWorkerState.Idle, fixture.Status.Current.Worker);
        }
        finally
        {
            fixture.StatusSink.ReleaseAccepted.TrySetResult();
            fixture.Launcher.Process?.Exit(130);
            if (stop is not null)
            {
                await stop.WaitAsync(Bound);
            }
            await dispatch.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        }
    }

    [TestMethod]
    [TestCategory("Change44")]
    public async Task WebComposition_StopBeforeSessionAcceptanceNeverPublishesRunning()
    {
        var input = new SessionInputStream
        {
            BlockWriteCall = 1
        };
        await using var fixture = WebChildBackendFixture.Create(
            new ImmediateInvocationBuilder(),
            firstInput: input);
        var delivered = new List<ProcessAssetsWorkerState>();
        var deliveredGate = new object();
        var idleDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = fixture.Status.Subscribe(snapshot =>
        {
            lock (deliveredGate)
            {
                delivered.Add(snapshot.Worker);
            }

            if (snapshot.Worker == ProcessAssetsWorkerState.Idle && snapshot.Revision > 0)
            {
                idleDelivered.TrySetResult();
            }
        });
        Task<ProcessingRunAdmissionResult> dispatch = fixture.Coordinator.TriggerManualAsync();
        Task? stop = null;
        try
        {
            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await dispatch.WaitAsync(Bound),
                "status-stop-before-acceptance-admission");
            await fixture.Launcher.WaitUntilEnteredAsync();
            ProcessingRunRequest request = fixture.Launcher.Request!;
            SessionTestProcess process = fixture.Launcher.Process!;
            process.StandardOutputSource.Enqueue(
                SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
            await input.FirstWrite.WaitAsync(Bound);
            Assert.IsFalse(fixture.Launcher.Session!.ExecuteRequestAccepted.IsCompleted);

            stop = fixture.Coordinator.StopActiveRun()
                ?? throw new AssertFailedException("the admitted worker did not expose its stop task");
            await fixture.Launcher.Session.FirstTerminationRequest.WaitAsync(Bound);
            Assert.AreEqual(ProcessAssetsWorkerState.Cancelling, fixture.Status.Current.Worker);

            input.ReleaseBlockedWrite();
            await fixture.Launcher.Session.ExecuteRequestAccepted.WaitAsync(Bound);
            EmitPostReadyTerminalSequence(process, request, WorkerProtocolV1.CancelledType);
            process.Exit(130);
            await stop.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
            await idleDelivered.Task.WaitAsync(Bound);

            lock (deliveredGate)
            {
                CollectionAssert.DoesNotContain(delivered, ProcessAssetsWorkerState.Running);
            }
        }
        finally
        {
            input.ReleaseBlockedWrite();
            fixture.StatusSink.ReleaseAccepted.TrySetResult();
            fixture.Launcher.Process?.Exit(130);
            if (stop is not null)
            {
                await stop.WaitAsync(Bound);
            }
            await dispatch.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        }
    }

    [TestMethod]
    [TestCategory("Change44")]
    public async Task WebComposition_SlowThrowingReentrantStatusObserverCannotBlockWorkerLifecycle()
    {
        await using var fixture = WebChildBackendFixture.Create(new ImmediateInvocationBuilder());
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var throwObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reentrantObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var adversarial = fixture.Status.Subscribe(snapshot =>
        {
            if (snapshot.Worker == ProcessAssetsWorkerState.Starting)
            {
                slowEntered.TrySetResult();
                releaseSlow.Task.GetAwaiter().GetResult();
            }
            else if (snapshot.Worker == ProcessAssetsWorkerState.Running)
            {
                throwObserved.TrySetResult();
                throw new InvalidOperationException("Synthetic status observer failure.");
            }
            else if (snapshot.Worker == ProcessAssetsWorkerState.Cancelling)
            {
                _ = fixture.Status.Current;
                using var nested = fixture.Status.Subscribe(_ => { });
                reentrantObserved.TrySetResult();
            }
        });
        using var completion = fixture.Status.Subscribe(snapshot =>
        {
            if (snapshot.Worker == ProcessAssetsWorkerState.Idle && snapshot.Revision > 0)
            {
                idleObserved.TrySetResult();
            }
        });
        Task<ProcessingRunAdmissionResult> dispatch = fixture.Coordinator.TriggerManualAsync();
        Task? stop = null;
        try
        {
            await slowEntered.Task.WaitAsync(Bound);
            Assert.AreEqual(
                ProcessingRunAdmissionResult.Accepted,
                await dispatch.WaitAsync(Bound),
                "slow-observer-admission");
            await fixture.Launcher.WaitUntilEnteredAsync();
            Assert.IsFalse(releaseSlow.Task.IsCompleted, "the first status callback remains blocked");

            ProcessingRunRequest request = fixture.Launcher.Request!;
            SessionTestProcess process = fixture.Launcher.Process!;
            process.StandardOutputSource.Enqueue(
                SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
            await fixture.Launcher.Session!.ExecuteRequestAccepted.WaitAsync(Bound);
            await fixture.StatusSink.AcceptedForwarded.Task.WaitAsync(Bound);
            Assert.AreEqual(ProcessAssetsWorkerState.Running, fixture.Status.Current.Worker);

            stop = fixture.Coordinator.StopActiveRun()
                ?? throw new AssertFailedException("the running worker did not expose its stop task");
            await fixture.Launcher.Session.FirstTerminationRequest.WaitAsync(Bound);
            Assert.AreEqual(ProcessAssetsWorkerState.Cancelling, fixture.Status.Current.Worker);
            Assert.IsFalse(stop.IsCompleted, "Stop waits for the real session, not presentation callbacks");

            releaseSlow.TrySetResult();
            await throwObserved.Task.WaitAsync(Bound);
            await reentrantObserved.Task.WaitAsync(Bound);

            EmitPostReadyTerminalSequence(process, request, WorkerProtocolV1.CancelledType);
            process.Exit(130);
            await stop.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
            await idleObserved.Task.WaitAsync(Bound);
            Assert.AreEqual(ProcessAssetsWorkerState.Idle, fixture.Status.Current.Worker);
        }
        finally
        {
            releaseSlow.TrySetResult();
            fixture.StatusSink.ReleaseAccepted.TrySetResult();
            fixture.Launcher.Process?.Exit(130);
            if (stop is not null)
            {
                await stop.WaitAsync(Bound);
            }
            await dispatch.WaitAsync(Bound);
            await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);
        }
    }

    [TestMethod]
    [TestCategory("Change44")]
    public async Task WebComposition_EmptyScheduledCheckRetainsFailureUntilNextChildAdmission()
    {
        var noWork = new NoWorkScheduledRunGate();
        await using var fixture = WebChildBackendFixture.Create(
            new ImmediateInvocationBuilder(),
            scheduledGate: noWork);

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "status-first-admission");
        await fixture.Launcher.WaitUntilEnteredAsync();
        ProcessingRunRequest failedRequest = fixture.Launcher.Request!;
        SessionTestProcess failedProcess = fixture.Launcher.Process!;
        failedProcess.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
        await fixture.Launcher.Session!.ExecuteRequestAccepted.WaitAsync(Bound);
        failedProcess.Exit(42);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        ProcessAssetsWebStatusSnapshot failed = fixture.Status.Current;
        Assert.AreEqual(ProcessAssetsWorkerState.Failed, failed.Worker);
        Assert.IsNotNull(failed.FailureSummary);

        Assert.AreEqual(
            ScheduledTriggerResult.AcceptedAfterTerminal,
            await ((IScheduledRunTrigger)fixture.Coordinator)
                .TriggerScheduledAsync(CancellationToken.None)
                .WaitAsync(Bound),
            "status-empty-schedule");
        Assert.AreEqual(1, noWork.CallCount);
        Assert.AreEqual(1, fixture.Launcher.CallCount, "empty schedule launches no child");
        Assert.AreSame(failed, fixture.Status.Current, "local empty completion cannot clear retained failure");

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "status-replacement-admission");
        await fixture.Launcher.SecondEntered.Task.WaitAsync(Bound);

        Assert.AreEqual(2, fixture.Launcher.CallCount);
        Assert.AreNotSame(failedRequest, fixture.Launcher.Request);
        Assert.AreEqual(ProcessAssetsWorkerState.Starting, fixture.Status.Current.Worker);
        Assert.IsNull(fixture.Status.Current.FailureSummary);
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
    [TestCategory("Change44")]
    public async Task WebComposition_HostileFailureEvidenceRendersOnlyClosedStatusSummary()
    {
        const string stderrSecret = "stderr-secret-44";
        const string processDetail = "pid-99221";
        const string protocolDetail = "frame-secret-44";
        const string lastErrorSecret = "last-error-secret-44";
        await using var fixture = WebChildBackendFixture.Create(new ImmediateInvocationBuilder());

        Assert.AreEqual(
            ProcessingRunAdmissionResult.Accepted,
            await fixture.Coordinator.TriggerManualAsync().WaitAsync(Bound),
            "hostile-evidence-admission");
        await fixture.Launcher.WaitUntilEnteredAsync();
        SessionTestProcess process = fixture.Launcher.Process!;
        process.StandardErrorSource.Enqueue(Encoding.UTF8.GetBytes(
            $"{processDetail} {protocolDetail} password={stderrSecret}\n"));
        process.StandardOutputSource.Enqueue(
            SessionTestSupport.Frame(WorkerProtocolMapper.Ready(1, SessionTestSupport.Start)));
        await fixture.Launcher.Session!.ExecuteRequestAccepted.WaitAsync(Bound);
        process.Exit(42);
        await fixture.Coordinator.WaitForActiveRunAsync().WaitAsync(Bound);

        ChildWorkerCompletionObservation evidence =
            await fixture.Launcher.Session.EvidenceFinality.WaitAsync(Bound);
        StringAssert.Contains(evidence.StandardErrorTail.Text, processDetail);
        StringAssert.Contains(evidence.StandardErrorTail.Text, protocolDetail);
        StringAssert.Contains(evidence.StandardErrorTail.Text, stderrSecret);

        ProcessAssetsWebStatusSnapshot status = fixture.Status.Current;
        Assert.AreEqual(ProcessAssetsWorkerState.Failed, status.Worker);
        Assert.IsNotNull(status.FailureSummary);
        Assert.IsTrue(Enum.GetValues<WorkerRunFailureCategory>()
            .Select(WorkerRunDiagnostics.Describe)
            .Contains(status.FailureSummary, StringComparer.Ordinal));
        fixture.State.ReportErrorDiagnostic(lastErrorSecret);
        Assert.AreEqual(lastErrorSecret, fixture.State.LastError);

        var dashboard = WebStatusRenderingTests.CreateDashboard(fixture.Status, fixture.State);
        var navigation = WebStatusRenderingTests.CreateNavigation(fixture.Status);
        await using var dashboardRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await using var navigationRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await dashboardRenderer.AttachAsync(dashboard);
        await navigationRenderer.AttachAsync(navigation);
        string statusCard = (await dashboardRenderer.ReadAsync())
            .ElementTextByCssClass("worker-status-card");
        string navStatus = (await navigationRenderer.ReadAsync())
            .ElementTextByCssClass("nav-worker-status");

        StringAssert.Contains(statusCard, "Worker: Failed");
        StringAssert.Contains(statusCard, status.FailureSummary);
        StringAssert.Contains(navStatus, "Worker: Failed");
        foreach (string forbidden in new[]
        {
            stderrSecret,
            processDetail,
            protocolDetail,
            lastErrorSecret
        })
        {
            Assert.IsFalse(statusCard.Contains(forbidden, StringComparison.Ordinal));
            Assert.IsFalse(navStatus.Contains(forbidden, StringComparison.Ordinal));
            Assert.IsFalse(status.FailureSummary.Contains(forbidden, StringComparison.Ordinal));
        }
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

    private static void EmitPostReadyTerminalSequence(
        SessionTestProcess process,
        ProcessingRunRequest request,
        string terminalType)
    {
        DateTimeOffset at = SessionTestSupport.Start;
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

    private static void AssertV1ExecuteAndCancelFrames(
        IReadOnlyList<byte[]> frames,
        ProcessingRunRequest request)
    {
        Assert.AreEqual(2, frames.Count, "one-execute-and-one-cancel-frame");
        WorkerProtocolControllerParseResult execute = WorkerProtocolCodec.ParseControllerInput(frames[0]);
        WorkerProtocolControllerParseResult cancel = WorkerProtocolCodec.ParseControllerInput(frames[1]);
        Assert.IsTrue(execute.IsSuccess, "v1-execute-parses");
        Assert.IsTrue(cancel.IsSuccess, "v1-cancel-parses");
        Assert.AreEqual(WorkerProtocolV1.Version, ReadVersion(frames[0]), "v1-execute-version");
        Assert.AreEqual(WorkerProtocolV1.Version, ReadVersion(frames[1]), "v1-cancel-version");
        Assert.AreEqual(WorkerProtocolV1.ExecuteType, execute.Message!.Type, "v1-execute-type");
        Assert.AreEqual(WorkerProtocolV1.CancelType, cancel.Message!.Type, "v1-cancel-type");
        CollectionAssert.AreEqual(
            frames[0],
            WorkerProtocolCodec.SerializeControllerInput(execute.Message),
            "v1-execute-canonical-bytes-match-frozen-codec");
        CollectionAssert.AreEqual(
            frames[1],
            WorkerProtocolCodec.SerializeControllerInput(cancel.Message),
            "v1-cancel-canonical-bytes-match-frozen-codec");
        Assert.AreEqual(request.RunId, execute.Message.RunId, "execute-admitted-run-id");
        Assert.AreEqual(request.RunId, cancel.Message.RunId, "cancel-admitted-run-id");
        ProcessingRunRequest encodedRequest = ((ExecuteRequestPayload)execute.Message.Payload).Request;
        Assert.AreEqual(request.RunId, encodedRequest.RunId, "execute-request-run-id");
        Assert.AreEqual(request.Trigger, encodedRequest.Trigger, "execute-request-trigger");
    }

    private static int ReadVersion(byte[] frame)
    {
        using JsonDocument document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("version").GetInt32();
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
            Status = provider.GetRequiredService<IProcessAssetsWebStatus>();
            StatusSink = provider.GetRequiredService<RecordingStatusSink>();
        }

        internal ControlledSessionLauncher Launcher { get; }
        internal ProcessingRunCoordinator Coordinator { get; }
        internal ProcessingStateEventReporter Reporter { get; }
        internal ProcessingState State { get; }
        internal IProcessAssetsWebStatus Status { get; }
        internal RecordingStatusSink StatusSink { get; }

        internal static WebChildBackendFixture Create(
            IWorkerCommandInvocationBuilder builder,
            bool startFailure = false,
            TimeProvider? timeProvider = null,
            ChildProcessKillOutcome killOutcome = ChildProcessKillOutcome.Requested,
            bool exitOnKill = true,
            SessionInputStream? firstInput = null,
            Dictionary<string, string?>? firstEnvironment = null,
            IProcessingWorkDetector? scheduledGate = null,
            bool gateAcceptedStatus = false)
        {
            string root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-change33", Guid.NewGuid().ToString("N"));
            var launcher = new ControlledSessionLauncher(
                startFailure,
                killOutcome,
                exitOnKill,
                firstInput,
                firstEnvironment);
            try
            {
                var services = new ServiceCollection();
                services.AddWebComposition(ApplicationCompositionContext.Create(
                    CompositionEnvironment.Development,
                    root,
                    Path.Combine(root, "data"),
                    Path.Combine(root, "config"),
                    DeploymentMode.Standard));
                services.RemoveAll<IProcessAssetsWorkerStatusSink>();
                services.AddSingleton(sp => new RecordingStatusSink(
                    sp.GetRequiredService<ProcessAssetsWebStatus>(),
                    gateAcceptedStatus));
                services.AddSingleton<IProcessAssetsWorkerStatusSink>(sp =>
                    sp.GetRequiredService<RecordingStatusSink>());
                if (scheduledGate is not null)
                {
                    services.RemoveAll<IProcessingWorkDetector>();
                    services.AddSingleton(scheduledGate);
                }
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
        private readonly SessionInputStream? _firstInput;
        private readonly Dictionary<string, string?>? _firstEnvironment;

        internal ControlledSessionLauncher(
            bool startFailure,
            ChildProcessKillOutcome killOutcome,
            bool exitOnKill,
            SessionInputStream? firstInput,
            Dictionary<string, string?>? firstEnvironment)
        {
            _startFailure = startFailure;
            _killOutcome = killOutcome;
            _exitOnKill = exitOnKill;
            _firstInput = firstInput;
            _firstEnvironment = firstEnvironment;
        }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProcessingRunRequest? Request { get; private set; }
        internal SessionTestProcess? Process { get; private set; }
        internal ChildWorkerSession? Session { get; private set; }
        internal ChildWorkerObserverArmingAcknowledgements? ObserverArming { get; private set; }
        internal ChildProcessStartDescriptor? Descriptor { get; private set; }
        internal Dictionary<string, string?>? ChildEnvironment { get; private set; }
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
            WorkerJobDispatch dispatch,
            IWorkerJobEventSink eventSink,
            ChildWorkerLauncherOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            var processAssets = Assert.IsInstanceOfType<ProcessAssetsWorkerJobDispatch>(dispatch);
            ProcessingRunRequest request = processAssets.Request.ProcessingRequest;
            CallCount++;
            Request = request;
            if (_startFailure)
            {
                Entered.TrySetResult();
                return new ChildWorkerLaunchResult.StartFailed(ChildWorkerStartFailureCategory.ProcessStartFailed);
            }

            var factory = new ControlledProcessFactory(
                CallCount == 1 ? _firstInput : null,
                _killOutcome,
                _exitOnKill,
                CallCount == 1 ? _firstEnvironment : null);
            ChildWorkerLaunchResult result = await new ChildWorkerLauncher(
                factory,
                () => ObserverArming = new ChildWorkerObserverArmingAcknowledgements()).LaunchAsync(
                invocation,
                dispatch,
                eventSink,
                options,
                cancellationToken);
            Process = factory.Process;
            Descriptor = factory.Descriptor;
            ChildEnvironment = factory.ChildEnvironment;
            Session = Assert.IsInstanceOfType<ChildWorkerLaunchResult.Started>(result, "controlled-real-launcher").Session;
            if (CallCount == 1)
            {
                Entered.TrySetResult();
            }
            else if (CallCount == 2)
            {
                SecondEntered.TrySetResult();
            }
            return result;
        }

        private sealed class ControlledProcessFactory(
            SessionInputStream? input,
            ChildProcessKillOutcome killOutcome,
            bool exitOnKill,
            Dictionary<string, string?>? parentEnvironment) : IChildProcessFactory
        {
            internal SessionTestProcess? Process { get; private set; }
            internal ChildProcessStartDescriptor? Descriptor { get; private set; }
            internal Dictionary<string, string?>? ChildEnvironment { get; private set; }

            public ValueTask<IChildProcess?> StartAsync(
                ChildProcessStartDescriptor descriptor,
                CancellationToken cancellationToken)
            {
                Descriptor = descriptor;
                var source = parentEnvironment
                    ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                ChildEnvironment = new Dictionary<string, string?>(source, source.Comparer);
                SystemChildProcessFactory.ApplyEnvironmentPolicy(
                    ChildEnvironment,
                    descriptor.EnvironmentPolicy);
                Process = new SessionTestProcess(
                    input ?? new SessionInputStream(),
                    killOutcome,
                    exitOnKill);
                return ValueTask.FromResult<IChildProcess?>(Process);
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

    private sealed class RecordingStatusSink(ProcessAssetsWebStatus status, bool gateAccepted)
        : IProcessAssetsWorkerStatusSink
    {
        internal TaskCompletionSource AcceptedEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AcceptedForwarded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseAccepted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Admit(ProcessingRunRequest exactRequest, bool cancellationAlreadyWon)
        {
            ((IProcessAssetsWorkerStatusSink)status).Admit(exactRequest, cancellationAlreadyWon);
        }

        public void ObserveTransport(ProcessingRunRequest exactRequest, WorkerRunTransportPhase phase)
        {
            if (phase != WorkerRunTransportPhase.Accepted)
            {
                ((IProcessAssetsWorkerStatusSink)status).ObserveTransport(exactRequest, phase);
                return;
            }

            AcceptedEntered.TrySetResult();
            if (gateAccepted)
            {
                ReleaseAccepted.Task.GetAwaiter().GetResult();
            }

            try
            {
                ((IProcessAssetsWorkerStatusSink)status).ObserveTransport(exactRequest, phase);
            }
            finally
            {
                AcceptedForwarded.TrySetResult();
            }
        }

        public void ObserveCancellation(ProcessingRunRequest exactRequest)
        {
            ((IProcessAssetsWorkerStatusSink)status).ObserveCancellation(exactRequest);
        }

        public void ObserveFinality(
            ProcessingRunRequest exactRequest,
            ProcessingRunOutcome outcome,
            WorkerRunFailureCategory category)
        {
            ((IProcessAssetsWorkerStatusSink)status).ObserveFinality(exactRequest, outcome, category);
        }

        public void Release(ProcessingRunRequest exactRequest)
        {
            ((IProcessAssetsWorkerStatusSink)status).Release(exactRequest);
        }
    }

    private sealed class NoWorkScheduledRunGate : IProcessingWorkDetector
    {
        internal int CallCount { get; private set; }

        public Task<ProcessingWorkDetectionResult> DetectAsync(ProcessingWorkDetectionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(ProcessingWorkDetectorStub.Result(false));
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
