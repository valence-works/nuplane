# Coordinated state publication: partial native integration

This increment implements actual pending ledger/state publication and exact-prior/next recovery for an **already persisted, independently bound ledger**. It is partial T028/T029/T031/T033/T034/T044/T045 work, not complete enrollment, runtime admission, graph validation, pruning execution, or complete runtime process-crash qualification. The original increment qualified at `76eafb7`; T031 is accepted, bringing Spec030 to **23/128 accepted tasks**. Cleanup-resolution and actual publisher/recovery process tests described below pass final local gates; hosted qualification is pending.

## Implemented behavior

- `PhysicalStoreLock` acquires an existing canonical, regular single-link root lock before existing member locks, sorted by the complete physical state-slot tuple. Names bind the parent identity, native profile and canonical basename with a domain-separated hash. Acquisition is nonblocking; failure/cancellation unwinds all acquired owners in reverse order, retaining cleanup errors. The returned native owner can later back the existing counted operation scope. No live-use-sentinel wait is introduced.
- `RootMembershipPayloadSerializer` bounds actual payload reads/writes to 4 MiB, rejects duplicate/unknown/missing/null/unsupported fields and invalid binding shapes, and verifies nested protection and outer canonical digests. The default protection converter and membership codec share the recursive duplicate guard. This codec alone grants no authority and cannot validate state-body content without reopening actual state.
- Pending records now bind prior membership status/digest, a publication GUID, and nullable actual stage/backup identities. After the first pending ledger is persisted, state and backup files are exclusively created, flushed and reopened. Their observed identities are republished in pending before native replacement. No predicted final identity is used.
- `RootMembershipRegistry` opens a preexisting ledger through held native directories, obtains root-before-member ownership using untrusted lock-order hints, then rereads and compares the verified ledger under ownership. It validates every member's actual prior state, writes pending, binds/validates artifacts, publishes the new state, reopens/validates actual body and protection content, and only then acknowledges the new observed identity. It never reopens diagnostic configured paths.
- Recovery reconstructs and verifies the exact prior ledger digest/status, accepts only exact unchanged prior or recorded-stage next content at the same slot, and revalidates every member before publication. A formerly Complete status can only be retained from that exact prior and all-member validation; an initial Incomplete root remains Incomplete. Missing/third state, unbound artifacts, changed backup identity, or metadata loss refuse while preserving evidence.

The internal namespace uses root-relative `.nuplane-store`, `membership.json`, `root.lock` and `member-<slot-sha256>.lock`. Transaction state siblings are `.nuplane-<publication-guid>.tmp` and `.bak`, bound by pending before mutation. Membership staging files use unique `membership-<guid>.tmp` names. Files left by an uncertain transition are not discovered or removed merely by suffix. These constants do not implement the still-missing configured-path/ancestor authority lookup.

## Original increment limits

There is no production initialization shortcut: the initial draft was removed after review found it could strand a present namespace without a ledger. Full declaration/enrollment intent, incomplete enrollment recovery, authority lookup, operation-cycle integration and every runtime mutation path remain open. Tests arrange a valid prior ledger with the real core codec/native files; fixture setup does not prove enrollment.

The original `76eafb7` local tests injected exceptions at actual persisted-file seams and constructed a fresh registry over actual disk content; they were not child-process termination tests. In that original implementation a stop after acknowledgement but before backup deletion left an unreferenced backup. The subsequent increment below closes that gap and adds real child-process termination/contention/recovery tests. Stage/backup creation before durable identity binding still refuses and preserves unbound artifacts. No directory power-loss durability or automatic sweep claim is made.

Complete active/recoverable graph validation, every before-read driver, graph lifetime, the real two-composition non-destructive proof, native pruning execution, stable releases and Foundation adoption remain required. No production/customer store was mutated.

## Review and local verification

Independent/root source review is clean for this bounded publication surface. Two test-hash snapshots drifted during final fixture fixes; root retained the earlier failed hash verification and verified the current twelve-file review set against the explicit final-hash addendum. Source review is separate from execution evidence.

Final core build: net8/net9/net10, **zero warnings/errors**. Full local Store: **306 passed, zero failed, 23 explicit platform/casefold skips**. Focused serialization/payload/digest/ledger: **78 passed, zero failed/skipped on each framework**. CI-selection mirror: **22 native publication/locking cases passed, zero skips**. YAML parses, the four-platform matrix is verified, and the exact embedded TRX validator passes against the actual 22-case run. All final gates share the 777-input manifest `ec114ed98b8d984ce9c34ee933f24bdaed02512a80d140e0380e72c5f2aa79a1`.

Cases cover all three prior branches, actual changed state-file identity at an unchanged slot, before/after replacement interruptions, exact-prior/next recovery, third/missing state and changed backup preservation, unsupported/custom serializers losing metadata on actual stage reopen, a second member's missing state/parent, Complete-prior restoration/refusal, native root/member contention, exact names/type/link checks, sorted lock acquisition, reverse release and partial-acquisition cancellation. Local native execution is macOS ARM64 only; original-increment hosted qualification is recorded below.

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

Local logs, TRX, source manifests, original mutation backups, final review reports and actual-process follow-up design are retained under the owned `prune-admission-authorization-audit/native-filesystem-gates/` artifact directory (with adjacent program audit reports). The hosted workflow runs the 78-case framework selection on all supported frameworks/platforms and demands the 22 native coordinated cases without skips. Exact-head hosted results are recorded below; earlier primitive qualification is in [publication-primitives.md](publication-primitives.md).

## Hosted qualification of the original increment

[Validate 37938628401](https://github.com/valence-works/nuplane/actions/runs/37938628401) passed all six jobs on exact commit `76eafb71ea1bf564bff34b4c8f6b8893b78ab6eb`. Full Ubuntu solution: **1,568 passed, zero failed, 23 explicit platform/casefold skips**. Each of twelve framework/platform runs passed **78 cases without skips**; each supported platform ran all **22 coordinated native publication/locking cases without skips**. Native Unix/Windows, Windows identity, both demanded owned ext4 casefold and packaging gates passed. Root inspected exact-head jobs and full logs; log SHA-256 `cc682061d2c3166cb745b60566e9d46db7da59aa90e9229c37d070affadf2c95`. This qualifies T031's actual saved/reopened custom-serializer refusal; it does not qualify subsequent cleanup-resolution or process-test changes.

## Durable cleanup resolution and actual process recovery

The subsequent candidate adds a required, digest-bound `PendingStateCommitResolution`: Unresolved, Prior or Next. An unresolved publication requires every bound artifact to be present and verifies exact state, all siblings and all artifacts before persisting a selected resolution. Prior/Next keeps the original member bindings and Incomplete ledger status throughout cleanup. Only a durable resolution permits an absent bound artifact to count as already removed. Present files still require exact identities, single-link regular type, canonical native names and decoded prior/next payloads; unbound names must be absent.

Cleanup validates the selected state, every sibling and all remaining artifacts before removal; backup removal rechecks selected state and siblings after any stage-removal seam, and each artifact payload is checked again immediately before its native removal. Final acknowledgement/Complete restoration occurs only after both artifact names are absent and all selected states still validate. The marker remains if cancellation, state drift or removal fails, allowing a fresh owner to resume safely. Normal publication and recovery share this implementation. This closes the original post-ack backup gap by moving acknowledgement after cleanup; final local qualification is recorded below, while hosted qualification remains pending.

Review found a masking test assertion expecting Unresolved for an explicitly Prior record; root corrected it. Review also found that same-identity state drift after stage removal could otherwise delete the last exact prior backup; selected-state/sibling revalidation and a regression now retain that backup. The final local gates below include both fixes.


The real-process fixture seeds a valid two-member Incomplete ledger using actual codecs/native files, with independent state parents outside the install root. It does not prove initial enrollment. The child invokes the real publisher/recovery APIs, reports correlated process identity and actual disk checkpoint evidence, then holds native root/member ownership while the parent proves contention and unchanged files. The parent terminates the child and starts recovery in a fresh process. The 28 publisher rows cover prospective, existing-unprotected and acknowledged prior bindings at every applicable publication/cleanup checkpoint; a separate recovery restart fact interrupts both Prior stage removal and Next backup removal, then resumes with a third process. Four existing child ownership/disposal facts remain green. Unbound pre-binding artifacts and an already acknowledged ledger refuse without mutation; acknowledged state is independently checked for exact protection and artifact absence.

### Final local qualification of cleanup/process increment

All seven final gates used the same **781-input** source/build/workflow manifest, `0fa9cdb04b39a136b32bf4703acff3ab69c6afabd4b0c9c00248330057fee21b`, unchanged before/after each execution. Root compared every final input against the worktree and verified current independent registry/process review hashes. The updated workflow parses, preserves all four platform lanes and passes its exact embedded native/process TRX validators against these actual local runs.

- Core build: net8/net9/net10, **zero warnings/errors**.
- Full Store: **324 passed, zero failed, 23 explicit platform/casefold skips**.
- Serialization/protection/digest/membership selection: **87 passed, zero failed/skipped on each of net8/net9/net10**.
- Native coordination/lock selection: **31 passed, zero failed/skipped**.
- Actual publisher/recovery and process ownership selection: **33 passed, zero failed/skipped**.

Three causal controls each produced exactly one intended regression failure, with no passes/skips: omitting durable Prior/Next publication, allowing missing bound artifacts before durable selection, and omitting selected-state/all-member validation immediately before backup removal. The registry source was restored byte-for-byte before final green gates. Root's final diff review found no remaining actionable issue in this bounded increment; enrollment, graph validation, runtime integration and pruning remain open.

Four initial process-harness compile runs failed and remain retained: missing shared-helper namespace, nullable checkpoint inference, fixture disposal/linked shared source, and an incorrect named argument plus xUnit async-context warnings. Root corrected these before the final green candidate. The earlier 33-case v5 pass is supplementary; v6 is the final source-pinned process evidence. Failures are not counted as passes.

| Final gate/control log | SHA-256 |
|---|---|
| cleanup-core-build-v2 | `30d944d8baf32163a9d0c4d5720f5ac9400cd5609428154d91bb244034ef8bc8` |
| cleanup-store-tests-v2 | `9a7629b1504f47b71e82d5ed74e48d065bea0d0d8207bdff18862693bb7ff478` |
| cleanup-framework-net8.0-v1 | `cab8823a5053542aa6de0a60165dc3a70abda2297c9e4623ea6c9cde4fa1e1a5` |
| cleanup-framework-net9.0-v1 | `79073961f6e08c5104e4b2cebce59c43afcbe9d1cfd204ba7d881fc22e0805a4` |
| cleanup-framework-net10.0-v1 | `5d74f3454fd2dc16f74297323fa1fa6a0e9cb0fd8d3dfd1152058694ec0bac84` |
| cleanup-native-selection-v1 | `2edbc2a31abc1d90a6dbfaa6aaade06e55dd518cafbf7cff5d571563e5d9c002` |
| cleanup-process-tests-v6 | `f2b4db3bfebe604a62702689114f8e604e4a09f141c46b94ec1cf236b69172f6` |
| cleanup-durable-resolution-mutation-v1 | `381b99ab3f689056dd0d4ca0af7c383a4ec1355886a182bce4ab950d055967ae` |
| cleanup-strict-presence-mutation-v1 | `b143749690deee084a89e6c45d8fa1ef9ffbee84c2b47d0b1e7880a00708db70` |
| cleanup-selected-state-mutation-v1 | `679a08260a31f274e44619b0135c559f6159c73217ff76481b8a8945a7238d0c` |

Local native/process execution is macOS ARM64 only. The updated hosted workflow requires all 31 native and 33 process cases without skips on Windows x64, macOS ARM64 and Linux x64/ARM64. No hosted pass, initial-enrollment, active/LKG graph closure, before-first-read runtime protection, two-composition lifetime proof or deletion acceptance is inferred from the local green results. Spec030 stays **23/128 accepted**.


### Hosted qualification of cleanup/process increment

[Validate 37943220221](https://github.com/valence-works/nuplane/actions/runs/37943220221) passed all six jobs on exact commit `360fac6f410abd0963f8b9946dcb23776107abe5`. Full Ubuntu solution: **1,615 passed, zero failed, 23 explicit platform/casefold skips**. All twelve framework/platform executions passed **87 each without skips**. Each of Windows x64, macOS ARM64 and Linux x64/ARM64 executed **31 native coordination cases and 33 actual publisher/recovery/process ownership cases, all passed without skips**. Unix/Windows identity/publication, both demanded ext4 casefold and packaging gates passed. Root verified exact-head jobs and parsed full logs; full-log SHA-256 `894c3c5ba6983dab56b97195962a8716ce64241e499bc2452c62f7c3ede6847f`.

This qualifies the bounded durable-cleanup/process increment. It does not qualify subsequent directory-publication/enrollment changes or any runtime admission, graph-lifetime, two-composition or deletion gate. Full initial enrollment and every runtime mutation/read path remain required.


Root and an independent read-only acceptance audit mapped T028/T045 to their actual stated deliverables: the native root-before-sorted-member lock primitive and its tests, plus the separately tested counted borrow/close unit. Both are accepted from the exact-head source and hosted execution above, bringing Spec030 to **25/128 accepted tasks**. Runtime operation-cycle propagation is a separate later-driver obligation and remains open; accepting the primitive/tests does not imply that integration. The test path in T045 is corrected to its actual Coordination directory. T029/T033/T034/T044 remain partial for their still-missing enrollment/runtime scope.
