# Feature Specification: Startup Reconciliation & Loading Events

**Feature Branch**: `009-startup-and-loading-events`  
**Created**: 2026-03-05  
**Status**: Draft  
**Input**: User description: "Startup reconciliation and loading events for INuplaneObserver"

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Packages Available Immediately on Startup (Priority: P1)

As a host application developer, I want packages already present in my configured feeds or drop-folders to be loaded immediately when the application starts, so that I do not have to wait up to 60 seconds for the first periodic reconciliation tick before my plugins are available.

**Why this priority**: Without startup reconciliation, hosts that rely on pre-deployed packages experience a dead window after launch where no plugins are available. This is the most impactful gap — it blocks real-world production usage.

**Independent Test**: Deploy a package to the configured source before starting the host. Start the host with automatic reconciliation enabled. Verify that the package is loaded and available for type scanning before the first periodic timer tick fires.

**Acceptance Scenarios**:

1. **Given** a package exists in a configured feed/drop-folder, **When** the host application starts with automatic reconciliation enabled, **Then** the package is loaded before the first periodic timer tick fires.
2. **Given** no packages exist in any configured source, **When** the host application starts, **Then** the startup reconciliation cycle completes without error and the host continues into normal periodic reconciliation.
3. **Given** a package in the feed is corrupted or fails trust validation, **When** the startup reconciliation cycle runs, **Then** the failure is handled with last-known-good safety semantics (no crash, no partial state) and is observable through existing failure events.
4. **Given** automatic reconciliation is disabled, **When** the host application starts, **Then** no startup reconciliation cycle runs.

---

### User Story 2 — Notified When Packages Are Loaded (Priority: P1)

As a host application developer, I want to receive a dedicated event when packages are loaded into the application (assemblies loaded into AssemblyLoadContexts), so that I can discover types and activate plugins at the correct moment — without conflating loading with reconciliation state changes.

**Why this priority**: The current workaround (using `OnPackagesChangedAsync` as a proxy for loading) is semantically incorrect and couples host plugin discovery to reconciliation internals. A clean loading event is essential for correct host integration.

**Independent Test**: Register an observer that implements the loading event handler. Trigger a reconciliation cycle that loads a new package. Verify that the loading event fires with the correct package information and that the loaded assemblies are scannable at the time the event fires.

**Acceptance Scenarios**:

1. **Given** a reconciliation cycle loads a new package, **When** assembly loading succeeds, **Then** the `OnPackagesLoadedAsync` event fires with the list of newly loaded packages.
2. **Given** a reconciliation cycle loads multiple packages, **When** all assemblies are loaded, **Then** the `OnPackagesLoadedAsync` event fires once per cycle containing all loaded packages.
3. **Given** loading fails for one package but succeeds for others, **When** the cycle completes the loading stage, **Then** `OnPackagesLoadedAsync` fires with only the successfully loaded packages, and the failed package triggers the existing failure event.
4. **Given** no new packages need loading in a cycle, **When** the cycle completes, **Then** `OnPackagesLoadedAsync` does not fire.
5. **Given** an observer does not implement `OnPackagesLoadedAsync`, **When** the event fires, **Then** the default no-op implementation is used and no error occurs (backward compatibility).

---

### User Story 3 — Notified When Packages Are Unloaded (Priority: P2)

As a host application developer, I want to receive a dedicated event when packages are unloaded from the application (assemblies removed from AssemblyLoadContexts), so that I can clean up references, deregister services, or log unload activity.

**Why this priority**: Unload events complete the loading lifecycle and are important for hosts that manage long-lived references to plugin types, but most hosts can function initially without explicit unload notification.

**Independent Test**: Register an observer that implements the unload event handler. Remove a package from the desired state and trigger reconciliation. Verify that the unload event fires with the correct package information after the assembly is unloaded.

**Acceptance Scenarios**:

1. **Given** a reconciliation cycle removes a package from the desired state, **When** the assembly is successfully unloaded, **Then** `OnPackagesUnloadedAsync` fires with the unloaded package information.
2. **Given** an unload is pending (references still held), **When** the unload cannot complete in this cycle, **Then** `OnPackagesUnloadedAsync` does not fire for the pending package.
3. **Given** an observer does not implement `OnPackagesUnloadedAsync`, **When** the event fires, **Then** the default no-op implementation is used and no error occurs (backward compatibility).

---

### User Story 4 — Startup Loading Event Enables Host Initialization (Priority: P1)

As a host application developer, I want the startup reconciliation cycle to produce the same loading events as any other cycle, so that I can use a single event handler (`OnPackagesLoadedAsync`) for both initial discovery and runtime updates — without writing special startup initialization code.

**Why this priority**: Unifying startup and runtime loading into one event handler is the core design goal. If startup loading fired a different event or required separate handling, the feature would fail its purpose.

**Independent Test**: Deploy packages to the source, start the host, and verify that `OnPackagesLoadedAsync` fires during the startup cycle with the full initial package set. Then add another package and verify the same event fires again during a subsequent periodic cycle.

**Acceptance Scenarios**:

1. **Given** packages exist in configured sources at startup, **When** the startup reconciliation cycle loads them, **Then** `OnPackagesLoadedAsync` fires with the full set of initially loaded packages.
2. **Given** the host receives `OnPackagesLoadedAsync` during startup, **When** a subsequent periodic cycle loads an additional package, **Then** the same `OnPackagesLoadedAsync` event fires with only the newly loaded package.
3. **Given** the host implements only `OnPackagesLoadedAsync`, **When** both startup and periodic loading occur, **Then** both are handled by the same observer method with no special-case code.

---

### Edge Cases

- What happens when the startup reconciliation cycle is still running when the first periodic timer tick fires? The existing single-flight protection must prevent concurrent cycles.
- What happens when an observer throws an exception in `OnPackagesLoadedAsync`? The exception must be caught and logged (consistent with existing observer error handling) without interrupting the reconciliation pipeline.
- What happens when loading is disabled (`LoadingOptions.Enabled = false`)? Loading events must not fire; the feature is inert.
- What happens when the startup cycle loads zero packages (empty sources)? No loading event fires; the host proceeds normally into periodic reconciliation.
- What happens when a pending unload from a prior cycle completes in a subsequent cycle? The `OnPackagesUnloadedAsync` event fires in the cycle where the unload actually completes.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The `ReconciliationHostedService` MUST execute an immediate reconciliation cycle with `TriggerType.Startup` before entering the periodic timer loop, so that pre-existing packages are loaded without waiting for the first `PollInterval` tick. This cycle MUST respect the same middleware pipeline, single-flight protection, and error handling as periodic cycles.

- **FR-002**: The `INuplaneObserver` interface MUST define an `OnPackagesLoadedAsync(PackageLoadedEvent loadedEvent, CancellationToken ct)` method with a default no-op implementation (`=> Task.CompletedTask`) for backward compatibility. This method fires after assemblies have been successfully loaded into AssemblyLoadContexts during a reconciliation cycle.

- **FR-003**: The `INuplaneObserver` interface MUST define an `OnPackagesUnloadedAsync(PackageUnloadedEvent unloadedEvent, CancellationToken ct)` method with a default no-op implementation (`=> Task.CompletedTask`) for backward compatibility. This method fires after assemblies have been successfully unloaded from AssemblyLoadContexts during a reconciliation cycle.

- **FR-004**: A `PackageLoadedEvent` record MUST be defined in `Nuplane.Abstractions` containing the list of successfully loaded packages, the trigger type of the cycle, and the correlation identifier.

- **FR-005**: A `PackageUnloadedEvent` record MUST be defined in `Nuplane.Abstractions` containing the list of successfully unloaded packages, the trigger type of the cycle, and the correlation identifier.

- **FR-006**: The `IObserverEventDispatcher` interface MUST expose a `PublishLoadedAsync` method to dispatch `OnPackagesLoadedAsync` to all registered observers, and a `PublishUnloadedAsync` method to dispatch `OnPackagesUnloadedAsync` to all registered observers.

- **FR-007**: The `ObserverEventDispatcher` MUST implement `PublishLoadedAsync` and `PublishUnloadedAsync` using the same error-isolation pattern as existing dispatch methods (catch and log per-observer exceptions without interrupting dispatch to remaining observers).

- **FR-008**: The `PackageLoadingMiddleware` MUST invoke `PublishLoadedAsync` after successfully loading packages, passing only the packages that were successfully loaded in the current cycle. If no packages were loaded, the event MUST NOT fire.

- **FR-009**: The `UnloadMiddleware` MUST invoke `PublishUnloadedAsync` after successfully unloading packages, passing only the packages that were fully unloaded (not pending) in the current cycle. If no packages were fully unloaded, the event MUST NOT fire.

- **FR-010**: The existing `OnPackagesChangingAsync`, `OnPackagesChangedAsync`, and `OnPackageFailedAsync` events MUST remain unchanged in signature, behavior, and pipeline position. Loading events are additive and orthogonal to reconciliation events.

- **FR-011**: The sample `PluginDiscoveryObserver` MUST be updated to implement `OnPackagesLoadedAsync` for plugin type scanning instead of using `OnPackagesChangedAsync` as a proxy. The `OnPackagesChangedAsync` implementation SHOULD be simplified to logging-only (reconciliation audit) or removed.

- **FR-012**: The sample `Program.cs` MUST set `EnableAutomaticReconciliation = true` so that startup reconciliation is demonstrated in the sample application.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: The startup reconciliation cycle MUST be idempotent — if the host is restarted and the same packages are still present, the cycle MUST produce the same loading outcome without duplicate activations or state corruption.

- **OSR-002**: If the startup reconciliation cycle fails (transient source error, trust failure, loading failure), the host MUST remain stable with last-known-good state preserved. The failure MUST be logged with a correlation identifier and surfaced through existing observer failure events (`OnPackageFailedAsync`, `OnScopedFailureAsync`).

- **OSR-003**: The startup reconciliation cycle MUST emit structured logs with a correlation identifier that distinguishes it from periodic cycles (via `TriggerType.Startup`). Loading and unloading events MUST be included in reconciliation metrics (load/unload counts, durations).

- **OSR-004**: Observer dispatch for loading and unloading events MUST follow the same error-isolation pattern as existing events: per-observer exceptions are caught and logged, never propagated to the middleware pipeline or other observers.

- **OSR-005**: Automated tests MUST cover: (a) startup cycle fires before periodic timer, (b) `OnPackagesLoadedAsync` fires with correct packages after loading, (c) `OnPackagesUnloadedAsync` fires with correct packages after unloading, (d) backward compatibility — observers without loading/unloading method implementations receive no errors, (e) observer exceptions in loading/unloading callbacks are isolated.

### Key Entities

- **PackageLoadedEvent**: Represents the outcome of successful package loading in a reconciliation cycle. Contains the list of loaded packages, the trigger type (Startup, Scheduled, Manual, DirectoryChange), and the correlation identifier for the cycle.

- **PackageUnloadedEvent**: Represents the outcome of successful package unloading in a reconciliation cycle. Contains the list of unloaded packages, the trigger type, and the correlation identifier for the cycle.

- **TriggerType.Startup**: An existing enum value that identifies the first automatic reconciliation cycle after host startup. Currently defined but unused — this feature activates it.

## Assumptions

- The startup reconciliation cycle uses the same middleware pipeline as all other cycles. No special startup-only middleware or bypass logic is introduced.
- `TriggerType.Startup` is already defined in `ReconciliationTrigger.cs` and requires no modifications to the enum itself.
- The `PackageLoadedEvent` and `PackageUnloadedEvent` types belong in `Nuplane.Abstractions` because they are part of the public observer contract.
- Loading events fire from within the existing middleware pipeline positions: `PackageLoadingMiddleware` for load events, `UnloadMiddleware` for unload events. No middleware reordering is required.
- Default interface method implementations (`=> Task.CompletedTask`) provide backward compatibility for existing observer implementations that do not implement the new methods.
- The sample application update (FR-011, FR-012) demonstrates the intended usage pattern but does not change any runtime library behavior.
- Single-flight protection (existing `EnableSingleFlight` option) prevents the startup cycle from running concurrently with a timer-triggered or directory-watcher-triggered cycle.
- When loading is disabled (`LoadingOptions.Enabled = false`), loading events do not fire because the `PackageLoadingMiddleware` skips loading entirely.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: When packages are pre-deployed to a configured source, they are loaded and available for host type scanning within 5 seconds of application startup (not delayed until the first periodic timer tick).
- **SC-002**: 100% of reconciliation cycles that load new packages result in an `OnPackagesLoadedAsync` event firing with exactly the set of successfully loaded packages.
- **SC-003**: 100% of reconciliation cycles that unload packages result in an `OnPackagesUnloadedAsync` event firing with exactly the set of fully unloaded packages (excluding pending unloads).
- **SC-004**: Existing observer implementations that do not implement the new loading/unloading methods continue to function with zero errors or behavioral changes after the update.
- **SC-005**: The sample application demonstrates end-to-end startup loading: deploy a package, start the app, and observe plugin discovery via `OnPackagesLoadedAsync` in application logs — with no code changes beyond the observer method switch.
- **SC-006**: On startup reconciliation failure, the host remains operational and the failure is visible in structured logs with a correlation identifier containing the `Startup` trigger type.
