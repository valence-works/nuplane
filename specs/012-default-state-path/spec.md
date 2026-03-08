# Feature Specification: Default State Path

**Feature Branch**: `012-default-state-path`  
**Created**: 2026-03-08  
**Status**: Draft  
**Input**: User description: "Default the store state path under the host app, add an explicit in-memory mode, and require startup logging and validation for state persistence behavior."

## Clarifications

### Session 2026-03-08

- Q: How should explicit in-memory mode be represented in configuration? → A: Add `UseInMemoryStore: bool` alongside `StateFilePath`; validation rejects using both together.
- Q: How should persistence write failures be handled when persistence is enabled? → A: Fail the reconciliation/apply operation and surface the error clearly.
- Q: What exact default state file path should be used? → A: `.nuplane/store-state.json` under `AppContext.BaseDirectory`.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Persist State By Default (Priority: P1)

As an operator hosting Nuplane, I want reconciliation state to persist automatically even when I do not configure a state file path, so that restarts keep active-version and last-known-good state without hidden configuration requirements.

**Why this priority**: Silent in-memory behavior is operationally unsafe as a default because a host can appear correctly configured while losing reconciliation state on restart. A safe default is more important than preserving the current implicit opt-out behavior.

**Independent Test**: Start a host with no explicit state path configured, perform a successful reconciliation, restart the host, and verify the previously recorded store state is reloaded from a generated host-relative path.

**Acceptance Scenarios**:

1. **Given** Nuplane starts without an explicit state file path, **When** the first successful reconciliation persists state, **Then** the system writes store state to a deterministic default path under the host application directory.
2. **Given** state was previously persisted to the default path, **When** the host restarts, **Then** the system loads the existing state before serving reconciliation-dependent operations.
3. **Given** an operator does not set a state file path, **When** startup completes, **Then** the system emits an informational log showing that the default state persistence path is in effect.

---

### User Story 2 - Explicit Ephemeral Mode (Priority: P1)

As an operator who wants short-lived or test-only behavior, I want to explicitly opt into in-memory-only state through a dedicated boolean setting so that ephemeral execution remains available without depending on a missing-path side effect.

**Why this priority**: Once default persistence is introduced, ephemeral behavior must remain available as an intentional and visible configuration choice, not as accidental omission.

**Independent Test**: Configure explicit in-memory mode, run reconciliation, restart the host, and verify no state file is created and no prior state is reloaded.

**Acceptance Scenarios**:

1. **Given** the operator enables explicit in-memory mode, **When** reconciliation records active versions, failures, or source snapshots, **Then** the system keeps that data only in memory for the lifetime of the process.
2. **Given** the operator enables explicit in-memory mode, **When** the host restarts, **Then** the system starts with empty store state and does not attempt to read a persisted state file.
3. **Given** explicit in-memory mode is enabled, **When** startup completes, **Then** the system emits a warning or informational log stating that reconciliation state persistence is disabled by configuration.

---

### User Story 3 - Fail Fast On Invalid Persistence Configuration (Priority: P2)

As an operator supplying custom persistence settings, I want invalid or conflicting state-persistence configuration to fail at startup so that Nuplane does not run with ambiguous or misleading store behavior.

**Why this priority**: Correct defaults solve the common path, but custom persistence settings still need clear validation and diagnostics to avoid production drift and silent state loss.

**Independent Test**: Configure conflicting or invalid persistence settings, start the host, and verify startup fails with a descriptive configuration validation error before runtime services begin processing.

**Acceptance Scenarios**:

1. **Given** an operator configures a blank custom state path, **When** startup validation runs, **Then** startup fails with a descriptive error.
2. **Given** an operator configures both a custom persisted path and explicit in-memory mode, **When** startup validation runs, **Then** startup fails because the persistence mode is ambiguous.
3. **Given** an operator configures a valid custom path, **When** startup completes, **Then** the system uses that path instead of the default path and logs the effective persistence mode.

### Edge Cases

- What happens when the default path's parent directory does not exist? The system MUST create required directories before the first successful save.
- What happens when the configured custom path is relative? The system MUST resolve it deterministically against the host application root before use and log the effective resolved path.
- What happens when the process cannot write to the resolved persistence location? The system MUST fail the reconciliation/apply operation, surface the failure clearly, and MUST NOT silently downgrade to in-memory persistence.
- What happens when a host upgrades from the current behavior where no path means in-memory only? The new default MUST be documented and observable in startup logs so the behavior change is visible.
- What happens when multiple reconciliation writes occur with the same effective path? Existing serialized store updates MUST remain deterministic and preserve store consistency.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The setup-to-runtime configuration pipeline MUST determine a single effective store persistence mode at startup: custom persisted path, default persisted path, or explicit in-memory mode.
- **FR-002**: When no explicit state file path is configured and explicit in-memory mode is not enabled, the system MUST resolve a deterministic default state file path of `.nuplane/store-state.json` under the host application's base directory, using a `.nuplane` storage folder convention consistent with existing package storage defaults.
- **FR-003**: The store registry runtime component MUST load persisted reconciliation state from the effective path during startup and MUST save updated active versions, last-known-good versions, failure records, and source snapshots back to that same path during runtime operations.
- **FR-004**: The setup and runtime configuration surface MUST expose a `UseInMemoryStore` boolean option that explicitly disables state persistence and runs the store registry in in-memory mode without requiring omission of the path setting as a side effect.
- **FR-005**: Startup options validation MUST reject blank custom paths, the combination of `UseInMemoryStore=true` with `StateFilePath` set, and any other persistence configuration that cannot be interpreted into a single effective mode. Validation failures MUST prevent startup.
- **FR-006**: When a custom state path is provided, the system MUST use that path instead of the default path and MUST preserve existing configured-path behavior.
- **FR-007**: Startup logging MUST record the effective persistence mode and, when persistence is enabled, the resolved state file path so operators can verify how reconciliation state will be stored.
- **FR-008**: The defaulting behavior MUST be applied before reconciliation services and operational projections begin using store state, so all runtime components observe the same effective persistence configuration.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Effective state path resolution MUST be deterministic. Given the same configuration and host root, startup MUST resolve the same persistence mode and path every time.
- **OSR-002**: Existing transactional store safety guarantees MUST remain unchanged. Persisted state updates MUST continue to protect last-known-good tracking and MUST NOT introduce partial-write ambiguity into reconciliation flows. If persistence is enabled and a store-state write fails, the reconciliation/apply operation MUST fail rather than report success with non-durable state.
- **OSR-003**: State persistence configuration MUST remain local to the host environment. No new external sources, network locations, or secret-bearing storage mechanisms may be introduced by this feature.
- **OSR-004**: The feature MUST emit structured logs for startup mode selection and persistence failures so operators can distinguish persisted mode from explicit in-memory mode and diagnose write failures quickly.
- **OSR-005**: Automated tests MUST cover default-path resolution, explicit in-memory mode, configured-path override, startup validation failures, and restart behavior proving persisted state is reloaded when persistence is enabled.

### Key Entities *(include if feature involves data)*

- **Effective Store Persistence Mode**: The resolved runtime choice describing whether the store registry uses a custom persisted path, a generated default path, or explicit in-memory behavior via `UseInMemoryStore`.
- **Effective State File Path**: The single normalized filesystem path used for loading and saving reconciliation state when persistence is enabled, defaulting to `.nuplane/store-state.json` under the host base directory.
- **Store State Record**: The persisted reconciliation snapshot containing active versions, last-known-good versions, failure records, source snapshots, and the last update timestamp.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In a host with no explicit state path configured, the first successful reconciliation creates a persisted state file at the default location without requiring any additional operator configuration.
- **SC-002**: After a host restart, persisted active-version and last-known-good state are restored from the default or custom path in 100% of automated restart validation scenarios.
- **SC-003**: Operators who want ephemeral behavior can enable explicit in-memory mode and observe zero persisted state files created across restart validation scenarios.
- **SC-004**: Invalid or conflicting persistence configuration is rejected before runtime services begin processing in 100% of validation test scenarios.
- **SC-005**: Startup logs always disclose the effective persistence mode, and when persistence is enabled, the resolved state path, making the runtime behavior directly observable.

## Assumptions

- The default persistence location is `.nuplane/store-state.json` under the host base directory, following the repository's existing host-relative storage convention rather than using an application-model-specific path such as `App_Data`.
- Introducing a default persisted path is an intentional behavior change from the current implicit in-memory fallback and is acceptable as long as explicit in-memory mode remains available.
- Explicit in-memory mode is represented by a boolean configuration property rather than an enum or separate mode object.
- The existing store serializer remains the persistence mechanism; this feature changes how the effective path and mode are chosen, not the serialization format.
