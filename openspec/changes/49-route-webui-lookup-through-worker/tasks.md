## 1. Reconcile prerequisites and contracts

- [ ] 1.1 Verify blocks 47 and 48 are applied; bind to the exact finalized `CoordinateLookup` fields (`Latitude`, `Longitude`, `IncludeAirportInfrastructure`, `IncludeLiveOverturePlaces`, `PreferGadmAdministrativeAreas`, and canonical typed default/country city-profile overrides), closed progress-step discriminator, transport source/result/attribution/license DTOs, session/cancellation/classifier behavior, and exclusive-heavy-geodata descriptor; stop rather than edit block 48 or create parallel contracts if applied APIs differ.
- [ ] 1.2 Record the exact transition dependency: block 49 uses a temporary shared-shaped admission implementation, block 50 replaces it with central arbitration, and block 55 must not remove heavy registrations until Lookup routing is verified.
- [ ] 1.3 Add contract assertions that one lower-case GUID-D `jobId` is preserved across admission, launch, events, cancellation, terminal/classification, cleanup, and release.

## 2. Page-independent lookup state and validation

- [ ] 2.1 Add a control-plane-only Lookup submission/state model with explicit idle, validating, admitting, starting, running, cancel-requested, completed, cancelled, busy, and failed phases plus derived control-enable flags.
- [ ] 2.2 Move or expose coordinate parsing/range validation through a testable seam; reject non-finite and out-of-range values before identity creation/admission while retaining the last completed result and responsive controls.
- [ ] 2.3 Snapshot `Latitude`, `Longitude`, `IncludeAirportInfrastructure`, `IncludeLiveOverturePlaces`, `PreferGadmAdministrativeAreas`, and the bounded canonical optional-default/ISO3-sorted country city-profile overrides immutably at valid submission; include no separate diagnostics bag or unrelated AppConfig/UI/database state.
- [ ] 2.4 Add state-transition tests for validation, re-entry/double click, result clearing only after admission, last-result labeling after validation/busy rejection, and control enable/disable behavior.

## 3. Admission and worker orchestration

- [ ] 3.1 Define a narrow admission/launch contract with admitted handle, structured busy metadata, and safe unavailable outcomes using block 47's resource/job metadata shapes.
- [ ] 3.2 Implement and register a temporary atomic process-local gate for Lookup launches only; prove no process starts on rejection and release occurs exactly once after startup failure, completion, cancellation, crash, protocol failure, and disposal.
- [ ] 3.3 Implement the lightweight Lookup controller/client that launches exactly one v2 `CoordinateLookup` session and depends on no Overture/GADM resolver or cache service, `ProcessingState`, or asset repository.
- [ ] 3.4 Map admission, readiness/start, the finalized closed country/Overture-cache/admin/GADM-cache/admin/airport/live-Places/final-selection steps without percentages, nested cache activity start/end, safe logs, authoritative typed terminal result, safe terminal error, and controller-classified no-terminal outcomes into bounded page state.
- [ ] 3.5 Enforce protocol version, job kind, `jobId`, operation-generation, and disposed-state correlation; add tests that mismatched and stale events cannot mutate UI state.

## 4. Cancellation, cleanup, and races

- [ ] 4.1 Wire one idempotent Cancel operation to the exact active session using the established cooperative grace/kill contract and keep controls locked through stream/session finality.
- [ ] 4.2 Implement async page/controller disposal that marks rendering stale first, joins the same stop task, awaits bounded session disposal, and releases admission once on navigation/circuit disposal.
- [ ] 4.3 Add deterministic tests for cancel-before-start, cancel-versus-completed-terminal, repeated cancel, forced kill/classified failure, disposal during startup/running/drain, late callbacks, and successful subsequent reuse.

## 5. Route Lookup.razor through the controller

- [ ] 5.1 Replace `Lookup.razor` direct calls to country, division, GADM, cache, airport, and live Places services with the lightweight controller and remove its heavy service injections, cache-ensure methods, page-private geodata result construction, and direct exception rendering.
- [ ] 5.2 Preserve the existing country/admin/GADM/airport/Places/final-output/trace markup by mapping block 48's explicit source states, readiness/country codes, release/version, best matches, bounded candidates/truncation, trace/profile summary, closed final attribution, and GADM dataset/version/license DTOs through a lightweight display model where necessary.
- [ ] 5.3 Add visible Checking availability, Starting, Running/current activity, Cancelling, Completed, Cancelled, Busy, Unavailable, and Failed copy; show only safe bounded errors and friendly busy job labels.
- [ ] 5.4 Disable paste, coordinate, option, and Lookup controls from admission through final cleanup; expose Cancel only for an admitted cancellable operation and prevent repeated cancellation.
- [ ] 5.5 Keep GADM labeled experimental and non-commercial at the option and result/status surfaces, and distinguish GADM source-unavailable text from the licensing notice.
- [ ] 5.6 Add any required shared styles to `src/ImmichReverseGeo.Web/wwwroot/app.css` only; do not add scoped Razor CSS.

## 6. Composition and boundary verification

- [ ] 6.1 Register the lightweight Lookup controller/client and internal worker launch path in Standard and Web-only compositions with equivalent behavior; verify run-once exposes no interactive Lookup surface.
- [ ] 6.2 Add composition/constructor-graph tests proving Lookup resolves without Web-side Overture/GADM resolvers or caches and has no in-process fallback when admission or worker launch fails.
- [ ] 6.3 Add negative tests proving every Lookup outcome emits no `ProcessingState` transition/activity/log/count and invokes no Immich asset/`asset_exif` write path.
- [ ] 6.4 Verify the worker-backed result remains a preview of what processing would write and preserves block 48 resolver/source precedence without duplicating its parity suite.

## 7. Fixtures, documentation, and transition to block 50

- [ ] 7.1 Extend block 48's checked-in/no-network real-worker fixture at the controller boundary for successful typed source/result/attribution/license rendering, ordered discrete progress, balanced cache activity, cancellation, safe completed source degradation, startup/protocol failure, correlation mismatch, and final stream drain without duplicating its protocol goldens or resolver parity cases.
- [ ] 7.2 Add focused markup/presenter assertions for lifecycle labels, control states, retained versus cleared result behavior, safe errors, friendly busy text, and GADM non-commercial/error copy using the existing test stack or the smallest isolated render seam.
- [ ] 7.3 Update `docs/website/using-the-app.md`, `docs/website/troubleshooting.md`, and `docs/website/data-sources.md` for worker startup/progress/cancel/busy behavior, Standard/Web-only parity, no asset writes, no in-process fallback, and the GADM license link/error distinction.
- [ ] 7.4 Run the block 48 worker/lookup parity fixtures and normal non-integration Web tests, then run strict OpenSpec validation for this change.
- [ ] 7.5 During block 50, replace and delete only the temporary gate implementation/registration, adapt the same admission contract to the shared coordinator, and rerun busy/release/crash/cancel/reuse tests without changing `Lookup.razor` behavior.
- [ ] 7.6 Before block 55, verify no Lookup page/controller dependency resolves a heavy geodata/cache service in either Standard or Web-only mode.
