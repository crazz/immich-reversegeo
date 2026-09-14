using System;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Web.WorkerEventDelivery;

internal sealed class WorkerEventDeliveryScope(
    InternalWorkerProtocolVersion version,
    WorkerJobContext context)
{
    internal InternalWorkerProtocolVersion Version { get; } = version;
    internal WorkerJobContext Context { get; } = context;
}

internal sealed record WorkerEventDeliveryInput(
    InternalWorkerProtocolVersion Version,
    WorkerJobOutputMessage Message,
    WorkerProtocolEvent? CompatibilityEvent = null);

// The queue authenticates this exact object only during its one projection call.
// Neither wire data nor a copied/replayed object can authorize a sequence jump.
internal sealed class WorkerEventDelivery(
    WorkerEventDeliveryQueue issuer,
    WorkerEventDeliveryInput input,
    long? suppressedStart,
    long? suppressedEnd)
{
    internal WorkerEventDeliveryInput Input { get; } = input;
    internal long? SuppressedStart { get; } = suppressedStart;
    internal long? SuppressedEnd { get; } = suppressedEnd;

    internal long ValidateAdvance(WorkerEventDeliveryScope scope, long lastSequence)
    {
        if (!issuer.Authenticates(this, scope))
        {
            throw new InvalidOperationException("The event delivery does not own this projection.");
        }

        long sequence = Input.Message.Sequence;
        if (SuppressedStart is null && SuppressedEnd is null
            && lastSequence < long.MaxValue && sequence == lastSequence + 1)
        {
            return 0;
        }

        if (SuppressedStart is not { } first || SuppressedEnd is not { } last
            || lastSequence == long.MaxValue || first != lastSequence + 1
            || first > last || last == long.MaxValue || sequence != last + 1
            || !WorkerEventDeliveryPolicy.IsReplaceable(issuer.Descriptor, Input.Message))
        {
            throw new InvalidOperationException("The event delivery has an unexplained sequence gap.");
        }

        return sequence - first;
    }
}

internal enum WorkerEventDeliveryFinality
{
    Open,
    Terminal,
    Nonterminal,
    Abandoned,
    ProjectionFailed
}

internal sealed class StaleWorkerEventDeliveryException() : InvalidOperationException(
    "The accepted event no longer owns its read-model projection.");

internal sealed record WorkerEventDeliveryObservation(
    long AcceptedSnapshots,
    long AcceptedLossless,
    long ReplacedSnapshots,
    long DeliveredSnapshots,
    long DeliveredLossless,
    int FifoHighWater,
    long EnqueueWaits,
    long EnqueueWaitMilliseconds,
    long ProjectionMilliseconds,
    long? TerminalFlushMilliseconds,
    long AbandonedItems,
    long StaleRejected,
    WorkerEventDeliveryFinality Finality);
