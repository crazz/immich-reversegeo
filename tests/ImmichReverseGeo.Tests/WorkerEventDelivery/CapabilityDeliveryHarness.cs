using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventDelivery;

namespace ImmichReverseGeo.Tests.WorkerEventDelivery;

// A raw-validator-to-real-page-sink fixture. Process transport is covered separately.
internal sealed class CapabilityDeliveryHarness : IAsyncDisposable
{
    private readonly WorkerJobOutputStreamValidator _primary;
    private readonly TaskCompletionSource _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal WorkerEventDeliveryQueue Queue { get; }
    internal Task Acknowledged => _acknowledged.Task;

    internal CapabilityDeliveryHarness(IWorkerJobAdmissionLease lease, IWorkerJobEventSink sink,
        TimeProvider time, long acknowledgedSequence)
    {
        _primary = new(lease.Context.JobId, lease.Context.JobKind);
        var accepted = Assert.IsInstanceOfType<IAcceptedWorkerEventSink>(sink);
        var scope = new WorkerEventDeliveryScope(InternalWorkerProtocolVersion.V2, lease.Context);
        accepted.BindDeliveryScope(scope);
        Queue = new(scope, lease.Descriptor, new WorkerEventDeliveryPolicy { LosslessCapacity = 1 }, time,
            async (delivery, token) =>
            {
                await accepted.AcceptDeliveryAsync(delivery, token);
                if (delivery.Input.Message.Sequence == acknowledgedSequence)
                {
                    _acknowledged.TrySetResult();
                }
            });
    }

    internal Task SendAsync(WorkerJobOutputMessage message)
    {
        var result = _primary.Validate(message);
        Assert.IsTrue(result.IsSuccess, result.Failure?.Diagnostic);
        return Queue.EnqueueAsync(new(InternalWorkerProtocolVersion.V2, message), CancellationToken.None).AsTask();
    }

    // Build the source frame before primary acceptance; no delivered frame is renumbered.
    internal Task SendAsync(WorkerJobOutputMessage template, long sourceSequence) => SendAsync(new(
        template.Category, template.Type, sourceSequence, template.TimestampUtc,
        template.JobId, template.JobKind, template.Payload));

    public ValueTask DisposeAsync() => Queue.DisposeAsync();
}
