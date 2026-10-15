# Confirmed execution recovers stale graph uses

A source-consumer run reached actual collectible context death and passive sentinel release, but confirmed maintenance could not progress: read-only inspection correctly classified a stale use as Unknown, and Execute reused that non-recovering path. No package was deleted by that failure.

Execute now validates the exact root and enrollment epoch, verifies all catalog member protection under its retained root/member owner, invokes the existing native graph-use recovery kernel, revalidates the catalog, then builds its fresh inventory and retention plan. It does not reacquire root locks. Live uses remain protected; uncertain namespace or failed recovery prevents package deletion. Inspect and Preview remain read-only.

## Regression proof

The three public-operation cases cover unchanged stale control names, native identities and bytes across Inspect/Preview and wrong root/epoch confirmation; verified stale recovery followed by actual eligible deletion; preservation of the exact live old graph and every node while unrelated content is deleted; and unknown-artifact refusal without changing install evidence.

Removing only the recovery call makes the positive test fail with expected `Completed`, actual `Refused`. Production source was restored byte-for-byte before rebuilding the combined suite. The final local Debug/net10 suite passes all **78 required retained-loading cases**, with zero failures/skips; its strict manifest preserves all prior 75 names. This includes the new three cases and the earlier native-lifetime, public migration/recovery, maintenance-observability and post-loading cases.

| Raw TRX | SHA-256 |
|---|---|
| Final local 78-case suite | `da186733d17ffc90d9413891423d8573da5c0719994328138f8a38cb1da1dd74` |
| Omitted-recovery causal control, expected failure | `438a4fbb29dbfeaba08c1de84073310283ad2c1902ab51e07a0ce452ffefa025` |

The actual source-only CShells consumer also completes enrollment, removal, old serving-scope drain, long weak-context death, passive Live-to-Stale observation, confirmed fresh-known execution with physical fixture deletion, and repeat `AlreadyAbsent`. Its log SHA-256 is `d200667af4695e6443bfd73ee110c2b9be29916857700bb404bdb02604698e94`. That scenario has zero selected recoverable-LKG candidates; it does not by itself prove positive LKG retention. Existing separate protection tests cover that boundary. The retained host provider is disposed and the exclusively owned run root is removed.

Independent source review found no blocker and recorded exact source hashes. Release builds of Abstractions/Core/Admin/Admin.Api pass for .NET 8, 9 and 10 with zero warnings/errors. CShells adapter source-overlay tests pass 49/49 on Debug/net10. The broader Runtime suite passed 984 cases with two Windows-only skips on this Mac before this Execute-only increment.

These results are local source proof. Final exact-head Linux/macOS/Windows qualification, public package archives, package-backed .NET 8/9/10 consumers, stable releases and Foundation adoption remain separate unfinished delivery gates.
