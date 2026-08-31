# Changes during implementation

## Owner turn 1 — design/proof only

- Change: `14-cover-executor-independently-from-scheduler`
- Start UTC: `2026-08-31T13:52:20Z`
- Branch: `major-redesign`
- Baseline HEAD and `origin/major-redesign`: `fd48bd43a717cebeb27e7888e4963d066b144365`
- OpenSpec schema/state/progress: `spec-driven`, `ready`, `0/42` tasks complete.
- Scope for this turn: proof design only. No task is complete and no task checkbox is changed.
- Owner turn 1 explicitly forbade production, test, project, proposal, design, spec, tasks, and task-checkbox edits. It created only `Changes-during-implementation.md` and `verification-plan.json`; no planning artifact was edited.
- Preserved unrelated baseline: deleted active Change-02 files, archived Change-02, `update-sqlite-dependencies/`, `.agents/`, and `.brooks-lint-history.json`.

## Performance counters through owner turn 4

| Counter | Value |
|---|---:|
| owner turns | 4 |
| pre-code proof-review rounds | 3 |
| proof-plan rejections | 3 |
| production-design approvals | 1 |
| proof-gate approvals claimed | 0 |
| implementation turns | 0 |
| delegated/agent calls | 0 |
| production files edited | 0 |
| test source/project files edited | 0 |
| planning artifacts edited | 3 |
| proposal files edited | 0 |
| evidence files edited | 2 |
| builds | 0 |
| test runs | 0 |
| commits/stages/pushes/syncs/archives/cleans | 0 |
| task external gates claimed complete | 0 |
| OpenSpec tasks claimed complete | 0 |
| asynchronous wait ceiling | 10 seconds per signal wait |
| sleep/polling/wall-clock ordering allowances | 0 |

## Applied prerequisite and actual-source inventory

All finalized artifacts (`.openspec.yaml`, proposal, design, delta spec, tasks, and the two Change-13 implementation/performance records) for archived Changes 7–13 were read. Change 14's proposal, design, spec, tasks, `AGENTS.md`, actual executor, contracts, result/event session, reporter support, direct tests, Change-13 test-only composition, retained host delegation tests, and retained DI/composition tests were read.

### Actual production executor surface (retained, not editable)

- `ProcessingRunExecutor` has exactly eight constructor collaborators: `ILogger`, `IProcessingRunConfiguration`, `IProcessingAssetRepository`, `IProcessingSkippedStore`, `IProcessingAdministrativeResolver`, `IProcessingInfrastructureLookup`, `IProcessingRunDelay`, and `TimeProvider`.
- Public execution is `ExecuteAsync(ProcessingRunRequest, IProcessingEventReporter, CancellationToken)`.
- The executor owns exact count, eligibility publication, zero gate, one skipped snapshot, one configuration snapshot, keyset pages, cursor advancement before suppression, bounded `Parallel.ForEachAsync`, admin/airport/fallback, independent writes, disposition publication, delay, terminal result, and finish.
- `ProcessingEventReporter.OpenRunAsync` constructs a session and accepts `RunStarted` before returning it. This is one combined open/start boundary.
- The concrete session linearizes accepted events, commits disposition accounting on accepted `ProgressChanged`, closes open activities before `RunFinished`, and becomes broken on sink failure.
- `ProcessingRunResult` requires exact request identity, zero-offset UTC, end >= start, nonnegative counts, `Processed = Updated + Skipped + Failed`, and outcome/detail consistency.

### Inherited direct executor coverage (retained, eligible for fixture extraction/extension)

`ProcessingRunExecutorTests.cs`:

1. `ExecuteAsync_ZeroEligibility_UsesOneSessionBeforeCountAndReturnsExactCompletedResult`
2. `ExecuteAsync_ActiveCancellationDuringEligibility_ReturnsCancelledWithoutEligibility`
3. `ExecuteAsync_EligibilityFailure_ReturnsFailedWithoutFabricatedEligibility`

`ProcessingRunExecutorChange11Tests.cs` direct methods:

1. `ExecuteAsync_ActiveCancellationAtTokenBearingReporterAdmission_ReturnsOneHealthyCancelledTerminal` (six data rows: eligibility, skipped-information, batch-information, warning, trace, error)
2. `ExecuteAsync_MatchedLocationFallsBackToState_WritesExactStateCityAndOnlyUpdated`
3. `ExecuteAsync_MatchedLocationFallsBackToCountry_WritesExactCountryCityAndOnlyUpdated`
4. `ExecuteAsync_ParallelReporterFailure_CapturesFirstExactlyAndRefusesEveryLaterSessionCall`
5. `ExecuteAsync_MixedBranches_ProduceExactCombinedCausalEventsWritesAndAccounting`
6. `ExecuteAsync_ForeignOceAssetFailure_AllowsPeerToCommitAndRunToComplete`
7. `ExecuteAsync_ActiveParallelCancellation_PreservesPriorCommitClosesEveryActivityAndLeavesInterruptedAssetsUncounted`
8. `ExecuteAsync_ActivityEndFailure_PropagatesOriginalOnceWithoutErrorDispositionOrTerminalRetry`
9. `ExecuteAsync_SkippedInsertFailure_LogsExactErrorCommitsOnlyFailedAndFinishesOnce`
10. `ExecuteAsync_LaterOutOfMemory_PreservesPriorCommitLogsFatalOnceAndAddsNoAssetFailure`
11. `ExecuteAsync_PassDelayFailure_LogsExactFatalOnceRetainsCommitAndHasNoFailedDisposition`
12. `ExecuteAsync_TerminalSinkFailure_PropagatesOriginalExactlyOnceWithoutRetryOrSyntheticResult`
13. `ExecuteAsync_ConfigurationReadFailure_LogsExactFatalAndReturnsFailedWithoutPerAssetWork`
14. `ExecuteAsync_BatchFetchForeignOceAndOrdinaryFailure_AreExactFatalPassFailuresWithoutAssetDisposition`
15. `ExecuteAsync_CancellationAfterWriteEnd_CommitsUpdatedThenReturnsExactCancelledTerminal`
16. `ExecuteAsync_TwoAssetsReverseCompletion_CorrelatesOneDispositionEachAndOneFinalTerminal`
17. `ExecuteAsync_SettingsProviderMutationAfterCapturedSnapshot_RetainsExactRunPolicyAcrossEveryPage`
18. `ExecuteAsync_ParallelismClamp_UsesExactLowerAndUpperBoundary` (lifecycle `modify`: retain `[DataRow(0, 1)]` and `[DataRow(99, 32)]`; implementation adds exact `[DataRow(-7, 1)]`)
19. `ExecuteAsync_ConcurrentIndependentRuns_ShareNoMutableInvocationStateOrEvents`
20. `WithFallbackCity_MakesLoggerOnlyNoCityExecutorBranchUnreachableForEveryHasMatchShape` (structural behavior proof)
21. `ProcessingRunExecutor_LifetimeSurface_ExcludesControlPlaneUiAndMutableInvocationState` (structural proof)

Existing helper/reporting support inspected: `Change11ExecutorProbe`, `Change11Scenario`, `Change11Reporter`, `Change11GatedSettingsProvider`, `Change11ConcurrentRunProbe`, `EligibilityBoundaryOperations`, `GatedZeroOperations`, `RecordingProcessingEventReporter`, and `ProcessingRunExecution.ProcessingOperations`. The Change-11 probe already records cursors, batch sizes, delays, writes, skipped inserts, resolver configs/sessions, ordered operations, and pending disposition identity, but it is monolithic and has non-fail-fast defaults. The implementation turn must extract/extend, not wrap or duplicate, this support. The current settings mutation method changes configuration only, not the skipped-ID backing source; S05 therefore remains planned for extension. Existing direct fixtures still use `TimeProvider.System`, so S01 is not established by lifetime reflection and requires the planned fixed-clock architecture/manifest proofs.

### Explicitly retained outside Change-14 fixture

- Host delegation: `ProcessingBackgroundServiceDelegationTests` methods `TriggerRunAsync_Accepted_DelegatesExactArmedRequestReporterAndManualTokenOnce`, `TryRunScheduledAsync_Accepted_DelegatesExactArmedRequestReporterAndHostTokenOnce`, and `Admission_WhileOwned_InvokesExecutorZeroTimesAndRetainsRecoverableLock`.
- DI identity: `ProcessingServiceRegistrationTests.AddProcessingServices_ExecutorCollaboratorAndHostedAliasesPreserveReferenceIdentity`.
- Change-13 composition/host ownership: `ProcessingRunCoordinatorTestHost.cs`, `ProcessingRunExecution.cs`, `ProcessingRunCoordinatorChange13Tests`, `ProcessingRunCoordinatorTurn2Tests`, and `ProcessingCompositionTurn2Tests`.
- Scheduler proofs: Change-12 schedule tests and audit tests.
- State/reporter adapter/lifecycle: Phase-1 and Changes 8–10 tests.
- Small non-executor model/state checks in `ProcessingPipelineTests.cs` remain outside; no Change-14 wrapper may call them.

## Executor causal/state model — pre-coding gate

### Phases and linearization points

1. **Open + RunStarted**: `OpenRunAsync(request, startedAt, None)` linearizes only when the sink accepts `RunStarted`; before acceptance there is no usable session. Failure propagates unchanged and permits no finish attempt.
2. **Count**: count call begins after start acceptance. Eligibility exists only when `EligibilityDetermined` is accepted. Zero accepted count jumps to cleanup/finish with no snapshots or data-plane calls.
3. **One-time snapshots**: for positive eligibility, skipped IDs are copied once, then configuration is obtained once. Their successful return is the run-local snapshot point. Later source mutation cannot affect this run.
4. **Keyset fetch**: initial cursor is `AssetCursor.Initial`; each non-empty fetch advances the next cursor to the fetched batch's final row **before** suppression. Empty fetch is the loop sentinel.
5. **Per-asset administration**: each non-suppressed asset begins admin resolution. A suppressed row has no per-asset causal chain but still contributes its final-row cursor when applicable.
6. **Optional airport**: admin completion precedes airport invocation; disabled means zero airport calls. Containing airport overrides; non-containing airport fills only absent admin city.
7. **Fallback**: exactly one mandatory `WithFallbackCity` decision applies City → State → Country. A matched Country with neither City nor State receives Country as City and follows the normal write/Updated path. The logger-only no-city guard remains structurally unreachable; no skipped insert, Skipped disposition, or warning may be fabricated there.
8. **Persistence/decision commit**: update/skip-store effect commits when its fake operation returns successfully. A reachable handled-failure decision commits after its Error diagnostic and before non-cancelled Failed publication. There is no executable no-city Skipped decision.
9. **Disposition acceptance**: accounting linearizes only when the reporter accepts the matching `ProgressChanged`. The executor increments its local counter immediately after that acceptance. Persistence effect and accepted disposition are distinct states.
10. **Batch delay**: the batch join completes only after every asset either reaches an accepted terminal disposition, is suppressed, or escapes/cancels/fatally fails. A positive configured delay starts after that join, including after the last non-empty batch.
11. **Next batch**: only successful delay completion permits the next keyset fetch. No delay follows the empty sentinel.
12. **Cleanup/activity end**: healthy finish closes all outstanding activities, with each `ActivityEnded` accepted before terminal acceptance. A broken session cleans activity state locally and must not recurse through reporting.
13. **RunFinished**: validated result construction precedes one finish attempt; terminal completion linearizes only on accepted `RunFinished`.
14. **Return**: `ExecuteAsync` returns only after terminal acceptance and returns the exact same result reference carried by `RunFinished`. Finish rejection returns no result.

### Effect/disposition states

For each asset the only legal effect/accounting states are:

- `NoEffect/NoDisposition`: suppressed, interrupted before commit, or fatal/reporter escape before commit.
- `EffectCommitted/NoDisposition`: write or skipped insert returned, then reporter failed. Effect remains; no rollback/compensation/retry and no terminal retry through a broken session.
- `NoEffect/DispositionAccepted(Failed)`: reachable handled failure accepted; no persistence is invented.
- `EffectCommitted/DispositionAccepted(Updated|Skipped)`: persistence precedes matching acceptance.

No disposition may precede its required persistence. A persistence failure cannot publish a false Updated/Skipped. Stores are independent; there is no cross-store transaction.

### Cancellation and exception taxonomy/precedence

1. Reporter-origin failure has highest classification priority once captured by `ReporterAdmissionBoundary`; the original first reporter exception (including OOM/OCE not caused by the active token) propagates, all later session calls fail with it, and no terminal retry occurs.
2. Active-run cancellation is only an `OperationCanceledException` observed while the executor token is cancelled. It yields healthy-session `Cancelled`, null detail, retained accepted counts/effects, and no interrupted-asset count.
3. Controlled `OutOfMemoryException` from a non-reporter collaborator escapes the per-asset ordinary catch and becomes a healthy-session fatal `Failed` result, message-only detail, no artificial per-asset Failed increment, retained prior effects.
4. Ordinary per-asset admin/airport/update/skipped-store failures emit one Error and accept one Failed disposition, then peers continue and the run can remain Completed.
5. Foreign `OperationCanceledException` with an active but not-cancelled executor token follows the boundary's ordinary classification: handled Failed for per-asset work, fatal Failed for pass-level work.
6. Other pass-level count/snapshot/batch/delay exceptions become one healthy `Failed` terminal result with exact message only and retained prior counts.
7. If result construction itself cannot satisfy the inherited reporter/result contract, that is a design incompatibility, not permission to modify production in Change 14.

### Parallel causal edges and ordering rules

For each asset `a`: `batch(a) < admin(a) < airport?(a) < fallback(a) < persistence-or-decision(a) < dispositionAccepted(a)`. For each batch `b`: every asset-terminal/suppression boundary in `b` precedes `delay(b)`; `delay(b)` precedes `fetch(b+1)`. Every activity end precedes accepted finish; accepted finish precedes return. There is **no global cross-asset order** among admin, airport, persistence, diagnostics, or dispositions. Tests release gates out of input order and compare only these per-asset edges plus complete asset/effect/disposition identity sets. The existing global FIFO pending-disposition queue is not sufficient proof and must be replaced or extended with asset-correlated acceptance in turn 3.

### Deterministic gate/wait rules

- Every gate is a `TaskCompletionSource` with `RunContinuationsAsynchronously`.
- Every wait is signal-driven and bounded only by the existing 10-second deadlock ceiling; no `Thread.Sleep`, `Task.Delay`, polling, wall-clock ordering, host, or infrastructure.
- A test must observe the entered signal before cancellation/release, assert forbidden later signals are incomplete at the boundary, then release exactly once and await execution.
- Concurrency rows hold N active resolver/persistence gates, assert N+1 has not entered, then release deliberately out of order.
- Reporter gates distinguish Attempted from Accepted. Failure injection occurs pre-acceptance, and assertions inspect both collections.

## Boundary table — exact expected outcomes

Legend: `S/E/C/B/A/P/D/F/R` = start, eligibility, config/skipped snapshots, batch, admin, airport, persistence/decision, disposition, finish, return. “No extras” always means no unlisted downstream collaborator call, disposition, terminal retry, rollback, compensation, or synthetic result/event.

| Boundary/injection | Exact expected result/events | Effects/counts | Calls/order and no-extras |
|---|---|---|---|
| token already cancelled when count is called | Cancelled; S,F; no E | none; 0/0/0/0 | count is called once with the cancelled token and the fake aborts before/during count; no snapshots/batch/asset/delay |
| active cancellation during count | Cancelled; S,F; no E | none; zero | count entered once; no downstream |
| active cancellation while E acceptance waits | Cancelled; S accepted, E attempted not accepted, F once | none; zero | no snapshots/batch; healthy session remains finishable |
| cancellation during skipped snapshot | Cancelled; S,E,F | none; zero | skipped load once; no config/batch |
| cancellation during config snapshot | Cancelled; S,E,F | none; zero | skipped then config; no batch |
| cancellation during batch fetch | Cancelled; S,E,F | prior counts retained | no asset work from interrupted fetch; no later batch/delay |
| cancellation during admin | Cancelled; S,E,F | interrupted asset uncounted | no airport/persistence/disposition for it |
| cancellation during airport | Cancelled; S,E,F | interrupted asset uncounted | admin before airport; no persistence/disposition |
| cancellation during update before success | Cancelled; one F | no new write effect/disposition | write entered; no false Updated |
| cancellation during skipped insert before success | Cancelled; one F | no new insert effect/disposition | insert entered; no false Skipped |
| cancellation after successful update before D acceptance | Cancelled after non-cancelled Updated acceptance | write retained; Updated counted | write-end < Updated; no rollback/retry |
| cancellation after successful skipped insert before D acceptance | Cancelled after non-cancelled Skipped acceptance | insert retained; Skipped counted | insert-end < Skipped |
| cancellation after reachable handled Failed decision | Cancelled after non-cancelled Failed acceptance | no persistence; Failed counted | Error accepted < committed Failed decision < cancellation < Failed acceptance |
| cancellation between batches | Cancelled; prior D then F | prior effects/counts retained | no next fetch; no retry |
| cancellation during controlled delay | Cancelled; prior D then F | prior effects/counts retained | delay entered; no next fetch |
| later active cancellation after prior effects | Cancelled | exact prior coherent counts | no rollback/compensation/retry |
| foreign OCE at admin/airport/update/insert | Completed if peers finish | injected asset Failed once; effects only if committed before throw | Error < Failed; peer continues |
| foreign OCE at count/snapshot/batch/delay | Failed with exact OCE message; one F | prior counts retained, no fatal Failed increment | no later batch; one fatal logger entry |
| count ordinary failure | Failed; S,F; no E | zero | count once; no downstream |
| skipped snapshot ordinary failure | Failed; S,E,F | zero | no config/batch |
| config snapshot ordinary failure | Failed; S,E,F | zero | skipped before config; no batch |
| batch ordinary failure | Failed; S,E,F | prior counts retained | no asset work from failed fetch/later batch |
| delay ordinary failure | Failed; S,E, prior D,F | prior effects/counts retained | no next fetch |
| admin ordinary failure | Completed | one Error + Failed; peer continues | no airport/persistence for failed asset |
| airport ordinary failure | Completed | one Error + Failed; peer continues | admin < airport; no persistence for failed asset |
| update ordinary failure | Completed | no write effect; one Error + Failed | no Updated; peer continues |
| skipped insert ordinary failure | Completed | no insert effect; one Error + Failed | warning may precede insert; no Skipped |
| non-reporter OOM at admin/airport/update/insert | Failed with exact message; one F | no ordinary Failed increment; prior effects retained | escapes per-asset handler; no Error event for OOM |
| non-reporter OOM at count/snapshot/batch/delay | Failed with exact message; one F | prior counts retained | no later batch; no ordinary Failed |
| reporter open/RunStarted failure (ordinary/OOM) | original exception; no result | none | one start attempt; no usable session, E, F, fallback |
| reporter E/log/activity-start/activity-end/disposition/cleanup failure | original first exception; no result | pre-fault effects remain; only pre-fault accepted counts | session broken; zero F attempts; no recursive report |
| nested resolver activity/log reporter failure | original reporter exception; no result | no Failed disposition fabricated | not source unavailability; no terminal attempt |
| reporter failure after persistence at D | original reporter exception; no result | effect retained; D unaccepted | one D attempt; zero F; no compensation |
| reporter rejects F | original reporter exception; no return | all prior effects/counts remain | exactly one validated F attempt, zero accepted F, no retry |
| healthy completed empty/mixed | exact Completed result and same F/R reference | coherent counts | one accepted F, cleanup before F, no extras |
| healthy cancelled/failed partial | exact request/times and retained coherent counts | no fatal count inflation | one accepted F; null cancel detail / exact fatal message |

## Owner turn 2 — approved planning correction after two design-gate rejections

Two pre-code design-gate reviews rejected the turn-1 proof package: one found fabricated executable no-city behavior, and the other found inaccurate retained/planned classifications plus under-specified matrix cases and parallel identity proof. The user approved one coordinated artifact correction. No design approval is claimed.

- S16 now specifies the applied City → State → Country fallback: Country becomes City and the normal write/Updated path runs; the retained country-fallback executor method and unreachable-guard structural method jointly prove it. The prior blocker is removed.
- S27 now covers only the reachable committed handled-Failed decision before non-cancelled Failed acceptance.
- S01, S05, S10, and S13 are planned extensions: fixed-clock/forbidden-dependency architecture checks, mutation of both snapshot sources, an explicit negative parallelism row, and asset-correlated partial-order/complete-set proof.
- S30 accurately calls count once with a pre-cancelled token; the fake observes cancellation before/during count.
- Task 5.4 covers both enabled admin-before-airport order and disabled zero-airport calls. Task 5.5 has independently identifiable containing/preserve/fill cases.
- Every planned Matrix/Rows method declares exact machine-readable case IDs, arguments, and assertions; future manifest checks require each case to exist exactly once in its declared file/type.

## Remaining design risks; no known contradiction

1. Eligibility lower than fetched rows is only compatible with the concrete reporter when suppressed/unclassified rows keep accepted dispositions <= eligibility. The planned lower-count case uses a suppressed fetched asset and does not weaken the reporter invariant.
2. Existing Change-11 support correlates dispositions through a global pending queue. Under out-of-order parallel completion it can accidentally encode global order. The implementation must use per-asset identities/edges and complete identity/effect sets rather than FIFO proof.
3. Skipped-store `AddAsync` has no token. Cancellation-before-success cases gate a closure that observes the known active token and throws OCE; they do not imply the interface transports a token.
4. The executor local counters increment immediately after reporter acceptance. Reporter-failure-after-effect cases intentionally produce no result; assertions use fake effects and reporter attempts, not a wrapper that discards a returned result.
5. Activity boundaries originate in the resolver/session collaborator, not directly in the executor. Resolver-reporter cases prove the exact awaited nested path and do not attribute activity creation to executor code.
6. Existing tests using `TimeProvider.System` must be migrated only during the implementation test-fixture refactor; no production source change is authorized.

## Owner turn 3 — final pre-code proof-plan correction after proof rejection

The production design reviewer approved the corrected production design. The proof reviewer rejected the turn-2 manifest; this is not gate approval. Owner turn 3 edits only the two evidence files. Owner turn 2 was the sole planning-edit turn and was explicitly user-approved; it edited exactly these three planning artifacts: `specs/processing-run-executor-testing/spec.md`, `design.md`, and `tasks.md`.

### Exact lifecycle migrations

- `ExecuteAsync_SettingsProviderMutationAfterCapturedSnapshot_RetainsExactRunPolicyAcrossEveryPage`: lifecycle `modify`; extend the existing no-argument method to mutate both configuration and skipped-ID backing sources. Remove the duplicate planned snapshot method name.
- `ExecuteAsync_ParallelismClamp_UsesExactLowerAndUpperBoundary(System.Int32,System.Int32)`: lifecycle `modify`; add only `[DataRow(-7, 1)]`, retaining `[DataRow(0, 1)]` and `[DataRow(99, 32)]`. Remove the duplicate planned below-minimum method name.
- `ExecuteAsync_TwoAssetsReverseCompletion_CorrelatesOneDispositionEachAndOneFinalTerminal`: lifecycle `modify`; remove the global exact disposition order and the FIFO pending-disposition correlation. Preserve the reverse-release/completion fact, assert complete asset/write/disposition identity sets, and assert only each asset's admin → write effect → Updated edges. Remove the duplicate planned reverse-completion method name.
- S23 retains the existing State and Country no-argument methods and adds only `ExecuteAsync_MatchedLocationPreservesCity_WritesExactCityAndOnlyUpdated`; remove the planned Cartesian fallback-row method.
- No existing TestMethod is removed or renamed. Four unimplemented duplicate planned names have lifecycle `remove`; all other existing referenced methods are `retain` unless listed `modify`.

### Canonical proof manifest

- The method catalog records exact declaring file, fully-qualified type, signature, existence, lifecycle, and authoritative method proof kind. Mixed scenario/task mappings derive `proofKinds`; S16 and tasks 5.1/5.6 no longer have inaccurate scalar proof kinds.
- The canonical top-level case table has 63 unique executions. Shared ordinary update/insert cases each cover S17/S24/S35 once. Shared OOM update/insert cases each cover S24/S36 once. S16 and S23 share the one Country-fallback execution. S16 and S33 multi-method cases bind explicitly to their exact no-argument methods.
- Every case binds by exact `DataRow`, no-argument method, or typed-case-table case ID with ordered typed arguments. Every case references named contracts and supplies explicit calls, effects, events, logs, result, identities, partial-order edges, forbidden observations/retries, and `noExtras=true`.
- Reflection is limited to file/type/method/signature/TestMethod/DataRow/case binding. Assertion contracts are validated as resolvable manifest references and must be consumed by test code; their behavioral truth is not claimed to be reflected.
- Task/Gate 9.4 selects exactly seven planned architecture/manifest TestMethods. Retained host/DI methods remain in a separate inventory and are not selected by that filter.

The companion `verification-plan.json` remains authoritative. Owner turn 3 ran parse/reference/cardinality checks plus strict OpenSpec/status/diff/staging checks only. It ran no build or test and claimed no task or gate complete.

## Owner turn 4 — final manifest-only correction after proof-gate round 3 rejection

Proof-gate round 3 rejected the manifest; no proof approval is claimed. This turn edits only the log and verification manifest.

- All 21 named non-empty-batch cases now contain exactly one accepted Information `LogEmitted` event for each non-empty batch, with exact level, message, request correlation, and causal edges `batch return → accepted batch log → first per-asset handling`. The two cases with two non-empty batches (`cancel-admin`, `cancel-airport`) contain exact batch-1 and batch-2 events without duplication.
- `both-snapshot-sources-mutate` contains the two exact Information batch events and exact verbose Trace events for `asset-late` and `asset-a`; each Trace is correlated to the request/asset and ordered `admin → accepted Trace → write`.
- `parallelism-above-maximum`, `parallelism-within-four`, and `parallelism-within-thirty-two` use unordered effect/disposition identity sets, an exact monotonic complete count set, gated/per-asset causal edges, and no asset-to-global-ordinal or cross-asset disposition order.
- Test-only fixed-time lifecycle records modify `ProcessingRunExecution.cs:21` and `ProcessingRunExecutorTests.cs:183,259` from `TimeProvider.System` to the approved fixed UTC seam. `Change11TimeProvider` and its uses around lines 1390, 1469, and 1625 are explicitly retained. No other excluded host/coordinator/DI fixture is in migration scope.
- S03 and task 1.3 now derive structural authority from two exact planned architecture/manifest methods and retain the host/DI inventory only as external inventory evidence.
- Wait rules remain `TaskCompletionSource` with `RunContinuationsAsynchronously`, a 10-second deadlock ceiling per signal wait, and zero sleep, polling, or wall-clock ordering.

Owner turn 4 runs JSON/reference/cardinality/exact-named-case checks, strict OpenSpec validation, diff/status, and staging inspection only. Production/test/build/test counters remain zero; no task or gate is complete.

## Owner turn 5 — implementation and final verification

Both independent pre-code gates returned **APPROVE DESIGN GATE** before implementation began: the production-design gate approved the corrected design, and the proof-design gate approved the corrected verification manifest. Implementation started from baseline `fd48bd43a717cebeb27e7888e4963d066b144365` at `2026-08-31T15:53:42Z`. This turn changed test code and Change 14 evidence only; it made no production-source, dependency, schema, scheduler/coordinator/state/host/DI, documentation, or UI change.

### Implemented lifecycle and proof package

- Added the scheduler-free fixed-UTC fixture, asynchronous gates using `TaskCompletionSource` with `RunContinuationsAsynchronously`, scriptable fail-fast in-memory collaborators, recording/fault reporter, effect/call/log histories, and result invariant helpers in `ProcessingRunExecutorTestFixture.cs`.
- Modified the captured-snapshot proof so both configuration and the skipped-ID backing source mutate after capture while the run retains both captured values.
- Added `[DataRow(-7, 1)]` while retaining `[DataRow(0, 1)]` and `[DataRow(99, 32)]`.
- Replaced Change 11's FIFO pending-disposition queue with asset-keyed pending dispositions. The reverse-completion proof retains the gated reverse-completion fact, checks complete asset/effect/disposition sets, and asserts only per-asset `admin → write → Updated` edges; it asserts no global cross-asset disposition order.
- Applied the three declared `TimeProvider.System` test-support migrations to the fixed UTC seam and retained `Change11TimeProvider` unchanged.
- Retained the existing State and Country fallback methods and added only the missing City fallback proof.
- Added the manifest-declared snapshot/paging, bounded parallelism, disposition/source, persistence, cancellation, pass/critical failure, reporter failure, terminal-invariant, architecture, and manifest tests. The active catalog resolves to 45 TestMethods and 63 canonical case bindings; the manifest retains 43 scenarios, 42 tasks, and four external gates.

### Stabilization command ledger

Every implementation shell command attempt is listed below. Times are wall-clock `real` values printed by `/usr/bin/time -p`, except commands deliberately executed literally for final gates, which record the shell `SECONDS` value.

| # | Command / scope | Outcome | Wall time |
|---:|---|---|---:|
| 1 | `dotnet build tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --no-restore` | failed: duplicate `SkippedSnapshot` property | 19.06s |
| 2 | same build after duplicate removal | failed: wrong eligibility property plus unused fixture field | 1.45s |
| 3 | same build after compile fixes | passed, 0 warnings/errors | 2.16s |
| 4 | `dotnet test ... --no-build --filter 'FullyQualifiedName~ProcessingRunExecutorSnapshotsAndPagingTests'` | passed 5/5 | 2.62s |
| 5 | parallelism class filter | passed 3/3 | 0.95s |
| 6 | disposition class filter | passed 4/4 | 0.93s |
| 7 | disposition-failure class filter | passed 2/2 | 0.95s |
| 8 | persistence class filter | passed 7/7 | 0.94s |
| 9 | cancellation class filter | passed 9/9 | 0.93s |
| 10 | pass-failure class filter | failed 1 of 14: OOM-delay page contained two assets before delay | 0.92s |
| 11 | build after placing OOM-delay on an exact inter-batch boundary | passed | 1.81s |
| 12 | exact OOM matrix method filter | passed 9/9 rows | 1.20s |
| 13 | reporter-failure class filter | passed 11/11 | 0.90s |
| 14 | terminal-invariant class filter | passed 3/3 | 0.93s |
| 15 | modified Change 11 executor class filter | passed 28/28 | 0.94s |
| 16 | modified direct executor boundary class filter | passed 3/3 | 0.91s |
| 17 | architecture class filter | failed 1 of 2 because the checker scanned its own prohibited-API string literals | 0.88s |
| 18 | build after excluding structural checker files from the direct-proof timing scan | passed | 2.03s |
| 19 | architecture class filter | passed 2/2 | 1.30s |
| 20 | manifest class filter | failed 2 of 5: semantic checker name contained “Placeholder”; DataRow reflection required params-array flattening | 0.94s |
| 21 | build after manifest checker corrections | passed | 1.67s |
| 22 | manifest class filter | passed 5/5 | 1.39s |
| 23 | pre-final build | passed, 0 warnings/errors | 0.75s |
| 24 | `dotnet test ... --no-build --filter 'TestCategory=Change14'` | failed 1 of 96: skipped persistence cancellation exposed a per-asset token propagation race | 1.00s |
| 25 | build after synchronizing the skipped-store cancellation closure to the per-asset token cancellation registration | passed, 0 warnings/errors | 1.72s |
| 26 | exact active-persistence-cancellation method filter | passed 2/2 rows | 1.47s |

The last test edit was the per-asset-token cancellation synchronization in `ProcessingRunExecutorPersistenceTests.cs`. No test file changed after command 25. Command 25 is therefore the final build after the last test edit; command 26 is a stabilization rerun, followed by the ordered final gates below.

### Ordered final verification after the last test edit

1. Build: command 25 passed with zero warnings and zero errors (1.72s).
2. Dedicated Change 14 category: `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --no-build --filter 'TestCategory=Change14'` passed 96/96 (0.95s).
3. Exact task 9.2 command: `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~ProcessingRunExecutor|FullyQualifiedName~ProcessingPipeline"` passed 103/103 (shell timing 2s).
4. `npm run test` was executed exactly once in this implementation turn and passed 531/531 with default Integration and Performance exclusions (reported duration 34.324s; shell timing 39s).
5. Exact task 9.4 seven-method architecture/manifest filter passed 7/7 (shell timing 1s).
6. Strict OpenSpec validation, final status, scope diff, and staging inspection follow this log update and are recorded below after execution.

The executor signal wait ceiling remains 10 seconds per approved test wait. The controlling parent/delegated-operation wait ceiling is 600 seconds; no parent wait command was executed in this turn. No test uses sleep, polling, `Task.Yield`, filesystem/infrastructure dependencies, sync-over-async, or a global cross-asset disposition order.

### Counters before strict final checks

- Design gates: 2 approved, 0 outstanding.
- Build attempts: 8 total; 6 passed and 2 failed during stabilization.
- Direct/focused/structural test invocations before strict checks: 22 total, including the one and only `npm run test` invocation; all final invocations passed.
- Final dedicated category: 96 passed, 0 failed.
- Final focused task 9.2 scope: 103 passed, 0 failed.
- Final repository default suite: 531 passed, 0 failed.
- Final task 9.4 scope: 7 passed, 0 failed.
- Change 14 task checkboxes: 42/42 complete.


### Strict final checks and scope disposition

- `openspec validate 14-cover-executor-independently-from-scheduler --strict`: passed (change valid).
- `openspec status --change 14-cover-executor-independently-from-scheduler --json`: passed; schema `spec-driven`, planning complete, implementation complete.
- `openspec instructions apply --change 14-cover-executor-independently-from-scheduler --json`: `state=all_done`, progress `42/42`, remaining `0` (shell timing 0s).
- `git diff --check`: passed with no whitespace errors.
- Task count was verified by parsed file content as 42 complete and 0 pending. The shell ledger's initial grep display of `complete=0` was a grep bracket-expression mistake, not a task-file result; direct file parsing and OpenSpec apply both report 42/42.
- `git diff --cached --name-only`: empty; no files are staged.
- Scope review found no modified production `src/` file. Implementation outputs are the declared test files/support migrations plus Change 14 `tasks.md` and this log.
- Existing unrelated baseline work remains untouched and unstaged: Change 02 delete/archive/spec synchronization, `update-sqlite-dependencies`, `.agents/`, and `.brooks-lint-history.json`. Change 14 design/spec/manifest edits are the approved prior planning turns, not production implementation edits. Nothing was cleaned, staged, committed, pushed, synced, or archived.


## Owner turn 6 — final pre-audit round 1 remediation

Final pre-audit round 1 rejected owner turn 5. All 42 task checkboxes were reopened before remediation and remain 0/42 pending until the ordered final gate completes. This turn is test/evidence-only: no production `src/`, dependency, schema, or runtime file changed.

### A-H findings closed

- **A — compiled exhaustive contracts:** added `ExactObservationContractRegistry` with 62 typed behavioral observations and retained the typed structural contract for `unreachable-no-city-guard`, covering all 63 canonical bindings. `ExactObservationContractEngine` checks exact call ledgers, effects, attempted events, accepted events, ILogger tuples, fetched-page identities, escaped exceptions, active/foreign cancellation observations, cleanup/max-active observations, and no extras. Concurrent cases compare unordered multisets and keep their explicit gate/per-asset causal assertions; they do not impose global cross-asset order.
- **B — fail-fast seams:** every `ExecutorFixture` seam throws by default; `Successful` and each scenario explicitly opt into only expected seams.
- **C/D — causal concurrency:** removed FIFO/`.Single()` pending-disposition inference and global S13 write/disposition order. Per-asset authorization gates prove only causal edges. Parallelism proofs admit any N distinct identities, reject N+1, release an arbitrary observed identity, await its accepted disposition, and admit exactly one additional distinct identity.
- **E/H — continuation and partial effects:** ordinary failures use a completed failed-disposition page before the healthy peer page. OOM cases with prior effects complete the prior page/effect before the fatal page or delay boundary. Active and foreign OCE, attempted versus accepted reporter events, cleanup, terminal precedence, and committed partial effects are included in exact contracts and direct assertions.
- **F — compiled architecture:** architecture/manifest tests use reflection, compiled metadata, and IL only. Runtime tests do not read the repository, source text, or `verification-plan.json`. Both `ProcessingRunExecution.RunOnceAsync` overloads return `Task<ProcessingRunResult>`.
- **G — DynamicData:** validation reads each method's own `DynamicDataAttribute` custom-attribute constructor member and named `DeclaringType`, then resolves that exact member/type.

The verification manifest now declares the compiled registry/engine as runtime authority, carries the exact 62 behavioral observations as implementation-time provenance, and retains the structural case plus 43 scenarios, 42 tasks, 49 lifecycle methods (45 active), 63 bindings, and four external gates.

### Remediation/interruption command ledger

The harness interrupted immediately after the first `ProcessingRunExecutorPassFailureTests --no-build` attempt. On resume, `git status --short`, `git diff --stat`, and staged inspection confirmed no staged files, no production-source edit, the unrelated Change 02/archive/spec/update-sqlite work remained untouched, and Change 14 remained 0/42. A fresh timed build then passed before rerunning that class, as requested.

- Compile remediation builds initially failed once on obsolete DynamicData members, then passed after exact `CustomAttributeData` handling (0 warnings/errors; reported build times included 3.11s and 12.49s). A later accidental partial restoration of `ProcessingRunExecutorChange11Tests.cs` was recovered from baseline `fd48bd43a717cebeb27e7888e4963d066b144365`; subsequent builds passed. No recovery output under ignored `_out/` is part of scope.
- Snapshot stabilization: first failed on stale ILogger/event cardinalities, second failed on fetched semantic identities, then passed 5/5 after exact page capture and deterministic alias alignment.
- Parallelism: first failed all 3 rows on symbolic all-assets seam counts, then passed 3/3 after exact arbitrary-identity expansion.
- Ordinary continuation: first failed 2/2 on the remediated three-page count, then passed 2/2 with the failed disposition accepted before the peer page.
- Persistence: first failed 7/7 on exact seam expansion, second failed 3/7 on ILogger boundary tuples, then passed 7/7.
- Cancellation: first failed 9/9 on exact boundary seams, second failed only `cancel-batch` on a non-returned target identity, then passed 9/9.
- Pass/OOM: the interrupted `--no-build` run failed 12/14; the resumed timed build passed in 1.84s; rerun failed 5/14 on prior-effect identities; the corrected class passed 14/14.
- Reporter: initial run failed 9/11, then 7/11, 5/11, 3/11, and 1/11 as attempted/accepted ledgers, activity cleanup, effects, and identities became exact; final targeted run passed 11/11.
- Terminal partial results first failed 2/3 on complete seam counts, then passed 3/3.
- Restored retained Change 11 first failed 10/28, then 9/28, 8/28, 3/28, and 1/28 while deterministic IDs, fixed UTC, ledger ownership, exact fatal text, and semantic aliases were aligned; final targeted run passed 28/28.
- Compiled architecture passed 2/2 and manifest passed 5/5; combined task 9.4 stabilization later passed 7/7 in reported 1.192s (1.55s wall).
- Change14 stabilization passed 96/96. Exact-contract capture deliberately failed 62 behavioral executions once to emit implementation-time observations (62 unique) while the structural execution passed; that temporary emitter was removed. The first embedded-registry validation failed 62 due incorrect raw-string quote escaping, then failed 3 on random activity IDs. Activity call IDs were normalized while start/end correlation remained label-based. Two consecutive category stability runs then passed 96/96 (reported 1.034s and 0.664s).
- The last pre-final timed build after exact registry architecture coverage passed with zero warnings/errors (1.66s build, 1.79s wall). `git diff --check` passed and `git status --short -- src` was empty.

All direct signal waits retain the bounded 10-second ceiling. The controlling parent/delegated-operation ceiling is accurately 600 seconds; no parent operation exceeded it. No sleep, polling, `Task.Yield`, filesystem proof, sync-over-async, staging, commit, push, sync, archive, or clean action occurred.

### Ordered final verification after the last test edit

1. `dotnet build tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --no-restore`: passed, 0 warnings/errors (0.58s build; 0.70s wall).
2. `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --no-build --filter 'TestCategory=Change14'`: passed 96/96 (0.638s reported; 0.97s wall).
3. Exact task 9.2 command, `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~ProcessingRunExecutor|FullyQualifiedName~ProcessingPipeline"`: passed 103/103 (0.630s reported; 1.91s wall).
4. `npm run test` was executed exactly once in owner turn 6: passed 531/531 with default Integration and Performance exclusions (25.713s reported; 28.83s wall).
5. Exact task 9.4 command, `dotnet test --project tests/ImmichReverseGeo.Tests/ImmichReverseGeo.Tests.csproj --filter "FullyQualifiedName~ProcessingRunExecutorArchitectureTests|FullyQualifiedName~ProcessingRunExecutorManifestTests"`: passed 7/7 (0.616s reported; 2.05s wall).
6. Only after all final test gates passed, task checkboxes changed from 0/42 to 42/42. `openspec validate 14-cover-executor-independently-from-scheduler --strict` passed; status reports schema `spec-driven`, planning complete, and implementation complete; apply instructions report `state=all_done`, total 42, complete 42, remaining 0.

Final diff, status, production-scope, and staging inspection followed this log edit. `git diff --check` passed; branch/head remain `major-redesign` / `fd48bd43a717cebeb27e7888e4963d066b144365`; `git status --short -- src` was empty; parsed tasks are 42 complete and 0 pending; `git diff --cached --name-only` was empty. Unrelated Change 02/archive/spec synchronization, `update-sqlite-dependencies`, `.agents/`, and Brooks history remain untouched and unstaged. No test file changed after step 1's final build. Nothing was staged, committed, pushed, synced, archived, or cleaned.

## Owner turn 7 — audit round 2 rejection and implementation remediation turn 3

Audit round 2 rejected owner turn 6. The 96/96 and 531/531 results above remain historical command results only; they did not establish acceptable authority. The target miss was architectural: emitter-derived exact-observation registries, duplicated behavioral catalogs, string/prefix and cardinality proofs, boolean token ownership, non-correlated concurrent dispositions, blanket-success fixture setup, and incomplete IL traversal made the proof self-certifying. Tasks 6.3–6.5, 7.1–7.4, 8.2–8.4, and 9.1 were reopened immediately and remain pending until the new ordered final gate.

### Finding-by-finding remediation

- Deleted the two exact-observation capture registry files; no capture/emitter registry remains.
- Embedded `processing-run-executor-contracts.json` is the single behavioral authority for 63 cases and 49 lifecycle methods. The verification catalog derives methods and bindings from it.
- Replaced collaborator/event strings, prefixes, counts-only checks, and token booleans with typed exact calls, events, effects, disposition identities/count states, exceptions, cancellation owners, and causal edges.
- Every token-bearing seam records the actual token. Run, batch-asset, None, and foreign owners use exact equality/inequality; observed OCE ownership is exact.
- Replaced FIFO disposition inference with a deterministic single-admission async gate. Actual observations retain exact asset, outcome, all four cumulative counts, and accepted-event sequence.
- Removed the blanket successful fixture. Raw seams fail fast; tests opt into only reached seams.
- Replaced weaker scanners with one transitive IL walker following helpers, async MoveNext, lambdas, and local/compiler-generated methods. An excluded forbidden sentinel proves Task.Delay, filesystem, and sync-over-async detection.
- Replaced the duplicated 16,401-line verification plan with an artifact path/SHA/count/provenance record and deterministic external equivalence gate. Runtime tests use only the embedded resource.

### Remediation command/error ledger before final verification

- Initial checkpoint build failed with 25 old fixture/registry references; typed migration reduced this to 7 assertion/legacy errors, then builds passed with zero warnings/errors.
- The first targeted manifest command named a nonexistent test and returned MTP exit 8. Exact filtering exposed symbolic disposition IDs; the artifact moved to typed asset ordinals and exact cumulative counts.
- Targeted fallback exposed the run-token versus Parallel.ForEachAsync batch-asset-token distinction; both owners are now modeled exactly.
- Architecture initially flagged normal compiler awaits. The final check detects the immediate GetAwaiter/GetResult chain, while the excluded sentinel still proves detection.
- Concurrency exposed false count-to-asset pairing. Actual records retain both; concurrent authority checks exact identity/outcome and exact count-state multisets independently with per-asset edges.
- Reporter stabilization initially failed 9/11 on exception reference/type authority and now passes 11/11.
- Current targeted passes: architecture 3/3, manifest 5/5, snapshots 5/5, parallelism 3/3, dispositions 4/4, continuation 2/2, persistence 7/7, cancellation 9/9, pass/OOM 14/14, reporter 11/11, terminal 3/3, and retained Change 11 28/28.
- The equivalence command passes with SHA-256 `53c3414624156baba2ce7a5a9e270141868d1d692eb4e94312bc89e4a474833d`, 63 contracts, 49 methods, one authority, no rejected keys, and embedded-resource confirmation.

The owner-turn-6 final sequence is superseded. The first owner-turn-7 final sequence attempt built successfully (0 warnings/errors; 0.64s reported), then the Change14 category failed 95/97 because the exact seam matrix omitted the resolver's original `TestSinkException` for activity-start/end sink failures. The first targeted correction incorrectly named a wrapper and failed 9/11; the two Admin seam contracts were then corrected to the exact original type/message, the artifact SHA changed to `53c3414624156baba2ce7a5a9e270141868d1d692eb4e94312bc89e4a474833d`, and the full final sequence was restarted from its build. Task 9.2, the single permitted npm test, and task 9.4 have not run in owner turn 7. No reopened checkbox is complete yet. No production, dependency, schema, runtime, staging, commit, push, sync, archive, or clean action occurred.

### Owner-turn-7 final ordered verification

The restarted sequence passed without another test/evidence-code edit: build 0 warnings/errors (0.64s reported, 0.9s wall); Change14 97/97 (0.779s reported, 1.186s wall); focused task 9.2 104/104 (0.749s reported, 2.336s wall); the single owner-turn-7 `npm run test` invocation 532/532 (34.127s reported, 37.3s wall); and task 9.4 3/3 (0.628s reported, 1.086s wall). The authority equivalence gate passed 63 contracts/49 methods at SHA-256 `53c3414624156baba2ce7a5a9e270141868d1d692eb4e94312bc89e4a474833d`. Strict OpenSpec validation passed and apply status is `all_done`, 42/42. Final branch/head are `major-redesign` / `fd48bd43a717cebeb27e7888e4963d066b144365`; `git diff --check` passed, `git status --short -- src` and staged diff were empty, and unrelated pre-existing workspace changes remained untouched.

## Owner turn 8 — audit round 3 rejection and remediation turn 4

Audit round 3 rejected owner turn 7. Tasks 2.1, 2.2, 2.4, 5.3, 5.6, 7.4, 8.3, 8.4, 9.1, 9.4, and 9.5 were reopened before any remediation edit. The eight target misses were: S16 length-only/duplicated structural authority; narrow filesystem-member checks; raw-byte hashing; never-retired static correlation graphs; attempted-RunStarted inference for open-session state; OCE-derived foreign-token expectation; shallow schema/mapping equivalence with incorrectly cased rejected keys; and reporter-failure fixtures that enabled unreachable downstream seams.

### Round-3 closure

1. **S16 structural authority:** schema 4 contains five typed fallback rows with exact input/output Country/State/City/HasMatch, guard result, `NotInvoked` disposition, and false effect. The test enumerates authority rows as inputs, records actual typed outputs, and compares every field; the hard-coded matrix and calls-length surrogate are gone.
2. **Filesystem IL policy:** one transitive walker now rejects every System.IO declaring type, type token, field/property type, parameter, return type, generic argument, and array/byref element except an explicit narrow Stream/MemoryStream/StreamReader operation allowlist and Assembly.GetManifestResourceStream. The excluded async-lambda sentinel independently contains File.ReadAllText, FileInfo, FileStream, Directory.EnumerateFiles, Task.Delay, and sync-over-async; its self-test proves each distinct path.
3. **Canonical semantic hash:** the external gate parses JSON, recursively sorts object keys, preserves array order, emits canonical UTF-8 JSON plus one LF, and hashes that semantic representation. It proves reordered root keys hash identically. Final pre-gate semantic SHA is `5fb25f342ce7f00698e36d5e24d18b61e307187dc0dab6fdec074ca7076461c9`; no raw-byte SHA field remains.
4. **Correlation lifetime:** static event/activity dictionaries and static AsyncLocal asset context were removed. Reporter-scoped asset/activity maps retire on acceptance/rejection and complete at exact zero; main/retained pending disposition, retained asset/airport, and concurrent token/page maps retire and assert zero after run. Reporter exception references are consumed during classification or cleared/asserted at completion.
5. **Open session:** explicit observation fields now record sessionConstructed, sessionReturned, terminalAttempted, terminalAccepted, and activitiesBalanced. Open-start ordinary/OOM rows assert constructed=true, returned=false, terminalAttempted=false, zero downstream, and no terminal recursion; engine no longer derives open state from attempted events.
6. **Foreign token:** both retained foreign sources pass their independently owned CancellationTokenSource token to VerifyLegacyCase. The observation separately records the actual OCE token and compares exact identity/inequality; expected ownership is never read from the OCE.
7. **Round-3 equivalence (superseded in round 4):** owner turn 8 expanded the script to 63 typed contracts, 49 methods/45 active, 43 scenarios, 42 tasks, four gates, references, mapping partitions, and source-text checks. Audit round 4 rejected its subset-only key checking and source-text/DynamicData proof; the current schema-5 boundary is documented below.
8. **Granular reporter failures:** open-start enables only reporter; eligibility enables reporter/count; batch-log failures stop before resolver; resolver activity failures keep writes/downstream fail-fast; disposition/cleanup finish rows enable only exact prior happy write seams. Exact contracts continue to reject every extra call/effect/event.

### Round-3 editing command/error ledger

- First S16, architecture, and reporter target: S16 passed 1/1 and architecture passed 3/3; reporter cleanup/activity-end exposed one over-pruned finish seam and explicit accepted-activity balance. Both were corrected; reporter now passes 11/11.
- Initial full equivalence failed because the approved spec had 43 unlabeled scenarios; the existing headings were mapped S01-S43 and exact set equality now passes.
- Initial source-file mapping failed on one inactive historical lifecycle method and on a cross-file nameof reference. Active declarations are now mapped by exact declaring class; all 45 active methods are source-verified while four inactive lifecycle entries remain inspectable metadata.
- Removing static AsyncLocal correlation initially exposed one retained multi-asset event whose asset identity needed exact token correlation; it is now correlated from actual call tokens without global state. Retained Change11 passes 28/28.
- First broad type-token filesystem run correctly caught File/Directory/Path references inside the architecture/manifest policy verifiers themselves. Those self-verifier roots and the excluded sentinel/walker implementation are excluded from the target graph and independently self-tested; the architecture class passes 3/3.
- Adding zero-pending assertions exposed reporter-origin activity failures being temporarily classified as handled source failures. Exact rejected-exception identity now suppresses that pending decision and is immediately retired; reporter passes 11/11.
- Targeted classes currently pass: snapshots 5/5, parallelism 3/3, dispositions 4/4, ordinary failures 2/2, persistence 7/7, cancellation 9/9, pass/OOM 14/14, reporter 11/11, terminal 3/3, eligibility 3/3, manifest 5/5, retained Change11 28/28, architecture 3/3, and S16 1/1.
- The complete semantic equivalence command passes 63 contracts, 49 methods/45 active, 43 scenarios, 42 tasks, four gates, semantic SHA and mapping digest. The final ordered owner-turn-8 sequence then passed after the last test/evidence-code edit: build 0 warnings/errors (1.82s reported, 2.043s wall); Change14 97/97 (1.281s reported, 1.722s wall); focused task 9.2 104/104 (0.815s reported, 2.365s wall); the single owner-turn-8 npm canonical invocation 532/532 (26.645s reported, 30.111s wall); architecture 3/3 (0.637s reported, 1.105s wall); semantic equivalence 63 contracts, 49 methods/45 active, 43 scenarios, 42 tasks, four gates (0.088s wall). The 11 reopened tasks were closed only after these gates. Strict OpenSpec validation then passed (3.61s wall), and apply status reported `all_done`, 42/42 complete, zero remaining. No production/runtime/dependency/staging/commit/push/sync/archive/clean action occurred.

## Owner turn 9 — audit round 4 rejection and remediation turn 5

Production review approved; test review rejected four narrow issues. Tasks 2.2, 2.4, 5.1, 5.6, 9.1, 9.4, and 9.5 were reopened before remediation. Test/evidence scope remained exclusive.

1. **Consumed result schema:** schema 5 removes the nested `result.result` member from all 63 authority rows, `ExecutorResultContract`, runtime parsing, and external exact keys. No replacement field was added. Every remaining result member is explicitly consumed: returned rows compare every produced value and assert no propagated exception; non-returned rows assert all result values null and verify the propagated exception; the structural row explicitly consumes its null result metadata.
2. **Exact closure:** external `exactKeys` compares equality—not subset inclusion—for the authority root, provenance/audit objects, methods, every contract, binding/argument, call/effect/event/log/result/exception/disposition/forbidden/token/cleanup/edge/edge-point/fallback object, plus primitive-array types. Runtime camel-case deserialization is case-sensitive and globally uses `JsonUnmappedMemberHandling.Disallow` across typed authority records, including provenance. An embedded-resource test independently injects and rejects an unknown top-level key and unknown nested behavioral key. The lowercase blacklist remains secondary defense only.
3. **Proof separation:** the external script reads exactly four files: authority JSON, verification plan JSON, delta spec Markdown, and tasks Markdown. It reads no C# source, project file, source text, or DynamicData attribute text; method source-file metadata was removed from authority and mapping. External checks now prove artifact/spec/task/plan semantic equality only. Compiled reflection/IL tests exclusively resolve active declaring types, method identities/signatures, TestMethod attributes, exact DataRow/DynamicData bindings, direct executor construction, and forbidden transitive dependencies.
4. **Narrow S16:** the five structural authority/observation rows now contain only independently observed input Country/State/City/HasMatch, fallback output Country/State/City/HasMatch, and guard result. Constant disposition/effect fields were removed from JSON, C#, test construction, and script. The separate executable `country-fallback-update` row proves WriteAccepted, one write effect, Updated, and absence of skipped effects/events; structural S16 proves only fallback shape and guard unreachability. Together they cover S16/5.1/5.6 without self-certification.

Editing checks: the first strict runtime attempt correctly rejected previously untyped root provenance and was fixed by adding exact provenance/audit records. The first script run caught that `semantics` is a scalar enum rather than an array and was corrected. Adding embedded JSON reading exposed `TextReader.ReadToEnd` in the System.IO policy; the explicit embedded/in-memory reader allowlist was extended without weakening deny-by-default behavior. Current targeted results: Change11 28/28, manifest/schema 6/6, architecture 3/3, S16 structural 1/1, and semantic equivalence pass. Final stable owner-turn-9 gates passed after the last test/evidence-code edit: build 0 warnings/errors (1.88s reported, 2.053s wall); Change14 98/98 (1.246s reported, 1.617s wall); focused task 9.2 105/105 (0.831s reported, 2.105s wall); the single owner-turn-9 `npm run test` invocation 533/533 (30.863s reported, 33.756s wall); architecture 3/3 (0.624s reported, 1.057s wall); semantic equivalence passed (0.1s wall); strict OpenSpec passed (3.112s wall); status is `all_done`, 42/42, zero remaining. The seven reopened tasks were closed only after build through equivalence passed. Current semantic SHA is `f8e9440c32dfe8829f3cb4bb87217ae2ad6064a299a54ff5b882a807d0c130cc`; mapping digest is `f0bd9a270956659535e16c4e68bba9d6fdb1bcc4ed82c2ecb66d29388c3118bd`.

## Owner turn 10 — audit round 5 P0 and remediation turn 6

Audit round 5 rejected one P0 consumption gap. Tasks 2.4, 5.1, 5.6, 9.1, and 9.4 were reopened before remediation. Test/evidence scope remained exclusive.

- Every behavioral `Verify` invocation now asserts `FallbackShapes.Length == 0`; a behavioral contract cannot silently carry structural fallback rows.
- Structural verification now consumes the entire common contract surface: exact case/scenario/task/binding metadata, `Ordered` semantics, empty assets and effect identities, all seven forbidden booleans, exact six retries, empty expected tokens, exact cleanup defaults, empty causal edges and seam exceptions, no extras, empty calls/effects/events/logs/dispositions, every result default, and the five exact fallback observations. Any field mutation fails.
- Runtime mutation sentinels inject fallback rows into a real behavioral contract and independently mutate structural semantics, assets, effect identities, a forbidden boolean, retries, expected tokens, cleanup, causal edges, and seam exceptions. Every mutation must throw `AssertFailedException` through the same verification helpers used by real cases.
- External equivalence mirrors the behavioral/structural role checks and runs the same representative mutation matrix. The role is included in the inspectable case mapping as `behavioral` or `structural-fallback`, so the recomputed mapping digest changes coherently.
- Editing ledger: the first runtime sentinel used the nonexistent historical case ID `zero-count` and failed with `KeyNotFoundException`; it was corrected to the real `positive-immediate-empty` behavioral case. Targeted results then passed: manifest/consumption 7/7, retained Change11 28/28, architecture 3/3, and external semantic equivalence.

Schema is now 6. Semantic SHA is `6d807bb35e8e75f5bec71e129a570fe11d477ad31857c67066bf5cbdb7d37d8d`; role-aware mapping digest is `13487e8fb9ddc2fd4b05b18c2974b8edbef0c6597212ab0660c5efa3ad762f8c`. Final stable owner-turn-10 gates passed after the last test/evidence-code edit: build 0 warnings/errors (0.73s reported, 0.9s wall); Change14 99/99 (0.797s reported, 1.2s wall); focused task 9.2 106/106 (0.847s reported, 2.174s wall); the single owner-turn-10 `npm run test` invocation 534/534 (32.353s reported, 34.744s wall); architecture 3/3 (0.623s reported, 1.03s wall); semantic equivalence passed (0.128s wall); strict OpenSpec passed (0.678s wall); status is `all_done`, 42/42, zero remaining. The five reopened tasks were closed only after build through equivalence passed.

## Owner turn 11 — formal Brooks pass 1 Health80 remediation turn 1

Formal Brooks pass 1 rejected Health80 on two false-proof defects. Tasks 2.4, 8.4, 9.1, 9.4, and 9.5 were reopened before editing and are closed only against the repaired proof. Test/evidence scope remained exclusive.

1. **Fail-closed requirement partitions:** authority schema 7 binds every one of the exact 43 scenario keys and 42 task keys to either a consumed exact contract or one of seven typed structural/external proof bindings. Behavioral gaps S04, S15, S39, and S42 now consume dedicated exact contracts (`zero-eligibility`, `no-administrative-match`, `reporter-finish-rejection`, and `fixed-utc-terminal-order`). Truthful existing exact executions jointly bind S06/S07/S08/S14/S18/S19/S28/S29/S40. Only S01-S03 use typed proof bindings; the remaining typed proofs bind inventory and external gates. Runtime reflection/IL verifies every compiled proof target and typed semantic clause; external equivalence independently verifies every method/gate target and clause. Runtime and external negative mutations remove S01 and task 1.1 bindings and must fail. Final authority cardinality is 67 exact contracts, seven proof bindings, 50 methods/46 active, 43 scenarios, 42 tasks, and four gates; no scenario/task partition is empty.
2. **Root-isolated transitive IL:** `TransitiveIlWalker` starts from each selected root, adds only that root's `AsyncStateMachineAttribute.StateMachineType.MoveNext`, and recursively follows IL-referenced test-assembly helpers, lambdas, and local functions. The declaring-type scan that enqueued every compiler-generated nested `MoveNext` was removed. An excluded neighboring sentinel proves the valid async root reaches its required helper without the unbound root's marker/filesystem call, while the unbound root reaches its own marker/filesystem call without inheriting the valid neighbor. The pre-existing async-lambda sentinel continues to prove full forbidden delay, synchronous awaiter, File, FileInfo, FileStream, and Directory traversal.

Editing ledger: the first S39 exact row exposed the deliberately null attempted-finish timestamp and the nested retained exception type; both were corrected from actual observations. The first three additional exact rows exposed the `Skip` enum spelling, null terminal event timestamps, one duplicated final batch call, and the actual activity-end-before-progress ordering; each correction is now exact authority. Targeted proof is green: the four new exact behavioral roots pass, manifest/proof/mutation tests pass 9/9, architecture/root-isolation passes after cardinality correction, Change14 passes 103/103, and external equivalence passes. Schema-7 semantic SHA is `e2baab08f0f8b4191404f33d4cc5cbf6856ab3b584690a8cacc178ddb2e2fa37`; proof-aware mapping digest is `487ec142ea27cca83c329ef29360e5766b14d73ec8b12f03ca571dfc097ffb5e`.

Final stable owner-turn-11 sequence passed after the last test/authority edit: build 0 warnings/errors (0.66s reported, 0.84s wall); dedicated Change14 103/103 (0.65s wall); focused executor 110/110 (1.03s wall); the single owner-turn-11 `npm run test` invocation 538/538 (30.58s wall); architecture/root isolation 4/4 (0.63s wall); external equivalence 67 contracts, seven proof bindings, 50 methods/46 active, 43 scenarios, 42 tasks, and four gates (0.09s wall); strict OpenSpec passed (0.73s wall); status is `all_done`, 42/42, zero remaining. Branch/head remain `major-redesign` / `fd48bd43a717cebeb27e7888e4963d066b144365`; `src/` diff and staging are empty. No production, dependency, stage, commit, push, sync, archive, or clean action occurred.

## Owner turn 12 — post-Brooks remediation round 7 P0

One P0 rejected the prior terminal correlation. Tasks 2.4, 8.4, and 9.1 were reopened before editing. Test/evidence scope remains exclusive.

- **Symptom:** the attempted S39 `RunFinished` event carried `resultSame: true` even though `ExecuteAsync` returned no result.
- **Source:** `returnedResult is null || ReferenceEquals(...)` collapsed unknown/not-applicable into a successful identity match.
- **Consequence:** finish rejection falsely proved returned/reported result identity despite the required no-result outcome.
- **Remedy:** schema 8 preserves nullable `bool?` correlation and computes null when `returnedResult` is null, true only for exact `ReferenceEquals`, and false for a distinct returned instance. The S39 authority row now requires null while preserving all attempted terminal counts/outcome, exact nested sink exception identity, no accepted terminal, no returned result, and all retry/recursion prohibitions. A runtime direct tri-state assertion proves null/true/false, and external equivalence rejects a null-to-true mutation.

Schema-8 semantic SHA is `00acade23e7c341c08f89649cf628954aaeba01101877b548dcc81886d033a47`. The recomputed proof-partition mapping digest remains `487ec142ea27cca83c329ef29360e5766b14d73ec8b12f03ca571dfc097ffb5e` because no case/method/proof partition changed.

Targeted proof passed after the one compile correction adding the processing-event namespace: direct nullable/exact-reference correlation plus retained S39 passed 2/2; manifest/schema/mutation passed 10/10; external equivalence passed. Tasks 2.4, 8.4, and 9.1 were then closed. Final stable owner-turn-12 sequence passed after the last test/authority edit: build 0 warnings/errors (0.77s reported, 0.94s wall); dedicated Change14 104/104 (0.71s wall); focused executor 111/111 (1.04s wall); the single owner-turn-12 `npm run test` invocation 539/539 (27.15s wall); architecture 4/4 (0.62s wall); schema-8 equivalence passed (0.08s wall); strict OpenSpec passed (0.74s wall); status is `all_done`, 42/42, zero remaining. Branch/head remain `major-redesign` / `fd48bd43a717cebeb27e7888e4963d066b144365`; `src/` diff and staging are empty. No production, dependency, stage, commit, push, sync, archive, or clean action occurred.

## Final approvals and pre-commit performance

- Pre-code design gate required four proof-review rounds; final production and proof reviewers both approved before implementation.
- Final independent pre-Brooks auditing required eight rounds. Production and test reviewers both returned `APPROVE PRE-AUDIT ROUND 8` on the final surface.
- Formal Brooks pass 1 scored Health 80/100 and rejected empty enforceable scenario/task partitions plus cross-method IL reachability. Owner turn 11 introduced schema 7 with 67 exact contracts, seven typed proof bindings, nonempty fail-closed coverage for all 43 scenarios and 42 tasks, S39 exact proof, removal-mutation checks, and root-isolated IL traversal.
- Post-Brooks audit found one nullable-reference self-certification; owner turn 12 introduced schema 8 and exact nullable S39 `ResultSame`. Both independent reviewers then approved round 8.
- Formal Brooks pass 2 reviewed the complete tracked and untracked Change-14 surface, ran its quick checks, scored Health **100/100**, found zero actionable items, and issued **BROOKS APPROVED** at 2026-08-31T23:18:09Z.
- Final immutable authority: 67 exact contracts, seven typed proof bindings, 50 methods/46 active, 43/43 scenarios, 42/42 tasks, four gates; semantic SHA `00acade23e7c341c08f89649cf628954aaeba01101877b548dcc81886d033a47`; mapping digest `487ec142ea27cca83c329ef29360e5766b14d73ec8b12f03ca571dfc097ffb5e`.
- End-to-end wall time from start 2026-08-31T13:52:20Z through Brooks approval: **9h 25m 49s**. Same-owner turns: **12** (four pre-code proof turns, one initial implementation turn, six independent-audit remediation turns, one formal/post-Brooks remediation turn). Pre-code proof-review rounds: **4**. Final independent audit rounds: **8**. Formal Brooks passes: **2**.
- Canonical local invocations after stable owner edits: **8**, each about 27–37 seconds; final canonical result **539/539**. The target of 2–3 owner turns, two audit rounds, 3–4 canonical runs, and under four hours was not met.
- Parent observed **38** 600-second subagent wait ceilings plus one 600-second confirmation timeout (**6h 30m** of opaque ceiling windows, overlapping active agent work/user wait rather than pure idle time).
- No production/runtime source changed. Implementation commit, implementation CI, sync/archive, archive CI, and final performance comparison remain pending.
