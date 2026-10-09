# Coordinated state publication: partial native integration

This increment implements actual pending ledger/state publication and exact-prior/next recovery for an **already persisted, independently bound ledger**. It is partial T028/T029/T031/T033/T034/T044/T045 work, not complete enrollment, runtime admission, graph validation, pruning execution, or process-crash qualification. Spec030 remains **22/128 accepted tasks** while latest hosted qualification is pending.

## Implemented behavior

- `PhysicalStoreLock` acquires an existing canonical, regular single-link root lock before existing member locks, sorted by the complete physical state-slot tuple. Names bind the parent identity, native profile and canonical basename with a domain-separated hash. Acquisition is nonblocking; failure/cancellation unwinds all acquired owners in reverse order, retaining cleanup errors. The returned native owner can later back the existing counted operation scope. No live-use-sentinel wait is introduced.
- `RootMembershipPayloadSerializer` bounds actual payload reads/writes to 4 MiB, rejects duplicate/unknown/missing/null/unsupported fields and invalid binding shapes, and verifies nested protection and outer canonical digests. The default protection converter and membership codec share the recursive duplicate guard. This codec alone grants no authority and cannot validate state-body content without reopening actual state.
- Pending records now bind prior membership status/digest, a publication GUID, and nullable actual stage/backup identities. After the first pending ledger is persisted, state and backup files are exclusively created, flushed and reopened. Their observed identities are republished in pending before native replacement. No predicted final identity is used.
- `RootMembershipRegistry` opens a preexisting ledger through held native directories, obtains root-before-member ownership using untrusted lock-order hints, then rereads and compares the verified ledger under ownership. It validates every member's actual prior state, writes pending, binds/validates artifacts, publishes the new state, reopens/validates actual body and protection content, and only then acknowledges the new observed identity. It never reopens diagnostic configured paths.
- Recovery reconstructs and verifies the exact prior ledger digest/status, accepts only exact unchanged prior or recorded-stage next content at the same slot, and revalidates every member before publication. A formerly Complete status can only be retained from that exact prior and all-member validation; an initial Incomplete root remains Incomplete. Missing/third state, unbound artifacts, changed backup identity, or metadata loss refuse while preserving evidence.

The internal namespace uses root-relative `.nuplane-store`, `membership.json`, `root.lock` and `member-<slot-sha256>.lock`. Transaction state siblings are `.nuplane-<publication-guid>.tmp` and `.bak`, bound by pending before mutation. Membership staging files use unique `membership-<guid>.tmp` names. Files left by an uncertain transition are not discovered or removed merely by suffix. These constants do not implement the still-missing configured-path/ancestor authority lookup.

## Explicit remaining work

There is no production initialization shortcut: the initial draft was removed after review found it could strand a present namespace without a ledger. Full declaration/enrollment intent, incomplete enrollment recovery, authority lookup, operation-cycle integration and every runtime mutation path remain open. Tests arrange a valid prior ledger with the real core codec/native files; fixture setup does not prove enrollment.

The local tests inject exceptions at actual persisted-file seams and construct a fresh registry over actual disk content. They are **not child-process termination tests**. The next work unit must exercise the real publisher in an owned child, prove cross-process contention while it holds ownership, terminate it, and recover in a fresh process. Stage/backup creation before durable identity binding currently refuses and preserves unbound artifacts. A stop after acknowledgement but before backup deletion leaves an unreferenced backup; durable post-ack cleanup evidence/recovery remains open. No cleanup-complete, directory power-loss durability or automatic sweep claim is made.

Complete active/recoverable graph validation, every before-read driver, graph lifetime, the real two-composition non-destructive proof, native pruning execution, stable releases and Foundation adoption remain required. No production/customer store was mutated.

## Review and local verification

Independent/root source review is clean for this bounded publication surface. Two test-hash snapshots drifted during final fixture fixes; root retained the earlier failed hash verification and verified the current twelve-file review set against the explicit final-hash addendum. Source review is separate from execution evidence.

Final core build: net8/net9/net10, **zero warnings/errors**. Full local Store: **306 passed, zero failed, 23 explicit platform/casefold skips**. Focused serialization/payload/digest/ledger: **78 passed, zero failed/skipped on each framework**. CI-selection mirror: **22 native publication/locking cases passed, zero skips**. YAML parses, the four-platform matrix is verified, and the exact embedded TRX validator passes against the actual 22-case run. All final gates share the 777-input manifest `ec114ed98b8d984ce9c34ee933f24bdaed02512a80d140e0380e72c5f2aa79a1`.

Cases cover all three prior branches, actual changed state-file identity at an unchanged slot, before/after replacement interruptions, exact-prior/next recovery, third/missing state and changed backup preservation, unsupported/custom serializers losing metadata on actual stage reopen, a second member's missing state/parent, Complete-prior restoration/refusal, native root/member contention, exact names/type/link checks, sorted lock acquisition, reverse release and partial-acquisition cancellation. Local native execution is macOS ARM64 only; hosted Windows/Linux qualification for this increment remains pending.

Causal controls each caused exactly one intended failure with zero passes/skips: omitting actual staged protection verification, omitting all-member recovery validation, and omitting deterministic member sorting. Original production source was restored byte-for-byte before final green gates. Initial core compile failure (exception constructor argument) and two failed lock-fixture setups are retained with corrections; they are not counted as passing evidence. The existing xUnit2031 warning in a preexisting assertion remains unchanged; no new test warning is introduced.

| Retained gate log | SHA-256 |
|---|---|
| coordinated-publication-core-build-v2 | `9d602f07cfe517a0245b34bcae8fdde108dee8720585e8893766939553f0d3de` |
| coordinated-publication-store-tests-v1 | `68cef3db38a729b11143c53ed8510a68bb533c0980e3c69cd51831d52f3690c9` |
| coordinated-publication-framework-net8.0-v1 | `e34bebc1380785ee85b25c2ee85610905359ae4396bbe3dc841dafec37c376b0` |
| coordinated-publication-framework-net9.0-v1 | `ec55d7fd5cb72932e1c515ea8eb8f8e98956bf799b4936e8ace8859a8ea9014d` |
| coordinated-publication-framework-net10.0-v1 | `0c0b93492729b58a33ef4deec42a4a12b28019ce9ed521233cf9773fc3f6769c` |
| coordinated-publication-native-selection-v1 | `5cdcbe4b3fb216011c311d3bb9156c29ad3c77cc9518dc2c7f3bd8a47b1fcbfc` |
| coordinated-stage-reopen-mutation-v1 | `f54a9020ea9c2a80ff852df097edd1dae8f671c2592bc3aa249e896d4940fce0` |
| coordinated-all-members-mutation-v1 | `533083c26e5f0958fd4ea026dd274c1bd56702b2679fdf708a91f05fdca75e15` |
| coordinated-lock-order-mutation-v1 | `6f02702584b14575400586f961227c5f1d6bf1cb32ddda640ab81b92d5489d08` |

Local logs, TRX, source manifests, original mutation backups, final review reports and actual-process follow-up design are retained under the owned `prune-admission-authorization-audit/native-filesystem-gates/` artifact directory (with adjacent program audit reports). The hosted workflow runs the 78-case framework selection on all supported frameworks/platforms and demands the 22 native coordinated cases without skips. Exact-head hosted results will be recorded separately; earlier primitive qualification is in [publication-primitives.md](publication-primitives.md).
