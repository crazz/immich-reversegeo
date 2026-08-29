## Context

See [proposal.md](proposal.md) for motivation and [specs/cross-process-run-locking/spec.md](specs/cross-process-run-locking/spec.md) for behavior. Block 23 fixes the lifecycle boundary: busy is a typed first executor-entry gate after exact-once host invocation and `run-started`, but before domain/heavy execution. It must produce the existing failed terminal and exit 3. The Web's local admission lock remains a separate, complementary boundary.

Npgsql connections are pooled by default, so disposing a logical connection normally returns its physical session to the pool. PostgreSQL session advisory locks survive transactions and disappear when that server session ends. A healthy pooled session therefore must be explicitly unlocked before it can be returned, while an ambiguously released session must be prevented from re-entering the pool.

## Goals / Non-Goals

**Goals:**
- Serialize protected processing for workers targeting one Immich database and lock-key version.
- Preserve block 20/23 exact-once executor, reporter, terminal, and exit-precedence ownership.
- Make acquisition, cancellation, loss, unlock, and disposal deterministic and unit-testable.
- Exercise real PostgreSQL session semantics in explicit `Integration` tests.

**Non-Goals:**
- Replace the Web coordinator or its in-process lock.
- Add fencing rows/tokens, tables, indexes, migrations, queues, retries, or public configuration.
- Prove the block-32 cross-process/controller matrix.
- Coordinate separate PostgreSQL databases or applications that use a different key version.

## Decisions

### Use one published signed-bigint key for version 1

Define compile-time constants equivalent to:

- key version: `1`
- derivation label: `immich-reversegeo/postgresql-advisory-run-lock/v1`
- SHA-256: `916360a3f80ad7d0ae2a32661692f1381e43b2f336f19a58491ce5582ffb9dbf`
- first eight digest bytes, big-endian signed two's complement: `-7970420658158250032L` (bits `0x916360A3F80AD7D0`)

The runtime uses the checked-in numeric constant; a test recomputes the documented derivation to detect accidental drift. Acquisition executes parameterized `SELECT pg_try_advisory_lock($1)` with an explicit bigint parameter. Release executes parameterized `SELECT pg_advisory_unlock($1)` and requires exactly `true`.

This key is database scoped. Independent installations against the same Immich database intentionally contend. An unrelated application in that database can collide only if it uses the same 64-bit value; the published namespace and SHA-256-derived value establish collision discipline, not a mathematical guarantee. Changing the label, byte order, signed interpretation, PostgreSQL key overload, or value creates a new coordination version and cannot be rolled out to mixed workers without an overlap strategy.

Alternative: PostgreSQL's two-int key overload. Rejected because one documented signed bigint is simpler to reproduce and less prone to component-order mistakes. Alternative: .NET `GetHashCode`. Rejected because it is not a stable cross-runtime contract.

### Acquire inside the existing executor/reporter session

The host still invokes the executor exactly once. The reporter emits `run-started`; immediately afterward the executor calls the lock collaborator before any eligibility query, snapshots, skipped-asset access, cache/geodata initialization, lookup, mutation, or progress-producing domain work. Opening the dedicated Npgsql connection is part of this first gate. This explicitly replaces the old shorthand “before ProcessingRunExecutor execution” with “before domain/heavy execution.”

The collaborator returns a discriminated result: acquired lease, busy, cooperatively cancelled, or infrastructure failure. Busy follows the normal executor/reporter completion path with one valid `failed` terminal containing predefined safe detail. It contributes block 23's typed Busy fact, emits no eligibility event and leaves terminal `ProcessedCount`, `UpdatedCount`, `SkippedCount`, and `FailedCount` all zero, and never routes through domain-failure accounting. No new protocol event or terminal type is introduced.

Alternative: acquire before executor invocation. Rejected because it would bypass the accepted-run session, contradict block 23, and leave contention without the required terminal. Alternative: acquire before `run-started` inside the executor. Rejected because `run-started` is the established first accepted-session event.

### Represent ownership as an async lease over one exact connection

The acquisition collaborator opens a new connection from the existing worker data source and never exposes it to repositories. A successful lease owns that exact logical/physical session exclusively. It remains open and out of the pool from acquisition through all protected work, terminal reporting, and lock finalization. The lease is single-disposal and coordinates monitor shutdown, explicit unlock, and connection disposal.

On orderly cleanup it:
1. stops and awaits the ownership monitor;
2. runs unlock with a short bounded internal cleanup token independent of the already-cancelled run token;
3. requires `pg_advisory_unlock` to return true;
4. disposes the connection only after confirmed unlock.

If unlock is false, throws, is cancelled by the cleanup bound, or release is otherwise ambiguous, cleanup records infrastructure failure. It calls `NpgsqlConnection.ClearPool(connection)` before disposal so the involved connector and other pre-clear pooled connectors are closed rather than risking reuse of a session that might still own the key. This broad pool invalidation is intentionally limited to an ambiguous-release fault. A connection already known broken is disposed and is never treated as reusable.

Alternative: rely on `DisposeAsync` alone. Rejected because pooled disposal normally returns the physical session and session-level locks are not a transaction-reset concern to leave implicit. Alternative: disable pooling for all worker database traffic. Rejected because only one lock connection needs special lifetime handling.

### Detect ownership loss and stop protected work

The lease runs a serialized health probe on its dedicated connection every five seconds using a lightweight parameterless command. The monitor performs no concurrent command during acquisition or unlock. A connection state transition or probe failure marks ownership lost exactly once and cancels an internal loss token linked into protected executor work. The resulting internal cancellation is translated to block 23 infrastructure outcome 5 and safe failed-terminal detail, not cooperative cancellation 130. If caller cancellation races with detected loss, infrastructure retains block 23's higher precedence.

A session advisory lock has no fencing token. A network break can exist before client I/O detects it, so a schema-free design cannot prove zero overlap during that detection window. The five-second probe bounds normal detection effort; after loss is observed no additional protected work may begin, and mutation code receives the linked token. Tests use an injected monitor interval/time seam rather than wall-clock delays.

Alternative: rely only on connection `StateChange`. Rejected because a dead idle socket may not be noticed until I/O. Alternative: add a fencing row checked by every mutation. Rejected because this block forbids schema changes and would be a materially different consistency design.

### Preserve cancellation and block-23 precedence

The accepted request/host token is passed to connection open and `pg_try_advisory_lock`. Cancellation already requested, or an `OperationCanceledException` attributable to that token, yields the existing cancelled terminal and 130 unless a higher-precedence fact exists. A false scalar result alone means Busy/3. Open, command, result-shape, or database exceptions mean Infrastructure/5 and attempt the existing failed terminal with predefined safe text.

After acquisition, protected work uses a linked token containing request/host cancellation and ownership loss. Finally always attempts lease cleanup. Cleanup uses its own bounded token so caller cancellation cannot skip unlock. Unlock/disposal/loss failures contribute infrastructure facts to the existing accumulator; they do not rewrite a terminal already flushed. Block 23's precedence and block 30's later anomaly classification remain authoritative.

### Keep unit seams narrow and real-PostgreSQL evidence in this block

Use a small lock boundary consumed by the executor and an internal connection/session factory/command seam around Npgsql. Pure unit tests do not mock PostgreSQL semantics; they verify executor ordering and typed mapping with a fake acquisition boundary, and lease state transitions with deterministic connection/command/monitor seams.

Add `[TestCategory("Integration")]` tests against a real configured PostgreSQL database for:
- exact key acquisition on one dedicated session and non-blocking false on a second session in the same database;
- explicit unlock followed by acquisition by the second session;
- owner connection close/disposal releasing the lock;
- cancellation/error cleanup leaving no reusable locked pooled session where the harness can induce it;
- same key used in separately created databases not contending, only when the environment grants database-creation rights (otherwise keep this assertion at the PostgreSQL contract level and do not make the suite privilege-dependent).

These tests use independent connections/data sources but remain in-process. Starting two worker processes, success/failure/cancellation/crash release combinations, controller observation, and coordinator return-to-idle remain exclusively block 32.

## Risks / Trade-offs

- [A network partition can release or obscure the server session before the worker detects loss] → Probe every five seconds, link loss into executor cancellation, stop after observation, and document that schema-free advisory locking is not fencing.
- [Ambiguous unlock could return a still-locked connector to the pool] → Require a true unlock result and clear the associated pool before disposal on ambiguity.
- [`ClearPool` closes unrelated pooled connectors] → Use it only on exceptional ambiguous release; normal release confirms unlock and preserves pooling.
- [A different version/key allows mixed deployments to overlap] → Treat the exact derivation as a compatibility contract and require overlap-safe migration before any version change.
- [An unrelated same-database application can intentionally or accidentally use the key] → Publish the label/value and reserve it for the Immich ReverseGeo run-exclusion domain.
- [Busy is represented by a failed terminal] → Preserve block 23's typed Busy fact and zero domain failed count so exit 3, not asset-failure accounting, carries the cause.

## Migration Plan

1. Re-read the applied block-15, block-20, block-21, block-22, and block-23 APIs and the finalized worker composition; stop rather than duplicate executor, reporter, terminal, outcome, host, stream, or disposal ownership.
2. Add the stable key constants, typed acquisition/lease boundary, and deterministic unit seams.
3. Wire the gate immediately after `run-started` and before every domain/heavy operation; map Busy/3, cancellation/130, and infrastructure/5 through existing block-23 facts.
4. Add loss monitoring, linked cancellation, explicit unlock verification, ambiguous-release pool sanitation, and exactly-once async disposal.
5. Add focused unit tests and real-PostgreSQL `Integration` tests, then run normal and explicit integration suites.
6. Rollback removes the worker collaborator and wiring only; there is no database migration or persistent lock object to remove.

## Audit Reconciliation

Advisory-lock Busy is canonical: after `run-started`, it emits no eligibility event and commits the reserved failed Busy terminal with all four terminal counts exactly zero (`ProcessedCount=0`, `UpdatedCount=0`, `SkippedCount=0`, `FailedCount=0`). It performs no executor or producer work and retains exit code 3 as evidence, not a domain failed-asset count.

