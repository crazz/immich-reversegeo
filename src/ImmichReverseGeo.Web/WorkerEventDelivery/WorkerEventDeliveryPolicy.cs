using System;
using System.Threading;
using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Web.WorkerEventDelivery;

internal sealed record WorkerEventDeliveryPolicy
{
    internal const int DefaultLosslessCapacity = 256;
    internal static readonly TimeSpan DefaultNotificationCadence = TimeSpan.FromMilliseconds(100);

    // Selected after real-process/component measurements; see the maintainer record.
    internal static bool ProductionEnabled => true;
    internal int LosslessCapacity { get; init; } = DefaultLosslessCapacity;
    internal TimeSpan NotificationCadence { get; init; } = DefaultNotificationCadence;

    internal void Validate()
    {
        if (LosslessCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(LosslessCapacity));
        }

        if (NotificationCadence <= TimeSpan.Zero || NotificationCadence == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(NotificationCadence));
        }
    }

    internal static bool IsReplaceable(WorkerJobDescriptor descriptor, WorkerJobOutputMessage message)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(message);
        return ReferenceEquals(descriptor, WorkerJobDescriptors.ProcessAssets)
            && descriptor.ProgressReplaceability == WorkerProgressReplaceability.ProcessAssetsAbsoluteCounts
            && message.JobKind == WorkerJobKind.ProcessAssets
            && message.Type == WorkerJobProtocolV2.ProgressChangedType
            && message.Payload is ProcessAssetsProgressPayload;
    }
}
