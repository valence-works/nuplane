# Retained catalog ownership and typed recovery adoption

This bounded prerequisite implements internal retained-catalog ownership for the [multiroot protocol](../contracts/multiroot-protection.md). Acquisition and adoption have no runtime, DI, producer, CLI or deletion call sites. The full #108 checklist remains 38/128; this evidence does not accept a complete task or the pruning feature.

## Behavior

The registry resolves every immutable configured locator and retains its original native path evidence. Positively absent control namespaces are retained and revalidated without creating them; they supply no group authority. Enrolled physical roots are deduplicated by native identity, locked in canonical order before any member lock, and reread before deriving and after acquiring every root-local member obligation. Equal state slots on different roots retain distinct physical locks.

The owner accepts Complete bound ledgers, exact recoverable local PendingState transactions, or exact descriptor-bound group Prior/Intent/Next evidence. Generic Incomplete, declared, malformed, missing and changed evidence refuses. Every group descriptor must cover every and only retained catalog member bound to its shared slot, with exact root/epoch/member identity, before minting and before adopted group operations.

Registry-minted borrows serialize use of the held root/member union. Typed local recovery and group publication/recovery reuse the existing strict native cores through counted shares rather than reacquiring locks. Overlapping use of one borrow refuses; close rejects new operations and drains existing work; failed adopted mutations poison the owner. Exact owned outcomes refresh retained evidence only after replaying original path edges, expected ledger digest/file identity, epochs and unchanged member-lock obligations.

## Local verification

Root ran the full Store suite and Core build serially through the normal shared build wrapper on macOS against the same unchanged 1,361-input manifest, SHA-256 `b7cf52cb9c29ddec529363358ec17efc3878e5e9a483f32948f2128c876b83b7`:

| Gate | Actual result |
|---|---|
| `dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj --configuration Release --framework net10.0 --no-restore --blame-hang-timeout 3m` | 826 passed, zero failed, 44 explicit platform skips; 870 total |
| Required native group/catalog-owner identities within the actual full Store TRX | All 28 appeared exactly once and passed, without skips |
| `dotnet build src/Nuplane/Nuplane.csproj --configuration Release --no-restore` | net8/net9/net10 passed; zero warnings/errors |

Qualification report SHA-256: `7e4cf37b4afd88228f54f16318e629a60a6ed9829e781ad628334f632e3227f0`. Actual Store TRX: `3ea44368018027cf7470d270873590539e27cb5fb8bce03da52493c7402546f9`; Store log: `2fab07c7f7079de8b14ef51d1e29a9b4a3fad91a38b6fe87b4903fa7a8022906`; Core build log: `61b7900d497a2b3907ad3ce9dc714f8294d9f3f77d3ca631bdce6260f5988ea9`. The qualification enumerates all 44 skip identities. They cover unavailable Windows and ext4/case-sensitive filesystem cases; they are not passing platform evidence. The Store compilation emitted an existing xUnit2031 warning in unchanged `RootMembershipRecordTests.cs`.

The earlier standalone focused gate passed all 28 required cases. That run preceded an additional pre-mint refusal assertion; the final full Store result above executes that assertion. The subsequently added evidence document is outside the frozen executable-input snapshot.

The expanded native gate requires 28 exact cases without skips on each native CI platform: 13 previous group cases plus 14 catalog-owner group cases and one local pending-recovery case. The new cases cover descriptor-bound first-Intent adoption through an untouched schema-v1 peer, two publication generations and repeated recovery with the same borrow, missing/foreign/stale/extra participants, positive absence, changed ledger refusal, canonical all-roots-before-members ordering, separate same-slot root-local locks, partial acquisition contention/cancellation unwind, borrow overlap, close/drain, poison and local PendingState recovery.

The untouched schema-v1 peer is **Incomplete**. Its test does not establish the stronger Complete/v1 runtime restart-fence requirement.

A compiled negative control disabled the overlap guard while preserving compilation. The required overlap test failed at its specific refusal-message assertion (line 121), rather than accepting an incidental stale-ledger refusal as overlap proof. The runner restored exact production bytes, SHA-256 `5ee371c651fcb50d21efe6eaa48ff40de13a6f01bffe4167d23b48b7b9d72fa9`. Negative-control report SHA-256: `a3171d1dd013a3ce02c3c33c89fe6cb2c145d667dd527b060e365b13d82f1c80`.

Earlier failures remain preserved. Focused runs exposed and corrected descriptor-bound prior admission, incomplete native test-wrapper forwarding and test fixtures/assertions. The first full Store run reported 823 passed, 3 failed and 44 platform skips: the wrapper printed a `(Name, Parent)` tuple in its established filename-only write event. The correction restores `observed.Name` without weakening assertions or changing production behavior. Failed Store TRX SHA-256: `189e7730345dc2da226563f94561466a955906e095e7e082465fc8ec574b9241`.

Independent source/test review found no remaining actionable finding within this dormant slice. Final addendum SHA-256: `3bb57341ddd3befda52ca3bed7b7988c43c52f14b15f43cfb81ee39afb324f7d`. Review is separate from executed verification.

## Remaining acceptance

Fresh hosted qualification of the integrated head is required for Windows, macOS and Linux. Production first-read/restart fencing, standalone writer guard and permanent-marker integration, all runtime/startup/LKG/restore/offline authoritative paths, actual process-kill recovery, Complete/v1 peer qualification, public maintenance operations and physical pruning/deletion remain unfinished. Stable Nuplane/CShells releases and actual released-package Foundation adoption remain required. No merge, release or full-program acceptance is implied by this increment.
