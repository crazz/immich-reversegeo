using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Core.WorkerProcessExitOutcomes;
using ImmichReverseGeo.Core.WorkerProtocol;
using ImmichReverseGeo.Web.ChildWorkerLaunching;
using ImmichReverseGeo.Web.Services;
using ImmichReverseGeo.Web.WorkerEventStateBridge;
using ImmichReverseGeo.Web.WorkerFailureRecovery;

namespace ImmichReverseGeo.Tests.WorkerFailureRecovery;

[TestClass]
[TestCategory("Change30")]
public class WorkerRunEvidenceClassifierTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddSeconds(1);
    private static readonly ProcessingRunRequest Request = new(Guid.Parse("10101010-1010-1010-1010-101010101010"), ProcessingRunTrigger.Manual);

    [TestMethod]
    public void Classify_CommittedReceiptWinsEveryLateAnomaly()
    {
        var result = Result(Request, ProcessingRunOutcome.Failed);
        var receipt = new ProcessingRunFinalizationReceipt(Request, result, ProcessingRunFinalizationOrigin.WorkerTerminal);
        var protocol = new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidLifecycle, "late semantic failure", WorkerProtocolFailureDetail.TerminalConsistency);
        var evidence = Evidence(Request, Completion(Request, 3, firstProtocol: new ChildWorkerProtocolObservation.ProtocolFailure(protocol))) with
        {
            Receipt = receipt,
            BridgeObservation = new WorkerEventStateBridgeObservation.EventRejected(protocol),
            ManagedExit = WorkerProcessExitFact.OutputTransport(),
            CleanupFailed = true,
            ShutdownRequested = true
        };

        var decision = WorkerRunEvidenceClassifier.Classify(evidence);

        AssertDecision(decision, ProcessingRunOutcome.Failed, WorkerRunAuthority.CommittedReceipt, WorkerRunFailureCategory.Terminal, result);
        Assert.AreEqual(WorkerRunAnomaly.ProtocolAfterTerminal | WorkerRunAnomaly.ProjectionAfterTerminal |
            WorkerRunAnomaly.OutputTransport | WorkerRunAnomaly.CleanupFailure | WorkerRunAnomaly.ShutdownAfterTerminal,
            decision.Anomalies, "committed-terminal-keeps-late-anomalies");
        Assert.IsFalse(decision.Anomalies.HasFlag(WorkerRunAnomaly.TerminalExitMismatch), "failed-code-three-is-consistent");
    }

    [TestMethod]
    public void Classify_CommittedReceiptsFromBothOriginsRemainAuthoritative()
    {
        foreach (var origin in new[] { ProcessingRunFinalizationOrigin.WorkerTerminal, ProcessingRunFinalizationOrigin.ControlPlane })
        {
            var result = Result(Request, ProcessingRunOutcome.Completed);
            var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 0)) with
            {
                Receipt = new ProcessingRunFinalizationReceipt(Request, result, origin)
            });

            AssertDecision(decision, ProcessingRunOutcome.Completed, WorkerRunAuthority.CommittedReceipt, WorkerRunFailureCategory.Terminal, result);
        }
    }

    [TestMethod]
    public void Classify_ValidUncommittedTerminalIsTheOnlyCandidateAuthorityAndNeverRetries()
    {
        var result = Result(Request, ProcessingRunOutcome.Completed);
        var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 0)) with
        {
            BridgeObservation = new WorkerEventStateBridgeObservation.TerminalProjectionNotCommitted(result)
        });

        AssertDecision(decision, ProcessingRunOutcome.Completed, WorkerRunAuthority.ValidatedTerminal, WorkerRunFailureCategory.Terminal, result);
    }

    [TestMethod]
    public void Classify_IndeterminateProjectionWithoutReceiptFails()
    {
        var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 0)) with
        {
            BridgeObservation = WorkerEventStateBridgeObservation.ProjectionResponseIndeterminate.Instance
        });

        AssertDecision(decision, ProcessingRunOutcome.Failed, WorkerRunAuthority.ControlPlane, WorkerRunFailureCategory.ProjectionFailure, null);
    }

    [TestMethod]
    public void Classify_RejectsReceiptAndTerminalCandidateForAnotherRequestInstance()
    {
        var staleRequest = new ProcessingRunRequest(Guid.Parse("20202020-2020-2020-2020-202020202020"), ProcessingRunTrigger.Manual);
        var staleResult = Result(staleRequest, ProcessingRunOutcome.Completed);

        Assert.ThrowsExactly<ArgumentException>(() => WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 0)) with
        {
            Receipt = new ProcessingRunFinalizationReceipt(staleRequest, staleResult, ProcessingRunFinalizationOrigin.WorkerTerminal)
        }));
        Assert.ThrowsExactly<ArgumentException>(() => WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 0)) with
        {
            BridgeObservation = new WorkerEventStateBridgeObservation.TerminalProjectionNotCommitted(staleResult)
        }));
    }

    [TestMethod]
    public void Classify_SemanticRejectionNeverReplaysTheRawTerminal()
    {
        var failure = new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidLifecycle, "terminal counts must match", WorkerProtocolFailureDetail.TerminalConsistency);
        var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 0, terminal: WorkerProtocolV1TestData.Completed())) with
        {
            BridgeObservation = new WorkerEventStateBridgeObservation.EventRejected(failure)
        });

        AssertDecision(decision, ProcessingRunOutcome.Failed, WorkerRunAuthority.ControlPlane, WorkerRunFailureCategory.TerminalConsistency, null);
    }

    [TestMethod]
    public void ClassifyProtocol_MapsEveryTypedLifecycleDetailToItsClosedCategory()
    {
        var mappings = new (WorkerProtocolFailureDetail Detail, WorkerRunFailureCategory Category)[]
        {
            (WorkerProtocolFailureDetail.None, WorkerRunFailureCategory.Lifecycle),
            (WorkerProtocolFailureDetail.Readiness, WorkerRunFailureCategory.Readiness),
            (WorkerProtocolFailureDetail.ProgressConsistency, WorkerRunFailureCategory.ProgressConsistency),
            (WorkerProtocolFailureDetail.TerminalConsistency, WorkerRunFailureCategory.TerminalConsistency),
            (WorkerProtocolFailureDetail.ActivityCardinality, WorkerRunFailureCategory.ActivityCardinality),
            (WorkerProtocolFailureDetail.MissingTerminal, WorkerRunFailureCategory.MissingTerminal)
        };

        foreach (var mapping in mappings)
        {
            var failure = new WorkerProtocolFailure(WorkerProtocolFailureCode.InvalidLifecycle, "semantic failure", mapping.Detail);
            Assert.AreEqual(mapping.Category, WorkerRunEvidenceClassifier.ClassifyProtocol(failure), $"lifecycle-detail-{mapping.Detail}");
        }
    }

    [TestMethod]
    public void Classify_TypedStartupFaultsMapToTheirClosedCategories()
    {
        var cases = new (string Name, ChildWorkerStartupObservation Startup, WorkerRunFailureCategory Category)[]
        {
            ("ready-timeout", ChildWorkerStartupObservation.ReadyTimedOut.Instance, WorkerRunFailureCategory.ReadyTimeout),
            ("exit-observation", ChildWorkerStartupObservation.PreReadyExitObservationFailed.Instance, WorkerRunFailureCategory.ExitObservation),
            ("ready-rejected", ChildWorkerStartupObservation.SinkFailed.Instance, WorkerRunFailureCategory.ReadyRejected),
            ("execute-serialization", ChildWorkerStartupObservation.RequestSerializationFailed.Instance, WorkerRunFailureCategory.ExecuteSerialization),
            ("execute-write", ChildWorkerStartupObservation.RequestWriteFailed.Instance, WorkerRunFailureCategory.ExecuteWrite),
            ("execute-flush", ChildWorkerStartupObservation.RequestFlushFailed.Instance, WorkerRunFailureCategory.ExecuteFlush),
            ("output-transport", ChildWorkerStartupObservation.PreReadyReadFailed.Instance, WorkerRunFailureCategory.OutputTransport)
        };

        foreach (var test in cases)
        {
            var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 42, test.Startup)));

            AssertDecision(decision, ProcessingRunOutcome.Failed, WorkerRunAuthority.ControlPlane, test.Category, null);
        }
    }

    [TestMethod]
    public void Classify_TypedProtocolFailuresMapToTheirClosedCategories()
    {
        var cases = new (string Name, WorkerProtocolFailureCode Code, WorkerRunFailureCategory Category)[]
        {
            ("invalid-encoding", WorkerProtocolFailureCode.InvalidEncoding, WorkerRunFailureCategory.InvalidEncoding),
            ("invalid-correlation", WorkerProtocolFailureCode.InvalidCorrelation, WorkerRunFailureCategory.Correlation)
        };

        foreach (var test in cases)
        {
            var failure = new WorkerProtocolFailure(test.Code, $"{test.Name}-diagnostic", WorkerProtocolFailureDetail.None);
            var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request,
                Completion(Request, 42, firstProtocol: new ChildWorkerProtocolObservation.ProtocolFailure(failure))));

            AssertDecision(decision, ProcessingRunOutcome.Failed, WorkerRunAuthority.ControlPlane, test.Category, null);
        }
    }

    [TestMethod]
    public void Classify_ManagedExitFactsWinOverConflictingRawExitValues()
    {
        var cases = new[]
        {
            (Name: "infrastructure", RawExit: 42, Fact: WorkerProcessExitFact.StartupInfrastructure(), Category: WorkerRunFailureCategory.Infrastructure),
            (Name: "invalid-input", RawExit: 5, Fact: WorkerProcessExitFact.InputInvalid(), Category: WorkerRunFailureCategory.InvalidInput),
            (Name: "busy", RawExit: 4, Fact: WorkerProcessExitFact.Busy(), Category: WorkerRunFailureCategory.BusyWithoutTerminal),
            (Name: "execution-failure", RawExit: 2, Fact: WorkerProcessExitFact.ExecutionFailure(), Category: WorkerRunFailureCategory.ExecutionFailure)
        };

        foreach (var test in cases)
        {
            Assert.AreNotEqual(test.Fact.ExitCode, test.RawExit, $"{test.Name}-raw-exit-conflicts-with-fact");
            var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, test.RawExit)) with
            {
                ManagedExit = test.Fact
            });

            AssertDecision(decision, ProcessingRunOutcome.Failed, WorkerRunAuthority.ControlPlane, test.Category, null);
        }
    }

    [TestMethod]
    public void Classify_ConfirmedKillRejectionIsAClosedFailureCategory()
    {
        var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 0)) with
        {
            Cancellation = Cancellation(
                ChildWorkerTerminationIntent.Stop,
                graceExpired: true,
                killAttempted: true,
                killOutcome: ChildProcessKillOutcome.PermissionDenied)
        });

        AssertDecision(decision, ProcessingRunOutcome.Failed, WorkerRunAuthority.ControlPlane, WorkerRunFailureCategory.KillRejected, null);
    }

    [TestMethod]
    public void Classify_RawMappedLookingExitNeverBecomesManagedEvidence()
    {
        var cases = new (int ExitCode, WorkerRunFailureCategory Category)[]
        {
            (0, WorkerRunFailureCategory.MissingTerminal),
            (2, WorkerRunFailureCategory.InconsistentExit),
            (3, WorkerRunFailureCategory.InconsistentExit),
            (4, WorkerRunFailureCategory.InconsistentExit),
            (5, WorkerRunFailureCategory.InconsistentExit),
            (6, WorkerRunFailureCategory.InconsistentExit),
            (130, WorkerRunFailureCategory.InconsistentExit)
        };

        foreach (var test in cases)
        {
            var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, test.ExitCode)));

            AssertDecision(decision, ProcessingRunOutcome.Failed, WorkerRunAuthority.ControlPlane, test.Category, null);
        }
    }

    [TestMethod]
    public void Classify_ClosedManagedExitFactsHaveDeterministicPrecedence()
    {
        var precedence = new[]
        {
            WorkerProcessExitFact.OutputTransport(),
            WorkerProcessExitFact.StartupInfrastructure(),
            WorkerProcessExitFact.InputInvalid(),
            WorkerProcessExitFact.Busy(),
            WorkerProcessExitFact.ExecutionFailure(),
            WorkerProcessExitFact.ShutdownCancelled(),
            WorkerProcessExitFact.Completed()
        };

        for (var winner = 0; winner < precedence.Length; winner++)
        {
            for (var contender = winner; contender < precedence.Length; contender++)
            {
                Assert.AreSame(precedence[winner], WorkerProcessExitFact.Combine(precedence[winner], precedence[contender]), $"managed-exit-precedence-{winner}-{contender}");
                Assert.AreSame(precedence[winner], WorkerProcessExitFact.Combine(precedence[contender], precedence[winner]), $"managed-exit-commutative-{winner}-{contender}");
            }
        }
    }

    [TestMethod]
    public void Classify_OwnedStopOrShutdownCancellationOverridesPreReadyExitEvidence()
    {
        foreach (var intent in new[] { ChildWorkerTerminationIntent.Stop, ChildWorkerTerminationIntent.Shutdown })
        {
            foreach (var startup in new ChildWorkerStartupObservation[]
            {
                ChildWorkerStartupObservation.PreReadyEndOfStream.Instance,
                ChildWorkerStartupObservation.PreReadyExit.Instance
            })
            {
                var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 130, startup)) with
                {
                    ManagedExit = WorkerProcessExitFact.ShutdownCancelled(),
                    Cancellation = Cancellation(intent, requestAccepted: false)
                });

                AssertDecision(decision, ProcessingRunOutcome.Cancelled, WorkerRunAuthority.ControlPlane, WorkerRunFailureCategory.ManagedCancellation, null);
            }
        }
    }

    [TestMethod]
    public void Classify_AcceptedGraceKillOverridesPreReadyEndOfStream()
    {
        var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 1, ChildWorkerStartupObservation.PreReadyEndOfStream.Instance)) with
        {
            Cancellation = Cancellation(ChildWorkerTerminationIntent.Stop, requestAccepted: true, graceExpired: true, killAttempted: true, killOutcome: ChildProcessKillOutcome.Requested)
        });

        AssertDecision(decision, ProcessingRunOutcome.Cancelled, WorkerRunAuthority.ControlPlane, WorkerRunFailureCategory.ForcedTermination, null);
        Assert.AreEqual(WorkerRunAnomaly.ForcedTermination | WorkerRunAnomaly.MissingTerminal, decision.Anomalies, "accepted-grace-kill-anomalies");
    }

    [TestMethod]
    public void Classify_AlreadyExitedKillDoesNotInventForcedTerminationOrRejection()
    {
        var result = Result(Request, ProcessingRunOutcome.Cancelled);
        var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 130)) with
        {
            Receipt = new ProcessingRunFinalizationReceipt(Request, result, ProcessingRunFinalizationOrigin.WorkerTerminal),
            Cancellation = Cancellation(ChildWorkerTerminationIntent.Stop, requestAccepted: true, graceExpired: true, killAttempted: true, killOutcome: ChildProcessKillOutcome.AlreadyExited)
        });

        AssertDecision(decision, ProcessingRunOutcome.Cancelled, WorkerRunAuthority.CommittedReceipt, WorkerRunFailureCategory.Terminal, result);
        Assert.IsFalse(decision.Anomalies.HasFlag(WorkerRunAnomaly.ForcedTermination), "already-exited-is-not-forced");
        Assert.IsFalse(decision.Anomalies.HasFlag(WorkerRunAnomaly.KillRejected), "already-exited-is-not-rejected");
    }

    [TestMethod]
    public void Classify_FaultContainmentKeepsItsReasonButDoesNotClaimCancellationAuthority()
    {
        var reason = ChildWorkerFaultContainmentReason.RequestWriteFailed.Instance;
        var facts = Cancellation(ChildWorkerTerminationIntent.FaultContainment, firstContainmentReason: reason);
        var decision = WorkerRunEvidenceClassifier.Classify(Evidence(Request, Completion(Request, 130)) with
        {
            ManagedExit = WorkerProcessExitFact.ShutdownCancelled(),
            Cancellation = facts
        });

        Assert.AreSame(reason, facts.FirstContainmentReason, "first-containment-reason-retained");
        AssertDecision(decision, ProcessingRunOutcome.Failed, WorkerRunAuthority.ControlPlane, WorkerRunFailureCategory.InconsistentExit, null);
    }

    [TestMethod]
    public void FinalityState_IsMonotonicAndAdvancesTransportAndCommitIndependently()
    {
        var state = new WorkerRunFinalityState();

        state.AdvanceCommit(WorkerRunCommitPhase.Committed);
        state.AdvanceTransport(WorkerRunTransportPhase.Ready);
        state.AdvanceTransport(WorkerRunTransportPhase.Accepted);
        state.AdvanceCommit(WorkerRunCommitPhase.TerminalValidated);
        state.AdvanceTransport(WorkerRunTransportPhase.PreReady);
        state.AdvanceCommit(WorkerRunCommitPhase.Uncommitted);

        var snapshot = state.Snapshot;
        Assert.AreEqual(WorkerRunTransportPhase.Accepted, snapshot.Transport, "transport-never-regresses");
        Assert.AreEqual(WorkerRunCommitPhase.Committed, snapshot.Commit, "commit-never-regresses");
    }

    [TestMethod]
    public void Classify_OnlyClosedNoProcessCategoriesAreAccepted()
    {
        foreach (var category in new[] { WorkerRunFailureCategory.CommandResolution, WorkerRunFailureCategory.ProcessStart })
        {
            var decision = WorkerRunEvidenceClassifier.Classify(new WorkerRunEvidence
            {
                Request = Request,
                LastPhase = WorkerRunTransportPhase.Resolving,
                NoProcessFailure = category
            });
            AssertDecision(decision, ProcessingRunOutcome.Failed, WorkerRunAuthority.ControlPlane, category, null);
        }

        Assert.ThrowsExactly<ArgumentException>(() => WorkerRunEvidenceClassifier.Classify(new WorkerRunEvidence
        {
            Request = Request,
            LastPhase = WorkerRunTransportPhase.Resolving,
            NoProcessFailure = WorkerRunFailureCategory.Crash
        }));
    }

    private static WorkerRunEvidence Evidence(ProcessingRunRequest request, ChildWorkerCompletionObservation completion) => new()
    {
        Request = request,
        LastPhase = WorkerRunTransportPhase.EvidenceFinal,
        Completion = completion
    };

    private static ChildWorkerCompletionObservation Completion(
        ProcessingRunRequest request,
        int exitCode,
        ChildWorkerStartupObservation? startup = null,
        WorkerProtocolEvent? terminal = null,
        ChildWorkerProtocolObservation? firstProtocol = null) => new(
        42,
        request.RunId,
        startup ?? ChildWorkerStartupObservation.ReadyAccepted.Instance,
        true,
        exitCode,
        ChildWorkerStreamFinality.EndOfStream.Instance,
        ChildWorkerStreamFinality.EndOfStream.Instance,
        terminal,
        firstProtocol,
        new ChildWorkerStandardErrorTail([], 0, false, false));

    private static ChildWorkerCancellationFacts Cancellation(
        ChildWorkerTerminationIntent intent,
        bool requestAccepted = false,
        bool graceExpired = false,
        bool killAttempted = false,
        ChildProcessKillOutcome? killOutcome = null,
        ChildWorkerFaultContainmentReason? firstContainmentReason = null) => new(
        StartedAt,
        EndedAt,
        requestAccepted,
        ChildWorkerCancelDeliveryPhase.Flushed,
        ChildWorkerCancellationExitRace.None,
        graceExpired,
        killAttempted,
        killOutcome,
        intent,
        firstContainmentReason);

    private static ProcessingRunResult Result(ProcessingRunRequest request, ProcessingRunOutcome outcome) => new(
        request,
        StartedAt,
        EndedAt,
        0,
        0,
        0,
        0,
        outcome,
        outcome == ProcessingRunOutcome.Failed ? "worker failed" : null);

    private static void AssertDecision(
        WorkerRunDecision decision,
        ProcessingRunOutcome outcome,
        WorkerRunAuthority authority,
        WorkerRunFailureCategory category,
        ProcessingRunResult? terminal)
    {
        Assert.AreEqual(outcome, decision.Outcome, "outcome");
        Assert.AreEqual(authority, decision.Authority, "authority");
        Assert.AreEqual(category, decision.Category, "category");
        Assert.AreSame(terminal, decision.TerminalResult, "terminal");
        Assert.IsFalse(decision.Retry, "recovery-never-retries");
    }
}
