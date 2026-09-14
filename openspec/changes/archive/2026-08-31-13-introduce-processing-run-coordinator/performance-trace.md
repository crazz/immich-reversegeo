# Change 13 Loop Performance Trace

## Outcome

Change 13 completed successfully with 24/24 tasks, both independent pre-audits approved, Brooks Health 100/100, implementation CI success, synced main specs, and archive CI success.

- Start: 2026-08-31T00:10:47Z
- Brooks approval: 2026-08-31T12:03:53Z
- Archive CI completion: 2026-08-31T12:16:58Z
- Start-to-approval wall time: 11h 53m 06s
- Start-to-archive-CI wall time: 12h 06m 11s
- Post-approval commit/sync/archive/CI time: 13m 05s
- Implementation CI: 2m 28s
- Archive CI: 2m 21s
- CI execution total: 4m 49s

## Loop Volume

- Implementation-owner turns: 6
- Independent pre-audit rounds: 6
- Formal Brooks passes: 1
- Recorded 600-second wait ceilings before approval: 16
- Observed ceiling time: 2h 40m, 22.4% of start-to-approval wall time
- Builds before implementation commit, including formal review: 37
- Dedicated/focused/targeted test invocations before implementation commit, including formal review: 55
- Canonical full-suite invocations before implementation commit, including formal review: 11
- Final implementation commit: 21 files, 4,613 insertions, 265 deletions
- Final new exact-test support alone exceeded 3,000 lines; the implementation log added 524 lines

Test inventory grew as proof was repaired:

| Stable checkpoint | Dedicated | Focused | Canonical |
|---|---:|---:|---:|
| Owner turn 1 | 14 | 141 | 390 |
| Owner turn 2 | 55 | 182 | 431 |
| Owner turn 3 | 78 | 205 | 454 |
| Owner turn 4 | 80 | 207 | 456 |
| Owner turn 5 | 86 | 213 | 462 |
| Owner turn 6 / final | 89 | 216 | 465 |

The individual local commands were fast: final build was 1.34s real, final dedicated tests 1.07s, final focused tests 4.65s, and final canonical tests 27.84s. Even eleven canonical runs represent minutes, not hours. The dominant cost was reasoning, implementation churn, proof expansion, repeated read-only audits, and waiting for long owner/reviewer turns.

## Where Time Went

### 1. The first completion was declared before the proof map was executable

Turn 1 marked 24/24 while the scenario/task map named methods that did not exist and broad requirements were represented by wrappers, source inspection, partial counts, or retained tests that did not exercise the coordinator contract directly. The first test audit therefore reopened most of the change.

Impact:

- Large turn-2 test expansion from 14 to 55 dedicated cases.
- Rework of the task map and evidence chronology.
- Extra focused and canonical runs whose passing counts did not prove the requested contracts.

### 2. The concurrency model was implicit instead of written as a state machine

The preflight listed cancellation and cleanup boundaries, but it did not define explicit per-handle states, linearization points, forbidden transitions, or the rule that external callbacks never execute while a state gate is held.

This allowed three production defects to appear sequentially:

1. Shutdown could cancel after reservation but before preparation/dispatch ownership settled.
2. Cancellation and CTS disposal could overlap.
3. Serializing both with a monitor called cancellation callbacks while holding the gate, creating reentrancy/deadlock and synchronous cleanup risks.

Turn 6 finally introduced the correct model: reserve state under a short lock, execute external Cancel/Dispose outside it, represent cancel-in-progress and deferred disposal explicitly, and await one stable disposal completion before releasing admission.

Impact:

- Owner turns 4–6 primarily refined the same cancellation/disposal state machine.
- Three additional pre-audit cycles.
- Repeated rebuild/focused/canonical batches after each production change.

### 3. Exactness requirements were discovered incrementally

Auditors repeatedly found partial assertions after broad behavior was already green:

- prefix arrays instead of complete terminal/release/dispose arrays;
- sequential examples labeled as a concurrent race;
- count-only log checks instead of exact ordered level/message/exception tuples;
- setup failure tests that proved the first failure but not complete retrigger cleanup;
- scheduled-token cancellability without exact owned/supplied-token linkage;
- Dashboard source grep or helper invocation instead of the rendered callback;
- inferred hosted stop order instead of a real Host lifecycle execution;
- infinite Monitor waits despite bounded outer awaits;
- external-gate evidence that named final filters without recording literal commands.

Impact:

- Dedicated inventory expanded by 75 cases after the initial completion claim.
- High reviewer context cost across an eventually 4,600-line change.

### 4. Long owner and audit turns hid progress

Sixteen 600-second waits were observed. These waits overlapped active agent work, so they are not pure idle time, but they made 2h40 of wall time opaque. The same owner constraint was preserved correctly, yet each turn bundled production, many direct tests, evidence repair, full verification, and narrative updates.

Impact:

- Slow feedback on whether a turn was coding, debugging, running tests, or updating evidence.
- No opportunity to catch a wrong concurrency model until the whole turn completed.

### 5. Full-suite execution was not the bottleneck

CI consumed 4m49 total. The final local build plus dedicated, focused, and canonical commands consumed about 35 seconds. Repeating the canonical suite eleven times was wasteful, but eliminating every redundant full run would save minutes, not the roughly twelve-hour wall time.

The main optimization target is reducing remediation cycles, not making the test runner faster.

## What Worked

- The same implementation owner retained full production/test responsibility.
- Independent production and test-contract auditors found real races and false proof claims before commit.
- Deterministic causal gates exposed the pre-dispatch shutdown race that ordinary passing tests missed.
- Formal Brooks review required only one pass after both pre-audits finally approved.
- Explicit-path staging preserved all unrelated workspace state.
- Implementation and archive CI both passed on their exact commits.
- The final coordinator model is substantially stronger because the loop did not waive concurrency or proof defects.

## Improvements for the Next Loop

### Priority 1: Require a machine-checkable proof manifest before any 24/24 claim

Create one structured artifact with:

- every spec scenario ID;
- every task ID;
- exact direct TestMethod names;
- assertion clauses per method;
- external command gates kept in a separate typed section.

Automate these checks before the first audit:

- all referenced methods exist and are independently executable;
- no TestMethod invokes another TestMethod;
- no scenario relies only on source grep, wrapper return discarding, or counts;
- task and scenario key sets have no gaps or overlaps;
- external commands are literal and reproducible.

Expected effect: catch the turn-1 false proof map locally and avoid most of turns 2–3.

### Priority 2: Make a concurrency state-transition table a pre-coding gate

For every owned resource, record:

- states and legal transitions;
- exact linearization point;
- which transitions happen under the gate;
- which callbacks execute outside it;
- cancellation/disposal ownership and completion signal;
- stale-handle identity rule;
- primary-versus-cleanup failure precedence;
- shutdown interaction at reservation, preparation, execution, terminal projection, and cleanup.

For Change 13, this table should have prohibited external Cancel/Dispose under the gate and required explicit cancel-in-progress/deferred-dispose state before implementation.

Expected effect: avoid the production redesign in turns 4–6.

### Priority 3: Add a short design-concurrency audit before implementation

Use one read-only reviewer on the proposed state machine and dependency graph before production code is written. This is not a second implementation owner. The reviewer should approve:

- linearization points;
- callback-under-lock policy;
- exact resource lifetime;
- host start/stop ordering;
- failure precedence.

Expected effect: move race discovery from late executable proof into the cheap design stage.

### Priority 4: Define “exact proof” templates up front

Every concurrency test should use a standard assertion template:

1. exact pre-state;
2. exact operation array in causal order;
3. exact request/token/result/reference identities;
4. exact event and logger arrays;
5. exact cancel/dispose/arm/dispatch counts;
6. no extras;
7. an intervening post-cleanup action;
8. frozen-state reassertion.

Every wait must be causally gated and directly bounded, including synchronous Monitor or event waits.

Expected effect: prevent repeated findings about prefixes, counts, vacuous immutability, and unbounded inner waits.

### Priority 5: Use staged verification, not repeated broad verification during unstable design

Recommended order:

1. build plus the single failing direct test;
2. complete direct class;
3. dedicated matrix;
4. focused regression;
5. canonical suite only after production/test edits stabilize;
6. independent audits;
7. one remediation batch, then one final focused/canonical batch;
8. formal review.

Passing counts must be invalidated explicitly after every production/test edit, but evidence-only edits should not force unnecessary reruns.

Expected effect: reduce canonical runs from 11 toward 3–4. This saves only minutes directly, but also simplifies evidence chronology.

### Priority 6: Split giant proof surfaces by failure story

The 1,810-line coordinator turn-2 test file increased review cost. Use focused files such as:

- admission/preparation;
- cancellation/disposal;
- terminal/infrastructure failures;
- shutdown/host lifecycle;
- Dashboard/DI boundaries.

Keep shared fixtures test-only and ensure mapped methods still assert the coordinator result directly.

Expected effect: faster independent review, less missed coverage, and smaller remediation diffs.

### Priority 7: Improve long-turn observability

For a single-owner change of this size, plan explicit same-owner checkpoints:

- state model complete;
- production compiles;
- direct matrix complete;
- retained regression complete;
- final evidence complete.

The owner should update the durable implementation log at each checkpoint. A parent can then report useful progress without polling or duplicating work.

Expected effect: reduce opaque 600-second waits and make genuine blockers distinguishable from normal long execution.

## Target for a Comparable Future Change

A realistic target, without weakening any quality gate:

- 2–3 owner turns instead of 6;
- 2 pre-audit rounds instead of 6;
- 1 Brooks pass;
- 3–4 canonical runs instead of 11;
- under 4 hours start-to-archive-CI for a similar change.

The largest gain comes from executable proof mapping and an explicit state machine before coding. Test or CI speed improvements alone cannot achieve this target.
