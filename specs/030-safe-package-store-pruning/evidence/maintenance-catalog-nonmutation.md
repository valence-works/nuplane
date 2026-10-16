# Read-only catalog admission for Maintenance

Status: bounded increment accepted after root and independent local/native review, 2026-10-11. Full T051, selected-label public inspection, T116/T117 and pruning remain open.

## Existing promise and narrow correction

The internal inspection interface promises no package/control-file mutation. Its service requests Maintenance admission, but the shared catalog-session path previously recovered pending local/group publications before descriptive verification. Stale graph-use recovery already excluded Maintenance. The public maintenance contract currently promises no payload mutation; this correction enforces the existing internal boundary rather than changing that public contract.

Both configured-root and install-path catalog entry points now pass their existing operation kind to session acquisition. Maintenance skips only pending recovery. Full sorted catalog/member ownership, all-member protection verification, Complete/schema-1 fencing, request replay and cleanup remain intact. Non-Maintenance admission retains its recovery behavior. Pending or incomplete catalog membership is refused before a capability returns; inspection does not turn recovery into a side effect.

## Actual native fixture evidence

Four cases cover configured-root/path admission with pending StatePublished evidence on the selected root or an offline catalog peer. They reuse two real completed native membership fixtures with nonempty protected Active/LKG graphs and installed package artifacts. Maintenance returns exact typed UnknownAuthority. Before/after snapshots compare the complete captured directory/file name set and native identities across both owned fixture trees, plus every non-lock file byte. Lock identities are compared; lock bytes are deliberately not read.

The pending publication ID/resolution remains unchanged. Subsequent ordinary Reconciliation admission recovers the exact Next protection revision/digest, clears pending membership and returns Complete; another Maintenance admission then succeeds, proving ownership drained. A fifth settled Complete two-root positive case returns enrolled admission with captured evidence unchanged. These are in-process actual filesystem/publication interruptions, not child-process crash or physical deletion proof.

## Causal and local qualification

All five focused cases passed. Root compiled omission of only the new Maintenance condition, keeping all later catalog verification intact. All four pending cases failed because a capability returned instead of typed refusal; the settled positive still passed. The source was restored byte-for-byte, SHA-256 `03671d5334bb5169ecec1a49895e992ec13a4709a9c042cd7131da4fb1da7a3c` before and after.

Restored Release/net10 Runtime passed 933 tests with two existing platform-specific skips. All 930 prior stable test IDs, names and outcomes are unchanged; exactly five cases were added. Existing xUnit truncated display names collide in both runs, so the comparison uses unique stable IDs. Integration passed all 241 cases without failures/skips; every prior ID/name/outcome is unchanged. Affected Store admission/inspection tests passed 51 with one owned ext4-casefold platform skip. Core Release builds on net8/net9/net10 completed with zero warnings/errors.

The native coordinated-runtime manifest retains all previous 52 requirements and adds the five exact actual passing TRX names, requiring 57 cases. Its strict verifier passed against a derived 57-row subset of the actual full Runtime run; this was not a separate test run. Independent reviews checked source, actual TRX results, stable-ID comparisons, causal failures, restored source hash and exact manifest coverage. Initial audit assumptions about xUnit error wording and unique display names were corrected against saved results without test reruns.

The earlier Complete/v1 pending child-process proof is accepted separately at exact `080456f`; it does not qualify this new production change. No selected-label public Admin operation, ordinary standalone SaveAsync routing, schema-2 activation, enrollment/migration or package deletion is added. The owner's first support-policy card remains unanswered; this fix is independent of that choice.

## Native acceptance

[Validate 38148031094](https://github.com/valence-works/nuplane/actions/runs/38148031094) passed all seven jobs and five native lanes at exact `ea261fbb0d307b0ccb1c5581d955f05e3da5e258`. Root and independent raw-log/source audits reconciled all 75 source pins. Each native lane passed all 57 required runtime cases, including the five Maintenance cases, without skips. The unchanged 36-case process, 25-case retained-loading, 26-case graph-use recovery and 11-case same-handle recovery gates also passed. Full six-suite CI reported 2,386 passes, zero failures and 68 explicit platform skips.

Together with the compiled condition-omission control, this accepts the bounded no-mutation correction. The [bounded claim is released](https://github.com/valence-works/nuplane/issues/108#issuecomment-6110359014). Complete T051/T116/T117 and pruning remain open; the accepted checklist remains 45/128.
