# Data Model — Phase 1 Runtime Baseline

## Entity: FeedDefinition
- Purpose: Defines a trusted package feed for remote resolution.
- Fields:
  - `name` (string, required, unique)
  - `serviceIndex` (uri, required)
  - `trustLevel` (enum: `trusted`, `restricted`)
  - `credentialsRef` (string, optional; runtime secret reference only)
- Validation rules:
  - `name` must be unique across configured feeds.
  - `serviceIndex` must be absolute HTTPS URI.

## Entity: DesiredSourceDefinition
- Purpose: Configures desired-state source participation.
- Fields:
  - `sourceName` (string, required, unique)
  - `sourceType` (enum: `explicit`, `directory`)
  - `enabled` (bool, required)
  - `allowlistPackageIds` (set<string>, required, non-empty for Phase 1)
  - `lastSnapshotVersion` (string, optional)
  - `lastSnapshotAt` (datetime, optional)
- Validation rules:
  - Only configured + enabled sources influence reconciliation.
  - Non-allowlisted package IDs are rejected pre-resolution.

## Entity: PackageRequest
- Purpose: Captures desired package intent.
- Fields:
  - `id` (string, required)
  - `versionRange` (string, required)
  - `feedName` (string, optional in explicit requests)
  - `updatePolicy` (enum: `exact`, `range`)
  - `sourceName` (string, required)
- Validation rules:
  - `id` must match allowlist before inclusion.
  - Duplicate IDs resolve deterministically by highest-version-wins and source-name tie-break.

## Entity: ResolvedPackage
- Purpose: Concrete package selected for activation.
- Fields:
  - `id` (string, required)
  - `version` (string, required)
  - `feedName` (string, required for feed-resolved packages)
  - `stagingPath` (string, required during transaction)
  - `installPath` (string, required after publish)
  - `resolvedAt` (datetime, required)
- Validation rules:
  - `version` must satisfy originating `versionRange`.
  - Package identity/version must pass validation before publish/activation.

## Entity: PackageChangeSet
- Purpose: Cycle-level change summary and event payload.
- Fields:
  - `correlationId` (string, required)
  - `timestamp` (datetime, required)
  - `added` (list<ResolvedPackage>)
  - `updated` (list<ResolvedPackage>)
  - `removed` (list<string packageId>)
  - `cycleStatus` (enum: `success`, `partial-failure`, `failure`)
- Validation rules:
  - `correlationId` is stable across logs/events/metrics for one cycle.

## Entity: StoreStateRecord
- Purpose: Persisted deterministic store state for restart safety.
- Fields:
  - `activeVersionById` (map<string id, string version>)
  - `lastKnownGoodById` (map<string id, string version>)
  - `lastFailureById` (map<string id, FailureRecord>)
  - `lastSuccessfulSourceSnapshots` (map<string sourceName, SnapshotRef>)
  - `updatedAt` (datetime)
- Validation rules:
  - Active pointer and state metadata updates are committed only after atomic switch success.

## Entity: FailureRecord
- Purpose: Captures package-level failure diagnostics.
- Fields:
  - `packageId` (string, required)
  - `stage` (enum: `source-read`, `resolve`, `stage`, `validate`, `publish`, `activate`, `persist-state`)
  - `message` (string, required)
  - `occurredAt` (datetime, required)
  - `correlationId` (string, required)

## Relationships
- `DesiredSourceDefinition` produces many `PackageRequest`.
- `PackageRequest` resolves to zero or one `ResolvedPackage` per cycle.
- `PackageChangeSet` references many `ResolvedPackage` and package IDs.
- `StoreStateRecord` references many package IDs and optional `FailureRecord` entries.

## State Transitions

### Per-package transaction
1. `NotPresent` -> `Staged`
2. `Staged` -> `Validated`
3. `Validated` -> `PublishedImmutable`
4. `PublishedImmutable` -> `Activated` (atomic pointer switch)
5. `Activated` -> `StatePersisted`
6. Any step failure -> `Failed` with no active-pointer regression; LKG remains active.

### Health state
1. `Healthy` -> `Degraded` when any source/package failure occurs in cycle.
2. `Degraded` -> `Healthy` only after a fully successful cycle with fresh reads from all configured sources.
