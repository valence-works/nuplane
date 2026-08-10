# Contract: Local Directory Feeds

## Interface Boundary

- Local directory feeds are treated as first-class feed definitions and participate in:
  - desired-state discovery (enumerate `.nupkg` artifacts)
  - feed resolution (as eligible candidate feeds)
  - acquisition (local artifact path instead of remote download)

## Configuration Contract

- A local directory feed is represented as a `FeedDefinition` where:
  - `ServiceIndex` uses `file://...` and points at a directory
  - `Name` is a stable, unique identifier
  - `TrustLevel` is enforced the same way as for remote feeds (Trusted/Restricted/Untrusted)
  - `Credentials` is not permitted

## Behavioral Contract

- Directory-discovered packages MUST produce `PackageRequest` values that:
  - carry explicit `FeedName = <localDirectoryFeedName>` (so local acquisition is eligible without remote feeds)
  - carry `SourceName` attribution for diagnostics
- Resolution MUST be deterministic:
  - if `FeedName` is set, only that feed is eligible
  - if no eligible feed exists, the package fails with an explicit failure outcome (no unhandled exception)
- Acquisition MUST treat local directory feeds as "artifact already present":
  - no remote fetch is required
  - the resolver MUST locate the `.nupkg` file in the feed directory using the conventional filename `{packageId}.{version}.nupkg`
  - the resolver MUST extract the `.nupkg` contents to a versioned install directory under the configured package install root (`{PackageInstallRoot}/{feedName}/{packageId}/{version}/`, superseded by issue #56)
  - `ResolvedPackage.InstallPath` MUST point to the extracted directory on disk, NOT a synthetic or placeholder path
  - the loader depends on `InstallPath` being a real directory containing assemblies (under `lib/<tfm>/` or root); a non-existent path causes a loader boundary failure
  - extraction MUST be idempotent (skip if directory already exists)
  - partial-write safety rules apply (see directory observation contract)

## Error Contract

- Local feed failures are explicit and actionable:
  - invalid file naming / parse failures are ignored with diagnostics
  - directory access errors are degraded and do not crash the process
  - "no feeds configured" produces explicit idle diagnostics rather than exceptions
  - missing `.nupkg` file for a resolved package produces a `FileNotFoundException` with the expected file path and feed name
- Failure propagation to observers:
  - when a package transaction fails, the observer notification MUST include the specific failure reason (e.g., policy gate message, stage failure message), not just a generic "failed to apply" wrapper
  - `PackageApplyExecutionResult` MUST carry per-package failure messages so downstream middleware and observers can surface actionable diagnostics

## Test Contract

- Must cover:
  - local-directory-only operation with zero remote feeds
  - deterministic resolution behavior when both local and remote feeds exist
  - regression: prevent `InvalidOperationException: No available feed could resolve package ...` for directory-originating packages
  - install path validation: resolved `InstallPath` for local feeds MUST be a real directory on disk (assert `Directory.Exists(result.InstallPath)`)
  - nupkg extraction idempotency: resolving the same package twice MUST NOT fail and MUST reuse the existing extracted directory
  - missing nupkg: resolving a package whose `.nupkg` file does not exist in the feed directory MUST produce an explicit `FileNotFoundException`
