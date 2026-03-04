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
- Acquisition MUST treat local directory feeds as “artifact already present”:
  - no remote fetch is required
  - partial-write safety rules apply (see directory observation contract)

## Error Contract

- Local feed failures are explicit and actionable:
  - invalid file naming / parse failures are ignored with diagnostics
  - directory access errors are degraded and do not crash the process
  - “no feeds configured” produces explicit idle diagnostics rather than exceptions

## Test Contract

- Must cover:
  - local-directory-only operation with zero remote feeds
  - deterministic resolution behavior when both local and remote feeds exist
  - regression: prevent `InvalidOperationException: No available feed could resolve package ...` for directory-originating packages
