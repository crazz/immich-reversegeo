## Context

See `proposal.md` for motivation and `specs/standard-deployment-mode/spec.md` for behavior. Block 40 owns deployment-mode parsing, normalization, the unset-to-Standard default, invalid-value diagnostics, and internal-worker role precedence. This change starts only after that contract is finalized and consumes its resolved value; it must not introduce a second parser or reinterpret raw `IMMICH_REVERSEGEO_MODE` input.

Applied Phase 3–5 work already separates Web and internal-worker composition, routes manual and eligible scheduled passes through one coordinator-owned child boundary, keeps the scheduled detector in Web before child resolution, removes production in-process execution, and defines child cancellation/shutdown. Exact landed names may differ from planning names, so apply must bind the Standard branch to those finalized contracts rather than recreating parallel services. Lookup and Data still require their current Web registrations in this phase and move later.

## Goals / Non-Goals

**Goals:**
- Make Standard the explicit production Web composition selected by block 40, including its unset default.
- Preserve Kestrel/Razor/Blazor hosting, internal scheduling, manual control, current Lookup/Data behavior, child-only heavy processing, and one process-local active run.
- Preserve startup validation, graceful host/child shutdown, port compatibility, and the separate config/data storage roots.
- Provide Standard-specific descriptor tests and a hermetic behavioral smoke without duplicating the later all-mode or Docker matrices.

**Non-Goals:**
- No deployment-mode parser, normalization, accepted-value, precedence, or raw-environment handling changes; block 40 owns them.
- No mode display, scheduler-policy message, worker PID/run-ID, or lifecycle UI; block 44 owns mode UI.
- No Web-only or external run-once host composition. Block 43 owns the external run-once entry path and its use of the established PostgreSQL advisory exclusion; this change neither bypasses nor redesigns the worker-side lock.
- No Lookup/Data worker migration or removal of their current heavy Web dependencies; Phase 7 owns that cutover.
- No all-mode composition matrix or production-image/Docker smoke harness; blocks 45 and 46 own those broader gates.
- No new endpoint, port, volume, persisted setting, protocol, retry, fallback, or second worker artifact.

## Decisions

### 1. Branch on the finalized resolved mode, not the environment

After private application-role selection has preserved internal-worker precedence, use block 40's finalized resolved deployment-mode value to choose the Standard composition. The Standard branch is also the branch reached by block 40's unset default. Do not read `IMMICH_REVERSEGEO_MODE` again, duplicate accepted values, or persist the mode in `AppConfig`.

Alternative: let the current unconditional Web branch stand for Standard and branch only for later special modes. Rejected because it leaves Standard implicit, makes the composition matrix harder to verify, and encourages later branches to duplicate registration.

### 2. Standard is one explicit Web control-plane composition

Build the existing ASP.NET Core `WebApplication` with Kestrel, Razor/Blazor services, static assets, interactive endpoints, middleware, configuration, processing state, and UI-facing services. Register the finalized internal scheduler, coordinator, scheduled detector, child backend/launcher, event bridge, cancellation/classification, and host-lifecycle owner with their established lifetimes and aliases. If the finalized scheduler seam is still `ProcessingBackgroundService`, preserve one concrete singleton exposed through the hosted-service alias; do not create a second scheduler instance.

Keep the production listener behavior unchanged: Docker remains on port 8080 through the existing production URL configuration, and local development remains on port 5122 through the existing launch configuration. Standard does not add a listener, endpoint, redirect, or port override of its own.

Alternative: create a new Standard-specific scheduler or coordinator facade. Rejected because the applied Phase 2–5 contracts already own admission, lifecycle, and scheduling, and parallel control paths would permit duplicate work.

### 3. Manual and scheduled processing preserve different pre-dispatch behavior

Manual processing enters the finalized coordinator directly and resolves exactly one child-processing backend after admission. It does not run the scheduled detector; a manual no-work decision remains authoritative inside the child.

A due scheduled occurrence first wins the same local admission, marks the matching request pending, and invokes the finalized lightweight database detector. A false result finalizes locally without resolving a backend, launcher, worker protocol, executor, or geodata graph. A true result resolves and dispatches exactly one child backend. Detector cancellation/failure retains its finalized local pre-dispatch outcome. The child still performs the authoritative count; detector results are never sent as work sets or processing truth.

Alternative: launch the child for every scheduled tick. Rejected because it discards the established empty-schedule gate. Alternative: apply the detector to manual runs. Rejected because it changes manual semantics and creates a second eligibility authority.

### 4. Heavy processing remains outside Web and locally single-owner

The Standard Web graph contains no authoritative processing executor registration or callable in-process fallback. The coordinator admits at most one local manual or scheduled run, lazily creates one run scope only after the correct pre-dispatch boundary, and supervises at most one heavy child worker at a time. Rejected or detector-empty requests resolve no child backend. Child startup, handshake, protocol, crash, cancellation, or cleanup failure is final for that request: publish the established visible outcome and never execute in Web, retry, replay, or launch a replacement.

The child command targets the private internal-worker role shipped in the same application assembly and image. Internal-worker precedence and composition remain owned by the role parser/composition work; Standard does not expose that role as a public mode or require a second executable/image. The worker-side PostgreSQL advisory lock remains the cross-process authority established in Phase 5. Standard adds only the existing local single-run admission; external run-once orchestration arrives in block 43 and must consume, not redefine, that lock.

Alternative: use Standard mode as permission for an in-Web fast path. Rejected because block 38 removed that production route and block 39 protects the no-geodata processing boundary.

### 5. Lookup, Data, storage, and startup stay backward-compatible

Retain all current-phase Lookup and Data route/component registrations and the heavy services they still consume, while keeping them unreachable from Web processing roots. Do not claim whole-Web geodata isolation until Phase 7.

Continue deriving settings from the existing config root and mutable/downloadable geodata and caches from the existing data root. Production defaults remain `/config` and `/data`, including independent volume mounts and existing environment overrides; secrets remain environment-backed. No settings migration or directory initialization is added to mode selection.

Run the finalized child-launch prerequisite and host shutdown-budget validation before the Web host starts accepting requests. Invalid or unavailable private worker launch composition fails startup with the established actionable diagnostic and no in-process fallback. Invalid raw mode input remains block 40's earlier failure. Registration/provider construction must not launch a worker, materialize geodata, or bind a second listener.

Alternative: tolerate missing child prerequisites until the first run. Rejected because it would present a healthy UI whose only production processing path cannot start.

### 6. Reuse shutdown ownership and test at two levels

Standard host stopping closes coordinator admission atomically, joins any admitted/start-racing child, reuses the finalized bounded cooperative-cancel/forced-tree-termination policy, drains stdout/stderr, disposes the exact session, and releases matching ownership before reporting clean shutdown. Do not create a Standard-specific cancellation deadline or detach a live child when the host stop token expires.

Add focused composition tests over service descriptors/providers and startup seams. Assert the unset-resolved Standard graph has Web/Kestrel/UI services, one scheduler instance and hosted alias, one coordinator, the scheduled detector, one child backend/launcher path, host-lifecycle cleanup, and current Lookup/Data dependencies; assert the Web graph cannot resolve the authoritative executor or an in-process backend. Assert worker composition remains the private same-assembly role with execution dependencies and no Web host.

Add one hermetic Standard behavioral smoke using the finalized host/composition seam with fake clock, detector, child boundary, and lifecycle hooks. It covers: default Web startup and endpoint mapping without binding fixed ports; manual admission dispatching one child without detector use; scheduled false dispatching none; scheduled true dispatching one; local contention never producing two heavy children; child-prerequisite startup failure preventing host start; and shutdown joining/cancelling the owned child. Keep live PostgreSQL, geodata, downloads, Docker, fixed-port listeners, and a real heavy worker out of this smoke. Blocks 45–46 later broaden the matrix and production-image evidence.

## Risks / Trade-offs

- [Standard accidentally re-parses mode while block 40 is landing concurrently] → Consume only the finalized resolved-mode API and re-read block 40 before apply; do not copy parser rules or identifiers into production.
- [Hosted scheduler is registered twice] → Assert concrete/hosted alias identity and one scheduled trigger source.
- [Scheduled child resolves before detector success] → Use fail-on-resolution fakes and verify false/cancel/failure paths create no child scope.
- [Retained Lookup/Data services are mistaken for an allowed Web processing path] → Pair descriptor retention assertions with block-39 processing-root absence/activation guards.
- [Startup validation eagerly constructs worker geodata] → Validate launch artifacts and role reachability without resolving the executor or heavy worker graph.
- [A host shutdown race strands a child] → Reuse the exact Phase 4 lifecycle owner and assert admission/start/shutdown race cleanup in the smoke.
- [Standard tests absorb later blocks] → Keep this change Standard-only and hermetic; leave mode UI, all-mode matrix, fixed production ports in Docker, and image/volume execution to blocks 44–46.

## Migration Plan

1. Re-read the applied block-40 resolved-mode API and Phase 3–5 composition contracts; stop if prerequisites are not finalized rather than creating duplicate abstractions.
2. Make the finalized Standard value select one explicit Web/control-plane registration and endpoint path.
3. Preserve current Lookup/Data registrations, existing port/path configuration, child prerequisite validation, and lifecycle ownership.
4. Add Standard-specific composition tests and the hermetic behavioral smoke.
5. Run focused tests, the normal test suite with repository exclusions, strict OpenSpec validation, final status, and a block-41-only scope review.

Existing deployments require no configuration or data migration: omission still resolves to Standard. Rollback deploys the previous application version; do not add a runtime in-process fallback.
