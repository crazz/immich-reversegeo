# Change 14 Optimized-Loop Performance Trace

## Result through archive preparation

Change 14 completed implementation, exact verification, independent audits, formal Brooks approval, implementation commit/CI, main-spec synchronization, and archive movement successfully. Archive commit/CI is the only remaining lifecycle gate.

- Start: 2026-08-31T13:52:20Z
- Pre-code design approval: 2026-08-31T15:53:11Z
- Initial implementation completion: 2026-08-31T16:29:38Z
- Final independent audit approval: 2026-08-31T22:03:41Z
- Formal Brooks approval: 2026-08-31T23:18:09Z
- Implementation CI completion: 2026-08-31T23:23:58Z
- Archive move: 2026-08-31T23:28:35Z
- Start to Brooks approval: **9h 25m 49s**
- Start to implementation CI: **9h 31m 38s**
- Start to archive move: **9h 36m 15s**
- Final implementation CI: **2m 43s**

## Target versus actual

| Metric | Change 14 target | Change 14 actual | Change 13 baseline |
|---|---:|---:|---:|
| Same-owner turns | 2–3 | 12 | 6 |
| Pre-code proof-review rounds | 1 design gate | 4 | not separated |
| Final independent audit rounds | 2 | 8 | 6 |
| Formal Brooks passes | 1 | 2 | 1 |
| Local canonical runs | 1–2 | 8 | 11 |
| Start to approval | under 4h | 9h 25m 49s | 11h 53m 06s |
| 600s subagent wait ceilings | materially fewer | 38 | 16 |
| Additional confirmation timeout | 0 | 1 × 600s | 0 |
| Final implementation diff | constrained | 43,553 insertions / 336 deletions | 4,613 insertions / 265 deletions |

The workflow improved approval wall time by **2h 27m 17s (20.6%)** versus Change 13, and reduced local canonical runs from 11 to 8. It did not meet the four-hour, owner-turn, audit-round, or wait targets.

## Where time went

### 1. The pre-code proof manifest became a second implementation

The intended lightweight machine-checkable map expanded before code into exact method/file/type/signature bindings, 63 runtime cases, exact calls/effects/events/logs/results/tokens/edges, and no-extra contracts. Four owner/design-review cycles consumed **2h 00m 51s** before implementation began.

This prevented some Change-13 mistakes, notably the impossible no-city scenario and overstated retained coverage, but it shifted implementation risk into a large unexecuted specification that could still be internally wrong.

### 2. The first implementation proved the manifest shape, not its semantics

The initial 96-test suite passed while behavioral rows did not consume the manifest contracts. Audit round 1 correctly identified a coverage illusion: exact JSON existed, but direct tests asserted subsets and permissive fixture defaults allowed unexpected calls.

The remediation then built a contract engine, but its first version marked fields consumed through cardinality checks and used implementation-captured snapshots. This created multiple sources of truth and required another redesign.

### 3. Proof infrastructure dominated the change

The final commit contains 43,553 insertions. The embedded authority alone contains approximately 36,000 lines—more than seven times the entire Change-13 implementation diff. Reviewers had to audit:

- direct executor behavior;
- fixture fail-fast semantics;
- typed observation capture;
- contract parsing and exact-field consumption;
- JSON schema closure;
- external equivalence and hashes;
- method/case binding;
- transitive IL analysis;
- coverage partitioning;
- mutation sentinels.

The proof system became a product with its own defects: captured-output authority, unconsumed fields, permissive unknown fields, self-certified token/result identities, global state leaks, incomplete filesystem detection, empty scenario partitions, and cross-method IL contamination.

### 4. Review findings were sequential consequences of the proof architecture

Independent audit rounds uncovered layers in this order:

1. permissive fixture, global order, weak causal gates, source/filesystem proof;
2. duplicated/cardinality contract authority and self-certified tokens/dispositions;
3. S16 drift, incomplete filesystem policy, raw-byte hash, static state, session/foreign-token semantics;
4. unconsumed result field, unknown fields, source-text equivalence, S16 constants;
5. behavioral/structural unused fields;
6. approval;
7. post-Brooks nullable result-reference issue;
8. approval.

Formal Brooks then found empty enforceable requirement partitions and root-contaminated IL traversal. These were not isolated missing assertions; they were failure modes of the meta-verification layer itself.

### 5. Commands were still not the bottleneck

The final stable sequence took approximately:

- Build: under 1 second wall
- Change14 104/104: under 1 second
- Focused 111/111: about 1 second
- Canonical 539/539: about 27 seconds
- Architecture 4/4: under 1 second
- Equivalence and strict validation: under 1 second each

Eight canonical runs cost only several minutes. The dominant cost was agent reasoning, very large proof diffs, repeated adversarial review, and **38 × 600-second** opaque subagent wait ceilings. Including the confirmation timeout, observed ceiling windows totaled **6h 30m**, overlapping active work rather than representing pure idle time.

## What improved over Change 13

- The impossible country/no-city scenario was corrected before test implementation.
- Retained-proof gaps such as skipped snapshot mutation, negative clamp, pre-count cancellation, and cross-asset ordering were identified before coding.
- The first implementation owner needed only one implementation turn to reach a green suite.
- Local canonical runs fell from 11 to 8.
- Brooks ultimately reached 100/100 with zero production changes.
- Approval wall time fell by 20.6% despite a test surface roughly nine times larger by inserted lines.

## What must change next

### 1. Keep the pre-code manifest lightweight

Before coding, record only:

- requirement/scenario/task IDs;
- proof kind;
- planned direct method or external gate;
- deterministic seam/gate needed;
- core identity/order/no-extra clauses;
- state and failure transition table.

Do **not** encode every event, message, effect, token, edge, file, signature, and assertion before executable observations exist. Cap the pre-code manifest at roughly 500 lines and one review/correction cycle.

### 2. Make direct typed tests the behavioral authority

Avoid a separate 36,000-line JSON oracle. Define each case once as compiled typed test data next to its direct test. The case object should contain input, exact expected observation, and scenario/task IDs; the test executes the executor and consumes the same object.

Generate the small OpenSpec coverage map from those compiled case definitions. Do not copy behavior into a second JSON registry or capture expected output from the implementation.

### 3. Add one vertical-slice implementation checkpoint

Revised owner sequence:

1. **Owner turn 1:** state/failure model, lightweight coverage map, raw fail-fast fixture, and four representative executable cases: success, cancellation, reporter failure, and concurrent completion.
2. Independent design/test gate reviews the actual fixture and representative assertions once.
3. **Owner turn 2:** bulk remaining matrix using the approved pattern; one final dedicated/focused/canonical batch.
4. Final audit round 1.
5. **Owner turn 3:** one consolidated remediation batch if needed; one final canonical batch.
6. Final audit round 2, then Brooks.

This catches permissive seams, self-certified tokens, global ordering, and incomplete exact assertions against executable code rather than speculative JSON.

### 4. Give the first review a fail-closed proof checklist

The first executable checkpoint must explicitly reject:

- fields declared but not consumed;
- expected values derived from actual observations;
- implementation-captured golden snapshots;
- permissive default collaborators;
- literal booleans standing in for observed tokens or references;
- disposition counts without asset identity;
- global order under concurrency;
- unknown authority fields;
- empty scenario/task partitions;
- source/filesystem behavioral proof;
- per-method IL traversal contaminated by neighbors;
- static test correlation state without cleanup.

These checks should happen once, before multiplying the pattern across all scenarios.

### 5. Use a coding-optimized owner model

Use the configured `worker` route for the same primary implementation owner and reserve `deep`/`review` for design and adversarial verification. Change 14 used a deep reasoning owner for large mechanical test expansion, contributing to long opaque turns.

### 6. Enforce a proof-complexity budget

For a test-only characterization change:

- proof infrastructure should not exceed the direct behavioral test surface;
- one semantic source of truth;
- no generated/captured oracle unless generation is reviewed and deterministic;
- no meta-verification system larger than the runtime component being characterized;
- if the pre-code proof gate rejects twice, simplify the proof architecture rather than add another metadata layer.

## Revised realistic target

For a comparable test-only matrix:

- 2–3 owner turns;
- one pre-code executable-pattern gate;
- two final audit rounds;
- 1–2 local canonical runs plus implementation/archive CI;
- under four hours;
- a proof map under 500 lines and no separate giant behavioral oracle.

The optimized order was directionally useful, but the machine-checkable manifest was placed too early and given too much semantic responsibility. The next loop should front-load **one executable vertical slice**, not a complete parallel specification of all tests.
