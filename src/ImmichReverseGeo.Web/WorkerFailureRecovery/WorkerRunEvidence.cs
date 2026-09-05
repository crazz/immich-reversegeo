using System;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;

namespace ImmichReverseGeo.Web.WorkerFailureRecovery;

internal enum WorkerRunTransportPhase
{
    Admitted, Resolving, Starting, PreReady, Ready, Accepted, Draining, EvidenceFinal, Released
}

internal enum WorkerRunCommitPhase
{
    Uncommitted, TerminalValidated, Committed
}

internal sealed class WorkerRunFinalityState
{
    private readonly object _gate = new();
    private WorkerRunTransportPhase _transport;
    private WorkerRunCommitPhase _commit;

    internal (WorkerRunTransportPhase Transport, WorkerRunCommitPhase Commit) Snapshot
    {
        get
        {
            lock (_gate)
            {
                return (_transport, _commit);
            }
        }
    }

    internal void AdvanceTransport(WorkerRunTransportPhase phase)
    {
        lock (_gate)
        {
            if (phase > _transport)
            {
                _transport = phase;
            }
        }
    }

    internal void AdvanceCommit(WorkerRunCommitPhase phase)
    {
        lock (_gate)
        {
            if (phase > _commit)
            {
                _commit = phase;
            }
        }
    }
}

internal enum WorkerRunFailureCategory
{
    Terminal,
    CommandResolution,
    ProcessStart,
    ReadyTimeout,
    PreReadyEndOfStream,
    StartupCrash,
    ExitObservation,
    ReadyRejected,
    ExecuteSerialization,
    ExecuteWrite,
    ExecuteFlush,
    MalformedFrame,
    InvalidEncoding,
    OversizedFrame,
    UnknownOrIncompatible,
    Readiness,
    Sequence,
    Correlation,
    Lifecycle,
    ProgressConsistency,
    TerminalConsistency,
    ActivityCardinality,
    ProjectionFailure,
    OutputTransport,
    Infrastructure,
    InvalidInput,
    BusyWithoutTerminal,
    ExecutionFailure,
    ManagedCancellation,
    ForcedTermination,
    KillRejected,
    MissingTerminal,
    UnmappedExit,
    InconsistentExit,
    Crash
}

[Flags]
internal enum WorkerRunAnomaly
{
    None = 0,
    TerminalExitMismatch = 1,
    ProtocolAfterTerminal = 2,
    ProjectionAfterTerminal = 4,
    OutputTransport = 8,
    InputTransport = 16,
    CleanupFailure = 32,
    ShutdownAfterTerminal = 64,
    ForcedTermination = 128,
    KillRejected = 256,
    MissingTerminal = 512
}

internal enum WorkerRunAuthority
{
    CommittedReceipt,
    ValidatedTerminal,
    ControlPlane
}

/// <summary>A frozen snapshot, constructed only after no-process failure or physical exit and both pumps.</summary>
internal sealed record WorkerRunEvidence
{
    internal required ProcessingRunRequest Request { get; init; }
    internal required WorkerRunTransportPhase LastPhase { get; init; }
    internal WorkerRunFailureCategory? NoProcessFailure { get; init; }
    internal ChildWorkerCompletionObservation? Completion { get; init; }
    internal ProcessingRunFinalizationReceipt? Receipt { get; init; }
    internal WorkerEventStateBridgeObservation? BridgeObservation { get; init; }
    internal ChildWorkerCancellationFacts? Cancellation { get; init; }
    // Only an owned managed source may supply this. Never reconstruct it from Completion.ExitCode.
    internal WorkerProcessExitFact? ManagedExit { get; init; }
    internal bool CleanupFailed { get; init; }
    internal bool ShutdownRequested { get; init; }
}

internal sealed record WorkerRunDecision(
    ProcessingRunOutcome Outcome,
    WorkerRunAuthority Authority,
    WorkerRunFailureCategory Category,
    WorkerRunTransportPhase Phase,
    WorkerRunAnomaly Anomalies,
    ProcessingRunResult? TerminalResult)
{
    internal bool Retry => false;
}
