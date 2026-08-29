## 1. Verify finalized numbered prerequisites

- [ ] 1.1 Verify the applied change-51 mutation result/finality/reload seam, final source/ISO types, cache path and owned-temporary policy; stop rather than introduce parallel protocol or completion types if they differ.
- [ ] 1.2 Verify finalized change-52 explicit per-cache/delete-all result fields, actual `Deleted` representation, partial outcomes, and current explicit page reload; treat them as existing prerequisites and make no change-52 dependency on inventory.
- [ ] 1.3 Define closed immutable inventory/source/entry DTOs and idempotent internal key/source invalidation operations, excluding area/geometry row counts, raw paths/errors, and any interface that change 51 or 52 must implement or call.

## 2. Implement contained storage inspection

- [ ] 2.1 Add strict source/ISO validation and fixed canonical source-directory derivation from `StorageOptions`, with immediate-child containment and no-follow symlink/reparse-point policy.
- [ ] 2.2 Add bounded deterministic enumeration for canonical finals and the finalized operation-owned temporary patterns, including missing/inaccessible directory, ignored junk name, truncation, and per-ISO temporary tracking behavior.
- [ ] 2.3 Add read-only `Pooling=false` SQLite inspection limited to expected schema and bounded `_meta` keys, with nullable version/release/downloaded fields and no data-table count, integrity, geometry, or DuckDB work.
- [ ] 2.4 Map stable files to `Available`/`Invalid`/`Unreadable`/`Unsafe`, exact missing keys to `Absent`, temporary-only keys to `InProgress`, and final-plus-temp keys to final status plus temporary indication using safe diagnostic codes.
- [ ] 2.5 Capture size/mtime around metadata reads, dispose every reader/connection before retry or return, and implement one bounded retry for concurrent replacement/deletion without retaining pooled handles.

## 3. Add snapshot caching and invalidation

- [ ] 3.1 Implement a lazy singleton inventory with immutable snapshots, validated internal scan/value/timeout bounds, fixed scan concurrency, and one shared in-flight scan per generation.
- [ ] 3.2 Implement explicit refresh plus dirty-key/source/all invalidation; ensure mid-scan generation changes cannot publish a reusable stale snapshot or clear dirty state.
- [ ] 3.3 Add a change-53-owned adapter over finalized change 51's exact explicit completion result; after session finality invalidate its source/ISO before inventory reread, covering already-ready and every non-success outcome without modifying change 51.
- [ ] 3.4 Add a change-53-owned adapter over finalized change 52's exact explicit deletion result(s); invalidate only actual `Deleted` keys before inventory reread, preserve partial delete-all failures and change 52's current explicit page reload, and require no change-52 inventory interface.

## 4. Migrate Web composition and Data consumers

- [ ] 4.1 Register the inventory once as a lazy singleton only in Web-capable composition, with no hosted startup work and no dependency on cache services, resolvers, exporters, DuckDB, country indexes, processing state, or worker launchers.
- [ ] 4.2 Change `Data.razor` initialization/reload to count `Available` entries and surface safe source-level unreadable/truncated states without resolving heavy cache services.
- [ ] 4.3 Change `GeoBoundaries.razor` to consume immutable inventory rows, replace Area Count with Status/Last Modified, retain optional Version/Release, Size and Downloaded At, and preserve source/ISO filters, deterministic sorting, actions, and GADM attribution/license copy.
- [ ] 4.4 Preserve page operation-generation/disposal safeguards; let change-53 adapters inspect finalized 51/52 explicit results before invalidating/rereading inventory, while leaving change 52's existing explicit reload and all mutation admission, cancellation, and deletion behavior independent of inventory.

## 5. Verify with isolated storage and concurrency tests

- [ ] 5.1 Use temporary directories and minimal SQLite schema/`_meta` fixtures (no geodata rows) to test both sources, optional/malformed metadata, empty/corrupt/wrong-schema files, zero length, exact absence, temp-only, final-plus-temp, unrelated names, and stable DTO fields.
- [ ] 5.2 Test canonical containment, lowercase/non-ASCII/path-like ISO rejection, nested entries, file/source-directory symlinks or reparse points where supported, and safe diagnostics with no absolute path leakage.
- [ ] 5.3 Test missing and denied directories/files, transient SQLite/I/O/locking errors through deterministic seams, scan/value/timeout bounds, truncation, cancellation, and continued results for unaffected entries.
- [ ] 5.4 Test concurrent readers, atomic replacement, deletion during read, one-retry behavior, invalidation during scan, repeated/idempotent invalidation, and prove all SQLite files can be moved/deleted immediately after snapshots (no pooled/open handles).
- [ ] 5.5 Move/replace existing cache `GetStatus` coverage and add Data/GeoBoundaries projection tests for available counts, non-available display, filtering, deterministic sorting, nullable metadata, and removal of area-count queries.
- [ ] 5.6 Add Standard/Web-only composition sentinel tests whose heavy cache/resolver/exporter/index factories throw if resolved, plus worker/run-once tests proving inventory performs no startup work and is not required for job composition.
- [ ] 5.7 Run focused inventory/UI/composition tests, the normal non-integration suite, relevant deterministic integration tests, strict OpenSpec validation/status, and a block-53 scope check confirming no project code or artifacts owned by block 52 were changed during planning.
