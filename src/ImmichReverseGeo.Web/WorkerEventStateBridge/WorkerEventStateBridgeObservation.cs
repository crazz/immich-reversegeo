using System;
using ImmichReverseGeo.Core.WorkerProtocol;

namespace ImmichReverseGeo.Web.WorkerEventStateBridge;

internal abstract class WorkerEventStateBridgeObservation
{
    private WorkerEventStateBridgeObservation()
    {
    }

    internal sealed class EventRejected : WorkerEventStateBridgeObservation
    {
        internal EventRejected(WorkerProtocolFailure failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            Failure = failure;
        }

        internal WorkerProtocolFailure Failure { get; }
    }

    internal sealed class ProjectionFailed : WorkerEventStateBridgeObservation
    {
        internal ProjectionFailed(string diagnostic)
        {
            if (string.IsNullOrWhiteSpace(diagnostic) || diagnostic.Length > 256)
            {
                throw new ArgumentException("A bridge diagnostic must be non-blank and bounded.", nameof(diagnostic));
            }

            Diagnostic = diagnostic;
        }

        internal string Diagnostic { get; }
    }

    internal sealed class NonterminalDisposed : WorkerEventStateBridgeObservation
    {
        internal static NonterminalDisposed Instance { get; } = new();

        private NonterminalDisposed()
        {
        }
    }
}

internal sealed class WorkerEventStateBridgeException : Exception
{
    internal WorkerEventStateBridgeException(WorkerEventStateBridgeObservation observation)
        : base(GetMessage(observation))
    {
        Observation = observation;
    }

    internal WorkerEventStateBridgeObservation Observation { get; }

    private static string GetMessage(WorkerEventStateBridgeObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return observation switch
        {
            WorkerEventStateBridgeObservation.EventRejected rejected => rejected.Failure.Diagnostic,
            WorkerEventStateBridgeObservation.ProjectionFailed failed => failed.Diagnostic,
            WorkerEventStateBridgeObservation.NonterminalDisposed => "The worker event stream ended without a terminal event.",
            _ => "Worker event state projection failed."
        };
    }
}
