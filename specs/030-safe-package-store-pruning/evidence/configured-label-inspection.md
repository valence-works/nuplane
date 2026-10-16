# Internal configured-label inspection

Status: accepted at exact `ef932de28667036d33a7b2b9ea9242e11bcf98be` after root and independent local/source/native qualification. Full T051 and public maintenance operations remain open.

## Behavior and ownership

The internal inspection service resolves the selected label through the trusted configured-root resolver before native access. The existing default operation delegates to the configured default label. Unknown labels and a mismatched filesystem provider refuse before catalog acquisition.

One retained native catalog owner and borrow span all acknowledged-state verification, selected-root live-use inspection, inventory and retention planning, and final evidence replay. Every enrolled catalog member is verified, including independent v1 peers and shared-state v2 participants. Only after complete verification does inspection select the exact root-local install identities for Active and recoverable last-known-good protection. Final replay compares member/state identity, revision, state-body digest and protection digest before returning the snapshot.

An unenrolled selected root reports Unknown/Unenrolled without invented membership, inventory or protection. Enrolled peers are still verified; a wholly unenrolled catalog needs no acknowledged-state verification. Pending selected or peer publications refuse without recovery. Stale live-use evidence remains Unknown. Cleanup preserves the original operation failure together with any ownership-disposal failures.

This is an internal descriptive operation. It does not activate schema-2 runtime admission, expose a partial public maintenance interface, enroll or migrate a store, route standalone SaveAsync, or delete packages. The unanswered owner support-policy decision does not affect this read-only increment.

## Actual fixture and causal evidence

The combined focused Store run passed all 20 cases: the original ten inspection cases, six configured-label service rows, and four v2 rows. Existing cases retain nonempty v1 Active/LKG and live-use protection. New cases cover additional/default/unknown labels, provider mismatch, unenrolled selection with an enrolled peer, and pending v1 selected/peer publications. The native v2 fixture has an actual cross-root ActiveAndRecoverable graph; assertions compare exact selected-root installs and both protection reasons against its verified bundle.

Corrupt independent-peer acknowledgement and pending v2 selected/peer cases compare before/after native identities, full captured entry sets and non-lock file bytes, including current stage/backup/control paths. Lock identities are retained; lock bytes are deliberately omitted. These are in-process native filesystem fixtures, not process-crash or physical deletion proof.

Root compiled a mutation that omits the independent v1 peer from verification while retaining the full native owner and both v2 participants. The settled v2 positive passed; the corrupt-peer refusal failed specifically because no exception was thrown and a snapshot returned. Source restoration was byte-exact: SHA-256 `b1dde55d55ae97c83cb6698f58346f09c66dc5953b5fa1f7e9945bc1e75a02c6` before and after. Independent review reconciled the actual source, TRX, failure message and restoration. Primary-error preservation during cleanup is source-reviewed; no injected cleanup-failure test is claimed.

## Qualification boundary

Restored Release/net10 Store passed 920 cases with zero failures and 63 explicit platform skips. Runtime passed 933 with two existing platform skips; Integration passed all 241 without skips. Every previous Runtime 935 and Integration 241 stable test ID, name and outcome is unchanged. Core Release builds on net8/net9/net10 completed with zero warnings/errors. The test build reports the pre-existing xUnit2031 warning in RootMembershipRecordTests; no warning-free test-build claim is made.

Native preview manifests preserve their previous 41 Unix and 40 Windows requirements and add six actual service names, requiring 47/46. The native multiroot manifest preserves its 48 requirements and adds four actual v2 rows, requiring 52. Existing CI filters select these cases without a workflow change. Strict verification passes against subsets derived by those filters from the actual restored full Store TRX; these subsets are not separate test runs or Windows-native proof.

Exact-head [Validate 38151279564](https://github.com/valence-works/nuplane/actions/runs/38151279564) passed all seven jobs and five native lanes. Root and independent raw-log/source audits reconciled all 87 pins. Each native lane passed preview 47 Unix/46 Windows and multiroot52 without skips, plus runtime57, process36, retained Loading25, recovery26 and same-handle11. Full six-suite CI: 2396 passed, zero failures, 68 explicit platform skips, 2464 total. The retained Loading cases here precede the strengthened T105 sequence; this run does not qualify its later source. Full T051, T052–T059, enrollment/migration, public writer policy, schema-2 activation, T105's complete before-read proof and pruning execution remain open. This increment checks off no complete task; the accepted checklist remains 45/128.
