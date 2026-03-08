# Research: Default State Path

## Decision 1: Default persistence path uses the existing `.nuplane` host-relative convention

- **Decision**: Resolve the default persisted state file to `.nuplane/store-state.json` under `AppContext.BaseDirectory`.
- **Rationale**: The repo already uses `AppContext.BaseDirectory/.nuplane/packages` for default package installs in `NuGetRemotePackageAcquirer`, so the same storage root keeps host-relative data layout consistent and host-neutral.
- **Alternatives considered**:
  - `App_Data/...`: rejected because it is web-app flavored and inconsistent with the existing `.nuplane` convention.
  - `.nuplane/state/store-state.json`: rejected because the extra nesting adds ceremony without current value.

## Decision 2: Explicit in-memory mode is a boolean option, not inferred from omission

- **Decision**: Add `UseInMemoryStore: bool` to both `NuplaneSetupOptions` and `StoreRegistryOptions`, and expose a matching builder method to configure it programmatically.
- **Rationale**: A boolean fits the current option model, makes the opt-out explicit, and preserves a simple three-way outcome: configured path, generated default path, or explicit in-memory mode.
- **Alternatives considered**:
  - Enum/string `PersistenceMode`: rejected as unnecessary complexity for one explicit opt-out.
  - Continue inferring in-memory mode from missing path: rejected because it causes silent degradation and is the bug this feature fixes.

## Decision 3: Centralize effective-mode/path resolution in the store layer

- **Decision**: Introduce a small resolved-settings model/helper in `Nuplane.Store.State` that normalizes `StoreRegistryOptions` into an effective mode and normalized path before store operations proceed.
- **Rationale**: The codebase already separates data options from validation and runtime behavior. A dedicated runtime-resolution step prevents duplicating path-defaulting logic across service registration, setup translation, and `StoreRegistry`.
- **Alternatives considered**:
  - Compute the default path directly inside `StoreRegistry` from raw options: rejected because it mixes interpretation, logging, and persistence behavior into one type.
  - Compute the default path during configuration binding only: rejected because runtime tests and direct DI configuration still need one authoritative resolution path.

## Decision 4: Validation uses the .NET options pipeline at both setup and runtime layers

- **Decision**: Extend `NuplaneSetupOptionsValidator` for setup-surface conflicts and add a new `StoreRegistryOptionsValidator` registered via `IValidateOptions<StoreRegistryOptions>` with `ValidateOnStart()`.
- **Rationale**: This follows the repository's constitution and the existing validation pattern in `NuplaneServiceCollectionExtensions`, where option sets are data-only and fail fast before runtime services start.
- **Alternatives considered**:
  - Ad-hoc validation in builder methods or service constructors: rejected because it scatters policy and violates the repo's options-validation discipline.
  - Setup-layer validation only: rejected because `StoreRegistryOptions` can also be configured directly through the runtime options section and needs its own fail-fast validation.

## Decision 5: Persisted-mode write failure remains fatal to the reconciliation/apply operation

- **Decision**: Do not degrade to in-memory mode after a failed persisted-state write; let the current save failure continue to fail the operation when persistence is enabled.
- **Rationale**: The constitution's transactional store safety rule requires durable state updates to remain part of successful reconciliation. Reporting success without persisted metadata would undermine restart behavior and LKG semantics.
- **Alternatives considered**:
  - Log and continue with in-memory state: rejected because it produces a non-durable success state.
  - Retry indefinitely: rejected because bounded failure behavior is safer and simpler; current serializer failures should remain immediate and visible.

## Decision 6: Preserve low-level direct-construction in-memory semantics for tests

- **Decision**: Keep the direct `StoreRegistry(IStoreStateSerializer, string? stateFilePath)` constructor behavior as-is for manual test composition, while the DI/options pipeline resolves the new default path behavior.
- **Rationale**: Many existing tests intentionally pass `stateFilePath: null` to model in-memory behavior. Preserving that low-level constructor prevents unnecessary churn while still fixing the runtime configuration bug where omitted configuration currently disables persistence by accident.
- **Alternatives considered**:
  - Make every null constructor path default to `.nuplane/store-state.json`: rejected because it would unexpectedly change many targeted tests and obscure explicit test intent.

## Decision 7: Effective mode/path must be logged from the authoritative runtime resolution point

- **Decision**: Emit structured startup logs from the runtime component that resolves the effective store settings, including the chosen mode and the fully resolved path when persistence is enabled.
- **Rationale**: Existing hosted services use structured `ILogger` patterns. Logging from the authoritative resolution point prevents duplicate or inconsistent messages and makes the behavior visible during startup/debugging.
- **Alternatives considered**:
  - Log only from configuration binding: rejected because there is no logger at binding time and it would not cover programmatic builder configuration consistently.
  - Log only on first save failure: rejected because operators also need to see the chosen mode during normal startup.