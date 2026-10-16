# Exact native entry-removal prerequisite

This is a bounded prerequisite for T021–T023, T107 and T111. T105's non-destructive overlapping-generation gate was accepted at `ac70d64`. The recursive executor, quarantine protocol and public Execute remain unfinished; these tasks stay unchecked.

## Implemented boundary

The internal `IPhysicalStoreDeletionFileSystem` removes one exact canonical regular single-link file or one exact empty directory relative to a provider-owned held parent. It requires the expected native identity, same provider/volume, no-follow entry selection and stable parent/name semantics. Success requires positive absence after the native mutation and handle close. Missing, replaced, linked/reparse, aliased, wrong-kind, foreign/disposed-parent and nonempty-directory inputs refuse. No recursive walk or path fallback is exposed.

Unix reuses the existing exact control-file removal kernel, with same-volume and post-removal profile checks for payload files. Empty directories use `unlinkat(AT_REMOVEDIR)` with the platform-specific flag. Windows reuses the relative DELETE-capable file kernel; directories use a no-follow relative open, actual removal-handle identity verification and one native disposition call. Both preserve the existing control-publication contract.

The caller must later retain complete root/all-state ownership and freshly verified eligibility/containment. Pre/post identity checks coordinate participating owners; they do not provide an atomic identity compare-and-delete against arbitrary external filesystem writers. Exceptions can follow completed mutation and must never erase partial facts.

## Local evidence

- Final required native manifest: 12/12 passed, no skips, with the strict exact-name/multiplicity verifier.
- Full Store regression: 953 passed, zero failed, 63 explicit platform skips, 1,016 total. All 1,003 previous IDs/names/outcomes are unchanged; 13 new passes comprise the native-removal cases and the separate T041 basename test.
- Core builds for net8/net9/net10: zero warnings/errors.
- Compiled negative control substitutes the current file identity for the caller's expected identity. The replacement-preservation regression fails because removal no longer refuses. Source is restored byte-for-byte; restored and final required suites pass.
- Root reviewed the Unix implementation and corrected its missing namespace import and a helper rename that broke other partial-class callers. Independent review approved the Windows/shared source and the final test correction. Windows refuses textual held-parent rename; Unix permits it and verifies removals stay with the held parent while replacement-path entries survive.

Saved TRX, source hashes, negative-control output, full-regression ID reconciliation and reviews are retained under the delivery workspace's `catalog-runtime-integration-v1/deletion-readiness/` and `foundational-identity-audit/deletion-readiness/` directories. These are local macOS results; they do not establish Windows/Linux hosted acceptance.

## Hosted qualification and limits

Every native CI lane now selects `PhysicalStoreDeletionTests` and requires the exact 12-case manifest with no skips. The existing guarded-writer manifest requires 21 cases, retaining all earlier 20 requirements plus T041's basename-change regression. Other native gates are retained.

The preceding exact-`c094b46` run, Validate38158737072, is terminal cancelled and unaccepted: Intel exceeded its 30-minute whole-job limit, skipping the final multiroot56/writer20 gates. Four other native lanes passed; full build-test passed 2,416 tests with zero failures and 68 platform skips. Intel's next whole-job budget is 45 minutes; Windows remains 30 and other lanes 20. Test hang limits and assertions are unchanged. The later exact `36d1ced` / Validate38163624111 run completed failure: four native lanes passed all12 removal cases, while Windows passed the repaired writer21 but failed two empty-directory cases with sharing status `0xC0000043`. Root and independent diagnosis found canonical revalidation reopening a normal protective target handle after DELETE access was already held. The correction performs canonical revalidation before opening the removal handle, validates the opened identity and held parent/profile, closes after disposition, and retains positive-absence verification. Removal denies delete sharing to pin its target name; publication and ordinary observer sharing are unchanged. Local native12 passes after the correction; actual Windows runtime qualification is pending. No failed/skipped gate is accepted.

Direct provider/volume mismatch coverage and a deterministic post-prepare Windows DELETE-handle replacement test remain coverage limits. Source enforces those checks; existing replacement tests change entries before the call. Quarantine location/names, restart journal, fresh locked planning, bounded recursive traversal, partial-result reconciliation and complete driver qualification remain required. There is no production registration or public caller of this internal companion, and no owner support-policy choice is inferred.
