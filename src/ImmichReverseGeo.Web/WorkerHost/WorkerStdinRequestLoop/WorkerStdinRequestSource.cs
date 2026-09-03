using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.WorkerHost.WorkerStdinRequestLoop;

internal interface IWorkerStandardInputStreamFactory
{
    Stream OpenStandardInput();
}

internal sealed class WorkerStandardInputStreamFactory : IWorkerStandardInputStreamFactory
{
    public Stream OpenStandardInput()
    {
        return Console.OpenStandardInput();
    }
}

internal sealed class WorkerStdinRequestSource : IInitialProcessingRunAcquirer, IAsyncDisposable
{
    private readonly IWorkerStandardInputStreamFactory _inputFactory;
    private readonly ILogger<WorkerStdinRequestSource> _logger;
    private readonly object _gate = new();
    private readonly WorkerProtocolControllerInputValidator _validator = new();
    private readonly TaskCompletionSource<InitialProcessingRunAcquisition> _initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WorkerInputPumpFinality> _finality = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkerStdinFrameReader? _reader;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pumpTask;
    private Task? _shutdownTask;
    private WorkerStdinProcessingRunLease? _lease;
    private InputPhase _phase = InputPhase.BeforeInvocation;
    private bool _started;
    private bool _stopRequested;

    internal WorkerStdinRequestSource(
        IWorkerStandardInputStreamFactory inputFactory,
        ILogger<WorkerStdinRequestSource> logger)
    {
        ArgumentNullException.ThrowIfNull(inputFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _inputFactory = inputFactory;
        _logger = logger;
    }

    public Task<InitialProcessingRunAcquisition> AcquireAsync(CancellationToken cancellationToken)
    {
        StartPump();
        return _initial.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Task shutdown;
        lock (_gate)
        {
            shutdown = GetOrStartShutdownUnderGate();
        }

        await shutdown.ConfigureAwait(false);
    }

    internal void NotifyExecutionStarting(WorkerStdinProcessingRunLease lease)
    {
        lock (_gate)
        {
            VerifyLeaseUnderGate(lease);
            if (_phase == InputPhase.BeforeInvocation)
            {
                _phase = InputPhase.Executing;
            }
        }
    }

    internal async ValueTask<WorkerInputPumpFinality> SettleAsync(
        WorkerStdinProcessingRunLease lease,
        CancellationToken cancellationToken)
    {
        Task shutdown;
        lock (_gate)
        {
            VerifyLeaseUnderGate(lease);
            shutdown = GetOrStartShutdownUnderGate();
        }

        await shutdown.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _finality.Task.ConfigureAwait(false);
    }

    internal async Task NotifyTerminalAsync(ProcessingRunRequest request, CancellationToken cancellationToken)
    {
        Task shutdown;
        lock (_gate)
        {
            VerifyRequestUnderGate(request);
            if (_phase is InputPhase.BeforeInvocation or InputPhase.Executing)
            {
                _phase = InputPhase.Terminal;
            }

            RecordFinalityUnderGate(WorkerInputPumpFinality.ExpectedShutdown());
            shutdown = GetOrStartShutdownUnderGate();
        }

        await shutdown.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StartPump()
    {
        var openFailed = false;
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            if (_stopRequested)
            {
                throw new ObjectDisposedException(nameof(WorkerStdinRequestSource));
            }

            _started = true;
            _pumpCancellation = new CancellationTokenSource();
            try
            {
                var reader = new WorkerStdinFrameReader(_inputFactory.OpenStandardInput());
                _reader = reader;
                var cancellationToken = _pumpCancellation.Token;
                _pumpTask = Task.Run(() => PumpLifetimeAsync(reader, cancellationToken));
            }
            catch
            {
                _pumpTask = Task.CompletedTask;
                _initial.TrySetResult(InitialProcessingRunAcquisition.Fail(WorkerSafeFailure.Reader()));
                openFailed = true;
            }
        }

        if (openFailed)
        {
            LogSafely(WorkerSafeFailure.Reader().Category);
        }
    }

    private async Task PumpLifetimeAsync(
        WorkerStdinFrameReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await PumpAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecordExpectedShutdown();
        }
        catch (ObjectDisposedException)
        {
            RecordExpectedOrReaderFailure();
        }
        catch
        {
            RecordReaderFailure();
        }
    }

    private async Task PumpAsync(WorkerStdinFrameReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frameResult = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frameResult.IsEndOfInput)
            {
                HandleEndOfInput();
                return;
            }

            if (frameResult.IsReaderFailure)
            {
                RecordExpectedOrReaderFailure();
                return;
            }

            if (!frameResult.IsSuccess)
            {
                RecordInputFailure(WorkerSafeFailure.Input(frameResult.FailureCode!.Value));
                return;
            }

            var parsed = WorkerProtocolCodec.ParseControllerInput(frameResult.Frame!);
            if (!parsed.IsSuccess)
            {
                RecordInputFailure(WorkerSafeFailure.Input(parsed.Failure!.Code));
                return;
            }

            WorkerStdinProcessingRunLease? acceptedLease = null;
            var requestCancellation = false;
            WorkerSafeFailure? validationFailure = null;
            lock (_gate)
            {
                var validated = _validator.Validate(parsed.Message!, true, ToProtocolPhase(_phase));
                if (!validated.IsSuccess)
                {
                    validationFailure = WorkerSafeFailure.Input(validated.Failure!.Code);
                }
                else if (validated.Message!.Payload is ExecuteRequestPayload execute)
                {
                    acceptedLease = new WorkerStdinProcessingRunLease(execute.Request, this);
                    _lease = acceptedLease;
                }
                else
                {
                    requestCancellation = validated.CancelDisposition is
                        WorkerProtocolCancelDisposition.LatchedBeforeInvocation or
                        WorkerProtocolCancelDisposition.CooperativeCancellationRequested;
                }
            }

            if (validationFailure is not null)
            {
                RecordInputFailure(validationFailure);
                return;
            }

            if (acceptedLease is not null)
            {
                _initial.TrySetResult(InitialProcessingRunAcquisition.Accept(acceptedLease));
            }

            if (requestCancellation)
            {
                WorkerStdinProcessingRunLease? lease;
                lock (_gate)
                {
                    lease = _lease;
                }

                lease?.RequestCancellation();
            }
        }
    }

    private void HandleEndOfInput()
    {
        InitialProcessingRunAcquisition? initial = null;
        lock (_gate)
        {
            if (_lease is null)
            {
                var finalized = _validator.FinalizeInput(false);
                initial = finalized.IsSuccess
                    ? InitialProcessingRunAcquisition.EndOfInput()
                    : InitialProcessingRunAcquisition.Fail(WorkerSafeFailure.Input(finalized.Failure!.Code));
            }
            else
            {
                var finalized = _validator.FinalizeInput(false);
                if (finalized.IsSuccess)
                {
                    RecordFinalityUnderGate(WorkerInputPumpFinality.ControlsClosed());
                }
                else
                {
                    RecordFinalityUnderGate(WorkerInputPumpFinality.InputFailure(
                        WorkerSafeFailure.Input(finalized.Failure!.Code)));
                }
            }
        }

        if (initial is not null)
        {
            _initial.TrySetResult(initial);
        }
    }

    private void RecordInputFailure(WorkerSafeFailure failure)
    {
        var preRequest = false;
        lock (_gate)
        {
            if (_lease is null)
            {
                preRequest = true;
            }
            else
            {
                RecordFinalityUnderGate(WorkerInputPumpFinality.InputFailure(failure));
            }
        }

        if (preRequest)
        {
            _initial.TrySetResult(InitialProcessingRunAcquisition.Fail(failure));
        }

        LogSafely(failure.Category);
    }

    private void RecordReaderFailure()
    {
        var failure = WorkerSafeFailure.Reader();
        var preRequest = false;
        lock (_gate)
        {
            if (_lease is null)
            {
                preRequest = true;
            }
            else
            {
                RecordFinalityUnderGate(WorkerInputPumpFinality.ReaderFailure());
            }
        }

        if (preRequest)
        {
            _initial.TrySetResult(InitialProcessingRunAcquisition.Fail(failure));
        }

        LogSafely(failure.Category);
    }

    private void RecordExpectedOrReaderFailure()
    {
        lock (_gate)
        {
            if (_stopRequested)
            {
                RecordFinalityUnderGate(WorkerInputPumpFinality.ExpectedShutdown());
                return;
            }
        }

        RecordReaderFailure();
    }

    private void RecordExpectedShutdown()
    {
        lock (_gate)
        {
            RecordFinalityUnderGate(WorkerInputPumpFinality.ExpectedShutdown());
        }
    }

    private Task GetOrStartShutdownUnderGate()
    {
        if (_shutdownTask is not null)
        {
            return _shutdownTask;
        }

        _stopRequested = true;
        if (_phase is InputPhase.BeforeInvocation or InputPhase.Executing or InputPhase.Terminal)
        {
            _phase = InputPhase.Stopped;
        }

        if (_lease is not null)
        {
            RecordFinalityUnderGate(WorkerInputPumpFinality.ExpectedShutdown());
        }

        _shutdownTask = Task.Run(ShutdownCoreAsync);
        return _shutdownTask;
    }

    private async Task ShutdownCoreAsync()
    {
        CancellationTokenSource? pumpCancellation;
        WorkerStdinFrameReader? reader;
        Task? pumpTask;
        lock (_gate)
        {
            pumpCancellation = _pumpCancellation;
            reader = _reader;
            pumpTask = _pumpTask;
        }

        Exception? cleanupFailure = null;
        try
        {
            pumpCancellation?.Cancel();
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        if (reader is not null)
        {
            try
            {
                await reader.CloseInputAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
        }

        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
        }

        if (reader is not null)
        {
            try
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
        }

        try
        {
            pumpCancellation?.Dispose();
        }
        catch (Exception exception)
        {
            cleanupFailure ??= exception;
        }

        try
        {
            _lease?.DisposeCancellationAfterPump();
        }
        catch (Exception exception)
        {
            cleanupFailure ??= exception;
        }

        if (_lease is null)
        {
            _initial.TrySetResult(InitialProcessingRunAcquisition.Fail(WorkerSafeFailure.Reader()));
        }

        if (cleanupFailure is not null)
        {
            LogSafely(WorkerSafeFailure.Cleanup().Category);
            throw new InvalidOperationException(WorkerSafeFailure.Cleanup().Category);
        }
    }

    private void RecordFinalityUnderGate(WorkerInputPumpFinality finality)
    {
        _finality.TrySetResult(finality);
    }

    private void VerifyLeaseUnderGate(WorkerStdinProcessingRunLease lease)
    {
        if (!ReferenceEquals(_lease, lease))
        {
            throw new InvalidOperationException("The processing run lease does not belong to this input source.");
        }
    }

    private void VerifyRequestUnderGate(ProcessingRunRequest request)
    {
        if (_lease is null || !ReferenceEquals(_lease.Request, request))
        {
            throw new InvalidOperationException("The processing request does not match the accepted input request.");
        }
    }

    private void LogSafely(string category)
    {
        try
        {
            _logger.LogWarning(category);
        }
        catch
        {
        }
    }

    private static WorkerProtocolExecutionPhase ToProtocolPhase(InputPhase phase)
    {
        return phase switch
        {
            InputPhase.BeforeInvocation => WorkerProtocolExecutionPhase.BeforeInvocation,
            InputPhase.Executing => WorkerProtocolExecutionPhase.Executing,
            InputPhase.Terminal or InputPhase.Stopped => WorkerProtocolExecutionPhase.Terminal,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
        };
    }

    private enum InputPhase
    {
        BeforeInvocation,
        Executing,
        Terminal,
        Stopped
    }
}

internal sealed class WorkerStdinProcessingRunLease : IProcessingRunLease
{
    private readonly WorkerStdinRequestSource _owner;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private readonly object _gate = new();
    private Task? _disposeTask;
    private int _cancellationDisposed;

    internal WorkerStdinProcessingRunLease(ProcessingRunRequest request, WorkerStdinRequestSource owner)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(owner);
        Request = request;
        _owner = owner;
        _token = _cancellation.Token;
    }

    public ProcessingRunRequest Request { get; }

    public CancellationToken CancellationToken => _token;

    public void NotifyExecutionStarting()
    {
        _owner.NotifyExecutionStarting(this);
    }

    public ValueTask<WorkerInputPumpFinality> SettleAsync(CancellationToken cancellationToken)
    {
        return _owner.SettleAsync(this, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        await disposeTask.ConfigureAwait(false);
    }

    internal void RequestCancellation()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void DisposeCancellationAfterPump()
    {
        if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
        {
            _cancellation.Dispose();
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _owner.SettleAsync(this, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            DisposeCancellationAfterPump();
        }
    }
}
