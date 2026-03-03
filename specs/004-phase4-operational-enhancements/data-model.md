# Data Model — Phase 4 Cluster-Convergent Runtime Loading (Lean)

## Entity: DesiredManifest
- Purpose: Deterministic desired-state artifact that drives convergence across replicas.
- Fields:
  - `schemaVersion` (string, required)
  - `generatedAt` (datetime, required)
  - `packages` (list, required)
    - `id` (string, required)
    - `version` (string, required; exact)
    - `sourceHint` (string, optional; e.g., feed name / container path)
    - `sha512` (string, optional)
- Validation rules:
  - Duplicate `id` entries are not permitted within one manifest.
  - Exact versions are required for deterministic convergence.

## Entity: DesiredManifestReadResult
- Purpose: Captures manifest read/parse outcome for one cycle.
- Fields:
  - `status` (enum: `Succeeded`, `NotFound`, `Unreadable`, `Invalid`)
  - `reasonCode` (string, required)
  - `correlationId` (string, required)
  - `observedAt` (datetime, required)
- Validation rules:
  - `Invalid` and `Unreadable` must produce degraded, non-mutating outcomes.

## Entity: DesiredAggregationOutcome
- Purpose: Deterministic aggregation result across desired sources.
- Fields:
  - `requestedPackages` (list, required)
  - `duplicateResolution` (list, optional)
    - `packageId` (string, required)
    - `selectedSource` (string, required)
    - `reasonCode` (string, required)
  - `correlationId` (string, required)
- Validation rules:
  - Identical inputs must yield identical `requestedPackages` ordering and content.

## Entity: AcquisitionOutcome
- Purpose: Captures per-package acquisition/activation boundary result.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `status` (enum: `Acquired`, `Activated`, `Failed`, `Skipped`)
  - `stage` (enum: `Resolve`, `Download`, `Validate`, `Activate`)
  - `reasonCode` (string, required)
  - `correlationId` (string, required)
- Validation rules:
  - Any failure must preserve active/LKG pointers.

## Entity: LoaderOutcome
- Purpose: Captures per-package loader boundary outcome when loader is enabled.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `status` (enum: `Loaded`, `Failed`, `Skipped`)
  - `reasonCode` (string, required)
  - `correlationId` (string, required)
- Validation rules:
  - Loader failures do not crash the host and do not corrupt the store.

## Entity: OperationalSnapshot
- Purpose: Operator-facing point-in-time state projection.
- Fields:
  - `snapshotAt` (datetime, required)
  - `activePackages` (list, required)
  - `lastReconcileOutcome` (object, required)
  - `healthState` (enum: `Healthy`, `Degraded`)
  - `correlationId` (string, required)
- Validation rules:
  - Snapshot must be internally consistent for one correlation scope.

## Relationships
- One `DesiredManifestReadResult` influences one `DesiredAggregationOutcome` per cycle.
- One `DesiredAggregationOutcome` produces zero or more `AcquisitionOutcome` results.
- When loader is enabled, each activated package may produce a `LoaderOutcome`.
- One `OperationalSnapshot` summarizes the last reconcile outcome, active set, and current health.
