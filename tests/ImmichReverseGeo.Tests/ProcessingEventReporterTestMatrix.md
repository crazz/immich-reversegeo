# Processing Event Reporter Test Matrix

| Task | Named scenario | Exact test method |
|---|---|---|
| 4.1 | Pre-eligibility operation rejection and duplicate eligibility | `PreEligibility_AllPublicOperationsAreRejectedWithoutEmission` |
| 4.1 | Completed finish before eligibility | `CompletedFinishBeforeEligibility_IsRejectedWithoutTerminal` |
| 4.1 | Count cancellation and failure start-to-finish sequences | `PreCountCancellationAndFailure_EmitExactStartToFinishSequences` |
| 4.1 | Every operation after finish | `AfterFinish_AllPublicOperationsAreRejectedWithoutEmission` |
| 4.1 | Null request/result/progress, blank label, empty activity end ID, negative fields, checked overflow | `Payloads_RejectNullBlankEmptyNegativeAndOverflowValues` |
| 4.1 | Defined and invalid log levels | `LogPayloads_AcceptEveryDefinedLevelAndRejectInvalid` |
| 4.1 | Reference identity for terminal result | `EqualValueDifferentRequestInstanceIsRejected` |
| 4.2 | Updated/skipped/failed accounting and handled-failure completion | `MixedDispositions_EmitCoherentMonotonicSnapshotsAndCompletedHandledFailure` |
| 4.2 | Fatal failure does not increment per-asset failures | `FatalRunFailure_DoesNotAddPerAssetFailure` |
| 4.2 | Cancellation immediately after accepted write progress | `UpdatedAfterWriteThenCancellation_RetainsAcceptedProgressThroughPostAcceptanceHook` |
| 4.2 | Committed disposition waits through session-gate cancellation | `CommittedDisposition_WaitingForSessionGate_IsRetainedAfterCancellation` |
| 4.2 | Committed disposition waits through bounded-capacity cancellation | `CommittedDisposition_WaitingForBoundedCapacity_IsRetainedAfterCancellation` |
| 4.3 | Caller-token cancellation while waiting for session gate | `CancellationWhileWaitingForSessionGate_EmitsNothing` |
| 4.3 | Caller-token cancellation while waiting for bounded capacity | `CancellationWhileWaitingForBoundedCapacity_EmitsNothingBeforeLinearization` |
| 4.3 | Bounded capacity waits before linearization | `BoundedCapacity_BlocksBeforeAcceptanceUntilReleased` |
| 4.3 | Concurrent-session isolation | `ConcurrentSessions_InterleaveWithoutCrossContamination` |
| 4.3 | Concurrent per-session linearization/no loss | `ConcurrentDispositions_AreLinearizedWithoutLoss` |
| 4.4 | Equal labels and finish-owned closure | `ActivityScopes_AreUniqueAndFinishClosesThem` |
| 4.4 | Cancellation unwind, one non-cancelled activity end, duplicate dispose | `CancellationUnwindAndDuplicateDispose_EmitOneNonCancelledActivityEnd` |
| 4.4 | Finish activity-end order and late dispose | `Finish_ClosesActivitiesInStartOrderBeforeTerminal` |
| 4.4 | Every event acceptance fault and no recursive event | `ReporterFaultAtEveryEventKind_BreaksSessionWithoutRecursiveEvents` |
| 4.4 | Activity end acceptance fault locally closes scope | `ReporterFaultAtEveryEventKind_BreaksSessionWithoutRecursiveEvents` (ActivityEnded row) |
| 4.4 | Finish racing scope disposal | `FinishBlockedOnActivityEndMakesConcurrentDisposeLocalNoOp` |
| 4.5 | Trace-before-error, unchanged plain messages, Warning/Error mapping, logger-only no-event | `CompatibilityFixtures_MapDiagnosticsWithoutRuntimeWiring` |
| 4.5 | No exception, stack, token, delegate, or protocol payload | `DiagnosticPayloads_ArePlainAndTransportNeutral` |
