using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Web.WorkerEventDelivery;

// Lookup/cache own their page state. This adapter owns only the delivery cursor;
// their progress steps remain lossless and cannot authorize a suppressed range.
internal sealed class AcceptedCapabilityEventSink : IWorkerJobEventSink, IAcceptedWorkerEventSink
{
    private readonly object _gate = new();
    private readonly WorkerJobContext _context;
    private readonly Func<WorkerJobOutputMessage, bool> _apply;
    private WorkerEventDeliveryScope? _scope;
    private WorkerJobOutputStreamValidator? _validator;
    private long _lastSequence;
    private bool _projected;

    internal AcceptedCapabilityEventSink(WorkerJobContext context, Func<WorkerJobOutputMessage, bool> apply)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(apply);
        if (context.JobKind is not (WorkerJobKind.CoordinateLookup or WorkerJobKind.CacheMutation))
        {
            throw new ArgumentException("Only page-owned worker capabilities use this projection.", nameof(context));
        }

        _context = context;
        _apply = apply;
    }

    public void BindDeliveryScope(WorkerEventDeliveryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            if (_projected || scope.Version != InternalWorkerProtocolVersion.V2
                || scope.Context != _context
                || (_scope is not null && !ReferenceEquals(scope, _scope)))
            {
                throw new InvalidOperationException("The delivery scope does not own this page operation.");
            }

            // Capability clients rebuild the transport context from the admitted
            // identity. The captured page closure still owns the exact lease
            // context/generation; this opaque scope is bound once by reference.
            _scope = scope;
            _validator ??= new(_context.JobId, _context.JobKind);
        }
    }

    public ValueTask AcceptAsync(WorkerJobOutputMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_scope is not null)
            {
                throw new InvalidOperationException("This page operation requires authenticated delivery.");
            }

            _projected = true;
            _apply(message);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask AcceptDeliveryAsync(WorkerEventDelivery delivery, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_scope is null || delivery.ValidateAdvance(_scope, _lastSequence) != 0)
            {
                throw new InvalidOperationException("This page operation requires an exact lossless delivery sequence.");
            }

            var result = _validator!.Validate(delivery.Input.Message);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException("The page delivery violates the accepted capability lifecycle.");
            }

            if (!_apply(delivery.Input.Message))
            {
                throw new StaleWorkerEventDeliveryException();
            }

            _projected = true;
            _lastSequence = delivery.Input.Message.Sequence;
            return ValueTask.CompletedTask;
        }
    }
}
