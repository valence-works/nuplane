# Historical failed-root retention preparation

Date: 2026-10-09. This is the pure preparation prerequisite for T062, not transaction integration or safe-pruning acceptance.

## Contract and limits

`ActivePackageCatalogMapper.PrepareHistoricalGraphRetention` verifies the prior persisted protection/state/active/recoverable selection using the existing semantic verifier. It selects exactly one persisted recovery snapshot per desired root explicitly marked failed, preserving all matching request occurrences, outgoing reachable nodes, induced edges and exact native install identities. Caller ID sets and next-version maps normalize package IDs case-insensitively; ambiguous next-map aliases refuse. A known-empty state with no failed roots yields an empty plan; missing/Unknown/LegacyUnknown history refuses, as does failed-root recovery from an empty selection.

When every parent request remains selected, the exact parent snapshot is retained. A strict subset gets one new snapshot ID per plan, preserves its parent generation ID and recovery source revision, and uses a deterministic domain-separated `RetainedGraphSubclosureV1` identity binding parent graph/generation, sorted request/node/edge payloads, multiplicity and native install identities. It does not reconstruct target frameworks, source choices, or graph identities from descriptors or version maps.

Required retained versions must agree with the proposed active selection. Any incoming resolved node overlapping a retained package must match its exact version and supply the same observed install identity; a conflicting or absent observation refuses with `StateMismatch`. The immutable descriptive plan carries prior protection revision, protection digest and state-body digest, selected snapshots and required install/version sets. It performs no I/O and grants no root/member/native authority.

The plan currently has no runtime caller. Therefore this increment does **not** prove refusal before runtime transactions, atomic active/recoverable retirement, final descriptor projection or apply-failure coherence. Those remain T062 requirements. In particular, existing per-package pointer transactions can partly succeed before a later graph node fails; this pure helper does not solve that boundary. No graph-use lifetime or deletion acceptance is claimed.

## Review and executable verification

Root review corrected two concrete issues before final gates: mixed-case ordinal caller sets could silently omit required fallback, and unconditional UseLastKnownGood selection refused a legitimate known-empty first transition. Independent review found no concrete remaining blocker in the pinned mapper/digest implementation and producer tests, explicitly limited to this pure contract. Root reviewed final test-only helper placement and added the known-empty/failed-root refusal assertion. Shared fixture setup and one reflection helper avoid repeated construction boilerplate.

The initial focused gate failed compilation because a reflection helper was nested under the fixture while one outer test invoked it. Moving the existing helper to the outer test class fixed that test-only scope error. The next focused run passed **10 tests, zero failed/skipped**: log `7a18de8f12ce5c696113a7c7ddc377839c86c7a52ac965942e291144e5e4b5fc`, TRX `297352a456d170226f6c644ae1f361902e56f66c37d85e9b6b3b5845cc7b8886`.

A compiled causal control removed only the overlap-preflight call. Both selected version/native-identity refusal regressions failed at `Assert.Throws`; production source was then restored byte-for-byte. Causal log `fabdc6a5f0912c895bae8ce6ef4724a7642cf545178d38a3e62edc77234af9e6`, TRX `32c6673e958ee535f40981cb86af5b249beae4cc30831604a20cbaee078dffb8`.

Final gates rebuilt restored production and include the additional known-empty assertion:

| Gate | Actual result | Log SHA-256 | TRX SHA-256 |
| --- | --- | --- | --- |
| Full Store suite | 476 passed, zero failed, 31 explicit platform skips; all ten preparation cases passed | `6da73d08f8776b7ce392169d4363b822d5a9623522496a3fa77f9715f1141a83` | `a7f9853c2da763c4aa8b364a3c18e769be7826ebd03816a30af40320cdbe2c33` |
| Core builds | net8/net9/net10, zero warnings/errors | `c4f30f9f218968501ae33603b71f63e231ad687f0512e133acae6bc2f7dbd006` | Not applicable |

Both final gates share the unchanged 829-input manifest SHA-256 `fd4fc1faef27d5a7d14e2e7e27471940cbb2d32180607ea9746f5e59a96cbedf`. The unchanged Store test analyzer warning xUnit2031 in `RootMembershipRecordTests.cs:79` remains; no warning-free test build is claimed. The local helper increment did not add a Runtime execution or reuse preceding-head CI as current-head proof.

[Validate 37984421171](https://github.com/valence-works/nuplane/actions/runs/37984421171) subsequently passed all six jobs on exact helper head `bab1c842fc539ae957a052f3c6b6efe708d141f1`. Full Ubuntu solution: 1,806 passed, zero failed, 31 explicit platform skips; Store 476, Runtime 834, Loading 257, Integration 193, Directory 21, NuGet 25. The distinct 67-case native boundary passed Unix/macOS 64/three explicit skips and Windows 63/four explicit skips. All twelve platform/framework serialization lanes passed 97 each without skips; both owned Linux ext4 lanes passed eight each without skips. Complete hosted log SHA-256 `4a95480eac86ab0e6176379f8f6dddc92b3db2b940a986c086700cbcd039ddfe`. Independent parsed qualification v2 SHA-256 `9e7fc9486363e62a60f3b620a6d0086198c251d0e429751b3ee598530d77b1b0`. This qualifies committed helper source only, not subsequent runtime changes.

## Final source pins

| Path | SHA-256 |
| --- | --- |
| `src/Nuplane/Operational/ActivePackageCatalogMapper.cs` | `b20152bfd53c0a22c9864add769b6e30a0cab97a3f29b3f6b7cfa6f4eca4d74f` |
| `src/Nuplane/Store/Coordination/ProtectionDigest.cs` | `8d30458ed20f744663887e7703264aa2acd4e4d1e0873f4dee36b21fabcf10cc` |
| `test/Nuplane.Store.Tests/Coordination/HistoricalGraphRetentionPreparationTests.cs` | `2161328ed44e03a0dc27d13d70329c1c816e29f8fb423bf809731c9dc6152e40` |
