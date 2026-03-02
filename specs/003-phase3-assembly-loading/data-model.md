# Data Model — Phase 3 Optional Package Loading

## Entity: PackageLoadSession
- Purpose: Represents the active loading lifecycle for one package version in one reconciliation context.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `activeInstallPath` (string, required)
  - `contextKey` (string, required, unique per active package session)
  - `loadState` (enum: `NotLoaded`, `Loaded`, `LoadFailed`, `UnloadInitiated`, `UnloadPending`, `Unloaded`)
  - `lastTransitionAt` (datetime, required)
  - `lastOutcomeCode` (string, optional)
  - `correlationId` (string, required)
- Validation rules:
  - `activeInstallPath` must resolve to current package store active location.
  - At most one `Loaded` session exists per `packageId` at a time.
  - Repeated identical cycles must not create duplicate sessions.

## Entity: SharedAssemblyPolicyEntry
- Purpose: Defines which host assemblies may be shared into package load contexts.
- Fields:
  - `name` (string, required)
  - `publicKeyToken` (string, required)
  - `majorVersion` (int, required)
  - `enabled` (bool, required)
- Validation rules:
  - Match key is exactly (`name`, `publicKeyToken`, `majorVersion`).
  - Name-only matching is not permitted in default policy.

## Entity: LoadDecision
- Purpose: Captures deterministic assembly-resolution path for one requested assembly.
- Fields:
  - `packageId` (string, required)
  - `assemblyName` (string, required)
  - `resolutionSource` (enum: `SharedPolicy`, `PackageResolver`, `FrameworkFallback`, `Failed`)
  - `selectedIdentity` (string, optional)
  - `reason` (string, required)
  - `correlationId` (string, required)
  - `timestamp` (datetime, required)
- Validation rules:
  - Resolution source order is deterministic.
  - Failed decisions include explicit reason code.

## Entity: DeactivationAttempt
- Purpose: Records bounded host deactivation behavior before unload attempts.
- Fields:
  - `packageId` (string, required)
  - `requestedAt` (datetime, required)
  - `timeoutMs` (int, required)
  - `completed` (bool, required)
  - `timedOut` (bool, required)
  - `outcomeCode` (string, required)
  - `correlationId` (string, required)
- Validation rules:
  - `timeoutMs` must be > 0.
  - If `timedOut` is true, unload attempt still proceeds and is recorded.

## Entity: UnloadOutcomeRecord
- Purpose: Captures package removal unload result and retry lifecycle.
- Fields:
  - `packageId` (string, required)
  - `attemptNumber` (int, required, starts at 1)
  - `attemptedAt` (datetime, required)
  - `outcome` (enum: `Unloaded`, `UnloadPending`, `Failed`)
  - `pendingReason` (string, optional)
  - `retryEligible` (bool, required)
  - `correlationId` (string, required)
- Validation rules:
  - `UnloadPending` implies `retryEligible = true`.
  - Retry is scheduled every reconciliation cycle until `Unloaded`.

## Entity: LoadingHealthSnapshot
- Purpose: Provides loading-specific health projection for operators.
- Fields:
  - `timestamp` (datetime, required)
  - `loadedCount` (int, required)
  - `loadFailureCount` (int, required)
  - `unloadPendingCount` (int, required)
  - `healthState` (enum: `Healthy`, `Degraded`)
  - `correlationId` (string, required)
- Validation rules:
  - `healthState` must be `Degraded` when `unloadPendingCount > 0`.

## Relationships
- One `PackageLoadSession` has many `LoadDecision` records.
- One package removal flow may produce one `DeactivationAttempt` and many `UnloadOutcomeRecord` retries.
- `SharedAssemblyPolicyEntry` influences many `LoadDecision` records.
- `LoadingHealthSnapshot` aggregates active session and unload outcome data.

## State Transitions

### Package load lifecycle
1. `NotLoaded`
2. `Loaded` or `LoadFailed`
3. On removal: `UnloadInitiated`
4. `Unloaded` or `UnloadPending`
5. For pending: retry each cycle until `Unloaded`

### Unload retry lifecycle
1. `Attempted`
2. `Unloaded` or `UnloadPending`
3. If pending: `RetryScheduledNextCycle`
4. Repeat until `Unloaded` or package no longer applicable
