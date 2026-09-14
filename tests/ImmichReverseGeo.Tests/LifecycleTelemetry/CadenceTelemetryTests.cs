using System;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.LifecycleTelemetry;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using ImmichReverseGeo.Web.WorkerFailureRecovery;
using StateBridge = ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridge;

namespace ImmichReverseGeo.Tests.LifecycleTelemetry;

[TestClass]
[TestCategory("Change66")]
public sealed class CadenceTelemetryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [TestMethod]
    public async Task TwoOwners_RetainIndependentFrozenCountsWhileLifetimeCountContinues()
    {
        var clock = new CancellationTestClock();
        var delivered = Channel.CreateUnbounded<long>();
        await using var cadence = new ReadModelNotificationCadence(clock, Interval, (_, revision) =>
        {
            delivered.Writer.TryWrite(revision);
            return ValueTask.CompletedTask;
        });
        var first = new object();
        cadence.Bind(first);
        var firstObservation = cadence.CaptureOwner(first)!;
        for (int revision = 1; revision <= 2; revision++)
        {
            cadence.Signal(first, revision);
            clock.Advance(Interval);
            Assert.AreEqual(revision, await delivered.Reader.ReadAsync().AsTask().WaitAsync(Bound));
        }

        using var logs = new RecordingLifecycleLogs();
        var firstTelemetry = new WorkerJobTelemetry(logs.CreateLogger(LifecycleEventCatalog.Category), clock,
            new WorkerJobContext(Guid.NewGuid(), WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Manual));
        firstTelemetry.ProcessOwned(42, clock.GetTimestamp());
        firstTelemetry.BindNotificationObservation(firstObservation);
        var second = new object();
        cadence.Bind(second); // Replacement freezes the old handle before another job dispatches.
        var secondObservation = cadence.CaptureOwner(second)!;
        cadence.Signal(second, 1);
        clock.Advance(Interval);
        await delivered.Reader.ReadAsync().AsTask().WaitAsync(Bound);
        cadence.Signal(second, 2, final: true);
        await delivered.Reader.ReadAsync().AsTask().WaitAsync(Bound);
        cadence.CompleteOwner(secondObservation);
        Assert.AreEqual(2L, await firstObservation.FinalOrdinaryDispatched.WaitAsync(Bound));
        Assert.AreEqual(1L, await secondObservation.FinalOrdinaryDispatched.WaitAsync(Bound));
        Assert.AreEqual(3L, cadence.Observation.OrdinaryDispatched);
        Assert.AreEqual(1L, cadence.Observation.FinalDispatched);
        Assert.IsNull(cadence.CaptureOwner(first));

        firstTelemetry.Finalized(WorkerLogClassification.InfrastructureFailed, 5, false,
            ChildWorkingSetSummary.NoSample, Saturated());
        await firstTelemetry.CoalescingObservation.WaitAsync(Bound);
        firstTelemetry.Finalized(WorkerLogClassification.InfrastructureFailed, 5, false,
            ChildWorkingSetSummary.NoSample, Saturated());
        var saturation = logs.Entries.Single(entry => entry.Event.Id == 6650);
        Assert.AreEqual(2L, saturation["cadence_notification_count"], "Late finality must not read the new owner's1 or lifetime3.");
        Assert.AreEqual(2L, firstObservation.OrdinaryDispatched);
    }

    [TestMethod]
    public async Task Saturation_WaitsForOwnerFreezeWithoutBlockingClassificationOrCountingFinalNotification()
    {
        var clock = new CancellationTestClock();
        var finalDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cadence = new ReadModelNotificationCadence(clock, Interval, (_, _) =>
        {
            finalDispatched.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var owner = new object();
        cadence.Bind(owner);
        var observation = cadence.CaptureOwner(owner)!;
        using var logs = new RecordingLifecycleLogs();
        var telemetry = new WorkerJobTelemetry(logs.CreateLogger(LifecycleEventCatalog.Category), clock,
            new WorkerJobContext(Guid.NewGuid(), WorkerJobKind.CoordinateLookup, WorkerJobRequestOrigin.Manual));
        telemetry.ProcessOwned(42, clock.GetTimestamp());
        telemetry.BindNotificationObservation(observation);
        telemetry.Finalized(WorkerLogClassification.InfrastructureFailed, 5, false,
            ChildWorkingSetSummary.NoSample, Saturated() with { Finality = WorkerEventDeliveryFinality.Nonterminal });
        Assert.AreEqual(1, logs.Entries.Count(entry => entry.Event.Id == 6641));
        Assert.AreEqual(0, logs.Entries.Count(entry => entry.Event.Id == 6650));
        Assert.IsFalse(telemetry.CoalescingObservation.IsCompleted);
        cadence.Signal(owner, 1, final: true);
        await finalDispatched.Task.WaitAsync(Bound);
        cadence.CompleteOwner(observation);
        await telemetry.CoalescingObservation.WaitAsync(Bound);
        var saturation = logs.Entries.Single(entry => entry.Event.Id == 6650);
        Assert.AreEqual(0L, saturation["cadence_notification_count"]);
        Assert.IsNull(saturation["terminal_flush_duration_ms"]);
        Assert.AreEqual("nonterminal", saturation["finality_kind"]);
    }

    [TestMethod]
    public async Task RealProcessingReporter_FreezesItsCapturedBridgeObservationBeforeRearming()
    {
        var clock = new CancellationTestClock();
        await using var state = new ProcessingState(clock, new WorkerEventDeliveryPolicy());
        var reporter = new ProcessingStateEventReporter(state);
        var notifications = Channel.CreateUnbounded<bool>();
        state.OnChanged += () => notifications.Writer.TryWrite(true);
        var firstRequest = SessionTestSupport.CreateRequest();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(firstRequest));
        await using var firstBridge = new StateBridge(firstRequest, reporter);
        clock.Advance(Interval);
        await notifications.Reader.ReadAsync().AsTask().WaitAsync(Bound);
        var firstObservation = firstBridge.NotificationOwnerObservation!;
        new WorkerRunFinalizer(firstRequest, reporter, clock).FinalizeNoProcess(WorkerRunFailureCategory.ProcessStart);
        Assert.AreEqual(1L, await firstObservation.FinalOrdinaryDispatched.WaitAsync(Bound));
        while (notifications.Reader.TryRead(out _)) { }

        var secondRequest = SessionTestSupport.CreateRequest();
        state.MarkPending();
        Assert.IsTrue(reporter.Arm(secondRequest));
        await using var secondBridge = new StateBridge(secondRequest, reporter);
        Assert.AreNotSame(firstObservation, secondBridge.NotificationOwnerObservation);
        state.AppendLog("ordinary state mutation");
        clock.Advance(Interval);
        // The cadence counter is incremented before the ordinary callback begins.
        while (secondBridge.NotificationOwnerObservation!.OrdinaryDispatched == 0)
        {
            await notifications.Reader.ReadAsync().AsTask().WaitAsync(Bound);
        }

        new WorkerRunFinalizer(secondRequest, reporter, clock).FinalizeNoProcess(WorkerRunFailureCategory.ProcessStart);
        Assert.AreEqual(1L, await secondBridge.NotificationOwnerObservation!.FinalOrdinaryDispatched.WaitAsync(Bound));
        Assert.AreEqual(1L, await firstObservation.FinalOrdinaryDispatched);
        Assert.AreEqual(2L, state.NotificationObservation!.OrdinaryDispatched);
    }

    private static WorkerEventDeliveryObservation Saturated() => new(8, 7, 3, 5, 7, 2,
        1, 11, 13, 17, 0, 0, WorkerEventDeliveryFinality.Terminal);
}
