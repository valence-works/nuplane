# Data Model — Local Directory Feeds + Watchers

## Entity: FeedDefinition

- Purpose: A configured package artifact source used for resolution and acquisition.
- Representation (incremental / minimal-change plan): reuse `Nuplane.Abstractions.FeedDefinition` for both remote and local feeds.
- Fields:
  - `name` (string, required, case-insensitive identity)
  - `serviceIndex` (URI, required)
    - `https://...` / `http://...` indicates a remote feed (NuGet v3 service index)
    - `file://...` indicates a local directory feed (directory path)
  - `trustLevel` (`Trusted | Restricted | Untrusted`, required)
  - `credentials` (string?, optional; remote-only)
- Validation:
  - `name` must be unique across all configured feeds.
  - `file://` feeds must point to a directory path (absolute once normalized).
  - `file://` feeds must not accept credentials.

## Entity: LocalDirectoryFeedOptions

- Purpose: Per-feed local directory observation + discovery configuration (driver-facing options).
- Fields:
  - `feedName` (string, required)
  - `directoryPath` (string, required, absolute after normalization)
  - `allowlistedPackageIds` (string[], optional; empty means all)
  - `triggerReconciliationOnChange` (bool, default: true)
  - `debounceWindow` (timespan, default: 1s)
- Validation:
  - `directoryPath` is non-empty.
  - `debounceWindow` is bounded and non-negative.
  - `feedName` matches an existing configured `FeedDefinition` with `file://` endpoint.

## Entity: PackageRequest

- Purpose: Desired-state request produced by configured inputs.
- Relevant fields (existing):
  - `id` (string, required)
  - `versionRange` (string, required)
  - `feedName` (string?, optional explicit feed preference)
  - `updatePolicy` (enum)
  - `sourceName` (string, required attribution)
- Local directory feed rule (normative for this feature):
  - For directory-discovered `.nupkg` inputs, requests use `feedName = <localDirectoryFeedName>` so the artifact source is explicit and resolution does not require any remote feed.

## Entity: ResolvedPackage (install path semantics)

- Purpose: Concrete package selected for activation, with a resolved install path.
- Relevant fields:
  - `installPath` (string, required): The file-system path where the package content is installed. The package loader expects this to be a real directory containing loadable assemblies.
- Install path rules (normative for this feature):
  - **Local directory feeds (`file://` scheme)**: The resolver MUST extract the `.nupkg` archive to a versioned install directory within the feed directory (e.g., `{feedLocalPath}/.installed/{packageId}/{version}/`) and set `installPath` to that extracted directory. Extraction MUST be idempotent (skip if directory already exists). A synthetic or placeholder path MUST NOT be used.
  - **Remote feeds**: The resolver produces a path that is populated by a future acquisition/download step. For the current implementation this is a synthetic path (e.g., `/packages/{id}/{version}`).
  - **Loader dependency**: The package loader (`PackageLoader.ResolveMainAssemblyPath`) requires `installPath` to be a real directory on disk containing `.dll` files (directly or under `lib/<tfm>/`). If `installPath` does not exist, the loader reports a boundary failure.

## Entity: ReconcileTrigger

- Purpose: Operator-visible record describing why reconciliation ran.
- Fields:
  - `correlationId` (string, required)
  - `triggerType` (`Scheduled | DirectoryChange | Manual | Startup`)
  - `triggerSource` (string?, optional; e.g., feed name for directory change)
  - `triggeredAtUtc` (datetime, required)
  - `reasonCode` (string, required)
- Validation:
  - Trigger records are emitted for every cycle, even when the cycle no-ops or is skipped due to single-flight.

## Entity: DirectoryObservationStatus

- Purpose: Health-relevant status describing whether real-time observation is active or degraded.
- Fields:
  - `feedName` (string, required)
  - `state` (`Active | Degraded | Disabled`)
  - `lastError` (string?, optional)
  - `observedAtUtc` (datetime, required)
- Validation:
  - Degraded/Disabled states must be observable via health + logs.

## Relationships

- One `LocalDirectoryFeedOptions` binds to one `FeedDefinition` whose `serviceIndex` is `file://...`.
- `LocalDirectoryFeedOptions` drives:
  - desired-state discovery (directory scanning → `PackageRequest` list)
  - observation trigger generation (watcher events → `ReconcileTrigger` of type `DirectoryChange`)
  - `DirectoryObservationStatus` updates.
- `ReconcileTrigger` is attached to (or emitted alongside) each reconciliation cycle outcome and shares its `correlationId`.

## State Transitions

### Directory observation lifecycle

`Disabled -> Active -> Degraded -> Active`

- `Disabled -> Active`: watcher successfully established for the configured directory.
- `Active -> Degraded`: watcher creation/operation fails (permissions, path invalid, OS limitations); scheduled reconciliation remains the fallback driver.
- `Degraded -> Active`: periodic retry re-establishes watcher successfully.

### Reconciliation trigger behavior

- `DirectoryChange`: coalesced watcher events emit a single trigger per debounce window.
- `Scheduled`: periodic reconciliation continues regardless of watcher state.
- `Idle`: when no feeds are configured, scheduled ticks may still occur but cycles produce an explicit idle/no-input diagnostic instead of throwing.
