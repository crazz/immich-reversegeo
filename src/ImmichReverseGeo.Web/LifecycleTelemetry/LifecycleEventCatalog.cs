using System;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerEventDelivery;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.LifecycleTelemetry;

internal static class LifecycleEventCatalog
{
    internal const string Category = "ImmichReverseGeo.Lifecycle";
    private const string JobFields = " job_id={job_id} job_kind={job_kind} job_origin={job_origin} controller_process_id={controller_process_id} worker_process_id={worker_process_id}";
    private const string RoleFields = " application_role={application_role} deployment_mode={deployment_mode} process_id={process_id}";

    internal static void ModeSelected(ILogger logger, RoleLogContext context)
    {
        if (context.DeploymentMode is not null)
        {
            Write(logger, 6601, "DeploymentModeSelected", LogLevel.Information,
                "Deployment mode selected deployment_mode={deployment_mode} application_role={application_role} process_id={process_id}",
                context.DeploymentMode, context.ApplicationRole, context.ProcessId);
        }
    }

    internal static void RoleStarting(ILogger logger, RoleLogContext context) =>
        Write(logger, 6602, "RoleProcessStarting", LogLevel.Information, "Role starting" + RoleFields,
            context.ApplicationRole, context.DeploymentMode, context.ProcessId);

    internal static void RoleReady(ILogger logger, RoleLogContext context, long startupMilliseconds) =>
        Write(logger, 6603, "RoleProcessReady", LogLevel.Information,
            "Role ready" + RoleFields + " readiness_kind={readiness_kind} startup_duration_ms={startup_duration_ms}",
            context.ApplicationRole, context.DeploymentMode, context.ProcessId, context.ReadinessKind, Nonnegative(startupMilliseconds));

    internal static void RoleStopping(ILogger logger, RoleLogContext context, RoleStopReason reason) =>
        Write(logger, 6604, "RoleProcessStopping", LogLevel.Information,
            "Role stopping" + RoleFields + " stop_reason={stop_reason}",
            context.ApplicationRole, context.DeploymentMode, context.ProcessId, StopReason(reason));

    internal static void RoleStopped(ILogger logger, RoleLogContext context, RoleStopReason reason,
        long processMilliseconds, long stopMilliseconds) =>
        Write(logger, 6605, "RoleProcessStopped",
            reason is RoleStopReason.Completed or RoleStopReason.HostShutdown ? LogLevel.Information : LogLevel.Warning,
            "Role stopped" + RoleFields + " process_outcome={process_outcome} process_duration_ms={process_duration_ms} stop_duration_ms={stop_duration_ms}",
            context.ApplicationRole, context.DeploymentMode, context.ProcessId,
            reason switch { RoleStopReason.Completed => "completed", RoleStopReason.HostShutdown => "cancelled", _ => "failed" },
            Nonnegative(processMilliseconds), Nonnegative(stopMilliseconds));

    internal static void LaunchStarted(ILogger logger, WorkerJobLogContext context) =>
        Job(logger, 6610, "WorkerJobLaunchStarted", LogLevel.Information, context, "Worker launch started" + JobFields);

    internal static void ProcessStarted(ILogger logger, WorkerJobLogContext context, long milliseconds) =>
        Job(logger, 6611, "WorkerJobProcessStarted", LogLevel.Information, context,
            "Worker process started" + JobFields + " process_start_duration_ms={process_start_duration_ms}", Nonnegative(milliseconds));

    internal static void WorkerReady(ILogger logger, WorkerJobLogContext context, long readinessMilliseconds, long startupMilliseconds) =>
        Job(logger, 6612, "WorkerJobReady", LogLevel.Information, context,
            "Worker ready" + JobFields + " readiness_duration_ms={readiness_duration_ms} startup_duration_ms={startup_duration_ms}",
            Nonnegative(readinessMilliseconds), Nonnegative(startupMilliseconds));

    internal static void CancellationRequested(ILogger logger, WorkerJobLogContext context,
        ChildWorkerTerminationIntent intent, WorkerCancellationPhase phase) =>
        Job(logger, 6620, "WorkerJobCancellationRequested", LogLevel.Information, context,
            "Worker cancellation requested" + JobFields + " cancellation_reason={cancellation_reason} cancellation_phase={cancellation_phase} grace_period_ms={grace_period_ms}",
            intent switch
            {
                ChildWorkerTerminationIntent.Stop => "user",
                ChildWorkerTerminationIntent.Shutdown => "web-host-shutdown",
                _ => "controller-fault"
            },
            phase switch
            {
                WorkerCancellationPhase.Starting => "starting",
                WorkerCancellationPhase.Ready => "ready",
                WorkerCancellationPhase.Running => "running",
                _ => "finalizing"
            },
            (long)ChildWorkerCancellationPolicy.Grace.TotalMilliseconds);

    internal static void GraceCompleted(ILogger logger, WorkerJobLogContext context, long milliseconds, bool terminalObserved) =>
        Job(logger, 6621, "WorkerJobCancellationGraceCompleted", LogLevel.Information, context,
            "Worker cancellation grace completed" + JobFields + " cancellation_duration_ms={cancellation_duration_ms} terminal_observed={terminal_observed} exit_observed={exit_observed}",
            Nonnegative(milliseconds), terminalObserved, true);

    internal static void Escalated(ILogger logger, WorkerJobLogContext context, long elapsedMilliseconds) =>
        Job(logger, 6622, "WorkerJobCancellationEscalated", LogLevel.Warning, context,
            "Worker cancellation escalated" + JobFields + " grace_period_ms={grace_period_ms} grace_elapsed_ms={grace_elapsed_ms} escalation_action={escalation_action}",
            (long)ChildWorkerCancellationPolicy.Grace.TotalMilliseconds, Nonnegative(elapsedMilliseconds), "kill-process-tree");

    internal static void ForcedStopCompleted(ILogger logger, WorkerJobLogContext context, long milliseconds, ChildProcessKillOutcome outcome) =>
        Job(logger, 6623, "WorkerJobForcedStopCompleted", LogLevel.Warning, context,
            "Worker forced stop completed" + JobFields + " escalation_duration_ms={escalation_duration_ms} kill_result={kill_result}",
            Nonnegative(milliseconds), outcome switch
            {
                ChildProcessKillOutcome.Requested or ChildProcessKillOutcome.AlreadyExited => "succeeded",
                ChildProcessKillOutcome.Unsupported => "not-supported",
                _ => "failed"
            });

    internal static void ProtocolViolation(ILogger logger, WorkerJobLogContext context,
        WorkerProtocolLogDirection direction, WorkerProtocolLogPhase phase, WorkerProtocolFailureCode code, long? sequence) =>
        Job(logger, 6630, "WorkerProtocolViolation", LogLevel.Warning, context,
            "Worker protocol violation" + JobFields + " protocol_direction={protocol_direction} protocol_phase={protocol_phase} violation_code={violation_code} sequence={sequence}",
            direction == WorkerProtocolLogDirection.WorkerOutput ? "worker-output" : "controller-input",
            phase switch
            {
                WorkerProtocolLogPhase.Ready => "ready",
                WorkerProtocolLogPhase.Execute => "execute",
                WorkerProtocolLogPhase.Events => "events",
                WorkerProtocolLogPhase.Terminal => "terminal",
                _ => "drain"
            },
            ProtocolCode(code), sequence is >= 0 ? sequence : null);

    internal static void TerminalObserved(ILogger logger, WorkerJobLogContext context, WorkerJobTerminalOutcome outcome, long sequence) =>
        Job(logger, 6640, "WorkerJobTerminalObserved",
            outcome is WorkerJobTerminalOutcome.Completed or WorkerJobTerminalOutcome.Cancelled ? LogLevel.Information : LogLevel.Warning,
            context, "Worker terminal observed" + JobFields + " terminal_outcome={terminal_outcome} terminal_sequence={terminal_sequence}",
            Terminal(outcome), Nonnegative(sequence));

    internal static void ProcessClassified(ILogger logger, WorkerJobLogContext context, bool readyObserved,
        WorkerJobTerminalOutcome? terminal, WorkerLogClassification classification, int? exitCode,
        bool forcedStop, long totalMilliseconds, ChildWorkingSetSummary memory) =>
        Job(logger, 6641, "WorkerJobProcessClassified",
            classification is WorkerLogClassification.Completed or WorkerLogClassification.Cancelled ? LogLevel.Information : LogLevel.Warning,
            context, "Worker process classified" + JobFields
                + " ready_observed={ready_observed} terminal_outcome={terminal_outcome} process_classification={process_classification} exit_observation={exit_observation} exit_code={exit_code} forced_stop={forced_stop} total_duration_ms={total_duration_ms}"
                + " memory_scope={memory_scope} memory_sampling_method={memory_sampling_method} memory_sample_interval_ms={memory_sample_interval_ms} memory_observation={memory_observation} peak_working_set_bytes={peak_working_set_bytes} memory_sample_count={memory_sample_count} memory_unavailable_reason={memory_unavailable_reason}",
            readyObserved, terminal is { } outcome ? Terminal(outcome) : null, Classification(classification),
            exitCode is null ? "unavailable" : exitCode is 0 or 2 or 3 or 4 or 5 or 6 or 130 ? "managed" : "unmapped",
            exitCode, forcedStop, Nonnegative(totalMilliseconds),
            "worker-process-only", "parent-periodic-working-set-max-v1", 1000,
            memory.PeakBytes is null ? "unavailable" : "available", memory.PeakBytes, memory.SuccessfulSamples,
            memory.PeakBytes is null ? MemoryReason(memory.UnavailableReason) : null);

    internal static void CoalescingSaturated(ILogger logger, WorkerJobLogContext context,
        WorkerEventDeliveryObservation observation, long cadenceNotifications)
    {
        if (observation.Finality == WorkerEventDeliveryFinality.Open
            || (observation.EnqueueWaits == 0 && observation.ReplacedSnapshots == 0))
        {
            return;
        }

        bool terminal = observation.Finality == WorkerEventDeliveryFinality.Terminal;
        Job(logger, 6650, "WorkerEventCoalescingSaturated", LogLevel.Warning, context,
            "Worker event coalescing saturated" + JobFields
                + " finality_kind={finality_kind} accepted_replaceable_count={accepted_replaceable_count} accepted_lossless_count={accepted_lossless_count} replaced_count={replaced_count} delivered_snapshot_count={delivered_snapshot_count} fifo_high_water={fifo_high_water} enqueue_wait_count={enqueue_wait_count} enqueue_wait_duration_ms={enqueue_wait_duration_ms} projection_duration_ms={projection_duration_ms} cadence_notification_count={cadence_notification_count} terminal_flush_duration_ms={terminal_flush_duration_ms} stale_rejection_count={stale_rejection_count} abnormal_abandonment_count={abnormal_abandonment_count}",
            terminal ? "terminal" : "nonterminal", observation.AcceptedSnapshots, observation.AcceptedLossless,
            observation.ReplacedSnapshots, observation.DeliveredSnapshots, observation.FifoHighWater,
            observation.EnqueueWaits, observation.EnqueueWaitMilliseconds, observation.ProjectionMilliseconds,
            cadenceNotifications, terminal ? observation.TerminalFlushMilliseconds : null,
            observation.StaleRejected, observation.AbandonedItems);
    }

    internal static string Classification(WorkerLogClassification value) => value switch
    {
        WorkerLogClassification.Completed => "completed",
        WorkerLogClassification.Cancelled => "cancelled",
        WorkerLogClassification.Busy => "busy",
        WorkerLogClassification.WorkerFailed => "worker-failed",
        WorkerLogClassification.StartupFailed => "startup-failed",
        WorkerLogClassification.ProtocolFailed => "protocol-failed",
        WorkerLogClassification.TransportFailed => "transport-failed",
        WorkerLogClassification.MissingTerminal => "missing-terminal",
        WorkerLogClassification.TerminalExitMismatch => "terminal-exit-mismatch",
        WorkerLogClassification.ForcedStop => "forced-stop",
        WorkerLogClassification.Crashed => "crashed",
        _ => "infrastructure-failed"
    };

    private static string Terminal(WorkerJobTerminalOutcome outcome) => outcome switch
    {
        WorkerJobTerminalOutcome.Completed => "completed",
        WorkerJobTerminalOutcome.Cancelled => "cancelled",
        _ => "failed"
    };

    private static string StopReason(RoleStopReason value) => value switch
    {
        RoleStopReason.Completed => "completed",
        RoleStopReason.HostShutdown => "host-shutdown",
        RoleStopReason.StartupFailure => "startup-failure",
        _ => "fatal-failure"
    };

    private static string MemoryReason(ChildWorkingSetUnavailable value) => value switch
    {
        ChildWorkingSetUnavailable.NoSample => "no-sample",
        ChildWorkingSetUnavailable.ProcessExited => "process-exited",
        ChildWorkingSetUnavailable.AccessDenied => "access-denied",
        ChildWorkingSetUnavailable.NotSupported => "not-supported",
        _ => "sample-failed"
    };

    private static string ProtocolCode(WorkerProtocolFailureCode value) => value switch
    {
        WorkerProtocolFailureCode.MessageTooLarge => "message-too-large",
        WorkerProtocolFailureCode.InvalidEncoding => "invalid-encoding",
        WorkerProtocolFailureCode.InvalidFraming => "invalid-framing",
        WorkerProtocolFailureCode.MalformedJson => "malformed-json",
        WorkerProtocolFailureCode.InvalidEnvelope => "invalid-envelope",
        WorkerProtocolFailureCode.UnsupportedProtocol => "unsupported-protocol",
        WorkerProtocolFailureCode.UnsupportedVersion => "unsupported-version",
        WorkerProtocolFailureCode.UnsupportedType => "unsupported-type",
        WorkerProtocolFailureCode.InvalidPayload => "invalid-payload",
        WorkerProtocolFailureCode.InvalidSequence => "invalid-sequence",
        WorkerProtocolFailureCode.InvalidCorrelation => "invalid-correlation",
        _ => "invalid-lifecycle"
    };

    private static long Nonnegative(long value) => Math.Max(0, value);

    private static void Job(ILogger logger, int id, string name, LogLevel level, WorkerJobLogContext context,
        string template, params object?[] fields)
    {
        object?[] values = new object?[fields.Length + 5];
        values[0] = context.JobId;
        values[1] = context.Kind;
        values[2] = context.Origin;
        values[3] = context.ControllerProcessId;
        values[4] = context.WorkerProcessId;
        fields.CopyTo(values, 5);
        Write(logger, id, name, level, template, values);
    }

    private static void Write(ILogger logger, int id, string name, LogLevel level, string template, params object?[] values)
    {
        try
        {
            logger.Log(level, new EventId(id, name), template, values);
        }
        catch
        {
            // Diagnostics cannot decide execution, retry, or cleanup outcomes.
        }
    }
}
