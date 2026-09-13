using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using AcceptedDelivery = ImmichReverseGeo.Web.WorkerEventDelivery.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.WorkerProcessFailureMatrix;

// Observe only after the real consumer accepted delivery. Preserve the queue's
// authenticated delivery object and cadence owner through this test boundary.
internal sealed class MatrixEventTap(IWorkerJobEventSink inner, MatrixExecutionControl? control = null) : IWorkerJobEventSink, IAcceptedWorkerEventSink
{
    private readonly object _gate = new();
    private readonly List<WorkerJobOutputMessage> _events = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ReadModelNotificationCadence.OwnerObservation? _notification;
    private IAcceptedWorkerEventSink Accepted => inner is ProcessAssetsWorkerJobEventSink processing
        ? processing.AcceptedDeliverySink ?? throw new AssertFailedException("Processing lost accepted delivery.")
        : (IAcceptedWorkerEventSink)inner;

    public ReadModelNotificationCadence.OwnerObservation? NotificationOwnerObservation =>
        _notification ??= Accepted.NotificationOwnerObservation;

    internal WorkerJobOutputMessage[] Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public void BindDeliveryScope(WorkerEventDeliveryScope scope) => Accepted.BindDeliveryScope(scope);

    public async ValueTask AcceptDeliveryAsync(AcceptedDelivery delivery, CancellationToken cancellationToken)
    {
        if (control is not null)
        {
            await control.BeforeProjectionAsync(delivery.Input.Message, cancellationToken);
        }
        await Accepted.AcceptDeliveryAsync(delivery, cancellationToken);
        Record(delivery.Input.Message);
    }

    public async ValueTask AcceptAsync(WorkerJobOutputMessage message, CancellationToken cancellationToken)
    {
        await inner.AcceptAsync(message, cancellationToken);
        Record(message);
    }

    internal async Task WaitForLogAsync(string marker)
    {
        await WaitForAsync(e => e.Payload is WorkerJobLogPayload log && log.Message == marker, marker);
    }

    internal Task<WorkerJobOutputMessage> WaitForTerminalAsync() =>
        WaitForAsync(e => e.Payload is WorkerJobTerminalPayload, "terminal");

    private async Task<WorkerJobOutputMessage> WaitForAsync(Func<WorkerJobOutputMessage, bool> predicate, string marker)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_events.FirstOrDefault(predicate) is { } found)
                {
                    return found;
                }

                changed = _changed.Task;
            }

            await MatrixWait.ForAsync(changed, "accepted-fixture-marker/" + marker);
        }
    }

    private void Record(WorkerJobOutputMessage message)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _events.Add(message);
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }
}
