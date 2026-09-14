# Immich change markers and watermark safety

**Decision: select no polling watermark.** Keep the full-eligibility EXISTS detector introduced in change 58. Change 62 is no-go; the withdrawn changes 62–64 do not acquire runtime scope through this research. Overlap, deduplication and periodic reconciliation cannot establish that a lossy watermark has zero false negatives.

This review inspected Immich ReverseGeo revision `d9d3db28ae82f1d5c54b15c085b8fa5f8070da2b`. Its [repository queries and writes](https://github.com/crazz/immich-reversegeo/blob/d9d3db28ae82f1d5c54b15c085b8fa5f8070da2b/src/ImmichReverseGeo.Web/Services/ImmichDbRepository.cs) use this eligibility predicate: city and country are null, both GPS coordinates are present, and the asset is not soft-deleted. Batch ordering by `(asset."createdAt", asset.id)` is pagination. The launch detector observes current eligibility without remembering a cursor. It does not promise to capture transient historical eligibility between polls. See the [EXISTS evidence](scheduled-existence-evidence.md) and [query-plan diagnostics](postgresql-detector-query-plans.md) for its behavior and cost.

## Pinned evidence and compatibility

These are inspected evidence points, not a newly declared supported version interval. The project has no declared minimum/maximum Immich interval for a watermarked detector. The release tags below identify the pinned source; moving `main` is not evidence.

| Evidence point | Reviewed sources | Watermark conclusion |
|---|---|---|
| [v3.1.0, `8aa95c67`](https://github.com/immich-app/immich/commit/8aa95c67470a02a8ddedf03c2e52963af33065ff) | [asset][31-asset], [EXIF][31-exif], [audit][31-audit], [functions][31-functions], [decorators][31-decorators], [metadata][31-metadata], [upsert][31-upsert], [backfill][31-backfill] | Scalar sources fail commit-order safety. Source inspection does not establish operational compatibility. |
| [v3.2.0, `1b6098c9`](https://github.com/immich-app/immich/commit/1b6098c9dbfffe978bec2d414606ed7a4c8e019a) | [asset][32-asset], [EXIF][32-exif], [audit][32-audit], [functions][32-functions], [decorators][32-decorators], [metadata][32-metadata], [upsert][32-upsert], [backfill][32-backfill] | Same rejection; also the schema witness used for change 58's isolated query fixtures. |
| [Historical research revision `469a870a`](https://github.com/immich-app/immich/commit/469a870a2233e7361bcb855b183fd41272cfd056) | [asset][ref-asset], [EXIF][ref-exif], [audit][ref-audit], [functions][ref-functions], [decorators][ref-decorators], [metadata][ref-metadata], [upsert][ref-upsert], [backfill][ref-backfill] | A historical main-branch observation, not today's main or a release support contract. |
| [Rename migration `c699df00`](https://github.com/immich-app/immich/blob/c699df002a32ac175cee276b8f7a9eab1e1b4c42/server/src/schema/migrations/1752267649968-StandardizeNames.ts) and older schemas | Migration renames `assets` to `asset` and `exif` to `asset_exif`. | Naming evidence only. No release interval or old marker/index compatibility was tested here. |
| Other revisions, restored databases, or future schemas | Unverified until pinned and tested. | Do not advance a watermark on inferred compatibility. Stop the proposed incremental path; use full EXISTS only where its schema contract is valid. |

The asset, EXIF and audit table source files are byte-identical at the three inspected revisions. The reviewed UUID/update trigger blocks and EXIF geography upsert block also agree; other functions, metadata workflows and repository methods differ. This comparison is narrower than claiming whole-release equivalence.

In the [v3.2 asset model][32-asset], `id` is a generated primary key, while `createdAt`, `updatedAt` and indexed `updateId` are separate fields. Its primary-key decorator alone is not evidence of commit order. The [EXIF model][32-exif] has nullable GPS/geography, `updatedAt`, indexed `updateId`, and an asset foreign key with cascading deletion. [Decorators][32-decorators] attach row-level BEFORE UPDATE stamping. The [trigger function][32-functions] assigns the update timestamp and UUID before commit. An EXIF-only SQL update does not invoke an asset-table update trigger.

The [UUID function][32-functions] combines a millisecond timestamp with random UUID bits. It neither serializes transactions nor establishes ordering within a millisecond. Timestamp conversion, clock changes and precision need explicit tests in any future design. [Metadata construction][32-metadata] starts with null GPS/geography and fills it when available. [EXIF upsert][32-upsert] handles these columns and can preserve locked properties. This is evidence of relevant write paths, not a claim that every metadata operation writes only EXIF. ReverseGeo's location writes and three clear paths issue EXIF UPDATEs without setting markers themselves; upstream triggers can therefore create feedback events.

## Candidate outcomes

| Candidate | Coverage and index/order evidence | Counterexample or missing guarantee | Decision |
|---|---|---|---|
| `asset.createdAt` + `id` | Stable batch ordering; asset creation time is indexed in reviewed models. | Old rows can become eligible through delayed EXIF, GPS/geography changes or restore. Insertion order is not commit order. | Reject. |
| `asset.updatedAt` or `updateId` | Asset-row updates; indexed UUID update ID. | EXIF-only changes do not advance asset markers. Assignment occurs before commit. | Reject. |
| `asset_exif.updatedAt` | Surviving EXIF insert/update versions, including own writes. No dedicated timestamp index demonstrated by the reviewed model. | Clock/precision and commit inversion; deleted rows have no remaining marker. | Reject. |
| `asset_exif.updateId`, including `assetId` tie-breaker | Indexed UUID marker, deterministic tuple pagination. | Same-millisecond random suffix and delayed commit can leave an eligible row below the cursor. | Reject. |
| Asset marker plus `asset_audit` | Separate asset-update and hard-delete audit streams; audit ID and indexed asset/owner/deletion fields. | No common commit order. Reviewed tables/functions do not demonstrate a complete EXIF-delete audit stream. | Reject. |
| `xmin`, transaction ID, or snapshots | Current row-version/internal transaction metadata, not an application tail index. | Allocation precedes commit; internal `xid` is 32-bit and wraps, and current rows do not retain all deleted/history events. A snapshot is not a durable event feed. | Reject. |
| Trigger plus LISTEN/NOTIFY | Configurable wake-ups delivered after commit to registered listeners. | Registration is session-scoped; initialization needs a state inspection. Notifications alone do not replay work missed while disconnected. | Reject as durable source. |
| Custom table storing the last scalar | Can persist the consumer's cursor. | Persistence cannot recover an unseen lower marker. A maximum sequence value has the same overtaking problem. | Reject. |
| Transactional outbox consumed as a queue | Could retain configured write/delete events until acknowledged. | Needs custom Immich schema/triggers and queue ownership/recovery proof. Consuming `id > max_seen` still fails. | Future proposal only. |
| Durable logical decoding slot and commit LSN | Committed changes from both relations with a replayable WAL feed. | Requires atomic bootstrap, durable acknowledgment, replay, retention, failover, privileges and DDL controls. Streaming in-progress transactions must not be treated as committed. | Future architecture candidate; unproven here. |

PostgreSQL assigns [transaction IDs when transactions first write](https://www.postgresql.org/docs/16/transaction-id.html); lower IDs mean earlier assignment, not earlier commit. [`xmin` identifies a row version's inserting transaction](https://www.postgresql.org/docs/16/ddl-system-columns.html). [LISTEN](https://www.postgresql.org/docs/16/sql-listen.html) and [NOTIFY](https://www.postgresql.org/docs/16/sql-notify.html) describe listener lifetime, initialization and delivery. Logical decoding's ordinary [begin/commit callback ordering](https://www.postgresql.org/docs/16/logicaldecoding-output-plugin.html#LOGICALDECODING-OUTPUT-PLUGIN-CALLBACKS) is a stronger source, but [slots can replay after crashes and retain WAL](https://www.postgresql.org/docs/16/logicaldecoding-explanation.html). These properties do not supply an implemented consumer or a passed compatibility matrix.

## Reproduce commit inversion

The [research script](research/verify-watermark-commit-inversion.py) runs real transactions against PostgreSQL 16, using synthetic rows and fixed timestamp/UUID values. It models a legal pre-commit assignment order; it does not execute Immich migrations, UUID generation or application mutations. XIDs and `xmin` are produced by PostgreSQL. The synthetic `eligible` flag isolates the ordering failure from geodata and application processing.

| Step | Lower-marker transaction T1 | Higher-marker transaction T2 | Observer |
|---|---|---|---|
| 1 | Insert lower marker; remain uncommitted. | — | — |
| 2 | Still open. | Insert higher marker and commit. | — |
| 3 | Still invisible. | Visible and eligible. | Poll sees exactly T2; save its marker and mark it processed. |
| 4 | Commit. | Already processed. | Full current eligibility finds T1; every scalar and `(marker, id)` tail misses it. |

The script also repeats the schedule with equal timestamp/UUID markers and T1's lower secondary ID. Stable ties still miss T1. It checks both scalar and tuple forms for timestamps, UUIDs, transaction IDs and `xmin`: eight failed tail strategies in each of two cases. It uses completed SQL acknowledgments and visibility assertions, with bounded waits and no timing sleeps.

Run only in a new disposable container. The example has no host ports or host bind mounts and uses a synthetic research credential. Wait for Docker to report it healthy before running the script; rerunning the script creates a fresh, uniquely named research database.

```bash
docker run --detach --name immich-rg-watermark-research \
  --network none \
  --label org.immich-reversegeo.research=watermark-commit-inversion \
  --env POSTGRES_USER=research --env POSTGRES_PASSWORD=research_only \
  --health-cmd 'pg_isready -U research -d postgres' \
  --health-interval 1s --health-timeout 3s --health-retries 20 \
  postgres@sha256:cf78e76683b9ca8c5733cbbdce6c9262b45b6767934dd0a95e671f9a0fc20685
docker inspect --format '{{.State.Health.Status}}' immich-rg-watermark-research
python3 docs/maintainer/research/verify-watermark-commit-inversion.py immich-rg-watermark-research
docker rm --force --volumes immich-rg-watermark-research
```

Python 3 and Docker are required; psql comes from the pinned PostgreSQL 16.15 image. The script refuses containers without its label, network isolation or PostgreSQL major version, and refuses host ports/bind mounts. It closes owned sessions and drops only its generated `wm_research_...` database, even after an assertion fails. Success emits two case results followed by `cleanup: database-absent`; a missing result or nonzero exit is not passing evidence. Remove the dedicated container after either success or failure. Nothing in these commands connects to an Immich installation.

## Mutation acceptance cases for a future proposal

**These are defined gates, not executed Immich integration results.** Every case must run on each release/commit in the future declared support matrix. Start with the inspected v3.1.0 and v3.2.0 schemas above, then pin every additionally supported revision. Record the actual API/SQL/migration entry point, before/after row state, emitted event, durable acknowledgment and final eligible asset set. Run relevant writes with both normal and inverted commit order. Any mismatch is a failure, not an acceptable measured miss rate.

| Case and entry point | Required observation and assertion |
|---|---|
| M01 asset plus EXIF insertion; asset creation followed by delayed EXIF insertion | A new eligible asset and an old asset first acquiring eligible EXIF both reach processing. Advancing through a newer asset cannot hide either. |
| M02 metadata extraction/upsert: GPS absent → added, corrected, one/both coordinates cleared → restored | Capture every transition into the active predicate; reject rows still missing either coordinate. Corrections to an already eligible row remain discoverable. Test locked and unlocked fields separately. |
| M03 geography changes: city-only clear, country-only clear, both clear, state-only clear | Only null city **and** null country satisfy today's predicate. State alone does not control eligibility. Observe the transition when the second required field becomes null. |
| M04 ReverseGeo `WriteLocationAsync` and all three clear methods | Populating city/country exits current eligibility; own-write feedback must not create a processing loop. Clearing all locations, selected assets, and locations by value must rediscover eligible old rows. |
| M05 overwrite predicates | No overwrite mode exists in the inspected ReverseGeo implementation. A future mode must enumerate each new predicate and re-run the full transition matrix; current evidence is not inherited automatically. |
| M06 soft delete then restore of an old asset | Deleted rows are excluded; restoring an otherwise eligible row emits discoverable work even if EXIF and creation markers are old. |
| M07 hard asset delete/cascade, direct EXIF delete, EXIF recreation, delete/recreate with reused key | Do not process nonexistent rows; retain necessary deletion observations and rediscover eligible recreated rows without stale deduplication suppressing them. |
| M08 migration backfill and database restore | Use pinned migrations, including the reviewed [rating normalization][32-backfill] as non-eligibility feedback; add a backfill that changes eligibility. Floods must not cause skips. Restored older markers must invalidate unsafe progress and force a safe bootstrap. |
| M09 timezone, DST, timestamp serialization/truncation, clock rollback, equal timestamps and same-millisecond UUIDs | Preserve required ordering/identity through storage and transport; zero missed rows even for equal values and reverse UUID suffix order. UTC conversion alone is insufficient. |
| M10 long transactions, equal marker with different asset IDs, reverse marker/commit order, rollback/savepoint | Commit only after a higher marker has been observed, as in this fixture. Both distinct and equal-marker cases must survive advancement; rolled-back changes must not become durable work. |

## Recovery and coordination acceptance cases

These additional gates apply to any future persisted source. They are not implemented by a cursor file or proven by the synthetic fixture.

| Case | Required failure/recovery behavior |
|---|---|
| R01 restart before/after event receipt, before/after durable effects, before/after acknowledgment | No acknowledgment can overtake durable processing state. Replays are safe and no committed eligible event disappears. |
| R02 crash during cursor/state write; missing, truncated, corrupt or incompatible state | Detect invalid state and stop or bootstrap safely; never substitute an advanced/default cursor that skips rows. |
| R03 duplicate and reordered delivery, reconnect and replay | Idempotence preserves legitimate later changes to the same asset. Deduplication must not suppress a distinct eligibility transition. |
| R04 two containers with independent `/data` volumes | Each intended consumer receives complete work or a declared shared coordinator owns consumption. Independent files cannot be assumed to coordinate schedulers. |
| R05 two containers on a shared volume, concurrent writes, stale writer, process death and lock loss | Prove atomic state updates, fencing/ownership and takeover. A shared filesystem alone is insufficient; stale writers cannot overwrite newer durable state or advance it without effects. |
| R06 schema/DDL drift, unsupported release, privilege loss | Stop incremental advancement. Fall back to full EXISTS only if its own schema/privilege contract is valid; otherwise surface failure. |
| R07 restore/failover/timeline change, slot loss or invalidation, retention exhaustion | Detect discontinuity and recover through a safe bootstrap; do not resume an incomparable LSN/marker. Bound and monitor retained resources. |
| R08 simultaneous bootstrap and writes to either relation | Atomic snapshot/feed handoff includes every committed event exactly as required by idempotent replay. Cover INSERT, UPDATE, DELETE and applicable two-phase/streamed transaction modes. |

## Revisit gate

A new or revised proposal may reopen change 62 only with all of the following evidence:

1. A declared minimum/maximum Immich release matrix with commit-pinned migrations, columns, triggers, indexes and actual mutation entry points. Unknown versions are not silently accepted.
2. Automated mutation cases with **zero missed eligible transitions**, including M01–M10, inverse commit order, ties and clock changes. Tests must demonstrate the source's no-loss construction, not just a short run with no observed misses.
3. Restart/crash/replay/corrupt-state and two-container results satisfying R01–R08 without unsafe advancement.
4. Explicit schema-drift behavior that stops or falls back to valid full EXISTS without advancing state.
5. `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` plans and measurements for every supported schema, demonstrating an indexed or explicitly bounded tail cost under representative cardinality and backlog. State the cost bound and test data; an index name alone is not evidence.
6. For logical decoding: atomic snapshot/slot bootstrap; both asset relations and all relevant operations; durable effects before commit-LSN acknowledgment; idempotent crash replay; WAL retention/exhaustion and slot-loss alarms; failover/timeline recovery; documented privileges; and a stop policy for unknown DDL.

No candidate passed this gate. No watermark state, runtime migration, trigger, listener, replication slot or reconciliation schedule is introduced. Change 59's cost telemetry and change 60's diagnostics remain the tools for assessing the full EXISTS path.

[31-asset]: https://github.com/immich-app/immich/blob/8aa95c67470a02a8ddedf03c2e52963af33065ff/server/src/schema/tables/asset.table.ts
[31-exif]: https://github.com/immich-app/immich/blob/8aa95c67470a02a8ddedf03c2e52963af33065ff/server/src/schema/tables/asset-exif.table.ts
[31-audit]: https://github.com/immich-app/immich/blob/8aa95c67470a02a8ddedf03c2e52963af33065ff/server/src/schema/tables/asset-audit.table.ts
[31-functions]: https://github.com/immich-app/immich/blob/8aa95c67470a02a8ddedf03c2e52963af33065ff/server/src/schema/functions.ts
[31-decorators]: https://github.com/immich-app/immich/blob/8aa95c67470a02a8ddedf03c2e52963af33065ff/server/src/decorators.ts
[31-metadata]: https://github.com/immich-app/immich/blob/8aa95c67470a02a8ddedf03c2e52963af33065ff/server/src/services/metadata.service.ts
[31-upsert]: https://github.com/immich-app/immich/blob/8aa95c67470a02a8ddedf03c2e52963af33065ff/server/src/repositories/asset.repository.ts
[31-backfill]: https://github.com/immich-app/immich/blob/8aa95c67470a02a8ddedf03c2e52963af33065ff/server/src/schema/migrations/1771535611395-ConvertRating0ToNull.ts
[32-asset]: https://github.com/immich-app/immich/blob/1b6098c9dbfffe978bec2d414606ed7a4c8e019a/server/src/schema/tables/asset.table.ts
[32-exif]: https://github.com/immich-app/immich/blob/1b6098c9dbfffe978bec2d414606ed7a4c8e019a/server/src/schema/tables/asset-exif.table.ts
[32-audit]: https://github.com/immich-app/immich/blob/1b6098c9dbfffe978bec2d414606ed7a4c8e019a/server/src/schema/tables/asset-audit.table.ts
[32-functions]: https://github.com/immich-app/immich/blob/1b6098c9dbfffe978bec2d414606ed7a4c8e019a/server/src/schema/functions.ts
[32-decorators]: https://github.com/immich-app/immich/blob/1b6098c9dbfffe978bec2d414606ed7a4c8e019a/server/src/decorators.ts
[32-metadata]: https://github.com/immich-app/immich/blob/1b6098c9dbfffe978bec2d414606ed7a4c8e019a/server/src/services/metadata.service.ts
[32-upsert]: https://github.com/immich-app/immich/blob/1b6098c9dbfffe978bec2d414606ed7a4c8e019a/server/src/repositories/asset.repository.ts
[32-backfill]: https://github.com/immich-app/immich/blob/1b6098c9dbfffe978bec2d414606ed7a4c8e019a/server/src/schema/migrations/1771535611395-ConvertRating0ToNull.ts
[ref-asset]: https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/tables/asset.table.ts
[ref-exif]: https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/tables/asset-exif.table.ts
[ref-audit]: https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/tables/asset-audit.table.ts
[ref-functions]: https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/functions.ts
[ref-decorators]: https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/decorators.ts
[ref-metadata]: https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/services/metadata.service.ts
[ref-upsert]: https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/repositories/asset.repository.ts
[ref-backfill]: https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/migrations/1771535611395-ConvertRating0ToNull.ts
