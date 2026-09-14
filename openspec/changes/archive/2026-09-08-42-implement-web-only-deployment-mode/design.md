## Context

See `proposal.md` and `specs/web-only-deployment-mode/spec.md`. Block 40 finalizes a strict, immutable startup-only deployment-mode value: exact `web-only` comes only from `IMMICH_REVERSEGEO_MODE`, missing defaults to Standard, invalid input fails before host side effects, and the private internal-worker role has precedence. Block 41 defines Standard as the compatible Web control plane and establishes the common Kestrel/UI, coordinator, child launcher, scheduled detector, startup-validation, and lifecycle contracts.

Block 42 is a composition subtraction from that finalized Web baseline, not a second host architecture. It must remove the automatic trigger root while retaining manual child execution. Lookup and Data remain transitional in Phase 6: blocks 47–55, not this change, generalize worker jobs, add shared arbitration, route pages, split lightweight inventory/maintenance, and finally remove heavy Web registrations.

## Goals / Non-Goals

**Goals:**
- Select one explicit Web-only composition from the finalized resolved mode.
- Keep Kestrel/Razor/Blazor, manual child processing, startup validation, truthful existing processing status, and owned-child shutdown.
- Make internal scheduling structurally impossible in this composition, independent of mutable saved settings.
- Preserve network, storage, Lookup, and Data compatibility while making the Phase 7 boundary explicit.
- Add focused block-42 evidence without absorbing later cross-mode or image matrices.

**Non-Goals:**
- No deployment-mode parsing, aliases, persistence, reload, UI selection, or public input beyond the existing environment variable.
- No run-once implementation or edits to block 43.
- No resolved-mode or safe ProcessAssets lifecycle UI owned by block 44, and no PID, run/job identity, generic active-job card, or non-ProcessAssets page state.
- No all-mode matrix or Docker/image/volume smoke owned by blocks 45–46.
- No Lookup/Data worker-job protocol, arbitration, page cutover, lightweight inventory, maintenance redesign, or heavy-registration removal before blocks 47–55.
- No new HTTP trigger, CLI trigger, durable queue, retry, replay, fallback, endpoint, port, or volume.

## Decisions

### 1. Branch on the finalized mode and share the Standard Web control plane

Consume block 40's immutable resolved mode after private-role precedence. Build Web-only through the same common Web/Kestrel/UI registration and endpoint path as Standard, then select the scheduler policy explicitly. Do not reread the environment or allow the absence/default rule to leak into composition.

Alternative: clone `Program.cs` into a Web-only host. Rejected because duplicate middleware, endpoints, storage setup, and lifecycle wiring would drift. Alternative: treat every Web role as Standard and inspect mode from the scheduler. Rejected because it makes the forbidden component part of the graph and weakens composition tests.

### 2. Omit the entire automatic-trigger root

Do not register the finalized scheduler concrete service or hosted-service alias in Web-only. No replacement hosted service is added. Consequently no schedule snapshot is loaded for triggering, no cron calculation occurs, no one-minute/five-minute/due wait is created, no pending state is marked for a scheduled occurrence, and no scheduled detector or scheduled dispatch path is resolved. If the landed composition has a dedicated scheduled-detector registration used nowhere else, omit it too; if a shared repository contract has non-scheduled consumers, retain only that shared contract and prove no scheduled path can reach it.

A runtime `if (WebOnly) return` inside the hosted scheduler is rejected: it still activates scheduler lifecycle and permits future waits or side effects before the guard. A no-op hosted service is rejected for the same reason. Saved `Schedule.Enabled` and cron values are presentation/configuration data in this mode, not an input to trigger composition.

### 3. Manual processing enters the coordinator directly

Dashboard/manual control uses the finalized process-local coordinator and child backend/launcher from block 41 without routing through scheduler lifecycle. Preserve admission, pending marking, child-event bridging, local contention, worker advisory exclusion, failure classification, and cleanup. The Web graph has no authoritative executor and no in-process fallback.

If the landed scheduler type still combines manual API and hosted scheduling, extract or use its finalized coordinator-facing manual contract rather than registering the combined type without its hosted alias. Do not create a Web-only coordinator, launcher, or status model. This is the key apply-time inventory: exact Phase 4–5 names must be reused.

Alternative: keep the scheduler concrete singleton only for Dashboard injection but omit its hosted alias. Rejected unless the finalized class has already become a scheduler-free manual facade; otherwise construction still couples manual operation to scheduling state and makes the graph ambiguous.

### 4. Preserve saved schedule editing and add only the block-42 notice

Keep `ScheduleEditorState`, existing validation, load, and save behavior unchanged. Inject/read only the immutable resolved mode through the existing startup/composition seam needed for rendering, and show a concise notice near schedule controls: saved settings are retained but internal scheduling is disabled while running Web-only. Saving other settings must neither insert mode into `AppConfig` nor rewrite schedule values because of mode.

This notice is policy feedback required for safe operation, not block 44's broader status feature. Block 44 remains responsible for resolved-mode display elsewhere and safe ProcessAssets lifecycle presentation only; PID/run/job identity is never assigned to UI, and generalized arbitration or non-ProcessAssets page state remains later-owned.

Alternative: disable or hide the schedule editor. Rejected because operators may prepare settings before returning to Standard, and it would make a startup-only topology choice mutate the settings workflow.

### 5. Keep Lookup/Data available now; inherit worker-backed target later

Block 42 introduces no feature gate. Preserve the same current-phase Lookup, Data, GeoBoundaries, and reset registrations used by Standard, including transitional heavy Web dependencies. Pair that retention with the existing processing-root isolation tests so those services cannot become a manual-processing fallback. Explicitly avoid claims that all Web work is child-backed or that Web-only is geodata-free.

The target state is inherited, not implemented here:
- Blocks 47, 48, and 50 establish generalized jobs, coordinate Lookup handling, and shared admission.
- Block 49 routes Lookup through the job client.
- Block 51 routes cache download/export/refresh mutations through workers.
- Blocks 52–54 finalize coordinated deletion, lightweight inventory, and database-maintenance placement; those operations are not assumed to require children when their owning plans intentionally keep them lightweight or serialized in Web.
- Block 55 makes both Standard and Web-only consume only approved control-plane/job/inventory dependencies and removes heavy registrations.

No temporary feature flag is warranted: hiding current features breaks block 42's compatibility goal, while prematurely routing them would duplicate Phase 7 protocol and arbitration decisions. Later common-Web composition changes must flow to both Web modes without reintroducing Web-only scheduling.

### 6. Reuse startup validation, shutdown ownership, and existing status bridge

Manual processing makes child-launch availability a startup prerequisite even without a scheduler. Run the same same-image launch-path and host-shutdown-budget validation as Standard before requests are accepted. Provider construction and validation must not launch a worker or resolve the heavy worker graph.

Register the same lifecycle owner once. Host stopping closes coordinator admission atomically and joins any admitted/start-racing child through the finalized cooperative cancellation, forced process-tree termination, stream drain, disposal, and exact-handle release. Scheduler absence means there is no scheduled wait to cancel or scheduler stop message to publish.

Existing manual pending/progress/terminal status continues through the block 41 event bridge. The settings notice is the only mode-specific status addition here; resolved mode and safe ProcessAssets lifecycle presentation remain block 44, while non-ProcessAssets pages retain their own state.

### 7. Treat external scheduling as an ownership boundary, not an API

Web-only is useful when another system owns cadence because it guarantees this Web process cannot race that cadence with an internal timer. It does not itself provide external execution. Before the separately owned run-once composition is delivered, operators have UI-manual processing only. After that composition exists, cron/Compose/Kubernetes-style schedulers can invoke it independently while this host remains Web-only, subject to the finalized worker-side exclusion contract.

Do not add an HTTP endpoint, CLI alias, command handler, queue consumer, or persisted setting in block 42. Do not prescribe or edit block 43 behavior; reference it only as a dependency for the eventual automated use case.

### 8. Verify composition absence and behavior without duplicating later matrices

Add descriptor/provider tests for explicit Web-only: Web/Kestrel/UI and common control services are present; scheduler concrete/hosted registrations and scheduled-only detector path are absent; coordinator/launcher, startup validator, lifecycle owner, and current Lookup/Data dependencies are present; authoritative executor and in-process backend are absent. Verify mode snapshot identity rather than raw-environment parsing.

Add one hermetic Web-only behavioral smoke using fake schedule/config, clock/wait, detector, child boundary, and lifecycle hooks. An enabled valid cron must create zero waits, detector calls, scheduled pending transitions, or children. Manual admission must create exactly one child without detector use. Cover prerequisite failure and shutdown during a start-racing/running manual child. Assert schedule save round-trips unchanged. Keep live database, geodata, downloads, Docker, fixed ports, and a real worker out of scope. Blocks 45–46 broaden the evidence later.

## Risks / Trade-offs

- [A manual API is still owned by the scheduler class] → Inventory the landed block 41 seams first and route UI through the finalized coordinator-facing contract; do not retain a combined scheduler merely for injection convenience.
- [A scheduler survives as an unhosted singleton] → Assert both concrete and hosted registrations are absent and fail on scheduler construction in behavioral tests.
- [Saved enabled settings are mistaken for active automation] → Keep the editor, preserve its values, and render the explicit Web-only policy notice.
- [Detector or timer work occurs indirectly at startup] → Use fail-on-resolution detector and wait fakes and assert zero scheduled pending/status transitions.
- [Lookup/Data are incorrectly advertised as worker-backed] → State the transitional behavior in spec, tests, and docs and name blocks 47–55 as prerequisites.
- [Phase 7 creates Web-mode drift] → Centralize common feature registrations and require block 55 coverage for both Standard and Web-only.
- [External scheduler guidance implies a trigger API] → Document that block 42 only disables internal cadence and that automated execution depends on separately delivered composition.
- [Shutdown omits child cleanup because no scheduler is hosted] → Bind lifecycle ownership to the coordinator/launcher, not scheduler activation, and test a start/shutdown race.

## Migration Plan

1. Re-read the applied block 40/41 APIs and inventory exact common-Web, scheduler, manual coordinator, child launcher, validator, status bridge, and lifecycle registrations. Stop rather than invent parallel abstractions if those prerequisites are not landed.
2. Extract or use one common Web composition path and make the finalized Web-only value omit the complete scheduler trigger root.
3. Route manual UI control directly through the existing coordinator-facing contract and retain startup/shutdown ownership.
4. Preserve current Lookup/Data registrations and settings persistence; add the Web-only schedule-policy notice and concise documentation.
5. Add focused composition and behavioral tests, run affected tests and the normal suite, then run strict OpenSpec validation and a block-42-only scope review.

Operators select the mode with exact `IMMICH_REVERSEGEO_MODE=web-only` and restart. Removing the variable or setting exact `standard` restores Standard selection and its internal scheduler; saved schedules need no reconstruction. Rollback uses the previous image and requires no settings, database, cache, port, or volume migration.
