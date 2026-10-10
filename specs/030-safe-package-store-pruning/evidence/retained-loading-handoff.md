# Retained graph loading after root-operation release

## Implemented scope

Core exposes `IResolvedPackageGraphUseLeaseAcquisition` in `src/Nuplane`, accepting an actual resolved graph and its original root requests under an existing admitted borrow. Native install binding, complete immutable record publication, and the callback's final all-member replay finish before the read capability escapes. The former snapshot-only provider was introduced only on this unmerged PR; it is not a released interface.

The optional Loading module consumes copied `ScopedResolvedPackageGraph` envelopes through its internal `IScopedPackageLoader` companion. It validates the complete graph, exact original paths, source/hash projection, roots, edges, requests and lease identities before callbacks or package reads. All advisors and activation gates must explicitly support scoped or path-independent participation. It holds counted graph read pins across metadata, activation and materialization, transfers ownership to the actual load context before resolver/enumeration IO, and drains every untransferred owner on failure or cancellation. Reused contexts retain the new owner; partial collectible contexts retain protection until weak death, and noncollectible contexts retain it through process lifetime.

Retained metadata uses the filesystem provider captured by the lease. It replays native root, path, install, completion-marker and metadata identities without taking root/member locks or requiring a fresh membership ledger. A newer coordinated publication therefore cannot invalidate an older still-live graph merely by changing the ledger. The ordinary unscoped reader and legacy loader retain their existing admission behavior.

Concrete-first DI registration aliases the scoped loader to the existing `PackageLoader`. The lifetime observer uses an explicit factory because its constructor is internal. Core has no dependency on Loading.

## Actual direct-loader proof

`OverlappingPackageGraphProtectionTests` uses two real `AddNuplane` compositions, distinct enrolled state files, one owned physical root, actual graph resolution/protected state/native sentinel publication, and real generated managed assemblies. Composition A publishes its lease and releases its short borrow/root operation, then pauses at the first actual `nuplane.json` payload read. Composition B persists and rereads a newer active graph generation, publishes its own lease, releases its operation, and loads. A then resumes. Both actual collectible contexts and expected assemblies are verified, along with two native-busy Live use records. Cleanup unloads the contexts and observes native ownership become Stale.

Five additional cases prove invalid exact-path refusal before metadata IO, caller-list mutation isolation, retention of an actual assembly/context after a partial load failure, and refusal of unknown gates/advisors before their callbacks or metadata reads.

This proves the direct scoped-loader handoff. Automatic `ReconciliationService` to Loading routing, a maintenance-command refusal attempt, remaining package readers/catalogs, and crash/deletion behavior remain separate work.

## Local qualification

Commands run through the normal build-slot wrapper with a source-input manifest before and after each gate:

```text
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --configuration Release --framework net10.0 --filter FullyQualifiedName~OverlappingPackageGraphProtectionTests --blame-hang-timeout 3m
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --configuration Release --framework net10.0
dotnet build src/Nuplane.Loading/Nuplane.Loading.csproj --configuration Release
```

- Restored focused gate: **six passed, zero failed, zero skipped**; unchanged 871-input manifest SHA-256 `edcaeadb88748200d5d6c58189656a45971df19e421a8275aa5e9c531ca5e949`, log `bf813967da620890fc1067fd984e25eb10efc962013a5d14399377457569fbc8`, TRX `10b3d5aa2c3ff637eee8a9f9b6fafcc060cc67ca42903cc09542d9642da4529f`.
- Full Loading regression: **259 passed, zero failed, zero skipped**; same unchanged manifest, log `d43e0caf0e5a427137d71e1a494dd7e3f506eb7ffa52a43d016e1233497efda9`.
- Earlier Core/Store regression: **599 passed, zero failed, 38 explicit platform NotExecuted cases**. This preceded the final observer-DI and participant-preflight fixes and is supporting regression evidence, not final-head qualification.
- Final Loading/Core/Abstractions Release build passed all three production frameworks (`net8.0`, `net9.0`, `net10.0`) with zero warnings/errors and the same unchanged manifest; log SHA-256 `62a3036b729b9fe9b392a2d8b672a978dc3e1afecf420a91fe77c5cb581e7644`. Exact-head hosted platform execution remains pending.

The four existing platform jobs now require these exact six named cases with no skips. The actual embedded validator accepts the real passing TRX and rejects missing, empty, duplicate, skipped, failed and substituted result controls. All 20 detected embedded Python blocks compile and the added bash step parses. Validator report SHA-256 `934866c895cb290b6df2d02ec908f7507063153af3832cf15efbcf550e8acb71`; workflow SHA-256 `4e6f4b07d161534e774049809b9d5ae921c7fd15584e36911975e938cc83a696`.

## Review, causal control and preserved failures

Root and independent source review covered production lease/read/lifetime wiring and the six test cases. Independent reports are local artifacts under `prune-admission-authorization-audit/`: `loading-integration-source-review-v1.md` SHA-256 `3043ed138a47284338b8f3e6c6d581900f4f816a0ec37c47ec49449afecb3446` and corrected `overlapping-loading-test-fidelity-review-v1.md` SHA-256 `67e228283b3cd43c6589188b9678e9ba52627ec227a0a97715898d3d4e515499`. Independent review was source-only; runtime results above were root-run and inspected separately.

A compiled one-line mutation removed advisor participation preflight. The unknown-advisor regression failed: it reached the deliberately held native metadata read and timed out instead of returning the required admission refusal. The loader was restored byte-for-byte to SHA-256 `3f1fe3759e29b22bdcd210fed8406e3f9b71e8bdc0b6b1f7c70aca0481a3ca07`, then all six cases passed. Failing control TRX SHA-256 `768fefbe71fa92035f47fe46e320f95bb1379eaf99c26f5663705b58f67bd762`.

Preserved failed runs include initial fixture compile errors, a cleanup helper that recursively entered its serialized root-operation gate, the runtime observer-DI construction error hidden by that cleanup deadlock, misplaced imports, and a wrong cleanup expectation that records disappear. The latter hang dump proves no pending original assertion exception, no remaining graph load context, and an owner with released native ownership, zero reads and no release failure. `PublishedGraphUseRecord` deliberately preserves stale artifacts; the corrected test probes native Stale ownership instead of requiring deletion. Diagnosis artifact SHA-256 `19cd625cfb5439e5d08e78b5387f13a7bd1daa9fc8d8b6fe4d7eaf37cd8e2544`.

No recursive deletion or release/merge readiness is accepted by this increment. T036, T047, the driver tasks and final platform/program gates remain open.
