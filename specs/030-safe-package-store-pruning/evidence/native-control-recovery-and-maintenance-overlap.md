# Native control recovery and actual maintenance/Loading overlap

This increment adds internal same-handle control-file recovery primitives and a test-only actual maintenance/Loading overlap proof. It performs no recursive package deletion and accepts no complete runtime driver, public maintenance API or full before-delete gate. The checklist remains 37/128.

## Native control capability

`IPhysicalStoreControlRecoveryFileSystem` is an additive internal companion. The built-in Unix and Windows adapters can open an exact canonical regular single-link control file relative to a held parent, take its exclusive native lock without waiting, and return a provider-bound one-shot token. Null means a positively observed busy lock; open/share/identity uncertainty refuses. A same-parent no-replace move reuses existing publication checks and preserves the original record bytes and file identity.

Removal replays parent/name/file identity and name semantics while the same handle owns the lock. Unix unlinks under that descriptor lock and verifies absence before closing. Windows sets disposition on its locked DELETE-capable handle, closes directly, then verifies positive absence through the retained parent. It performs no unlock or other operation on the delete-marked handle. Ordinary disposal before mutation still unlocks and closes. A possibly effective mutation error consumes and closes the token without claiming success.

The token serializes the complete attempt through post-close verification. Independent review of candidate v2 caught a gap between direct close and parent verification that let concurrent Dispose release the parent lease. Worker commit `3b920e904d407ee15d17c40a827d162ca8881980` corrects that race by running the post-close verifier under the same token gate. A compiled control moving verification outside the gate failed the disposal-serialization assertion; exact source restoration passed. Accepted token SHA-256: `9fcbe8b5ce33bbe3d6e296d820c20ac2b908ae9e5d0226cca75ce6ef285dd60d`. Root and independent review found no production correctness blocker in the correction; independent review SHA-256: `8e263da76a61b4bd477f79593ec20e25e1f157394c6546daf61df3daa778a82a`. Test-only worker commit `1f36cef7918e4485f18e64fa7d6ec27b79119134` closes the review proof gap: a release-observing SafeHandle disposes the underlying file handle before setting the event asserted inside verification. A compiled control disposing the wrapper while delaying its lease release failed at that assertion; restored source passed. Final test SHA-256: `d7ba85465411dd483827e0dcbd346a91f8c3dbf7c85031bef0929b2e5cbe4bd2`. Independent review found no remaining actionable issue in this delta; report SHA-256: `c223982c57ec59f41adc30a188096cb121dd2d52f30b72387e05725b098c21c4`.

## Actual maintenance overlap

Worker commit `cdebd4b1846c503e4bf28ecc1e05e64c51e5bd95` adds one test and extends the existing private native fixture. Two actual `AddNuplane` providers use different enrolled state files under one package root. A pauses before its first deferred Loading metadata read, after the short root/member owner has released and while its published graph-use lease and read pins remain live. Native lock accounting proves both lock kinds were acquired successfully and both active counts are zero at that pause.

B performs actual reconciliation, commits and loads its newer graph. A separately loaded old B collectible context remains strongly retained after the newer transition; its caller owner has been disposed and its old root install is absent from every persisted Active/LKG graph. A fresh manually composed `PackageStoreInspectionService.InspectAsync(0)` owns Maintenance admission, verifies both members, inspects native live-use records, inventories and plans while A is still paused. It retains the old root solely for LiveUse, retains the complete old/new/shared graphs, and marks only the unrelated completed install eligible. Captured revisions/body/protection digests match subsequent state rereads. Exact install identities and hashes of four fixture files per install remain unchanged; after release both current graphs actually load.

Removing only the inspection service's LiveUse contribution fails at the old-root Retained-versus-Eligible assertion. The production inspection source was restored byte-for-byte to SHA-256 `fc54d42df53c6158569b24005768266ef6cf5f277b185336ea58a051d121d95a` and the frozen final test passed. Independent source/evidence review found no actionable finding; report SHA-256: `f7130823d5ff172cb9674def4883bcbc28a93d602e484b0103f2d36f253bba25`.

The first test barrier intercepted earlier Phase-A metadata while root.lock was held, correctly excluding B. A later activation-gate barrier was too late to prove first metadata access. Both failed assumptions remain preserved; the final phase-aware native read barrier addresses the retained Loading boundary. This proof does not establish every earlier resolver/read driver, a public or DI-wired maintenance service, recursive package-tree verification or full US3 acceptance.

## Root verification

All five root gates below passed on the same 951-input manifest `4bf98d7f2950d6b6ed1df3c65a55fb4b1e875f2883697b90aeaaf07146a2839b`, with inputs unchanged throughout each run.

| Gate | Result | Log SHA-256 |
|---|---|---|
| Required native control recovery | 9 passed / 0 failed / 0 skipped | `9f5f6cf0a3952c2e932c796e8b303e37612397b637bc12ddd686d4ef6b0f8884` |
| Required retained Loading and maintenance overlap | 19 passed / 0 failed / 0 skipped | `b2ca47c5a3b3e7445fc5b42e8e495342aac4dfedade3974575ad88e06cb735e7` |
| Full Store suite | 696 passed / 0 failed / 42 platform skips | `7c4cc4ff9093250a9fcd15fb1bc4b94f941c132b766311fe5d52d05d35d529b8` |
| Core Release .NET8/9/10 | 0 warnings / 0 errors | `c431dee180279629fa8d106a833ac6c8ecfc1a103c3f7ea65af3a293c6c84858` |
| Full Integration suite | 212 passed / 0 failed / 0 skipped | `b7d30901bc4a104caafce2d89aa870a58f82435939c7f616b7307d3e44a7fd8d` |

Root checkpoint SHA-256: `125386984466a5f2e17f418f993e53ef453eecd8a396c3c22458e6a2d2cd44d1`. Native required-case accounting also rejected empty, missing, duplicate, extra, skipped and failed results. The changed Bash gate parses. The hosted native gate requires nine exact cases on each platform, with the respective Unix/Windows-specific case included; the retained Loading gate now requires all nineteen cases. Actual Windows/Linux execution of this increment remains pending until the new exact-head hosted run succeeds. The earlier green inspection run contains neither new slice.

After that test-only strengthening, root reran all nine required native cases and the full Store suite: **696 passed / 0 failed / 42 platform skips**. Final 951-input manifest: `4ef5d74d68dc3043c1987fe4e9c4955f4b0e041db210896d33728abffbb63548`; focused log: `724e77dd93da6b44b1ee0eb8d0d2f900b20ff2077f866d119d67df66b91924b3`; Store log: `794f37b14aff2f2be23fba014a611c057882adc6ca4f834ecd8bd688638e8478`. Both runs had unchanged inputs. The sole source-input delta from the five-gate table is the Store test file; production, Integration tests and all build inputs are identical, so the earlier Integration/retained Loading/build evidence remains applicable with that explicitly reviewed delta. Final root checkpoint SHA-256: `a2ce831ce770d08d9b1520f3c48270b7443220730f579ee92f4cc4e2a640cc1f`. Final accounting verified both actual nine-case native and nineteen-case retained Loading TRXs and rejected all six malformed result controls for each. Windows remains structural/local compilation evidence until hosted execution.

## Remaining work

T115's phase journal/restart coordinator, stale-use cleanup integration, public configured-label/enrollment/preview/execute operations, every remaining runtime path driver, the full before-delete gate, native quarantine/deletion and crash/restart outcomes remain required. Stable Nuplane/CShells releases, including CShells.Nuplane, and Foundation package/Host/Workbench adoption and actual e2e/main proof remain required. PR112 is not merge/release ready.
