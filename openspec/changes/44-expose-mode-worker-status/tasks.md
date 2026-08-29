## 1. Reconcile landed prerequisite contracts

- [ ] 1.1 Re-read the applied block 13, 24–30, and 40–43 source/tests and record the exact immutable deployment-mode snapshot, processing-worker coordinator/session observations, safe failure-summary boundary, and Web component render-test seam; stop rather than invent replacements if any prerequisite is absent.
- [ ] 1.2 Confirm the block-44 diff excludes block 45 composition-matrix work, block 46 image/process smoke tests, generalized worker protocol/jobs, block-50 generic diagnostics/arbitration, non-ProcessAssets page state, new settings/controls, protocol changes, and all PID/run/job-ID exposure.

## 2. Add the read-only Web status projection

- [ ] 2.1 Define an immutable process-wide ProcessAssets-focused status snapshot and read-only consumer interface containing only Standard/Web-only display mode, derived schedule policy, Idle/Starting/Running/Cancelling/Failed worker state, optional classifier-approved safe summary, and a monotonic presentation revision.
- [ ] 2.2 Initialize the singleton from block 40's already-resolved startup snapshot in Standard and Web-only composition without rereading the environment, touching `AppConfig`/`ConfigService`, or registering it in Run-once.
- [ ] 2.3 Adapt the authoritative processing-worker coordinator/session observations into the exhaustive state mapping, using internal exact-session identity only to reject stale updates and exposing no identity, PID, exit, protocol, command, exception, or raw stderr field.
- [ ] 2.4 Retain Failed after matching handle release, clear it atomically on the next worker admission, return Completed/Cancelled finality to Idle, and initialize a new Web-host process at Idle without durable persistence.
- [ ] 2.5 Publish one ordered notification only after each effective immutable snapshot change; make stale/idempotent transitions and unrelated `ProcessingState` counter/log/activity updates notification-free.

## 3. Render accessible Dashboard and navigation status

- [ ] 3.1 Add an always-visible read-only Dashboard status card, independent of database-stat loading, with exact mode/worker labels, Standard scheduling availability, the Web-only schedule-disabled/saved-values-retained/manual-run explanation, and no control.
- [ ] 3.2 Render a Failed alert using only the block-30-approved bounded operator summary and an optional Logs link; never bind the status detail to arbitrary `LastError`, exception, stderr, protocol, or process data.
- [ ] 3.3 Replace NavMenu's `IsRunning`/`LastError` status-dot inference with the shared worker snapshot and provide an accessible textual `Worker: <state>` equivalent rather than color/title alone.
- [ ] 3.4 Add stable polite status and failure alert semantics that do not reannounce an unchanged retained failure on unrelated rerenders, and correctly unsubscribe component handlers on disposal/reconnect.
- [ ] 3.5 Add responsive status-card/navigation classes, wrapping, state colors, and reduced-motion handling only in `src/ImmichReverseGeo.Web/wwwroot/app.css`, preserving readable text at the existing 900px/640px mobile breakpoints.

## 4. Add focused state and UI verification

- [ ] 4.1 Add MSTest mapping coverage for every landed raw phase and the exact Idle/Starting/Running/Cancelling/Failed labels, including startup, execute acceptance, cancellation, completion, orderly cancellation, authoritative failure, next-admission clearing, and exhaustive handling of unknown future phases.
- [ ] 4.2 Add concurrency/correlation tests proving stale and duplicate observations cannot overwrite a replacement session, snapshots are visible before notifications, and effective changes notify exactly once.
- [ ] 4.3 Add tests proving local schedule checks, detector no-work results with no launch, database-stat refresh, unrelated processing updates, `CoordinateLookup`, and `CacheMutation` do not fabricate ProcessAssets card activity or lifecycle notifications; prove those jobs retain page-owned state and block-50 generic diagnostics remain separate with no PID/JobId UI.
- [ ] 4.4 Add safety tests proving the public snapshot and rendered UI contain no PID, run/job ID, exit value, protocol/frame, command/environment value, exception/stack, raw stderr, credential, token, or connection-string data and accept only the safe bounded failure summary.
- [ ] 4.5 Add focused Dashboard/NavMenu rendering tests for Standard and Web-only copy, all five worker states, Run-once's absence from Web composition without duplicating block 45's matrix, current-snapshot rendering after component/circuit replacement, semantic live/alert markup, accessible non-color labels, handler cleanup, and responsive class hooks.

## 5. Update concise operator documentation

- [ ] 5.1 Update `docs/website/using-the-app.md` with the Dashboard mode/schedule/worker fields, exact labels, Failed troubleshooting direction, and the fact that Run-once has no UI.
- [ ] 5.2 Update `docs/website/configuration.md` and `docs/website/troubleshooting.md` only as needed to explain immutable startup mode, Web-only's retained saved schedule/manual-run behavior, reconnect versus host-restart status behavior, and safe failure detail boundaries; defer comprehensive mode trade-offs and release migration guidance to blocks 70/72.

## 6. Validate block 44

- [ ] 6.1 Run focused status/state/component tests and `npm run test` with the repository's normal Integration/Performance exclusions; run a docs build if documentation changed.
- [ ] 6.2 Run `openspec validate 44-expose-mode-worker-status --strict` and `openspec status --change 44-expose-mode-worker-status`, then review the final diff for block-44-only scope and report any prerequisite naming or restart-semantics ambiguity.
