## Context

See `proposal.md` for motivation and `specs/immich-watermark-source-selection/spec.md` for the gate. The current repository batches eligible rows by `(asset."createdAt", asset.id)`, but that is pagination only; eligibility is city and country null, latitude and longitude present, and asset not deleted. ReverseGeo writes and all three clear paths update `asset_exif` without explicitly managing a timestamp. Block 58's planned `SELECT EXISTS` remains a full current-state observation and is the safe baseline.

Upstream evidence was reviewed against Immich v3.1.0 ([release](https://github.com/immich-app/immich/releases/tag/v3.1.0), commit [`8aa95c67`](https://github.com/immich-app/immich/commit/8aa95c67470a02a8ddedf03c2e52963af33065ff)) and current main commit [`469a870a`](https://github.com/immich-app/immich/commit/469a870a2233e7361bcb855b183fd41272cfd056). The result is intentionally a no-go outside as well as inside that observed matrix: absence of evidence is not compatibility.

## Goals / Non-Goals

**Goals:**
- Decide whether any reviewed source proves zero false negatives for transitions into current or future eligibility.
- Separate deterministic keyset pagination and practical risk reduction from strict commit-ordered change capture.
- Give block 62 an unambiguous go/no-go and objective criteria for a future review.

**Non-Goals:**
- Do not implement a watermark, overlap window, state table, trigger, listener, replication slot, reconciliation schedule, or schema mutation.
- Do not weaken or alter block 58's EXISTS predicate, worker authority, or current overwrite semantics.
- Do not claim support for an Immich version merely because a column name appears in one revision.

## Decisions

### 1. Select no polling watermark and preserve EXISTS

**Decision:** No reviewed polling scalar is safe. Block 62 is **no-go**. Frequent checks continue to use block 58's exact full-eligibility EXISTS observation; block 63 cannot be used to excuse known misses.

A scalar can be assigned in transaction T1, T1 can pause, T2 can receive a greater scalar and commit, a poll can advance through T2, and T1 can then commit below the watermark. A deterministic tuple fixes ties but not this commit inversion. Finite overlap, deduplication, and reconciliation reduce risk but do not prove zero false negatives.

### 2. Interpret current upstream row markers narrowly

Immich's current `asset` schema separates `createdAt`, `updatedAt`, UUIDv4 `id`, and UUIDv7 `updateId` ([schema](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/tables/asset.table.ts#L64-L102)). `asset_exif` contains nullable GPS/geography plus `updatedAt` and indexed `updateId` ([location](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/tables/asset-exif.table.ts#L62-L75), [markers/index](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/tables/asset-exif.table.ts#L107-L120)). BEFORE UPDATE triggers stamp both update fields unconditionally ([decorator](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/decorators.ts#L21-L26), [function](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/functions.ts#L38-L50)). EXIF-only writes therefore do not advance `asset.updatedAt`, while ReverseGeo's own EXIF UPDATE advances EXIF markers and can create feedback traffic even though populated city/country exits current eligibility.

UUIDv7 here embeds millisecond time plus a random tail and is generated before commit ([implementation](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/functions.ts#L3-L23)); it is neither asset-ID chronology nor commit order. Metadata extraction/upsert covers GPS and geography ([construction](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/services/metadata.service.ts#L251-L280), [upsert](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/repositories/asset.repository.ts#L251-L307)), including clears of unlocked fields, but column stamping still occurs before commit.

### 3. Candidate outcome matrix

| Candidate | Coverage and behavior | Index/order evidence | Failure relevant to eligibility | Outcome |
|---|---|---|---|---|
| `asset.createdAt` + `id` | Inserts only; repository uses it for batches | Deterministic tuple; asset ID is UUIDv4 | Misses delayed EXIF, GPS changes/clears, ReverseGeo clears, old-row restores; not commit order | Reject |
| `asset.updatedAt` / `updateId` | Asset-row writes and soft deletes | Current markers exist; updateId is UUIDv7 | EXIF-only eligibility changes do not advance asset marker; pre-commit assignment | Reject |
| `asset_exif.updatedAt` | EXIF inserts/updates, GPS/geography edits, clears, backfills, own writes | Current updatedAt plus indexed updateId | Wall clock is pre-commit and can regress; hard/direct EXIF delete has no row marker | Reject |
| `asset_exif.updateId` or tuple with assetId | Same EXIF writes; deterministic tie handling with assetId | Indexed current UUIDv7 marker | Same-ms random tail and pre-commit generation permit commit inversion | Reject |
| Asset marker plus `asset_audit` | Soft/hard asset events plus EXIF current state | Separate streams | Hard-delete audit exists, but no EXIF-delete audit and no common commit order ([audit](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/tables/asset-audit.table.ts#L4-L16)) | Reject |
| `xmin`, txid, or transaction snapshots | Surviving row versions | PostgreSQL internals, not stable application index | XIDs allocate before commit, wrap/freeze, and deleted/transient rows disappear ([system columns](https://www.postgresql.org/docs/18/ddl-system-columns.html), [snapshots](https://www.postgresql.org/docs/18/functions-info.html#FUNCTIONS-PG-SNAPSHOT)) | Reject |
| Trigger + `LISTEN/NOTIFY` | Configurable events | No durable queue | Session registration is transient and initialization races; notification is wake-up only ([LISTEN](https://www.postgresql.org/docs/18/sql-listen.html), [NOTIFY](https://www.postgresql.org/docs/18/sql-notify.html)) | Reject |
| Custom state table storing last scalar | Persists detector state only | Under our control | Cannot recover events the source never exposes; max sequence can still overtake uncommitted lower sequence | Reject |
| Transactional outbox consumed as a queue | Could cover configured writes/deletes | Requires custom Immich DB triggers/schema | Can be no-loss only with queue semantics, not max-ID polling; unsupported invasive coupling | Do not select; future proposal only |
| Durable logical decoding slot + commit LSN | Committed INSERT/UPDATE/DELETE from both relations | Commit-ordered WAL feed | Conditionally safe only with bootstrap, retention, replay, failover, privileges, and DDL controls ([slots](https://www.postgresql.org/docs/18/logicaldecoding-explanation.html#LOGICALDECODING-REPLICATION-SLOTS), [ordering](https://www.postgresql.org/docs/18/logicaldecoding-output-plugin.html#LOGICALDECODING-OUTPUT-PLUGIN-CALLBACKS)) | Future research candidate, not block 62's polling design |

### 4. Mutation and operational conclusions

- **Inserts/delayed metadata:** asset creation can precede EXIF insertion or later extraction; only current-state EXISTS has demonstrated complete observation without relying on marker order.
- **GPS changes and metadata clears:** EXIF upserts can make an old asset eligible; metadata extraction can null unlocked coordinates/geography. Asset-only markers miss these.
- **ReverseGeo feedback and overwrite:** current writes populate city/state/country and leave the exact null/null predicate. Their EXIF trigger stamps cause false-positive tail traffic. No overwrite mode exists today; any future mode changes the transition matrix and invalidates prior evidence.
- **Deletes/restores:** soft delete changes asset only; hard delete cascades EXIF via FK ([FK](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/tables/asset-exif.table.ts#L21-L24)); restoring an old row can make it eligible below a createdAt cursor. Current-row scalar polling has no complete tombstone stream.
- **Backfills/restores:** Immich migrations mass-update rows, including EXIF rating normalization ([migration](https://github.com/immich-app/immich/blob/469a870a2233e7361bcb855b183fd41272cfd056/server/src/schema/migrations/1771535611395-ConvertRating0ToNull.ts#L3-L5)). This causes floods/false positives and does not repair commit order.
- **Timezone/ties:** use UTC and a stable asset key for pagination, but timestamp precision, clock rollback, same-millisecond UUIDs, and long transactions keep a watermark unsafe.
- **Multi-container:** a file under `/data` is not authoritative unless all schedulers share it and updates are atomic and coordinated. Independent volumes diverge; shared volumes still need single-writer fencing. Persisting an unsafe source more reliably does not make it safe.

### 5. Compatibility matrix and fail-closed rule

| Immich line/revision | Schema evidence | Decision |
|---|---|---|
| v1.135.3 and earlier naming | Predates the `assets`→`asset` and `exif`→`asset_exif` rename | Outside verified direct-SQL matrix; fail closed |
| v1.136.0 rename boundary | Rename introduced by commit [`c699df00`](https://github.com/immich-app/immich/commit/c699df002a32ac175cee276b8f7a9eab1e1b4c42) ([migration](https://github.com/immich-app/immich/blob/c699df002a32ac175cee276b8f7a9eab1e1b4c42/server/src/schema/migrations/1752267649968-StandardizeNames.ts#L5-L18)) | Names alone do not prove marker semantics or index compatibility; fail closed |
| v3.1.0 / `8aa95c67` | Current reviewed asset/EXIF columns, triggers, UUID generation, and indexes | All scalar candidates still fail commit-order proof |
| main / `469a870a` | Same reviewed model at research time | Research reference only, not a supported release contract; all scalar candidates fail |
| Any future schema | Unknown until pinned and tested | Fail closed; continue EXISTS |

The project has no declared supported Immich version interval. This research therefore does not invent one; it identifies evidence points and rejects the watermark on every row.

### 6. Measurable criteria for revisiting

A revised proposal may reopen block 62 only when it supplies:
1. a declared minimum/maximum Immich release matrix with commit-pinned migrations, columns, triggers, and indexes;
2. automated cases with **zero missed eligible transitions** for insert, delayed EXIF, GPS add/change/clear, geography clear, ReverseGeo write, each overwrite predicate, soft delete/restore, hard delete/recreate, backfill, equal values, clock skew, and transactions committed in inverse marker order;
3. restart/crash/replay/corrupt-state and two-container coordination tests with no unsafe advancement;
4. explicit schema-drift behavior that stops or falls back to full EXISTS without advancing state;
5. `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` evidence that every supported schema has an indexed or bounded-cost tail path; and
6. for logical decoding, atomic snapshot/slot bootstrap, durable effects before commit-LSN acknowledgment, idempotent replay, slot retention/loss alarms, failover/timeline recovery, privilege documentation, and unknown-DDL stop behavior.

Meeting only an overlap-window target, observed low miss rate, or periodic reconciliation is insufficient because the acceptance threshold is zero false negatives by construction.

## Risks / Trade-offs

- **EXISTS can remain expensive on empty libraries** → Keep block 59 cost telemetry and block 60 diagnostics; optimize only with independently safe indexes/query plans.
- **No-go delays NAS-oriented frequent-tail scheduling** → Prefer correctness; blocks 62–64 stay gated until a new proof exists.
- **Upstream links can outlive behavioral support assumptions** → Use commit-pinned links and repeat the matrix for every declared supported release.
- **Logical decoding can retain excessive WAL or fail operationally** → Treat it as a separate future architecture with monitoring and fail-closed behavior, not a hidden extension of block 62.

## Migration Plan

No runtime migration is authorized. Keep block 58's stateless EXISTS detector and do not create cursor files, DB objects, listeners, slots, or configuration. A future passing proposal must define its own rollout, bootstrap, downgrade, and rollback plan.
