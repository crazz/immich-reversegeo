using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.WorkerHost;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.WorkerHost.WorkerNdjsonOutput;

/// <summary>
/// Owns the worker's managed stdout protocol stream.
/// </summary>
internal sealed class WorkerNdjsonEmitter : IWorkerReadinessPublisher, IAsyncDisposable
{
    internal const int ProductionQueueCapacity = 256;

    private readonly Stream _stdout;
    private readonly WorkerNdjsonOutputStreamOwnership _stdoutOwnership;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkerNdjsonEmitter> _logger;
    private readonly WorkerProcessExitOutcomeAccumulator _outcomes;
    private readonly Channel<EmissionCandidate> _queue;
    private readonly WorkerProtocolEventStreamValidator _validator = new();
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Task _writer;
    private InitializationAttempt? _initialization;
    private OutOfMemoryException? _fatalOutOfMemory;
    private WorkerNdjsonTransportException? _broken;
    private Task? _disposeTask;
    private bool _readyFlushed;
    private bool _intakeClosed;
    private bool _runStartedAccepted;
    private bool _terminalAccepted;
    private long _nextSequence;

    internal WorkerNdjsonEmitter(
        Stream stdout,
        WorkerNdjsonOutputStreamOwnership stdoutOwnership,
        TimeProvider timeProvider,
        ILogger<WorkerNdjsonEmitter> logger,
        WorkerProcessExitOutcomeAccumulator outcomes,
        int queueCapacity = ProductionQueueCapacity)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);

        _stdout = stdout;
        _stdoutOwnership = stdoutOwnership;
        _timeProvider = timeProvider;
        _logger = logger;
        _outcomes = outcomes;
        _queue = Channel.CreateBounded<EmissionCandidate>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _writer = ConsumeAsync();
    }

    internal static WorkerNdjsonEmitter CreateProduction(
        IWorkerNdjsonOutputStreamFactory stdoutFactory,
        TimeProvider timeProvider,
        ILogger<WorkerNdjsonEmitter> logger,
        WorkerProcessExitOutcomeAccumulator outcomes)
    {
        ArgumentNullException.ThrowIfNull(stdoutFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(outcomes);

        try
        {
            return new WorkerNdjsonEmitter(
                stdoutFactory.OpenStandardOutput(),
                WorkerNdjsonOutputStreamOwnership.Unowned,
                timeProvider,
                logger,
                outcomes);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            var broken = new WorkerNdjsonEmitter(
                Stream.Null,
                WorkerNdjsonOutputStreamOwnership.Unowned,
                timeProvider,
                logger,
                outcomes);
            broken.Break(WorkerNdjsonFailureStage.OpenStandardOutput);
            return broken;
        }
    }

    public Task PublishAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            try
            {
                if (_fatalOutOfMemory is not null)
                {
                    return Task.FromException(_fatalOutOfMemory);
                }

                if (_broken is not null)
                {
                    return Task.FromException(_broken);
                }

                if (_initialization is not null)
                {
                    return _initialization.Task;
                }

                var attempt = new InitializationAttempt();
                _initialization = attempt;
                attempt.Task = PublishReadyAsync(attempt, cancellationToken);
                return attempt.Task;
            }
            catch (OutOfMemoryException outOfMemoryFailure)
            {
                throw FailFatally(outOfMemoryFailure);
            }
        }
    }

    private async Task PublishReadyAsync(InitializationAttempt attempt, CancellationToken cancellationToken)
    {
        try
        {
            var candidate = new EmissionCandidate();
            await EnqueueAsync(
                candidate,
                requiresReady: false,
                isRunStarted: false,
                isTerminal: false,
                cancellationToken).ConfigureAwait(false);
            await candidate.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_initialization, attempt))
                {
                    _initialization = null;
                }
            }

            throw;
        }
        catch (OutOfMemoryException outOfMemoryFailure)
        {
            throw FailFatally(outOfMemoryFailure);
        }
    }

    internal async ValueTask SubmitAsync(ProcessingEvent processingEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processingEvent);

        try
        {
            var candidate = new EmissionCandidate(processingEvent);
            await EnqueueAsync(
                candidate,
                requiresReady: true,
                isRunStarted: processingEvent is RunStarted,
                isTerminal: processingEvent is RunFinished,
                cancellationToken).ConfigureAwait(false);

            // An accepted candidate is committed; intentionally do not use the caller token here.
            await candidate.Completion.Task.ConfigureAwait(false);
        }
        catch (OutOfMemoryException outOfMemoryFailure)
        {
            throw FailFatally(outOfMemoryFailure);
        }
    }

    private async ValueTask EnqueueAsync(
        EmissionCandidate candidate,
        bool requiresReady,
        bool isRunStarted,
        bool isTerminal,
        CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            ThrowIfUnavailable(requiresReady);
        }

        using var cancellationRegistration = cancellationToken.UnsafeRegister(
            static state => ((CandidateCancellation)state!).Cancel(),
            new CandidateCancellation(this, candidate));
        try
        {
            while (await _queue.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_stateGate)
                {
                    ThrowIfUnavailable(requiresReady);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        candidate.CancelAdmission();
                    }

                    candidate.ThrowIfAdmissionCancelled(cancellationToken);
                    if (_queue.Writer.TryWrite(candidate))
                    {
                        candidate.TransferToWriter();
                        if (isRunStarted)
                        {
                            _runStartedAccepted = true;
                        }

                        if (isTerminal)
                        {
                            // Channel acceptance is the terminal intake linearization point.
                            _terminalAccepted = true;
                            _intakeClosed = true;
                        }

                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkerNdjsonTransportException)
        {
            throw;
        }
        catch (WorkerNdjsonOutputClosedException)
        {
            throw;
        }
        catch (OutOfMemoryException outOfMemoryFailure)
        {
            throw FailFatally(outOfMemoryFailure);
        }
        catch
        {
            throw GetUnavailableFailure();
        }

        throw GetUnavailableFailure();
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var candidate in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (GetUnavailableFailureOrNull() is { } failure)
                {
                    candidate.Completion.TrySetException(failure);
                    continue;
                }

                try
                {
                    await EmitAsync(candidate).ConfigureAwait(false);
                    candidate.Completion.TrySetResult();
                }
                catch (WorkerNdjsonTransportException transportFailure)
                {
                    candidate.Completion.TrySetException(transportFailure);
                }
                catch (OutOfMemoryException outOfMemoryFailure)
                {
                    var fatalFailure = FailFatally(outOfMemoryFailure);
                    candidate.Completion.TrySetException(fatalFailure);
                    throw fatalFailure;
                }
                catch
                {
                    candidate.Completion.TrySetException(Break(WorkerNdjsonFailureStage.Writer));
                }
            }
        }
        catch (OutOfMemoryException outOfMemoryFailure)
        {
            throw FailFatally(outOfMemoryFailure);
        }
        catch
        {
            Break(WorkerNdjsonFailureStage.Writer);
        }
        finally
        {
            var failure = GetUnavailableFailureOrNull();
            if (failure is not null)
            {
                while (_queue.Reader.TryRead(out var pending))
                {
                    pending.Completion.TrySetException(failure);
                }
            }
        }
    }

    private async Task EmitAsync(EmissionCandidate candidate)
    {
        var sequence = checked(_nextSequence + 1);
        WorkerProtocolEvent @event;
        try
        {
            @event = candidate.ProcessingEvent is null
                ? WorkerProtocolMapper.Ready(sequence, _timeProvider.GetUtcNow())
                : WorkerProtocolMapper.Map(candidate.ProcessingEvent, sequence, _timeProvider.GetUtcNow());
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            throw Break(WorkerNdjsonFailureStage.Mapping);
        }

        byte[] json;
        try
        {
            json = WorkerProtocolCodec.Serialize(@event);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw Break(WorkerNdjsonFailureStage.Size);
        }
        catch
        {
            throw Break(WorkerNdjsonFailureStage.Serialization);
        }

        try
        {
            if (!_validator.Validate(@event).IsSuccess)
            {
                throw new InvalidOperationException();
            }
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            throw Break(WorkerNdjsonFailureStage.Validation);
        }

        byte[] frame;
        try
        {
            frame = new byte[checked(json.Length + 1)];
            json.CopyTo(frame, 0);
            frame[^1] = (byte)'\n';
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            throw Break(WorkerNdjsonFailureStage.Framing);
        }

        try
        {
            await _stdout.WriteAsync(frame.AsMemory(), _lifetimeCancellation.Token).ConfigureAwait(false);
            if (GetUnavailableFailureOrNull() is { } writeFailure)
            {
                throw writeFailure;
            }
        }
        catch (WorkerNdjsonTransportException)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            throw Break(WorkerNdjsonFailureStage.Write);
        }

        try
        {
            await _stdout.FlushAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            if (GetUnavailableFailureOrNull() is { } flushFailure)
            {
                throw flushFailure;
            }
        }
        catch (WorkerNdjsonTransportException)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            throw Break(WorkerNdjsonFailureStage.Flush);
        }

        _nextSequence = sequence;
        if (candidate.ProcessingEvent is null)
        {
            lock (_stateGate)
            {
                _readyFlushed = true;
            }
        }

        if (candidate.ProcessingEvent is RunFinished)
        {
            _queue.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposeTask is null)
            {
                _intakeClosed = true;
                _disposeTask = DisposeCoreAsync(_runStartedAccepted || !_readyFlushed, _terminalAccepted);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(bool terminalRequired, bool terminalAccepted)
    {
        try
        {
            await DisposeCoreWithFatalFanoutAsync(terminalRequired, terminalAccepted).ConfigureAwait(false);
        }
        catch (OutOfMemoryException outOfMemoryFailure)
        {
            throw FailFatally(outOfMemoryFailure);
        }
    }

    private async Task DisposeCoreWithFatalFanoutAsync(bool terminalRequired, bool terminalAccepted)
    {
        WorkerNdjsonTransportException? failure = null;
        if (GetFatalOutOfMemoryOrNull() is null && terminalRequired && !terminalAccepted)
        {
            failure = Break(WorkerNdjsonFailureStage.Disposal);
            try
            {
                _lifetimeCancellation.Cancel();
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch
            {
                // The stable disposal failure remains authoritative.
            }
        }
        else if (!terminalAccepted)
        {
            _queue.Writer.TryComplete();
        }

        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            failure ??= Break(WorkerNdjsonFailureStage.Writer);
        }

        if (failure is null && !_validator.FinalizeStream().IsComplete)
        {
            failure = Break(WorkerNdjsonFailureStage.Disposal);
        }

        var disposalFailure = _stdoutOwnership == WorkerNdjsonOutputStreamOwnership.Owned
            ? await DisposeOutputSafelyAsync().ConfigureAwait(false)
            : null;
        failure ??= disposalFailure ?? GetBrokenFailureOrNull();
        _lifetimeCancellation.Dispose();

        if (failure is not null)
        {
            throw failure;
        }
    }

    private async Task<WorkerNdjsonTransportException?> DisposeOutputSafelyAsync()
    {
        try
        {
            await _stdout.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            return Break(WorkerNdjsonFailureStage.Disposal);
        }
    }

    private void CancelAdmission(EmissionCandidate candidate)
    {
        lock (_stateGate)
        {
            candidate.CancelAdmission();
        }
    }

    private void ThrowIfUnavailable(bool requiresReady)
    {
        if (_fatalOutOfMemory is not null)
        {
            throw _fatalOutOfMemory;
        }

        if (_broken is not null)
        {
            throw _broken;
        }

        if (requiresReady && !_readyFlushed)
        {
            throw new InvalidOperationException("worker-ndjson-ready-required");
        }

        if (_intakeClosed)
        {
            throw new WorkerNdjsonOutputClosedException();
        }
    }

    private WorkerNdjsonTransportException Break(WorkerNdjsonFailureStage stage)
    {
        WorkerNdjsonTransportException failure;
        var log = false;
        lock (_stateGate)
        {
            if (_broken is not null)
            {
                return _broken;
            }

            failure = new WorkerNdjsonTransportException(stage);
            _broken = failure;
            _intakeClosed = true;
            _queue.Writer.TryComplete(failure);
            log = true;
        }

        if (log)
        {
            _outcomes.Add(WorkerProcessExitFact.OutputTransport());

            try
            {
                _logger.LogWarning("worker-ndjson-output-failed stage={FailureStage}", stage);
            }
            catch
            {
            }
        }

        return failure;
    }

    private OutOfMemoryException FailFatally(OutOfMemoryException failure)
    {
        lock (_stateGate)
        {
            if (_fatalOutOfMemory is not null)
            {
                return _fatalOutOfMemory;
            }

            _fatalOutOfMemory = failure;
            _intakeClosed = true;
            _queue.Writer.TryComplete(failure);
            return failure;
        }
    }

    private Exception GetUnavailableFailure()
    {
        lock (_stateGate)
        {
            return _fatalOutOfMemory ?? (Exception?)_broken ?? new WorkerNdjsonOutputClosedException();
        }
    }

    private Exception? GetUnavailableFailureOrNull()
    {
        lock (_stateGate)
        {
            return _fatalOutOfMemory ?? (Exception?)_broken;
        }
    }

    private OutOfMemoryException? GetFatalOutOfMemoryOrNull()
    {
        lock (_stateGate)
        {
            return _fatalOutOfMemory;
        }
    }

    private WorkerNdjsonTransportException? GetBrokenFailureOrNull()
    {
        lock (_stateGate)
        {
            return _broken;
        }
    }

    private sealed class InitializationAttempt
    {
        internal Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed class CandidateCancellation(WorkerNdjsonEmitter owner, EmissionCandidate candidate)
    {
        internal void Cancel()
        {
            owner.CancelAdmission(candidate);
        }
    }

    private sealed class EmissionCandidate
    {
        private EmissionCandidateOwnership _ownership = EmissionCandidateOwnership.Producer;

        internal EmissionCandidate(ProcessingEvent? processingEvent = null)
        {
            ProcessingEvent = processingEvent;
        }

        internal ProcessingEvent? ProcessingEvent { get; }
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void CancelAdmission()
        {
            if (_ownership == EmissionCandidateOwnership.Producer)
            {
                _ownership = EmissionCandidateOwnership.Cancelled;
            }
        }

        internal void ThrowIfAdmissionCancelled(CancellationToken cancellationToken)
        {
            if (_ownership == EmissionCandidateOwnership.Cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        internal void TransferToWriter()
        {
            if (_ownership != EmissionCandidateOwnership.Producer)
            {
                throw new InvalidOperationException("worker-ndjson-invalid-candidate-ownership");
            }

            _ownership = EmissionCandidateOwnership.Writer;
        }
    }

    private enum EmissionCandidateOwnership
    {
        Producer,
        Writer,
        Cancelled
    }
}

internal enum WorkerNdjsonOutputStreamOwnership
{
    Unowned,
    Owned
}

internal enum WorkerNdjsonFailureStage
{
    OpenStandardOutput,
    Mapping,
    Validation,
    Serialization,
    Size,
    Framing,
    Write,
    Flush,
    Disposal,
    Writer
}

internal interface IWorkerNdjsonOutputStreamFactory
{
    Stream OpenStandardOutput();
}

internal sealed class WorkerNdjsonStandardOutputStreamFactory : IWorkerNdjsonOutputStreamFactory
{
    public Stream OpenStandardOutput()
    {
        return Console.OpenStandardOutput();
    }
}

internal sealed class WorkerNdjsonTransportException : InvalidOperationException
{
    internal WorkerNdjsonTransportException(WorkerNdjsonFailureStage stage)
        : base("worker-ndjson-output-failed")
    {
        Stage = stage;
    }

    internal WorkerNdjsonFailureStage Stage { get; }
}

internal sealed class WorkerNdjsonOutputClosedException : InvalidOperationException
{
    internal WorkerNdjsonOutputClosedException()
        : base("worker-ndjson-output-closed")
    {
    }
}
