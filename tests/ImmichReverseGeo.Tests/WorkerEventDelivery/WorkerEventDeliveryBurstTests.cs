using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Tests.WorkerProcessFixture;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using AcceptedDelivery = ImmichReverseGeo.Web.WorkerEventDelivery.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.WorkerEventDelivery;

[TestClass]
[TestCategory("Change65")]
public sealed class WorkerEventDeliveryBurstTests
{
    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1)]
    [DataRow(InternalWorkerProtocolVersion.V2)]
    public async Task RealBurst_TerminalIntakeAndExitRemainLiveWhileProjectionIsBlocked(InternalWorkerProtocolVersion version)
    {
        const int count = 4_000;
        await using var fixture = new Fixture();
        var session = await fixture.LaunchAsync("progress-burst", version, count, 0);
        try
        {
            await fixture.Entered.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.IsNotNull(session.EventDeliveryIntakeClosed);
            await session.EventDeliveryIntakeClosed.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await session.PhysicalExitConfirmed.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.IsFalse(fixture.Release.Task.IsCompleted);
            Assert.IsFalse(session.Completion.IsCompleted, "physical exit does not release accepted projection finality");
            Assert.AreEqual(count, session.EventDeliveryObservation!.AcceptedSnapshots);
            Assert.AreEqual(count - 1, session.EventDeliveryObservation.ReplacedSnapshots);
            Assert.AreEqual(4L, session.EventDeliveryObservation.AcceptedLossless);
            Assert.IsNull(fixture.BridgeCase.Adapter.GetFinalizationReceipt(fixture.BridgeCase.Request));
            fixture.Release.TrySetResult();
            var completion = await fixture.Lease.CompleteAsync();
            await session.Settlement.WaitAsync(WorkerProcessFixtureLease.Watchdog);

            Assert.AreEqual(0, completion.ExitCode);
            Assert.IsNull(completion.FirstProtocolObservation);
            Assert.AreEqual(262_177L, completion.StandardErrorTail.TotalBytes, "real stderr flood drains past pipe capacity");
            Assert.AreEqual(65_536, completion.StandardErrorTail.Bytes.Length);
            Assert.IsTrue(completion.StandardErrorTail.Text.EndsWith("fixture-stderr-suffix\n", StringComparison.Ordinal));
            Assert.AreEqual(WorkerEventDeliveryFinality.Terminal, completion.EventDelivery!.Finality);
            Assert.AreEqual(1L, completion.EventDelivery.DeliveredSnapshots);
            Assert.AreEqual(4L, completion.EventDelivery.DeliveredLossless);
            Assert.AreEqual(0L, completion.EventDelivery.AbandonedItems);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, count + 3, count + 4 }, fixture.Delivered.Select(x => x.Sequence).ToArray());
            Assert.AreEqual(count, fixture.BridgeCase.State.ProcessedThisRun);
            Assert.IsFalse(fixture.BridgeCase.State.IsRunning);
            Assert.IsTrue(fixture.BridgeCase.Bridge.IsTerminal);
            fixture.Lease.AssertExactCapture();
        }
        finally
        {
            fixture.Release.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1)]
    [DataRow(InternalWorkerProtocolVersion.V2)]
    public async Task RealBurst_InterleavedLogsAndActivitiesRetainEveryBarrier(InternalWorkerProtocolVersion version)
    {
        await using var fixture = new Fixture();
        fixture.Release.TrySetResult();
        await fixture.LaunchAsync("progress-burst", version, 1_000, 100);
        var completion = await fixture.Lease.CompleteAsync();
        Assert.IsNull(completion.FirstProtocolObservation);
        Assert.AreEqual(WorkerEventDeliveryFinality.Terminal, completion.EventDelivery!.Finality);
        Assert.AreEqual(64L, completion.EventDelivery.DeliveredLossless);
        Assert.AreEqual(1, completion.EventDelivery.FifoHighWater);
        var delivered = fixture.Delivered.ToArray();
        Assert.AreEqual(10, delivered.Count(x => x.Payload is ActivityStartedPayload));
        Assert.AreEqual(10, delivered.Count(x => x.Payload is ActivityEndedPayload));
        string[] levels = ["trace", "information", "warning", "error"];
        CollectionAssert.AreEqual(Enumerable.Range(0, 10).SelectMany(_ => levels).ToArray(),
            delivered.Select(x => x.Payload).OfType<LogEmittedPayload>().Select(x => x.Level).ToArray());
        Assert.IsTrue(delivered.Zip(delivered.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
        int block = 0;
        for (int i = 0; i < delivered.Length; i++)
        {
            if (delivered[i].Payload is ActivityStartedPayload)
            {
                block++;
                Assert.AreEqual(block * 100, Assert.IsInstanceOfType<ProgressChangedPayload>(delivered[i - 1].Payload).ProcessedCount);
            }
        }

        Assert.AreEqual(1_000L, fixture.BridgeCase.State.ProcessedThisRun);
        Assert.IsNull(fixture.BridgeCase.State.CurrentActivity);
        Assert.IsTrue(fixture.BridgeCase.Bridge.IsTerminal);
        fixture.Lease.AssertExactCapture();
    }

    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1, "progress-burst-gap", 499, 0)]
    [DataRow(InternalWorkerProtocolVersion.V2, "progress-burst-gap", 499, 0)]
    [DataRow(InternalWorkerProtocolVersion.V1, "progress-burst-crash", 1_000, 42)]
    [DataRow(InternalWorkerProtocolVersion.V2, "progress-burst-crash", 1_000, 42)]
    public async Task RealBurst_GapOrCrashRetainsOriginalProtocolEvidenceWithoutTerminal(
        InternalWorkerProtocolVersion version, string scenario, int accepted, int exitCode)
    {
        await using var fixture = new Fixture();
        var session = await fixture.LaunchAsync(scenario, version, 1_000, 0);
        try
        {
            await fixture.Entered.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.IsNotNull(session.EventDeliveryIntakeClosed);
            await session.EventDeliveryIntakeClosed.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            await session.PhysicalExitConfirmed.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.AreEqual(accepted, session.EventDeliveryObservation!.AcceptedSnapshots);
            fixture.Release.TrySetResult();
            var completion = await fixture.Lease.CompleteAsync();
            Assert.AreEqual(exitCode, completion.ExitCode);
            Assert.IsNull(completion.Terminal);
            Assert.IsNull(completion.JobTerminal);
            var failure = Assert.IsInstanceOfType<ChildWorkerProtocolObservation.ProtocolFailure>(completion.FirstProtocolObservation);
            Assert.AreEqual(scenario.EndsWith("gap", StringComparison.Ordinal)
                ? WorkerProtocolFailureCode.InvalidSequence : WorkerProtocolFailureCode.InvalidLifecycle, failure.Failure.Code);
            Assert.AreEqual(WorkerEventDeliveryFinality.Nonterminal, completion.EventDelivery!.Finality);
            Assert.IsFalse(fixture.BridgeCase.Bridge.IsTerminal);
            Assert.IsNull(fixture.BridgeCase.Adapter.GetFinalizationReceipt(fixture.BridgeCase.Request));
            Assert.AreEqual(accepted, fixture.BridgeCase.State.ProcessedThisRun);
            Assert.IsFalse(fixture.Delivered.Any(x => WorkerProtocolV1.IsTerminal(x.Type)));
        }
        finally
        {
            fixture.Release.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1, false)]
    [DataRow(InternalWorkerProtocolVersion.V2, false)]
    [DataRow(InternalWorkerProtocolVersion.V1, true)]
    [DataRow(InternalWorkerProtocolVersion.V2, true)]
    public async Task RealBurst_CancellationPreservesCooperativeTerminalOrAbandonsAtExistingGrace(
        InternalWorkerProtocolVersion version, bool unresponsive)
    {
        var clock = new CancellationTestClock();
        await using var fixture = new Fixture(clock);
        fixture.Release.TrySetResult();
        var session = await fixture.LaunchAsync(unresponsive ? "progress-burst-unresponsive" : "progress-burst-cancel", version, 1_000, 100);
        await fixture.Armed.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        long timerGeneration = clock.TimerGeneration;
        var stop = session.RequestStop();
        await clock.WaitForTimerCreatedAsync(timerGeneration).WaitAsync(WorkerProcessFixtureLease.Watchdog);
        await session.WaitForCancellationDeliveryAsync().WaitAsync(WorkerProcessFixtureLease.Watchdog);
        if (unresponsive)
        {
            await fixture.CancelObserved.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.IsFalse(stop.IsCompleted);
            Assert.AreEqual(0, fixture.Lease.TreeKillCalls);
            clock.Advance(ChildWorkerCancellationPolicy.Grace);
            Assert.AreEqual(ChildProcessKillOutcome.Requested,
                await fixture.Lease.TreeKillObserved.WaitAsync(WorkerProcessFixtureLease.Watchdog));
        }

        await stop.WaitAsync(WorkerProcessFixtureLease.Watchdog);
        var completion = await fixture.Lease.CompleteAsync();
        Assert.IsNotNull(completion.EventDelivery);
        if (unresponsive)
        {
            Assert.AreEqual(1, fixture.Lease.TreeKillCalls);
            Assert.IsTrue(session.CancellationFacts!.GraceExpired);
            Assert.AreEqual(WorkerEventDeliveryFinality.Abandoned, completion.EventDelivery.Finality);
            Assert.IsNull(completion.Terminal);
            Assert.IsNull(completion.JobTerminal);
            Assert.IsFalse(fixture.BridgeCase.Bridge.IsTerminal);
        }
        else
        {
            Assert.AreEqual(0, fixture.Lease.TreeKillCalls);
            Assert.IsFalse(session.CancellationFacts!.GraceExpired);
            Assert.AreEqual(130, completion.ExitCode);
            Assert.AreEqual(WorkerEventDeliveryFinality.Terminal, completion.EventDelivery.Finality);
            Assert.IsInstanceOfType<CancelledPayload>(completion.Terminal!.Payload);
            Assert.IsTrue(fixture.BridgeCase.Bridge.IsTerminal);
            Assert.AreEqual(1, fixture.Delivered.Count(x => WorkerProtocolV1.IsTerminal(x.Type)));
        }

        Assert.AreEqual(1_000L, fixture.BridgeCase.State.ProcessedThisRun);
        Assert.IsNull(fixture.BridgeCase.State.CurrentActivity);
    }

    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1)]
    [DataRow(InternalWorkerProtocolVersion.V2)]
    public async Task RealBurst_OwnerDisposalAtFullCapacityJoinsCancelledProjectionAndBothStreams(
        InternalWorkerProtocolVersion version)
    {
        var clock = new CancellationTestClock();
        // Capacity three cannot fill during ready/start/eligibility setup.
        // Its first full wait must belong to the interleaved burst below.
        await using var fixture = new Fixture(clock, capacity: 3);
        var session = await fixture.LaunchAsync("progress-burst-unresponsive", version, 1_000, 100);
        try
        {
            await fixture.Entered.Task.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.IsNotNull(session.EventDeliveryFirstBackpressure);
            await session.EventDeliveryFirstBackpressure.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            Assert.AreEqual(3, session.EventDeliveryObservation!.FifoHighWater);
            Assert.AreEqual(7L, session.EventDeliveryObservation.AcceptedLossless,
                "ready/start/eligibility plus three queued barriers and one waiting barrier");
            Assert.IsFalse(fixture.Release.Task.IsCompleted, "eligibility projection is still held when owner cleanup starts");
            long generation = clock.TimerGeneration;
            Task disposal = session.DisposeAsync().AsTask();
            Task repeated = session.DisposeAsync().AsTask();
            Assert.AreSame(disposal, repeated, "one owner-disposal operation");
            // This clock is used only by the session: ready already settled;
            // the next created timer is the unchanged process-stop grace timer.
            await clock.WaitForTimerCreatedAsync(generation).WaitAsync(WorkerProcessFixtureLease.Watchdog);
            clock.Advance(ChildWorkerCancellationPolicy.Grace);
            await disposal.WaitAsync(WorkerProcessFixtureLease.Watchdog);
            var completion = await fixture.Lease.CompleteAsync();

            Assert.IsFalse(fixture.Release.Task.IsCompleted, "cleanup cancelled projection without releasing the test gate");
            Assert.AreEqual(WorkerEventDeliveryFinality.Abandoned, completion.EventDelivery!.Finality);
            Assert.AreEqual(6L, completion.EventDelivery.AbandonedItems,
                "in-flight eligibility, one sealed snapshot, three queued barriers and one waiting barrier");
            Assert.AreEqual(2L, completion.EventDelivery.DeliveredLossless, "already projected ready/start facts are retained");
            Assert.IsNull(completion.Terminal);
            Assert.IsNull(completion.JobTerminal);
            Assert.IsTrue(completion.ExitObserved);
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardOutputFinality);
            Assert.IsInstanceOfType<ChildWorkerStreamFinality.EndOfStream>(completion.StandardErrorFinality);
            Assert.AreEqual(1, fixture.Lease.TreeKillCalls);
            Assert.IsTrue(session.Settlement.IsCompletedSuccessfully);
        }
        finally
        {
            fixture.Release.TrySetResult();
        }
    }

    private sealed class Fixture : IWorkerProtocolEventSink, IAcceptedWorkerEventSink, IAsyncDisposable
    {
        internal Fixture(TimeProvider? clock = null, int capacity = 1)
        {
            BridgeCase = new BridgeTestCase();
            Lease = new WorkerProcessFixtureLease
            {
                Request = BridgeCase.Request,
                LauncherOptions = new ChildWorkerLauncherOptions
                {
                    TimeProvider = clock ?? TimeProvider.System,
                    EventDeliveryPolicy = new WorkerEventDeliveryPolicy { LosslessCapacity = capacity }
                }
            };
        }

        internal BridgeTestCase BridgeCase { get; }
        internal WorkerProcessFixtureLease Lease { get; }
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        internal TaskCompletionSource Armed { get; } = Signal();
        internal TaskCompletionSource CancelObserved { get; } = Signal();
        internal ConcurrentQueue<WorkerProtocolEvent> Delivered { get; } = new();

        internal Task<ChildWorkerSession> LaunchAsync(string scenario, InternalWorkerProtocolVersion version, int count, int every) =>
            Lease.LaunchAsync(scenario, this, version, true, "--progress-count", count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--barrier-every", every.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void BindDeliveryScope(WorkerEventDeliveryScope scope) => BridgeCase.Bridge.BindDeliveryScope(scope);

        public ValueTask AcceptAsync(WorkerProtocolEvent @event, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture requires the actual accepted-delivery session path.");

        public async ValueTask AcceptDeliveryAsync(AcceptedDelivery delivery, CancellationToken cancellationToken)
        {
            if (delivery.Input.Message.Sequence == 3)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            await BridgeCase.Bridge.AcceptDeliveryAsync(delivery, cancellationToken);
            Delivered.Enqueue(delivery.Input.CompatibilityEvent!);
            if (delivery.Input.CompatibilityEvent!.Payload is LogEmittedPayload log)
            {
                if (log.Message == "fixture:burst-armed")
                {
                    Armed.TrySetResult();
                }
                else if (log.Message == "fixture:burst-cancel-observed")
                {
                    CancelObserved.TrySetResult();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            try
            {
                await Lease.DisposeAsync();
            }
            finally
            {
                await BridgeCase.DisposeAsync();
            }
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
