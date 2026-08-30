# Changes During Implementation

## 2026-08-29 — Change 06: geodata cancellation blocker

### Evidence
- The original task 5.3 filters used only `FullyQualifiedName~Overture` and `FullyQualifiedName~Gadm`, which selected Integration and Performance tests in addition to deterministic tests.
- The GADM selection included the live Liechtenstein download and the Integration/Performance all-country importer, explaining the apparent runner hang.
- The live Zurich Overture lookup returned a release and no service error but zero candidates. The cancellation change did not alter its query, radius, or ranking, so this was classified as external data/query drift rather than cancellation behavior.
- `GadmCacheExporter.ExportGeoPackageToSqlite` remained tokenless and lacked required cancellation checkpoints between managed layers and rows. This was a genuine implementation gap.

### Decisions
- Change 06 task 4.1 was reopened and now explicitly includes GADM export loops.
- Task 5.3 now explicitly excludes Integration and Performance categories for deterministic acceptance.
- Live-source verification remains separate and is not waived.
- Acceptance criteria remain equivalent or stricter; no failing live assertion is treated as passing.

## 2026-08-29 — Change 06: reconciliation follow-up

### Evidence
- Independent review found that GADM failure cleanup could replace the original cancellation or memory failure, publication had no post-move cancellation checkpoint, and the real exporter layer/row loops lacked deterministic controlled-checkpoint coverage.
- `GadmCacheExporter` now observes cancellation before each native managed-loop `Read()` boundary for layer definitions, layers, and rows; an internal test-only checkpoint seam deterministically cancels before the next row or layer read.
- `GadmDivisionCacheService` now suppresses cleanup failures only while an operation is already failing, preserving the original exception, and observes cancellation after the synchronous publication move before reporting readiness/success.

### Decisions
- Reopened task 4.1 only while adding the corrected exporter-loop checkpoints and tests; public constructors and production DI remain unchanged.
- Retain OOM propagation for ordinary public deletion/status paths; only failure-path cleanup is best effort so it cannot replace the original cancellation or OOM.
- The post-move check guarantees cancellation cannot return success; pre-publication cancellation retains the no-new-cache guarantee already covered by the existing gate test.

## 2026-08-29 — Change 06: final verification status

### Evidence
- The reconciled GADM cache/export test suite passed: 15/15, including deterministic cancellation immediately before the next real exporter row and layer read, and cancellation after the synchronous cache move before success is reported.
- The corrected focused suites passed before the reconciliation follow-up: Web 27/27, Overture deterministic 54/54, and GADM deterministic 25/25. The default excluded suite was independently observed passing 158/158 before the final two GADM tests were added; it was not rerun at final synthesis under the instruction not to repeat completed long suites without a concrete code-test failure.
- Isolated GADM live integration passed 1/1 in 2.3 seconds; the all-country importer was not run because it is tagged both Integration and Performance.
- A 10-minute isolated Integration-minus-Performance run exceeded its wall-clock cap. A bounded Overture-only retry failed the Zurich candidate assertion after 20.7 seconds and then exceeded its 120-second cap. The live request obtained a release and no service error but zero candidates; deep analysis confirmed this change does not alter that query, radius, filtering, eligibility, or ranking.

### Decisions
- Task 4.1 is complete: real GADM managed layer/row loops now checkpoint before native reads and are covered deterministically.
- Task 5.4 remains unchecked: default-suite evidence predates the two final GADM tests, and applicable live verification remains unresolved. This does not relabel the external Overture assertion as passing or weaken deterministic acceptance.
- Preserve the Overture live failure/runtime as a separate external-source verification issue; no all-country importer, commit, archive, or unrelated workspace cleanup was performed.

## 2026-08-29 — Change 06: deterministic completion

### Evidence
- Final `npm run test` passed with the default Integration/Performance exclusions: 160 passed, 0 failed, 0 skipped (17.6 seconds), including the two final GADM checkpoint tests.
- The required integration-minus-Performance invocation was attempted after integration-covered changes; it was separately diagnosed above. Its GADM live case passed, while the Overture live Zurich assertion remains an external source-data/runtime failure and was not treated as passing.

### Decisions
- Task 5.4 is complete as verification execution: the final deterministic default suite passed and isolated live verification was run without the Performance-tagged all-country importer.
- The documented external Overture live failure remains unresolved and separate from deterministic change-06 acceptance; no assertion, acceptance criterion, or test was weakened.

## 2026-08-30 — Change 06: Overture Zurich root cause

### Evidence
- Current upstream discovery returns Overture release `2026-08-19.0` with Places schema v1.18.0. The Zurich coordinate is not stale: bounded current-release probes found 2,064 Swiss rows in the 2.5 km query rectangle, 560 at confidence >= 0.90, 17 airport/confidence rows, and 51 application-eligible rows within 2.5 km; the nearest eligible row was 77 m away.
- The production Places query applies an unordered `LIMIT 80` after only name/bbox/country predicates. Haversine distance, confidence, operating status, included/excluded category eligibility, and ranking are applied later in C#. On the current release, the arbitrary first 80 contain no airport row and yield zero application candidates even though eligible nearby rows exist.
- The query and Zurich fixture both predate change 06 (initial commit `8afe725`). The change-06 diff adds cancellation/OOM handling and test seams but does not alter the Places SQL, 2.5 km radius, country filter, eligibility, limit, or ranking. This excludes a change-06 regression.
- Upstream row-order/volume drift exposes the pre-existing unordered-limit defect. The live-latest assertion is brittle as a change-specific gate because it depends on arbitrary upstream scan order, but its positive-candidate assertion remains valid and continues to fail.
- `bbox.xmin/ymin BETWEEN` is a latent general overlap weakness, but it is not this Zurich failure: Places v1.18.0 uses Point geometry, and bounded probes returned the same 2,064 rows for xmin/ymin, full bbox overlap, and point-coordinate predicates. Changing only that predicate would not restore candidates.
- The August 2026 schema deprecates `categories` for removal in September 2026 in favor of `basic_category` and `taxonomy`; this is a separate imminent compatibility risk, not a cancellation regression.

### Decisions
- Classify the blocker as a pre-existing query defect (primary), exposed by upstream drift (contributing), with a brittle live-latest fixture/gating relationship; do not classify it as external data absence or a change-06 regression.
- Task 5.4 remains complete because the post-final-edit default suite passed 160/160 and bounded live verification was executed and honestly dispositioned. The Zurich assertion is not called passing, weakened, or made inconclusive.
- Correct the Places lookup separately before relying on the live test as a release gate: filter for application relevance before truncation, make ordering deterministic, retain a positive in-radius assertion, and add deterministic coverage with more than 80 irrelevant rows preceding a valid candidate. Address category/taxonomy schema migration in the same scoped query correction.
- Full bbox overlap is the safer general predicate, but it is not a sufficient Zurich fix and must not substitute for correcting filter-before-limit and deterministic ordering.


## 2026-08-29 — Change 06: Brooks review remediation

### Findings fixed
- Lookup now routes its component execution boundary through an OOM-preserving helper; deterministic Web coverage proves a controlled OOM escapes rather than becoming component error state.
- Processing rethrows OOM at both per-asset and run-level boundaries; an end-to-end controlled geodata OOM test proves terminal cleanup occurs without an ordinary asset/fatal error.
- Overture and GADM managed reader loops now observe cancellation before and immediately after each native Read; GADM exporter/lookup tests cover controlled row/layer checkpoints and post-native return windows.
- GADM candidate containment now treats recognized ParseException and TopologyException locally as geometry false while preserving bbox fallback/ranking; controlled tests cover the topology case.
- GADM cache lifecycle has deterministic hooks and tests proving OOM escapes status, readiness, validation, and deletion boundaries, while ordinary fallback behavior remains intact.
- Header, schema, and WKB source-export failures now begin with an existing published cache and byte-verify that it remains unchanged after temporary-artifact cleanup.

### Verification
- Focused Web: 33 passed, 0 failed.
- Focused Overture deterministic: 54 passed, 0 failed.
- Focused GADM deterministic: 30 passed, 0 failed.
- `npm run test`: 165 passed, 0 failed, 0 skipped (default Integration/Performance exclusions).
- These fixes preserve cancellation and fault boundaries only; they do not change live source query/ranking semantics, so bounded integration was not rerun. The all-country importer was not run.

## 2026-08-29 — Change 06: Brooks follow-up verification

### Evidence
- Reopened tasks 4.1, 4.3, and 4.5 were completed after deterministic GADM exporter/lookup checkpoint, lifecycle OOM, topology-local containment/ranking, and published-cache identity tests.
- Lookup now explicitly rethrows OutOfMemoryException at its component catch boundary; Processing does the same at per-asset and run boundaries with end-to-end coverage.
- Overture/GADM reader loops now check cancellation before and immediately after native Read, and Overture metadata reads check cancellation both before and after native scalar work.
- Focused verification: Web 33/33, Overture deterministic 54/54, and GADM deterministic 30/30 passed.
- Final `npm run test` passed: 165/165, 0 failed, 0 skipped (17.2 seconds). `git diff --check` passed.

### Decisions
- The follow-up only tightens cancellation, OOM, and malformed-data behavior; it does not alter external-source query/ranking semantics. Bounded integration was therefore not rerun, and the Performance-tagged all-country importer was not run.
- No unrelated pre-existing workspace changes were modified, and no commit, push, sync, or archive action was performed.

## 2026-08-29 — Change 06: second Brooks re-review remediation

### Evidence
- Administrative resolver now uses Overture cache EnsureDataAsync normalization; resolver-level controlled live-waiter coverage proves a foreign owner cancellation is ordinary source unavailability rather than cancellation of the live caller.
- Lookup has direct component-boundary OOM coverage proving OOM escapes rather than populating _error.
- Overture and GADM native reader/metadata boundaries gained immediate pre-native, post-native, and final pre-return checks, with controlled checkpoint coverage.
- Overture two-candidate controlled TopologyException coverage proves the malformed candidate is locally false and processing continues to a valid second candidate. GADM LayerDefinitionRow and metadata checkpoint cases are covered.

### Verification
- Focused Web: 36 passed, 0 failed. Focused Overture deterministic: 55 passed, 0 failed. Focused GADM deterministic: 30 passed, 0 failed.
- Final npm run test: 168 passed, 0 failed, 0 skipped (16.1 seconds). git diff --check passed.
- No live/all-country run: these fixes do not change source query behavior.

## 2026-08-29 — Change 06: third Brooks remediation

### Evidence
- Resolver awaits and classifies the exact shared Overture cache task returned by GetOrStartDownload; a forced removal race proves foreign-owner cancellation is ordinary unavailability/fallback without duplicate work.
- GADM checkpoints are phase-distinct and occurrence-aware, including gpkg_contents LayerDefinitionRow and metadata cancellation after native scalar return.
- Overture schema HasColumn probes accept and observe tokens between real PRAGMA reader operations; topology continuation and cancellation are covered across two candidates.

### Verification
- Focused Web 20/20, Overture deterministic 57/57, GADM deterministic 34/34 passed.
- Final npm run test 175/175 passed, zero failures/skips (16.6 seconds); git diff --check passed.
- No live or all-country rerun: no query behavior changed.

## 2026-08-29 — Change 06: final Overture task-window remediation

### Evidence
- GetOrStartDownload now materializes the dictionary-winning Lazy task once, observes cancellation immediately before tuple publication, and returns that exact task.
- Controlled acquisition-window coverage cancels at the internal post-map hook and proves no tuple is returned while the sole shared owner task remains the only source invocation.

### Verification
- Focused Web OvertureDivisionCacheService tests: 11/11 passed.
- Focused Overture deterministic: 57/57 passed.
- Final npm run test: 176/176 passed, 0 failed/skipped (16.2 seconds). git diff --check passed.
- No live or all-country verification was run because query behavior was unchanged.
