using System;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.WorkerEventStateBridge;

namespace ImmichReverseGeo.Web.WorkerFailureRecovery;

internal static class WorkerRunEvidenceClassifier
{
    internal static WorkerRunDecision Classify(WorkerRunEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if ((evidence.Completion is null) == (evidence.NoProcessFailure is null)
            || evidence.NoProcessFailure is not (null or WorkerRunFailureCategory.CommandResolution or WorkerRunFailureCategory.ProcessStart)
            || (evidence.Completion is { } completion && completion.RunId != evidence.Request.RunId))
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
                WorkerRunFailureCategory.Terminal, evidence.LastPhase, TerminalAnomalies(evidence, receipt.Result), receipt.Result);
        }

        if (evidence.BridgeObservation is WorkerEventStateBridgeObservation.TerminalProjectionNotCommitted candidate)
        {
            RequireMatchingResult(evidence, candidate.Candidate);
            return new(candidate.Candidate.Outcome, WorkerRunAuthority.ValidatedTerminal,
                WorkerRunFailureCategory.Terminal, evidence.LastPhase, TerminalAnomalies(evidence, candidate.Candidate), candidate.Candidate);
        }

        if (evidence.BridgeObservation is WorkerEventStateBridgeObservation.ProjectionResponseIndeterminate)
        {
            return Failed(evidence, WorkerRunFailureCategory.ProjectionFailure);
        }

        if (evidence.NoProcessFailure is { } noProcess)
        {
            return Failed(evidence, noProcess);
        }

        var raw = evidence.Completion!;
        var startupFailure = ClassifyStartup(raw.Startup);
        if (startupFailure is not null && !IsExpectedTerminationEnd(evidence, raw.Startup))
        {
            return Failed(evidence, startupFailure.Value);
        }

        if (evidence.BridgeObservation is WorkerEventStateBridgeObservation.EventRejected rejected)
        {
            return Failed(evidence, ClassifyProtocol(rejected.Failure));
        }

        if (raw.FirstProtocolObservation is ChildWorkerProtocolObservation.ProtocolFailure protocol
            && protocol.Failure.Detail != WorkerProtocolFailureDetail.MissingTerminal)
        {
            return Failed(evidence, ClassifyProtocol(protocol.Failure));
        }

        if (raw.StandardOutputFinality is ChildWorkerStreamFinality.ReadFailed
            || raw.StandardErrorFinality is ChildWorkerStreamFinality.ReadFailed
            || evidence.ManagedExit?.ExitCode == 6)
        {
            return Failed(evidence, WorkerRunFailureCategory.OutputTransport);
        }

        if (evidence.BridgeObservation is WorkerEventStateBridgeObservation.ProjectionFailed
            || raw.FirstProtocolObservation is ChildWorkerProtocolObservation.SinkFailure)
        {
            return Failed(evidence, WorkerRunFailureCategory.ProjectionFailure);
        }

        // This is the closed managed fact selected by block 23, never an interpretation of raw OS status.
        var managedFailure = evidence.ManagedExit?.ExitCode switch
        {
            5 => WorkerRunFailureCategory.Infrastructure,
            2 => WorkerRunFailureCategory.InvalidInput,
            3 => WorkerRunFailureCategory.BusyWithoutTerminal,
            4 => WorkerRunFailureCategory.ExecutionFailure,
            _ => (WorkerRunFailureCategory?)null
        };
        if (managedFailure is not null)
        {
            return Failed(evidence, managedFailure.Value);
        }

        if (evidence.Cancellation is { } cancellation)
        {
            if (cancellation.KillAttempted && cancellation.KillOutcome is not (ChildProcessKillOutcome.Requested or ChildProcessKillOutcome.AlreadyExited))
            {
                return Failed(evidence, WorkerRunFailureCategory.KillRejected);
            }

            if (cancellation.FirstIntent is ChildWorkerTerminationIntent.Stop or ChildWorkerTerminationIntent.Shutdown)
            {
                if (cancellation.GraceExpired && cancellation.KillAttempted && cancellation.KillOutcome == ChildProcessKillOutcome.Requested)
                {
                    return new(ProcessingRunOutcome.Cancelled, WorkerRunAuthority.ControlPlane,
                        WorkerRunFailureCategory.ForcedTermination, evidence.LastPhase,
                        WorkerRunAnomaly.ForcedTermination | WorkerRunAnomaly.MissingTerminal, null);
                }
                if (evidence.ManagedExit?.ExitCode == 130)
                {
                    return new(ProcessingRunOutcome.Cancelled, WorkerRunAuthority.ControlPlane,
                        WorkerRunFailureCategory.ManagedCancellation, evidence.LastPhase,
                        cancellation.RequestAccepted ? WorkerRunAnomaly.MissingTerminal : WorkerRunAnomaly.None, null);
                }
            }
        }

        if (evidence.CleanupFailed)
        {
            return Failed(evidence, WorkerRunFailureCategory.Infrastructure);
        }

        if (!raw.ExitObserved || raw.ExitCode is null)
        {
            return Failed(evidence, WorkerRunFailureCategory.Crash);
        }

        return Failed(evidence, raw.ExitCode switch
        {
            0 => WorkerRunFailureCategory.MissingTerminal,
            2 or 3 or 4 or 5 or 6 or 130 => WorkerRunFailureCategory.InconsistentExit,
            _ => WorkerRunFailureCategory.UnmappedExit
        });
    }

    internal static WorkerRunFailureCategory ClassifyProtocol(WorkerProtocolFailure failure)
    {
        return failure.Code switch
        {
            WorkerProtocolFailureCode.MessageTooLarge => WorkerRunFailureCategory.OversizedFrame,
            WorkerProtocolFailureCode.InvalidEncoding => WorkerRunFailureCategory.InvalidEncoding,
            WorkerProtocolFailureCode.InvalidFraming or WorkerProtocolFailureCode.MalformedJson
                or WorkerProtocolFailureCode.InvalidEnvelope or WorkerProtocolFailureCode.InvalidPayload => WorkerRunFailureCategory.MalformedFrame,
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

private static bool IsExpectedTerminationEnd(WorkerRunEvidence evidence, ChildWorkerStartupObservation startup)
    {
        if (startup is not (ChildWorkerStartupObservation.PreReadyEndOfStream or ChildWorkerStartupObservation.PreReadyExit)
            || evidence.Cancellation is not { FirstIntent: ChildWorkerTerminationIntent.Stop or ChildWorkerTerminationIntent.Shutdown } cancellation)
        {
            return false;
        }
        return evidence.ManagedExit?.ExitCode == 130
            || (cancellation.GraceExpired && cancellation.KillAttempted && cancellation.KillOutcome == ChildProcessKillOutcome.Requested);
    }

    private static WorkerRunFailureCategory? ClassifyStartup(ChildWorkerStartupObservation startup)
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

    private static WorkerRunAnomaly TerminalAnomalies(WorkerRunEvidence evidence, ProcessingRunResult result)
    {
        var anomalies = WorkerRunAnomaly.None;
        if (evidence.Completion is { } raw)
        {
            var consistent = raw.ExitObserved && (result.Outcome switch
            {
                ProcessingRunOutcome.Completed => raw.ExitCode == 0,
                ProcessingRunOutcome.Cancelled => raw.ExitCode == 130,
                // Code 3 is reserved advisory evidence for the existing Failed busy terminal.
                ProcessingRunOutcome.Failed => raw.ExitCode is 3 or 4,
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
            if (raw.StandardOutputFinality is ChildWorkerStreamFinality.ReadFailed
                || raw.StandardErrorFinality is ChildWorkerStreamFinality.ReadFailed || evidence.ManagedExit?.ExitCode == 6)
            {
                anomalies |= WorkerRunAnomaly.OutputTransport;
            }
        }
        if (evidence.Cancellation is { } cancel)
        {
            if (cancel.DeliveryPhase is ChildWorkerCancelDeliveryPhase.SerializationFailed or ChildWorkerCancelDeliveryPhase.WriteFailed
                or ChildWorkerCancelDeliveryPhase.FlushFailed)
            {
                anomalies |= WorkerRunAnomaly.InputTransport;
            }
            if (cancel.KillAttempted && cancel.KillOutcome == ChildProcessKillOutcome.Requested)
            {
                anomalies |= WorkerRunAnomaly.ForcedTermination;
            }
            else if (cancel.KillAttempted && cancel.KillOutcome != ChildProcessKillOutcome.AlreadyExited)
            {
                anomalies |= WorkerRunAnomaly.KillRejected;
            }
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
