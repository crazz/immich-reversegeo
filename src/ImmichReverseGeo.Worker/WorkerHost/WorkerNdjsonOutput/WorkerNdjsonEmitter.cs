using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Processing;
using ImmichReverseGeo.Core.WorkerJobs;
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
    private readonly InternalWorkerProtocolVersion _protocolVersion;
    private readonly WorkerJobReadyPayload? _jobReadyPayload;
    private readonly Channel<EmissionCandidate> _queue;
    private readonly WorkerProtocolEventStreamValidator _validator = new();
    private WorkerJobOutputStreamValidator? _jobValidator;
    private WorkerJobOutputMessage? _jobReady;
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
        int queueCapacity = ProductionQueueCapacity,
        InternalWorkerProtocolVersion protocolVersion = InternalWorkerProtocolVersion.V1,
        WorkerJobReadyPayload? jobReadyPayload = null)
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
        if (!Enum.IsDefined(protocolVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        if ((protocolVersion == InternalWorkerProtocolVersion.V2) != (jobReadyPayload is not null))
        {
            throw new ArgumentException(
                "Protocol v2 requires registered job-kind metadata and protocol v1 must not receive it.",
                nameof(jobReadyPayload));
        }

        _protocolVersion = protocolVersion;
        _jobReadyPayload = jobReadyPayload;
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

    internal static WorkerNdjsonEmitter CreateProduction(
        IWorkerNdjsonOutputStreamFactory stdoutFactory,
        TimeProvider timeProvider,
        ILogger<WorkerNdjsonEmitter> logger,
        WorkerProcessExitOutcomeAccumulator outcomes,
        InternalWorkerProtocolVersion protocolVersion,
        WorkerJobReadyPayload? jobReadyPayload)
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
                outcomes,
                ProductionQueueCapacity,
                protocolVersion,
                jobReadyPayload);
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
                outcomes,
                ProductionQueueCapacity,
                protocolVersion,
                jobReadyPayload);
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
        if (_protocolVersion != InternalWorkerProtocolVersion.V1)
        {
            throw new InvalidOperationException("Processing protocol output requires protocol v1.");
        }

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

    internal ValueTask SubmitJobStartedAsync(
        WorkerJobContext context,
        string trigger,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SubmitJobOutputAsync(
            context,
            new WorkerJobStartedPayload(trigger, startedAtUtc),
            startedAtUtc,
            isJobStarted: true,
            isTerminal: false,
            cancellationToken);
    }

    internal ValueTask SubmitJobEventAsync(
        WorkerJobContext context,
        WorkerJobHandlerEvent @event,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return SubmitJobOutputAsync(
            context,
            @event.Payload,
            @event.TimestampUtc,
            isJobStarted: false,
            isTerminal: false,
            cancellationToken);
    }

    internal ValueTask SubmitJobTerminalAsync(
        WorkerJobContext context,
        WorkerJobTerminalPayload terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return SubmitJobOutputAsync(
            context,
            terminal,
            terminal.EndedAtUtc,
            isJobStarted: false,
            isTerminal: true,
            cancellationToken);
    }

    private async ValueTask SubmitJobOutputAsync(
        WorkerJobContext context,
        WorkerJobOutputPayload payload,
        DateTimeOffset timestampUtc,
        bool isJobStarted,
        bool isTerminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);
        if (_protocolVersion != InternalWorkerProtocolVersion.V2)
        {
            throw new InvalidOperationException("Typed worker-job output requires protocol v2.");
        }

        try
        {
            var candidate = new EmissionCandidate(context, payload, timestampUtc);
            await EnqueueAsync(
                candidate,
                requiresReady: true,
                isRunStarted: isJobStarted,
                isTerminal,
                cancellationToken).ConfigureAwait(false);
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
        byte[] json;
        try
        {
            json = _protocolVersion == InternalWorkerProtocolVersion.V1
                ? CreateAndValidateV1(candidate, sequence)
                : CreateAndValidateV2(candidate, sequence);
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
        if (candidate.IsReady)
        {
            lock (_stateGate)
            {
                _readyFlushed = true;
            }
        }

        if (candidate.IsTerminal)
        {
            _queue.Writer.TryComplete();
        }
    }

    private byte[] CreateAndValidateV1(EmissionCandidate candidate, long sequence)
    {
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

        byte[] json = WorkerProtocolCodec.Serialize(@event);
        if (!_validator.Validate(@event).IsSuccess)
        {
            throw Break(WorkerNdjsonFailureStage.Validation);
        }

        return json;
    }

    private byte[] CreateAndValidateV2(EmissionCandidate candidate, long sequence)
    {
        WorkerJobOutputMessage message;
        try
        {
            if (candidate.IsReady)
            {
                message = WorkerJobProtocolMapper.Ready(
                    sequence,
                    _timeProvider.GetUtcNow(),
                    _jobReadyPayload!);
                _jobReady = message;
            }
            else if (candidate.JobPayload is WorkerJobStartedPayload started)
            {
                message = WorkerJobProtocolMapper.JobStarted(
                    candidate.JobContext!,
                    started.Trigger,
                    started.StartedAtUtc,
                    sequence);
                _jobValidator = new WorkerJobOutputStreamValidator(
                    candidate.JobContext!.JobId,
                    candidate.JobContext.JobKind);
                if (_jobReady is null || !_jobValidator.Validate(_jobReady).IsSuccess)
                {
                    throw new InvalidOperationException();
                }
            }
            else if (candidate.JobPayload is WorkerJobTerminalPayload terminal)
            {
                message = WorkerJobProtocolMapper.Terminal(
                    candidate.JobContext!,
                    terminal,
                    sequence);
            }
            else
            {
                message = WorkerJobProtocolMapper.Map(
                    candidate.JobContext!,
                    new WorkerJobHandlerEvent(candidate.JobTimestampUtc!.Value, candidate.JobPayload!),
                    sequence);
            }
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            throw Break(WorkerNdjsonFailureStage.Mapping);
        }

        if (message.Type != WorkerJobProtocolV2.ReadyType
            && (_jobValidator is null || !_jobValidator.Validate(message).IsSuccess))
        {
            throw Break(WorkerNdjsonFailureStage.Validation);
        }

        return WorkerJobProtocolCodec.Serialize(message);
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

        bool protocolIncomplete = _protocolVersion == InternalWorkerProtocolVersion.V1
            ? !_validator.FinalizeStream().IsComplete
            : _jobValidator is not null
                && _jobValidator.FinalizeOutput(hasPartialFrame: false) is not null;
        if (failure is null && protocolIncomplete)
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

        internal EmissionCandidate(
            WorkerJobContext jobContext,
            WorkerJobOutputPayload jobPayload,
            DateTimeOffset jobTimestampUtc)
        {
            JobContext = jobContext;
            JobPayload = jobPayload;
            JobTimestampUtc = jobTimestampUtc;
        }

        internal ProcessingEvent? ProcessingEvent { get; }
        internal WorkerJobContext? JobContext { get; }
        internal WorkerJobOutputPayload? JobPayload { get; }
        internal DateTimeOffset? JobTimestampUtc { get; }
        internal bool IsReady => ProcessingEvent is null && JobPayload is null;
        internal bool IsTerminal => ProcessingEvent is RunFinished || JobPayload is WorkerJobTerminalPayload;
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
