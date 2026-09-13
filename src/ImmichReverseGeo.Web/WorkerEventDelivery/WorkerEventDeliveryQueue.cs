using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Web.WorkerEventDelivery;

// Single accepted-stream producer, independent asynchronous projection consumer.
// Each FIFO barrier owns at most one sealed preceding snapshot. Only _latest is
// replaceable, bounding retained input objects by 2 * capacity + 1, plus in-flight work.
internal sealed class WorkerEventDeliveryQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly Queue<Barrier> _fifo = [];
    private readonly Func<WorkerEventDelivery, CancellationToken, ValueTask> _project;
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly CancellationTokenSource _abandonCancellation = new();
    private readonly Task _consumer;
    private readonly TaskCompletionSource _intakeClosed = Signal();
    private readonly TaskCompletionSource _firstBackpressure = Signal();
    private TaskCompletionSource _available = Signal();
    private TaskCompletionSource _space = Signal();
    private TaskCompletionSource _producerSettled = Signal();
    private TaskCompletionSource _abandonSignalled = Signal();
    private PendingSnapshot? _latest;
    private WorkerEventDelivery? _inFlight;
    private ExceptionDispatchInfo? _failure;
    private Task? _disposeTask;
    private long _lastAccepted;
    private bool _producerActive;
    private bool _waitingLossless;
    private bool _closed;
    private bool _intakeComplete;
    private bool _terminalAccepted;
    private bool _terminalDelivered;
    private bool _abandoned;
    private bool _finished;
    private long _terminalTimestamp;
    private long _acceptedSnapshots;
    private long _acceptedLossless;
    private long _replaced;
    private long _deliveredSnapshots;
    private long _deliveredLossless;
    private int _highWater;
    private long _waits;
    private long _waitMilliseconds;
    private double _projectionMilliseconds;
    private long? _terminalFlushMilliseconds;
    private long _abandonedItems;
    private long _staleRejected;

    internal WorkerEventDeliveryQueue(
        WorkerEventDeliveryScope scope,
        WorkerJobDescriptor descriptor,
        WorkerEventDeliveryPolicy policy,
        TimeProvider timeProvider,
        Func<WorkerEventDelivery, CancellationToken, ValueTask> project)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(project);
        policy.Validate();
        if (!Enum.IsDefined(scope.Version) || scope.Context.JobKind != descriptor.Kind)
        {
            throw new ArgumentException("Delivery scope must match the selected worker descriptor.", nameof(scope));
        }

        Scope = scope;
        Descriptor = descriptor;
        _capacity = policy.LosslessCapacity;
        _time = timeProvider;
        _project = project;
        _producerSettled.SetResult();
        _abandonSignalled.SetResult();
        _consumer = Task.Run(ConsumeAsync);
    }

    internal WorkerEventDeliveryScope Scope { get; }
    internal WorkerJobDescriptor Descriptor { get; }
    internal Task Completion => _consumer;
    internal Task IntakeClosed => _intakeClosed.Task;
    internal Task FirstBackpressure => _firstBackpressure.Task;

    internal WorkerEventDeliveryObservation Observation
    {
        get
        {
            lock (_gate)
            {
                return new(
                    _acceptedSnapshots, _acceptedLossless, _replaced,
                    _deliveredSnapshots, _deliveredLossless, _highWater,
                    _waits, _waitMilliseconds, _projectionMilliseconds >= long.MaxValue ? long.MaxValue : (long)_projectionMilliseconds,
                    _terminalFlushMilliseconds, _abandonedItems, _staleRejected,
                    _failure is not null ? WorkerEventDeliveryFinality.ProjectionFailed
                        : _abandoned ? WorkerEventDeliveryFinality.Abandoned
                        : _terminalDelivered ? WorkerEventDeliveryFinality.Terminal
                        : _finished ? WorkerEventDeliveryFinality.Nonterminal
                        : WorkerEventDeliveryFinality.Open);
            }
        }
    }

    internal async ValueTask EnqueueAsync(WorkerEventDeliveryInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        bool terminal;
        TaskCompletionSource? ready = null;
        long? waitStarted = null;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_producerActive)
            {
                throw new InvalidOperationException("Delivery requires one ordered accepted-stream producer.");
            }

            ValidateInput(input);
            _lastAccepted = input.Message.Sequence;
            if (WorkerEventDeliveryPolicy.IsReplaceable(Descriptor, input.Message))
            {
                _acceptedSnapshots++;
                if (_latest is not null)
                {
                    _replaced++;
                }

                _latest = new PendingSnapshot(input, _latest?.FirstSequence ?? input.Message.Sequence);
                Pulse(ref _available);
                return;
            }

            _producerActive = true;
            _producerSettled = Signal();
            _waitingLossless = true;
            _acceptedLossless++;
            terminal = input.Message.Type == WorkerJobProtocolV2.TerminalType;
            if (terminal)
            {
                _terminalAccepted = true;
                _closed = true;
                _intakeClosed.TrySetResult();
                _terminalTimestamp = _time.GetTimestamp();
            }

            if (input.Message.Type == WorkerJobProtocolV2.ReadyType)
            {
                ready = Signal();
            }
        }

        try
        {
            while (true)
            {
                Task space;
                lock (_gate)
                {
                    _failure?.Throw();
                    if (_abandoned)
                    {
                        throw new OperationCanceledException("The event delivery was abandoned.");
                    }

                    if (!terminal)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (_fifo.Count < _capacity)
                    {
                        if (waitStarted is { } started)
                        {
                            _waitMilliseconds += ElapsedMilliseconds(started);
                            waitStarted = null;
                        }

                        _fifo.Enqueue(new Barrier(input, _latest, ready));
                        _latest = null;
                        _waitingLossless = false;
                        _highWater = Math.Max(_highWater, _fifo.Count);
                        _intakeComplete = terminal;
                        Pulse(ref _available);
                        break;
                    }

                    if (waitStarted is null)
                    {
                        waitStarted = _time.GetTimestamp();
                        _waits++;
                        _firstBackpressure.TrySetResult();
                    }

                    space = _space.Task;
                }

                await space.WaitAsync(terminal ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            }

            if (ready is not null)
            {
                await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (terminal)
            {
                await _consumer.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!terminal)
        {
            AbandonIntake();
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (waitStarted is { } started)
                {
                    _waitMilliseconds += ElapsedMilliseconds(started);
                }

                _producerActive = false;
                _waitingLossless = false;
                if (_closed)
                {
                    _intakeComplete = true;
                    Pulse(ref _available);
                }

                _producerSettled.TrySetResult();
            }
        }
    }

    internal Task CompleteAsync()
    {
        lock (_gate)
        {
            if (!_terminalAccepted)
            {
                _closed = true;
                _intakeClosed.TrySetResult();
                _intakeComplete = !_producerActive;
                Pulse(ref _available);
                Pulse(ref _space);
            }
        }

        return _consumer;
    }

    internal Task AbandonAsync()
    {
        AbandonIntake();
        return _consumer;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    internal bool Authenticates(WorkerEventDelivery delivery, WorkerEventDeliveryScope scope)
    {
        lock (_gate)
        {
            return ReferenceEquals(_inFlight, delivery) && ReferenceEquals(Scope, scope);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Publish the one disposal task before invoking any cancellation callback,
        // including a callback which reenters DisposeAsync.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Task completion = AbandonAsync();
        Task producer;
        lock (_gate)
        {
            producer = _producerSettled.Task;
        }

        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            await producer.ConfigureAwait(false);
            Task cancellationDispatch;
            lock (_gate)
            {
                cancellationDispatch = _abandonSignalled.Task;
            }

            await cancellationDispatch.ConfigureAwait(false);
            _abandonCancellation.Dispose();
        }
    }

    private void ValidateInput(WorkerEventDeliveryInput input)
    {
        var message = input.Message;
        if (input.Version != Scope.Version
            || _lastAccepted == long.MaxValue || message.Sequence != _lastAccepted + 1
            || (message.Type != WorkerJobProtocolV2.ReadyType
                && (message.JobId != Scope.Context.JobId || message.JobKind != Scope.Context.JobKind)))
        {
            throw new InvalidOperationException("The accepted event does not match this delivery stream.");
        }

        if (input.CompatibilityEvent is { } compatibility
            && (Scope.Context.JobKind != WorkerJobKind.ProcessAssets
                || compatibility.Sequence != message.Sequence
                || (message.Type != WorkerJobProtocolV2.ReadyType && compatibility.RunId != message.JobId)))
        {
            throw new InvalidOperationException("The compatibility event does not match the accepted source event.");
        }
    }

    private async Task ConsumeAsync()
    {
        TaskCompletionSource? activeReady = null;
        try
        {
            while (true)
            {
                Barrier? barrier = null;
                PendingSnapshot? snapshot = null;
                Task? available = null;
                lock (_gate)
                {
                    if (_fifo.Count > 0)
                    {
                        barrier = _fifo.Dequeue();
                        activeReady = barrier.Ready;
                        Pulse(ref _space);
                    }
                    else if (_latest is not null)
                    {
                        snapshot = _latest;
                        _latest = null;
                    }
                    else if (_intakeComplete)
                    {
                        return;
                    }
                    else
                    {
                        available = _available.Task;
                    }
                }

                if (available is not null)
                {
                    await available.ConfigureAwait(false);
                    continue;
                }

                if (barrier is not null)
                {
                    if (barrier.Before is { } before)
                    {
                        try
                        {
                            await DeliverAsync(before.Input, before.FirstSequence).ConfigureAwait(false);
                        }
                        catch
                        {
                            lock (_gate)
                            {
                                _abandonedItems++; // The sealed barrier itself was not attempted.
                            }

                            throw;
                        }
                    }

                    await DeliverAsync(barrier.Input, null).ConfigureAwait(false);
                    activeReady?.TrySetResult();
                    activeReady = null;
                }
                else if (snapshot is not null)
                {
                    await DeliverAsync(snapshot.Input, snapshot.FirstSequence).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_abandonCancellation.IsCancellationRequested)
        {
            activeReady?.TrySetCanceled(_abandonCancellation.Token);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (exception is StaleWorkerEventDeliveryException)
                {
                    _staleRejected++;
                }

                _failure = ExceptionDispatchInfo.Capture(exception);
                _closed = true;
                _intakeClosed.TrySetResult();
                _intakeComplete = true;
                DiscardBuffered(exception);
                Pulse(ref _space);
            }

            activeReady?.TrySetException(exception);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _finished = true;
                _inFlight = null;
                Pulse(ref _space);
            }
        }
    }

    private async ValueTask DeliverAsync(WorkerEventDeliveryInput input, long? firstSnapshotSequence)
    {
        long? first = firstSnapshotSequence is { } start && start < input.Message.Sequence ? start : null;
        var delivery = new WorkerEventDelivery(this, input, first, first is null ? null : input.Message.Sequence - 1);
        lock (_gate)
        {
            if (_abandoned)
            {
                _abandonedItems++;
                return;
            }

            _inFlight = delivery;
        }

        long started = _time.GetTimestamp();
        try
        {
            await _project(delivery, _abandonCancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (WorkerEventDeliveryPolicy.IsReplaceable(Descriptor, input.Message))
                {
                    _deliveredSnapshots++;
                }
                else
                {
                    _deliveredLossless++;
                }

                if (input.Message.Type == WorkerJobProtocolV2.TerminalType)
                {
                    _terminalDelivered = true;
                    _terminalFlushMilliseconds = ElapsedMilliseconds(_terminalTimestamp);
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                // An unacknowledged projection is not retried. This also counts
                // indeterminate callbacks; it does not claim their side effects vanished.
                _abandonedItems++;
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                _projectionMilliseconds += Math.Max(0, _time.GetElapsedTime(started).TotalMilliseconds);
                _inFlight = null;
            }
        }
    }

    private void AbandonIntake()
    {
        TaskCompletionSource signalled;
        lock (_gate)
        {
            if (_terminalAccepted || _finished || _abandoned)
            {
                return;
            }

            _abandoned = true;
            _abandonSignalled = signalled = Signal();
            _closed = true;
            _intakeClosed.TrySetResult();
            _intakeComplete = true;
            DiscardBuffered(null);
            Pulse(ref _available);
            Pulse(ref _space);
        }

        try
        {
            _abandonCancellation.Cancel();
        }
        finally
        {
            signalled.TrySetResult();
        }
    }

    private void DiscardBuffered(Exception? failure)
    {
        while (_fifo.TryDequeue(out var barrier))
        {
            _abandonedItems += barrier.Before is null ? 1 : 2;
            if (failure is not null)
            {
                barrier.Ready?.TrySetException(failure);
            }
            else
            {
                barrier.Ready?.TrySetCanceled();
            }
        }

        _abandonedItems += (_latest is null ? 0 : 1) + (_waitingLossless ? 1 : 0);
        _latest = null;
    }

    private void ThrowIfUnavailable()
    {
        _failure?.Throw();
        if (_closed)
        {
            throw new InvalidOperationException("The event delivery intake is closed.");
        }
    }

    private long ElapsedMilliseconds(long started)
    {
        double milliseconds = _time.GetElapsedTime(started).TotalMilliseconds;
        return milliseconds <= 0 ? 0 : milliseconds >= long.MaxValue ? long.MaxValue : (long)milliseconds;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Pulse(ref TaskCompletionSource signal)
    {
        TaskCompletionSource previous = signal;
        signal = Signal();
        previous.TrySetResult();
    }

    private sealed record PendingSnapshot(WorkerEventDeliveryInput Input, long FirstSequence);
    private sealed record Barrier(WorkerEventDeliveryInput Input, PendingSnapshot? Before, TaskCompletionSource? Ready);
}
