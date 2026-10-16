# Retained operation admission and active-graph transition integration

This increment supplies internal configured-root and complete-path-set admission under native root/all-member locks. It does not yet wire runtime drivers, bind a DI composition's configured state slot to membership, authorize missing staging destinations, publish graph-use leases, or perform deletion. T026 and T062 remain open; the full program still requires actual two-DI before-first-read, lifetime, manual-prune, release and Foundation proof.

## Retained admission

The resolver retains both membership digest and native ledger-file identity. Admission binds that discovery evidence to a fresh locked ledger reread. Multi-path admission copies and classifies the complete path set, deduplicates physical roots, sorts by native provider/device/file identity, and acquires every root plus its complete member lock set before the separate member-verification loop reads any state payload. Both entry points then perform full semantic protection verification and native active/recoverable install identity verification. Every original caller-path observation is replayed after verification before returning ownership. Archives are classified from one native regular-file identity without archive payload reads; hardlinks refuse.

Counted owner/borrow state retains all native ownership through borrow draining. Per-path borrows can validate only their exact included path; cross-owner/context access refuses. An internal context facet exposes the already-held member locations to future coordinated state writers, without recursively acquiring locks. Its current ledger observation follows verified publication rather than caching the initial ledger identity forever. Unknown/Incomplete never becomes Unenrolled. A positive Unenrolled configured result has no fabricated owner and closes all observations.

Partial acquisition and release attempt every retained owner and observation in reverse order. Native cleanup failure remains visible together with the primary admission error, and repeated owner disposal reports the original failed release. A provider that fails before performing its own cleanup can leave its OS lock held; this implementation reports that failure and does not claim guaranteed release against a broken provider.

Fifteen native admission facts exercise actual Complete enrollment, configured and multi-root retention/draining, aliases and exact-path restrictions, archive classification without archive reads, busy later-root unwind, inspection/release faults, positive Unenrolled and Incomplete refusal. Three of the facts publish digest-valid but semantically invalid actual state, replace an actual completion marker, or replace an actual install directory after Complete. Both admission entry points must refuse, non-empty tracked handle sets must all close, and all original member/root locks must be reacquirable afterward. The fixture setup is shared. A separate first-member-state-read probe requires both root locks and all four member locks to be busy before the payload read, and a cancellation case reacquires every lock after cancellation at the second root lock.

Independent source review found no concrete blocker in the frozen admission source. Driver before-first-read proof is not inferred from this internal seam.

## Selected graph projection

Successful root contraction removes the superseded graph from the exact Active map. A failed desired root retains its prior graph only while all exact selected node versions still agree. Protected descriptor projection merges actual historical root selections and dependency edges with current resolved graphs. Legacy descriptors are descriptive only and are carried only for the same selected graph, generation and package version; no edges or enrollment authority are invented.

Two root transition assertions construct complete active/recoverable projections and run the full persisted semantic verifier. The cleanup regression uses actual `PackageApplyExecutor.ResolveAsync` output but manually supplies the apply/merge context; it proves resolver-to-cleanup mapping, not execution/merge pipeline behavior.

Two root causal controls remove the failed-root guard or exact-node supersession guard. The v2 controls compiled and reached the expected assertion failures (one selected graph instead of two; two graphs instead of one). Production mapper bytes were restored to SHA `c3e27d60db7efc661dea8b3b36f3990f9d5ae1157038c39bc9b0d6be42d221f7`. Control log SHAs: failed root `17886a46742b8d5bb31a0cf8e99d9d6d633aaf0cc874d34140e8b880db6d9735`; contraction `88517fb3940c259a9e8c9e1d4c0785caa3f4819444b42e928463ce35270d02d8`. The initial v1 controls and first admission integration gate failed during compilation against stale reference output after copied older timestamps; they did not establish behavioral proof. Refreshing copied source timestamps rebuilt the unchanged source successfully. All failed attempts remain retained.

## Remaining exact transition

If A advances while failed B needs an independent historical B→C closure, derive B's exact subclosure only from verified persisted requested roots, node identities and directed edges under the retained operation owner. Use an explicit deterministic retained-subclosure identity with provenance from the prior graph/generation and selected payload; do not reuse the old full-graph ID or claim to recreate its unavailable original target-framework/source hash inputs. Publish that exact selection, descriptors, activation records, LKG and retirement evidence together.

If B requires A@1 while candidate A@2 conflicts, reject the candidate before `ExecuteTransactionsAsync` switches any pointers and preserve coherent fallback. Refusal at cleanup is too late. Legacy/unknown state cannot supply trustworthy edges. The current full verifier checks resulting graph/state consistency but does not by itself prove derivation lineage or retirement semantics. Required regressions must drive real resolution → transaction → merge → protected publication, including zero pointer changes on conflict. This is still required delivery, not deferred out of scope.

## Causal admission proof

Root reran and retained actual logs/TRX for both verification-call controls. Omitting only configured-root full verification made all three refusal cases fail at the configured-root assertion; omitting only path-set verification made all three fail at the path assertion. Both compiled and reached `Assert.IsType` failures because unsafe admission returned instead of refusing. Production was restored byte-for-byte to SHA `7af5c4162e7313d50dd57e1fb9dc0e44e332575dfd47265e0e9359479837190d`. Control log SHAs: configured `099a0617f3f76837cba3fedc73f3fc3204cdc9de372b525eec393530c6e62475`; path `c663c5deec1aa7ca3aa9d2426e02e6631148a66cee2c86f9830d8f55a9e68cc1`.

The ordering control moved verification into each root-acquisition iteration. The actual first-read assertion then found all three locks of the later root free; the cancellation case still passed. Restored-source ordering baseline passed both. Worker control log SHA `2964ba59216345e77441419488766ba1b287f2b0048576425dfcac27ceed0842`; TRX SHA `0d3fb29b38850dea6cccbee5e1378f5822317c953f9d1b0241dcaccac26d6332`. Root inspected both and preserved them in the owned program artifacts. Root changed only cancellation-source teardown to `using var` and reran the full native and Store suites. This is two real native roots, not two DI runtimes.

## Final local qualification

| Gate | Terminal result | Log SHA-256 |
|---|---|---|
| Exact native member/install/producer/completion/admission boundary | 44 passed, zero failed, two explicit ext4-only skips | `3edbc0b4f54a8b1a2b5087d9809e81a45944cc94f13bda8f038d1a2abf2f8ada` |
| Complete Store suite | 446 passed, zero failed, 30 explicit OS/casefold skips | `4ab738bcdc75fb007156f04f9bc3f72ca821d4e2674b5afa605edb69af779303` |
| Complete Runtime suite | 817 passed, zero failed/skipped | `0e31f433ffa009e9993b817e37137931bc4090fb01d3fb06e1d9123a94cbe514` |
| Core net8/net9/net10 build | Zero warnings/errors | `bb1e7db6b0273be9e991e6844e1baf60d648ee1c99cb96009e77bf862d344db5` |

Native/Store final manifest: `fd8680d67b2feed9171c6578f26c53ee3bf7d79940eb73f067bceafc82f25f29`. Core/Runtime final manifest: `0ae5ac004d63ed14ae7524eeabe98f98878bbd1358b278996e701a87abb39a51`. Root compared manifests: their only differences are the added Store ordering test and its workflow selection/accounting. Production and Runtime inputs are byte-identical; core/Runtime qualification is not borrowed across a production edit. Every gate's own source snapshot remained unchanged while it ran.

CI requires exactly 46 native boundary rows: 37 uniquely passing facts, six uniquely passing malformed-input rows, and three explicit platform outcomes. Ordinary Unix lanes require 44 passes/two casefold skips; Windows requires 43 passes/three explicit platform skips. No new admission case may skip on a supported platform. The existing owned ext4 lanes still require all seven cases. All twelve embedded Python blocks compile. The actual local TRX passes the exact workflow validator; six negative accounting controls refuse missing, duplicate-at-same-count, skipped-required, unexpected-casefold, missing-admission-at-same-count and duplicate-theory-at-same-count rows. Workflow triggers, permissions, concurrency and runners are unchanged. Exact new-head hosted qualification remains required.

The public program issue had stale text calling M3 blocked on an owner choice that had already been resolved; root corrected and reread it. Project 58 still reports #2507 In Progress / Assigned / Pending. This housekeeping does not establish a delivery gate.
