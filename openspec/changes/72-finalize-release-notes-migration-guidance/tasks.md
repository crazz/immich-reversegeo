## 1. Build the release evidence gate

- [ ] 1.1 Re-read landed blocks 1–71, source/tests, final block-70 public deployment guide, block-71 maintainer guide, release workflows, and all three release seams; record drift or any unfinished authorized task as a release blocker rather than inferring behavior from planning. Treat blocks 62–64 as complete only when their no-go artifacts are finalized/validated, the retained full-eligibility behavior is evidenced, and negative inspection proves no rejected implementation or release claim exists—not when nonexistent build tasks are checked.
- [ ] 1.2 Create checklist evidence rows with owning block, exact bounded claim, source/test/doc link, command or CI URL/result, reviewer/date, release-candidate image digest/tag, and previous-image identity where applicable.
- [ ] 1.3 Link passing blocks 40–46 evidence for absent-only Standard, strict exact mode values and exit 2, startup-only selection, one neutral image/entrypoint, same-image workers, ports, and distinct writable `/config` and `/data` mounts.
- [ ] 1.4 Link passing blocks 47–56 evidence for Standard/Web-only UI/manual/Lookup/supported heavy Data behavior, worker arbitration/cancellation/finality, no heavy Web executor/geodata initialization, and direct same-process Run-once composition.
- [ ] 1.5 Link passing blocks 65–68 and 71 protocol, coalescing, failure/finality, process-tree/stream/temp cleanup, selected soak, redaction, and memory-observation evidence; record sampler/profile limits and reject unsupported peak/RSS/total-memory claims.
- [ ] 1.6 Link the required block-69 `Docker Mode Integration` CI result invoking exactly `npm run test:docker-smoke`, with one build/immutable image ID across Standard, Web-only, Run-once, and invalid-mode cases plus mount, non-root, diagnostics, and cleanup evidence.
- [ ] 1.7 Confirm release wording uses no persisted watermark, reconciliation cadence, or NAS-specific control from rejected blocks 62–64 and does not rely on stale periodic-reconciliation text.

## 2. Verify upgrade and rollback compatibility

- [ ] 2.1 Start the exact release-candidate image using representative preexisting separate `/config` and `/data` volumes; verify retained settings and usable data, and record that no Immich schema change or Immich/config-data migration occurred.
- [ ] 2.2 Exercise Standard default, explicit Web-only, and Run-once operations on the release candidate, including Web-only saved-schedule retention/no scheduling and Run-once no-listener/one-attempt/no-retry behavior with exits 0/2/3/4/5/130 where applicable.
- [ ] 2.3 Stop new admissions and active work, stop the upgraded instance, start the named previous released image with the same tested volumes, and verify startup plus representative settings/data behavior without running old and new images concurrently.
- [ ] 2.4 Record exact rollback caveats for newer settings, cache formats, forward-created data, retained/partial Immich writes, mode configuration unsupported by the previous image, backups/snapshots, and every untested image-volume combination; make no zero-downtime or automatic-reversal claim.

## 3. Synchronize release communication

- [ ] 3.1 Update `CHANGELOG.md` and `docs/website/changelog.md` together under `Unreleased` (or explicit temporary version/date placeholders), cross-link them, and keep exact default/mode/compatibility/retry/rollback meaning aligned while using technical versus self-hoster language appropriately.
- [ ] 3.2 In both changelogs, state only evidence-backed facts about Standard, strict `IMMICH_REVERSEGEO_MODE` values, one image, separate volumes, no Immich schema/config-data migration, Web-only, Run-once exits/operations, and tested rollback; keep all private selectors and protocol controls out of public release copy.
- [ ] 3.3 Describe disposable-worker memory behavior only for verified Standard/Web-only heavy jobs, distinguish Run-once direct execution, and retain block-68 caveats with no universal peak/RSS/total-memory claim.
- [ ] 3.4 Keep the optional GADM academic/other non-commercial-use restriction visible in the user-facing entry and link the final deployment-mode and data-source/license pages.
- [ ] 3.5 Expand `docs/maintainer/RELEASE_CHECKLIST.md` with evidence links/results, image identities, all release blockers, semantic sync review, private-selector/no-go negative checks, placeholder replacement, and final publish/no-publish sign-off.

## 4. Run the final release gate

- [ ] 4.1 Run focused mode/composition/dependency-boundary tests and the normal default-exclusion suite; link the results from the checklist.
- [ ] 4.2 Run focused protocol/process failure/finality coverage and the explicitly selected block-68 memory soak/profile required for the intended claim; link retained bounded evidence and caveats.
- [ ] 4.3 Run `npm run docs:build`, verify release/deployment/data-source links and generated routes, and resolve any technical/public changelog mismatch.
- [ ] 4.4 Re-run or confirm the required block-69 `Docker Mode Integration` result against the exact release-candidate image and archive its bounded diagnostics/evidence link.
- [ ] 4.5 Replace version/date placeholders only after the actual release version/tag and date are known; recheck published image pull/start and previous-image rollback identities.
- [ ] 4.6 Run `openspec validate 72-finalize-release-notes-migration-guidance --strict`, confirm 4/4 status, and perform a scope review proving apply changed only the three release seams; do not publish while any blocker remains.
