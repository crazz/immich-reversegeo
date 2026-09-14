using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Web.WorkerEventStateBridge;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Change27")]
public sealed class WorkerEventStateBridgeConcurrencyTests
{
    [TestMethod]
    public async Task HeldProjection_BackpressuresCallbackAndPreventsLaterEventOvertaking()
    {
        var hold = new ProjectionHold();
        await using var test = new BridgeTestCase(beforeProjection: hold.Observe);
        using var releaseBeforeBridgeDisposal = hold;
        await PrepareAsync(test);
        var first = Task.Run(() => test.Bridge.AcceptAsync(
            test.Frame(new EligibilityDetermined(test.Request, 2), 3), CancellationToken.None).AsTask());
        await hold.Entered.Task.WaitAsync(BridgeTestCase.Bound);
        var second = test.Bridge.AcceptAsync(test.Frame(
            new LogEmitted(test.Request, ProcessingLogLevel.Information, "after eligibility"), 4), CancellationToken.None).AsTask();
        Assert.IsFalse(first.IsCompleted, "Accepted delivery must await the held state projection.");
        Assert.IsFalse(second.IsCompleted, "Later delivery must wait behind the accepted event.");
        Assert.AreEqual(0, test.Notifications, "The gate holds before any visible eligibility mutation.");
        hold.Release();
        await Task.WhenAll(first, second).WaitAsync(BridgeTestCase.Bound);
        Assert.AreEqual(2L, test.State.TotalUnprocessed);
        Assert.AreEqual(2, test.Logs.Length);
        StringAssert.EndsWith(test.Logs[0], "Run started. 2 assets to process.");
        StringAssert.EndsWith(test.Logs[1], "after eligibility");
        Assert.IsTrue(test.Notifications >= 2);
    }

    [TestMethod]
    public async Task CancellationAfterGateAdmission_DoesNotRetractProjectionOrItsNotifications()
    {
        var hold = new ProjectionHold();
        await using var test = new BridgeTestCase(beforeProjection: hold.Observe);
        using var releaseBeforeBridgeDisposal = hold;
        using var caller = new CancellationTokenSource();
        using var unrelatedWait = new CancellationTokenSource();
        await PrepareAsync(test);
        var projection = Task.Run(() => test.Bridge.AcceptAsync(
            test.Frame(new EligibilityDetermined(test.Request, 7), 3), caller.Token).AsTask());
        await hold.Entered.Task.WaitAsync(BridgeTestCase.Bound);
        caller.Cancel();
        unrelatedWait.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => projection.WaitAsync(unrelatedWait.Token));
        Assert.IsFalse(projection.IsCompleted, "Cancelling a caller's wait cannot cancel accepted state mutation.");
        hold.Release();
        await projection.WaitAsync(BridgeTestCase.Bound);
        Assert.AreEqual(7L, test.State.TotalUnprocessed);
        Assert.IsTrue(test.Notifications > 0);
        Assert.IsNull(test.Bridge.FirstObservation);
    }

    [TestMethod]
    public async Task CancelledQueuedCallback_LeavesNextSequenceAvailable()
    {
        var hold = new ProjectionHold();
        await using var test = new BridgeTestCase(beforeProjection: hold.Observe);
        using var releaseBeforeBridgeDisposal = hold;
        using var waiting = new CancellationTokenSource();
        await PrepareAsync(test);
        var first = Task.Run(() => test.Bridge.AcceptAsync(
            test.Frame(new EligibilityDetermined(test.Request, 0), 3), CancellationToken.None).AsTask());
        await hold.Entered.Task.WaitAsync(BridgeTestCase.Bound);
        var next = test.Frame(new LogEmitted(test.Request, ProcessingLogLevel.Information, "retry waiting delivery"), 4);
        var cancelledWait = test.Bridge.AcceptAsync(next, waiting.Token).AsTask();
        waiting.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledWait);
        hold.Release();
        await first.WaitAsync(BridgeTestCase.Bound);
        await test.Bridge.AcceptAsync(next, CancellationToken.None);
        StringAssert.EndsWith(test.Logs.Last(), "retry waiting delivery");
        Assert.IsNull(test.Bridge.FirstObservation, "A caller that never entered the gate is not a projection failure.");
    }

    [TestMethod]
    public async Task Disposal_WaitsForAcceptedProjectionAndSuppressesQueuedAndLaterCallbacks()
    {
        var hold = new ProjectionHold();
        await using var test = new BridgeTestCase(beforeProjection: hold.Observe);
        using var releaseBeforeBridgeDisposal = hold;
        await PrepareAsync(test);
        var first = Task.Run(() => test.Bridge.AcceptAsync(
            test.Frame(new EligibilityDetermined(test.Request, 3), 3), CancellationToken.None).AsTask());
        await hold.Entered.Task.WaitAsync(BridgeTestCase.Bound);
        var suppressed = test.Frame(new LogEmitted(test.Request, ProcessingLogLevel.Error, "must not project"), 4);
        var queued = test.Bridge.AcceptAsync(suppressed, CancellationToken.None).AsTask();
        var disposal = test.Bridge.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted, "Disposal must await the already accepted projection.");
        hold.Release();
        await Task.WhenAll(first, queued, disposal).WaitAsync(BridgeTestCase.Bound);
        Assert.AreEqual(3L, test.State.TotalUnprocessed);
        Assert.IsTrue(test.State.IsRunning, "Disposal must not fabricate completion.");
        Assert.IsNull(test.State.LastError);
        Assert.AreEqual(1, test.Logs.Length);
        Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.NonterminalDisposed>(test.Bridge.FirstObservation);
        var final = test.Snapshot();
        await test.Bridge.AcceptAsync(suppressed, CancellationToken.None);
        await test.Bridge.DisposeAsync();
        Assert.AreEqual(final, test.Snapshot());
    }

    [TestMethod]
    public async Task ThrowingObserver_PoisonsUncertainProjectionWithoutRetainingRawFailureOrRetrying()
    {
        await using var test = new BridgeTestCase();
        await PrepareAsync(test);
        Action brokenObserver = () => throw new InvalidOperationException("private observer detail");
        test.State.OnChanged += brokenObserver;
        var frame = test.Frame(new EligibilityDetermined(test.Request, 9), 3);
        var failure = await Assert.ThrowsExactlyAsync<WorkerEventStateBridgeException>(
            () => test.Bridge.AcceptAsync(frame, CancellationToken.None).AsTask());
        test.State.OnChanged -= brokenObserver;
        Assert.IsNull(failure.InnerException);
        Assert.IsFalse(failure.ToString().Contains("private observer detail", StringComparison.Ordinal));
        Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.ProjectionFailed>(test.Bridge.FirstObservation);
        Assert.AreEqual(9L, test.State.TotalUnprocessed, "The observer threw after an irreversible visible mutation.");
        var observation = test.Bridge.FirstObservation;
        var committed = test.Snapshot();
        await Assert.ThrowsExactlyAsync<WorkerEventStateBridgeException>(
            () => test.Bridge.AcceptAsync(frame, CancellationToken.None).AsTask());
        Assert.AreEqual(committed, test.Snapshot(), "Uncertain accepted projection must never be replayed.");
        Assert.AreSame(observation, test.Bridge.FirstObservation);
    }

    [TestMethod]
    public async Task ActivityAbandonment_AttemptsEveryOwnedScopeWhenNotificationThrows()
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(0);
        await test.SendAsync(new ActivityStarted(test.Request, Guid.NewGuid(), "first"));
        await test.SendAsync(new ActivityStarted(test.Request, Guid.NewGuid(), "second"));
        var before = test.Snapshot();
        Action brokenObserver = () => throw new InvalidOperationException("private cleanup detail");
        test.State.OnChanged += brokenObserver;
        try
        {
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() => test.Adapter.AbandonProjectedActivities(test.Request));
            Assert.AreEqual("private cleanup detail", failure.Message);
        }
        finally
        {
            test.State.OnChanged -= brokenObserver;
        }
        Assert.IsNull(test.State.CurrentActivity, "Both owned scopes must be removed despite notification failure.");
        Assert.AreEqual(before.Logs, string.Join('\n', test.Logs));
        Assert.IsTrue(test.State.IsRunning);
        Assert.IsTrue(test.Adapter.IsArmed(test.Request));
        var cleaned = test.Snapshot();
        Assert.IsTrue(test.Adapter.AbandonProjectedActivities(test.Request));
        Assert.AreEqual(cleaned, test.Snapshot());
    }

    [TestMethod]
    public async Task ThrowingActivityStartObserver_DoesNotLeaveAnUnownedActivityAfterDisposal()
    {
        await using var test = new BridgeTestCase();
        await test.BeginAsync(0);
        Action brokenObserver = () => throw new InvalidOperationException("private activity observer detail");
        test.State.OnChanged += brokenObserver;
        try
        {
            await Assert.ThrowsExactlyAsync<WorkerEventStateBridgeException>(() => test.SendAsync(
                new ActivityStarted(test.Request, Guid.NewGuid(), "must be cleaned")));
        }
        finally
        {
            test.State.OnChanged -= brokenObserver;
        }

        await test.Bridge.DisposeAsync();
        Assert.IsNull(test.State.CurrentActivity, "A failed activity-start notification must not orphan its scope.");
        Assert.IsTrue(test.State.IsRunning);
        Assert.IsTrue(test.Adapter.IsArmed(test.Request));
        Assert.IsFalse(test.Bridge.IsTerminal);
        Assert.IsInstanceOfType<WorkerEventStateBridgeObservation.ProjectionFailed>(test.Bridge.FirstObservation);
    }

    [TestMethod]
    [DataRow("retained")]
    [DataRow("temporary")]
    public void FailedActivityStart_PreservesPeerScopeAndOriginalObserverException(string failedLabel)
    {
        var state = new ImmichReverseGeo.Web.Services.ProcessingState();
        using var owned = state.BeginActivity("retained");
        var original = new InvalidOperationException("initial notification failure");
        var cleanup = new InvalidOperationException("cleanup notification failure");
        var notifications = 0;
        Action brokenObserver = () => throw (++notifications == 1 ? original : cleanup);
        state.OnChanged += brokenObserver;
        try
        {
            var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => state.BeginActivity(failedLabel));
            Assert.AreSame(original, thrown, "Cleanup must retain the failure that prevented ownership transfer.");
        }
        finally
        {
            state.OnChanged -= brokenObserver;
        }

        Assert.AreEqual("retained", state.CurrentActivity, "A failed start must preserve a peer's independent scope.");
        owned.Dispose();
        Assert.IsNull(state.CurrentActivity, "Equal labels must retain no count for the unowned failed start.");
    }

    private static async Task PrepareAsync(BridgeTestCase test)
    {
        await test.ReadyAsync();
        await test.SendAsync(new RunStarted(test.Request, BridgeTestCase.StartedAt));
    }

    private sealed class ProjectionHold : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Observe(ProcessingEvent processingEvent)
        {
            if (processingEvent is EligibilityDetermined)
            {
                Entered.TrySetResult();
                _release.Task.WaitAsync(BridgeTestCase.Bound).GetAwaiter().GetResult();
            }
        }

        internal void Release() => _release.TrySetResult();
        public void Dispose() => Release();
    }
}
