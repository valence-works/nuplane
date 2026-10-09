# Declared-member binding and interruption evidence

## Scope

This internal quiescent-enrollment prerequisite binds every target in a valid all-Declared ledger to an observed existing-unprotected or prospective state slot and publishes the entire union as **Incomplete**. It is not Complete enrollment, runtime admission, graph validation, migration of already-bound/pending evidence, or pruning authority. T029 and the full enrollment/runtime/deletion tasks remain open; spec 030 still has **25/128 accepted tasks**. Initial declaration/bootstrap qualification is recorded in [initial enrollment evidence](enrollment-declaration.md).

The caller supplies independently resolved held state-parent handles and basenames keyed by member ID. Configured locators remain diagnostic ledger data; the future trusted componentwise resolver must establish their correspondence to those handles. This primitive does not reopen locator strings or establish that correspondence itself.

## Behavior

`BindDeclaredMembersAsync` copies declarations and locations before its first await, requires explicit continued quiescence, and checks exact root/epoch/member/target/location/locator associations under the root lock. Its bootstrap callback observes only native slot metadata and returns the entire lock set. Existing aliases are captured with the observed canonical spelling; absent slots preserve the native parent profile and requested basename. Duplicate actual canonical slots refuse before lock provisioning.

After root and every sorted member lock are held, the method rereads the exact ledger digest and revalidates the entire slot map before the first member-state byte read. Existing payloads must be bounded, decodable and unprotected; their actual file identity and state-body digest are recorded. Prospective slots remain positively absent. It revalidates all slots after the final read, then atomically publishes one whole bound Incomplete ledger using the existing transaction publisher. Fault/cancellation before publication retains the all-Declared ledger; a fault after publication retains the whole bound union. Locks and held control handles unwind in either case. The method writes no member state.

Known active and recoverable LKG closures are still required for verified Complete enrollment. A structurally valid ledger does not establish those facts. The explicitly participating serializer must preserve complete protection metadata; this is a provider contract, not interception of arbitrary third-party filesystem behavior. Absent spellings that may alias require native positive-absence checks at each later publication; this binder does not claim global prospective-name uniqueness.

## Focused proof

Ten native facts independently probe the root and every member lock immediately before actual member bytes are read, separately gate payload decoding, assert exact member/locator/target and per-binding identity/profile/digest evidence, and preserve external state-parent snapshots. They cover input mismatches, duplicate actual slots, protected/malformed prior state, changed ledger or state identity before reads, cancellation/fault before publication, post-publication fault, busy-last-member release/retry, and digest-valid target mismatch or already-bound refusal. Ordinary existing-lock reacquisition proves release after success and publication-boundary failure. Pending and retired variants are not separately exercised by these new tests.

A separate owned Linux ext4 casefold fact binds an existing alias using its native canonical spelling. The hosted casefold gate requires this and the three prior identity/file/directory-publication facts; an ordinary macOS run skips the Linux-only case rather than claiming it passed.

Two actual child-process cases pause the real binder at `BindingsPrepared` and `BoundLedgerPublished`. The parent correlates operation/PID/checkpoint, compares the durable ledger with its initial declaration, independently probes every root/member native lock, terminates the owned child tree and waits for nonzero exit. A fresh registry observes the exact prior declaration or whole bound Incomplete union. All locks are reusable, every external state-parent snapshot is unchanged, the pre-publication case can retry binding, and the already-published case refuses fresh adoption. This is process-interruption evidence, not machine-power-loss or full-enrollment recovery proof.

The causal control inserts an actual member byte read inside the metadata-only bootstrap callback. With the final native test pin, `declared-binding-first-read-mutation-v3` fails specifically because a native member lock does not yet exist/is not held before payload reads. Production source is restored byte-for-byte to SHA-256 `c4b338ecde85a4f2c2529eb27d38019774c8fe2788ff7104846c1f6f7bf7352d`. Mutation log SHA-256: `241ee65a8b57d0a5088b38ffb23d1b7b26e473acef52b1bed82387c455e3b4b6`. Earlier v1 failed indirectly through an absent-control exception and is retained as unqualified; v2 established the explicit diagnostic, and v3 repeated it against the expanded final test file.

## Review corrections

Independent source review and root review found no production defect in this bounded binder. Test review/QA corrected lost tuple element names, disposed an owned assertion stream, strengthened the missing-lock diagnostic, added exact bound evidence and lock-reuse/contention/no-adoption coverage, and replaced a process busy-set check that stopped at the root lock with independent probes of every member. The root also compared the initial declaration digest and extracted the repeated checkpoint command reader while preserving existing initialization/publication protocol messages. Focused native and child-process gates pass; the original compile failure and unqualified mutation remain retained rather than reported as passed.

Raw manifests, full logs, TRX, mutation drivers/results, worker freeze pins, and independent/root review reports are retained in the program artifact directory under `prune-admission-authorization-audit/native-filesystem-gates/`. Final qualification below records the actual candidate manifest; a base commit alone does not identify uncommitted inputs.

## Next critical path

Implement componentwise configured-path authority discovery without losing original components during normalization; validate full active and recoverable graphs and complete enrollment; integrate every runtime driver before its earliest read and retain leases through actual graph/context lifetime. The real two-composition non-destructive before-read demonstration must pass before recursive deletion implementation. Stable upstream releases and Foundation adoption remain required afterward.

## Final local qualification — 2026-10-09

All eight gates ran serially on macOS ARM64/APFS through the normal build-slot wrapper and the owned NuGet cache. They share the exact **799-input** source/workflow manifest SHA-256 `6064fd6b16d7fc79e284edf6fc33301bc7bdd837c4c81aa9599c2547d7094349`. Root checked every current source hash, unchanged-input result and log hash, and executed six exact embedded workflow validators against these final TRX. Base HEAD was `db2067d0ef0ac112d078c6e7c1174e73557a5ea8`; the manifest identifies the actual candidate bytes.

| Gate | Artifact prefix | Result | Log SHA-256 |
|---|---|---|---|
| Core build | `binding-final-core-build-v1` | net8.0/net9.0/net10.0; zero warnings/errors | `18f895d963ae18a89eb66930421aa75ef23bfb4fc0f31a2790ae46e9af4817e0` |
| Full Store suite | `binding-final-store-tests-v1` | 358 passed; zero failed; 27 explicit OS/casefold skips | `b212fae11a27657ba87c8e7c7678c5a27e48816987425e88ca3c9be78af5a2ab` |
| Native coordination/bootstrap | `binding-final-native-coordination-v1` | 37 passed; zero failed/skipped | `cf15ff3c8e0b9e6f294a6a7f315b71c161ef1c43a52c7c109d818b7fcc50f567` |
| Existing publisher/recovery processes | `binding-final-existing-process-v1` | 33 passed; zero failed/skipped | `bc2c0484bc5d556a9a9d9ee871d273f9c25b94af5543fc08dfb3de33fcd72bde` |
| Initial declaration/native profile | `binding-final-initial-declaration-v1` | 9 passed; zero failed/skipped | `f2004d5f87bb47e83f0b57f665ffe5e3228e71d158df51f650dca9fc2d5ef6fe` |
| Initial declaration child termination | `binding-final-initial-process-v1` | 2 passed; zero failed/skipped | `934265ecdb0a0b3e8a24c4a72880f7ed8cfa150cdc3b2fceaeb641b471d7eb93` |
| Declared-member native binding | `binding-final-declared-members-v1` | 10 passed; zero failed/skipped | `e3ecbac103e60854397667bd7914240e1f664a2f679504911cc59bf7fee627ca` |
| Binding child termination | `binding-final-process-v1` | 2 passed; zero failed/skipped | `0dc18f6b0314097de8df8692d18fc24dd51c7237428ed5e953f449d3fff664cb` |

The skipped Store rows are explicitly Windows-native or Linux-owned-casefold-only on macOS. They are not counted as platform acceptance. The workflow separately requires all ten native binding scenarios and both binding process checkpoints on every supported platform, with all four casefold scenarios on each owned Linux volume. Existing nine declaration/profile, two initialization-process, 37 coordination and 33 prior process cases remain required.

Independent source report SHA-256 `e2f4c91594d3c7c89e9380973961f39f69d59004b0f1c3028052b91c7ae12b69`; final native-test/workflow report `4ca9ad914085dc48c7f956a56f54e59fafcb7c51b7bde129e0648eceb7024a32`; final process report `ea5f54730ae6a4ddee86d2d619a05af201102092455aa4218a85f2baeb02eb63`. Reviews are source inspection; the executed gates are root-run evidence. Native test SHA-256 `f7df7dc2afe31cb459414d0536156550840767cd507a2a67f9cd20df1493ac8b`; process/host final pins are retained in `binding-process-integrated-review-inputs.json`. Full final root verification is `binding-root-final-review.json`.

Hosted qualification is pending at this local checkpoint. This evidence does not advance the full feature or program to complete. No recursive physical deletion has been implemented.

## Hosted qualification — 2026-10-09

[Validate 37955708022](https://github.com/valence-works/nuplane/actions/runs/37955708022) completed successfully on exact commit `da29f3e7bed62e4ee9f853aa71a091b97ac02d15`; all six jobs passed. Each Windows x64/NTFS, macOS ARM64/APFS, Linux x64/ext4 and Linux ARM64/ext4 lane executed ten native binding, two actual binding-process, nine declaration/profile, two initial-declaration process, 37 native coordination, 33 existing publication/recovery process and nine directory-publication cases without failures or skips. Both owned Linux casefold volumes passed all four cases. Twelve framework/platform protection runs passed 87 cases each.

The full Ubuntu solution passed 1,653 cases with zero failures and 27 explicit OS/casefold skips. Root verified terminal exact-head job metadata and parsed the complete hosted logs; metadata SHA-256 `ee8110faa713af3b4013a201c0b3bfc7f2eda7789e80213fe1d2e04a6a346c05`, log SHA-256 `9e7b04e8e0c1560671e92f17709242fb6de5536006009e4a36642620237b964e`. The retained machine-checked summary is `hosted-da29f3e-qualification.json`. These results qualify this bounded binding/interruption increment; full enrollment, runtime authority and pruning remain open.
