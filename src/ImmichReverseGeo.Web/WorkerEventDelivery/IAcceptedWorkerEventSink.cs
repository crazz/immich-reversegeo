using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.WorkerEventDelivery;

// Implemented by the existing read-model owner, never by the wire reader.
internal interface IAcceptedWorkerEventSink
{
    ReadModelNotificationCadence.OwnerObservation? NotificationOwnerObservation { get; }
    void BindDeliveryScope(WorkerEventDeliveryScope scope);
    ValueTask AcceptDeliveryAsync(WorkerEventDelivery delivery, CancellationToken cancellationToken);
}
