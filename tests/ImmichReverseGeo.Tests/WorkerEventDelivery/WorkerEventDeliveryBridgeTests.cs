using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using AcceptedDelivery = ImmichReverseGeo.Web.WorkerEventDelivery.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.WorkerEventDelivery;

[TestClass]
[TestCategory("Change65")]
public sealed class WorkerEventDeliveryBridgeTests
{
    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1)]
    [DataRow(InternalWorkerProtocolVersion.V2)]
    public async Task ValidatedBurst_ProjectsExactSuppressionAndEquivalentFinalCounts(InternalWorkerProtocolVersion version)
    {
        await using var fixture = new Fixture(version);
        await fixture.StartAndBlockAsync();
        long waitsBeforeBurst = fixture.Queue.Observation.EnqueueWaits;
        try
        {
            await fixture.SendAsync(new ProgressChanged(fixture.Request, new ProcessingProgress(1, 1, 0, 0)), 4);
            await fixture.SendAsync(new ProgressChanged(fixture.Request, new ProcessingProgress(2, 1, 1, 0)), 5);
            await fixture.SendAsync(new ProgressChanged(fixture.Request, new ProcessingProgress(3, 1, 1, 1)), 6);
            await fixture.SendAsync(new LogEmitted(fixture.Request, ProcessingLogLevel.Information, "retained barrier"), 7);
            await fixture.SendAsync(new ProgressChanged(fixture.Request, new ProcessingProgress(4, 2, 1, 1)), 8);
            Task terminal = fixture.SendAsync(new RunFinished(fixture.Request,
                fixture.BridgeCase.Result(ProcessingRunOutcome.Completed, 2, 1, 1)), 9);
            Assert.AreEqual(waitsBeforeBurst + 1, fixture.Queue.Observation.EnqueueWaits);
            Assert.IsFalse(terminal.IsCompleted, "terminal receipt waits for the actual state projection");
            Assert.IsNull(fixture.BridgeCase.Adapter.GetFinalizationReceipt(fixture.Request));
            fixture.Release.TrySetResult();
            await terminal.WaitAsync(BridgeTestCase.Bound);

            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 6, 7, 8, 9 }, fixture.DeliveredSequences.ToArray());
            CollectionAssert.AreEqual(new long[] { 3, 4 }, fixture.Projected.OfType<ProgressChanged>().Select(x => x.Progress.ProcessedCount).ToArray());
            CollectionAssert.AreEqual(new[] { "retained barrier" }, fixture.Projected.OfType<LogEmitted>().Select(x => x.Message).ToArray());
            Assert.AreEqual(2L, fixture.BridgeCase.State.ProcessedThisRun);
            Assert.AreEqual(1L, fixture.BridgeCase.State.SkippedThisRun);
            Assert.AreEqual(1L, fixture.BridgeCase.State.ErrorsThisRun);
            Assert.IsFalse(fixture.BridgeCase.State.IsRunning);
            Assert.IsNull(fixture.BridgeCase.State.CurrentActivity);
            Assert.IsTrue(fixture.BridgeCase.Bridge.IsTerminal);
            Assert.IsNull(fixture.BridgeCase.Bridge.FirstObservation);
            Assert.IsNotNull(fixture.BridgeCase.Adapter.GetFinalizationReceipt(fixture.Request));
            Assert.IsInstanceOfType<RunFinished>(fixture.Projected[^1]);
            Assert.AreEqual(WorkerEventDeliveryFinality.Terminal, fixture.Queue.Observation.Finality);
        }
        finally
        {
            fixture.Release.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(InternalWorkerProtocolVersion.V1)]
    [DataRow(InternalWorkerProtocolVersion.V2)]
    public async Task AllLogLevelsAndNestedActivities_RemainLosslessAndOrdered(InternalWorkerProtocolVersion version)
    {
        await using var fixture = new Fixture(version);
        fixture.Release.TrySetResult();
        await fixture.StartAndBlockAsync();
        Guid outer = Guid.NewGuid();
        Guid inner = Guid.NewGuid();
        ProcessingEvent[] events =
        [
            new ActivityStarted(fixture.Request, outer, "outer"),
            new LogEmitted(fixture.Request, ProcessingLogLevel.Trace, "trace retained"),
            new ActivityStarted(fixture.Request, inner, "inner"),
            new LogEmitted(fixture.Request, ProcessingLogLevel.Information, "information retained"),
            new LogEmitted(fixture.Request, ProcessingLogLevel.Warning, "warning retained"),
            new LogEmitted(fixture.Request, ProcessingLogLevel.Error, "error retained"),
            new ActivityEnded(fixture.Request, inner),
            new ActivityEnded(fixture.Request, outer),
            new RunFinished(fixture.Request, fixture.BridgeCase.Result(ProcessingRunOutcome.Completed))
        ];
        for (int i = 0; i < events.Length; i++)
        {
            await fixture.SendAsync(events[i], i + 4);
        }

        CollectionAssert.AreEqual(Enumerable.Range(1, 12).Select(x => (long)x).ToArray(), fixture.DeliveredSequences.ToArray());
        CollectionAssert.AreEqual(events.Select(x => x.GetType()).ToArray(), fixture.Projected.Skip(2).Select(x => x.GetType()).ToArray());
        CollectionAssert.AreEqual(events.OfType<LogEmitted>().Select(x => x.Level).ToArray(), fixture.Projected.OfType<LogEmitted>().Select(x => x.Level).ToArray());
        CollectionAssert.AreEqual(events.OfType<LogEmitted>().Select(x => x.Message).ToArray(), fixture.Projected.OfType<LogEmitted>().Select(x => x.Message).ToArray());
        Assert.AreEqual(0L, fixture.Queue.Observation.ReplacedSnapshots);
        Assert.AreEqual(12L, fixture.Queue.Observation.DeliveredLossless);
        Assert.IsNull(fixture.BridgeCase.State.CurrentActivity);
        Assert.IsTrue(fixture.BridgeCase.Logs.Any(x => x.Contains("trace retained", StringComparison.Ordinal)));
        Assert.IsTrue(fixture.BridgeCase.Logs.Any(x => x.Contains("error retained", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BoundBridge_RejectsDirectOrForgedDeliveryWithoutMutation()
    {
        await using var fixture = new Fixture(InternalWorkerProtocolVersion.V1);
        var ready = WorkerProtocolMapper.Ready(1, BridgeTestCase.ReadyAt);
        var input = new WorkerEventDeliveryInput(InternalWorkerProtocolVersion.V1,
            ProcessAssetsWorkerJobProjection.MapV1(fixture.Request, ready), ready);
        var forged = new AcceptedDelivery(fixture.Queue, input, null, null);
        var before = fixture.BridgeCase.Snapshot();
        await Assert.ThrowsExactlyAsync<WorkerEventStateBridgeException>(() =>
            fixture.BridgeCase.Bridge.AcceptDeliveryAsync(forged, CancellationToken.None).AsTask());
        Assert.AreEqual(before, fixture.BridgeCase.Snapshot());
        await fixture.BridgeCase.RejectWithoutMutationAsync(ready);
    }

    [TestMethod]
    public async Task DisposedBridge_IsCountedAsStaleWithoutClaimingProjectionOrTerminal()
    {
        var fixture = new Fixture(InternalWorkerProtocolVersion.V2);
        try
        {
            await fixture.StartAndBlockAsync();
            await fixture.BridgeCase.Bridge.DisposeAsync();
            var before = fixture.BridgeCase.Snapshot();
            fixture.Release.TrySetResult();
            await Assert.ThrowsExactlyAsync<StaleWorkerEventDeliveryException>(() =>
                fixture.Queue.CompleteAsync().WaitAsync(BridgeTestCase.Bound));
            Assert.AreEqual(before, fixture.BridgeCase.Snapshot());
            Assert.AreEqual(1L, fixture.Queue.Observation.StaleRejected);
            Assert.AreEqual(1L, fixture.Queue.Observation.AbandonedItems);
            Assert.AreEqual(2L, fixture.Queue.Observation.DeliveredLossless);
            Assert.AreEqual(WorkerEventDeliveryFinality.ProjectionFailed, fixture.Queue.Observation.Finality);
            Assert.IsFalse(fixture.BridgeCase.Bridge.IsTerminal);
        }
        finally
        {
            fixture.Release.TrySetResult();
            try
            {
                await fixture.DisposeAsync();
            }
            catch (StaleWorkerEventDeliveryException)
            {
                // The expected stale rejection remains the queue's stored final fault.
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly InternalWorkerProtocolVersion _version;
        private readonly WorkerProtocolEventStreamValidator _v1 = new();
        private readonly WorkerJobOutputStreamValidator _v2;
        private readonly ProcessAssetsWorkerJobProjection _compatibility;

        internal Fixture(InternalWorkerProtocolVersion version)
        {
            _version = version;
            BridgeCase = new BridgeTestCase(beforeProjection: Projected.Add);
            _v2 = new WorkerJobOutputStreamValidator(Request.RunId, WorkerJobKind.ProcessAssets);
            _compatibility = new ProcessAssetsWorkerJobProjection(Request);
            var scope = new WorkerEventDeliveryScope(version,
                new WorkerJobContext(Request.RunId, WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Manual));
            BridgeCase.Bridge.BindDeliveryScope(scope);
            Queue = new WorkerEventDeliveryQueue(scope, WorkerJobDescriptors.ProcessAssets,
                new WorkerEventDeliveryPolicy { LosslessCapacity = 1 }, TimeProvider.System, ProjectAsync);
        }

        internal BridgeTestCase BridgeCase { get; }
        internal ProcessingRunRequest Request => BridgeCase.Request;
        internal WorkerEventDeliveryQueue Queue { get; }
        internal List<ProcessingEvent> Projected { get; } = [];
        internal List<long> DeliveredSequences { get; } = [];
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();

        internal async Task StartAndBlockAsync()
        {
            await SendFrameAsync(WorkerProtocolMapper.Ready(1, BridgeTestCase.ReadyAt));
            await SendAsync(new RunStarted(Request, BridgeTestCase.StartedAt), 2);
            await SendAsync(new EligibilityDetermined(Request, 100), 3);
            await Entered.Task.WaitAsync(BridgeTestCase.Bound);
        }

        internal Task SendAsync(ProcessingEvent processingEvent, long sequence) =>
            SendFrameAsync(BridgeCase.Frame(processingEvent, sequence));

        private Task SendFrameAsync(WorkerProtocolEvent source)
        {
            WorkerJobOutputMessage message;
            WorkerProtocolEvent compatibility;
            if (_version == InternalWorkerProtocolVersion.V1)
            {
                var parsed = WorkerProtocolCodec.Parse(WorkerProtocolCodec.Serialize(source));
                Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
                var accepted = _v1.Validate(parsed.Event!);
                Assert.IsTrue(accepted.IsSuccess, accepted.Failure?.Diagnostic);
                compatibility = accepted.Event!;
                message = ProcessAssetsWorkerJobProjection.MapV1(Request, compatibility);
            }
            else
            {
                var mapped = ProcessAssetsWorkerJobProjection.MapV1(Request, source);
                var parsed = WorkerJobProtocolCodec.Parse(WorkerJobProtocolCodec.Serialize(mapped));
                Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
                var accepted = _v2.Validate(parsed.Message!);
                Assert.IsTrue(accepted.IsSuccess, accepted.Failure?.Diagnostic);
                message = accepted.Message!;
                compatibility = _compatibility.Map(message);
            }

            return Queue.EnqueueAsync(new(_version, message, compatibility), CancellationToken.None).AsTask();
        }

        private async ValueTask ProjectAsync(AcceptedDelivery delivery, CancellationToken cancellationToken)
        {
            if (delivery.Input.Message.Sequence == 3)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            await BridgeCase.Bridge.AcceptDeliveryAsync(delivery, cancellationToken);
            DeliveredSequences.Add(delivery.Input.Message.Sequence);
        }

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            try
            {
                await Queue.DisposeAsync().AsTask().WaitAsync(BridgeTestCase.Bound);
            }
            finally
            {
                await BridgeCase.DisposeAsync();
            }
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
