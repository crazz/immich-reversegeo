using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Core.Processing;

public abstract class ProcessingEventReporter : IProcessingEventReporter
{
    public async ValueTask<IProcessingRunEventSession> OpenRunAsync(ProcessingRunRequest request, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = new ProcessingRunEventSession(request, startedAtUtc, AcceptAsync);
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    protected abstract ValueTask AcceptAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken);
}

public sealed class NoOpProcessingEventReporter : ProcessingEventReporter
{
    public static NoOpProcessingEventReporter Instance { get; } = new();
    private NoOpProcessingEventReporter() { }
    protected override ValueTask AcceptAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ProcessingRunEventSession : IProcessingRunEventSession
{
    private readonly DateTimeOffset _startedAtUtc;
    private readonly Func<ProcessingEvent, CancellationToken, ValueTask> _accept;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ActivityScope> _activities = [];
    private bool _eligibilityDetermined;
    private bool _finished;
    private bool _broken;
    private long _eligibleCount;
    private long _updated;
    private long _skipped;
    private long _failed;

    public ProcessingRunEventSession(ProcessingRunRequest request, DateTimeOffset startedAtUtc, Func<ProcessingEvent, CancellationToken, ValueTask> accept)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(accept);
        if (startedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The start timestamp must have a zero UTC offset.", nameof(startedAtUtc));
        }

        Request = request;
        _startedAtUtc = startedAtUtc;
        _accept = accept;
    }

    public ProcessingRunRequest Request { get; }
    public ValueTask StartAsync(CancellationToken cancellationToken) => ExecuteAsync(() => new RunStarted(Request, _startedAtUtc), null, cancellationToken, false);

    public ValueTask DetermineEligibilityAsync(long eligibleCount, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            EnsureNotFinishedOrBroken();
            if (_eligibilityDetermined)
            {
                throw new InvalidOperationException("Eligibility has already been determined.");
            }

            return new EligibilityDetermined(Request, eligibleCount);
        }, () =>
        {
            _eligibleCount = eligibleCount;
            _eligibilityDetermined = true;
        }, cancellationToken, false);
    }

    public ValueTask ReportUpdatedAsync() => ReportDispositionAsync(Disposition.Updated);
    public ValueTask ReportSkippedAsync() => ReportDispositionAsync(Disposition.Skipped);
    public ValueTask ReportFailedAsync() => ReportDispositionAsync(Disposition.Failed);

    public ValueTask ReportLogAsync(ProcessingLogLevel level, string message, CancellationToken cancellationToken = default) => ExecuteAsync(() =>
    {
        EnsureEligible();
        return new LogEmitted(Request, level, message);
    }, null, cancellationToken, false);

    public async ValueTask<IAsyncDisposable> BeginActivityAsync(string label, CancellationToken cancellationToken = default)
    {
        var scope = new ActivityScope(this, Guid.NewGuid());
        await ExecuteAsync(() =>
        {
            EnsureEligible();
            return new ActivityStarted(Request, scope.Id, label);
        }, () => _activities.Add(scope.Id, scope), cancellationToken, false).ConfigureAwait(false);
        return scope;
    }

    public ValueTask FinishAsync(ProcessingRunResult result) => ExecuteAsync(() =>
    {
        EnsureNotFinishedOrBroken();
        ArgumentNullException.ThrowIfNull(result);
        if (!ReferenceEquals(result.Request, Request) || result.StartedAtUtc != _startedAtUtc)
        {
            throw new ArgumentException("The terminal result must match this session.", nameof(result));
        }

        if (!_eligibilityDetermined && result.Outcome == ProcessingRunOutcome.Completed)
        {
            throw new InvalidOperationException("A completed result requires eligibility.");
        }

        if (result.UpdatedCount != _updated || result.SkippedCount != _skipped || result.FailedCount != _failed || result.ProcessedCount > _eligibleCount)
        {
            throw new ArgumentException("The terminal result counts must match this session and eligibility.", nameof(result));
        }

        return new RunFinished(Request, result);
    }, () => _finished = true, CancellationToken.None, true);

    private ValueTask ReportDispositionAsync(Disposition disposition)
    {
        return ExecuteAsync(() =>
        {
            EnsureEligible();
            var updated = _updated + (disposition == Disposition.Updated ? 1 : 0);
            var skipped = _skipped + (disposition == Disposition.Skipped ? 1 : 0);
            var failed = _failed + (disposition == Disposition.Failed ? 1 : 0);
            var processed = checked(updated + skipped + failed);
            if (processed > _eligibleCount)
            {
                throw new InvalidOperationException("Per-asset dispositions must not exceed eligibility.");
            }

            _updated = updated;
            _skipped = skipped;
            _failed = failed;
            return new ProgressChanged(Request, new ProcessingProgress(processed, updated, skipped, failed));
        }, null, CancellationToken.None, false);
    }

    private async ValueTask EndActivityAsync(ActivityScope scope)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_finished || _broken || !_activities.Remove(scope.Id))
            {
                scope.CloseLocally();
                return;
            }

            try
            {
                await AcceptOrBreakAsync(new ActivityEnded(Request, scope.Id), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                scope.CloseLocally();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask ExecuteAsync(Func<ProcessingEvent?> createEvent, Action? afterAccepted, CancellationToken cancellationToken, bool finish)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotFinishedOrBroken();
            var processingEvent = createEvent();
            if (processingEvent is null)
            {
                return;
            }

            if (finish)
            {
                foreach (var scope in _activities.Values)
                {
                    await AcceptOrBreakAsync(new ActivityEnded(Request, scope.Id), CancellationToken.None).ConfigureAwait(false);
                    scope.CloseLocally();
                }

                _activities.Clear();
            }

            await AcceptOrBreakAsync(processingEvent, cancellationToken).ConfigureAwait(false);
            afterAccepted?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask AcceptOrBreakAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _accept(processingEvent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _broken = true;
            foreach (var scope in _activities.Values)
            {
                scope.CloseLocally();
            }

            _activities.Clear();
            throw;
        }
    }

    private void EnsureEligible()
    {
        EnsureNotFinishedOrBroken();
        if (!_eligibilityDetermined)
        {
            throw new InvalidOperationException("Eligibility must be determined first.");
        }
    }

    private void EnsureNotFinishedOrBroken()
    {
        if (_broken)
        {
            throw new InvalidOperationException("The processing event session is broken.");
        }

        if (_finished)
        {
            throw new InvalidOperationException("The processing event session has finished.");
        }
    }

    private enum Disposition { Updated, Skipped, Failed }

    private sealed class ActivityScope(ProcessingRunEventSession session, Guid id) : IAsyncDisposable
    {
        private int _closed;
        public Guid Id { get; } = id;
        public ValueTask DisposeAsync() => Volatile.Read(ref _closed) == 0 ? session.EndActivityAsync(this) : ValueTask.CompletedTask;
        public void CloseLocally() => Interlocked.Exchange(ref _closed, 1);
    }
}
