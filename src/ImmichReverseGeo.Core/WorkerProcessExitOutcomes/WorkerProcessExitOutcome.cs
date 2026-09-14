using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ImmichReverseGeo.Core.WorkerProcessExitOutcomes;

/// <summary>Defines one closed worker exit outcome category.</summary>
public sealed class WorkerProcessExitOutcome
{
    private static readonly WorkerProcessExitOutcome CompletedValue = new(WorkerProcessExitCodes.Completed, 0, "completed", "worker completed");
    private static readonly WorkerProcessExitOutcome CancelledValue = new(WorkerProcessExitCodes.Cancelled, 1, "cancelled", "worker cancellation or shutdown observed");
    private static readonly WorkerProcessExitOutcome ExecutionFailureValue = new(WorkerProcessExitCodes.ExecutorFailure, 2, "executor-failure", "worker executor failed");
    private static readonly WorkerProcessExitOutcome BusyValue = new(WorkerProcessExitCodes.Busy, 3, "busy", "worker advisory lock is busy");
    private static readonly WorkerProcessExitOutcome InvalidInputValue = new(WorkerProcessExitCodes.InvalidInput, 4, "invalid-input", "worker invocation or input is invalid");
    private static readonly WorkerProcessExitOutcome InfrastructureFailureValue = new(WorkerProcessExitCodes.InfrastructureFailure, 5, "infrastructure-failure", "worker infrastructure failed");
    private static readonly WorkerProcessExitOutcome OutputTransportFailureValue = new(WorkerProcessExitCodes.OutputTransportFailure, 6, "output-transport-failure", "worker output transport failed");

    private static readonly IReadOnlyList<WorkerProcessExitOutcome> Values = new ReadOnlyCollection<WorkerProcessExitOutcome>(
    [
        CompletedValue,
        CancelledValue,
        ExecutionFailureValue,
        BusyValue,
        InvalidInputValue,
        InfrastructureFailureValue,
        OutputTransportFailureValue
    ]);

    private WorkerProcessExitOutcome(int exitCode, int rank, string token, string message)
    {
        ExitCode = exitCode;
        Rank = rank;
        Token = token;
        Message = message;
    }

    /// <summary>Gets every supported portable worker exit outcome category.</summary>
    public static IReadOnlyList<WorkerProcessExitOutcome> All => Values;

    /// <summary>Gets the portable process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Gets the stable safe outcome token.</summary>
    public string Token { get; }

    internal int Rank { get; }

    internal string Message { get; }

    internal static WorkerProcessExitOutcome CompletedCategory => CompletedValue;

    internal static WorkerProcessExitOutcome CancelledCategory => CancelledValue;

    internal static WorkerProcessExitOutcome ExecutionFailureCategory => ExecutionFailureValue;

    internal static WorkerProcessExitOutcome BusyCategory => BusyValue;

    internal static WorkerProcessExitOutcome InvalidInputCategory => InvalidInputValue;

    internal static WorkerProcessExitOutcome InfrastructureFailureCategory => InfrastructureFailureValue;

    internal static WorkerProcessExitOutcome OutputTransportFailureCategory => OutputTransportFailureValue;
}

/// <summary>Defines one closed, orderly worker exit fact.</summary>
public sealed class WorkerProcessExitFact
{
    private static readonly WorkerProcessExitFact StartupInfrastructureFact = Create(WorkerProcessExitOutcome.InfrastructureFailureCategory, WorkerProcessExitFactPhase.Startup);
    private static readonly WorkerProcessExitFact TransportInfrastructureFact = Create(WorkerProcessExitOutcome.InfrastructureFailureCategory, WorkerProcessExitFactPhase.Transport);
    private static readonly WorkerProcessExitFact InputInfrastructureFact = Create(WorkerProcessExitOutcome.InfrastructureFailureCategory, WorkerProcessExitFactPhase.Input);
    private static readonly WorkerProcessExitFact InputInvalidFact = Create(WorkerProcessExitOutcome.InvalidInputCategory, WorkerProcessExitFactPhase.Input);
    private static readonly WorkerProcessExitFact ExecutionInfrastructureFact = Create(WorkerProcessExitOutcome.InfrastructureFailureCategory, WorkerProcessExitFactPhase.Execution);
    private static readonly WorkerProcessExitFact ExecutionFailureFact = Create(WorkerProcessExitOutcome.ExecutionFailureCategory, WorkerProcessExitFactPhase.Execution);
    private static readonly WorkerProcessExitFact OutputTransportFact = Create(WorkerProcessExitOutcome.OutputTransportFailureCategory, WorkerProcessExitFactPhase.Output);
    private static readonly WorkerProcessExitFact ShutdownCancelledFact = Create(WorkerProcessExitOutcome.CancelledCategory, WorkerProcessExitFactPhase.Shutdown);
    private static readonly WorkerProcessExitFact CleanupInfrastructureFact = Create(WorkerProcessExitOutcome.InfrastructureFailureCategory, WorkerProcessExitFactPhase.Cleanup);
    private static readonly WorkerProcessExitFact CompletedFact = Create(WorkerProcessExitOutcome.CompletedCategory, WorkerProcessExitFactPhase.Execution);
    private static readonly WorkerProcessExitFact BusyFact = Create(WorkerProcessExitOutcome.BusyCategory, WorkerProcessExitFactPhase.Execution);

    private static readonly IReadOnlyList<WorkerProcessExitFact> Values = new ReadOnlyCollection<WorkerProcessExitFact>(
    [
        StartupInfrastructureFact,
        TransportInfrastructureFact,
        InputInfrastructureFact,
        InputInvalidFact,
        ExecutionInfrastructureFact,
        ExecutionFailureFact,
        OutputTransportFact,
        ShutdownCancelledFact,
        CleanupInfrastructureFact,
        CompletedFact,
        BusyFact
    ]);

    private WorkerProcessExitFact(WorkerProcessExitOutcome outcome, WorkerProcessExitFactPhase phase, WorkerProcessExitDiagnostic diagnostic)
    {
        Outcome = outcome;
        Phase = phase;
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the closed startup infrastructure failure fact.</summary>
    public static WorkerProcessExitFact StartupInfrastructure() => StartupInfrastructureFact;

    /// <summary>Gets the closed transport infrastructure failure fact.</summary>
    public static WorkerProcessExitFact TransportInfrastructure() => TransportInfrastructureFact;

    /// <summary>Gets the closed input infrastructure failure fact.</summary>
    public static WorkerProcessExitFact InputInfrastructure() => InputInfrastructureFact;

    /// <summary>Gets the closed invalid-input fact.</summary>
    public static WorkerProcessExitFact InputInvalid() => InputInvalidFact;

    /// <summary>Gets the closed execution infrastructure failure fact.</summary>
    public static WorkerProcessExitFact ExecutionInfrastructure() => ExecutionInfrastructureFact;

    /// <summary>Gets the closed execution failure fact.</summary>
    public static WorkerProcessExitFact ExecutionFailure() => ExecutionFailureFact;

    /// <summary>Gets the closed output transport failure fact.</summary>
    public static WorkerProcessExitFact OutputTransport() => OutputTransportFact;

    /// <summary>Gets the closed shutdown cancellation fact.</summary>
    public static WorkerProcessExitFact ShutdownCancelled() => ShutdownCancelledFact;

    /// <summary>Gets the closed cleanup infrastructure failure fact.</summary>
    public static WorkerProcessExitFact CleanupInfrastructure() => CleanupInfrastructureFact;

    /// <summary>Gets the closed completed fact.</summary>
    public static WorkerProcessExitFact Completed() => CompletedFact;

    /// <summary>Gets the closed busy fact.</summary>
    public static WorkerProcessExitFact Busy() => BusyFact;

    /// <summary>Gets every supported closed worker exit fact.</summary>
    public static IReadOnlyList<WorkerProcessExitFact> All => Values;

    /// <summary>Gets the closed category selected by this fact.</summary>
    public WorkerProcessExitOutcome Outcome { get; }

    /// <summary>Gets the portable process exit code.</summary>
    public int ExitCode => Outcome.ExitCode;

    /// <summary>Gets the safe bounded final diagnostic.</summary>
    public WorkerProcessExitDiagnostic Diagnostic { get; }

    internal WorkerProcessExitFactPhase Phase { get; }

    /// <summary>Combines two closed facts using outcome rank then canonical phase rank.</summary>
    public static WorkerProcessExitFact Combine(WorkerProcessExitFact left, WorkerProcessExitFact right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Outcome.Rank > right.Outcome.Rank)
        {
            return left;
        }

        if (right.Outcome.Rank > left.Outcome.Rank)
        {
            return right;
        }

        return left.Phase.TiebreakRank >= right.Phase.TiebreakRank ? left : right;
    }

    private static WorkerProcessExitFact Create(WorkerProcessExitOutcome outcome, WorkerProcessExitFactPhase phase)
    {
        return new WorkerProcessExitFact(outcome, phase, WorkerProcessExitDiagnostic.Create(outcome, phase));
    }
}

internal sealed class WorkerProcessExitFactPhase
{
    internal static readonly WorkerProcessExitFactPhase Startup = new("startup", 0);
    internal static readonly WorkerProcessExitFactPhase Transport = new("transport", 1);
    internal static readonly WorkerProcessExitFactPhase Input = new("input", 2);
    internal static readonly WorkerProcessExitFactPhase Execution = new("execution", 3);
    internal static readonly WorkerProcessExitFactPhase Output = new("output", 4);
    internal static readonly WorkerProcessExitFactPhase Shutdown = new("shutdown", 5);
    internal static readonly WorkerProcessExitFactPhase Cleanup = new("cleanup", 6);

    private WorkerProcessExitFactPhase(string token, int tiebreakRank)
    {
        Token = token;
        TiebreakRank = tiebreakRank;
    }

    internal string Token { get; }

    internal int TiebreakRank { get; }
}

/// <summary>Contains the stable portable worker process exit codes.</summary>
public static class WorkerProcessExitCodes
{
    public const int Completed = 0;
    public const int InvalidInput = 2;
    public const int Busy = 3;
    public const int ExecutorFailure = 4;
    public const int InfrastructureFailure = 5;
    public const int OutputTransportFailure = 6;
    public const int Cancelled = 130;
}
