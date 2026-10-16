# Admitted active-package snapshot

The additive `NuplaneStore.OpenActivePackageSnapshotAsync` API takes a sealed `NuplaneStoreSnapshotAccess` resolved from the validated participant composition. It observes active descriptors without granting package authority, acquires one non-recovering full-catalog session in canonical physical-root order, and rereads the selected state member under that same ownership. The configured state root is explicitly included even when the active set is empty. It compares the serialized payload, protection revision/binding and active descriptors before publishing the scope; every active install must be enrolled.

The returned `AdmittedActivePackageSnapshot` exposes a copied read-only projection and counted borrows for captured paths. Repeated disposal shares one task and waits for existing borrows after closing new ones. Refusal releases selected and peer catalog ownership. Existing ordinary state/descriptor overloads remain pure observational readers and require no participant/native composition. Initial malformed state retains the ordinary JSON/I/O error behavior; membership and stable-state failures are typed admission refusals.

## Local proof

Source commit `72eaf25`, integrated as `fa1c3f1`, changes nine source/test paths. Root and independent review matched the same final source pins. Focused snapshot tests passed11/0/0, then the combined restored coordinated Runtime suite passed81/0/0. The 81-name required manifest retains every prior70 scenario and adds the eleven actual snapshot cases. Full Loading regression passed267/0/0 (267 distinct TRX test IDs; two existing long JSON parameter displays truncate to the same name). Nuplane builds for net8.0, net9.0 and net10.0 with zero warnings/errors.

The tests exercise selected-member routing, protected empty-state ownership, unchanged state/ledger/use evidence, pending/corrupt/incomplete peers, substituted DI facades, pre-admission cancellation, immutable descriptors, rejected foreign path borrows, repeated draining disposal, and peer ownership reacquisition after release. A real coordinated state mutation between initial observation and admission produces `StateMismatch` and drains both selected and peer ownership without further mutation.

A compiled control suppressed the stale-state comparison for an uncanceled caller. The stale-revision regression failed with the intended missing-refusal assertion. Its unexpected-return branch disposes the scope, preserving teardown even under the defect. Production source was restored byte-for-byte, rebuilt normally, and passed the 81-case gate; the restored assembly hash differs from the mutant. The initial compile's two type/name errors and corrections are retained as unaccepted intermediate evidence.

## First hosted attempt and fixture correction

Exact-head `d5ed266` [Validate38207148078](https://github.com/valence-works/nuplane/actions/runs/38207148078) failed the existing `PostResolutionTargetInspectionFaultClosesTheUntransferredNativeResolution` Store regression. The fault injector matched the generated async method name `AcquireForInstallPathsAsync`; factoring that body into `AcquirePathAdmissionAsync` disabled the injected fault. Unix reported the missing expected exception. On Windows, the unexpectedly returned admission was discarded, so fixture cleanup encountered its still-held native owner. All five native lanes and the full-suite job failed; the shim job succeeded. No hosted acceptance is credited to this attempt.

Root reproduced the original missing-exception failure locally. The test-only correction matches the captured exact target handle and admission component boundary without depending on a generated method name, explicitly asserts that injection occurred, and disposes any unexpected successful return. The handle-closure assertion remains intact. Corrected focused proof passes1/0/0; the full local Store suite passes1,033/0/65 platform skips (1,098 total). Root and independent review confirm the correction changes no production behavior. The corrected source was then qualified in the hosted run below.

## Hosted qualification

Exact head `6e447fcbad520248e65ce86276f6f75ebc5d3aa6` passed [Validate 38208470612](https://github.com/valence-works/nuplane/actions/runs/38208470612): all seven jobs succeeded, including all five native lanes. Root and independent audits matched all 241 frozen source/test/manifest hashes and the same raw job logs. Every native lane passed the strict Runtime81 and retained Loading32 gates without skips. The full suite recorded 2,570 passed, zero failed and 70 platform skips (2,640 total). This qualifies the corrected snapshot source and accepts T090/T098. The failed `d5ed266` attempt remains unaccepted.

## Boundaries

T090/T098 acceptance is limited to the implemented snapshot contract and its named cases. The cancellation case does not prove cancellation after native ownership acquisition. Native absent-member empty state and path-identity substitution races are not exercised by these eleven cases. Actual host-integrated graph-use handoff remains T097; raw snapshot paths are never long-lived graph leases. Whole T100/T101, recursive deletion, public pruning activation, release and downstream adoption remain separate unfinished gates.
