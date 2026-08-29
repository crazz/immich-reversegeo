## 1. Reconcile block 47 contracts and define transport

- [ ] 1.1 Re-read the applied block 47 v2 kind, descriptor, registry, codec, event, terminal, classifier, and fixture APIs; use the exact `CoordinateLookup` name and stop if their semantics differ from the finalized artifacts.
- [ ] 1.2 Define immutable bounded CoordinateLookup request/options DTOs for finite ranged coordinates, the three current Lookup source controls, and canonical typed city-resolver overrides without unrelated AppConfig/UI fields or untyped payload carriers.
- [ ] 1.3 Add semantic validation for inclusive latitude/longitude ranges, finite values, profile ISO3/subtype/tie-break uniqueness and bounds, and validation-before-registry/heavy-service behavior.
- [ ] 1.4 Define immutable result DTOs for country, caches, each source's best match/candidates/status/error/release/version, trace/profile summary, final values, closed source attribution, deterministic truncation facts, and GADM attribution/license visibility.

## 2. Extract and implement CoordinateLookup behavior

- [ ] 2.1 Extract a UI-independent lookup operation and narrow deterministic seams from the current Lookup flow without changing or routing `Lookup.razor` in this block.
- [ ] 2.2 Preserve bundled-country short circuit, Overture cache/admin lookup, current prefer-GADM territory-family cache/query behavior, city-profile selection, and field-by-field GADM-over-Overture fallback.
- [ ] 2.3 Preserve optional bundled-airport and live-Places work, including airport geometry override/admin/fallback ordering and Places diagnostic-only behavior aligned with processing.
- [ ] 2.4 Map all attempted, disabled, skipped, no-match, unavailable, and failed source outcomes into bounded transport diagnostics with stable ordering and safe messages.
- [ ] 2.5 Prove the handler resolves no Immich asset/exif/skipped repository, performs no asset/database/schema writes, and permits only existing geodata cache-file side effects.

## 3. Worker lifecycle, caches, and composition

- [ ] 3.1 Add closed CoordinateLookup progress steps plus common safe logs and uniquely correlated cache download/wait activities with balanced cleanup.
- [ ] 3.2 Propagate cooperative cancellation through every token-aware operation, prevent new work after cancellation, structurally observe started optional tasks, and preserve active-token versus foreign/critical failure classification.
- [ ] 3.3 Preserve in-worker cache sharing, source validation, temporary cleanup, and atomic publication; test that pre-publication cancellation leaves no partial cache and post-publication cancellation retains the valid cache.
- [ ] 3.4 Implement/register the typed CoordinateLookup handler and descriptor as cancellable, heavy, geodata-bearing, and exclusive-heavy-geodata; advertise it only after successful registry validation.
- [ ] 3.5 Keep the processing-only PostgreSQL advisory lock out of CoordinateLookup, reserve exit 3, and add no local admission/queue/busy policy or nested worker launch.

## 4. Deterministic parity and protocol verification

- [ ] 4.1 Add request/result validator and codec tests with canonical NDJSON goldens for profile ordering, all progress/source states, attribution/license metadata, bounds/truncation, malformed coordinates, duplicates, and kind/payload mismatches.
- [ ] 4.2 Re-run every v1 and existing v2 ProcessAssets golden byte-for-byte and verify ready advertises CoordinateLookup only when its handler is registered.
- [ ] 4.3 Add operation parity tests using BundledCountryResolution, OvertureDivisionsLogic, OverturePlacesLogic, GadmDivisionsLogic, and AdministrativeAreaResolverTerritory fixtures plus combined admin/airport/Places/fallback cases.
- [ ] 4.4 Add deterministic degradation/cancellation tests for source failure retention, GADM unavailable/no-match/license metadata, balanced activities, no completed partial result, structured task unwind, and cache publication/cleanup side effects.
- [ ] 4.5 Extend the real worker-process fixture with checked-in/no-network CoordinateLookup success, no-country, disabled sources, degraded source, cancellation, domain/startup/output failure, terminal uniqueness, stream finality, and managed exit 0/2/4/5/6/130 cases; assert no invented exit 3.
- [ ] 4.6 Run focused Web/Overture/GADM/protocol/worker-fixture tests, `npm run test`, `npm run test:integration` for the explicit fixture category, and `openspec validate 48-add-coordinate-lookup-worker-job --strict`; confirm block 49 and all non-48 MASTERPLAN content remain unchanged.
