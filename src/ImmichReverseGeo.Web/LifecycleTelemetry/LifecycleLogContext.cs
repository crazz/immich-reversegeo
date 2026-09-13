using System;
using ImmichReverseGeo.Core.ApplicationRole;
using ImmichReverseGeo.Core.WorkerJobs;
using Role = ImmichReverseGeo.Core.ApplicationRole.ApplicationRole;
using Mode = ImmichReverseGeo.Core.ApplicationRole.DeploymentMode;

namespace ImmichReverseGeo.Web.LifecycleTelemetry;

internal sealed record WorkerJobLogContext
{
    private WorkerJobLogContext(Guid jobId, string kind, string origin, int controllerPid, int? workerPid)
    {
        JobId = jobId;
        Kind = kind;
        Origin = origin;
        ControllerProcessId = controllerPid;
        WorkerProcessId = workerPid;
    }

    internal Guid JobId { get; }
    internal string Kind { get; }
    internal string Origin { get; }
    internal int ControllerProcessId { get; }
    internal int? WorkerProcessId { get; private init; }

    internal static WorkerJobLogContext Create(WorkerJobContext context, int controllerPid)
    {
        ArgumentNullException.ThrowIfNull(context);
        string origin = (context.JobKind, context.Origin) switch
        {
            (WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Manual) => "dashboard-manual",
            (WorkerJobKind.ProcessAssets, WorkerJobRequestOrigin.Scheduled) => "scheduler",
            (WorkerJobKind.CoordinateLookup, WorkerJobRequestOrigin.Manual) => "lookup-ui",
            (WorkerJobKind.CacheMutation, WorkerJobRequestOrigin.Manual) => "cache-ui",
            _ => throw new ArgumentException("The context is not a supported child-job origin.", nameof(context))
        };
        return new(context.JobId, WorkerJobKindNames.Format(context.JobKind), origin, controllerPid, null);
    }

    internal WorkerJobLogContext WithProcess(int processId) => this with { WorkerProcessId = processId };
}

internal sealed record RoleLogContext
{
    internal RoleLogContext(Role role, Mode? mode, int processId)
    {
        if (ReferenceEquals(role, Role.InternalWorker) && mode is null)
        {
            ApplicationRole = "internal-worker";
            ReadinessKind = "worker-protocol-ready";
        }
        else if (ReferenceEquals(role, Role.Web)
            && (ReferenceEquals(mode, Mode.Standard) || ReferenceEquals(mode, Mode.WebOnly)))
        {
            ApplicationRole = "web";
            DeploymentMode = ReferenceEquals(mode, Mode.Standard) ? "standard" : "web-only";
            ReadinessKind = "web-listening";
        }
        else if (ReferenceEquals(role, Role.RunOnce) && ReferenceEquals(mode, Mode.RunOnce))
        {
            ApplicationRole = "run-once";
            DeploymentMode = "run-once";
            ReadinessKind = "run-once-initialized";
        }
        else
        {
            throw new ArgumentException("The role and deployment mode must match the selected composition.");
        }

        ProcessId = processId;
    }

    internal string ApplicationRole { get; }
    internal string? DeploymentMode { get; }
    internal string ReadinessKind { get; }
    internal int ProcessId { get; }
}

internal enum RoleStopReason { Completed, HostShutdown, StartupFailure, FatalFailure }
internal enum WorkerCancellationPhase { Starting, Ready, Running, Finalizing }
internal enum WorkerProtocolLogDirection { WorkerOutput, ControllerInput }
internal enum WorkerProtocolLogPhase { Ready, Execute, Events, Terminal, Drain }
internal enum WorkerLogClassification
{
    Completed, Cancelled, Busy, WorkerFailed, StartupFailed, ProtocolFailed,
    TransportFailed, MissingTerminal, TerminalExitMismatch, ForcedStop, Crashed, InfrastructureFailed
}
