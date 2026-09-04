using System;

namespace ImmichReverseGeo.Core.WorkerProcessExitOutcomes;

/// <summary>
/// Contains bounded, predefined, one-line stderr metadata for an orderly worker exit.
/// </summary>
public sealed class WorkerProcessExitDiagnostic
{
    /// <summary>Gets the stable marker for a final worker exit summary.</summary>
    public const string FinalSummaryMarker = "worker-exit-summary";

    /// <summary>Gets the maximum number of characters in a final exit summary.</summary>
    public const int MaximumLength = 160;

    private WorkerProcessExitDiagnostic(string token, string phase, string message)
    {
        Token = token;
        Phase = phase;
        Message = message;
    }

    /// <summary>Gets the stable safe outcome token.</summary>
    public string Token { get; }

    /// <summary>Gets the stable safe lifecycle phase token.</summary>
    public string Phase { get; }

    /// <summary>Gets the bounded predefined safe message.</summary>
    public string Message { get; }

    /// <summary>
    /// Formats one safe final summary using this grammar:
    /// <c>worker-exit-summary outcome={token} phase={phase} message={message}</c>.
    /// </summary>
    public string FormatFinalSummary()
    {
        return $"{FinalSummaryMarker} outcome={Token} phase={Phase} message={Message}";
    }

    internal static WorkerProcessExitDiagnostic Create(
        WorkerProcessExitOutcome outcome,
        WorkerProcessExitFactPhase phase)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(phase);

        var diagnostic = new WorkerProcessExitDiagnostic(outcome.Token, phase.Token, outcome.Message);

        if (diagnostic.FormatFinalSummary().Length > MaximumLength)
        {
            throw new InvalidOperationException("The predefined worker exit diagnostic exceeds its safe bound.");
        }

        return diagnostic;
    }
}
