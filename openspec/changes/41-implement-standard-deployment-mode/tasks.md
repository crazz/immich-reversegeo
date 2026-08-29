## 1. Reconcile finalized prerequisites

- [ ] 1.1 Re-read the applied block-40 resolved-mode API and the finalized role/composition contracts from blocks 18–20; consume their landed names and preserve internal-worker precedence without adding another parser or raw environment read.
- [ ] 1.2 Inventory the finalized scheduler, coordinator, scheduled detector, child backend/launcher, event bridge, startup validator, and host-lifecycle contracts from Phases 2–5, and map each to one Standard registration or alias without creating parallel abstractions.
- [ ] 1.3 Inventory the current Lookup and Data route/component dependencies that must remain in Web for this phase and identify the authoritative executor/in-process services that must remain absent from production Web processing.

## 2. Compose Standard Web hosting

- [ ] 2.1 Make block 40's resolved `standard` value, including its unset default, select one explicit ASP.NET Core/Kestrel Web composition with existing Razor/Blazor services, middleware, static assets, and endpoint mappings.
- [ ] 2.2 Register the finalized internal scheduler as one singleton/hosted instance, plus the singleton coordinator, scheduled detector, child-processing scope/launcher path, and host-lifecycle cleanup owner with their established lifetimes and alias identities.
- [ ] 2.3 Preserve the existing production port-8080 and local-development port-5122 configuration, and preserve separate config/data roots, production `/config` and `/data` defaults, environment overrides, secret handling, and independent volume behavior.
- [ ] 2.4 Retain all current-phase Lookup and Data registrations needed by their existing routes without making the authoritative executor or retained processing geodata dependencies reachable from scheduler/coordinator processing roots.

## 3. Preserve child-only processing policy

- [ ] 3.1 Route an admitted manual request through exactly one finalized child backend without running the scheduled-only detector or exposing launcher/backend details to Razor components.
- [ ] 3.2 Keep scheduled admission ordered through the finalized lightweight detector: false/cancel/failure finalizes locally without child resolution, while true resolves and dispatches exactly one child backend.
- [ ] 3.3 Enforce one process-local active manual-or-scheduled run, no second heavy child under contention, no authoritative in-Web executor/backend, and no fallback, replay, replacement, or automatic retry after child failure.
- [ ] 3.4 Build child commands for the private internal-worker role in the same assembly/image, preserving the finalized worker-side PostgreSQL advisory-lock behavior without adding the later external run-once host.

## 4. Validate startup and shutdown

- [ ] 4.1 Run finalized child-launch and host-shutdown-budget validation before Standard accepts requests; fail startup actionably when prerequisites are invalid without launching work, materializing the worker heavy graph, or falling back in-process.
- [ ] 4.2 Wire Standard host stopping to the finalized lifecycle owner so admission closes atomically and an admitted, start-racing, or running child is cancelled/joined through process exit, stdout/stderr finality, disposal, and exact coordinator cleanup.
- [ ] 4.3 Verify idle shutdown starts no worker and active-child cleanup cannot report success while an owned process remains alive.

## 5. Add Standard-specific verification

- [ ] 5.1 Add descriptor/provider composition tests proving the unset-resolved and explicit Standard paths contain Web/Kestrel/UI, one scheduler singleton/hosted alias, one coordinator, the detector, one child dispatch path, lifecycle ownership, and current Lookup/Data dependencies.
- [ ] 5.2 Add negative composition assertions that Standard Web cannot resolve the authoritative executor or an in-process backend, and that private same-assembly worker composition contains execution dependencies but no Web host.
- [ ] 5.3 Add a hermetic behavioral smoke with fake time, detector, child boundary, and lifecycle hooks covering default startup/endpoint mapping, manual dispatch without detection, scheduled false/true gating, local contention, prerequisite startup failure, and active-child shutdown.
- [ ] 5.4 Keep the smoke free of live PostgreSQL, geodata, downloads, Docker, fixed-port listeners, and real heavy processing; leave mode UI, all-mode matrix, and production-image/volume smoke coverage to blocks 44–46.
- [ ] 5.5 Run focused Standard composition/smoke tests and `npm run test` with the repository's default Integration/Performance exclusions.
- [ ] 5.6 Run `openspec validate 41-implement-standard-deployment-mode --strict`, inspect final `openspec status --change 41-implement-standard-deployment-mode`, and perform a block-41-only scope review that confirms block 40 and implementation code were not edited during planning.
