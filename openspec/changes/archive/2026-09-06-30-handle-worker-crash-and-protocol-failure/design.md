## Context

See `proposal.md` for motivation. Finalized blocks 15/17/21–23 already own protocol semantics and managed exit mapping. Block 25 owns process creation, stream pumps, ready/execute handshake, accepted-event delivery, raw completion, and the 65,536-byte stderr tail. Block 27 owns validation at the state boundary and normal terminal projection. Blocks 28–29 own exact-session Stop, grace/kill, shutdown admission closure, drainage, disposal, and coordinator attachment. This change consumes their facts; it does not move those responsibilities.

## Goals / Non-Goals

**Goals:**
- Reconcile all raw session facts into one deterministic control-plane outcome.
- Preserve committed terminal authority and block-23 precedence.
- Guarantee one UI terminal mutation, activity cleanup, callback closure, and matching-handle release.
- Produce bounded actionable diagnostics without exposing arbitrary stderr or secrets.
- Specify deterministic unit and block-26 fixture matrices.

**Non-Goals:**
- Redefining protocol envelopes, codecs, validators, exit values, launcher pumps, cancellation deadlines, shutdown budgets, or worker fixture modes.
- Adding retries, persistence, telemetry transport, PostgreSQL locking, a busy terminal type, or block-31 implementation.
- Treating PID or a raw exit code as run identity or domain authority.

## Decisions

### 1. Add one pure evidence classifier and one side-effecting finalizer

The classifier consumes an immutable snapshot only after raw completion settles: command-resolution/start result; last lifecycle phase; execute/run-started evidence; first protocol and sink/projection faults; terminal and projection receipt; raw exit; stdout/stderr finality; stderr-tail metadata; exact-session Stop/shutdown/fault-containment intent; cancel transport; grace; and kill result. It returns:

- authoritative control-plane outcome: Completed, Cancelled, or Failed;
- source: committed terminal or synthesized control-plane finality;
- stable primary category and lifecycle phase;
- optional supplementary anomalies;
- safe diagnostic tokens and cleanup instructions;
- no retry directive.

The finalizer applies that decision through one run-scoped gate and performs ordered cleanup. Keeping classification pure makes the precedence matrix exhaustive; keeping side effects separate prevents a late observation from mutating state twice.

Alternative: classify incrementally in each pump/cancellation callback. Rejected because exit may precede trailing stdout/diagnostics, and multiple owners could race to terminalize.

### 2. Use an explicit session state machine

Transport/control states are:

1. `Admitted`: request/adapter armed; no process ownership yet.
2. `Resolving`: command resolution is in progress.
3. `Starting`: command resolved and start attempted.
4. `PreReady`: process and pumps owned; waiting for accepted ready.
5. `Ready`: ready accepted; execute is being written/flushed.
6. `Accepted`: execute flush succeeded; run events are allowed.
7. `Draining`: exit observed or Stop/shutdown/fault containment is active; callbacks may still deliver already-read bytes until pump finality.
8. `EvidenceFinal`: exit and both stream pumps are final, or a typed no-process command/start failure is final.
9. `Released`: callback closure, bridge/session cleanup, and exact coordinator-handle detachment are complete.

UI commitment is an independent monotonic dimension: `Uncommitted` → `TerminalValidated` → `Committed`, and may reach `Committed` before transport reaches `EvidenceFinal`. The final classifier never delays or rolls back normal terminal projection; it waits for transport finality only to calculate supplementary anomalies and perform release. Transitions in each dimension are monotonic. Protocol/sink faults latch first-fault evidence and suppress invalid/further callbacks according to block 25, but do not skip drainage. Stop and shutdown annotate the same state; they do not create a parallel terminal path. Any event after callback closure is rejected before projection. A stale cleanup token cannot advance another run.

Alternative: make every fault an immediate terminal. Rejected because it loses trailing terminal evidence and can deadlock the child by abandoning drains.

### 3. Terminal authority requires a committed projection receipt

A syntactically valid terminal frame is not by itself UI authority. Authority requires protocol/bridge acceptance plus an idempotent state-finalization receipt of `Committed` (or `AlreadyCommittedSameTerminal`). Block 27 remains the normal owner. The atomic gate durably records its winner before observable terminal mutations and returns one of: committed, already committed same terminal, rejected before mutation, or response indeterminate. A Preview-valid Completed/Cancelled/Failed terminal whose projection definitely failed before receipt claim is submitted once through that gate by the finalizer; semantic validator/correlation rejection is never resubmitted; the same terminal commits unless another recorded outcome already won. For an indeterminate response, the classifier queries the durable receipt: a recorded terminal is authoritative, while no receipt proves the atomic operation did not mutate and the classifier commits Failed with category `projection-failure`. It never replays stdout or issues a blind second mutation.

Once committed, later exit/input/output/disposal/shutdown contradictions are supplementary anomalies. They may add one bounded diagnostic through a post-terminal anomaly path, but cannot change outcome, counters, fatal count, summary, completion timestamp, or activities.

Alternative: let any accepted terminal dominate. Rejected because a sink can fail before or during projection, leaving UI state incomplete.

### 4. Apply a closed classification order

The classifier uses this order:

1. If a terminal is committed, use it and calculate only supplementary consistency anomalies.
2. If a validated terminal exists but did not commit, resolve it through the shared gate; if it cannot be resolved safely, fail as projection failure.
3. If no process started, classify start/descriptor failure as Failed.
4. Classify pre-ready timeout/EOF/exit, ready callback rejection, or execute write/flush failure as Failed.
5. Classify protocol/bridge faults by the first typed category: framing/encoding/size, unknown/incompatible discriminator, readiness, sequence, correlation, lifecycle/progress/terminal consistency, or activity cardinality.
6. Classify output transport/emitter or event-sink failure before generic infrastructure, mirroring block-23 code 6 over 5.
7. With no terminal and exact-session termination intent, classify an orderly managed cancellation/shutdown before run-started as Cancelled. After acceptance, a managed 130 or accepted tree kill after grace is Cancelled with an anomaly; kill rejection is Failed and ownership remains until settled.
8. Otherwise classify absent terminal, crash, unmapped exit, or inconsistent mapped exit as Failed.

For orderly worker facts, supplementary exit consistency uses block-23 precedence unchanged: 6 > 5 > 2 > 3 > 4 > 130 > 0. Abrupt platform termination remains raw and cannot be legitimized because its numeric value happens to match one of those integers.

### 5. Fault containment reuses the one process owner

A readiness, protocol, sink, projection, or output fault can leave a worker alive even though no terminal can be trusted. Block 30 invokes an internal `FaultContainment` reason on the exact block-28 lifecycle owner. That reason joins the same one-operation gate, fixed deadline, whole-tree kill, exit/drain, and disposal mechanics; it creates no second timer or owner and is not user Stop or host shutdown. After protocol/output safety is lost it closes input as owned cleanup but sends no cancel or other protocol frame. An accepted kill under fault containment remains Failed; only an earlier exact-session Stop/shutdown intent can authorize Cancelled. Kill rejection retains ownership and fails visibly.

Alternative: wait indefinitely for natural exit. Rejected because waiting for natural exit alone can leave ownership stuck. Applied block 28 now owns escalation; containment joins that exact owner rather than adding another kill path.

### 6. Cancellation and shutdown use intent plus evidence

A valid Cancelled terminal always wins. Without one, exact-session controller intent is required before synthesizing Cancelled:

- host shutdown or Stop before run-started plus an orderly managed 130 is Cancelled;
- accepted tree kill after the one block-28 grace deadline is Cancelled with `forced-termination` warning when Stop/shutdown targeted that exact session;
- kill failure, unrelated crash, stale Stop, or no matching intent is Failed.

This avoids reporting an unresponsive user-stopped process as an unexplained crash while still exposing forced termination. It also prevents raw 130 from proving cancellation by itself.

Alternative: make every missing terminal Failed. Rejected because block 23 explicitly permits no-terminal shutdown before request acceptance and block 28 produces authoritative exact-session kill facts. Alternative: make every 130 Cancelled. Rejected because raw platform codes are ambiguous.

### 7. Busy code 3 remains a failed terminal, not a non-error

Block 23 reserves code 3 for future typed advisory-lock contention and requires the existing Failed terminal after run-started and before eligibility/heavy work. Therefore:

- a committed Failed terminal plus code 3 is consistent and remains existing failed/fatal UI behavior;
- code 3 is advisory process evidence, not a second terminal and not proof of busy without the terminal;
- code 3 does not schedule retry;
- this change neither acquires a lock nor defines SQL/key/scope/placement.

Calling busy a UI warning or non-error would contradict finalized blocks 23 and 27 and would require a separate cross-block revision. This change stays aligned with future block 31 by consuming only the reserved fact.

### 8. Diagnostics are typed first and stderr-derived only through a safe renderer

The launcher retains its exact final 65,536 stderr bytes, total count, truncation flag, and replacement-safe decoding. The classifier's primary UI message is built from a closed category, safe lifecycle phase, terminal/exit consistency token, and predefined operator action. It never includes raw arguments, request frames, JSON, exception text, stack traces, configuration, SQL, connection strings, credentials, or environment values.

Arbitrary stderr is not copied verbatim to `LastError` or UI logs. If implementation exposes an excerpt, a dedicated renderer must apply a separately tested display bound, strip non-layout controls, redact URI userinfo, authorization/bearer material, connection strings, and secret-like key/value pairs, then mark truncation/redaction. The original bounded tail remains an in-memory raw observation only for existing owner-controlled diagnostics and disposal.

Alternative: append all retained stderr. Rejected because bounded data can still contain secrets and can duplicate the safe final summary.

### 9. Finalization and cleanup have one order

Normal terminal projection may claim the receipt before process exit. For evidence classification and abnormal finalization of the exact admitted run:

1. finish process exit plus stdout/stderr pumps, or settle a typed no-process failure;
2. freeze evidence and classify;
3. enter the shared finalization gate;
4. if no terminal was committed, apply one synthesized Failed/Cancelled UI terminal mutation through the adapter's narrow abnormal-finality surface;
5. close all adapter and bridge activities, append one safe diagnostic/summary in adapter-defined order, and close callback acceptance;
6. complete launcher/cancellation/shutdown disposal while retaining ownership on kill failure;
7. release only the matching coordinator handle;
8. reject/ignore every late, duplicate, stale, cross-run, or post-finality event.

Normal terminal projection already performs its terminal UI mutation and activity cleanup; abnormal finalization must detect that receipt and skip duplicate work. Coordinator release is never in a `finally` that can run before state finality.

### 10. Deterministic verification uses two layers

Pure/seam tests exhaust classification and race behavior, including cases block 26 cannot produce: OS-start failure, ready timeout, ready callback rejection, execute write/flush failure, event-sink/projection failures at pre-commit/indeterminate/post-commit points, output finality, kill rejection, and host-shutdown races.

Existing block-26 modes remain unchanged and cover:

| Mode/evidence | Expected authority |
|---|---|
| ready | ready, execute capture, minimal valid no-work terminal, exit 0; committed Completed |
| success, no-work | committed Completed terminal plus exit 0 |
| pre-ready-crash | synthesized Failed/startup-crash |
| post-ready-crash | synthesized Failed/missing-terminal |
| malformed | synthesized Failed/malformed-frame |
| oversized | synthesized Failed/oversized-frame |
| unknown-message | synthesized Failed/unknown-or-incompatible |
| invalid-sequence | synthesized Failed/sequence |
| terminal/exit mismatch | terminal unchanged; one anomaly |
| stderr-flood | terminal unchanged; bounded tail metadata |
| mapped exits 0/2/3/4/5/6/130 without sufficient terminal evidence | combine with lifecycle; never infer authority from number alone |
| unmapped exit | synthesized Failed/unmapped-exit |
| cooperative-cancel | committed Cancelled plus 130; no anomaly |
| unresponsive then accepted tree kill for exact Stop | synthesized Cancelled plus forced-termination warning |

All tests use positive handshakes, gates, fake time, explicit EOF/exit, complete drainage, and unconditional tree reaping. They assert one UI terminal mutation, no activity residue, callback closure, matching-handle release, and no retry.

## Risks / Trade-offs

- **[Risk] A projection sink faults after partial mutation** → Mitigation: require the shared idempotent commit receipt and query it before any repair; never blindly replay a terminal.
- **[Risk] Waiting for finality delays visible failure** → Mitigation: expose nonterminal safe startup/stopping status while retaining ownership; terminalize only when evidence is complete.
- **[Risk] Forced kill reported as cancellation hides unresponsiveness** → Mitigation: retain Cancelled authority only for exact-session intent and add a forced-termination warning.
- **[Risk] Stderr redaction misses an unknown secret form** → Mitigation: typed summaries are primary and arbitrary stderr is not displayed by default.
- **[Risk] Release races a replacement run** → Mitigation: release by exact coordinator generation/handle only after finalization and cleanup.
- **[Risk] Future block 31 changes busy UX** → Mitigation: preserve current Failed-terminal semantics and isolate any future UX change as an explicit cross-block revision.

## Migration Plan

1. Add pure evidence/outcome contracts and an exhaustive classifier without changing launch or UI routing.
2. Add the shared run-finalization receipt and narrow abnormal-finality/anomaly surfaces around the existing block-27 adapter.
3. Compose launcher completion, block-28 Stop, and block-29 shutdown through one finalizer; preserve their ownership and deadlines.
4. Add deterministic pure/seam tests, then existing block-26 real-process matrix tests.
5. Run focused tests, `npm run test`, strict OpenSpec validation, and change-status review. Rollback removes the classifier/finalizer routing while leaving finalized raw observation owners unchanged.

## Audit Reconciliation

There is one exact-session internal deadline, started by whichever happens first: accepted Stop, host shutdown, or fault containment. It is the block-28 internal exact 10-second `TimeProvider` deadline, never a second timer. Classification must keep semantic rejection (a definite invalid/contradictory event), noncommit (no authoritative terminal commit), and indeterminate receipt (a terminal/projection attempt whose authoritative commit cannot be known) distinct; none may be silently upgraded to a committed terminal. The coordination and worker-event bridge capability contracts are modified to expose these bounded observations and finalization handoff without changing UI projection ownership.


## Applied prerequisite reconciliation

- Session completion contains an OS exit integer, not managed provenance. The child adapter never reconstructs `WorkerProcessExitFact` from that integer. The pure classifier accepts a separately owned managed fact only when such provenance exists; actual child cancellation without a terminal therefore requires an accepted exact-session grace kill. A raw 130 alone fails. A committed terminal may use its raw numeric pairing for supplementary consistency without promoting that number into domain authority.
- `WorkerProtocolFailure.Detail` adds non-wire readiness, progress, terminal, activity-cardinality, and missing-terminal evidence. It preserves existing codes, diagnostics, codec bytes, and validation ordering; classification never parses diagnostic prose.
- Normal and abnormal projection use the reporter's one in-memory receipt gate. An indeterminate response queries that gate; semantic rejection never authorizes replay.
- New child execution opts into an evidence-finality hold: physical exit and both pumps publish evidence before owned resource disposal. After a receipt exists, the finalizer releases the hold, joins disposal/callback closure, and allows exact coordinator release. Legacy raw-session clients retain their existing disposal behavior.
- `WorkerRunControlPlane` is explicitly callable and registered without replacing the current in-process executor. Backend selection remains a later change. One exact coordinator reservation precedes command resolution and prevents duplicate launch.
- Optional excerpt export redacts the entire excerpt if any structured delimiter or sensitive marker is present, including multiline continuations. This deliberately sacrifices fidelity for unknown key/value and payload forms. Primary UI diagnostics never use stderr.

- Post-start resource-access failures retain an owned session with typed setup-failure evidence; available streams still drain. Only failures before process creation are reported as no-process failures.
- Abnormal result timestamps clamp an end observation earlier than the accepted/admitted start to that start, so wall-clock regression or worker/host skew cannot strand a settled run before receipt claim. Cancellation deadlines continue using the original monotonic clock.
- Fault timing capture preserves the exact monotonic provider/timestamp if UTC observation fails and explicitly marks the UTC sentinel as unavailable. If monotonic capture itself fails, the bounded observation task records that failure without breaking the pumps; no replacement clock or invented deadline is used.
