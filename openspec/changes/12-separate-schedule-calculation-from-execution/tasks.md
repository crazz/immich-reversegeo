## 1. Verify prerequisites and freeze boundaries

- [ ] 1.1 Verify blocks 1–11 are applied, re-read the final ProcessingRunExecutor/ProcessingBackgroundService APIs, and confirm the executor still owns the exact count, eligibility publication, zero gate, config/skipped snapshots, batches, geodata, persistence, and terminal result.
- [ ] 1.2 Characterize the applied host's exact startup initialization/log ordering, one-minute disabled wait, five-minute invalid/no-next wait, UTC Cronos Standard behavior, positive-delay next-run line, scheduled contention line, accepted-run awaiting, manual surface, and singleton/hosted identity before refactoring.
- [ ] 1.3 Record a dependency guard: schedule calculation/loop code may depend on schedule snapshots, TimeProvider, visibility, and the scheduled-trigger boundary only—not repositories, eligibility/count, skipped IDs, resolver/geodata, executor internals, ProcessingState admission inspection, or block-13 coordinator types.

## 2. Add deterministic schedule calculation and waiting

- [ ] 2.1 Add immutable DisabledRetry, InvalidRetry, and Due schedule-plan results plus a pure calculator accepting Enabled, cron text, and explicit zero-offset UTC now.
- [ ] 2.2 Preserve Cronos CronFormat.Standard, TimeZoneInfo.Utc, strictly-future next occurrence, invalid/no-next classification, and exact one-minute/five-minute retry durations without using ScheduleEditorState as a runtime parser.
- [ ] 2.3 Inject TimeProvider.System in production and use it for both GetUtcNow and cancellable delays; derive a positive relative due delay, emit once without delay if the clock already passed due, and never use real sleeps in tests.

## 3. Introduce the scheduled-trigger boundary without coordinator ownership

- [ ] 3.1 Define the minimal asynchronous scheduler-facing contract for a Scheduled-origin trigger with RejectedAlreadyRunning versus AcceptedAfterTerminal completion semantics and hosted-token propagation.
- [ ] 3.2 Implement a temporary adapter over block 11's existing control path; move no lock/request/reporter/MarkPending/CTS/executor/release policy and add no new admission arbitration. Do not create a DI cycle: resolve the hosted service and boundary in a composition test and prove no adapter-to-host back-edge exists.
- [ ] 3.3 Preserve rejection as zero executor calls plus the exact scheduled-contention UI line, and preserve acceptance as immediate pending, one executor call, terminal release, and no schedule reevaluation before terminal.
- [ ] 3.4 Leave the exact concrete coordinator, shared manual/scheduled ownership, Dashboard migration, trigger metadata creation, cancellation consolidation, and public coordinator API to block 13.

## 4. Reduce the hosted schedule loop

- [ ] 4.1 Retain skipped-storage initialization and its ILogger timing before the exact service-started UI line and first config read, using a narrow startup adapter only if needed to remove a direct repository dependency without changing order or identity.
- [ ] 4.2 Read one fresh ConfigService schedule snapshot per iteration, calculate one plan, append the exact UTC next-run line only before a positive due wait, wait with TimeProvider/stoppingToken, and emit one Scheduled trigger when due.
- [ ] 4.3 Preserve existing config-change timing: saves do not interrupt active disabled, invalid, due, or accepted-run waits; new values apply at the next iteration after wait/trigger handling.
- [ ] 4.4 Preserve shutdown cancellation through retry/due waits and accepted scheduled execution, with no post-cancellation trigger or ordinary error; preserve current manual TriggerRunAsync/CancelRun behavior and manual CTS scope.
- [ ] 4.5 Remove all eligibility/work checks from scheduling. Always pass an accepted trigger to the executor so its exact count and empty-pass lifecycle remain authoritative; do not add Any/count/skipped/geodata preflight.
- [ ] 4.6 Preserve ProcessingBackgroundService's concrete-singleton/hosted alias and Dashboard injection compatibility until block 13.

## 5. Verify schedule semantics with fake time and gates

- [ ] 5.1 Unit-test the pure calculator at fixed UTC instants for hourly/daily/weekly/custom valid expressions, strict exclusion of the current matching instant, standard five-field rejection, invalid/no-next classification, zero offset, and independence from host local time/DST.
- [ ] 5.2 Use FakeTimeProvider or an equivalent controllable TimeProvider to prove exact one-minute disabled and five-minute invalid waits, no trigger/log for either, and fresh config reads only after fake-time advancement.
- [ ] 5.3 Prove one positive future occurrence logs exactly `Next run scheduled at {due:u}` before waiting and emits exactly one Scheduled trigger at fake due time; prove an already-passed due emits once without delay/log and shutdown-before-due emits none.
- [ ] 5.4 Gate the trigger adapter to prove accepted execution blocks schedule reevaluation until terminal, rejection emits only the exact contention line and invokes no executor, and shutdown cancellation reaches an accepted scheduled executor path.
- [ ] 5.5 Change fake config during disabled, invalid, and valid due waits and prove the current plan remains pinned until its existing reevaluation point; retain ScheduleEditorState/Settings tests unchanged.
- [ ] 5.6 Gate startup initialization to prove no config read, next-run line, or trigger occurs before initialization and the service-started line; prove startup creates no immediate run and hosted StopAsync/ExecuteAsync terminate cooperatively.
- [ ] 5.7 Verify an accepted zero-work trigger still invokes the block-11 executor and publishes its authoritative zero eligibility/terminal lifecycle, while schedule collaborators expose no count, eligibility, skipped, batch, geodata, or persistence calls.
- [ ] 5.8 Retain Phase 1 manual pending/cancellation/exclusion and block-11 host/executor tests, including concrete/hosted singleton identity and Run Now compatibility, using TaskCompletionSource gates rather than timing sleeps.

## 6. Validate compatibility, sequencing, and scope

- [ ] 6.1 Run focused schedule calculator/loop, ProcessingBackgroundService, ConfigService, ScheduleEditorState, lifecycle, adapter, and executor tests with the repository's Microsoft.Testing.Platform command form.
- [ ] 6.2 Run npm run test with default Integration/Performance exclusions; run integration tests only if an integration-covered path actually changes.
- [ ] 6.3 Run openspec validate 12-separate-schedule-calculation-from-execution --strict and require success, then run openspec status --change 12-separate-schedule-calculation-from-execution and require all artifacts complete.
- [ ] 6.4 Review the diff to prove no edits to blocks 1–11 or 13+, no source implementation during planning, and no schedule editor/config format/UI, eligibility/count/geodata, coordinator, protocol/process, database/query, or processing behavior changes entered block 12.
- [ ] 6.5 Document the temporary adapter as the block-13 replacement point and require block 13 to preserve Scheduled trigger, accepted-after-terminal, contention, clock, wait, config-reevaluation, next-run visibility, and hosted-lifecycle semantics.
