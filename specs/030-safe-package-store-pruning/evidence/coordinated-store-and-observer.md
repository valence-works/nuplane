# Coordinated state publication and borrowed observer dispatch

Date: 2026-10-09. Root reviewed and integrated this bounded increment on top of `ebe5385953d39eaba89b790958b00cbf49f27931`, producing `4e15b4714c8e69730053fa8c3e041652260271c7`. The root and independent reviews cover the exact source hashes below. Its hosted run exposed a Windows publication failure; the head is not cross-platform qualified.

## Implemented boundaries

`ICoordinatedStoreRegistry` is an additive companion to `IStoreRegistry`: read, failure, source-snapshot and full active-state publication explicitly consume a caller-owned live borrow. No required member was added to the existing interface. `StoreRegistry` resolves its own configured file through the existing root/member lock context; callers cannot select a member id to write someone else's slot.

The configured file is componentwise resolved as native metadata and must match exactly one acknowledged member's native state slot and file identity before any coordinated member payload read. The context requires the exact same serializer instance selected by the registry, including caller-stream protection support. Missing/in-memory state, unlisted slots, expired borrows, and a distinct eligible serializer refuse. Every operation rebinds its path and rereads/verifies the complete current member union under the existing owner.

Failure and source-snapshot writes preserve verified active/recoverable/retired graph evidence, advance the protection revision with overflow checking, and recompute state/protection digests. Active publication accepts an explicitly complete caller candidate: exact root, epoch, member and next revision are required; semantic and native active/recoverable installs are checked before pending publication. The existing pending/acknowledgement transaction publishes and refreshes state-file identity without reacquiring root ownership. The context operation gate is acquired once; its full verification callback uses the already-held direct reader to avoid recursive semaphore acquisition. The fixture's optional revision/root-version parameters support a real v1.0→v1.1 candidate without repeating enrollment setup.

`ObserverEventDispatcher` implements all four existing scoped dispatcher forms. Before the first callback it checks the entire immutable observer set for scoped or explicit package-path-independent participation. Each awaited callback receives a counted borrow from the exact supplied owner; the dispatcher disposes it on success, cancellation and failure. Typed admission refusals propagate, ordinary callback failures retain isolation, and cancellation is checked before and after callbacks. Owner close waits for the active callback borrow. Existing unscoped overload bodies are unchanged.

## Source pins

| Path | SHA-256 |
| --- | --- |
| `src/Nuplane/Store/State/ICoordinatedStoreRegistry.cs` | `1599f9ef4df9c20cb54afbb743fe0d0a01c8b9ce953a10a2c4299e0721c41442` |
| `src/Nuplane/Store/State/StoreRegistry.cs` | `e3516daff925a92dbe2495e72d909e6e5bf25835314391d87013f852218213f8` |
| `src/Nuplane/Store/State/StoreRegistry.Coordinated.cs` | `db1a245fe0c2a3562b24bd51d2d9f524d34da902b85ea7428b5f9a47d2618fff` |
| `src/Nuplane/Store/Coordination/RootMembershipRegistry.LocatorReplay.cs` | `9255cdbf8f48da05bdb4b160994e69b56277a336773d9b1f5d545cba3b74823d` |
| `test/Nuplane.Store.Tests/Coordination/RootMembershipProtectionVerificationTests.cs` | `4a93c4658a24f794b714f25f3eec030043c5b4655584c691f39f65c4e9a4ebee` |
| `test/Nuplane.Store.Tests/Coordination/CoordinatedStoreRegistryTests.cs` | `99bafc07c02f96f6c2ecc530ef4225d93129a6c124e7bc4bf543442baac4e9f3` |
| `src/Nuplane/Events/ObserverEventDispatcher.cs` | `a91cd7809443f382b0d210d6351eeada3eca72afc3ffb662bc86fda8c8ff615e` |
| `src/Nuplane/Events/ObserverEventDispatcher.Scoped.cs` | `049c8b61c412b1f40b5fae4bd08d7e96cfe4543f84ca8dfba8d6632ad7e047a6` |
| `test/Nuplane.Runtime.Tests/Observers/ScopedObserverEventDispatcherTests.cs` | `e055a5d86d2391a16b83db317de19f0040b939d812f4c432c225cd90e83038d8` |
| `.github/workflows/validate.yml` | `47395c8c3eb1304f11c0d421a4c1a8692477b16d47fa583553907dc1891eb33a` |

## Root verification

All four final commands preserved identical source/test/workflow input bytes: manifest SHA-256 `ca23d4a97dfaa9a0dc6f54161cce4a0ed0f899195213bd05c17027ea658c3abf`. Logs and TRX files are retained under the owned `prune-admission-authorization-audit/native-filesystem-gates` artifact directory; no skipped case is described as passed.

| Gate | Result | Log SHA-256 |
| --- | --- | --- |
| native | {'Passed': 50, 'NotExecuted': 2} | `821d5443b948cf125a219a3e626b66736788637171f16263985cf7909678ed5f` |
| store | {'Passed': 452, 'NotExecuted': 30} | `da991e956fde8d720ef6c18ed82f5edd4bb88f78ea5cfc7c02e3c572f5540505` |
| runtime | {'Passed': 834} | `7d49a038ce1f9b20c1e906263fe9c3fe5477425103248f9583e206be4dec07dc` |
| core | net8/net9/net10, zero warnings/errors | `7d82855913dbe58916dbda8c824e34dc93496eec09602aea69da213cf6f386ec` |

The native gate has 52 rows: 43 unique required facts, six unique malformed-path theory rows and three explicit platform-dependent outcomes. On local macOS, 50 passed and the two ext4-only cases skipped. The Windows expectation is 49 passed/three explicit skips; that is an expected manifest, not local Windows proof. All twelve embedded Python blocks compile, the extracted validator accepts the actual local TRX, and six accounting controls reject missing, duplicate, skipped-required, unexpected-casefold, missing-coordinated and duplicate-theory results even when cardinality is preserved.

Seventeen focused observer cases execute as part of the 834-case Runtime gate. Six native coordinated-store cases execute as part of the 452-case Store gate. The positive sequence reads and publishes failure/source/active state for two actual acknowledged members under one retained owner, then reopens both state files and checks each ledger acknowledgement revision.

Two compiled causal controls establish relevant regression sensitivity:

- Removing only whole-observer preflight fails all four event-form assertions because no refusal occurs before callback invocation. Log SHA-256 `1b02709f8e3d5f635cce64f79d0840878dd33f4702aad02d2193061eb8285859`.
- Removing only the selected serializer identity checks fails the native distinct-eligible-serializer refusal assertion. Log SHA-256 `62283d4d28802a264bb84c742d86d0724e148b78f4493c4a697eb6820ce191fe`.

Both production files were restored byte-exactly before the final gates. An earlier observer-test compile attempt referenced unavailable abstraction internals; the tests were corrected to use the public root/epoch and post-disposal path-validation behavior, with no added friend access. That compilation failure is retained and is not causal behavioral evidence.

Root review found the serializer-instance bypass and the independent observer review found final-callback cancellation; both are corrected and covered. Independent re-review found no remaining concrete blocker in these bounded paths. Root performed the final diff, source-pin and gate review.

## Hosted qualification failure

[Validate 37978839840](https://github.com/valence-works/nuplane/actions/runs/37978839840), on exact head `4e15b4714c8e69730053fa8c3e041652260271c7`, completed with five of six jobs green. The build/test, universal Darwin shim, Ubuntu x64, Ubuntu ARM64 and macOS persistence jobs passed. Windows failed `CoordinatedStateReadAndThreePublicationsUseOneExistingOwnerAcrossTwoMembers`: the first coordinated failure-state publication could not replace `membership.json`, with native status `0xC0000022`. The Windows boundary produced 48 passes, one failure and three explicit platform skips; subsequent Windows stages did not execute.

The native resolver retained a ledger read handle for the whole admitted operation. Windows opens that ordinary handle without delete sharing, so retaining it blocks the same owner's atomic ledger replacement. This affects both configured-root and install-path admission observations. The correction must preserve native identity/digest replay and ordinary file sharing policy while ending the ledger observation handle's lifetime before publication. Fresh hosted Windows proof is required after the correction; Unix success does not qualify this boundary. The failed-run log is retained with SHA-256 `07993799418b59eabbb62a1874b4adb707827c6fc5d676a8cd1f610ebcf41fe4`.

## Acceptance limits and next integration

T011 is accepted for its additive contract and T077 for borrowed callback dispatch; runtime/DI routing remains required under T068–T070/T078/T089/T100. T064 remains partial: exposing coordinated methods does not prevent legacy direct writers or prove that every enrolled runtime write uses them. The complete candidate verifier establishes candidate consistency/native bindings; a historical graph selection/retirement transition must separately prove lineage before transactions. No unchanged old closure may be carried over a changed active map.

This test sequence is not the mandatory two-`AddNuplane`/overlapping-generation before-read gate. Actual coordinated runtime source/failure/cleanup writes, startup/restore/loading/static driver coverage, graph leases and actual context lifetimes, manual inventory/preview/confirmed execution and crash/platform proof, stable upstream releases and Foundation adoption remain outstanding. Recursive deletion is still unstarted. The next runtime work uses per-cycle explicitly bound services, preserves selected participant instances, and refuses unsupported participants before callbacks.
