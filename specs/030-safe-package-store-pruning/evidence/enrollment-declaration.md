# Initial enrollment declaration and bootstrap evidence

## Scope and remaining acceptance

This prerequisite publishes the complete declared member/target list as **Incomplete** before any member-state write. It does not mark enrollment complete, grant ordinary package access, discover omitted users, validate active/recoverable LKG closure, or authorize pruning. T029/T033/T044 and the later enrollment/runtime/deletion tasks remain open. Spec 030 remains **25/128 accepted**.

`RootMembershipRegistry.InitializeIncomplete` requires explicit fully quiescent cutover confirmation and an expected physical root. It prepares an isolated same-parent stage containing an empty `root.lock` and a bounded, digest-verified full declaration, closes staged handles, then publishes `.nuplane-store` using native no-replace directory publication. Existing final namespaces, including ones with missing/malformed ledgers, are preserved rather than repaired or adopted. Cancellation/fault leaves an inert stage before publication or valid Incomplete evidence afterward. Unknown orphan stages are retained. No machine-power-loss durability is claimed.

`PhysicalStoreLock.AcquireBootstrapAsync` acquires the existing root lock once, invokes the metadata-only preparation callback, validates/copies/sorts the complete returned slot set, exclusively provisions missing deterministic member lock files, then acquires all member locks. Root/canonical/profile checks occur after callback and after member acquisition. Failures/cancellation unwind in reverse; provisioned empty lock files remain and are reusable. The ordinary existing-lock API retains its duplicate-before-root validation and does not create missing locks.

Native held-directory lookup-profile observation is metadata-only and creates no probe entries. Unix and Windows providers recheck directory kind, identity and actual native profile; the returned profile grants no authority. Member binding and configured-path authority still require their separate validation.

## Local final qualification

Executed serially on macOS ARM64/APFS using the build-slot wrapper and an owned NuGet cache. All six gates use the same **794-input** source/workflow manifest, SHA-256 `1cb1db1d83fcfa83ea524d33369ce15269192726163967870cc56712e647d71f`. Root verified every current input hash, log hash and unchanged-input result. The retained base HEAD is `34a6a4b497d97cab27bcf2fd4c0b3c4bb6b6ffb6`; the manifest pins the actual uncommitted candidate, not that base commit alone.

| Gate | Artifact prefix | Result | Log SHA-256 |
|---|---|---|---|
| Core build, net8.0/net9.0/net10.0 | `enrollment-final-core-build-v2` | Pass, zero warnings/errors | `cb996ef06063f0f1fa2f5d74b2be4f4272d2e9fd1c4272d52fcd22ef4c530553` |
| Full Store suite | `enrollment-final-store-tests-v2` | 348 passed, zero failed, 26 explicit platform/casefold skips | `e2202228aacc44b096de3fe3c16392ce60eb70d56c7519dadef125e01e289a49` |
| Native coordination/bootstrap | `enrollment-final-native-coordination-v2` | 37 passed, zero failed/skipped | `29747fe5d6846571bb5362d798e6e83786160177955853260f5a91101e9a5853` |
| Existing publisher/recovery processes | `enrollment-final-existing-process-v2` | 33 passed, zero failed/skipped | `b91c106a379da2dcb78afa6a307c2dbdcecfa5aba4a7fe603e7e6bb830e32d71` |
| Initial declaration/native profile | `enrollment-final-initial-declaration-v2` | 9 passed, zero failed/skipped | `413d401ea7e3cd1bde5e379f3698ec8fb7a075e177de78880a61c479aae7f0a2` |
| Initial declaration child termination | `enrollment-final-initial-process-v2` | 2 passed, zero failed/skipped | `9160a6e70e683b31637fcd55187fa04473f1a3e62f7fa9e0e62cc1d354b60c38` |

The workflow adds distinct required nine-case declaration/profile and two-case actual initialization-process checks on all four platform lanes. Root parsed the YAML and executed the exact embedded validators against the final local TRX, alongside the native coordination and unchanged existing 33-process validator. The full Store skips are Windows-native tests on macOS and the three Linux-only owned ext4 casefold scenarios; they are not platform acceptance. Hosted qualification for this new candidate remains pending.

## Behavioral and review evidence

- Native declaration tests preserve the complete member-to-configured-locator association, every target, root identity and epoch, and all external state parent identities/contents. They cover unconfirmed/wrong-root/empty/duplicate/non-declared input, existing final missing/malformed evidence, pre-move cancellation/fault, post-move fault, competing publishers and retained losing/orphan stages.
- Two actual child-process cases pause at `DeclarationPrepared` and `ControlPublished`. The parent checks the checkpoint PID/operation correlation, terminates the owned child tree, waits for nonzero exit, and inspects through a fresh registry. The result is absent final authority with retained stage or the exact valid Incomplete declaration. This is declaration-interruption evidence, not bound-member or full-enrollment recovery proof.
- External-state snapshots show unchanged parent/entry identities, kinds, links, lengths and content digests. Source inspection establishes that the initializer opens only the package root/control namespace and never opens the configured state locators. Snapshots alone do not prove absence of reads or transient restored writes.
- The quiescence causal control weakened the confirmation guard. The exact precondition case then failed with `Assert.Throws() Failure: No exception was thrown`; production source was restored byte-for-byte to SHA-256 `bc07c7a586b0bc90109bb46c9cb035ac2bae1121e4baae6e127223f46526f46a` before final gates. Retained mutation log: `enrollment-quiescence-mutation-v1.log`, SHA-256 `d3399ce307b713d857bc9f9a511c8e872766d060b0683ea0c3a7807b92526171`.
- Independent source review and root review corrected fixture event pollution and the missing final root-lock/profile recheck. Test-harness review/QA corrected a nullable enum conditional, a missing import, new analyzer warnings, and the missing member/locator association assertions. The first compile failure is retained as `enrollment-initial-declaration-tests-v1`; subsequent proof and final gates passed. A full final review found no remaining actionable issue within this scope.
- The original handoff manifest predates the declaration list-snapshot fix. It is retained as history; `initial-declaration-input-pin-addendum.json`, worker freeze hashes, independent review pins and final gate manifest identify the actual reviewed candidate.

Local artifacts are retained under `prune-admission-authorization-audit/native-filesystem-gates/` in the program artifact directory, including `enrollment-root-final-review.json`, `enrollment-declaration-bootstrap-independent-review.md`, `initial-declaration-proof-independent-review.md`, mutation driver/results, complete command logs, manifests and TRX.

## Next critical-path transition

Bind the actual all-Declared ledger to every independently resolved held member parent/canonical or prospective slot under root-before-all-member locks; only then read payloads and atomically publish the entire bound union as Incomplete. Pending/previously bound evidence must use explicit validated recovery rather than fresh adoption. Complete enrollment remains contingent on validated full active and recoverable LKG closure, followed by all runtime before-read participation and the real two-composition non-destructive proof before deletion implementation.
