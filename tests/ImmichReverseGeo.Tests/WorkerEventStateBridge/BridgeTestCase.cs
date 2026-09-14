using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using Bridge = ImmichReverseGeo.Web.WorkerEventStateBridge.WorkerEventStateBridge;

namespace ImmichReverseGeo.Tests;

internal sealed class BridgeTestCase : IAsyncDisposable
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);
    internal static readonly DateTimeOffset ReadyAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    internal static readonly DateTimeOffset StartedAt = ReadyAt.AddSeconds(1);
    internal static readonly DateTimeOffset EndedAt = StartedAt.AddSeconds(1);
    private int _notifications;

    internal BridgeTestCase(bool previousRun = false, Action<ProcessingEvent>? beforeProjection = null)
    {
        State = new ProcessingState();
        if (previousRun)
        {
            State.StartRun(10);
            State.ApplyProgress(3, 2, 1);
            State.ReportErrorDiagnostic("prior error");
            State.AppendLog("prior history");
            State.CompleteRun();
        }

        State.MarkPending();
        Adapter = new ProcessingStateEventReporter(State, beforeProjection);
        Assert.IsTrue(Adapter.Arm(Request));
        Bridge = new WorkerEventStateBridgeFactory(Adapter).Create(Request);
        State.OnChanged += () => Interlocked.Increment(ref _notifications);
    }

    internal ProcessingRunRequest Request { get; } = new(Guid.NewGuid(), ProcessingRunTrigger.Manual);
    internal ProcessingState State { get; }
    internal ProcessingStateEventReporter Adapter { get; }
    internal Bridge Bridge { get; }
    internal long NextSequence { get; private set; } = 1;
    internal int Notifications => Volatile.Read(ref _notifications);
    internal string[] Logs => State.GetRecentLog().ToArray();

    internal async Task ReadyAsync()
    {
        await Bridge.AcceptAsync(WorkerProtocolMapper.Ready(NextSequence, ReadyAt), CancellationToken.None);
        NextSequence++;
    }

    internal async Task BeginAsync(long total)
    {
        await ReadyAsync();
        await SendAsync(new RunStarted(Request, StartedAt));
        await SendAsync(new EligibilityDetermined(Request, total));
    }

    internal WorkerProtocolEvent Frame(ProcessingEvent processingEvent, long? sequence = null)
    {
        var number = sequence ?? NextSequence;
        return processingEvent switch
        {
            RunStarted started => WorkerProtocolMapper.Map(started, number),
            RunFinished finished => WorkerProtocolMapper.Map(finished, number),
            _ => WorkerProtocolMapper.Map(processingEvent, number, StartedAt.AddTicks(number))
        };
    }

    internal async Task SendAsync(ProcessingEvent processingEvent)
    {
        await Bridge.AcceptAsync(Frame(processingEvent), CancellationToken.None);
        NextSequence++;
    }

    internal Task ProgressAsync(long updated, long skipped, long failed)
        => SendAsync(new ProgressChanged(Request, new ProcessingProgress(updated + skipped + failed, updated, skipped, failed)));

    internal ProcessingRunResult Result(ProcessingRunOutcome outcome, long updated = 0, long skipped = 0, long failed = 0)
        => new(Request, StartedAt, EndedAt, updated + skipped + failed, updated, skipped, failed,
            outcome, outcome == ProcessingRunOutcome.Failed ? "terminal failure" : null);

    internal Task FinishAsync(ProcessingRunOutcome outcome, long updated = 0, long skipped = 0, long failed = 0)
        => SendAsync(new RunFinished(Request, Result(outcome, updated, skipped, failed)));

    internal BridgeStateSnapshot Snapshot() => new(
        State.IsRunning, State.TotalUnprocessed, State.ProcessedThisRun, State.SkippedThisRun,
        State.ErrorsThisRun, State.LastRunStarted, State.LastRunCompleted, State.LastError,
        State.CurrentActivity, string.Join('\n', Logs), Notifications);

    internal async Task<WorkerEventStateBridgeException> RejectWithoutMutationAsync(WorkerProtocolEvent frame)
    {
        var before = Snapshot();
        var exception = await Assert.ThrowsExactlyAsync<WorkerEventStateBridgeException>(
            () => Bridge.AcceptAsync(frame, CancellationToken.None).AsTask());
        Assert.AreEqual(before, Snapshot(), "Rejected events must preserve every observable state value and notification count.");
        Assert.IsNotNull(Bridge.FirstObservation);
        Assert.IsNull(exception.InnerException, "Projection diagnostics must not retain arbitrary callback exceptions.");
        return exception;
    }

    public ValueTask DisposeAsync() => Bridge.DisposeAsync();
}

internal sealed record BridgeStateSnapshot(
    bool Running, long Total, long Updated, long Skipped, long Errors,
    DateTime? Started, DateTime? Completed, string? Error, string? Activity,
    string Logs, int Notifications);
