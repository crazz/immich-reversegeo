using System;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerJobs;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;

namespace ImmichReverseGeo.Web.WorkerFailureRecovery;

internal static class WorkerRunEvidenceClassifier
{
    internal static WorkerRunDecision Classify(WorkerRunEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if ((evidence.Completion is null) == (evidence.NoProcessFailure is null)
            || evidence.NoProcessFailure is not (null or WorkerRunFailureCategory.CommandResolution or WorkerRunFailureCategory.ProcessStart)
            || evidence.JobId != evidence.Request.RunId
            || evidence.JobKind != WorkerJobKind.ProcessAssets
            || !Enum.IsDefined(evidence.ProtocolVersion)
            || (evidence.Completion is { } completion
                && (completion.RunId != evidence.Request.RunId
                    || completion.JobId != evidence.JobId
                    || completion.JobKind != evidence.JobKind)))
        {
            throw new ArgumentException("Final evidence must identify exactly one matching session or no-process failure.", nameof(evidence));
        }

        if (evidence.Receipt is { } receipt)
        {
            RequireMatchingResult(evidence, receipt.Result);
            if (!ReferenceEquals(receipt.Request, evidence.Request))
            {
                throw new ArgumentException("The receipt must belong to the exact request.", nameof(evidence));
            }
            return new(receipt.Result.Outcome, WorkerRunAuthority.CommittedReceipt,
                WorkerRunFailureCategory.Terminal, evidence.LastPhase,
                TerminalAnomalies(
                    evidence,
                    receipt.Result,
                    receipt.Origin == ProcessingRunFinalizationOrigin.WorkerTerminal),
                receipt.Result);
        }

        if (evidence.BridgeObservation is WorkerEventStateBridgeObservation.TerminalProjectionNotCommitted candidate)
        {
            RequireMatchingResult(evidence, candidate.Candidate);
            return new(candidate.Candidate.Outcome, WorkerRunAuthority.ValidatedTerminal,
                WorkerRunFailureCategory.Terminal, evidence.LastPhase,
                TerminalAnomalies(evidence, candidate.Candidate, workerTerminalReported: true),
                candidate.Candidate);
        }

        if (evidence.BridgeObservation is WorkerEventStateBridgeObservation.ProjectionResponseIndeterminate)
        {
            return Failed(evidence, WorkerRunFailureCategory.ProjectionFailure);
        }

        if (evidence.NoProcessFailure is { } noProcess)
        {
            return Failed(evidence, noProcess);
        }
        WorkerJobNoTerminalDecision transport =
            WorkerJobNoTerminalEvidenceClassifier.Classify(new WorkerJobNoTerminalEvidence
            {
                Context = new ProcessAssetsWorkerJobDispatch(evidence.Request).Context,
                IntendedProtocolVersion = evidence.ProtocolVersion,
                LastPhase = evidence.LastPhase,
                Completion = evidence.Completion,
                ProjectedProtocolFailure =
                    (evidence.BridgeObservation as WorkerEventStateBridgeObservation.EventRejected)?.Failure,
                ProjectionFailed =
                    evidence.BridgeObservation is WorkerEventStateBridgeObservation.ProjectionFailed,
                Cancellation = evidence.Cancellation,
                ManagedExit = evidence.ManagedExit,
                CleanupFailed = evidence.CleanupFailed
            });
        return new WorkerRunDecision(
            transport.Outcome == WorkerJobNoTerminalOutcome.Cancelled
                ? ProcessingRunOutcome.Cancelled
                : ProcessingRunOutcome.Failed,
            WorkerRunAuthority.ControlPlane,
            transport.Category,
            evidence.LastPhase,
            transport.Anomalies,
            null);
    }

    internal static WorkerRunFailureCategory ClassifyProtocol(WorkerProtocolFailure failure)
    {
        return WorkerJobNoTerminalEvidenceClassifier.ClassifyProtocol(failure);
    }

    private static WorkerRunDecision Failed(WorkerRunEvidence evidence, WorkerRunFailureCategory category)
    {
        return new(ProcessingRunOutcome.Failed, WorkerRunAuthority.ControlPlane, category,
            evidence.LastPhase, WorkerRunAnomaly.None, null);
    }

    private static void RequireMatchingResult(WorkerRunEvidence evidence, ProcessingRunResult result)
    {
        if (!ReferenceEquals(result.Request, evidence.Request))
        {
            throw new ArgumentException("Terminal authority must belong to the exact request.", nameof(evidence));
        }
    }

    private static WorkerRunAnomaly TerminalAnomalies(
        WorkerRunEvidence evidence,
        ProcessingRunResult result,
        bool workerTerminalReported)
    {
        var anomalies = WorkerRunAnomaly.None;
        if (evidence.Completion is { } raw)
        {
            if (workerTerminalReported)
            {
                var consistent = raw.ExitObserved && (result.Outcome switch
                {
                    ProcessingRunOutcome.Completed => raw.ExitCode == 0,
                    ProcessingRunOutcome.Cancelled => raw.ExitCode == 130,
                    // Existing typed contract: 3 busy, 4 domain, and 5 infrastructure failures.
                    ProcessingRunOutcome.Failed => raw.ExitCode is 3 or 4 or 5,
                    _ => false
                });
                if (!consistent)
                {
                    anomalies |= WorkerRunAnomaly.TerminalExitMismatch;
                }
                if (raw.FirstProtocolObservation is ChildWorkerProtocolObservation.ProtocolFailure)
                {
                    anomalies |= WorkerRunAnomaly.ProtocolAfterTerminal;
                }
                if (raw.FirstProtocolObservation is ChildWorkerProtocolObservation.SinkFailure || evidence.BridgeObservation is not null)
                {
                    anomalies |= WorkerRunAnomaly.ProjectionAfterTerminal;
                }
            }
            if (raw.StandardOutputFinality is ChildWorkerStreamFinality.ReadFailed
                || raw.StandardErrorFinality is ChildWorkerStreamFinality.ReadFailed || evidence.ManagedExit?.ExitCode == 6)
            {
                anomalies |= WorkerRunAnomaly.OutputTransport;
            }
        }
        var inputTransportFailed = evidence.TerminalInputCloseFailure is not null;
        if (evidence.Cancellation is { } cancel)
        {
            inputTransportFailed |= cancel.DeliveryPhase is ChildWorkerCancelDeliveryPhase.SerializationFailed
                or ChildWorkerCancelDeliveryPhase.WriteFailed
                or ChildWorkerCancelDeliveryPhase.FlushFailed
                || cancel.FirstContainmentReason is ChildWorkerFaultContainmentReason.TerminalInputCloseFailed;
            if (cancel.KillAttempted && cancel.KillOutcome == ChildProcessKillOutcome.Requested)
            {
                anomalies |= WorkerRunAnomaly.ForcedTermination;
            }
            else if (cancel.KillAttempted && cancel.KillOutcome != ChildProcessKillOutcome.AlreadyExited)
            {
                anomalies |= WorkerRunAnomaly.KillRejected;
            }
        }
        if (inputTransportFailed)
        {
            anomalies |= WorkerRunAnomaly.InputTransport;
        }
        if (evidence.CleanupFailed)
        {
            anomalies |= WorkerRunAnomaly.CleanupFailure;
        }
        if (evidence.ShutdownRequested && result.Outcome != ProcessingRunOutcome.Cancelled)
        {
            anomalies |= WorkerRunAnomaly.ShutdownAfterTerminal;
        }
        return anomalies;
    }
}
