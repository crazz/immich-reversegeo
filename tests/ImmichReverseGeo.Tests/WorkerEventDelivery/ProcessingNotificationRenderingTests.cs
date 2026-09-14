using System.Reflection;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Tests.WorkerEventDelivery;

[TestClass]
[TestCategory("Change65")]
[DoNotParallelize]
public sealed class ProcessingNotificationRenderingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task BridgeBurst_UpdatesDashboardAndLogsOncePerWindowThenFlushesCompleteTerminalSnapshot()
    {
        var clock = new CancellationTestClock();
        await using var state = new ProcessingState(clock, new WorkerEventDeliveryPolicy());
        using var status = new ProcessAssetsWebStatus(DeploymentMode.Standard);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        var reporter = new ProcessingStateEventReporter(state);
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(request));
        await using var bridge = new WorkerEventStateBridgeFactory(reporter).Create(request);
        var started = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        long sequence = 1;
        await bridge.AcceptAsync(WorkerProtocolMapper.Ready(sequence++, started), CancellationToken.None);
        await SendAsync(new RunStarted(request, started));
        await SendAsync(new EligibilityDetermined(request, 1000));
        var dashboard = WebStatusRenderingTests.CreateDashboard(status, state);
        var logs = new ImmichReverseGeo.Web.Components.Pages.Logs();
        Inject(logs, "State", state);
        await using var dashboardRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await using var logsRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await dashboardRenderer.AttachAsync(dashboard);
        await logsRenderer.AttachAsync(logs);
        int dashboardBefore = dashboardRenderer.RenderCount;
        int logsBefore = logsRenderer.RenderCount;
        for (int count = 1; count <= 1000; count++)
        {
            await SendAsync(new ProgressChanged(request, new ProcessingProgress(count, count, 0, 0)));
        }

        state.AppendLog("Latest diagnostic before the terminal.");
        Assert.AreEqual(1000L, state.ProcessedThisRun);
        Assert.AreEqual(dashboardBefore, dashboardRenderer.RenderCount);
        Assert.AreEqual(logsBefore, logsRenderer.RenderCount);
        Task dashboardTick = dashboardRenderer.NextRenderAsync();
        Task logsTick = logsRenderer.NextRenderAsync();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await Task.WhenAll(dashboardTick, logsTick).WaitAsync(Bound);
        Assert.AreEqual(dashboardBefore + 1, dashboardRenderer.RenderCount, "one state notification reaches the real Dashboard handler");
        Assert.AreEqual(logsBefore + 1, logsRenderer.RenderCount, "one state notification reaches the real Logs handler");
        StringAssert.Contains((await logsRenderer.ReadAsync()).Text, "Latest diagnostic before the terminal.");

        Task logsFinal = logsRenderer.NextRenderAsync();
        await SendAsync(new RunFinished(request, new ProcessingRunResult(
            request, started, started.AddSeconds(1), 1000, 1000, 0, 0, ProcessingRunOutcome.Completed, null)));
        Assert.AreEqual(1L, state.NotificationObservation!.FinalAccepted, "the reporter accepts final dispatch before returning finality");
        await logsFinal.WaitAsync(Bound);
        Assert.IsFalse(state.IsRunning);
        StringAssert.Contains((await logsRenderer.ReadAsync()).Text, "Run complete. Processed=1000 Skipped=0 Errors=0");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1L, state.NotificationObservation.OrdinaryDispatched);
        Assert.AreEqual(1L, state.NotificationObservation.FinalDispatched, "summary append and CompleteRun share one complete final snapshot");

        Task SendAsync(ProcessingEvent processingEvent)
        {
            var frame = processingEvent switch
            {
                RunStarted item => WorkerProtocolMapper.Map(item, sequence++),
                RunFinished item => WorkerProtocolMapper.Map(item, sequence++),
                _ => WorkerProtocolMapper.Map(processingEvent, sequence++, started)
            };
            return bridge.AcceptAsync(frame, CancellationToken.None).AsTask();
        }
    }

    [TestMethod]
    public async Task WebStatusCadence_UpdatesDashboardAndNavigationAndReconnectReadsCurrentImmediately()
    {
        var clock = new CancellationTestClock();
        await using var status = new ProcessAssetsWebStatus(DeploymentMode.Standard, clock, new WorkerEventDeliveryPolicy());
        using var state = new ProcessingState();
        var dashboard = WebStatusRenderingTests.CreateDashboard(status, state);
        var navigation = WebStatusRenderingTests.CreateNavigation(status);
        await using var dashboardRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await using var navigationRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await dashboardRenderer.AttachAsync(dashboard);
        await navigationRenderer.AttachAsync(navigation);
        var sink = (IProcessAssetsWorkerStatusSink)status;
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.Manual);
        sink.Admit(request, cancellationAlreadyWon: false);
        sink.ObserveTransport(request, WorkerRunTransportPhase.Accepted);
        Assert.AreEqual(ProcessAssetsWorkerState.Running, status.Current.Worker);
        Assert.AreEqual(0L, status.NotificationObservation!.OrdinaryDispatched);
        var reconnected = WebStatusRenderingTests.CreateNavigation(status);
        await using var reconnectedRenderer = new WebStatusRenderingTests.ComponentRenderer();
        await reconnectedRenderer.AttachAsync(reconnected);
        StringAssert.Contains((await reconnectedRenderer.ReadAsync()).Text, "Worker: Running");

        Task first = dashboardRenderer.NextRenderAsync();
        Task second = navigationRenderer.NextRenderAsync();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await Task.WhenAll(first, second).WaitAsync(Bound);
        StringAssert.Contains((await dashboardRenderer.ReadAsync()).Text, "Worker: Running");
        StringAssert.Contains((await navigationRenderer.ReadAsync()).Text, "Worker: Running");
        Assert.AreEqual(1L, status.NotificationObservation.OrdinaryDispatched);
        first = dashboardRenderer.NextRenderAsync();
        second = navigationRenderer.NextRenderAsync();
        sink.ObserveFinality(request, ProcessingRunOutcome.Failed, WorkerRunFailureCategory.Crash);
        sink.Release(request);
        await Task.WhenAll(first, second).WaitAsync(Bound);
        StringAssert.Contains((await dashboardRenderer.ReadAsync()).Text, "ProcessAssets worker failed.");
        StringAssert.Contains((await navigationRenderer.ReadAsync()).Text, "Worker: Failed");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1L, status.NotificationObservation.FinalAccepted, "release preserves the already-notified retained failure revision");
        Assert.AreEqual(1L, status.NotificationObservation.FinalDispatched);
        Assert.AreEqual(1L, status.NotificationObservation.OrdinaryDispatched);
    }

    [TestMethod]
    public async Task SlowSubscriber_NewRunInvalidatesRemainingOldNotifications()
    {
        var clock = new CancellationTestClock();
        await using var state = new ProcessingState(clock, new WorkerEventDeliveryPolicy());
        var entered = Signal();
        var release = Signal();
        var newDispatchFinished = Signal();
        int firstCalls = 0;
        int laterCalls = 0;
        state.OnChanged += () =>
        {
            if (Interlocked.Increment(ref firstCalls) == 1)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        state.OnChanged += () => Interlocked.Increment(ref laterCalls);
        state.OnChanged += () =>
        {
            if (Volatile.Read(ref firstCalls) == 2)
            {
                newDispatchFinished.TrySetResult();
            }
        };
        try
        {
            state.AppendLog("Old operation.");
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await entered.Task.WaitAsync(Bound);
            state.MarkPending();
            state.ApplyProgress(9, 0, 0);
            release.TrySetResult();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await newDispatchFinished.Task.WaitAsync(Bound);
            Assert.AreEqual(1, Volatile.Read(ref laterCalls), "the old dispatch cannot continue through subscribers after ownership changes");
            Assert.AreEqual(9L, state.ProcessedThisRun);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task QueuedLogsRender_IsSuppressedAfterDisposalWhileProjectionAndFinalityRemainResponsive()
    {
        var clock = new CancellationTestClock();
        await using var state = new ProcessingState(clock, new WorkerEventDeliveryPolicy());
        var logs = new ImmichReverseGeo.Web.Components.Pages.Logs();
        Inject(logs, "State", state);
        await using var renderer = new WebStatusRenderingTests.ComponentRenderer();
        await renderer.AttachAsync(logs);
        var entered = Signal();
        var release = Signal();
        var posted = Signal();
        state.OnChanged += () => posted.TrySetResult();
        Task heldRenderer = Task.Run(() => renderer.Dispatcher.InvokeAsync(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }));
        try
        {
            await entered.Task.WaitAsync(Bound);
            int before = renderer.RenderCount;
            state.AppendLog("Queued while renderer is held.");
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await posted.Task.WaitAsync(Bound);
            Assert.IsFalse(heldRenderer.IsCompleted);
            state.ApplyProgress(7, 0, 0);
            state.FlushFinalNotification();
            Assert.AreEqual(7L, state.ProcessedThisRun, "projection never waits on the held Blazor dispatcher");
            Assert.AreEqual(1L, state.NotificationObservation!.FinalAccepted);
            logs.Dispose();
            release.TrySetResult();
            await heldRenderer.WaitAsync(Bound);
            await renderer.Dispatcher.InvokeAsync(() => { });
            Assert.AreEqual(before, renderer.RenderCount, "the already-posted callback rechecks disposal on the renderer");
        }
        finally
        {
            release.TrySetResult();
            await heldRenderer.WaitAsync(Bound);
        }
    }

    private static void Inject(object component, string name, object value) => component.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(component, value);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
