# ADR-0001: Lock-file artifact provenance

- Status: Accepted
- Date: 2026-10-05
- Owner: Sipke Schoorstra
- Related issue: [#104](https://github.com/valence-works/nuplane/issues/104)
- Finding: AR-003

## Context

Nuplane's public lock-file model records a package ID, version, feed, hash, and timestamp, but schema `1.0` does not define the hash representation or what bytes are hashed. The runtime currently evaluates lock entries after package acquisition and graph construction, drops the expected hash before transaction execution, does not compute an actual package hash, and never generates a lock file in production.

AR-003 requires each lock entry to act as one provenance constraint over the root package and complete dependency closure. The selected ID, version, feed, artifact, install path, graph node, and active state must agree before any state or active pointer mutation.

## Decision drivers

- Bind a lock entry to the exact artifact supplied by the selected feed.
- Use one canonical representation that is deterministic across supported operating systems.
- Make the algorithm explicit so future migrations cannot be confused with legacy free-form values.
- Preserve the existing `LockFileOptions` property names and `PackageLockEntry.Hash` JSON field.
- Validate hashes before activation and preserve LKG on every failure.
- Keep signature and feed-trust policy separate from byte-integrity verification.

## Options considered

### Option A — SHA-512 of the exact nupkg archive with an explicit prefix

Serialize the digest as `sha512:<standard-padded-base64>` and version the lock schema as `2.0`.

Benefits:

- Binds the lock to every byte delivered by the selected feed, including package signatures.
- Uses NuGet's established package-hash algorithm and Base64 encoding while removing algorithm ambiguity.
- Can be computed before extraction for local and remote feeds.
- Leaves room for a future explicitly versioned algorithm migration.

Costs and risks:

- Re-signing otherwise identical package content changes the digest and requires lock regeneration.
- Nuplane must persist the computed digest with an installed package because remote staging archives are deleted after extraction.

### Option B — NuGet package `contentHash`

Use NuGet's package-content hash representation.

Benefits:

- Closely resembles `packages.lock.json` behavior.
- Remains stable across some signature-only archive changes.

Costs and risks:

- Does not bind the lock to the exact signed archive delivered by the selected feed.
- Signed and unsigned packages require different content-hash semantics.

### Option C — Hash the extracted package tree

Hash normalized extracted paths and file contents.

Benefits:

- Can be recomputed when the original archive is no longer present.

Costs and risks:

- Requires a Nuplane-specific canonical filesystem manifest.
- Creates additional cross-platform path, metadata, and normalization rules.
- Does not identify the exact acquired archive.

## Accepted decision: canonical artifact hash

The owner accepted Option A on 2026-10-05.

For lock schema `2.0`, `PackageLockEntry.Hash` is:

```text
sha512:<standard-padded-base64>
```

The digest input is every byte of the exact `.nupkg` archive acquired from the lock-selected feed, before extraction. Signed-package signature bytes are included. The algorithm token is the lowercase ASCII string `sha512`; the separator is `:`; the payload is canonical standard Base64 with padding and must decode to exactly 64 bytes. Producers emit only this form. Consumers reject malformed or non-canonical schema `2.0` values instead of normalizing them silently.

This is an artifact-integrity hash, not NuGet's signed-package `contentHash`. It proves byte identity only; signature validation, publisher identity, and feed trust remain separate policy concerns.

## Accepted decision: legacy and missing hashes

The owner accepted the following compatibility policy on 2026-10-05:

- Enforce and Strict reject unsupported lock schemas.
- Enforce and Strict reject an existing entry without a valid canonical schema `2.0` hash.
- Legacy schema `1.0` is readable only to produce an actionable migration diagnostic; it is not silently trusted for activation.
- Generate mode may replace a successfully resolved legacy lock with a complete schema `2.0` lock atomically.
- Enforce continues to allow a package with no entry, preserving its existing version/feed fallback semantics; Strict requires entries for every acquired root and dependency.

The accepted trade-off is a deliberate migration requirement for existing lock files in exchange for never presenting unverified Enforce or Strict activation as provenance-locked.

## Consequences

### Positive

- Root and dependency artifacts share one unambiguous integrity contract.
- Hash comparisons are independent of filesystem extraction behavior.
- Lock generation can be deterministic and verified on Linux, macOS, and Windows.

### Negative and accepted trade-offs

- Package re-signing is treated as an artifact change.
- Existing installations need persisted artifact-hash metadata for cache reuse.
- Schema `1.0` lock files must be regenerated before Enforce or Strict activation.

## Validation and evidence

- The NuGet V3 catalog specifies SHA-512 package hashes encoded with standard Base64: <https://learn.microsoft.com/nuget/api/catalog-resource>.
- NuGet's package extraction path computes an archive SHA-512 separately from signed-package content hashing: <https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Packaging/PackageExtractor.cs>.
- AR-003 evidence and acceptance checks are recorded in `docs/reviews/codebase-quality-review.md`.

## Revisit conditions

Revisit this decision if NuGet adopts a different mandatory package-integrity algorithm, a demonstrated collision or ecosystem interoperability problem affects SHA-512 archive hashes, or Nuplane introduces a versioned multi-algorithm lock schema.
