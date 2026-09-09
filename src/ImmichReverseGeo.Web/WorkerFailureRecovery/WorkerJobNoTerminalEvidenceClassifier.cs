using System;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;

namespace ImmichReverseGeo.Web.WorkerFailureRecovery;

internal enum WorkerJobNoTerminalOutcome
{
    Cancelled,
    Failed
}

internal sealed record WorkerJobNoTerminalEvidence
{
    internal required WorkerJobContext Context { get; init; }
    internal InternalWorkerProtocolVersion IntendedProtocolVersion { get; init; } =
        InternalWorkerProtocolVersion.V2;
    internal required WorkerRunTransportPhase LastPhase { get; init; }
    internal WorkerRunFailureCategory? NoProcessFailure { get; init; }
    internal ChildWorkerCompletionObservation? Completion { get; init; }
    internal WorkerProtocolFailure? ProjectedProtocolFailure { get; init; }
    internal bool ProjectionFailed { get; init; }
    internal ChildWorkerCancellationFacts? Cancellation { get; init; }
    internal WorkerProcessExitFact? ManagedExit { get; init; }
    internal bool CleanupFailed { get; init; }
}

internal sealed record WorkerJobNoTerminalDecision(
    WorkerJobNoTerminalOutcome Outcome,
    WorkerRunFailureCategory Category,
    WorkerRunAnomaly Anomalies);

/// <summary>
/// Applies the shared controller-side precedence only when no valid terminal has
/// already won. Capability adapters remain responsible for committing typed terminals.
/// </summary>
internal static class WorkerJobNoTerminalEvidenceClassifier
{
    internal static WorkerJobNoTerminalDecision Classify(WorkerJobNoTerminalEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Context);
        if ((evidence.Completion is null) == (evidence.NoProcessFailure is null)
            || evidence.NoProcessFailure is not (null or WorkerRunFailureCategory.CommandResolution or WorkerRunFailureCategory.ProcessStart)
            || !Enum.IsDefined(evidence.Context.JobKind)
            || !Enum.IsDefined(evidence.IntendedProtocolVersion)
            || (evidence.Completion is { } completion
                && (completion.JobId != evidence.Context.JobId
                    || completion.JobKind != evidence.Context.JobKind
                    || completion.ProtocolVersion != evidence.IntendedProtocolVersion)))
        {
            throw new ArgumentException(
                "Final evidence must identify exactly one matching session or no-process failure.",
                nameof(evidence));
        }

        if (evidence.NoProcessFailure is { } noProcess)
        {
            return Failed(noProcess);
        }

        ChildWorkerCompletionObservation raw = evidence.Completion!;
        WorkerRunFailureCategory? startupFailure = ClassifyStartup(raw.Startup);
        bool acceptedRunStartedOverridesExecuteTransportFailure = raw.AcceptedRunStarted
            && raw.Startup is ChildWorkerStartupObservation.RequestWriteFailed
                or ChildWorkerStartupObservation.RequestFlushFailed;
        if (startupFailure is not null
            && !acceptedRunStartedOverridesExecuteTransportFailure
            && !IsExpectedTerminationEnd(evidence, raw.Startup))
        {
            return Failed(startupFailure.Value);
        }

        if (evidence.ProjectedProtocolFailure is { } projectedProtocolFailure)
        {
            return Failed(ClassifyProtocol(projectedProtocolFailure));
        }

        if (raw.FirstProtocolObservation is ChildWorkerProtocolObservation.ProtocolFailure protocol
            && protocol.Failure.Detail != WorkerProtocolFailureDetail.MissingTerminal)
        {
            return Failed(ClassifyProtocol(protocol.Failure));
        }

        if (raw.StandardOutputFinality is ChildWorkerStreamFinality.ReadFailed
            || raw.StandardErrorFinality is ChildWorkerStreamFinality.ReadFailed
            || evidence.ManagedExit?.ExitCode == 6)
        {
            return Failed(WorkerRunFailureCategory.OutputTransport);
        }

        if (evidence.ProjectionFailed
            || raw.FirstProtocolObservation is ChildWorkerProtocolObservation.SinkFailure)
        {
            return Failed(WorkerRunFailureCategory.ProjectionFailure);
        }

        WorkerRunFailureCategory? managedFailure = evidence.ManagedExit?.ExitCode switch
        {
            5 => WorkerRunFailureCategory.Infrastructure,
            2 => WorkerRunFailureCategory.InvalidInput,
            3 when evidence.Context.JobKind == WorkerJobKind.ProcessAssets =>
                WorkerRunFailureCategory.BusyWithoutTerminal,
            3 => WorkerRunFailureCategory.InconsistentExit,
            4 => WorkerRunFailureCategory.ExecutionFailure,
            _ => null
        };
        if (managedFailure is not null)
        {
            return Failed(managedFailure.Value);
        }

        if (evidence.Cancellation is { } cancellation)
        {
            if (cancellation.KillAttempted
                && cancellation.KillOutcome is not (
                    ChildProcessKillOutcome.Requested or
                    ChildProcessKillOutcome.AlreadyExited))
            {
                return Failed(WorkerRunFailureCategory.KillRejected);
            }

            if (cancellation.FirstIntent is
                ChildWorkerTerminationIntent.Stop or
                ChildWorkerTerminationIntent.Shutdown)
            {
                if (cancellation.GraceExpired
                    && cancellation.KillAttempted
                    && cancellation.KillOutcome == ChildProcessKillOutcome.Requested)
                {
                    return new WorkerJobNoTerminalDecision(
                        WorkerJobNoTerminalOutcome.Cancelled,
                        WorkerRunFailureCategory.ForcedTermination,
                        WorkerRunAnomaly.ForcedTermination | WorkerRunAnomaly.MissingTerminal);
                }

                if (evidence.ManagedExit?.ExitCode == 130)
                {
                    return new WorkerJobNoTerminalDecision(
                        WorkerJobNoTerminalOutcome.Cancelled,
                        WorkerRunFailureCategory.ManagedCancellation,
                        cancellation.RequestAccepted
                            ? WorkerRunAnomaly.MissingTerminal
                            : WorkerRunAnomaly.None);
                }
            }
        }

        if (evidence.CleanupFailed)
        {
            return Failed(WorkerRunFailureCategory.Infrastructure);
        }

        if (!raw.ExitObserved || raw.ExitCode is null)
        {
            return Failed(WorkerRunFailureCategory.Crash);
        }

        return Failed(raw.ExitCode switch
        {
            0 => WorkerRunFailureCategory.MissingTerminal,
            2 or 3 or 4 or 5 or 6 or 130 => WorkerRunFailureCategory.InconsistentExit,
            _ => WorkerRunFailureCategory.UnmappedExit
        });
    }

    internal static WorkerRunFailureCategory ClassifyProtocol(WorkerProtocolFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Code switch
        {
            WorkerProtocolFailureCode.MessageTooLarge => WorkerRunFailureCategory.OversizedFrame,
            WorkerProtocolFailureCode.InvalidEncoding => WorkerRunFailureCategory.InvalidEncoding,
            WorkerProtocolFailureCode.InvalidFraming or WorkerProtocolFailureCode.MalformedJson
                or WorkerProtocolFailureCode.InvalidEnvelope or WorkerProtocolFailureCode.InvalidPayload =>
                WorkerRunFailureCategory.MalformedFrame,
            WorkerProtocolFailureCode.UnsupportedProtocol or WorkerProtocolFailureCode.UnsupportedVersion
                or WorkerProtocolFailureCode.UnsupportedType => WorkerRunFailureCategory.UnknownOrIncompatible,
            WorkerProtocolFailureCode.InvalidSequence => WorkerRunFailureCategory.Sequence,
            WorkerProtocolFailureCode.InvalidCorrelation => WorkerRunFailureCategory.Correlation,
            WorkerProtocolFailureCode.InvalidLifecycle => failure.Detail switch
            {
                WorkerProtocolFailureDetail.Readiness => WorkerRunFailureCategory.Readiness,
                WorkerProtocolFailureDetail.ProgressConsistency => WorkerRunFailureCategory.ProgressConsistency,
                WorkerProtocolFailureDetail.TerminalConsistency => WorkerRunFailureCategory.TerminalConsistency,
                WorkerProtocolFailureDetail.ActivityCardinality => WorkerRunFailureCategory.ActivityCardinality,
                WorkerProtocolFailureDetail.MissingTerminal => WorkerRunFailureCategory.MissingTerminal,
                _ => WorkerRunFailureCategory.Lifecycle
            },
            _ => WorkerRunFailureCategory.Lifecycle
        };
    }

    private static bool IsExpectedTerminationEnd(
        WorkerJobNoTerminalEvidence evidence,
        ChildWorkerStartupObservation startup)
    {
        if (startup is not (
                ChildWorkerStartupObservation.PreReadyEndOfStream or
                ChildWorkerStartupObservation.PreReadyExit)
            || evidence.Cancellation is not
                { FirstIntent: ChildWorkerTerminationIntent.Stop or ChildWorkerTerminationIntent.Shutdown } cancellation)
        {
            return false;
        }

        return evidence.ManagedExit?.ExitCode == 130
            || (cancellation.GraceExpired
                && cancellation.KillAttempted
                && cancellation.KillOutcome == ChildProcessKillOutcome.Requested);
    }

    private static WorkerRunFailureCategory? ClassifyStartup(
        ChildWorkerStartupObservation startup)
    {
        return startup switch
        {
            ChildWorkerStartupObservation.PostStartSetupFailed => WorkerRunFailureCategory.Infrastructure,
            ChildWorkerStartupObservation.ReadyTimedOut => WorkerRunFailureCategory.ReadyTimeout,
            ChildWorkerStartupObservation.PreReadyEndOfStream => WorkerRunFailureCategory.PreReadyEndOfStream,
            ChildWorkerStartupObservation.PreReadyExit => WorkerRunFailureCategory.StartupCrash,
            ChildWorkerStartupObservation.PreReadyExitObservationFailed => WorkerRunFailureCategory.ExitObservation,
            ChildWorkerStartupObservation.PreReadyReadFailed => WorkerRunFailureCategory.OutputTransport,
            ChildWorkerStartupObservation.ProtocolFailure protocol => ClassifyProtocol(protocol.Failure),
            ChildWorkerStartupObservation.SinkFailed => WorkerRunFailureCategory.ReadyRejected,
            ChildWorkerStartupObservation.RequestSerializationFailed => WorkerRunFailureCategory.ExecuteSerialization,
            ChildWorkerStartupObservation.RequestWriteFailed => WorkerRunFailureCategory.ExecuteWrite,
            ChildWorkerStartupObservation.RequestFlushFailed => WorkerRunFailureCategory.ExecuteFlush,
            ChildWorkerStartupObservation.Disposed => WorkerRunFailureCategory.Infrastructure,
            _ => null
        };
    }

    private static WorkerJobNoTerminalDecision Failed(WorkerRunFailureCategory category)
    {
        return new WorkerJobNoTerminalDecision(
            WorkerJobNoTerminalOutcome.Failed,
            category,
            WorkerRunAnomaly.None);
    }
}
