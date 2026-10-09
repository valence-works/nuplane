# Configured authority resolution increment

Status: locally verified, hosted qualification pending. This is the metadata-only T025/T042 boundary, not T026 operation admission or verified Complete enrollment.

## Behavior

`PackageStoreAuthorityResolver` replays the exact configured locator through held native namespace, parent and child handles. Relative locators require an explicitly captured absolute base; neither path loses `.` or `..` through lexical normalization. Each visited directory's reserved control entry is inspected before consuming the next component or expanding an alias. A present but malformed, linked, unsupported, mismatched, incomplete or pending authority refuses instead of becoming Unenrolled.

Configured-root aliases may expand when their native identity and authority agree. A final package/member/content alias, including one followed only by a separator or `.`, refuses; archives must be stable single-link regular files. Absolute alias replay may traverse its namespace prefix outside the root, but an alias sourced inside an observed authority must finish inside it, and explicit parent steps cannot escape an authority already reached. Cycles and expansion/depth/evidence limits refuse.

`requiredRoot` describes the expected root: encountered candidates must match it and final classification must establish it. It does not invent a containment boundary before any reserved authority has actually been observed. A native regression reproduces the original over-refusal for `prefix-child/../packages` before the fix; absolute, captured-base relative and alias forms now pass, while `packages/../packages` still refuses after observing the root.

The disposable result retains native identity, directory name-profile, parent/name/child, alias text, control and ledger observations. Revalidation detects replacement, moved ancestry, new authority or changed membership. Disposal closes retained handles in reverse order and expires revalidation. No package/state/archive payload is read, no package/content directory is enumerated, and no lock, creation or write occurs. Membership verification may enumerate the reserved control directory to establish canonical names.

The membership fixture is deliberately a synthetic structural candidate. It does not demonstrate active/LKG validation, complete enrollment, a coordinated state writer or a positive runtime admission capability. Those remain prerequisites for the real two-composition before-first-read proof.

## Local root verification — 2026-10-09

- Initial all-TFM core build and twelve native tests passed.
- The added pre-authority parent-component regression failed behaviorally with the original resolver, at its premature root-containment check; the corrected thirteen-case native suite passed without skips.
- Final core build passed for `net8.0`, `net9.0` and `net10.0`.
- Full Store suite passed 371 cases with zero failures and 28 explicit OS/casefold skips. The added Linux ext4 casefold case requires its owned hosted volume and was not executed locally on macOS.
- CI now requires all thirteen named native resolver cases without failures/skips on each supported platform and all five named casefold cases on both owned Linux volumes. The exact thirteen-case workflow validator accepted the actual passing TRX and rejected missing, duplicate, skipped and failed rows. All eleven embedded Python workflow validators compile.
- A causal control removed the actual pre-component reserved-authority inspection. The native escaping-alias regression then failed because no admission exception was thrown. The source was restored byte-for-byte; its resolver SHA-256 remained unchanged. This demonstrates the test observes the production authority check, rather than only its test fixture.

Independent source/test/workflow review found no production blocker at the recorded pins; corrected report SHA-256 `69781d9a9356a06c6aa378bc2bce8ed9a03a0f085bb8b1f28c3574143dd8e8a2`. An initial Windows coverage concern was withdrawn after tracing the actual adapter: unsupported absolute NT substitute targets already refuse before reaching the resolver, and the absolute configured-alias scenario exercises a supported native local target on Windows. Extra Volume GUID and defensive parser branch coverage remains optional. The review also noted that a generic parent-open counter includes revalidation ancestry operations. The pre-authority regression's actual fail-before/pass-after outcome proves the corrected behavior; that counter alone is not treated as proof of a particular parent transition. Native Windows execution remains hosted evidence, not a local claim.

Final source/test/workflow input manifest SHA-256: `bbde521af4e3dbcde516725fd1ce970c2819a63b644e49b1b932bb03fba19bad`. Resolver SHA-256: `27ee02b9eeaa8cc6567d5db20f79866c4fae6548d22d654a5dab083ca3ad78ff`; test SHA-256: `cc2ee9496bb592cdaf0690c5093fee6ccc14bec0a17ad999497239b825a6b401`; workflow SHA-256: `8f81789c14354dae618fcc580c3f5ccfffe21415237a2bcd4eea89537c440354`.

Gate manifests/results, full logs, TRX, independent/root reviews and causal-control evidence are retained under the program artifact directory `prune-admission-authorization-audit/native-filesystem-gates/`, with prefixes `authority-`. Hosted results will be recorded after exact-head completion. T025/T042 remain open until that qualification settles; the full feature and program remain active.

## Hosted failure and fixture correction

[Validate 37960612273](https://github.com/valence-works/nuplane/actions/runs/37960612273) completed on `99eb76d13e245db4c5b8815756e75df2700b0116` with five successful jobs and one failed Windows job. The configured-authority Windows suite ran thirteen cases: twelve passed and the escape/return case failed during fixture cleanup because a membership handle remained open. Later Windows steps were skipped, so this is not Windows qualification. The original failure log is retained as `hosted-99eb76d-windows.log`.

The corrective test increment uses a relative escape/return link and immediately reads its native target to prove the literal `..` survived creation. It also scopes any unexpectedly successful resolver result inside each refusal assertion; such a result is now disposed before the assertion reports its failure, preventing that result from causing a misleading Windows cleanup error. Whether the original absolute-target creation rewrote its spelling remains unconfirmed. Production resolver, native adapters and workflow counts are unchanged.

Independent/root review found the test correction well targeted. All thirteen focused native cases pass locally without skips at test SHA-256 `bd527b9182aebd36a13fa0fd44a2cdf57441e5e7be4a0234d46f7d8d4d0e4899`, input manifest `303c9d15999c66ce9c4f0d32753b96c789d9856b229386ed2e45286f5872f1b2`. The corrected head still requires exact-head hosted verification; no failed gate has been represented as passed.
