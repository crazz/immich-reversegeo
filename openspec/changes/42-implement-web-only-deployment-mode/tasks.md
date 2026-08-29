## 1. Reconcile finalized prerequisites

- [ ] 1.1 Inventory the applied block 40 resolved-mode handoff and block 41 common-Web, scheduler, manual coordinator, child backend/launcher, event bridge, startup validator, and lifecycle-owner types and lifetimes; record the exact landed seams and stop rather than create parallel abstractions if they are absent.
- [ ] 1.2 Characterize the current scheduler registration, concrete/hosted identity, schedule-config reads, cron/retry/due waits, pending transitions, detector resolution, Dashboard manual injection, status messages, and host-stop ownership with focused tests before changing composition.
- [ ] 1.3 Inventory current Lookup, Data, GeoBoundaries, and reset registrations so block 42 preserves their Phase 6 behavior without claiming the worker-backed target owned by blocks 47–55.

## 2. Compose Web-only hosting

- [ ] 2.1 Make block 40's finalized Web-only value select the shared ASP.NET Core/Kestrel/Razor/Blazor registration, middleware, and endpoint path without rereading the environment, persisting mode, or changing Standard composition.
- [ ] 2.2 Omit the scheduler concrete service, hosted-service alias, schedule calculation/retry/due waits, scheduled pending callback, and scheduled-detector dispatch root from Web-only; do not substitute a no-op hosted service or runtime guard.
- [ ] 2.3 Retain exactly one finalized processing coordinator, child backend/launcher, child-event bridge, startup validator, and host-lifecycle owner with their established lifetimes, while keeping the authoritative executor and any in-process backend absent from Web.
- [ ] 2.4 Route Dashboard manual processing through the coordinator-facing manual contract without scheduler lifecycle or detector use; preserve local contention, pending marking, child-only execution, final failures, and worker-side advisory exclusion.

## 3. Preserve current Web features and configuration

- [ ] 3.1 Keep current Lookup, Data, GeoBoundaries, and reset routes and Phase 6 dependencies available in Web-only with no temporary feature gate, hide, or premature worker-job implementation; preserve processing-root isolation from those transitional services.
- [ ] 3.2 Keep `ScheduleEditorState` load/edit/validation/save behavior unchanged and add a concise Settings notice that saved schedule values remain visible but internal scheduling is disabled by Web-only mode.
- [ ] 3.3 Prove saving settings in Web-only does not persist deployment mode or mutate, clear, normalize, enable, or disable the saved schedule because of mode.
- [ ] 3.4 Preserve production port 8080, local-development port 5122, current middleware/endpoints, separate `/config` and `/data` roots, environment overrides, and independent volume semantics without migration.

## 4. Preserve startup, shutdown, and status behavior

- [ ] 4.1 Run the finalized same-image child-launch and shutdown-budget validation before Web-only accepts requests; invalid prerequisites fail actionably without launching work, resolving worker-heavy services, or falling back in-Web.
- [ ] 4.2 Bind host stopping to the existing lifecycle owner independently of scheduler activation so admission closes and any admitted/start-racing manual child is cancelled and joined through process, stream, disposal, and exact-ownership finality.
- [ ] 4.3 Preserve existing manual pending/progress/success/failure/cancellation status through the child-event bridge, assert no scheduler wait/next-run status is produced, and leave resolved-mode plus safe ProcessAssets lifecycle presentation to block 44; assign no PID/run/job identity, generic active-job card, or non-ProcessAssets page state there.

## 5. Document the operational boundary

- [ ] 5.1 Update the existing deployment-mode documentation with concise Web-only behavior: Web UI and manual processing remain available, saved schedules are retained but inactive, and a restart is required to change mode.
- [ ] 5.2 State that block 42 adds no HTTP/CLI/queue trigger and provides no external automation by itself; defer comprehensive trade-off guidance to block 70 and describe separately delivered external execution only after its owning change is available.
- [ ] 5.3 Document the Phase 6 transition accurately: Lookup and Data remain usable with current behavior now, while worker-backed heavy Lookup/cache operations and final Web geodata removal require the ordered blocks 47–55.

## 6. Add focused Web-only verification

- [ ] 6.1 Add service-descriptor/provider tests proving Web/Kestrel/UI, one coordinator/launcher path, validator, lifecycle owner, and current Lookup/Data dependencies are present, while scheduler concrete/hosted registrations, scheduled-only detector path, authoritative executor, and in-process backend are absent.
- [ ] 6.2 Add a hermetic saved-schedule test covering enabled valid cron plus disabled/invalid values and proving zero schedule waits, detector calls, scheduled pending transitions, automatic children, and persistence mutations.
- [ ] 6.3 Add a hermetic manual-path test proving direct coordinator admission launches one child without detector use, reports established busy/failure outcomes, and never falls back in-Web.
- [ ] 6.4 Add startup-failure and host-shutdown race tests proving invalid launch prerequisites prevent request acceptance and a start-racing/running manual child is joined by the existing lifecycle owner.
- [ ] 6.5 Add Settings rendering/save tests for the Web-only disabled-policy notice, editable retained values, and absence of broader block-44 status scope.
- [ ] 6.6 Run focused Web-only tests and `npm run test` with normal Integration/Performance exclusions; keep cross-mode matrix, Docker, fixed-port, live database/geodata, and real-worker evidence in blocks 45–46.
- [ ] 6.7 Run `openspec validate 42-implement-web-only-deployment-mode --strict`, inspect final status, and review the diff to confirm no block 43 files or implementation scope were touched.
