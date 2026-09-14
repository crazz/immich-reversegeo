using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests.WorkerNdjsonOutput;

[TestClass]
[TestCategory("Change23")]
public sealed class WorkerNdjsonEmitterTests
{
    private static readonly DateTimeOffset Timestamp = new DateTimeOffset(2026, 8, 30, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567);

    [TestMethod]
    public async Task PublishAsync_EmitsTheCanonicalReadyFrameOnlyAfterFlush()
    {
        var stream = new RecordingStream(blockFlush: true);
        var emitter = Create(stream);

        var publish = emitter.PublishAsync(CancellationToken.None);
        await stream.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(publish.IsCompleted, "ready-completes-after-flush");
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(ExpectedReadyJson() + "\n"), stream.Bytes.ToArray(), "ready-exact-bytes-before-flush");
        CollectionAssert.AreEqual(new[] { "write", "flush" }, stream.Calls, "ready-write-flush-order");

        stream.ReleaseFlush();
        await publish.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, stream.WriteCount, "ready-one-write");
        Assert.AreEqual(1, stream.FlushCount, "ready-one-flush");
        AssertReady(stream.Bytes.ToArray());
    }

    [TestMethod]
    public async Task SubmitAsync_ReceiptCompletesOnlyAfterWriteAndFlush()
    {
        var stream = new RecordingStream(blockFlush: true);
        var emitter = Create(stream);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        var ready = emitter.PublishAsync(CancellationToken.None);
        await stream.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stream.ReleaseFlush();
        await ready.WaitAsync(TimeSpan.FromSeconds(5));
        stream.BlockNextFlush();

        var receipt = emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None).AsTask();
        await stream.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(receipt.IsCompleted, "run-receipt-remains-pending-until-flush");
        Assert.AreEqual(2, stream.WriteCount, "run-receipt-write-has-completed");
        Assert.AreEqual(2, stream.FlushCount, "run-receipt-flush-is-gated");

        stream.ReleaseFlush();
        await receipt.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task PublishAsync_ConcurrentAndRepeatedCallsEmitOneReadyFrame()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var publishes = Enumerable.Range(0, 16).Select(_ => emitter.PublishAsync(CancellationToken.None)).ToArray();

        await Task.WhenAll(publishes).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, stream.WriteCount, "ready-concurrent-one-write");
        Assert.AreEqual(1, stream.FlushCount, "ready-concurrent-one-flush");
        AssertReady(stream.Bytes.ToArray());
    }

    [TestMethod]
    public async Task PublishAsync_PreAcceptanceCancellationDoesNotCacheTheCancelledAttempt()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await emitter.PublishAsync(cancelled.Token), "ready-pre-acceptance-cancelled");
        Assert.AreEqual(0, stream.WriteCount, "ready-pre-acceptance-no-write");
        Assert.AreEqual(0, stream.FlushCount, "ready-pre-acceptance-no-flush");

        await emitter.PublishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, stream.WriteCount, "ready-retry-after-cancel-one-write");
        Assert.AreEqual(1, stream.FlushCount, "ready-retry-after-cancel-one-flush");
        AssertReady(stream.Bytes.ToArray());
    }

    [TestMethod]
    public async Task PublishAsync_TransportFailureIsPermanentAndSafe()
    {
        foreach (var kind in new[] { FailureKind.Mapping, FailureKind.Write, FailureKind.Flush })
        {
            await AssertPermanentFailureAsync(kind);
        }
    }

    [TestMethod]
    public async Task Reporter_RejectsRunEventsUntilReadyHasFlushed()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var reporter = new WorkerNdjsonProcessingEventReporter(emitter);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await reporter.OpenRunAsync(request, Timestamp, CancellationToken.None), "reporter-ready-required");
        Assert.AreEqual(0, stream.WriteCount, "reporter-ready-required-no-write");
        Assert.AreEqual(0, stream.FlushCount, "reporter-ready-required-no-flush");

        await emitter.PublishAsync(CancellationToken.None);
        var session = await reporter.OpenRunAsync(request, Timestamp, CancellationToken.None);
        await session.FinishAsync(Result(request, Timestamp, Timestamp.AddSeconds(1), 0, 0, 0, 0, ProcessingRunOutcome.Cancelled));
        Assert.AreEqual(3, stream.WriteCount, "reporter-ready-required-recovered-write-count");
    }

    [TestMethod]
    public async Task Reporter_MapsEverySessionEventWithExactTypedProtocolValues()
    {
        foreach (var trigger in new[] { ProcessingRunTrigger.Manual, ProcessingRunTrigger.Scheduled, ProcessingRunTrigger.RunOnce })
        {
            var stream = new RecordingStream();
            var emitter = Create(stream);
            var reporter = new WorkerNdjsonProcessingEventReporter(emitter);
            var request = new ProcessingRunRequest(Guid.NewGuid(), trigger);
            await emitter.PublishAsync(CancellationToken.None);
            var session = await reporter.OpenRunAsync(request, Timestamp, CancellationToken.None);
            await session.DetermineEligibilityAsync(3);
            var activity = await session.BeginActivityAsync("resolve");
            foreach (var level in new[] { ProcessingLogLevel.Trace, ProcessingLogLevel.Information, ProcessingLogLevel.Warning, ProcessingLogLevel.Error })
            {
                await session.ReportLogAsync(level, "message-" + level);
            }

            await session.ReportUpdatedAsync();
            await session.ReportSkippedAsync();
            await session.ReportFailedAsync();
            await activity.DisposeAsync();
            await session.FinishAsync(Result(request, Timestamp, Timestamp.AddSeconds(1), 3, 1, 1, 1, ProcessingRunOutcome.Completed));

            var events = ParseAndValidate(stream.Bytes.ToArray());
            Assert.AreEqual(13, events.Length, "reporter-full-event-count");
            Assert.AreEqual(13, stream.WriteCount, "reporter-full-write-count");
            Assert.AreEqual(13, stream.FlushCount, "reporter-full-flush-count");
            for (var index = 0; index < events.Length; index++)
            {
                Assert.AreEqual(index + 1L, events[index].Sequence, "reporter-full-sequence");
            }

            AssertEvent(events[1], WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.RunStartedType, 2, Timestamp, request.RunId);
            var started = (RunStartedPayload)events[1].Payload;
            Assert.AreEqual(trigger switch { ProcessingRunTrigger.Manual => "manual", ProcessingRunTrigger.Scheduled => "scheduled", _ => "run-once" }, started.Trigger, "reporter-trigger");
            Assert.AreEqual(Timestamp, started.StartedAtUtc, "reporter-run-started-payload-timestamp");
            AssertEvent(events[2], WorkerProtocolV1.LifecycleCategory, WorkerProtocolV1.EligibilityDeterminedType, 3, Timestamp, request.RunId);
            Assert.AreEqual(3L, ((EligibilityDeterminedPayload)events[2].Payload).EligibleCount, "reporter-eligibility");
            AssertEvent(events[3], WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityStartedType, 4, Timestamp, request.RunId);
            var activityStarted = (ActivityStartedPayload)events[3].Payload;
            Assert.AreEqual("resolve", activityStarted.Label, "reporter-activity-label");
            for (var index = 0; index < 4; index++)
            {
                var log = (LogEmittedPayload)events[index + 4].Payload;
                AssertEvent(events[index + 4], WorkerProtocolV1.DiagnosticCategory, WorkerProtocolV1.LogEmittedType, index + 5, Timestamp, request.RunId);
                Assert.AreEqual(new[] { "trace", "information", "warning", "error" }[index], log.Level, "reporter-log-level");
                Assert.AreEqual("message-" + new[] { ProcessingLogLevel.Trace, ProcessingLogLevel.Information, ProcessingLogLevel.Warning, ProcessingLogLevel.Error }[index], log.Message, "reporter-log-message");
            }

            AssertEvent(events[8], WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, 9, Timestamp, request.RunId);
            var updatedProgress = (ProgressChangedPayload)events[8].Payload;
            Assert.AreEqual(1L, updatedProgress.ProcessedCount, "reporter-updated-processed-progress");
            Assert.AreEqual(1L, updatedProgress.UpdatedCount, "reporter-updated-progress");
            Assert.AreEqual(0L, updatedProgress.SkippedCount, "reporter-updated-skipped-progress");
            Assert.AreEqual(0L, updatedProgress.FailedCount, "reporter-updated-failed-progress");
            AssertEvent(events[9], WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, 10, Timestamp, request.RunId);
            var skippedProgress = (ProgressChangedPayload)events[9].Payload;
            Assert.AreEqual(2L, skippedProgress.ProcessedCount, "reporter-skipped-processed-progress");
            Assert.AreEqual(1L, skippedProgress.UpdatedCount, "reporter-skipped-updated-progress");
            Assert.AreEqual(1L, skippedProgress.SkippedCount, "reporter-skipped-progress");
            Assert.AreEqual(0L, skippedProgress.FailedCount, "reporter-skipped-failed-progress");
            AssertEvent(events[10], WorkerProtocolV1.ProgressCategory, WorkerProtocolV1.ProgressChangedType, 11, Timestamp, request.RunId);
            var failedProgress = (ProgressChangedPayload)events[10].Payload;
            Assert.AreEqual(3L, failedProgress.ProcessedCount, "reporter-failed-processed-progress");
            Assert.AreEqual(1L, failedProgress.UpdatedCount, "reporter-failed-updated-progress");
            Assert.AreEqual(1L, failedProgress.SkippedCount, "reporter-failed-skipped-progress");
            Assert.AreEqual(1L, failedProgress.FailedCount, "reporter-failed-progress");
            AssertEvent(events[11], WorkerProtocolV1.ActivityCategory, WorkerProtocolV1.ActivityEndedType, 12, Timestamp, request.RunId);
            Assert.AreEqual(activityStarted.ActivityId, ((ActivityEndedPayload)events[11].Payload).ActivityId, "reporter-activity-end-id");
            AssertEvent(events[12], WorkerProtocolV1.TerminalCategory, WorkerProtocolV1.CompletedType, 13, Timestamp.AddSeconds(1), request.RunId);
            var terminal = (CompletedPayload)events[12].Payload;
            var expectedTerminalTrigger = trigger switch
            {
                ProcessingRunTrigger.Manual => "manual",
                ProcessingRunTrigger.Scheduled => "scheduled",
                _ => "run-once"
            };
            Assert.AreEqual(expectedTerminalTrigger, terminal.Trigger, "reporter-terminal-trigger");
            Assert.IsNull(terminal.FailureMessage, "reporter-terminal-null-failure");
            Assert.AreEqual(Timestamp, terminal.StartedAtUtc, "reporter-terminal-started");
            Assert.AreEqual(Timestamp.AddSeconds(1), terminal.EndedAtUtc, "reporter-terminal-ended");
            Assert.AreEqual(3L, terminal.ProcessedCount, "reporter-terminal-processed");
            Assert.AreEqual(1L, terminal.UpdatedCount, "reporter-terminal-updated");
            Assert.AreEqual(1L, terminal.SkippedCount, "reporter-terminal-skipped");
            Assert.AreEqual(1L, terminal.FailedCount, "reporter-terminal-failed");
        }
    }

    [TestMethod]
    public async Task Reporter_ConcurrentMappedActivityLogAndProgressEventsRemainContiguous()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var reporter = new WorkerNdjsonProcessingEventReporter(emitter);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        var session = await reporter.OpenRunAsync(request, Timestamp, CancellationToken.None);
        await session.DetermineEligibilityAsync(1);
        var activity = await session.BeginActivityAsync("resolve");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = Task.Run(async () =>
        {
            await barrier.Task;
            await session.ReportLogAsync(ProcessingLogLevel.Information, "concurrent-log");
        });
        var progress = Task.Run(async () =>
        {
            await barrier.Task;
            await session.ReportUpdatedAsync();
        });
        var end = Task.Run(async () =>
        {
            await barrier.Task;
            await activity.DisposeAsync();
        });

        barrier.TrySetResult();
        await Task.WhenAll(log, progress, end).WaitAsync(TimeSpan.FromSeconds(5));
        await session.FinishAsync(Result(request, Timestamp, Timestamp.AddSeconds(1), 1, 1, 0, 0, ProcessingRunOutcome.Completed));

        var events = ParseAndValidate(stream.Bytes.ToArray());
        Assert.AreEqual(8, events.Length, "concurrent-mapping-activity-frame-count");
        Assert.AreEqual(WorkerProtocolV1.CompletedType, events[^1].Type, "concurrent-mapping-activity-terminal-last");
        Assert.AreEqual(1, events.Count(@event => @event.Type == WorkerProtocolV1.ActivityEndedType), "concurrent-mapping-activity-one-end");
        for (var index = 0; index < events.Length; index++)
        {
            Assert.AreEqual(index + 1L, events[index].Sequence, "concurrent-mapping-activity-exact-next-sequence");
            if (index > 0)
            {
                Assert.AreEqual(request.RunId, events[index].RunId, "concurrent-mapping-activity-stable-correlation");
            }
        }
    }

    [TestMethod]
    public async Task Reporter_FinishAndActivityDisposalRaceEmitsOneEndBeforeTerminal()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var reporter = new WorkerNdjsonProcessingEventReporter(emitter);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        var session = await reporter.OpenRunAsync(request, Timestamp, CancellationToken.None);
        await session.DetermineEligibilityAsync(0);
        var activity = await session.BeginActivityAsync("resolve");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = Task.Run(async () =>
        {
            await barrier.Task;
            await session.FinishAsync(Result(request, Timestamp, Timestamp.AddSeconds(1), 0, 0, 0, 0, ProcessingRunOutcome.Completed));
        });
        var dispose = Task.Run(async () =>
        {
            await barrier.Task;
            await activity.DisposeAsync();
        });

        barrier.TrySetResult();
        await Task.WhenAll(finish, dispose).WaitAsync(TimeSpan.FromSeconds(5));

        var events = ParseAndValidate(stream.Bytes.ToArray());
        Assert.AreEqual(6, events.Length, "finish-activity-race-frame-count");
        Assert.AreEqual(1, events.Count(@event => @event.Type == WorkerProtocolV1.ActivityEndedType), "finish-activity-race-one-end");
        Assert.AreEqual(WorkerProtocolV1.ActivityEndedType, events[^2].Type, "finish-activity-race-end-before-terminal");
        Assert.AreEqual(WorkerProtocolV1.CompletedType, events[^1].Type, "finish-activity-race-terminal-last");
    }

    [TestMethod]
    public async Task Reporter_MapsPermittedPreCountCancelledAndFailedTerminals()
    {
        foreach (var outcome in new[] { ProcessingRunOutcome.Cancelled, ProcessingRunOutcome.Failed })
        {
            var stream = new RecordingStream();
            var emitter = Create(stream);
            var reporter = new WorkerNdjsonProcessingEventReporter(emitter);
            var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
            await emitter.PublishAsync(CancellationToken.None);
            var session = await reporter.OpenRunAsync(request, Timestamp, CancellationToken.None);
            await session.FinishAsync(Result(request, Timestamp, Timestamp.AddSeconds(1), 0, 0, 0, 0, outcome));

            var events = ParseAndValidate(stream.Bytes.ToArray());
            Assert.AreEqual(3, events.Length, "reporter-precount-event-count");
            Assert.AreEqual(3, stream.WriteCount, "reporter-precount-write-count");
            Assert.AreEqual(3, stream.FlushCount, "reporter-precount-flush-count");
            AssertEvent(events[2], WorkerProtocolV1.TerminalCategory, outcome == ProcessingRunOutcome.Cancelled ? WorkerProtocolV1.CancelledType : WorkerProtocolV1.FailedType, 3, Timestamp.AddSeconds(1), request.RunId);
            var terminal = (TerminalPayload)events[2].Payload;
            Assert.AreEqual(0L, terminal.ProcessedCount, "reporter-precount-terminal-processed");
            Assert.AreEqual(outcome == ProcessingRunOutcome.Failed ? "failed" : null, terminal.FailureMessage, "reporter-precount-failure-detail");
        }
    }

    [TestMethod]
    public async Task SubmitAsync_CapacityOneHonorsPreAcceptanceCancellationWithoutSequenceConsumption()
    {
        var stream = new BlockingWriteStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        stream.BlockWrites();

        var started = emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None).AsTask();
        await stream.BlockedWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var eligibility = emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None).AsTask();
        using var cancellation = new CancellationTokenSource();
        var waiting = emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "cancelled-before-acceptance"), cancellation.Token).AsTask();
        Assert.IsFalse(waiting.IsCompleted, "waiting-producer-is-backpressured");

        cancellation.Cancel();
        var cancellationFailure = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await waiting,
            "waiting-producer-cancelled");
        Assert.AreEqual("The operation was canceled.", cancellationFailure.Message, "waiting-producer-cancellation-message");
        Assert.IsFalse(Encoding.UTF8.GetString(stream.Bytes.ToArray()).Contains("cancelled-before-acceptance", StringComparison.Ordinal), "cancelled-candidate-no-raw-payload");
        stream.ReleaseWrite();
        await Task.WhenAll(started, eligibility).WaitAsync(TimeSpan.FromSeconds(5));
        await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "accepted-after-cancellation"), CancellationToken.None);

        var events = ParseAndValidate(stream.Bytes.ToArray());
        CollectionAssert.AreEqual(
            new[] { WorkerProtocolV1.ReadyType, WorkerProtocolV1.RunStartedType, WorkerProtocolV1.EligibilityDeterminedType, WorkerProtocolV1.LogEmittedType },
            events.Select(@event => @event.Type).ToArray(),
            "cancelled-candidate-preserves-accepted-order");
        Assert.AreEqual("accepted-after-cancellation", ((LogEmittedPayload)events[^1].Payload).Message, "cancelled-candidate-next-accepted-message");
        Assert.AreEqual(4, events.Length, "cancelled-candidate-no-frame");
        for (var index = 0; index < events.Length; index++)
        {
            Assert.AreEqual(index + 1L, events[index].Sequence, "cancelled-candidate-no-sequence-gap");
        }
    }

    [TestMethod]
    public async Task SubmitAsync_PostAcceptanceCancellationDoesNotRetractCommittedCandidate()
    {
        var stream = new BlockingWriteStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        stream.BlockWrites();

        var started = emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None).AsTask();
        await stream.BlockedWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var accepted = emitter.SubmitAsync(new EligibilityDetermined(request, 1), cancellation.Token).AsTask();
        cancellation.Cancel();
        stream.ReleaseWrite();

        await Task.WhenAll(started, accepted).WaitAsync(TimeSpan.FromSeconds(5));
        var events = ParseAndValidate(stream.Bytes.ToArray());
        Assert.AreEqual(3, events.Length, "post-acceptance-event-retained");
        Assert.AreEqual(3L, events[2].Sequence, "post-acceptance-sequence");
        Assert.AreEqual(WorkerProtocolV1.EligibilityDeterminedType, events[2].Type, "post-acceptance-type");
    }

    [TestMethod]
    public async Task SubmitAsync_CallerCancellationAfterActiveWriteAcceptanceDoesNotCancelWriterLifetime()
    {
        var stream = new BlockingWriteStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        stream.BlockWrites();

        using var cancellation = new CancellationTokenSource();
        var active = emitter.SubmitAsync(new RunStarted(request, Timestamp), cancellation.Token).AsTask();
        await stream.BlockedWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.IsFalse(active.IsCompleted, "active-write-caller-cancellation-does-not-cancel-writer");

        stream.ReleaseWrite();
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None);

        var events = ParseAndValidate(stream.Bytes.ToArray());
        Assert.AreEqual(3, events.Length, "active-write-caller-cancellation-frame-count");
        Assert.AreEqual(WorkerProtocolV1.EligibilityDeterminedType, events[^1].Type, "active-write-caller-cancellation-writer-remains-live");
    }

    [TestMethod]
    public async Task SubmitAsync_CapacityOnePreservesAcceptedFifoWithoutDropOrCoalescing()
    {
        var stream = new BlockingWriteStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        stream.BlockWrites();

        var started = emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None).AsTask();
        await stream.BlockedWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var eligibility = emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None).AsTask();
        var log = emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "fifo-third"), CancellationToken.None).AsTask();
        Assert.IsFalse(log.IsCompleted, "fifo-third-waits-behind-capacity-one");

        stream.ReleaseWrite();
        await Task.WhenAll(started, eligibility, log).WaitAsync(TimeSpan.FromSeconds(5));

        var events = ParseAndValidate(stream.Bytes.ToArray());
        CollectionAssert.AreEqual(
            new[] { WorkerProtocolV1.ReadyType, WorkerProtocolV1.RunStartedType, WorkerProtocolV1.EligibilityDeterminedType, WorkerProtocolV1.LogEmittedType },
            events.Select(@event => @event.Type).ToArray(),
            "fifo-exact-accepted-order");
        Assert.AreEqual("fifo-third", ((LogEmittedPayload)events[3].Payload).Message, "fifo-no-replacement");
        Assert.AreEqual(4, stream.WriteCount, "fifo-one-write-per-candidate");
        Assert.AreEqual(4, stream.FlushCount, "fifo-one-flush-per-candidate");
    }

    [TestMethod]
    public async Task SubmitAsync_TerminalClosesIntakeAndRemainsTheLastFlushedFrame()
    {
        var stream = new BlockingWriteStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None);
        await emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None);
        await emitter.SubmitAsync(new ProgressChanged(request, new ProcessingProgress(1, 1, 0, 0)), CancellationToken.None);
        var activityId = Guid.NewGuid();
        await emitter.SubmitAsync(new ActivityStarted(request, activityId, "resolve"), CancellationToken.None);
        stream.BlockWrites();

        var ended = emitter.SubmitAsync(new ActivityEnded(request, activityId), CancellationToken.None).AsTask();
        await stream.BlockedWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var terminal = emitter.SubmitAsync(new RunFinished(request, Result(request, Timestamp, Timestamp.AddSeconds(1), 1, 1, 0, 0, ProcessingRunOutcome.Completed)), CancellationToken.None).AsTask();
        await Assert.ThrowsExactlyAsync<WorkerNdjsonOutputClosedException>(
            async () => await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "late"), CancellationToken.None),
            "late-event-rejected-after-terminal-acceptance");
        var dispose = emitter.DisposeAsync().AsTask();
        Assert.IsFalse(dispose.IsCompleted, "dispose-awaits-accepted-terminal");

        stream.ReleaseWrite();
        await Task.WhenAll(ended, terminal, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        var events = ParseAndValidate(stream.Bytes.ToArray());
        Assert.AreEqual(WorkerProtocolV1.ActivityEndedType, events[^2].Type, "activity-end-before-terminal");
        Assert.AreEqual(WorkerProtocolV1.CompletedType, events[^1].Type, "terminal-last");
        Assert.AreEqual(events.Length, stream.FlushCount, "terminal-flushed-last-frame");
    }

    [TestMethod]
    public async Task SubmitAsync_TerminalAcceptanceWinsCandidateRaceAndRejectsLateCandidate()
    {
        var stream = new BlockingWriteStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None);
        await emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None);
        stream.BlockWrites();

        var active = emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "before-terminal"), CancellationToken.None).AsTask();
        await stream.BlockedWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var terminal = emitter.SubmitAsync(
            new RunFinished(request, Result(request, Timestamp, Timestamp.AddSeconds(1), 0, 0, 0, 0, ProcessingRunOutcome.Completed)),
            CancellationToken.None).AsTask();
        Assert.IsFalse(terminal.IsCompleted, "terminal-race-terminal-waits-for-active-write");

        var lateFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonOutputClosedException>(
            async () => await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "after-terminal"), CancellationToken.None),
            "terminal-race-late-candidate-rejected");
        Assert.AreEqual("worker-ndjson-output-closed", lateFailure.Message, "terminal-race-late-candidate-message");
        Assert.IsFalse(Encoding.UTF8.GetString(stream.Bytes.ToArray()).Contains("after-terminal", StringComparison.Ordinal), "terminal-race-no-late-raw-payload");

        stream.ReleaseWrite();
        await Task.WhenAll(active, terminal).WaitAsync(TimeSpan.FromSeconds(5));

        var events = ParseAndValidate(stream.Bytes.ToArray());
        CollectionAssert.AreEqual(
            new[] { WorkerProtocolV1.ReadyType, WorkerProtocolV1.RunStartedType, WorkerProtocolV1.EligibilityDeterminedType, WorkerProtocolV1.LogEmittedType, WorkerProtocolV1.CompletedType },
            events.Select(@event => @event.Type).ToArray(),
            "terminal-race-accepted-order");
        Assert.AreEqual("before-terminal", ((LogEmittedPayload)events[3].Payload).Message, "terminal-race-active-message");
        Assert.AreEqual(5, events.Length, "terminal-race-no-late-frame");
        Assert.AreEqual(5, stream.WriteCount, "terminal-race-one-write-per-frame");
        Assert.AreEqual(5, stream.FlushCount, "terminal-race-one-flush-per-frame");
    }

    [TestMethod]
    public async Task SubmitAsync_ConcurrentTerminalCandidatesAcceptExactlyOneRunFinished()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None);
        await emitter.SubmitAsync(new EligibilityDetermined(request, 0), CancellationToken.None);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Exception?> SubmitTerminalAsync()
        {
            try
            {
                await barrier.Task;
                await emitter.SubmitAsync(
                    new RunFinished(request, Result(request, Timestamp, Timestamp.AddSeconds(1), 0, 0, 0, 0, ProcessingRunOutcome.Completed)),
                    CancellationToken.None);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        var first = SubmitTerminalAsync();
        var second = SubmitTerminalAsync();
        barrier.TrySetResult();
        var failures = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, failures.Count(failure => failure is null), "concurrent-terminal-one-accepted");
        Assert.AreEqual(1, failures.Count(failure => failure is WorkerNdjsonOutputClosedException), "concurrent-terminal-one-rejected");
        var rejected = failures.Single(failure => failure is WorkerNdjsonOutputClosedException);
        Assert.AreEqual("worker-ndjson-output-closed", rejected!.Message, "concurrent-terminal-rejection-message");

        var events = ParseAndValidate(stream.Bytes.ToArray());
        Assert.AreEqual(4, events.Length, "concurrent-terminal-one-frame");
        Assert.AreEqual(1, events.Count(@event => @event.Type == WorkerProtocolV1.CompletedType), "concurrent-terminal-one-run-finished");
        Assert.AreEqual(WorkerProtocolV1.CompletedType, events[^1].Type, "concurrent-terminal-terminal-last");
        for (var index = 0; index < events.Length; index++)
        {
            Assert.AreEqual(index + 1L, events[index].Sequence, "concurrent-terminal-contiguous-sequence");
        }
    }

    [TestMethod]
    public async Task DisposeAsync_BreaksPendingReceiptsAndCompletesAfterTheWriterSettles()
    {
        var stream = new BlockingWriteStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        stream.BlockWrites();

        var pending = emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None).AsTask();
        await stream.BlockedWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = emitter.DisposeAsync().AsTask();
        await stream.WriteFinalized.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pendingFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await pending, "dispose-fails-pending-receipt");
        var disposeFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await dispose.WaitAsync(TimeSpan.FromSeconds(5)),
            "dispose-reports-missing-terminal-failure");
        Assert.IsFalse(stream.DisposedBeforeWriteFinality, "dispose-waits-for-active-writer-finality");
        var futureFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None),
            "dispose-fails-future-receipt");
        Assert.AreSame(pendingFailure, disposeFailure, "dispose-pending-owner-failure-identity");
        Assert.AreSame(pendingFailure, futureFailure, "dispose-pending-future-failure-identity");
        Assert.AreEqual(WorkerNdjsonFailureStage.Disposal, disposeFailure.Stage, "dispose-missing-terminal-stage");
        var events = ParseAndValidate(stream.Bytes.ToArray());
        Assert.IsFalse(events.Any(@event => @event.Category == WorkerProtocolV1.TerminalCategory), "dispose-does-not-fabricate-terminal");
    }

    [TestMethod]
    public async Task DisposeAsync_FansOneExactFailureToActiveQueuedWaitingAndFutureCallers()
    {
        var stream = new BlockingWriteStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        stream.BlockWrites();

        var active = emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None).AsTask();
        await stream.BlockedWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None).AsTask();
        var waiting = emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "waiting"), CancellationToken.None).AsTask();
        Assert.IsFalse(waiting.IsCompleted, "dispose-fanout-waiting-producer-is-backpressured");

        var dispose = emitter.DisposeAsync().AsTask();
        await stream.WriteFinalized.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var activeFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await active, "dispose-fanout-active-failure");
        var queuedFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await queued, "dispose-fanout-queued-failure");
        var waitingFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await waiting, "dispose-fanout-waiting-failure");
        var disposeFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await dispose, "dispose-fanout-owner-failure");
        var futureFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "future"), CancellationToken.None),
            "dispose-fanout-future-failure");

        Assert.AreSame(activeFailure, queuedFailure, "dispose-fanout-active-queued-identity");
        Assert.AreSame(activeFailure, waitingFailure, "dispose-fanout-active-waiting-identity");
        Assert.AreSame(activeFailure, disposeFailure, "dispose-fanout-active-owner-identity");
        Assert.AreSame(activeFailure, futureFailure, "dispose-fanout-active-future-identity");
        Assert.AreEqual(WorkerNdjsonFailureStage.Disposal, activeFailure.Stage, "dispose-fanout-stage");
        Assert.IsFalse(stream.DisposedBeforeWriteFinality, "dispose-fanout-disposal-awaits-writer-finality");
    }

    [TestMethod]
    public async Task SubmitAsync_ConcurrentProducersEmitOneContiguousFramePerAcceptedCandidate()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None);
        await emitter.SubmitAsync(new EligibilityDetermined(request, 0), CancellationToken.None);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = Enumerable.Range(0, 16).Select(async index =>
        {
            await barrier.Task;
            await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "concurrent-" + index), CancellationToken.None);
        }).ToArray();

        barrier.TrySetResult();
        await Task.WhenAll(reports).WaitAsync(TimeSpan.FromSeconds(5));
        await emitter.SubmitAsync(new RunFinished(request, Result(request, Timestamp, Timestamp.AddSeconds(1), 0, 0, 0, 0, ProcessingRunOutcome.Completed)), CancellationToken.None);

        var events = ParseAndValidate(stream.Bytes.ToArray());
        Assert.AreEqual(20, events.Length, "concurrent-one-frame-per-candidate");
        Assert.AreEqual(events.Length, stream.WriteCount, "concurrent-one-write-per-frame");
        Assert.AreEqual(events.Length, stream.FlushCount, "concurrent-one-flush-per-frame");
        for (var index = 0; index < events.Length; index++)
        {
            Assert.AreEqual(index + 1L, events[index].Sequence, "concurrent-exact-next-sequence");
            if (index > 0)
            {
                Assert.AreEqual(request.RunId, events[index].RunId, "concurrent-stable-run-correlation");
            }
        }

        Assert.AreEqual(WorkerProtocolV1.CompletedType, events[^1].Type, "concurrent-terminal-last");
    }

    [TestMethod]
    public async Task SubmitAsync_MappingFailureWithThrowingLoggerBreaksSafelyWithoutPayloadLeakage()
    {
        const string sentinel = "ConnectionString=SECRET_NDJSON_SENTINEL";
        var stream = new RecordingStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), new ThrowingLogger(), CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);

        var failure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.SubmitAsync(new UnsupportedProcessingEvent(request, sentinel), CancellationToken.None),
            "mapping-failure-safe-transport-error");
        var future = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None),
            "mapping-failure-future-safe-transport-error");

        Assert.AreSame(failure, future, "mapping-failure-stable-instance");
        Assert.AreEqual("worker-ndjson-output-failed", failure.Message, "mapping-failure-safe-category");
        Assert.AreEqual(WorkerNdjsonFailureStage.Mapping, failure.Stage, "mapping-failure-stage");
        Assert.IsFalse(failure.ToString().Contains(sentinel, StringComparison.Ordinal), "mapping-failure-no-payload-in-error");
        Assert.IsFalse(Encoding.UTF8.GetString(stream.Bytes.ToArray()).Contains(sentinel, StringComparison.Ordinal), "mapping-failure-no-payload-on-stdout");
        Assert.AreEqual(1, stream.WriteCount, "mapping-failure-no-candidate-write");
        Assert.AreEqual(1, stream.FlushCount, "mapping-failure-no-candidate-flush");
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task TransportBoundaryLoggerOutOfMemory_IsSwallowedAndPreservesStableFanout()
    {
        var outcomes = CreateOutcomes();
        var emitter = new WorkerNdjsonEmitter(
            new RecordingStream(throwWrite: true),
            WorkerNdjsonOutputStreamOwnership.Owned,
            new FixedTimeProvider(Timestamp),
            new OutOfMemoryThrowingLogger(),
            outcomes,
            1);

        var first = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(() => emitter.PublishAsync(CancellationToken.None));
        var future = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(() => emitter.PublishAsync(CancellationToken.None));

        Assert.AreSame(first, future, "emitter-oom-logger-stable-reference");
        Assert.AreEqual(WorkerNdjsonFailureStage.Write, first.Stage, "emitter-oom-logger-stage");
        Assert.IsTrue(outcomes.HasFact, "emitter-oom-logger-retains-mapped-output-fact");
        Assert.AreEqual(6, outcomes.Fact.ExitCode, "emitter-oom-logger-retains-output-code");
    }

    [TestMethod]
    public async Task DisposeAsync_StreamDisposalFailureUsesTheStableTransportFailure()
    {
        var stream = new RecordingStream(throwDispose: true);
        var emitter = Create(stream);
        await emitter.PublishAsync(CancellationToken.None);

        var failure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.DisposeAsync(),
            "dispose-failure-safe-transport-error");
        var future = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.PublishAsync(CancellationToken.None),
            "dispose-failure-future-safe-transport-error");

        Assert.AreSame(failure, future, "dispose-failure-stable-instance");
        Assert.AreEqual("worker-ndjson-output-failed", failure.Message, "dispose-failure-safe-category");
        Assert.AreEqual(WorkerNdjsonFailureStage.Disposal, failure.Stage, "dispose-failure-stage");
        Assert.AreEqual(1, stream.DisposeCount, "dispose-failure-one-disposal-attempt");
    }

    [TestMethod]
    public async Task DisposeAsync_AfterFlushedTerminalDisposesOwnedStreamExactlyOnce()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None);
        await emitter.SubmitAsync(
            new RunFinished(request, Result(request, Timestamp, Timestamp.AddSeconds(1), 0, 0, 0, 0, ProcessingRunOutcome.Cancelled)),
            CancellationToken.None);

        await emitter.DisposeAsync();
        await emitter.DisposeAsync();

        Assert.AreEqual(1, stream.DisposeCount, "terminal-owner-disposes-stream-once");
        Assert.AreEqual(3, stream.WriteCount, "terminal-owner-no-disposal-frame");
        Assert.AreEqual(3, stream.FlushCount, "terminal-owner-no-disposal-flush");
    }

    [TestMethod]
    public async Task SubmitAsync_WriteFailureFansOneExactFailureToActiveQueuedAndFutureCallers()
    {
        var stream = new BlockingFailingWriteStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        stream.ArmFailure();

        var active = emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None).AsTask();
        await stream.BlockedWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None).AsTask();
        var waiting = emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "waiting"), CancellationToken.None).AsTask();
        Assert.IsFalse(waiting.IsCompleted, "fanout-third-producer-waits-for-capacity");
        stream.ReleaseFailure();

        var activeFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await active, "fanout-active-failure");
        var queuedFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await queued, "fanout-queued-failure");
        var waitingFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await waiting, "fanout-waiting-failure");
        var futureFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "future"), CancellationToken.None),
            "fanout-future-failure");
        var disposalFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.DisposeAsync(),
            "fanout-owner-disposal-failure");

        Assert.AreSame(activeFailure, queuedFailure, "fanout-active-queued-identity");
        Assert.AreSame(activeFailure, waitingFailure, "fanout-active-waiting-identity");
        Assert.AreSame(activeFailure, futureFailure, "fanout-active-future-identity");
        Assert.AreSame(activeFailure, disposalFailure, "fanout-active-owner-identity");
        Assert.AreEqual(WorkerNdjsonFailureStage.Write, activeFailure.Stage, "fanout-stage");
        Assert.AreEqual(2, stream.WriteCount, "fanout-no-queued-write");
        Assert.AreEqual(1, stream.FlushCount, "fanout-only-ready-flushed");
    }

    [TestMethod]
    [DataRow("LOGGER_SECRET_SENTINEL")]
    [DataRow("ConnectionString=LOGGER_SECRET_SENTINEL")]
    [DataRow("password=LOGGER_SECRET_SENTINEL")]
    [DataRow("Bearer LOGGER_SECRET_SENTINEL")]
    public async Task SubmitAsync_SafeLoggerDiagnosticNamesOnlyStableStageAndLogsOnce(string sentinel)
    {
        var stream = new RecordingStream();
        var logger = new RecordingLogger();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), logger, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);

        var failure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.SubmitAsync(new UnsupportedProcessingEvent(request, sentinel), CancellationToken.None),
            "logger-safe-mapping-failure");
        var future = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None),
            "logger-safe-future-failure");

        Assert.AreSame(failure, future, "logger-safe-stable-failure");
        Assert.IsFalse(failure.ToString().Contains(sentinel, StringComparison.Ordinal), "logger-safe-no-payload-in-transport-error");
        Assert.IsFalse(Encoding.UTF8.GetString(stream.Bytes.ToArray()).Contains(sentinel, StringComparison.Ordinal), "logger-safe-no-payload-on-stdout");
        Assert.AreEqual(1, logger.Entries.Count, "logger-safe-one-diagnostic");
        Assert.AreEqual(LogLevel.Warning, logger.Entries[0].Level, "logger-safe-warning-level");
        Assert.IsNull(logger.Entries[0].Exception, "logger-safe-no-exception-object");
        Assert.IsTrue(logger.Entries[0].Message.Contains("Mapping", StringComparison.Ordinal), "logger-safe-stage-only");
        Assert.IsTrue(logger.Entries[0].Message.Length <= 96, "logger-safe-bounded-diagnostic");
        Assert.IsFalse(logger.Entries[0].Message.Contains(sentinel, StringComparison.Ordinal), "logger-safe-no-payload");
    }

    [TestMethod]
    [DataRow("OpenStandardOutput")]
    [DataRow("Mapping")]
    [DataRow("Validation")]
    [DataRow("Size")]
    [DataRow("Write")]
    [DataRow("Flush")]
    [DataRow("Disposal")]
    public async Task NaturalTransportFailuresEmitOneExactSafeLoggerRow(string stageName)
    {
        var expectedStage = Enum.Parse<WorkerNdjsonFailureStage>(stageName);
        var logger = new RecordingLogger();

        var failure = await CauseNaturalFailureAsync(expectedStage, logger);

        Assert.AreEqual(expectedStage, failure.Stage, "logger-natural-exact-stage");
        Assert.AreEqual(1, logger.Entries.Count, "logger-natural-one-row");
        Assert.AreEqual(LogLevel.Warning, logger.Entries[0].Level, "logger-natural-exact-level");
        Assert.AreEqual(default, logger.Entries[0].EventId, "logger-natural-exact-event-id");
        Assert.AreEqual("worker-ndjson-output-failed stage=" + stageName, logger.Entries[0].Message, "logger-natural-exact-message");
        Assert.IsNull(logger.Entries[0].Exception, "logger-natural-no-exception");
    }

    [TestMethod]
    public async Task SubmitAsync_UsesLiteralUtf8LfFramingForEscapedAndMultibyteLogValues()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        const string message = "multibyte-東京\r\nnext";
        await emitter.PublishAsync(CancellationToken.None);
        await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None);
        await emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None);
        await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, message), CancellationToken.None);

        var bytes = stream.Bytes.ToArray();
        Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf, "utf8-no-bom");
        Assert.AreEqual(4, bytes.Count(value => value == (byte)'\n'), "utf8-one-lf-per-frame");
        Assert.AreEqual(0, bytes.Count(value => value == (byte)'\r'), "utf8-no-physical-cr");
        Assert.AreEqual(4, stream.WriteCount, "utf8-one-write-per-frame");
        Assert.AreEqual(4, stream.FlushCount, "utf8-one-flush-per-frame");
        var events = ParseAndValidate(bytes);
        Assert.AreEqual(message, ((LogEmittedPayload)events[^1].Payload).Message, "utf8-escaped-and-multibyte-value");
    }

    [TestMethod]
    public async Task SubmitAsync_ValidIntentionalSecretLogMatchesFixedRawProtocolOracle()
    {
        const string secret = "Server=db;Password=FIXED_SECRET_LOG_SENTINEL;SELECT private_value";
        var stream = new RecordingStream();
        var logger = new RecordingLogger();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), logger, CreateOutcomes(), 1);
        var request = new ProcessingRunRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), ProcessingRunTrigger.RunOnce);
        await PrepareForLogAsync(emitter, request);

        await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Error, secret), CancellationToken.None);

        var lines = Encoding.UTF8.GetString(stream.Bytes.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        const string expected = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"diagnostic\",\"type\":\"log-emitted\",\"sequence\":4,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"level\":\"error\",\"message\":\"Server=db;Password=FIXED_SECRET_LOG_SENTINEL;SELECT private_value\"}}";
        Assert.AreEqual(expected, lines[^1], "intentional-secret-log-exact-raw-oracle");
        Assert.IsTrue(lines[^1].Contains(secret, StringComparison.Ordinal), "intentional-secret-log-remains-on-protocol-path");
        Assert.AreEqual(0, logger.Entries.Count, "intentional-secret-log-does-not-use-ordinary-logger");
        var events = ParseAndValidate(stream.Bytes.ToArray());
        Assert.AreEqual(secret, ((LogEmittedPayload)events[^1].Payload).Message, "intentional-secret-log-round-trips");
    }

    [TestMethod]
    public async Task SubmitAsync_NaturalValidationFailureHasStableValidationStage()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        await emitter.SubmitAsync(new RunStarted(request, Timestamp.AddSeconds(1)), CancellationToken.None);

        var failure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None),
            "natural-validation-failure");
        var future = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "future"), CancellationToken.None),
            "natural-validation-future-failure");

        Assert.AreSame(failure, future, "natural-validation-stable-failure");
        Assert.AreEqual(WorkerNdjsonFailureStage.Validation, failure.Stage, "natural-validation-stage");
        Assert.AreEqual(2, stream.WriteCount, "natural-validation-no-invalid-write");
        Assert.AreEqual(2, stream.FlushCount, "natural-validation-no-invalid-flush");
    }

    [TestMethod]
    public async Task PublishAsync_PartialWriteBreaksOnceWithoutRetryOrSyntheticFrames()
    {
        var stream = new PartialWriteThenThrowStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);

        var failure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.PublishAsync(CancellationToken.None),
            "partial-write-safe-transport-error");
        var bytesAfterFailure = stream.Bytes.ToArray();
        var future = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.PublishAsync(CancellationToken.None),
            "partial-write-future-safe-transport-error");

        Assert.AreSame(failure, future, "partial-write-stable-instance");
        Assert.AreEqual(WorkerNdjsonFailureStage.Write, failure.Stage, "partial-write-stage");
        Assert.IsFalse(failure.ToString().Contains("BROKEN_PIPE_SECRET_SENTINEL", StringComparison.Ordinal), "partial-write-no-raw-stream-error");
        Assert.IsTrue(bytesAfterFailure.Length > 0, "partial-write-keeps-operating-system-boundary");
        CollectionAssert.AreEqual(bytesAfterFailure, stream.Bytes.ToArray(), "partial-write-no-retry-or-later-bytes");
        Assert.AreEqual(1, stream.WriteCount, "partial-write-one-write");
        Assert.AreEqual(0, stream.FlushCount, "partial-write-no-flush-after-write-failure");
        Assert.IsFalse(Encoding.UTF8.GetString(bytesAfterFailure).Contains("terminal", StringComparison.Ordinal), "partial-write-no-synthetic-terminal");
    }

    [TestMethod]
    public async Task SubmitAsync_RepresentativeRunMatchesHandAuthoredWireBytes()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var request = new ProcessingRunRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), ProcessingRunTrigger.RunOnce);
        var activityId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        await emitter.PublishAsync(CancellationToken.None);
        await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None);
        await emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None);
        await emitter.SubmitAsync(new ProgressChanged(request, new ProcessingProgress(1, 1, 0, 0)), CancellationToken.None);
        await emitter.SubmitAsync(new ActivityStarted(request, activityId, "resolve"), CancellationToken.None);
        await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Warning, "東京\r\nnext"), CancellationToken.None);
        await emitter.SubmitAsync(new ActivityEnded(request, activityId), CancellationToken.None);
        await emitter.SubmitAsync(
            new RunFinished(request, Result(request, Timestamp, Timestamp.AddSeconds(1), 1, 1, 0, 0, ProcessingRunOutcome.Completed)),
            CancellationToken.None);

        var expected = string.Join('\n', new[]
        {
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":null,\"payload\":{}}",
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"run-started\",\"sequence\":2,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"trigger\":\"run-once\",\"startedAtUtc\":\"2026-08-30T12:34:56.1234567Z\"}}",
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"eligibility-determined\",\"sequence\":3,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"eligibleCount\":1}}",
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"progress\",\"type\":\"progress-changed\",\"sequence\":4,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"processedCount\":1,\"updatedCount\":1,\"skippedCount\":0,\"failedCount\":0}}",
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"activity\",\"type\":\"activity-started\",\"sequence\":5,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"activityId\":\"22222222-2222-2222-2222-222222222222\",\"label\":\"resolve\"}}",
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"diagnostic\",\"type\":\"log-emitted\",\"sequence\":6,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"level\":\"warning\",\"message\":\"\\u6771\\u4EAC\\r\\nnext\"}}",
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"activity\",\"type\":\"activity-ended\",\"sequence\":7,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"activityId\":\"22222222-2222-2222-2222-222222222222\"}}",
            "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"completed\",\"sequence\":8,\"timestampUtc\":\"2026-08-30T12:34:57.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"trigger\":\"run-once\",\"startedAtUtc\":\"2026-08-30T12:34:56.1234567Z\",\"endedAtUtc\":\"2026-08-30T12:34:57.1234567Z\",\"processedCount\":1,\"updatedCount\":1,\"skippedCount\":0,\"failedCount\":0,\"failureMessage\":null}}"
        }) + "\n";

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), stream.Bytes.ToArray(), "representative-wire-exact-independent-bytes");
        Assert.AreEqual(8, stream.WriteCount, "representative-wire-one-write-each");
        Assert.AreEqual(8, stream.FlushCount, "representative-wire-one-flush-each");
    }

    [TestMethod]
    public async Task SubmitAsync_CancelledAndFailedTerminalsMatchHandAuthoredWireFields()
    {
        foreach (var outcome in new[] { ProcessingRunOutcome.Cancelled, ProcessingRunOutcome.Failed })
        {
            var stream = new RecordingStream();
            var emitter = Create(stream);
            var request = new ProcessingRunRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), ProcessingRunTrigger.Manual);
            await emitter.PublishAsync(CancellationToken.None);
            await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None);
            await emitter.SubmitAsync(
                new RunFinished(request, Result(request, Timestamp, Timestamp.AddSeconds(1), 0, 0, 0, 0, outcome)),
                CancellationToken.None);

            var line = Encoding.UTF8.GetString(stream.Bytes.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)[2];
            var type = outcome == ProcessingRunOutcome.Cancelled ? "cancelled" : "failed";
            var failure = outcome == ProcessingRunOutcome.Cancelled ? "null" : "\"failed\"";
            var expected = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"terminal\",\"type\":\"" + type + "\",\"sequence\":3,\"timestampUtc\":\"2026-08-30T12:34:57.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"trigger\":\"manual\",\"startedAtUtc\":\"2026-08-30T12:34:56.1234567Z\",\"endedAtUtc\":\"2026-08-30T12:34:57.1234567Z\",\"processedCount\":0,\"updatedCount\":0,\"skippedCount\":0,\"failedCount\":0,\"failureMessage\":" + failure + "}}";
            Assert.AreEqual(expected, line, "terminal-independent-exact-wire-" + type);
            Assert.AreEqual(3, stream.WriteCount, "terminal-independent-write-count-" + type);
            Assert.AreEqual(3, stream.FlushCount, "terminal-independent-flush-count-" + type);
        }
    }

    [TestMethod]
    public async Task SubmitAsync_IndependentAsciiAndMultibyteSizeBoundariesAreExact()
    {
        const string prefix = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"diagnostic\",\"type\":\"log-emitted\",\"sequence\":4,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"level\":\"information\",\"message\":\"";
        const string suffix = "\"}}";
        var fixedBytes = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(suffix);
        var budget = WorkerProtocolV1.MaxMessageBytes - fixedBytes;
        var ascii = new string('x', budget);
        var escapedUnicode = new string('é', budget / 6) + new string('x', budget % 6);

        await AssertIndependentBoundaryAsync(ascii, expectedSuccess: true);
        await AssertIndependentBoundaryAsync(ascii + "x", expectedSuccess: false);
        await AssertIndependentBoundaryAsync(escapedUnicode, expectedSuccess: true);
        await AssertIndependentBoundaryAsync(escapedUnicode + "x", expectedSuccess: false);
    }

    [TestMethod]
    public async Task DisposeAsync_DuringFlushFailsReceiptAndOwnerWithOneStableFailure()
    {
        var stream = new DisposalGatedFlushStream();
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
        var publish = emitter.PublishAsync(CancellationToken.None);
        await stream.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = emitter.DisposeAsync().AsTask();
        await stream.FlushFinalized.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var publishFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await publish.WaitAsync(TimeSpan.FromSeconds(5)),
            "dispose-during-flush-receipt-failure");
        var disposeFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await dispose.WaitAsync(TimeSpan.FromSeconds(5)),
            "dispose-during-flush-owner-failure");
        Assert.IsFalse(stream.DisposedBeforeFlushFinality, "dispose-during-flush-waits-for-writer-finality");
        var futureFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.PublishAsync(CancellationToken.None),
            "dispose-during-flush-future-failure");

        Assert.AreSame(publishFailure, disposeFailure, "dispose-during-flush-owner-identity");
        Assert.AreSame(publishFailure, futureFailure, "dispose-during-flush-future-identity");
        Assert.AreEqual(WorkerNdjsonFailureStage.Disposal, publishFailure.Stage, "dispose-during-flush-stage");
        Assert.AreEqual(1, stream.DisposeCount, "dispose-during-flush-owned-stream-once");
    }

    [TestMethod]
    public async Task DisposeAsync_BeforeRunStartsClosesIntakeAndDisposesOwnedStreamOnce()
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        await emitter.PublishAsync(CancellationToken.None);

        await emitter.DisposeAsync();
        await emitter.DisposeAsync();
        var future = await Assert.ThrowsExactlyAsync<WorkerNdjsonOutputClosedException>(
            async () => await emitter.SubmitAsync(
                new RunStarted(new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce), Timestamp),
                CancellationToken.None),
            "dispose-before-run-future-closed");

        Assert.AreEqual("worker-ndjson-output-closed", future.Message, "dispose-before-run-future-category");
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(ExpectedReadyJson() + "\n"),
            stream.Bytes.ToArray(),
            "dispose-before-run-exact-ready-bytes");
        Assert.AreEqual(1, stream.WriteCount, "dispose-before-run-ready-write-count");
        Assert.AreEqual(1, stream.FlushCount, "dispose-before-run-ready-flush-count");
        var ready = WorkerProtocolCodec.Parse(stream.Bytes.ToArray());
        Assert.IsTrue(ready.IsSuccess, ready.Failure?.Diagnostic);
        Assert.IsNotNull(ready.Event, "dispose-before-run-ready-event");
        Assert.AreEqual(WorkerProtocolV1.ReadyType, ready.Event.Type, "dispose-before-run-ready-type");
        Assert.IsInstanceOfType<ReadyPayload>(ready.Event.Payload, "dispose-before-run-ready-payload-type");
        Assert.AreEqual(1L, ready.Event.Sequence, "dispose-before-run-ready-sequence");
        Assert.IsNull(ready.Event.RunId, "dispose-before-run-ready-run-id");
        Assert.AreEqual(1, stream.DisposeCount, "dispose-before-run-owned-stream-once");
    }

    [TestMethod]
    [DataRow("open")]
    [DataRow("write")]
    [DataRow("flush")]
    [DataRow("dispose")]
    public async Task RawOutOfMemory_EscapesWithoutMappedOutputOutcome(string stage)
    {
        var outcomes = CreateOutcomes();
        var rawFailure = new OutOfMemoryException("controlled-emitter-oom");

        if (stage == "open")
        {
            var thrownOnOpen = Assert.ThrowsExactly<OutOfMemoryException>(() => WorkerNdjsonEmitter.CreateProduction(
                new ExceptionOutputStreamFactory(rawFailure),
                new FixedTimeProvider(Timestamp),
                NullLogger<WorkerNdjsonEmitter>.Instance,
                outcomes));
            Assert.AreSame(rawFailure, thrownOnOpen, "emitter-open-oom-reference");
            Assert.IsFalse(outcomes.HasFact, "emitter-open-oom-has-no-mapped-fact");
            Assert.AreEqual(0, outcomes.Fact.ExitCode, "emitter-open-oom-sentinel-remains-non-authoritative");
            return;
        }

        var stream = new StageExceptionStream(stage, rawFailure);
        var emitter = new WorkerNdjsonEmitter(
            stream,
            WorkerNdjsonOutputStreamOwnership.Owned,
            new FixedTimeProvider(Timestamp),
            NullLogger<WorkerNdjsonEmitter>.Instance,
            outcomes,
            1);

        var thrown = stage == "dispose"
            ? await PublishThenDisposeAsync(emitter)
            : await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => emitter.PublishAsync(CancellationToken.None));

        Assert.AreSame(rawFailure, thrown, $"emitter-{stage}-oom-reference");
        Assert.IsFalse(outcomes.HasFact, $"emitter-{stage}-oom-has-no-mapped-fact");
        Assert.AreEqual(0, outcomes.Fact.ExitCode, $"emitter-{stage}-oom-sentinel-remains-non-authoritative");

        if (stage is "write" or "flush")
        {
            var futureFailure = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(
                () => emitter.PublishAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)),
                $"emitter-{stage}-oom-future-fanout");
            var disposeFailure = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(
                () => emitter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
                $"emitter-{stage}-oom-dispose-fanout");
            Assert.AreSame(rawFailure, futureFailure, $"emitter-{stage}-oom-future-reference");
            Assert.AreSame(rawFailure, disposeFailure, $"emitter-{stage}-oom-dispose-reference");
        }
    }

    [TestMethod]
    [TestCategory("Change23")]
    public async Task FatalWriterOutOfMemory_FansExactUnmappedReferenceToActiveQueuedWaitingFutureAndDisposeWithoutHanging()
    {
        var outcomes = CreateOutcomes();
        var rawFailure = new OutOfMemoryException("controlled-fatal-writer-oom");
        var stream = new BlockingOutOfMemoryWriteStream(rawFailure);
        var emitter = new WorkerNdjsonEmitter(
            stream,
            WorkerNdjsonOutputStreamOwnership.Owned,
            new FixedTimeProvider(Timestamp),
            NullLogger<WorkerNdjsonEmitter>.Instance,
            outcomes,
            1);
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        await emitter.PublishAsync(CancellationToken.None);
        stream.ArmFailure();

        var active = emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None).AsTask();
        await stream.BlockedWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None).AsTask();
        var waiting = emitter.SubmitAsync(
            new LogEmitted(request, ProcessingLogLevel.Information, "fatal-waiting"),
            CancellationToken.None).AsTask();
        Assert.IsFalse(waiting.IsCompleted, "fatal-oom-waiting-producer-is-backpressured");

        stream.ReleaseFailure();

        var activeFailure = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => active.WaitAsync(TimeSpan.FromSeconds(5)));
        var queuedFailure = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        var waitingFailure = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        var futureFailure = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() =>
            emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, "fatal-future"), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var disposeFailure = await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => emitter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.AreSame(rawFailure, activeFailure, "fatal-oom-active-reference");
        Assert.AreSame(rawFailure, queuedFailure, "fatal-oom-queued-reference");
        Assert.AreSame(rawFailure, waitingFailure, "fatal-oom-waiting-reference");
        Assert.AreSame(rawFailure, futureFailure, "fatal-oom-future-reference");
        Assert.AreSame(rawFailure, disposeFailure, "fatal-oom-dispose-reference");
        Assert.IsFalse(outcomes.HasFact, "fatal-oom-does-not-create-mapped-output-fact");
        Assert.AreEqual(2, stream.WriteCount, "fatal-oom-no-write-after-fatal-boundary");
        Assert.AreEqual(1, stream.FlushCount, "fatal-oom-only-ready-flushed");
    }

    [TestMethod]
    public async Task CreateProduction_DoesNotDisposeStandardOutput()
    {
        var stream = new RecordingStream();
        var emitter = WorkerNdjsonEmitter.CreateProduction(
            new FixedOutputStreamFactory(stream),
            new FixedTimeProvider(Timestamp),
            NullLogger<WorkerNdjsonEmitter>.Instance,
            CreateOutcomes());
        await emitter.PublishAsync(CancellationToken.None);

        await emitter.DisposeAsync();

        Assert.AreEqual(0, stream.DisposeCount, "production-standard-output-is-unowned");
    }

    [TestMethod]
    public async Task DisposeAsync_DoesNotDisposeAnUnownedStream()
    {
        var stream = new RecordingStream();
        var emitter = new WorkerNdjsonEmitter(
            stream,
            WorkerNdjsonOutputStreamOwnership.Unowned,
            new FixedTimeProvider(Timestamp),
            NullLogger<WorkerNdjsonEmitter>.Instance,
            CreateOutcomes(),
            1);
        await emitter.PublishAsync(CancellationToken.None);

        await emitter.DisposeAsync();

        Assert.AreEqual(0, stream.DisposeCount, "unowned-stream-not-disposed");
    }

    private static async Task<WorkerNdjsonTransportException> CauseNaturalFailureAsync(
        WorkerNdjsonFailureStage stage,
        RecordingLogger logger)
    {
        var request = new ProcessingRunRequest(Guid.NewGuid(), ProcessingRunTrigger.RunOnce);
        if (stage == WorkerNdjsonFailureStage.OpenStandardOutput)
        {
            var emitter = WorkerNdjsonEmitter.CreateProduction(
                new ThrowingOutputStreamFactory(),
                new FixedTimeProvider(Timestamp),
                logger,
                CreateOutcomes());
            return await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
                async () => await emitter.PublishAsync(CancellationToken.None),
                "logger-natural-open-standard-output");
        }

        var stream = new RecordingStream(
            throwWrite: stage == WorkerNdjsonFailureStage.Write,
            throwFlush: stage == WorkerNdjsonFailureStage.Flush,
            throwDispose: stage == WorkerNdjsonFailureStage.Disposal);
        var clock = stage == WorkerNdjsonFailureStage.Mapping
            ? new FixedTimeProvider(Timestamp.ToOffset(TimeSpan.FromHours(1)))
            : new FixedTimeProvider(Timestamp);
        var output = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, clock, logger, CreateOutcomes(), 1);
        if (stage is WorkerNdjsonFailureStage.Mapping or WorkerNdjsonFailureStage.Write or WorkerNdjsonFailureStage.Flush)
        {
            return await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
                async () => await output.PublishAsync(CancellationToken.None),
                "logger-natural-ready-" + stage);
        }

        await output.PublishAsync(CancellationToken.None);
        if (stage == WorkerNdjsonFailureStage.Validation)
        {
            await output.SubmitAsync(new RunStarted(request, Timestamp.AddSeconds(1)), CancellationToken.None);
            return await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
                async () => await output.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None),
                "logger-natural-validation");
        }

        if (stage == WorkerNdjsonFailureStage.Size)
        {
            await output.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None);
            await output.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None);
            return await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
                async () => await output.SubmitAsync(
                    new LogEmitted(request, ProcessingLogLevel.Information, new string('x', WorkerProtocolV1.MaxMessageBytes)),
                    CancellationToken.None),
                "logger-natural-size");
        }

        return await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await output.DisposeAsync(),
            "logger-natural-disposal");
    }

    private static async Task AssertIndependentBoundaryAsync(string message, bool expectedSuccess)
    {
        var stream = new RecordingStream();
        var emitter = Create(stream);
        var request = new ProcessingRunRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), ProcessingRunTrigger.RunOnce);
        await PrepareForLogAsync(emitter, request);
        if (expectedSuccess)
        {
            await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, message), CancellationToken.None);
            var finalLf = stream.Bytes.LastIndexOf((byte)'\n');
            var previousLf = stream.Bytes.Take(finalLf).ToList().LastIndexOf((byte)'\n');
            var expectedMessage = message.Replace("é", "\\u00E9", StringComparison.Ordinal);
            var expectedRawJson = "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"diagnostic\",\"type\":\"log-emitted\",\"sequence\":4,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":\"11111111-1111-1111-1111-111111111111\",\"payload\":{\"level\":\"information\",\"message\":\"" + expectedMessage + "\"}}";
            var actualRawJson = Encoding.UTF8.GetString(stream.Bytes.Skip(previousLf + 1).Take(finalLf - previousLf - 1).ToArray());
            Assert.AreEqual(expectedRawJson, actualRawJson, "independent-size-full-raw-json");
            Assert.AreEqual(WorkerProtocolV1.MaxMessageBytes, finalLf - previousLf - 1, "independent-size-exact-json-byte-count");
            return;
        }

        var failure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(
            async () => await emitter.SubmitAsync(new LogEmitted(request, ProcessingLogLevel.Information, message), CancellationToken.None),
            "independent-size-one-over-rejected");
        Assert.AreEqual(WorkerNdjsonFailureStage.Size, failure.Stage, "independent-size-one-over-stage");
        Assert.AreEqual(3, stream.WriteCount, "independent-size-one-over-no-write");
    }

    private static async Task PrepareForLogAsync(WorkerNdjsonEmitter emitter, ProcessingRunRequest request)
    {
        await emitter.PublishAsync(CancellationToken.None);
        await emitter.SubmitAsync(new RunStarted(request, Timestamp), CancellationToken.None);
        await emitter.SubmitAsync(new EligibilityDetermined(request, 1), CancellationToken.None);
    }

    private static async Task AssertPermanentFailureAsync(FailureKind kind)
    {
        var stream = new RecordingStream(throwWrite: kind == FailureKind.Write, throwFlush: kind == FailureKind.Flush);
        var clock = kind == FailureKind.Mapping
            ? new FixedTimeProvider(Timestamp.ToOffset(TimeSpan.FromHours(1)))
            : new FixedTimeProvider(Timestamp);
        var emitter = new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, clock, NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);

        var first = emitter.PublishAsync(CancellationToken.None);
        var second = emitter.PublishAsync(CancellationToken.None);
        var firstFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await first, "ready-first-safe-failure");
        var secondFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await second, "ready-second-safe-failure");
        var writesAfterFailure = stream.WriteCount;
        var flushesAfterFailure = stream.FlushCount;
        var bytesAfterFailure = stream.Bytes.ToArray();
        var callsAfterFailure = stream.Calls.ToArray();
        var futureFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await emitter.PublishAsync(CancellationToken.None), "ready-future-safe-failure");
        var repeatedFutureFailure = await Assert.ThrowsExactlyAsync<WorkerNdjsonTransportException>(async () => await emitter.PublishAsync(CancellationToken.None), "ready-repeated-future-safe-failure");

        Assert.AreSame(firstFailure, secondFailure, "ready-concurrent-failure-identity");
        Assert.AreSame(firstFailure, futureFailure, "ready-future-failure-identity");
        Assert.AreSame(firstFailure, repeatedFutureFailure, "ready-repeated-future-failure-identity");
        Assert.AreEqual("worker-ndjson-output-failed", firstFailure.Message, "ready-safe-failure-category");
        Assert.AreEqual(kind switch
        {
            FailureKind.Mapping => WorkerNdjsonFailureStage.Mapping,
            FailureKind.Write => WorkerNdjsonFailureStage.Write,
            _ => WorkerNdjsonFailureStage.Flush
        }, firstFailure.Stage, "ready-safe-failure-stage");
        CollectionAssert.AreEqual(bytesAfterFailure, stream.Bytes.ToArray(), "ready-no-later-bytes");
        CollectionAssert.AreEqual(callsAfterFailure, stream.Calls.ToArray(), "ready-no-later-transport-calls");
        Assert.AreEqual(writesAfterFailure, stream.WriteCount, "ready-no-later-writes");
        Assert.AreEqual(flushesAfterFailure, stream.FlushCount, "ready-no-later-flushes");
        var expectedWrites = kind == FailureKind.Mapping ? 0 : 1;
        var expectedFlushes = kind == FailureKind.Flush ? 1 : 0;
        Assert.AreEqual(expectedWrites, writesAfterFailure, "ready-exact-write-count");
        Assert.AreEqual(expectedFlushes, flushesAfterFailure, "ready-exact-flush-count");
    }

    private static ProcessingRunResult Result(ProcessingRunRequest request, DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc, long processed, long updated, long skipped, long failed, ProcessingRunOutcome outcome)
    {
        return new ProcessingRunResult(request, startedAtUtc, endedAtUtc, processed, updated, skipped, failed, outcome, outcome == ProcessingRunOutcome.Failed ? "failed" : null);
    }

    private static WorkerProtocolEvent[] ParseAndValidate(byte[] bytes)
    {
        var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var validator = new WorkerProtocolEventStreamValidator();
        var events = new WorkerProtocolEvent[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            var parsed = WorkerProtocolCodec.Parse(Encoding.UTF8.GetBytes(lines[index] + "\n"));
            Assert.IsTrue(parsed.IsSuccess, "reporter-parse");
            Assert.IsNotNull(parsed.Event, "reporter-parsed-event");
            events[index] = parsed.Event;
            Assert.IsTrue(validator.Validate(events[index]).IsSuccess, "reporter-validator");
        }

        return events;
    }

    private static void AssertEvent(WorkerProtocolEvent @event, string category, string type, long sequence, DateTimeOffset timestampUtc, Guid runId)
    {
        Assert.AreEqual(category, @event.Category, "reporter-category");
        Assert.AreEqual(type, @event.Type, "reporter-type");
        Assert.AreEqual(sequence, @event.Sequence, "reporter-sequence");
        Assert.AreEqual(timestampUtc, @event.TimestampUtc, "reporter-timestamp");
        Assert.AreEqual(runId, @event.RunId, "reporter-run-id");
    }

    private static WorkerNdjsonEmitter Create(RecordingStream stream)
    {
        return new WorkerNdjsonEmitter(stream, WorkerNdjsonOutputStreamOwnership.Owned, new FixedTimeProvider(Timestamp), NullLogger<WorkerNdjsonEmitter>.Instance, CreateOutcomes(), 1);
    }

    private static WorkerProcessExitOutcomeAccumulator CreateOutcomes()
    {
        return new WorkerProcessExitOutcomeAccumulator();
    }

    private static async Task<OutOfMemoryException> PublishThenDisposeAsync(WorkerNdjsonEmitter emitter)
    {
        await emitter.PublishAsync(CancellationToken.None);
        return await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() => emitter.DisposeAsync().AsTask());
    }

    private static string ExpectedReadyJson()
    {
        return "{\"protocol\":\"immich-reversegeo.worker\",\"version\":1,\"direction\":\"worker-to-controller\",\"category\":\"lifecycle\",\"type\":\"ready\",\"sequence\":1,\"timestampUtc\":\"2026-08-30T12:34:56.1234567Z\",\"runId\":null,\"payload\":{}}";
    }

    private static void AssertReady(byte[] bytes)
    {
        Assert.AreEqual((byte)'\n', bytes[^1], "ready-lf");
        Assert.AreNotEqual((byte)'\r', bytes[^2], "ready-no-crlf");
        Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf, "ready-no-bom");
        var parsed = WorkerProtocolCodec.Parse(bytes);
        Assert.IsTrue(parsed.IsSuccess, parsed.Failure?.Diagnostic);
        Assert.IsNotNull(parsed.Event, "ready-parsed-event");
        Assert.AreEqual(WorkerProtocolV1.LifecycleCategory, parsed.Event.Category, "ready-category");
        Assert.AreEqual(WorkerProtocolV1.ReadyType, parsed.Event.Type, "ready-type");
        Assert.AreEqual(1L, parsed.Event.Sequence, "ready-sequence");
        Assert.IsNull(parsed.Event.RunId, "ready-null-run-id");
        Assert.AreEqual(Timestamp, parsed.Event.TimestampUtc, "ready-timestamp");
        Assert.IsInstanceOfType<ReadyPayload>(parsed.Event.Payload, "ready-empty-payload");
        var validator = new WorkerProtocolEventStreamValidator();
        Assert.IsTrue(validator.Validate(parsed.Event).IsSuccess, "ready-validator");
    }

    public enum FailureKind
    {
        Mapping,
        Write,
        Flush
    }

    private sealed record UnsupportedProcessingEvent(ProcessingRunRequest Request, string SensitivePayload) : ProcessingEvent(Request);

    private sealed class OutOfMemoryThrowingLogger : ILogger<WorkerNdjsonEmitter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => throw new OutOfMemoryException("controlled-emitter-logger-oom");
    }

    private sealed class ThrowingLogger : ILogger<WorkerNdjsonEmitter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("logger");
    }

    private sealed class RecordingLogger : ILogger<WorkerNdjsonEmitter>
    {
        public List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, eventId, formatter(state, exception), exception));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedOutputStreamFactory(Stream stream) : IWorkerNdjsonOutputStreamFactory
    {
        public Stream OpenStandardOutput() => stream;
    }

    private sealed class ThrowingOutputStreamFactory : IWorkerNdjsonOutputStreamFactory
    {
        public Stream OpenStandardOutput() => throw new IOException("OPEN_STDOUT_SECRET_SENTINEL");
    }

    private sealed class ExceptionOutputStreamFactory(Exception failure) : IWorkerNdjsonOutputStreamFactory
    {
        public Stream OpenStandardOutput() => throw failure;
    }

    private sealed class StageExceptionStream(string stage, Exception failure) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override ValueTask DisposeAsync() => stage == "dispose" ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => stage == "flush" ? Task.FromException(failure) : Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => stage == "write" ? Task.FromException(failure) : Task.CompletedTask;
    }

    private sealed class DisposalGatedFlushStream : Stream
    {
        private readonly TaskCompletionSource _flushRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _flushFinalized;
        public TaskCompletionSource FlushStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FlushFinalized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public bool DisposedBeforeFlushFinality { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposedBeforeFlushFinality = !_flushFinalized;
            return ValueTask.CompletedTask;
        }

        public override void Flush() => throw new NotSupportedException();
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushStarted.TrySetResult();
            try
            {
                await _flushRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _flushFinalized = true;
                FlushFinalized.TrySetResult();
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingStream : Stream
    {
        private readonly bool _blockFlush;
        private readonly bool _throwWrite;
        private readonly bool _throwFlush;
        private readonly bool _throwDispose;
        private TaskCompletionSource _flushRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _flushStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingStream(bool blockFlush = false, bool throwWrite = false, bool throwFlush = false, bool throwDispose = false)
        {
            _blockFlush = blockFlush;
            _throwWrite = throwWrite;
            _throwFlush = throwFlush;
            _throwDispose = throwDispose;
        }

        public List<byte> Bytes { get; } = [];
        public List<string> Calls { get; } = [];
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FlushStarted => _flushStarted;
        public int WriteCount { get; private set; }
        public int FlushCount { get; private set; }
        public int DisposeCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes.Count;
        public override long Position { get => Bytes.Count; set => throw new NotSupportedException(); }

        public void ReleaseFlush() => _flushRelease.TrySetResult();

        public void BlockNextFlush()
        {
            _flushRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (_throwDispose)
            {
                throw new IOException("dispose");
            }

            return base.DisposeAsync();
        }

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            Calls.Add("flush");
            FlushStarted.TrySetResult();
            if (_throwFlush)
            {
                throw new IOException("flush");
            }

            return _blockFlush ? _flushRelease.Task : Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteCount++;
            Calls.Add("write");
            WriteStarted.TrySetResult();
            if (_throwWrite)
            {
                throw new IOException("write");
            }

            Bytes.AddRange(buffer.AsSpan(offset, count).ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class PartialWriteThenThrowStream : Stream
    {
        public List<byte> Bytes { get; } = [];
        public int WriteCount { get; private set; }
        public int FlushCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes.Count;
        public override long Position { get => Bytes.Count; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteCount++;
            Bytes.AddRange(buffer.AsSpan(offset, count / 2).ToArray());
            throw new IOException("BROKEN_PIPE_SECRET_SENTINEL");
        }
    }

    private sealed class BlockingOutOfMemoryWriteStream(OutOfMemoryException failure) : Stream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _armed;

        public TaskCompletionSource BlockedWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCount { get; private set; }
        public int FlushCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public void ArmFailure()
        {
            _armed = true;
        }

        public void ReleaseFailure()
        {
            _release.TrySetResult();
        }

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteCount++;
            if (_armed)
            {
                BlockedWriteStarted.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                throw failure;
            }
        }
    }

    private sealed class BlockingFailingWriteStream : Stream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _armed;

        public TaskCompletionSource BlockedWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCount { get; private set; }
        public int FlushCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public void ArmFailure()
        {
            _armed = true;
        }

        public void ReleaseFailure()
        {
            _release.TrySetResult();
        }

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteCount++;
            if (_armed)
            {
                BlockedWriteStarted.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                throw new IOException("FANOUT_SECRET_SENTINEL");
            }
        }
    }

    private sealed class BlockingWriteStream : Stream
    {
        private TaskCompletionSource? _writeRelease;
        private bool _writeFinalized;

        public List<byte> Bytes { get; } = [];
        public TaskCompletionSource BlockedWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteFinalized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCount { get; private set; }
        public int FlushCount { get; private set; }
        public bool DisposedBeforeWriteFinality { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes.Count;
        public override long Position { get => Bytes.Count; set => throw new NotSupportedException(); }

        public void BlockWrites()
        {
            _writeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseWrite()
        {
            _writeRelease?.TrySetResult();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposedBeforeWriteFinality = !_writeFinalized;
                _writeRelease?.TrySetCanceled();
            }

            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var copy = buffer.AsSpan(offset, count).ToArray();
            return WriteCoreAsync(copy, cancellationToken);
        }

        private async Task WriteCoreAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            WriteCount++;
            var release = _writeRelease;
            if (release is not null && !release.Task.IsCompleted)
            {
                BlockedWriteStarted.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeFinalized = true;
                    WriteFinalized.TrySetResult();
                }
            }

            Bytes.AddRange(buffer);
        }
    }
}
