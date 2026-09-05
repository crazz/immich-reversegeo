using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

internal sealed class ChildWorkerObserverArmingAcknowledgements
{
    private readonly TaskCompletionSource _standardOutput = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _standardError = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _all;

    internal ChildWorkerObserverArmingAcknowledgements()
    {
        _all = Task.WhenAll(_standardOutput.Task, _standardError.Task, _exit.Task);
    }

    internal Task StandardOutput => _standardOutput.Task;
    internal Task StandardError => _standardError.Task;
    internal Task Exit => _exit.Task;
    internal Task All => _all;

    internal void AcknowledgeStandardOutput() => _standardOutput.SetResult();
    internal void AcknowledgeStandardError() => _standardError.SetResult();
    internal void AcknowledgeExit() => _exit.SetResult();

    internal ValueTask<T> StartStandardOutputAndConsumeAfterAllObserversArmAsync<T>(
        Func<ValueTask<T>> startOperation) =>
        StartAndConsumeAfterAllObserversArmAsync(startOperation, AcknowledgeStandardOutput);

    internal ValueTask<T> StartStandardErrorAndConsumeAfterAllObserversArmAsync<T>(
        Func<ValueTask<T>> startOperation) =>
        StartAndConsumeAfterAllObserversArmAsync(startOperation, AcknowledgeStandardError);

    internal ValueTask<T> StartExitAndConsumeAfterAllObserversArmAsync<T>(
        Func<ValueTask<T>> startOperation) =>
        StartAndConsumeAfterAllObserversArmAsync(startOperation, AcknowledgeExit);

    private async ValueTask<T> StartAndConsumeAfterAllObserversArmAsync<T>(
        Func<ValueTask<T>> startOperation,
        Action acknowledgeObserver)
    {
        ValueTask<T> operation;
        try
        {
            operation = startOperation();
        }
        catch (Exception exception)
        {
            operation = ValueTask.FromException<T>(exception);
        }

        acknowledgeObserver();
        await _all.ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }
}

internal sealed partial class ChildWorkerSession : IAsyncDisposable
{
    private const int ReadBufferBytes = 4096;
    private readonly IChildProcess _process;
    private readonly Stream _standardInputStream;
    private readonly Stream _standardOutputStream;
    private readonly Stream _standardErrorStream;
    private readonly IWorkerProtocolEventSink _eventSink;
    private readonly ProcessingRunRequest _request;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _readyTimeout;
    private readonly TaskCompletionSource<ChildWorkerStartupObservation> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _observationGate = new();
    private readonly object _disposeGate = new();
    private readonly StandardErrorRing _standardError = new();
    private readonly Task<ChildWorkerStreamFinality> _standardOutputTask;
    private readonly Task<ChildWorkerStreamFinality> _standardErrorTask;
    private readonly Task<ExitObservation> _exitTask;
    private readonly Task<ChildWorkerCompletionObservation> _completion;
    private readonly Task<ChildWorkerCompletionObservation> _settlement;
    private readonly Task _readyDeadlineTask;
    private Task? _disposeTask;
    private ChildWorkerProtocolObservation? _firstProtocolObservation;
    private WorkerProtocolEvent? _terminal;
    private int _startupAuthority;
    private bool _sinkCallbackAdmitted;
    private bool _suppressCallbacks;

    private ChildWorkerSession(
        IChildProcess process,
        ProcessingRunRequest request,
        IWorkerProtocolEventSink eventSink,
        ChildWorkerLauncherOptions options,
        Task observerActivation,
        ChildWorkerObserverArmingAcknowledgements observerArming)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _timeProvider = options.TimeProvider;
        _readyTimeout = options.ReadyTimeout;

        ProcessId = process.ProcessId;
        _standardInputStream = process.StandardInput;
        _standardOutputStream = process.StandardOutput;
        _standardErrorStream = process.StandardError;
        RunId = request.RunId;

        _standardOutputTask = DrainStandardOutputAsync(observerActivation, observerArming);
        _standardErrorTask = DrainStandardErrorAsync(observerActivation, observerArming);
        _exitTask = ObserveExitAsync(observerActivation, observerArming);
        _completion = ObserveCompletionAsync();
        _settlement = ObserveSettledCompletionAsync();
        _readyDeadlineTask = ObserveReadyDeadlineAsync(observerActivation, observerArming.All);
    }

    internal static async ValueTask<ChildWorkerSession> CreateAsync(
        IChildProcess process,
        ProcessingRunRequest request,
        IWorkerProtocolEventSink eventSink,
        ChildWorkerLauncherOptions options,
        ChildWorkerObserverArmingAcknowledgements observerArming)
    {
        ArgumentNullException.ThrowIfNull(observerArming);
        var observerActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ChildWorkerSession(
            process,
            request,
            eventSink,
            options,
            observerActivation.Task,
            observerArming);

        observerActivation.SetResult();
        await observerArming.All.ConfigureAwait(false);
        return session;
    }

    internal int ProcessId { get; }
    internal Guid RunId { get; }
    internal Task<ChildWorkerStartupObservation> Startup => _startup.Task;
    internal Task<ChildWorkerCompletionObservation> Completion => _completion;
    internal Task<ChildWorkerCompletionObservation> Settlement => _settlement;
    internal Task<ChildWorkerStartupObservation> WaitForStartupAsync(CancellationToken cancellationToken = default) => Startup.WaitAsync(cancellationToken);
    internal Task<ChildWorkerCompletionObservation> WaitForCompletionAsync(CancellationToken cancellationToken = default) => Completion.WaitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        lock (_observationGate)
        {
            _suppressCallbacks = true;
        }

        TryCommitPreReady(ChildWorkerStartupObservation.Disposed.Instance);
        if (TryConfirmKnownExit(ChildWorkerCancellationExitRace.BeforeControl))
        {
            await _settlement.ConfigureAwait(false);
            return;
        }

        await RequestStop().ConfigureAwait(false);
    }

    private async Task ObserveReadyDeadlineAsync(Task observerActivation, Task observersArmed)
    {
        await observerActivation.ConfigureAwait(false);
        await observersArmed.ConfigureAwait(false);
        if (_readyTimeout == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        if (await WaitForTimeAsync(_timeProvider, _readyTimeout, _startup.Task).ConfigureAwait(false))
        {
            TryCommitPreReady(ChildWorkerStartupObservation.ReadyTimedOut.Instance);
        }
    }

    private async Task<ExitObservation> ObserveExitAsync(
        Task observerActivation,
        ChildWorkerObserverArmingAcknowledgements observerArming)
    {
        await observerActivation.ConfigureAwait(false);
        try
        {
            var code = await observerArming.StartExitAndConsumeAfterAllObserversArmAsync(
                () => new ValueTask<int>(_process.WaitForExitAsync())).ConfigureAwait(false);
            TryCommitPreReady(ChildWorkerStartupObservation.PreReadyExit.Instance);
            ConfirmProcessExit();
            return new ExitObservation(true, code);
        }
        catch
        {
            TryCommitPreReady(ChildWorkerStartupObservation.PreReadyExitObservationFailed.Instance);
            if (GetExitState() == ChildProcessExitState.Exited)
            {
                ConfirmProcessExit();
            }

            return new ExitObservation(false, null);
        }
    }

    private async Task<ChildWorkerCompletionObservation> ObserveCompletionAsync()
    {
        var exit = await _exitTask.ConfigureAwait(false);
        var standardOutputFinality = await _standardOutputTask.ConfigureAwait(false);
        var standardErrorFinality = await _standardErrorTask.ConfigureAwait(false);
        var startup = await _startup.Task.ConfigureAwait(false);
        lock (_observationGate)
        {
            return new ChildWorkerCompletionObservation(ProcessId, RunId, startup, exit.Observed, exit.Code, standardOutputFinality, standardErrorFinality, _terminal, _firstProtocolObservation, _standardError.Snapshot());
        }
    }

    private async Task<ChildWorkerStreamFinality> DrainStandardOutputAsync(
        Task observerActivation,
        ChildWorkerObserverArmingAcknowledgements observerArming)
    {
        await observerActivation.ConfigureAwait(false);
        var reader = new StandardOutputFrameReader();
        var validator = new WorkerProtocolEventStreamValidator();
        var stream = _standardOutputStream;
        var firstRead = observerArming.StartStandardOutputAndConsumeAfterAllObserversArmAsync(
            () => reader.StartRead(stream));
        try
        {
            ValueTask<int>? pendingRead = firstRead;
            while (true)
            {
                var result = pendingRead.HasValue
                    ? await reader.ReadAsync(stream, pendingRead.Value).ConfigureAwait(false)
                    : await reader.ReadAsync(stream).ConfigureAwait(false);
                pendingRead = null;
                if (result.Kind is StandardOutputReadKind.EndOfStream)
                {
                    TryCommitPreReady(ChildWorkerStartupObservation.PreReadyEndOfStream.Instance);
                    if (!reader.Failed && !validator.FinalizeStream().IsComplete)
                    {
                        RecordProtocolFailure(new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidLifecycle, "Protocol stream is incomplete."));
                    }

                    return ChildWorkerStreamFinality.EndOfStream.Instance;
                }

                if (result.Kind is StandardOutputReadKind.FramingFailure)
                {
                    RecordProtocolFailure(new WorkerProtocolFailure(result.FailureCode, "The standard-output frame was invalid."));
                    reader.StopParsing();
                    continue;
                }

                if (reader.Failed)
                {
                    continue;
                }

                var parsed = WorkerProtocolCodec.Parse(result.Frame.Span);
                if (!parsed.IsSuccess)
                {
                    RecordProtocolFailure(parsed.Failure!);
                    reader.StopParsing();
                    continue;
                }

                var @event = parsed.Event!;
                if ((@event.Type == WorkerProtocolV1.ReadyType && @event.RunId is not null)
                    || (@event.Type != WorkerProtocolV1.ReadyType && @event.RunId != _request.RunId))
                {
                    RecordProtocolFailure(new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidCorrelation, "Event correlation did not match this session."));
                    reader.StopParsing();
                    continue;
                }

                var validated = validator.Validate(@event);
                if (!validated.IsSuccess)
                {
                    RecordProtocolFailure(validated.Failure!);
                    reader.StopParsing();
                    continue;
                }

                if (WorkerProtocolV1.IsTerminal(@event.Type))
                {
                    lock (_observationGate)
                    {
                        _terminal = @event;
                    }
                }

                if (!TryAdmitSinkCallback())
                {
                    continue;
                }

                if (!await DeliverAdmittedEventAsync(@event).ConfigureAwait(false))
                {
                    continue;
                }

                if (@event.Type == WorkerProtocolV1.ReadyType && TryReserveReady())
                {
                    await ExecuteOnceAsync().ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryCommitPreReady(ChildWorkerStartupObservation.PreReadyReadFailed.Instance);
            return ChildWorkerStreamFinality.ReadFailed.Instance;
        }
    }

    private async Task ExecuteOnceAsync()
    {
        byte[] objectBytes;
        try
        {
            var message = new WorkerProtocolControllerMessage(
                WorkerProtocolV1.RequestCategory,
                WorkerProtocolV1.ExecuteType,
                1,
                _timeProvider.GetUtcNow(),
                _request.RunId,
                new ExecuteRequestPayload(_request));
            objectBytes = WorkerProtocolCodec.SerializeControllerInput(message);
        }
        catch
        {
            _startup.TrySetResult(ChildWorkerStartupObservation.RequestSerializationFailed.Instance);
            return;
        }

        var frame = new byte[objectBytes.Length + 1];
        objectBytes.CopyTo(frame, 0);
        frame[^1] = (byte)10;

        var observation = await WriteExecuteFrameAsync(frame).ConfigureAwait(false);
        _startup.TrySetResult(observation);
    }

    private async Task<ChildWorkerStreamFinality> DrainStandardErrorAsync(
        Task observerActivation,
        ChildWorkerObserverArmingAcknowledgements observerArming)
    {
        await observerActivation.ConfigureAwait(false);
        var buffer = new byte[ReadBufferBytes];
        var stream = _standardErrorStream;
        var read = observerArming.StartStandardErrorAndConsumeAfterAllObserversArmAsync(
            () => stream.ReadAsync(buffer.AsMemory(), CancellationToken.None));
        try
        {
            while (true)
            {
                var count = await read.ConfigureAwait(false);
                if (count == 0)
                {
                    return ChildWorkerStreamFinality.EndOfStream.Instance;
                }

                lock (_observationGate)
                {
                    _standardError.Append(buffer.AsSpan(0, count));
                }

                read = stream.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            }
        }
        catch
        {
            return ChildWorkerStreamFinality.ReadFailed.Instance;
        }
    }

    private bool TryAdmitSinkCallback()
    {
        lock (_observationGate)
        {
            if (_suppressCallbacks
                || _sinkCallbackAdmitted
                || _firstProtocolObservation is ChildWorkerProtocolObservation.SinkFailure)
            {
                return false;
            }

            _sinkCallbackAdmitted = true;
            return true;
        }
    }

    private async Task<bool> DeliverAdmittedEventAsync(WorkerProtocolEvent @event)
    {
        try
        {
            await _eventSink.AcceptAsync(@event, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            RecordSinkFailure();
            return false;
        }
        finally
        {
            lock (_observationGate)
            {
                _sinkCallbackAdmitted = false;
            }
        }
    }

    private bool TryReserveReady()
    {
        lock (_observationGate)
        {
            if (_suppressCallbacks)
            {
                return false;
            }

            return Interlocked.CompareExchange(ref _startupAuthority, 1, 0) == 0;
        }
    }

    private void TryCommitPreReady(ChildWorkerStartupObservation observation)
    {
        if (Interlocked.CompareExchange(ref _startupAuthority, 2, 0) == 0)
        {
            _startup.TrySetResult(observation);
        }
    }

    private void RecordProtocolFailure(WorkerProtocolFailure failure)
    {
        lock (_observationGate)
        {
            _firstProtocolObservation ??= new ChildWorkerProtocolObservation.ProtocolFailure(failure);
        }

        TryCommitPreReady(new ChildWorkerStartupObservation.ProtocolFailure(failure));
    }

    private void RecordSinkFailure()
    {
        lock (_observationGate)
        {
            _firstProtocolObservation ??= ChildWorkerProtocolObservation.SinkFailure.Instance;
            _suppressCallbacks = true;
        }

        TryCommitPreReady(ChildWorkerStartupObservation.SinkFailed.Instance);
    }


    private static async Task<bool> WaitForTimeAsync(TimeProvider timeProvider, TimeSpan dueTime, Task startup)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ITimer timer = timeProvider.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            dueTime,
            Timeout.InfiniteTimeSpan);

        try
        {
            return await Task.WhenAny(completion.Task, startup).ConfigureAwait(false) == completion.Task;
        }
        finally
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task DisposeStreamAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private readonly record struct ExitObservation(bool Observed, int? Code);
}
