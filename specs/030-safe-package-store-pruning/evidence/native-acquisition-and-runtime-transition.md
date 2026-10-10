# Native acquisition and protected runtime transitions

This is a partial follow-up to [routed resolution and native streams](routed-resolution-and-native-streams.md). It does not accept the complete runtime-driver gate, authorize recursive deletion, or establish release readiness. The task checklist remains open for incomplete work.

## Native acquisition

The built-in remote acquirer now implements the optional scoped acquisition contract. Credential eligibility remains ahead of native cache access and HTTP requests. The original operation borrow reaches native completion/hash probes, archive creation, extraction and same-parent no-replace publication. The legacy overload retains its existing unenrolled behavior.

The internal installer exclusively creates its archive and extracted files through held native handles. It verifies canonical names, single-link regular files, root/parent identities and supported name profiles. It replays every root-to-parent edge after the awaited archive callback, before publication and after publication. Extraction validates ZIP entry types, traversal, file/directory collisions, folded component aliases, declared lengths and CRC-32 before publishing an empty completion marker and the canonical archive SHA-512. A competing destination is usable only when native completion and the exact archive hash agree.

Limits are explicit: 128 MiB archive, 10,000 entries, 512 MiB total expanded data, 128 MiB per file, 1,024 path characters, 32 components and 255 UTF-8 bytes per component. Non-ASCII components refuse on folding or normalization-insensitive profiles because the native provider does not expose a complete Unicode equivalence function. Reserved store authority names refuse at every path depth.

Identity-bound attempt/archive/prepared/publication evidence remains on disk. Native staging cleanup, interrupted acquisition recovery and physical deletion remain required; this increment implements none of those deletion capabilities.

## Capability metadata and automatic loading

The built-in capability contributor consumes scoped metadata reads under the supplied live owner. Its metadata memo requires the same owner as well as the cycle correlation and package identity/path. Expired borrows refuse before a memo hit; a new owner rereads metadata even with a reused correlation; legacy calls cannot reuse scoped cached reads. The static configured desired source explicitly declares path independence.

A real two-provider reconciliation regression uses the built-in resolver/acquirer and native installed-package cache. Provider A pauses inside the actual deferred Loading activation gate. Provider B must commit and load its next generation under the same root and member set while A's earlier graph-use record remains live. Both then load their actual assemblies and release leases through the existing context lifetime mechanism. This case uses fixture-owned preinstalled packages; it does not prove HTTP acquisition within that entire reconciliation route.

A separate real `AddNuplane` regression starts with a missing requested install, serves an authenticated NuGet service index and archive over loopback HTTP, and runs the built-in resolver/acquirer through reconciliation, native installation, protected-state publication and actual Loading. It verifies two authenticated requests, one archive download, the selected SHA-512, native completion, active/LKG versions, exact persisted graph nodes/edge/requested root, both expected assemblies together in a collectible context, a live generation-use record and eventual stale ownership after unloading. The cached dependency is fixture-owned. Runtime context membership is not asserted exclusive; the exact two-node assertion applies to persisted state. The test's free-port selection has a small bind race, and Windows qualification remains pending. Independent test/fixture review found no material blocker; report SHA-256 `ca68c97f1d59a3ad392e780b7716ab772c0ecd3a90df44c3ea65313b188049db`.

Holding the short root/member scope through deferred Loading was tested as a deliberate mutation. The regression failed when B encountered busy `root.lock`; the exact original source was restored. Mutation input manifest SHA-256: `0189228c77de3a183e86f64944f8ce7f685c347de4319fde0311b264c7ed94a6`; log SHA-256: `b7fbe15133b8e2e956cac1b195c5eb1d0a129b2cabf04c9d6434c6afed6f7957`. This proves the lock-release boundary, not the complete pruning/read-safety matrix.

## Review and qualification so far

Root review corrected native prepared-handle lifetime, folded implicit-parent alias checks, nested reserved authority names and complete root-to-parent replay. Independent review of the frozen nine-file acquisition/capability snapshot found no further concrete correctness blocker. Review report SHA-256: `c7aa0aa068d075c9a7bbad6f9cc8829ea1a04f98810af90e001f53ead5e69e8d`.

| Bounded local gate | Result |
| --- | --- |
| Scoped acquisition, capabilities and existing authentication | 35 passed, zero failed |
| Integrated native installer, readers, candidates and Unix streams | 47 passed, zero failed |
| Native installer after profile-specific test correction | 8 passed, zero failed |
| Core Release on .NET 8/9/10 | successful, zero warnings/errors |

The 47-case gate retained input-manifest SHA-256 `ab1eff2e42356fc886e47de8b2d20d3ef1e1bfc570f6fc5d5ddcdaa313cd9203`; its log SHA-256 is `cdeb5290946243514930aba6f2ab7a8ed1a7d21ba593c5885ce2c02f44101aa9`. The all-TFM build retained manifest SHA-256 `ce1e201ce4dd9701f16d247625105d3827980acc84d9d93d7b8c383a3b70d725` and log SHA-256 `c78df99e9d7b3e6b8ffa70ec8d3e12dec2871dad3430934a18988e6a392745e5`.

The worker's general folded-alias test returned early on the local case-sensitive filesystem. Root review replaced that early return with actual installation and distinct-content assertions for `Foo/a.txt` and `foo/b.txt` on case-sensitive profiles; folding profiles must instead produce alias-specific refusal. All eight installer cases pass after that correction, retaining input-manifest SHA-256 `45ff14c2c6f83d1cf051a3d7d0de7bfde31fd24268be8ab05f1d2dc8003d2e43` and log SHA-256 `50dfe03497d2b86f784492ac3e9715e817a20468f39023b6884d66f485c0ff4d`.

A separate ext4 casefold regression asserts a folding profile, an actual archive callback and alias-specific refusal; the owned ext4 CI lane explicitly requires it. Its actual hosted execution remains pending. General no-skip test accounting does not establish execution of a conditional folded branch.

The earlier hosted `795b04f` run failed on Windows exclusive-handle fixture reads and Linux inode-reuse assumptions. The fixtures now verify Windows contents through native streams and preserve the old directory identity while replacing a test-owned path. Fresh hosted qualification is still required; the earlier failed run is not relabeled green.

## Protected-transition repair

Independent review found that an apply-time failed desired root could lose an old-only dependency from the merged active map, making final prior-subclosure verification refuse. The repaired driver rereads acknowledged coordinated state under the same admitted owner after actual apply results, restores missing exact prior failed-root subclosure versions, refuses conflicting versions, and constructs a fresh complete candidate. Descriptor removal now follows the final retained map. Cleanup and final publication consume that same map. Independent repair review closed the original finding without a further material blocker; report SHA-256 `db4235411d8f500c43593a1b62f8c576b34e127e503198e8b38f73e270af5771`.

One regression drives actual `AddNuplane` reconciliation with a changed dependency closure and an apply-time expected-hash failure. Another supplies two exact graph selections at the real executor/middleware boundary and proves an independent successful graph publishes while the failed root's prior closure survives. The latter partition is synthetic: the production resolver currently merges desired roots into one graph. The transaction pointer switcher is in-memory, so neither test establishes filesystem crash atomicity or rollback. Disabling only failed-subclosure restoration makes the actual reconciliation regression fail for missing `Shared.Dependency@2.1.0`; restored bytes pass. Negative TRX SHA-256 `6e3fbdc82ec153fbd1d7d036009c44d183c6a1cfd127764a082b45ae4b782891`; restored positive TRX SHA-256 `a0ae1e39ea75496aa46d32d2d48de760e42c6dd0383c84eb55dafde0ebd26261`.

## Integrated local checkpoint

Root integration passed all four affected suites: Store 646 passed with 41 explicit platform skips; Runtime 875 passed; Loading 259 passed; Integration 211 passed. Total: **1,991 passed, zero failed**, with 41 platform skips. Core and Loading Release builds on .NET 8/9/10 completed with zero warnings/errors. These are affected-suite results, not a full-solution run.

The initial four-suite/build run held 915 inputs unchanged, manifest SHA-256 `c354389c9a585f731c3df90435e6da63f99176dcf8d2efb76f86a93047bc49ab`, log SHA-256 `99afb1090fd818b85bd9aa92d598d7394adfd22472056506626c33c29efc872e`. After integrating the authenticated HTTP test and its fixture change, root reran the entire Integration suite against 916 unchanged inputs: manifest SHA-256 `0acd1d9d134a851e9e7058b4aa441ceb63a50149d9ae8b97dd55af43f36bb13c`, log SHA-256 `ddf040357814b7a81229c1c2e81d911249b527f38c3d2af7ccac016ad3c7d325`. Production and the other suites' source inputs were byte-identical between those gates.

Final exact-name accounting accepted 31 native-reader, 18 retained-loading, 3 Unix-stream, 5 protected-candidate, 8 installer, 7 scoped-acquisition/capability and 41 runtime/startup cases with no skips. All 42 malformed-result controls refused; eight relevant bash steps parsed and 18 embedded Python blocks compiled. Final workflow accounting SHA-256 `6c70bc4119fb2afa4ca28d19e5e3cfa04d4f415e963c7bbec3b14ec2b72bade7`. Final workflow review restored an accidentally changed existing incomplete-binding expected count to its original ten; only workflow bytes changed after the final Integration gate. Combined provenance/accounting SHA-256 `fa2b2c5b69d4c216baae77ff2e8e134aa977f10c67c587be266491b09b15e886`.

This accepts the bounded local acquisition/runtime increment and the retention repair. Fresh exact-head hosted qualification, the complete runtime-driver gate and the mandatory before-delete evidence review remain pending.

Manual inventory/preview/execute, safe physical deletion and crash recovery, remaining driver entry points, complete platform qualification, stable upstream releases and Foundation adoption remain required.
