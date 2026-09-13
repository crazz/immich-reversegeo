using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Tests.ChildWorkerCancellation;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using AcceptedDelivery = ImmichReverseGeo.Web.WorkerEventDelivery.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.WorkerEventDelivery;

[TestClass]
[TestCategory("Change65")]
public sealed class WorkerEventDeliveryQueueTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task BackpressureObservation_DoesNotReuseAnEarlierWaitAfterConsumerAdvances()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new WorkerJobContext(Guid.NewGuid(), WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Manual);
        var delivered = new List<long>();
        using var watchdog = new CancellationTokenSource(Bound);
        await using var queue = new WorkerEventDeliveryQueue(
            new WorkerEventDeliveryScope(InternalWorkerProtocolVersion.V2, context),
            WorkerJobDescriptors.ProcessAssets, new WorkerEventDeliveryPolicy { LosslessCapacity = 1 }, TimeProvider.System,
            async (delivery, token) =>
            {
                if (delivery.Input.Message.Sequence == 2)
                {
                    firstEntered.TrySetResult();
                    await firstRelease.Task.WaitAsync(token);
                }
                else if (delivery.Input.Message.Sequence == 3)
                {
                    secondEntered.TrySetResult();
                    await secondRelease.Task.WaitAsync(token);
                }

                delivered.Add(delivery.Input.Message.Sequence);
            });
        Task AddAsync(long sequence) => queue.EnqueueAsync(new(InternalWorkerProtocolVersion.V2,
            sequence == 1
                ? WorkerJobProtocolMapper.Ready(1, DateTimeOffset.UtcNow, new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]))
                : new WorkerJobOutputMessage(WorkerJobProtocolV2.DiagnosticCategory, WorkerJobProtocolV2.LogEmittedType,
                    sequence, DateTimeOffset.UtcNow, context.JobId, context.JobKind, new WorkerJobLogPayload("trace", "retained"))),
            watchdog.Token).AsTask();
        try
        {
            await AddAsync(1);
            await AddAsync(2);
            await firstEntered.Task.WaitAsync(watchdog.Token);
            await AddAsync(3);
            Task firstWait = AddAsync(4);
            await queue.WaitForBackpressureAsync(watchdog.Token);
            Assert.IsFalse(firstWait.IsCompleted);
            Assert.AreEqual(1L, queue.Observation.EnqueueWaits);

            firstRelease.TrySetResult();
            await secondEntered.Task.WaitAsync(watchdog.Token);
            await firstWait.WaitAsync(watchdog.Token);
            Task currentBackpressure = queue.WaitForBackpressureAsync(watchdog.Token);
            Assert.IsFalse(currentBackpressure.IsCompleted,
                "the earlier full-FIFO wait has ended; no producer is currently waiting");
            Task secondWait = AddAsync(5);
            await currentBackpressure.WaitAsync(watchdog.Token);
            Assert.IsFalse(secondWait.IsCompleted);
            Assert.AreEqual(2L, queue.Observation.EnqueueWaits);
            Assert.AreEqual(5L, queue.Observation.AcceptedLossless);

            secondRelease.TrySetResult();
            await secondWait.WaitAsync(watchdog.Token);
            await queue.CompleteAsync().WaitAsync(watchdog.Token);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5 }, delivered);
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackpressureObservation_EndOfDeliveryWakesObserverWithoutInventingSaturation(bool abandon)
    {
        await using var fixture = new Fixture();
        using var watchdog = new CancellationTokenSource(Bound);
        Task observation = fixture.Queue.WaitForBackpressureAsync(watchdog.Token);
        Assert.IsFalse(observation.IsCompleted);
        await (abandon ? fixture.Queue.AbandonAsync() : fixture.Queue.CompleteAsync()).WaitAsync(Bound);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => observation);
        Assert.AreEqual(0L, fixture.Queue.Observation.EnqueueWaits);
    }

    [TestMethod]
    public async Task BackpressureObservation_CancellationEndsOnlyTheObserver()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        Task observation = fixture.Queue.WaitForBackpressureAsync(cancellation.Token);
        Assert.IsFalse(observation.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => observation.WaitAsync(Bound));
        Assert.AreEqual(WorkerEventDeliveryFinality.Open, fixture.Queue.Observation.Finality);
        await fixture.StartAndBlockAsync();
        Assert.AreEqual(3L, fixture.Queue.Observation.AcceptedLossless);
    }

    [TestMethod]
    public async Task CancellationBeforeEnqueue_DoesNotAcceptInputAndDisposalSettlesEmptyQueue()
    {
        await using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var ready = WorkerJobProtocolMapper.Ready(1, DateTimeOffset.UtcNow, new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Queue.EnqueueAsync(
            new(InternalWorkerProtocolVersion.V2, ready), cancelled.Token).AsTask());
        Assert.AreEqual(0L, fixture.Queue.Observation.AcceptedLossless);
        Assert.AreEqual(0, fixture.Delivered.Count);
        await fixture.Queue.DisposeAsync().AsTask().WaitAsync(Bound);
        Assert.IsTrue(fixture.Queue.IntakeClosed.IsCompletedSuccessfully);
        Assert.IsTrue(fixture.Queue.Completion.IsCompletedSuccessfully);
        Assert.AreEqual(WorkerEventDeliveryFinality.Abandoned, fixture.Queue.Observation.Finality);
    }

    [TestMethod]
    public async Task CancellationAfterAcceptance_WakesFullCapacityWaiterAndJoinsProjection()
    {
        await using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        await fixture.StartAndBlockAsync();
        try
        {
            await fixture.AddAsync(fixture.Log(4));
            var waitingInput = fixture.Log(5);
            Assert.IsTrue(fixture.Validator.Validate(waitingInput).IsSuccess);
            long previousWaits = fixture.Queue.Observation.EnqueueWaits;
            Task waiting = fixture.Queue.EnqueueAsync(new(InternalWorkerProtocolVersion.V2, waitingInput), cancelled.Token).AsTask();
            Assert.AreEqual(previousWaits + 1, fixture.Queue.Observation.EnqueueWaits);
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiting.WaitAsync(Bound));
            await fixture.Queue.Completion.WaitAsync(Bound);
            Assert.AreEqual(WorkerEventDeliveryFinality.Abandoned, fixture.Queue.Observation.Finality);
            Assert.AreEqual(3L, fixture.Queue.Observation.AbandonedItems, "in-flight eligibility, queued log and capacity-waiting log are accounted for");
            Assert.AreEqual(2L, fixture.Queue.Observation.DeliveredLossless);
            Assert.IsTrue(fixture.Queue.IntakeClosed.IsCompletedSuccessfully);
        }
        finally
        {
            fixture.Release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task CapacityOne_BurstBarrierAndTerminalRetainSourceSequenceAndExactSuppression()
    {
        await using var fixture = new Fixture();
        await fixture.StartAndBlockAsync();
        long waitsBeforeBurst = fixture.Queue.Observation.EnqueueWaits;
        try
        {
            await fixture.AddAsync(fixture.Progress(4, 1));
            await fixture.AddAsync(fixture.Progress(5, 2));
            await fixture.AddAsync(fixture.Progress(6, 3));
            await fixture.AddAsync(fixture.Log(7));
            await fixture.AddAsync(fixture.Progress(8, 4));
            Task terminal = fixture.AddAsync(fixture.Terminal(9, 4));

            Assert.AreEqual(1, fixture.Queue.Observation.FifoHighWater);
            Assert.AreEqual(waitsBeforeBurst + 1, fixture.Queue.Observation.EnqueueWaits, "terminal reached actual capacity wait");
            Assert.AreEqual(2L, fixture.Queue.Observation.ReplacedSnapshots);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Queue.EnqueueAsync(
                new(InternalWorkerProtocolVersion.V2, fixture.Log(10)), CancellationToken.None).AsTask());
            fixture.Release.TrySetResult();
            await terminal.WaitAsync(Bound);

            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 6, 7, 8, 9 }, fixture.Delivered.Select(x => x.Sequence).ToArray());
            var snapshot = fixture.Delivered.Single(x => x.Sequence == 6);
            Assert.AreEqual(4L, snapshot.First);
            Assert.AreEqual(5L, snapshot.Last);
            Assert.IsTrue(fixture.Delivered.Where(x => x.Sequence != 6).All(x => x.First is null && x.Last is null));
            Assert.AreEqual(2L, fixture.Queue.Observation.DeliveredSnapshots);
            Assert.AreEqual(5L, fixture.Queue.Observation.DeliveredLossless);
            Assert.AreEqual(WorkerEventDeliveryFinality.Terminal, fixture.Queue.Observation.Finality);
            Assert.IsNotNull(fixture.Queue.Observation.TerminalFlushMilliseconds);
        }
        finally
        {
            fixture.Release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task QueueWait_EndsWhenCapacityIsClaimedBeforeTerminalProjection()
    {
        var clock = new CancellationTestClock();
        await using var fixture = new Fixture(clock) { BlockTerminal = true };
        await fixture.StartAndBlockAsync();
        long previousWaits = fixture.Queue.Observation.EnqueueWaits;
        try
        {
            await fixture.AddAsync(fixture.Log(4));
            Task terminal = fixture.AddAsync(fixture.Terminal(5, 0));
            Assert.AreEqual(previousWaits + 1, fixture.Queue.Observation.EnqueueWaits, "terminal is waiting for FIFO capacity");
            clock.Advance(TimeSpan.FromMilliseconds(100));
            fixture.Release.TrySetResult();
            await fixture.TerminalEntered.Task.WaitAsync(Bound);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fixture.TerminalRelease.TrySetResult();
            await terminal.WaitAsync(Bound);
            Assert.AreEqual(100L, fixture.Queue.Observation.EnqueueWaitMilliseconds, "projection time is not capacity wait time");
            Assert.AreEqual(600L, fixture.Queue.Observation.TerminalFlushMilliseconds, "terminal flush includes both capacity and projection waits");
        }
        finally
        {
            fixture.Release.TrySetResult();
            fixture.TerminalRelease.TrySetResult();
        }
    }

    [TestMethod]
    public async Task PrimarySequenceGap_CannotBecomeAReplacementSpan()
    {
        await using var fixture = new Fixture();
        await fixture.StartAndBlockAsync();
        try
        {
            await fixture.AddAsync(fixture.Progress(4, 1));
            var invalid = fixture.Validator.Validate(fixture.Progress(6, 2));
            Assert.IsFalse(invalid.IsSuccess, "raw gap must fail before queue intake");
            Assert.IsNotNull(invalid.Failure);
            Assert.AreEqual(1L, fixture.Queue.Observation.AcceptedSnapshots);
            Assert.AreEqual(0L, fixture.Queue.Observation.ReplacedSnapshots);
            await fixture.Queue.AbandonAsync().WaitAsync(Bound);
            Assert.IsFalse(fixture.Delivered.Any(x => x.Sequence >= 4));
            Assert.AreEqual(WorkerEventDeliveryFinality.Abandoned, fixture.Queue.Observation.Finality);
        }
        finally
        {
            fixture.Release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Abandon_WakesCapacityProducerCancelsProjectionAndJoinsRepeatedDisposal()
    {
        var fixture = new Fixture();
        await fixture.StartAndBlockAsync();
        long waitsBeforeBurst = fixture.Queue.Observation.EnqueueWaits;
        Task? waiting = null;
        try
        {
            await fixture.AddAsync(fixture.Log(4));
            waiting = fixture.AddAsync(fixture.Log(5));
            Assert.AreEqual(waitsBeforeBurst + 1, fixture.Queue.Observation.EnqueueWaits, "producer is awaiting a full FIFO");
            Task first = fixture.Queue.DisposeAsync().AsTask();
            Task second = fixture.Queue.DisposeAsync().AsTask();
            Assert.AreSame(first, second);
            await first.WaitAsync(Bound);
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
            Assert.AreEqual(WorkerEventDeliveryFinality.Abandoned, fixture.Queue.Observation.Finality);
            Assert.IsFalse(fixture.Delivered.Any(x => x.Sequence >= 3));
        }
        finally
        {
            fixture.Release.TrySetResult();
            await fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AcceptedTerminal_WinsOverAbandonAndStillProjectsLast()
    {
        await using var fixture = new Fixture();
        await fixture.StartAndBlockAsync();
        try
        {
            await fixture.AddAsync(fixture.Progress(4, 1));
            Task terminal = fixture.AddAsync(fixture.Terminal(5, 1));
            Task abandoned = fixture.Queue.AbandonAsync();
            fixture.Release.TrySetResult();
            await Task.WhenAll(terminal, abandoned).WaitAsync(Bound);
            Assert.AreEqual(5L, fixture.Delivered[^1].Sequence);
            Assert.AreEqual(WorkerEventDeliveryFinality.Terminal, fixture.Queue.Observation.Finality);
            Assert.AreEqual(0L, fixture.Queue.Observation.AbandonedItems);
        }
        finally
        {
            fixture.Release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task ReentrantCancellationCallback_ObservesOnePublishedAsyncDisposalTask()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reentered = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkerEventDeliveryQueue? queue = null;
        queue = new WorkerEventDeliveryQueue(
            new WorkerEventDeliveryScope(InternalWorkerProtocolVersion.V2,
                new WorkerJobContext(Guid.NewGuid(), WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Manual)),
            WorkerJobDescriptors.ProcessAssets, new WorkerEventDeliveryPolicy { LosslessCapacity = 1 }, TimeProvider.System,
            async (_, token) =>
            {
                // Register WaitAsync first so the explicit reentry callback runs
                // before cancellation can unwind and dispose its registration.
                Task pendingProjection = release.Task.WaitAsync(token);
                using var registration = token.Register(() => reentered.TrySetResult(queue!.DisposeAsync().AsTask()));
                entered.TrySetResult();
                await pendingProjection;
            });
        try
        {
            var ready = WorkerJobProtocolMapper.Ready(1, DateTimeOffset.UtcNow, new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets]));
            Task producer = queue.EnqueueAsync(new(InternalWorkerProtocolVersion.V2, ready), CancellationToken.None).AsTask();
            await entered.Task.WaitAsync(Bound);
            Task disposal = queue.DisposeAsync().AsTask();
            Assert.AreSame(disposal, await reentered.Task.WaitAsync(Bound));
            await disposal.WaitAsync(Bound);
            await Assert.ThrowsAsync<OperationCanceledException>(() => producer);
            Assert.AreEqual(WorkerEventDeliveryFinality.Abandoned, queue.Observation.Finality);
            Assert.AreEqual(1L, queue.Observation.AbandonedItems, "cancelled in-flight ready remains an unacknowledged item");
            Assert.AreEqual(0L, queue.Observation.DeliveredLossless);
        }
        finally
        {
            release.TrySetResult();
            await queue.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    [TestMethod]
    public async Task ProjectionFault_AccountsForSealedBarrierWithoutRetryingOrReplacingFailure()
    {
        var fixture = new Fixture();
        var original = new InvalidOperationException("controlled projection failure");
        fixture.ProgressFailure = original;
        try
        {
            await fixture.StartAndBlockAsync();
            await fixture.AddAsync(fixture.Progress(4, 1));
            await fixture.AddAsync(fixture.Progress(5, 2));
            await fixture.AddAsync(fixture.Log(6));
            fixture.Release.TrySetResult();
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Queue.CompleteAsync().WaitAsync(Bound));
            Assert.AreSame(original, failure);
            Assert.AreEqual(1, fixture.ProgressFailureAttempts);
            Assert.AreEqual(WorkerEventDeliveryFinality.ProjectionFailed, fixture.Queue.Observation.Finality);
            Assert.AreEqual(2L, fixture.Queue.Observation.AbandonedItems, "unacknowledged latest snapshot and its unattempted sealed log");
            Assert.AreEqual(3L, fixture.Queue.Observation.DeliveredLossless);
            Assert.AreEqual(1L, fixture.Queue.Observation.ReplacedSnapshots);
            Assert.IsFalse(fixture.Delivered.Any(x => x.Sequence >= 4));
        }
        finally
        {
            fixture.Release.TrySetResult();
            try
            {
                await fixture.DisposeAsync();
            }
            catch (InvalidOperationException failure) when (ReferenceEquals(failure, original))
            {
                // Expected stored fault; any other cleanup error remains visible.
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        private readonly WorkerJobContext _context = new(Guid.NewGuid(), WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Manual);
        private long _lastDelivered;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        internal TaskCompletionSource TerminalEntered { get; } = Signal();
        internal TaskCompletionSource TerminalRelease { get; } = Signal();
        internal bool BlockTerminal { get; init; }
        internal List<(long Sequence, long? First, long? Last)> Delivered { get; } = [];
        internal WorkerEventDeliveryQueue Queue { get; }
        internal WorkerJobOutputStreamValidator Validator { get; }
        internal Exception? ProgressFailure { get; set; }
        internal int ProgressFailureAttempts { get; private set; }

        internal Fixture(TimeProvider? time = null)
        {
            Validator = new(_context.JobId, _context.JobKind);
            Queue = new(
                new WorkerEventDeliveryScope(InternalWorkerProtocolVersion.V2, _context),
                WorkerJobDescriptors.ProcessAssets,
                new WorkerEventDeliveryPolicy { LosslessCapacity = 1 },
                time ?? TimeProvider.System,
                ProjectAsync);
        }

        internal async Task StartAndBlockAsync()
        {
            await AddAsync(WorkerJobProtocolMapper.Ready(1, Now, new WorkerJobReadyPayload([WorkerJobKind.ProcessAssets])));
            await AddAsync(WorkerJobProtocolMapper.JobStarted(_context, "manual", Now, 2));
            await AddAsync(Message(3, WorkerJobProtocolV2.LifecycleCategory, WorkerJobProtocolV2.EligibilityDeterminedType, new ProcessAssetsEligibilityPayload(100)));
            await Entered.Task.WaitAsync(Bound);
        }

        internal Task AddAsync(WorkerJobOutputMessage message)
        {
            // This fixture deliberately models the accepted-event seam. Codec/framing
            // and real pipe observation remain separate required integration tests.
            var validated = Validator.Validate(message);
            Assert.IsTrue(validated.IsSuccess, validated.Failure?.Diagnostic);
            return Queue.EnqueueAsync(new(InternalWorkerProtocolVersion.V2, message), CancellationToken.None).AsTask();
        }

        internal WorkerJobOutputMessage Progress(long sequence, long count) => Message(
            sequence, WorkerJobProtocolV2.ProgressCategory, WorkerJobProtocolV2.ProgressChangedType,
            new ProcessAssetsProgressPayload(count, count, 0, 0));

        internal WorkerJobOutputMessage Log(long sequence) => Message(
            sequence, WorkerJobProtocolV2.DiagnosticCategory, WorkerJobProtocolV2.LogEmittedType,
            new WorkerJobLogPayload("trace", "A retained diagnostic."));

        internal WorkerJobOutputMessage Terminal(long sequence, long count) => WorkerJobProtocolMapper.Terminal(
            _context,
            new WorkerJobTerminalPayload(WorkerJobTerminalOutcome.Completed, Now, Now,
                new ProcessAssetsResult("manual", Now, Now, count, count, 0, 0), null),
            sequence);

        private WorkerJobOutputMessage Message(long sequence, string category, string type, WorkerJobOutputPayload payload) =>
            new(category, type, sequence, Now, _context.JobId, _context.JobKind, payload);

        private async ValueTask ProjectAsync(AcceptedDelivery delivery, CancellationToken cancellationToken)
        {
            _ = delivery.ValidateAdvance(Queue.Scope, _lastDelivered);
            var forged = new AcceptedDelivery(Queue, delivery.Input, delivery.SuppressedStart, delivery.SuppressedEnd);
            Assert.ThrowsExactly<InvalidOperationException>(() => forged.ValidateAdvance(Queue.Scope, _lastDelivered));
            Assert.ThrowsExactly<InvalidOperationException>(() => delivery.ValidateAdvance(
                new WorkerEventDeliveryScope(Queue.Scope.Version, _context), _lastDelivered));
            if (delivery.Input.Message.Sequence == 3)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            if (delivery.Input.Message.Payload is ProcessAssetsProgressPayload && ProgressFailure is { } failure)
            {
                ProgressFailureAttempts++;
                throw failure;
            }

            if (BlockTerminal && delivery.Input.Message.Type == WorkerJobProtocolV2.TerminalType)
            {
                TerminalEntered.TrySetResult();
                await TerminalRelease.Task.WaitAsync(cancellationToken);
            }

            Delivered.Add((delivery.Input.Message.Sequence, delivery.SuppressedStart, delivery.SuppressedEnd));
            _lastDelivered = delivery.Input.Message.Sequence;
        }

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            TerminalRelease.TrySetResult();
            await Queue.DisposeAsync().AsTask().WaitAsync(Bound);
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
