using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Web.LifecycleTelemetry;

// Maps a retained decision to closed log labels. Raw exit numbers never grant
// managed execution or cancellation authority on a path without a terminal.
internal static class WorkerClassificationLogProjection
{
    internal static WorkerLogClassification FromFinality(bool ready, WorkerJobTerminalOutcome? terminal,
        WorkerRunFailureCategory category, WorkerRunAnomaly anomalies, ChildWorkerCompletionObservation raw,
        ChildWorkerCancellationFacts? cancellation, bool terminalInputCloseFailed)
    {
        if (terminal is { } outcome)
        {
            if (raw.ExitObserved && raw.ExitCode is { } exit && !Agrees(outcome, exit))
            {
                return WorkerLogClassification.TerminalExitMismatch;
            }

            if ((anomalies & WorkerRunAnomaly.ProtocolAfterTerminal) != 0
                || raw.FirstProtocolObservation is ChildWorkerProtocolObservation.ProtocolFailure)
            {
                return WorkerLogClassification.ProtocolFailed;
            }

            if ((anomalies & (WorkerRunAnomaly.OutputTransport | WorkerRunAnomaly.InputTransport)) != 0
                || raw.StandardOutputFinality is ChildWorkerStreamFinality.ReadFailed
                || raw.StandardErrorFinality is ChildWorkerStreamFinality.ReadFailed
                || terminalInputCloseFailed
                || cancellation?.DeliveryPhase is ChildWorkerCancelDeliveryPhase.SerializationFailed
                    or ChildWorkerCancelDeliveryPhase.WriteFailed or ChildWorkerCancelDeliveryPhase.FlushFailed)
            {
                return WorkerLogClassification.TransportFailed;
            }

            if ((anomalies & (WorkerRunAnomaly.ProjectionAfterTerminal | WorkerRunAnomaly.CleanupFailure
                    | WorkerRunAnomaly.KillRejected)) != 0
                || raw.FirstProtocolObservation is ChildWorkerProtocolObservation.SinkFailure
                || (cancellation is { KillAttempted: true }
                    && cancellation.KillOutcome is not (ChildProcessKillOutcome.Requested or ChildProcessKillOutcome.AlreadyExited)))
            {
                return WorkerLogClassification.InfrastructureFailed;
            }

            if (cancellation?.KillAttempted == true)
            {
                return WorkerLogClassification.ForcedStop;
            }

            if (!raw.ExitObserved || raw.ExitCode is null)
            {
                return WorkerLogClassification.Crashed;
            }

            return raw.ExitCode switch
            {
                0 when ready => WorkerLogClassification.Completed,
                130 => WorkerLogClassification.Cancelled,
                3 when ready => WorkerLogClassification.Busy,
                4 when ready => WorkerLogClassification.WorkerFailed,
                _ => WorkerLogClassification.InfrastructureFailed
            };
        }

        return category switch
        {
            WorkerRunFailureCategory.CommandResolution or WorkerRunFailureCategory.ProcessStart
                or WorkerRunFailureCategory.ReadyTimeout or WorkerRunFailureCategory.PreReadyEndOfStream
                or WorkerRunFailureCategory.StartupCrash or WorkerRunFailureCategory.ReadyRejected =>
                ready ? WorkerLogClassification.InfrastructureFailed : WorkerLogClassification.StartupFailed,
            WorkerRunFailureCategory.ExecuteWrite or WorkerRunFailureCategory.ExecuteFlush =>
                ready ? WorkerLogClassification.TransportFailed : WorkerLogClassification.StartupFailed,
            WorkerRunFailureCategory.ExecuteSerialization =>
                ready ? WorkerLogClassification.InfrastructureFailed : WorkerLogClassification.StartupFailed,
            WorkerRunFailureCategory.OutputTransport => WorkerLogClassification.TransportFailed,
            WorkerRunFailureCategory.ProjectionFailure or WorkerRunFailureCategory.Infrastructure
                or WorkerRunFailureCategory.KillRejected => WorkerLogClassification.InfrastructureFailed,
            WorkerRunFailureCategory.ManagedCancellation => WorkerLogClassification.Cancelled,
            WorkerRunFailureCategory.ForcedTermination => WorkerLogClassification.ForcedStop,
            WorkerRunFailureCategory.ExecutionFailure => ready && raw.ExitObserved && raw.ExitCode == 4
                ? WorkerLogClassification.WorkerFailed : WorkerLogClassification.InfrastructureFailed,
            WorkerRunFailureCategory.MissingTerminal => ready
                ? WorkerLogClassification.MissingTerminal : WorkerLogClassification.StartupFailed,
            WorkerRunFailureCategory.UnmappedExit or WorkerRunFailureCategory.Crash
                or WorkerRunFailureCategory.ExitObservation => WorkerLogClassification.Crashed,
            WorkerRunFailureCategory.Terminal => WorkerLogClassification.InfrastructureFailed,
            _ => WorkerLogClassification.ProtocolFailed
        };
    }

    private static bool Agrees(WorkerJobTerminalOutcome terminal, int exitCode) => terminal switch
    {
        WorkerJobTerminalOutcome.Completed => exitCode == 0,
        WorkerJobTerminalOutcome.Cancelled => exitCode == 130,
        WorkerJobTerminalOutcome.Failed => exitCode is 3 or 4 or 5,
        _ => false
    };
}
