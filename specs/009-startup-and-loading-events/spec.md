# Feature Specification: Startup Reconciliation & Loading Events

**Feature Branch**: `009-startup-and-loading-events`  
**Created**: 2026-03-05  
**Status**: Revised 2026-03-05  
**Input**: Startup reconciliation + a Loading-owned observer that automatically loads reconciled packages and publishes `PackageLoadedEvent` to a separate `IPackageLoadingObserver` interface

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Packages Available Immediately on Startup (Priority: P1)

As a host application developer, I want packages already present in my configured sources to be loaded immediately when the application starts, so that I do not have to wait up to 60 seconds for the first periodic reconciliation tick before my plugins are available.

**Why this priority**: Without startup reconciliation, hosts that rely on pre-deployed packages experience a dead window after launch where no plugins are available. This is the most impactful gap — it blocks production usage.

**Independent Test**: Deploy a package to the configured source before starting the host. Start the host with automatic reconciliation enabled. Verify that the package is loaded and a `PackageLoadedEvent` is received by the host's `IPackageLoadingObserver` before the first periodic timer tick fires.

**Acceptance Scenarios**:

1. **Given** a package exists in a configured source, **When** the host starts with automatic reconciliation enabled, **Then** the package is reconciled and loaded before the first periodic timer tick fires.
2. **Given** no packages exist in any configured source, **When** the host starts, **Then** the startup reconciliation cycle completes without error and the host enters normal periodic reconciliation.
3. **Given** a package fails trust validation during the startup cycle, **When** the startup reconciliation runs, **Then** the failure is handled with last-known-good safety semantics and is observable through existing failure events.
4. **Given** automatic reconciliation is disabled, **When** the host starts, **Then** no startup reconciliation cycle runs.

---

### User Story 2 — Notified When Packages Are Loaded (Priority: P1)

As a host application developer, I want to receive a dedicated `PackageLoadedEvent` when the Loading library loads new packages into the application (assemblies loaded into AssemblyLoadContexts), so that I can discover types and activate plugins at the correct moment — independent of the reconciliation state machine.

**Why this priority**: The current pattern of using `OnPackagesChangedAsync` as a proxy for loading is semantically incorrect — reconciliation changed-state events are not loading events. A clean, Loading-owned event is essential for correct host integration and proper domain separation.

**Independent Test**: Register an `IPackageLoadingObserver`. Trigger a reconciliation cycle that includes a new package. Verify that `OnPackagesLoadedAsync` fires with the correct `PackageLoadSession` entries and that the loaded assemblies are scannable immediately after the event fires.

**Acceptance Scenarios**:

1. **Given** a reconciliation cycle adds or updates packages, **When** the `PackageAutoLoadingObserver` successfully loads their assemblies, **Then** `OnPackagesLoadedAsync` fires with all successfully loaded `PackageLoadSession` entries.
2. **Given** loading fails for one package but succeeds for others, **When** the observer completes loading, **Then** `OnPackagesLoadedAsync` fires with only the successfully loaded sessions, and `IPackageLoadingObserver.OnPackageLoadFailedAsync` fires for the failed one.
3. **Given** no packages need loading in a cycle (no additions or updates), **When** the cycle completes, **Then** `OnPackagesLoadedAsync` does not fire.
4. **Given** loading is disabled (`LoadingOptions.Enabled = false`), **When** reconciliation runs, **Then** `OnPackagesLoadedAsync` does not fire.
5. **Given** a host does not implement `IPackageLoadingObserver`, **When** the event fires, **Then** no error occurs.

---

### User Story 3 — Startup Loading Uses the Same Event (Priority: P1)

As a host application developer, I want the startup reconciliation cycle to produce the same `PackageLoadedEvent` as any subsequent cycle, so that I can use a single `IPackageLoadingObserver.OnPackagesLoadedAsync` handler for both initial discovery and runtime updates.

**Why this priority**: Unified startup and runtime loading is the core design goal. Any special-casing for startup would force hosts to write separate initialization code.

**Independent Test**: Deploy packages to the source, start the host, observe `OnPackagesLoadedAsync` firing for the startup cycle. Drop a new package, verify the same event fires again for the periodic cycle.

**Acceptance Scenarios**:

1. **Given** packages exist at startup, **When** the startup cycle reconciles and loads them, **Then** `OnPackagesLoadedAsync` fires with all loaded packages.
2. **Given** a subsequent periodic cycle loads an additional package, **When** the Loading observer processes the change, **Then** `OnPackagesLoadedAsync` fires with only the newly loaded package.
3. **Given** a host implements only `IPackageLoadingObserver.OnPackagesLoadedAsync`, **When** both startup and periodic loading occur, **Then** both are handled by the same method with no special-case code.

---

### Edge Cases

- What happens when the startup cycle is still running when the first timer tick fires? The existing single-flight protection prevents concurrent cycles.
- What happens when an `IPackageLoadingObserver` throws in `OnPackagesLoadedAsync`? The exception is caught and logged per-observer; other observers are not interrupted.
- What happens when `LoadingOptions.Enabled = false`? The `PackageAutoLoadingObserver` skips loading entirely; no event fires.
- What happens when the startup cycle produces no changes (empty sources)? `OnPackagesLoadedAsync` does not fire; no error.
- What happens when the Loading observer's `IPackageLoader.EnsureLoadedAsync` fails for all packages? `OnPackagesLoadedAsync` does not fire; per-package failures are reported via the existing reconciliation failure path.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The `ReconciliationHostedService` MUST execute an immediate reconciliation cycle with `TriggerType.Startup` before entering the periodic timer loop, so that pre-existing packages are reconciled and loaded without waiting for the first `PollInterval` tick. This cycle MUST respect the same middleware pipeline, single-flight protection, and error handling as periodic cycles.

- **FR-002**: A `PackageAutoLoadingObserver` class MUST be defined in `Nuplane.Loading.Hosting`. It MUST implement `INuplaneObserver`. On `OnPackagesChangedAsync`, it MUST call `IPackageLoader.EnsureLoadedAsync` for all packages in `changeSet.Added` and `changeSet.Updated`, then dispatch `PackageLoadedEvent` to all registered `IPackageLoadingObserver` instances via `ILoadingEventDispatcher`. If `LoadingOptions.Enabled` is false or the changed set is empty, it MUST skip loading and not dispatch an event.

- **FR-003**: An `IPackageLoadingObserver` interface MUST be defined in `Nuplane.Loading.Abstractions`. It MUST declare `OnPackagesLoadedAsync(PackageLoadedEvent loadedEvent, CancellationToken ct)` with a default no-op implementation, and `OnPackageLoadFailedAsync(string packageId, string reason, CancellationToken ct)` with a default no-op implementation.

- **FR-004**: A `PackageLoadedEvent` record MUST be defined in `Nuplane.Loading.Abstractions`. It MUST contain `IReadOnlyList<PackageLoadSession> LoadedPackages` (the sessions for successfully loaded packages), `Guid CorrelationId`, and `DateTimeOffset LoadedAt`.

- **FR-005**: An `ILoadingEventDispatcher` interface MUST be defined in `Nuplane.Loading.Abstractions`. It MUST declare `PublishLoadedAsync(PackageLoadedEvent loadedEvent, CancellationToken ct)`.

- **FR-006**: A `LoadingEventDispatcher` class MUST be defined in `Nuplane.Loading.Hosting` or `Nuplane.Loading`. It MUST implement `ILoadingEventDispatcher`. It MUST dispatch `OnPackagesLoadedAsync` to all registered `IPackageLoadingObserver` instances using the same per-observer error-isolation pattern as `ObserverEventDispatcher` (`catch` + log per observer; no interrupt of remaining observer dispatch).

- **FR-007**: `PackageLoadingMiddleware` MUST be removed from the `ReconciliationService` middleware pipeline. Assembly loading is the sole responsibility of `PackageAutoLoadingObserver`. The runtime pipeline MUST NOT perform assembly loading.

- **FR-008**: `AddNuplaneLoadingHosting()` MUST register `PackageAutoLoadingObserver` as an `INuplaneObserver` (so it receives reconciliation events), `LoadingEventDispatcher` as `ILoadingEventDispatcher`, and provide helper registration so host apps can add `IPackageLoadingObserver` implementations.

- **FR-009**: The existing `PackageLoadingMiddleware`, `IPackageLoaderBoundary`, `NuplaneLoadingAdapter`, and related runtime loading plumbing MUST be removed or inerted once FR-007 is satisfied. No dead code that performs loading through the pipeline should remain.

- **FR-010**: The existing `OnPackagesChangingAsync`, `OnPackagesChangedAsync`, and `OnPackageFailedAsync` events on `INuplaneObserver` MUST remain unchanged in signature, behavior, and pipeline position.

- **FR-011**: The sample `PluginDiscoveryObserver` MUST be refactored to implement `IPackageLoadingObserver` (in `Nuplane.Loading.Abstractions`) instead of `INuplaneObserver` for type scanning. `OnPackagesChangedAsync` MAY be retained for audit logging only or removed.

- **FR-012**: The sample `Program.cs` MUST set `EnableAutomaticReconciliation = true` so that startup reconciliation and the startup loading event are demonstrated.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: The startup reconciliation cycle MUST be idempotent — if the host is restarted with the same packages present, the `PackageAutoLoadingObserver` calls `IPackageLoader.EnsureLoadedAsync` which is already idempotent (returns existing sessions for already-loaded packages). No duplicate activations or state corruption.

- **OSR-002**: If the startup reconciliation cycle fails, the host MUST remain stable. Failed loading in `PackageAutoLoadingObserver` is isolated per-package; the observer MUST log each failure and (where possible) surface it via `IPackageLoadingObserver.OnPackageLoadFailedAsync`. Startup cycle infrastructure failures (source read, trust) are caught in `ReconciliationHostedService` and logged; the host enters normal periodic reconciliation.

- **OSR-003**: `PackageAutoLoadingObserver` MUST include the `changeSet.CorrelationId` in all log entries and in the `PackageLoadedEvent` it dispatches, so startup-cycle loading is traceable via `TriggerType.Startup` correlation.

- **OSR-004**: `LoadingEventDispatcher` MUST use per-observer exception isolation: each observer's callback is wrapped in try/catch; exceptions are logged with the correlation ID and observer type name; dispatch to remaining observers continues uninterrupted.

- **OSR-005**: Automated tests MUST cover: (a) startup cycle fires before periodic timer tick, (b) `PackageAutoLoadingObserver.OnPackagesChangedAsync` calls `IPackageLoader` and dispatches `PackageLoadedEvent` with correct sessions, (c) loading failures are isolated and do not prevent the event being dispatched for successfully loaded packages, (d) `LoadingEventDispatcher` per-observer exception isolation, (e) hosts with no `IPackageLoadingObserver` registered receive no errors.

### Key Entities

- **PackageAutoLoadingObserver**: `INuplaneObserver` implementation in `Nuplane.Loading.Hosting`. Subscribes to `OnPackagesChangedAsync`, calls `IPackageLoader.EnsureLoadedAsync` for added/updated packages, then dispatches `PackageLoadedEvent` via `ILoadingEventDispatcher`. Acts as the bridge between the reconciliation domain and the loading domain.

- **IPackageLoadingObserver**: Host-app–facing observer interface in `Nuplane.Loading.Abstractions`. Separate from `INuplaneObserver`. Declares `OnPackagesLoadedAsync` and `OnPackageLoadFailedAsync` with default no-op implementations. Host applications implement this interface to receive assembly-loading lifecycle events.

- **PackageLoadedEvent**: Loading-domain event record in `Nuplane.Loading.Abstractions`. Contains `IReadOnlyList<PackageLoadSession> LoadedPackages`, `string CorrelationId`, `DateTimeOffset LoadedAt`. Carries `PackageLoadSession` (id, version, install path, context key, loaded-at) which gives host observers everything needed for type scanning.

- **ILoadingEventDispatcher** / **LoadingEventDispatcher**: Interface (in `Nuplane.Loading.Abstractions`) + implementation (in `Nuplane.Loading.Hosting` or `Nuplane.Loading`) for dispatching `PackageLoadedEvent` to all registered `IPackageLoadingObserver` instances with per-observer error isolation.

- **TriggerType.Startup**: Existing enum value in `Nuplane.Runtime.Reconciliation.Models`. Remains in `Nuplane.Runtime` — it is not needed in the Loading event record. The correlation ID threads the startup context into Loading's log output.

## Assumptions

- `PackageAutoLoadingObserver` observes `OnPackagesChangedAsync` (not `OnPackagesLoadingAsync` or any new event) — this is the correct signal that reconciliation has committed new/updated desired state to the store.
- `IPackageLoader.EnsureLoadedAsync` is idempotent: calling it for a package that is already loaded returns the existing `PackageLoadSession` without re-loading, ensuring startup-cycle safety.
- `PackageLoadingMiddleware` (and `IPackageLoaderBoundary`/`NuplaneLoadingAdapter`) are removed as part of this feature. `UnloadMiddleware` is not changed; unloading remains in the runtime pipeline for now (a separate future spec will decide if unloading should also move to the Loading domain).
- `TriggerType` stays in `Nuplane.Runtime.Reconciliation.Models`. No migration to `Nuplane.Abstractions` is required because `PackageLoadedEvent` (now in `Nuplane.Loading.Abstractions`) does not need to reference it.
- `Nuplane.Loading.Abstractions` already references `Nuplane.Abstractions`, so `PackageLoadSession` and `ResolvedPackage` are both accessible there without new project references.
- Default interface implementations on `IPackageLoadingObserver` provide backward compatibility for host apps that add the interface incrementally.
- Adding `PackageAutoLoadingObserver` as an `INuplaneObserver` via `AddNuplaneLoadingHosting()` is opt-in; hosts that do not call `AddNuplaneLoadingHosting()` get no Loading observer and no loading events.
- The sample application update (FR-011, FR-012) demonstrates the intended usage pattern but does not change any runtime library behavior.
- Single-flight protection prevents the startup cycle from running concurrently with a timer-triggered cycle.

## Clarifications

### Session 2026-03-05 (initial)

- Q: `PackageLoadedEvent` reference to `TriggerType` creates an upward dependency. How to resolve? → Superseded by architecture revision (see below).
- Q: `PackageUnloadedEvent` payload type? → `IReadOnlyList<string>` (IDs). Superseded by architecture revision.
- Q: `PackageLoadedEvent` payload type? → `IReadOnlyList<ResolvedPackage>`. Superseded by architecture revision.

### Session 2026-03-05 (architecture revision)

- Q: Loading types (`PackageLoadedEvent`, `IPackageLoadingObserver`) and the loading observer belong in `Nuplane.Loading.*`, not `Nuplane.Abstractions`. `PackageLoadingMiddleware` should be removed; loading is the Loading library's responsibility. → A: **Confirmed**. All loading event types and the auto-loading observer move to `Nuplane.Loading.Abstractions` / `Nuplane.Loading.Hosting`.
- Q: How does the Loading observer dispatch events to the host app — via `INuplaneObserver` or a separate interface? → A: Separate `IPackageLoadingObserver` interface (Option Y). Host apps implement `IPackageLoadingObserver`; the Loading library dispatches via `ILoadingEventDispatcher`.
- Q: `PackageLoadedEvent` payload — `PackageLoadSession` is richer and available in `Nuplane.Loading.Abstractions`. Use it instead of `ResolvedPackage`? → A: **Yes**. `PackageLoadedEvent.LoadedPackages` is `IReadOnlyList<PackageLoadSession>`.
- Q: Does `PackageLoadedEvent` need `TriggerType`? → A: **No**. The Loading domain does not need reconciliation trigger metadata; `CorrelationId` provides sufficient traceability.
- Q: Unloading — does it also move to `Nuplane.Loading.Hosting`? → A: **Out of scope**. `IPackageLoader.TryRemoveContext` requires `(id, version)` but `changeSet.Removed` carries only IDs. Unloading remains in `UnloadMiddleware` (runtime pipeline) and a `PackageUnloadedEvent` / unload observer is deferred to a future spec.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: When packages are pre-deployed to a configured source, they are loaded and `OnPackagesLoadedAsync` fires on all registered `IPackageLoadingObserver` instances within 5 seconds of application startup — not delayed until the first periodic timer tick.
- **SC-002**: 100% of reconciliation cycles that add or update packages result in `OnPackagesLoadedAsync` firing with exactly the set of `PackageLoadSession` entries for successfully loaded packages.
- **SC-003**: Loading failures are isolated per-package: they do not prevent `OnPackagesLoadedAsync` from firing for other successfully loaded packages in the same cycle.
- **SC-004**: Host applications that implement `IPackageLoadingObserver` without implementing `INuplaneObserver` compile and run without errors.
- **SC-005**: The sample application demonstrates end-to-end startup loading: deploy a package, start the app, and observe plugin discovery via `IPackageLoadingObserver.OnPackagesLoadedAsync` in logs — without any `OnPackagesChangedAsync` type-scanning code.
- **SC-006**: On startup reconciliation failure, the host remains operational and the failure is visible in structured logs with a correlation identifier.


**Startup reconciliation behavior**: See [User Story 1 — Packages Available Immediately on Startup (Priority: P1)](#user-story-1--packages-available-immediately-on-startup-priority-p1) for the canonical rationale, independent test, and acceptance scenarios for startup reconciliation. The same behavior and outcomes apply here unchanged.
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

- **FR-004**: A `PackageLoadedEvent` record MUST be defined in `Nuplane.Abstractions` containing `IReadOnlyList<ResolvedPackage> LoadedPackages` (the packages successfully loaded in this cycle), the `TriggerType` of the cycle, and the correlation identifier. Using `ResolvedPackage` avoids any new project dependency and gives observers the identity, version, feed name, and install path needed for type scanning.

- **FR-005**: A `PackageUnloadedEvent` record MUST be defined in `Nuplane.Abstractions` containing `IReadOnlyList<string> UnloadedPackageIds` (the identifiers of successfully unloaded packages), the `TriggerType` of the cycle, and the correlation identifier. Package IDs are used rather than `ResolvedPackage` references because `UnloadMiddleware` tracks pending unloads by ID only and no store lookup is performed at unload time.

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

- **PackageLoadedEvent**: Represents the outcome of successful package loading in a reconciliation cycle. Contains `IReadOnlyList<ResolvedPackage> LoadedPackages` (packages loaded in this cycle), the `TriggerType` (Startup, Scheduled, Manual, DirectoryChange), and the correlation identifier for the cycle.

- **PackageUnloadedEvent**: Represents the outcome of successful package unloading in a reconciliation cycle. Contains `IReadOnlyList<string> UnloadedPackageIds` (the IDs of fully unloaded packages — excluding pending unloads), the `TriggerType`, and the correlation identifier. Uses string IDs rather than `ResolvedPackage` because `UnloadMiddleware` holds only ID-keyed context handles at unload time.

- **TriggerType.Startup**: An existing enum value that identifies the first automatic reconciliation cycle after host startup. Currently defined but unused — this feature activates it.

## Assumptions

- The startup reconciliation cycle uses the same middleware pipeline as all other cycles. No special startup-only middleware or bypass logic is introduced.
- `TriggerType` will be moved from `Nuplane.Runtime.Reconciliation.Models` to `Nuplane.Abstractions` so that `PackageLoadedEvent` and `PackageUnloadedEvent` (which reference it) can reside in `Nuplane.Abstractions` without creating an upward dependency on `Nuplane.Runtime`. The `Nuplane.Runtime` project will reference the canonical type from `Nuplane.Abstractions`. The `TriggerType` enum values (`Scheduled`, `DirectoryChange`, `Manual`, `Startup`) and their semantics remain unchanged.
- The `PackageLoadedEvent` and `PackageUnloadedEvent` types belong in `Nuplane.Abstractions` because they are part of the public observer contract.
- Loading events fire from within the existing middleware pipeline positions: `PackageLoadingMiddleware` for load events, `UnloadMiddleware` for unload events. No middleware reordering is required.
- Default interface method implementations (`=> Task.CompletedTask`) provide backward compatibility for existing observer implementations that do not implement the new methods.
- The sample application update (FR-011, FR-012) demonstrates the intended usage pattern but does not change any runtime library behavior.
- Single-flight protection (existing `EnableSingleFlight` option) prevents the startup cycle from running concurrently with a timer-triggered or directory-watcher-triggered cycle.
- When loading is disabled (`LoadingOptions.Enabled = false`), loading events do not fire because the `PackageLoadingMiddleware` skips loading entirely.

## Clarifications

### Session 2026-03-05

- Q: `PackageLoadedEvent` and `PackageUnloadedEvent` reference `TriggerType`, but `TriggerType` currently lives in `Nuplane.Runtime.Reconciliation.Models` while the events must be in `Nuplane.Abstractions`. How should this dependency conflict be resolved? → A: Move `TriggerType` enum to `Nuplane.Abstractions`; `Nuplane.Runtime` references it from there.
- Q: `PackageUnloadedEvent` needs a package list, but `UnloadMiddleware` only tracks unloads by package ID string — what type should the payload use? → A: `IReadOnlyList<string>` (package IDs only); no store lookup required at unload time.
- Q: `PackageLoadedEvent` needs a package list — should it use `IReadOnlyList<ResolvedPackage>` (same assembly, no new dependency) or another type? → A: `IReadOnlyList<ResolvedPackage>`; stays in `Nuplane.Abstractions` with no additional project reference.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: When packages are pre-deployed to a configured source, they are loaded and available for host type scanning within 5 seconds of application startup (not delayed until the first periodic timer tick).
- **SC-002**: 100% of reconciliation cycles that load new packages result in an `OnPackagesLoadedAsync` event firing with exactly the set of successfully loaded packages.
- **SC-003**: 100% of reconciliation cycles that unload packages result in an `OnPackagesUnloadedAsync` event firing with exactly the set of fully unloaded packages (excluding pending unloads).
- **SC-004**: Existing observer implementations that do not implement the new loading/unloading methods continue to function with zero errors or behavioral changes after the update.
- **SC-005**: The sample application demonstrates end-to-end startup loading: deploy a package, start the app, and observe plugin discovery via `OnPackagesLoadedAsync` in application logs — with no code changes beyond the observer method switch.
- **SC-006**: On startup reconciliation failure, the host remains operational and the failure is visible in structured logs with a correlation identifier containing the `Startup` trigger type.
