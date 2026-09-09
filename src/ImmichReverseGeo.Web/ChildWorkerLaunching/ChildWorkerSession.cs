using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
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
    private const string TerminalPreventingObservationTimestampFailureDiagnostic =
        "The terminal-preventing observation timestamp was unavailable.";
    private readonly IChildProcess _process;
    private readonly Stream _standardInputStream;
    private readonly Stream _standardOutputStream;
    private readonly Stream _standardErrorStream;
    private readonly IWorkerJobEventSink _eventSink;
    private readonly WorkerJobDispatch _dispatch;
    private readonly ProcessingRunRequest _request;
    private readonly bool _isCancellable;
    private readonly InternalWorkerProtocolVersion _protocolVersion;
    private readonly WorkerJobOutputStreamValidator? _jobOutputValidator;
    private readonly ProcessAssetsWorkerJobProjection? _jobProjection;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _readyTimeout;
    private readonly TaskCompletionSource<ChildWorkerStartupObservation> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _observationGate = new();
    private readonly object _terminalPreventingObservationGate = new();
    private readonly object _disposeGate = new();
    private readonly StandardErrorRing _standardError = new();
    private readonly Task<ChildWorkerStreamFinality> _standardOutputTask;
    private readonly Task<ChildWorkerStreamFinality> _standardErrorTask;
    private readonly Task<ExitObservation> _exitTask;
    private readonly Task<ChildWorkerCompletionObservation> _completion;
    private readonly Task<ChildWorkerCompletionObservation> _evidenceFinality;
    private readonly Task<ChildWorkerCompletionObservation> _settlement;
    private readonly Task _readyDeadlineTask;
    private Task? _disposeTask;
    private readonly TaskCompletionSource<ChildWorkerTerminalPreventingObservation> _firstTerminalPreventingObservation =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ChildWorkerTerminalPreventingObservation> _terminalInputCloseFailure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ChildWorkerEvidenceFinalityGate? _evidenceFinalityGate;
    private ChildWorkerProtocolObservation? _firstProtocolObservation;
    private WorkerProtocolEvent? _terminal;
    private WorkerJobOutputMessage? _jobTerminal;
    private bool _acceptedRunStarted;
    private int _startupAuthority;
    private bool _sinkCallbackAdmitted;
    private bool _suppressCallbacks;

    private ChildWorkerSession(
        IChildProcess process,
        WorkerJobDispatch dispatch,
        IWorkerJobEventSink eventSink,
        ChildWorkerLauncherOptions options,
        Task observerActivation,
        ChildWorkerObserverArmingAcknowledgements observerArming,
        InternalWorkerProtocolVersion protocolVersion)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        ArgumentNullException.ThrowIfNull(dispatch);
        _dispatch = dispatch;
        _request = dispatch is ProcessAssetsWorkerJobDispatch processAssets
            ? processAssets.Request.ProcessingRequest
            : new ProcessingRunRequest(dispatch.Context.JobId, ProcessingRunTrigger.Manual);
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _isCancellable = dispatch.IsCancellable;
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!Enum.IsDefined(protocolVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        _protocolVersion = protocolVersion;
        if (protocolVersion == InternalWorkerProtocolVersion.V2)
        {
            _jobOutputValidator = new WorkerJobOutputStreamValidator(
                dispatch.Context.JobId,
                dispatch.Context.JobKind,
                dispatch switch
                {
                    ProcessAssetsWorkerJobDispatch processingDispatch => processingDispatch.Request,
                    CoordinateLookupWorkerJobDispatch coordinateLookup => coordinateLookup.Request,
                    CacheMutationWorkerJobDispatch cacheMutation => cacheMutation.Request,
                    _ => null
                });
        }
        _jobProjection = dispatch is ProcessAssetsWorkerJobDispatch
            ? new ProcessAssetsWorkerJobProjection(_request)
            : null;

        _timeProvider = options.TimeProvider;
        _readyTimeout = options.ReadyTimeout;
        _evidenceFinalityGate = options.EvidenceFinalityGate;

        ChildProcessResources resources = CaptureProcessResources(process);
        ProcessId = resources.ProcessId;
        _standardInputStream = resources.StandardInput;
        _standardOutputStream = resources.StandardOutput;
        _standardErrorStream = resources.StandardError;
        JobId = dispatch.Context.JobId;
        JobKind = dispatch.Context.JobKind;

        if (resources.SetupFailed)
        {
            _startupAuthority = 2;
            _startup.SetResult(
                ChildWorkerStartupObservation.PostStartSetupFailed.Instance);
            _suppressCallbacks = true;
            PublishTerminalPreventingObservation(
                ChildWorkerFaultContainmentReason.PostStartSetupFailed.Instance);
        }

        _standardOutputTask = DrainStandardOutputAsync(observerActivation, observerArming);
        _standardErrorTask = DrainStandardErrorAsync(observerActivation, observerArming);
        _exitTask = ObserveExitAsync(observerActivation, observerArming);
        _completion = ObserveCompletionAsync();
        _evidenceFinality = ObserveEvidenceFinalityAsync();
        _settlement = ObserveSettledCompletionAsync();
        _readyDeadlineTask = ObserveReadyDeadlineAsync(observerActivation, observerArming.All);
    }

    private static ChildProcessResources CaptureProcessResources(
        IChildProcess process)
    {
        var setupFailed = false;
        int processId;
        try
        {
            processId = process.ProcessId;
        }
        catch
        {
            setupFailed = true;
            processId = 0;
        }

        Stream standardInput = CaptureProcessStream(
            () => process.StandardInput,
            ref setupFailed);
        Stream standardOutput = CaptureProcessStream(
            () => process.StandardOutput,
            ref setupFailed);
        Stream standardError = CaptureProcessStream(
            () => process.StandardError,
            ref setupFailed);
        return new ChildProcessResources(
            processId,
            standardInput,
            standardOutput,
            standardError,
            setupFailed);
    }

    private static Stream CaptureProcessStream(
        Func<Stream> capture,
        ref bool setupFailed)
    {
        try
        {
            return capture()
                ?? throw new InvalidOperationException(
                    "The child process stream was unavailable after start.");
        }
        catch
        {
            setupFailed = true;
            return new UnavailableChildProcessStream();
        }
    }

    private readonly record struct ChildProcessResources(
        int ProcessId,
        Stream StandardInput,
        Stream StandardOutput,
        Stream StandardError,
        bool SetupFailed);

    private sealed class UnavailableChildProcessStream : Stream
    {
        private const string FailureDiagnostic =
            "The child process stream was unavailable after start.";

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw CreateFailure();

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.FromException(CreateFailure());

        public override int Read(byte[] buffer, int offset, int count)
            => throw CreateFailure();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => Task.FromException<int>(CreateFailure());

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(CreateFailure());

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw CreateFailure();

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => Task.FromException(CreateFailure());

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException(CreateFailure());

        private static IOException CreateFailure() => new(FailureDiagnostic);
    }

    internal static async ValueTask<ChildWorkerSession> CreateAsync(
        IChildProcess process,
        ProcessingRunRequest request,
        IWorkerProtocolEventSink eventSink,
        ChildWorkerLauncherOptions options,
        ChildWorkerObserverArmingAcknowledgements observerArming)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);
        return await CreateAsync(
            process,
            new ProcessAssetsWorkerJobDispatch(request),
            new ProcessAssetsWorkerJobEventSink(request, eventSink),
            options,
            observerArming,
            InternalWorkerProtocolVersion.V1).ConfigureAwait(false);
    }

    internal static async ValueTask<ChildWorkerSession> CreateAsync(
        IChildProcess process,
        ProcessingRunRequest request,
        IWorkerProtocolEventSink eventSink,
        ChildWorkerLauncherOptions options,
        ChildWorkerObserverArmingAcknowledgements observerArming,
        InternalWorkerProtocolVersion protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);
        return await CreateAsync(
            process,
            new ProcessAssetsWorkerJobDispatch(request),
            new ProcessAssetsWorkerJobEventSink(request, eventSink),
            options,
            observerArming,
            protocolVersion).ConfigureAwait(false);
    }

    internal static async ValueTask<ChildWorkerSession> CreateAsync(
        IChildProcess process,
        WorkerJobDispatch dispatch,
        IWorkerJobEventSink eventSink,
        ChildWorkerLauncherOptions options,
        ChildWorkerObserverArmingAcknowledgements observerArming,
        InternalWorkerProtocolVersion protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(observerArming);
        var observerActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ChildWorkerSession(
            process,
            dispatch,
            eventSink,
            options,
            observerActivation.Task,
            observerArming,
            protocolVersion);

        observerActivation.SetResult();
        await observerArming.All.ConfigureAwait(false);
        return session;
    }

    internal int ProcessId { get; }
    internal Guid JobId { get; }
    internal Guid RunId => JobId;
    internal WorkerJobKind JobKind { get; }
    internal bool IsCancellable => _isCancellable;
    internal InternalWorkerProtocolVersion ProtocolVersion => _protocolVersion;
    internal Task<ChildWorkerStartupObservation> Startup => _startup.Task;
    internal Task<ChildWorkerCompletionObservation> Completion => _completion;
    internal Task<ChildWorkerCompletionObservation> EvidenceFinality => _evidenceFinality;
    internal Task<ChildWorkerCompletionObservation> Settlement => _settlement;
    internal Task<ChildWorkerTerminalPreventingObservation> FirstTerminalPreventingObservation
        => _firstTerminalPreventingObservation.Task;
    internal Task<ChildWorkerTerminalPreventingObservation> TerminalInputCloseFailure
        => _terminalInputCloseFailure.Task;
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

        if (await WaitForTimeAsync(_timeProvider, _readyTimeout, _startup.Task).ConfigureAwait(false)
            && TryCommitPreReady(ChildWorkerStartupObservation.ReadyTimedOut.Instance))
        {
            PublishTerminalPreventingObservation(
                ChildWorkerFaultContainmentReason.ReadyTimedOut.Instance);
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
            var failedBeforeReady = TryCommitPreReady(
                ChildWorkerStartupObservation.PreReadyExitObservationFailed.Instance);
            if (GetExitState() == ChildProcessExitState.Exited)
            {
                ConfirmProcessExit();
            }
            else if (failedBeforeReady)
            {
                PublishTerminalPreventingObservation(
                    ChildWorkerFaultContainmentReason.ExitObservationFailed.Instance);
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
            return new ChildWorkerCompletionObservation(
                ProcessId,
                JobId,
                startup,
                exit.Observed,
                exit.Code,
                standardOutputFinality,
                standardErrorFinality,
                _terminal,
                _jobTerminal,
                _firstProtocolObservation,
                _standardError.Snapshot(),
                JobKind,
                ProtocolVersion)
            {
                AcceptedRunStarted = _acceptedRunStarted
            };
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
                    TryCommitPreReady(
                        ChildWorkerStartupObservation.PreReadyEndOfStream.Instance);
                    if (!reader.Failed)
                    {
                        WorkerProtocolFailure? finalizationFailure =
                            FinalizeOutputProtocol(validator);
                        if (finalizationFailure is not null)
                        {
                            RecordProtocolFailure(finalizationFailure);
                        }
                    }

                    return ChildWorkerStreamFinality.EndOfStream.Instance;
                }

                if (result.Kind is StandardOutputReadKind.FramingFailure)
                {
                    RecordProtocolFailure(new WorkerProtocolFailure(
                        result.FailureCode,
                        "The standard-output frame was invalid."));
                    reader.StopParsing();
                    continue;
                }

                if (reader.Failed)
                {
                    continue;
                }

                if (!TryParseAndValidateOutput(
                        result.Frame.Span,
                        validator,
                        out WorkerJobOutputMessage? jobEvent,
                        out WorkerProtocolEvent? @event,
                        out WorkerProtocolFailure? failure))
                {
                    RecordProtocolFailure(failure!);
                    reader.StopParsing();
                    continue;
                }

                bool isTerminal = _protocolVersion == InternalWorkerProtocolVersion.V1
                    ? WorkerProtocolV1.IsTerminal(@event!.Type)
                    : jobEvent!.Type == WorkerJobProtocolV2.TerminalType;
                if (isTerminal)
                {
                    lock (_observationGate)
                    {
                        _terminal = @event;
                        _jobTerminal = jobEvent;
                    }

                    StartTerminalInputClose();
                }

                if (!TryAdmitSinkCallback())
                {
                    continue;
                }

                if (!await DeliverAdmittedEventAsync(jobEvent!, @event).ConfigureAwait(false))
                {
                    continue;
                }

                if (jobEvent!.Type == WorkerJobProtocolV2.JobStartedType)
                {
                    lock (_observationGate)
                    {
                        _acceptedRunStarted = true;
                    }
                }

                if (jobEvent.Type == WorkerJobProtocolV2.ReadyType && TryReserveReady())
                {
                    await ExecuteOnceAsync().ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryCommitPreReady(ChildWorkerStartupObservation.PreReadyReadFailed.Instance);
            PublishTerminalPreventingObservation(
                ChildWorkerFaultContainmentReason.StandardOutputReadFailed.Instance);
            return ChildWorkerStreamFinality.ReadFailed.Instance;
        }
    }

    private async Task ExecuteOnceAsync()
    {
        byte[] objectBytes;
        try
        {
            objectBytes = _protocolVersion == InternalWorkerProtocolVersion.V1
                ? WorkerProtocolCodec.SerializeControllerInput(
                    new WorkerProtocolControllerMessage(
                        WorkerProtocolV1.RequestCategory,
                        WorkerProtocolV1.ExecuteType,
                        1,
                        _timeProvider.GetUtcNow(),
                        RunId,
                        new ExecuteRequestPayload(_request)))
                : WorkerJobProtocolCodec.SerializeControllerInput(
                    new WorkerJobControllerMessage(
                        WorkerJobProtocolV2.RequestCategory,
                        WorkerJobProtocolV2.ExecuteType,
                        1,
                        _timeProvider.GetUtcNow(),
                        JobId,
                        JobKind,
                        _dispatch switch
                        {
                            ProcessAssetsWorkerJobDispatch processAssets =>
                                new ProcessAssetsExecutePayload(processAssets.Request),
                            CoordinateLookupWorkerJobDispatch coordinateLookup =>
                                new CoordinateLookupExecutePayload(coordinateLookup.Request),
                            CacheMutationWorkerJobDispatch cacheMutation =>
                                new CacheMutationExecutePayload(cacheMutation.Request),
                            _ => throw new NotSupportedException(
                                "The worker-job dispatch kind is not registered for serialization.")
                        }));
        }
        catch
        {
            _startup.TrySetResult(
                ChildWorkerStartupObservation.RequestSerializationFailed.Instance);
            PublishTerminalPreventingObservation(
                ChildWorkerFaultContainmentReason.RequestSerializationFailed.Instance);
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

    private async Task<bool> DeliverAdmittedEventAsync(
        WorkerJobOutputMessage jobEvent,
        WorkerProtocolEvent? compatibilityEvent)
    {
        try
        {
            if (_eventSink is IProcessAssetsWorkerJobEventSink processAssetsSink)
            {
                await processAssetsSink.AcceptProcessAssetsAsync(
                    jobEvent,
                    compatibilityEvent
                        ?? throw new InvalidOperationException(
                            "ProcessAssets output requires a compatibility projection."),
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _eventSink.AcceptAsync(jobEvent, CancellationToken.None).ConfigureAwait(false);
            }
            return true;
        }
        catch
        {
            RecordSinkFailure(jobEvent.Type == WorkerJobProtocolV2.ReadyType);
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

    private bool TryCommitPreReady(ChildWorkerStartupObservation observation)
    {
        if (Interlocked.CompareExchange(ref _startupAuthority, 2, 0) != 0)
        {
            return false;
        }

        _startup.TrySetResult(observation);
        return true;
    }

    private void PublishTerminalPreventingObservation(
        ChildWorkerFaultContainmentReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        lock (_terminalPreventingObservationGate)
        {
            if (_firstTerminalPreventingObservation.Task.IsCompleted)
            {
                return;
            }

            ChildWorkerStopRequest observedAt;
            try
            {
                observedAt = ChildWorkerStopRequest.CaptureFaultObservation(
                    _timeProvider);
            }
            catch
            {
                _firstTerminalPreventingObservation.SetException(
                    new InvalidOperationException(
                        TerminalPreventingObservationTimestampFailureDiagnostic));
                _ = _firstTerminalPreventingObservation.Task.Exception;
                return;
            }

            var observation = new ChildWorkerTerminalPreventingObservation(
                observedAt,
                reason);
            _firstTerminalPreventingObservation.SetResult(observation);
        }
    }

    private void PublishTerminalInputCloseFailure()
    {
        lock (_terminalPreventingObservationGate)
        {
            if (_terminalInputCloseFailure.Task.IsCompleted)
            {
                return;
            }

            ChildWorkerStopRequest observedAt;
            try
            {
                observedAt = ChildWorkerStopRequest.CaptureFaultObservation(
                    _timeProvider);
            }
            catch
            {
                var failure = new InvalidOperationException(
                    TerminalPreventingObservationTimestampFailureDiagnostic);
                _terminalInputCloseFailure.SetException(failure);
                _ = _terminalInputCloseFailure.Task.Exception;
                if (!_firstTerminalPreventingObservation.Task.IsCompleted)
                {
                    _firstTerminalPreventingObservation.SetException(failure);
                    _ = _firstTerminalPreventingObservation.Task.Exception;
                }

                return;
            }

            var observation = new ChildWorkerTerminalPreventingObservation(
                observedAt,
                ChildWorkerFaultContainmentReason.TerminalInputCloseFailed.Instance);
            _terminalInputCloseFailure.SetResult(observation);
            if (!_firstTerminalPreventingObservation.Task.IsCompleted)
            {
                _firstTerminalPreventingObservation.SetResult(observation);
            }
        }
    }

    private void RecordProtocolFailure(WorkerProtocolFailure failure)
    {
        lock (_observationGate)
        {
            _firstProtocolObservation ??=
                new ChildWorkerProtocolObservation.ProtocolFailure(failure);
        }

        TryCommitPreReady(new ChildWorkerStartupObservation.ProtocolFailure(failure));
        PublishTerminalPreventingObservation(
            new ChildWorkerFaultContainmentReason.ProtocolFailure(failure));
    }

    private void RecordSinkFailure(bool isReady)
    {
        lock (_observationGate)
        {
            _firstProtocolObservation ??=
                ChildWorkerProtocolObservation.SinkFailure.Instance;
            _suppressCallbacks = true;
        }

        TryCommitPreReady(ChildWorkerStartupObservation.SinkFailed.Instance);
        PublishTerminalPreventingObservation(
            isReady
                ? ChildWorkerFaultContainmentReason.ReadyRejected.Instance
                : ChildWorkerFaultContainmentReason.SinkFailure.Instance);
    }

    private WorkerProtocolFailure? FinalizeOutputProtocol(
        WorkerProtocolEventStreamValidator validator)
    {
        if (_protocolVersion == InternalWorkerProtocolVersion.V1)
        {
            WorkerProtocolStreamFinalizationResult finalization =
                validator.FinalizeStream();
            return finalization.IsComplete ? null : finalization.Failure;
        }

        return _jobOutputValidator!.FinalizeOutput(hasPartialFrame: false);
    }

    private bool TryParseAndValidateOutput(
        ReadOnlySpan<byte> frame,
        WorkerProtocolEventStreamValidator validator,
        out WorkerJobOutputMessage? jobEvent,
        out WorkerProtocolEvent? projectedEvent,
        out WorkerProtocolFailure? failure)
    {
        jobEvent = null;
        projectedEvent = null;
        failure = null;
        if (_protocolVersion == InternalWorkerProtocolVersion.V1)
        {
            WorkerProtocolParseResult parsed = WorkerProtocolCodec.Parse(frame);
            if (!parsed.IsSuccess)
            {
                failure = parsed.Failure;
                return false;
            }

            WorkerProtocolEvent @event = parsed.Event!;
            if ((@event.Type == WorkerProtocolV1.ReadyType && @event.RunId is not null)
                || (@event.Type != WorkerProtocolV1.ReadyType && @event.RunId != RunId))
            {
                failure = new WorkerProtocolFailure(
                    WorkerProtocolFailureCode.InvalidCorrelation,
                    "Event correlation did not match this session.");
                return false;
            }

            var validated = validator.Validate(@event);
            if (!validated.IsSuccess)
            {
                failure = validated.Failure;
                return false;
            }

            try
            {
                jobEvent = ProcessAssetsWorkerJobProjection.MapV1(_request, @event);
                projectedEvent = @event;
                return true;
            }
            catch
            {
                failure = new WorkerProtocolFailure(
                    WorkerProtocolFailureCode.InvalidPayload,
                    "The v1 processing output could not be adapted to a typed worker job.");
                return false;
            }
        }

        WorkerJobProtocolParseResult jobParsed = WorkerJobProtocolCodec.Parse(frame);
        if (!jobParsed.IsSuccess)
        {
            failure = jobParsed.Failure;
            return false;
        }

        WorkerJobOutputValidationResult jobValidated =
            _jobOutputValidator!.Validate(jobParsed.Message!);
        if (!jobValidated.IsSuccess)
        {
            failure = jobValidated.Failure;
            return false;
        }

        try
        {
            jobEvent = jobValidated.Message;
            projectedEvent = _jobProjection?.Map(jobValidated.Message!);
            return true;
        }
        catch
        {
            failure = new WorkerProtocolFailure(
                WorkerProtocolFailureCode.InvalidPayload,
                "The typed worker-job output could not be projected.");
            return false;
        }
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
