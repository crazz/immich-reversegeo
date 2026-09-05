using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

internal sealed partial class ChildWorkerSession
{
    private readonly object _cancellationGate = new();
    private readonly object _resourceGate = new();
    private readonly SemaphoreSlim _inputWriterGate = new(1, 1);
    private readonly CancellationTokenSource _inputLifetimeCancellation = new();
    private readonly CancellationTokenSource _controlCancellation = new();
    private readonly TaskCompletionSource _confirmedExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _executeRequestAccepted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ChildWorkerTerminationRequest> _firstTerminationRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationState? _cancellationState;
    private Task<ChildWorkerCancellationResult>? _stopTask;
    private Task? _cancelDeliveryTask;
    private TaskCompletionSource? _deadlineObserverSettled;
    private Task? _containmentInputCloseTask;
    private Task? _inputCloseTask;
    private Task? _resourceDisposalTask;
    private bool _processExitConfirmed;
    private int _inputClosed;

    internal TimeProvider Clock => _timeProvider;
    internal ProcessingRunRequest Request => _request;
    internal Task ExecuteRequestAccepted => _executeRequestAccepted.Task;
    internal Task<ChildWorkerTerminationRequest> FirstTerminationRequest
        => _firstTerminationRequest.Task;
    internal Task PhysicalExitConfirmed => _confirmedExit.Task;

    internal ChildWorkerCancellationFacts? CancellationFacts
    {
        get
        {
            lock (_cancellationGate)
            {
                return _cancellationState is null
                    ? null
                    : Snapshot(_cancellationState);
            }
        }
    }

    internal Task<ChildWorkerCancellationResult> RequestStop(
        ChildWorkerStopRequest? stopRequest = null)
    {
        stopRequest ??= ChildWorkerStopRequest.Capture(_timeProvider);
        return RequestTermination(new ChildWorkerTerminationRequest(
            stopRequest,
            ChildWorkerTerminationIntent.Stop));
    }

    internal Task<ChildWorkerCancellationResult> RequestTermination(
        ChildWorkerTerminationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ReferenceEquals(request.Deadline.Clock, _timeProvider))
        {
            throw new ArgumentException(
                "The termination request clock must match the child-worker session clock.",
                nameof(request));
        }

        if (GetExitState() == ChildProcessExitState.Exited)
        {
            ConfirmProcessExit();
        }

        Task<ChildWorkerCancellationResult> result;
        var closeInputForContainment = false;
        lock (_cancellationGate)
        {
            if (_stopTask is not null)
            {
                if (request.Intent == ChildWorkerTerminationIntent.FaultContainment
                    && _cancellationState!.FirstContainmentReason is null)
                {
                    _cancellationState.FirstContainmentReason = request.Reason;
                    closeInputForContainment = !_processExitConfirmed;
                }

                result = _stopTask;
            }
            else
            {
                _cancellationState = new CancellationState(
                    request.Deadline.FirstStopAtUtc,
                    request.Deadline.FirstStopAtUtc
                        + ChildWorkerCancellationPolicy.Grace,
                    request.Intent,
                    request.Reason,
                    request.Deadline.UtcObservationFailed);
                _firstTerminationRequest.SetResult(request);

                if (_processExitConfirmed)
                {
                    _cancellationState.DeliveryPhase =
                        ChildWorkerCancelDeliveryPhase.AlreadyExited;
                    _cancellationState.ExitRace =
                        ChildWorkerCancellationExitRace.BeforeControl;
                    _stopTask = CompleteKnownExitStopAsync();
                }
                else
                {
                    _deadlineObserverSettled = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    var remainingGrace = GetRemainingGrace(request.Deadline);
                    _stopTask = StopCoreAsync(
                        remainingGrace,
                        _deadlineObserverSettled,
                        request.Intent);
                    closeInputForContainment =
                        request.Intent ==
                            ChildWorkerTerminationIntent.FaultContainment;
                }

                result = _stopTask;
            }
        }

        if (closeInputForContainment)
        {
            _ = StartContainmentInputClose();
        }

        return result;
    }

    internal Task<ChildWorkerCancellationResult> WaitForStopAsync(
        CancellationToken cancellationToken = default)
        => RequestStop().WaitAsync(cancellationToken);

    internal Task WaitForCancellationDeliveryAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_cancellationGate)
        {
            return (_cancelDeliveryTask
                ?? throw new InvalidOperationException("Stop has not been requested."))
                .WaitAsync(cancellationToken);
        }
    }

    private async Task<ChildWorkerCancellationResult> StopCoreAsync(
        TimeSpan remainingGrace,
        TaskCompletionSource deadlineObserverSettled,
        ChildWorkerTerminationIntent intent)
    {
        var deadline = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ITimer timer = _timeProvider.CreateTimer(
            static state =>
            {
                var callback = (CancellationDeadlineCallback)state!;
                callback.Session.SignalCancellationDeadline(callback.Deadline);
            },
            new CancellationDeadlineCallback(this, deadline),
            remainingGrace,
            Timeout.InfiniteTimeSpan);

        Task delivery = intent == ChildWorkerTerminationIntent.FaultContainment
            ? DeliverContainmentInputCloseAsync()
            : DeliverCancelAsync(deadline.Task);
        lock (_cancellationGate)
        {
            _cancelDeliveryTask = delivery;
        }

        try
        {
            var winner = await Task.WhenAny(
                _confirmedExit.Task,
                deadline.Task).ConfigureAwait(false);
            if (ReferenceEquals(winner, deadline.Task))
            {
                EscalateAtDeadline();
            }
        }
        finally
        {
            await timer.DisposeAsync().ConfigureAwait(false);
            deadlineObserverSettled.TrySetResult();
        }

        var completion = await _settlement.ConfigureAwait(false);
        await delivery.ConfigureAwait(false);
        return new ChildWorkerCancellationResult(
            GetCancellationFacts(),
            completion);
    }

    private async Task<ChildWorkerCancellationResult> CompleteKnownExitStopAsync()
    {
        var completion = await _settlement.ConfigureAwait(false);
        return new ChildWorkerCancellationResult(GetCancellationFacts(), completion);
    }

    private async Task<ChildWorkerCompletionObservation> ObserveEvidenceFinalityAsync()
    {
        var completion = await _completion.ConfigureAwait(false);
        if (completion.ExitObserved || GetExitState() == ChildProcessExitState.Exited)
        {
            ConfirmProcessExit();
        }

        await _confirmedExit.Task.ConfigureAwait(false);
        return completion;
    }

    private async Task<ChildWorkerCompletionObservation> ObserveSettledCompletionAsync()
    {
        var completion = await _evidenceFinality.ConfigureAwait(false);
        if (_evidenceFinalityGate is not null)
        {
            await _evidenceFinalityGate.WaitForReleaseAsync().ConfigureAwait(false);
        }

        await EnsureResourcesDisposedAsync().ConfigureAwait(false);
        return completion;
    }

    private async Task<ChildWorkerStartupObservation> WriteExecuteFrameAsync(
        byte[] frame)
    {
        var entered = false;
        try
        {
            await _inputWriterGate
                .WaitAsync(_inputLifetimeCancellation.Token)
                .ConfigureAwait(false);
            entered = true;

            if (IsInputClosed
                || TryConfirmKnownExit(
                    ChildWorkerCancellationExitRace.BeforeControl))
            {
                PublishTerminalPreventingObservation(
                    ChildWorkerFaultContainmentReason.RequestWriteFailed.Instance);
                return ChildWorkerStartupObservation.RequestWriteFailed.Instance;
            }

            try
            {
                await _standardInputStream
                    .WriteAsync(
                        frame.AsMemory(),
                        _inputLifetimeCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                PublishTerminalPreventingObservation(
                    ChildWorkerFaultContainmentReason.RequestWriteFailed.Instance);
                return ChildWorkerStartupObservation.RequestWriteFailed.Instance;
            }

            try
            {
                await _standardInputStream
                    .FlushAsync(_inputLifetimeCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                PublishTerminalPreventingObservation(
                    ChildWorkerFaultContainmentReason.RequestFlushFailed.Instance);
                return ChildWorkerStartupObservation.RequestFlushFailed.Instance;
            }

            _executeRequestAccepted.TrySetResult();
            return ChildWorkerStartupObservation.ReadyAccepted.Instance;
        }
        catch
        {
            PublishTerminalPreventingObservation(
                ChildWorkerFaultContainmentReason.RequestWriteFailed.Instance);
            return ChildWorkerStartupObservation.RequestWriteFailed.Instance;
        }
        finally
        {
            if (entered)
            {
                _inputWriterGate.Release();
            }
        }
    }

    private async Task DeliverContainmentInputCloseAsync()
    {
        await Task.CompletedTask.ConfigureAwait(
            ConfigureAwaitOptions.ForceYielding);
        await StartContainmentInputClose().ConfigureAwait(false);
        if (TryConfirmKnownExit(ChildWorkerCancellationExitRace.BeforeControl))
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.AlreadyExited);
            return;
        }

        SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.InputClosed);
    }

    private async Task DeliverCancelAsync(Task deadline)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        var winner = await Task
            .WhenAny(_startup.Task, _confirmedExit.Task, deadline)
            .ConfigureAwait(false);

        if (ReferenceEquals(winner, _confirmedExit.Task))
        {
            SetExitRace(ChildWorkerCancellationExitRace.BeforeControl);
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.AlreadyExited);
            return;
        }

        if (TryConfirmKnownExit(ChildWorkerCancellationExitRace.BeforeControl))
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.AlreadyExited);
            return;
        }

        if (ReferenceEquals(winner, deadline) || deadline.IsCompleted)
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.DeadlineElapsed);
            return;
        }

        var startup = await _startup.Task.ConfigureAwait(false);
        if (startup is not ChildWorkerStartupObservation.ReadyAccepted)
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.NotAccepted);
            return;
        }

        if (deadline.IsCompleted)
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.DeadlineElapsed);
            return;
        }

        if (TryConfirmKnownExit(ChildWorkerCancellationExitRace.BeforeControl))
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.AlreadyExited);
            return;
        }

        byte[] frame;
        try
        {
            var message = new WorkerProtocolControllerMessage(
                WorkerProtocolV1.ControlCategory,
                WorkerProtocolV1.CancelType,
                2,
                _timeProvider.GetUtcNow(),
                _request.RunId,
                new CancelControlPayload());
            var objectBytes = WorkerProtocolCodec.SerializeControllerInput(message);
            frame = new byte[objectBytes.Length + 1];
            objectBytes.CopyTo(frame, 0);
            frame[^1] = (byte)10;
        }
        catch
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.SerializationFailed);
            return;
        }

        await WriteCancelFrameAsync(frame, deadline).ConfigureAwait(false);
    }

    private async Task WriteCancelFrameAsync(byte[] frame, Task deadline)
    {
        var entered = false;
        try
        {
            await _inputWriterGate
                .WaitAsync(_controlCancellation.Token)
                .ConfigureAwait(false);
            entered = true;

            if (deadline.IsCompleted)
            {
                SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.DeadlineElapsed);
                return;
            }

            if (IsInputClosed)
            {
                SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.InputClosed);
                return;
            }

            if (TryConfirmKnownExit(ChildWorkerCancellationExitRace.BeforeControl))
            {
                SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.AlreadyExited);
                return;
            }

            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.WriteStarted);
            try
            {
                await _standardInputStream
                    .WriteAsync(frame.AsMemory(), _controlCancellation.Token)
                    .ConfigureAwait(false);
                SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.WriteCompleted);
            }
            catch
            {
                TryConfirmKnownExit(ChildWorkerCancellationExitRace.DuringWrite);
                SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.WriteFailed);
                return;
            }

            if (TryConfirmKnownExit(ChildWorkerCancellationExitRace.DuringWrite))
            {
                SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.AlreadyExited);
                return;
            }

            if (deadline.IsCompleted)
            {
                SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.DeadlineElapsed);
                return;
            }

            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.FlushStarted);
            try
            {
                await _standardInputStream
                    .FlushAsync(_controlCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                TryConfirmKnownExit(ChildWorkerCancellationExitRace.DuringFlush);
                SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.FlushFailed);
                return;
            }

            if (TryConfirmKnownExit(ChildWorkerCancellationExitRace.DuringFlush))
            {
                SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.AlreadyExited);
                return;
            }

            if (deadline.IsCompleted)
            {
                SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.DeadlineElapsed);
                return;
            }

            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.Flushed);
        }
        catch (OperationCanceledException)
        {
            SetCancelledDeliveryPhase(deadline);
        }
        catch
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.WriteFailed);
        }
        finally
        {
            if (entered)
            {
                _inputWriterGate.Release();
            }
        }
    }

    private void SetCancelledDeliveryPhase(Task deadline)
    {
        if (TryConfirmKnownExit(ChildWorkerCancellationExitRace.BeforeControl))
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.AlreadyExited);
        }
        else if (deadline.IsCompleted)
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.DeadlineElapsed);
        }
        else if (IsInputClosed)
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.InputClosed);
        }
        else
        {
            SetDeliveryPhase(ChildWorkerCancelDeliveryPhase.WriteFailed);
        }
    }

    private void SignalCancellationDeadline(TaskCompletionSource deadline)
    {
        var signal = false;
        lock (_cancellationGate)
        {
            if (!_processExitConfirmed && _cancellationState is not null)
            {
                _cancellationState.GraceExpired = true;
                signal = deadline.TrySetResult();
            }
        }

        if (signal)
        {
            CancelWithoutFailure(_controlCancellation);
        }
    }

    private void EscalateAtDeadline()
    {
        if (TryConfirmKnownExit(ChildWorkerCancellationExitRace.BeforeEscalation))
        {
            SetEscalation(false, ChildProcessKillOutcome.AlreadyExited);
            return;
        }

        ChildProcessKillOutcome outcome;
        try
        {
            outcome = _process.KillProcessTree();
        }
        catch
        {
            outcome = ChildProcessKillOutcome.Failed;
        }

        SetEscalation(true, outcome);
        if (outcome == ChildProcessKillOutcome.AlreadyExited)
        {
            SetExitRace(ChildWorkerCancellationExitRace.DuringEscalation);
            ConfirmProcessExit();
            return;
        }

        if (GetExitState() == ChildProcessExitState.Exited)
        {
            ConfirmProcessExit();
        }
    }

    private bool TryConfirmKnownExit(ChildWorkerCancellationExitRace race)
    {
        lock (_cancellationGate)
        {
            if (_processExitConfirmed)
            {
                SetExitRaceWhileLocked(race);
                return true;
            }
        }

        if (GetExitState() != ChildProcessExitState.Exited)
        {
            return false;
        }

        SetExitRace(race);
        ConfirmProcessExit();
        return true;
    }

    private void ConfirmProcessExit()
    {
        lock (_cancellationGate)
        {
            if (_processExitConfirmed)
            {
                return;
            }

            _processExitConfirmed = true;
        }

        CancelWithoutFailure(_inputLifetimeCancellation);
        CancelWithoutFailure(_controlCancellation);
        _confirmedExit.TrySetResult();
    }

    private ChildProcessExitState GetExitState()
    {
        try
        {
            return _process.GetExitState();
        }
        catch
        {
            return ChildProcessExitState.Unavailable;
        }
    }

    private Task EnsureResourcesDisposedAsync()
    {
        lock (_resourceGate)
        {
            _resourceDisposalTask ??= DisposeResourcesCoreAsync();
            return _resourceDisposalTask;
        }
    }

    private async Task DisposeResourcesCoreAsync()
    {
        Task? deadlineObserver;
        lock (_cancellationGate)
        {
            deadlineObserver = _deadlineObserverSettled?.Task;
        }

        if (deadlineObserver is not null)
        {
            await deadlineObserver.ConfigureAwait(false);
        }

        Task? containmentInputClose;
        lock (_resourceGate)
        {
            containmentInputClose = _containmentInputCloseTask;
        }

        if (containmentInputClose is not null)
        {
            await containmentInputClose.ConfigureAwait(false);
        }
        else
        {
            await BeginInputClose().ConfigureAwait(false);
        }

        Task? delivery;
        lock (_cancellationGate)
        {
            delivery = _cancelDeliveryTask;
        }

        if (delivery is not null)
        {
            await delivery.ConfigureAwait(false);
        }

        await _readyDeadlineTask.ConfigureAwait(false);

        lock (_observationGate)
        {
            _suppressCallbacks = true;
        }

        try
        {
            await DisposeStreamAsync(_standardOutputStream).ConfigureAwait(false);
            await DisposeStreamAsync(_standardErrorStream).ConfigureAwait(false);
            await _process.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _inputLifetimeCancellation.Dispose();
            _controlCancellation.Dispose();
            _inputWriterGate.Dispose();
        }
    }

    private Task StartContainmentInputClose()
    {
        Volatile.Write(ref _inputClosed, 1);
        CancelWithoutFailure(_inputLifetimeCancellation);
        CancelWithoutFailure(_controlCancellation);
        lock (_resourceGate)
        {
            _containmentInputCloseTask ??=
                CloseInputAfterWriterAsync();
            return _containmentInputCloseTask;
        }
    }

    private async Task CloseInputAfterWriterAsync()
    {
        await Task.CompletedTask.ConfigureAwait(
            ConfigureAwaitOptions.ForceYielding);
        await _inputWriterGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            await BeginInputClose().ConfigureAwait(false);
        }
        finally
        {
            _inputWriterGate.Release();
        }
    }

    private Task BeginInputClose()
    {
        lock (_resourceGate)
        {
            Volatile.Write(ref _inputClosed, 1);
            _inputCloseTask ??= DisposeStreamAsync(_standardInputStream);
            return _inputCloseTask;
        }
    }

    private bool IsInputClosed => Volatile.Read(ref _inputClosed) != 0;

    private TimeSpan GetRemainingGrace(ChildWorkerStopRequest stopRequest)
    {
        var elapsed = _timeProvider.GetElapsedTime(
            stopRequest.FirstStopTimestamp,
            _timeProvider.GetTimestamp());

        if (elapsed <= TimeSpan.Zero)
        {
            return ChildWorkerCancellationPolicy.Grace;
        }

        return elapsed >= ChildWorkerCancellationPolicy.Grace
            ? TimeSpan.Zero
            : ChildWorkerCancellationPolicy.Grace - elapsed;
    }

    private ChildWorkerCancellationFacts GetCancellationFacts()
    {
        lock (_cancellationGate)
        {
            return Snapshot(_cancellationState!);
        }
    }

    private ChildWorkerCancellationFacts Snapshot(
        CancellationState state)
        => new(
            state.FirstStopAtUtc,
            state.DeadlineUtc,
            _executeRequestAccepted.Task.IsCompletedSuccessfully,
            state.DeliveryPhase,
            state.ExitRace,
            state.GraceExpired,
            state.KillAttempted,
            state.KillOutcome,
            state.FirstIntent,
            state.FirstContainmentReason,
            state.FirstStopUtcObservationFailed);

    private void SetDeliveryPhase(ChildWorkerCancelDeliveryPhase phase)
    {
        lock (_cancellationGate)
        {
            _cancellationState!.DeliveryPhase = phase;
        }
    }

    private void SetExitRace(ChildWorkerCancellationExitRace race)
    {
        lock (_cancellationGate)
        {
            SetExitRaceWhileLocked(race);
        }
    }

    private void SetExitRaceWhileLocked(ChildWorkerCancellationExitRace race)
    {
        if (_cancellationState is not null
            && _cancellationState.ExitRace == ChildWorkerCancellationExitRace.None)
        {
            _cancellationState.ExitRace = race;
        }
    }

    private void SetEscalation(bool attempted, ChildProcessKillOutcome outcome)
    {
        lock (_cancellationGate)
        {
            _cancellationState!.KillAttempted = attempted;
            _cancellationState.KillOutcome = outcome;
        }
    }

    private static void CancelWithoutFailure(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch
        {
        }
    }

    private sealed class CancellationState(
        DateTimeOffset firstStopAtUtc,
        DateTimeOffset deadlineUtc,
        ChildWorkerTerminationIntent firstIntent,
        ChildWorkerFaultContainmentReason? firstContainmentReason,
        bool firstStopUtcObservationFailed)
    {
        internal DateTimeOffset FirstStopAtUtc { get; } = firstStopAtUtc;
        internal DateTimeOffset DeadlineUtc { get; } = deadlineUtc;
        internal ChildWorkerTerminationIntent FirstIntent { get; } = firstIntent;
        internal ChildWorkerFaultContainmentReason? FirstContainmentReason { get; set; } =
            firstContainmentReason;
        internal bool FirstStopUtcObservationFailed { get; } =
            firstStopUtcObservationFailed;
        internal ChildWorkerCancelDeliveryPhase DeliveryPhase { get; set; }
        internal ChildWorkerCancellationExitRace ExitRace { get; set; }
        internal bool GraceExpired { get; set; }
        internal bool KillAttempted { get; set; }
        internal ChildProcessKillOutcome? KillOutcome { get; set; }
    }

    private sealed record CancellationDeadlineCallback(
        ChildWorkerSession Session,
        TaskCompletionSource Deadline);
}
