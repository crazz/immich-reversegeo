using System;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

internal abstract class ChildWorkerFaultContainmentReason
{
    private ChildWorkerFaultContainmentReason()
    {
    }

    internal sealed class PostStartSetupFailed : ChildWorkerFaultContainmentReason
    {
        internal static PostStartSetupFailed Instance { get; } = new();

        private PostStartSetupFailed()
        {
        }
    }

    internal sealed class ReadyTimedOut : ChildWorkerFaultContainmentReason
    {
        internal static ReadyTimedOut Instance { get; } = new();

        private ReadyTimedOut()
        {
        }
    }

    internal sealed class ReadyRejected : ChildWorkerFaultContainmentReason
    {
        internal static ReadyRejected Instance { get; } = new();

        private ReadyRejected()
        {
        }
    }

    internal sealed class ExitObservationFailed : ChildWorkerFaultContainmentReason
    {
        internal static ExitObservationFailed Instance { get; } = new();

        private ExitObservationFailed()
        {
        }
    }

    internal sealed class RequestSerializationFailed : ChildWorkerFaultContainmentReason
    {
        internal static RequestSerializationFailed Instance { get; } = new();

        private RequestSerializationFailed()
        {
        }
    }

    internal sealed class RequestWriteFailed : ChildWorkerFaultContainmentReason
    {
        internal static RequestWriteFailed Instance { get; } = new();

        private RequestWriteFailed()
        {
        }
    }

    internal sealed class RequestFlushFailed : ChildWorkerFaultContainmentReason
    {
        internal static RequestFlushFailed Instance { get; } = new();

        private RequestFlushFailed()
        {
        }
    }

    internal sealed class ProtocolFailure : ChildWorkerFaultContainmentReason
    {
        internal ProtocolFailure(WorkerProtocolFailure failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            Failure = failure;
        }

        internal WorkerProtocolFailure Failure { get; }
    }

    internal sealed class StandardOutputReadFailed : ChildWorkerFaultContainmentReason
    {
        internal static StandardOutputReadFailed Instance { get; } = new();

        private StandardOutputReadFailed()
        {
        }
    }

    internal sealed class SinkFailure : ChildWorkerFaultContainmentReason
    {
        internal static SinkFailure Instance { get; } = new();

        private SinkFailure()
        {
        }
    }
}

internal sealed record ChildWorkerTerminalPreventingObservation
{
    internal ChildWorkerTerminalPreventingObservation(
        ChildWorkerStopRequest observedAt,
        ChildWorkerFaultContainmentReason reason)
    {
        ArgumentNullException.ThrowIfNull(observedAt);
        ArgumentNullException.ThrowIfNull(reason);
        ObservedAt = observedAt;
        Reason = reason;
    }

    internal ChildWorkerStopRequest ObservedAt { get; }
    internal ChildWorkerFaultContainmentReason Reason { get; }
}

internal sealed class ChildWorkerEvidenceFinalityGate
{
    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task WaitForReleaseAsync() => _released.Task;

    internal void Release() => _released.TrySetResult();
}
