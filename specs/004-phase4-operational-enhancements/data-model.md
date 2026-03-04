# Data Model — Phase 4 Cluster-Convergent Runtime Loading (Lean)

## Entity: DesiredManifest

- Purpose: Canonical deterministic desired-state document shared by replicas.
- Fields:
  - `schemaVersion` (string, required)
  - `generatedAtUtc` (datetime, required)
  - `packages` (array, required, stable-sorted by `id` then `version`)
    - `id` (string, required, case-insensitive identity)
    - `version` (string, required, exact semantic/package version)
    - `sourceHint` (string, optional)
    - `sha512` (string, optional integrity hint)
- Validation:
  - Duplicate package IDs in one manifest are invalid.
  - Version ranges/floating versions are invalid.
  - Invalid manifest read/parse is degraded and non-mutating.

## Entity: DesiredManifestReadResult

- Purpose: Result envelope for manifest acquisition/parsing for a single cycle.
- Fields:
  - `status` (`Succeeded | NotFound | Unreadable | Invalid`)
  - `reasonCode` (string, required)
  - `sourceId` (string, required)
  - `correlationId` (string, required)
  - `observedAtUtc` (datetime, required)

## Entity: DesiredAggregationOutcome

- Purpose: Deterministic desired package set built from all configured desired sources.
- Fields:
  - `requestedPackages` (array of package requests, required, deterministic ordering)
  - `duplicateResolution` (array, optional)
    - `packageId` (string, required)
    - `winningSourceId` (string, required)
    - `losingSourceIds` (array, required)
    - `reasonCode` (string, required)
  - `degradedSources` (array, optional)
  - `correlationId` (string, required)
- Validation:
  - Identical source inputs produce byte-for-byte equivalent request projection.
  - Source outage is isolated to impacted source/package requests.

## Entity: ReconciliationCycleOutcome

- Purpose: Top-level reconciliation cycle summary for logs/metrics/health/admin views.
- Fields:
  - `cycleId` (string, required)
  - `correlationId` (string, required)
  - `triggerType` (`Startup | Polling | Manual`)
  - `status` (`Succeeded | Degraded | FailedNonMutating`)
  - `startedAtUtc` (datetime, required)
  - `completedAtUtc` (datetime, required)
  - `reasonCodes` (array, optional)

## Entity: AcquisitionOutcome

- Purpose: Per-package acquisition + activation stage result.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `stage` (`Resolve | Download | Validate | Activate`)
  - `status` (`Succeeded | Failed | Skipped`)
  - `reasonCode` (string, required)
  - `correlationId` (string, required)
- Validation:
  - Any stage failure preserves LKG active pointer.
  - Unrelated package requests continue where safe.

## Entity: LoaderOutcome

- Purpose: Optional loader-boundary execution result per activated package.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `status` (`Loaded | Failed | Skipped`)
  - `reasonCode` (string, required)
  - `correlationId` (string, required)
- Validation:
  - Loader failure is isolated and cannot crash host process.

## Entity: OperationalSnapshot

- Purpose: Operator-facing consistent read model.
- Fields:
  - `snapshotAtUtc` (datetime, required)
  - `activePackages` (array, required)
  - `lastReconcile` (`ReconciliationCycleOutcome`, required)
  - `healthState` (`Healthy | Degraded`)
  - `degradedReasons` (array, optional)
  - `correlationId` (string, required)

## Entity: ConvergenceOptions

- Purpose: Root configuration object for manifest/admin/loader/polling behaviors.
- Fields (representative):
  - `Manifest` (nested options)
  - `Admin` (nested options)
  - `Loader` (nested options)
  - `PollInterval` (timespan)
  - `Retry` (nested bounded retry/backoff options)
- Validation:
  - Enforced via `IValidateOptions<T>` validators.
  - Required options fail startup via `ValidateOnStart()`.

## Relationships

- One `DesiredManifestReadResult` contributes to one `DesiredAggregationOutcome` per cycle.
- One `DesiredAggregationOutcome` drives one `ReconciliationCycleOutcome` and many `AcquisitionOutcome` rows.
- Successful activation may emit one `LoaderOutcome` per package when loader is enabled.
- `OperationalSnapshot` projects active state + `ReconciliationCycleOutcome` + health for admin reads.

## State Transitions

### Reconciliation cycle

`Idle -> CollectDesiredState -> AggregateDesiredState -> AcquireAndValidate -> PublishAndSwitch -> (OptionalLoad) -> EmitObservability -> Idle`

Failure branches:

- Manifest/source failure: transition to `EmitObservability` with `Degraded` and non-mutating result for impacted scope.
- Acquisition/activation failure: rollback to LKG pointer, then `EmitObservability` with degraded reason.
- Loader failure: keep active package state unchanged, mark loader failure, continue cycle completion.
