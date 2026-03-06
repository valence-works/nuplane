# Research: Startup Reconciliation & Loading Events

**Branch**: `009-startup-and-loading-events` | **Date**: 2026-03-05 (revised)

All decisions below reflect the revised architecture (architecture revision session 2026-03-05):
`PackageLoadingMiddleware` is removed; loading is the responsibility of `PackageAutoLoadingObserver`
in `Nuplane.Loading.Hosting`; host apps observe loading via a separate `IPackageLoadingObserver`.

---

## D-001 — Remove PackageLoadingMiddleware and IPackageLoaderBoundary

**Decision**: Delete `PackageLoadingMiddleware.cs` from `Nuplane.Runtime`, delete
`IPackageLoaderBoundary.cs` from `Nuplane.Runtime/Loading/`, and delete
`NuplaneLoadingAdapter.cs` from `Nuplane.Loading.Hosting`. Remove the
`PackageLoadingMiddleware` step from the `ReconciliationService` pipeline.

**Rationale**: Loading assemblies is the sole concern of the `Nuplane.Loading.*` domain.
`PackageLoadingMiddleware` reaches across the domain boundary by calling the `IPackageLoader`
contract from inside the runtime reconciliation pipeline. Removing it eliminates this
coupling. `IPackageLoaderBoundary` and `NuplaneLoadingAdapter` exist solely to adapt
`IPackageLoader` for use by `PackageLoadingMiddleware`; once the middleware is gone, both are
dead code.

**Postcondition**: The reconciliation pipeline becomes:
`DesiredStateRead → PackageResolution → TrustAndLockGate → DiffAndChange →
TransactionExecution → UnloadMiddleware → Cleanup → HealthAndMetrics`

**Alternatives considered**:
- Keep `PackageLoadingMiddleware` and add an observer hook alongside it (rejected: perpetuates
  the domain boundary violation; user direction was explicit).

---

## D-002 — PackageAutoLoadingObserver Location

**Decision**: Create `PackageAutoLoadingObserver` in `Nuplane.Loading.Hosting`. It implements
`INuplaneObserver` and is registered as such in `NuplaneLoadingHostingServiceCollectionExtensions`.

**Rationale**: `Nuplane.Loading.Hosting` already depends on `Nuplane.Runtime` (it must see
`INuplaneObserver`), `Nuplane.Loading` (it needs `IPackageLoader`), and
`Nuplane.Loading.Abstractions`. It is the only project in the graph that can access all
required types without introducing new cycles. `PackageAutoLoadingObserver` is an
infrastructure adaptation class — it bridges the runtime observer notification with the
loading domain.

**Dependency satisfied**: `Nuplane.Loading.Hosting` → `Nuplane.Runtime` (existing) provides
`INuplaneObserver`. `Nuplane.Loading.Hosting` → `Nuplane.Loading.Abstractions` (existing)
provides `ILoadingEventDispatcher` and `IPackageLoadingObserver`.

**Alternatives considered**:
- Put in `Nuplane.Loading` (rejected: `Nuplane.Loading` does not depend on `Nuplane.Runtime`;
  adding that dependency would create a cycle).
- Put in `Nuplane.Runtime` (rejected: runtime has no dependency on loading domain; loading
  is not runtime's concern).

---

## D-003 — IPackageLoadingObserver in Nuplane.Loading.Abstractions

**Decision**: Define `IPackageLoadingObserver` in `Nuplane.Loading.Abstractions` with a
single method `Task OnPackagesLoadedAsync(PackageLoadedEvent evt, CancellationToken ct)`.
All methods have default no-op implementations.

**Rationale**: Host applications that want to react to load events need a stable, versioned
interface contract that does not require a runtime dependency. `Nuplane.Loading.Abstractions`
is the lowest-level loading-domain project (`Nuplane.Loading.Abstractions` → `Nuplane.Abstractions`
only). Placing `IPackageLoadingObserver` here gives host apps a minimal dependency surface.

**Default implementations**: All methods use `=> Task.CompletedTask` defaults so that
implementors only override what they need. This avoids breaking changes when new event
methods are added in future.

**Alternatives considered**:
- Extend `INuplaneObserver` instead of a separate interface (rejected: user explicit
  decision — Option Y; also conflates runtime observer protocol with loading-domain events).
- Place in `Nuplane.Loading` (rejected: that project adds `IPackageLoader` implementation
  details; host apps should not need that dependency just to implement an observer).

---

## D-004 — PackageLoadedEvent Payload Type

**Decision**: `PackageLoadedEvent.LoadedPackages` is `IReadOnlyList<PackageLoadSession>`.

**Rationale**: `PackageAutoLoadingObserver.OnPackagesChangedAsync` calls
`IPackageLoader.LoadAsync` which returns `PackageLoadSession`. `PackageLoadSession` is
already in `Nuplane.Loading.Abstractions` — the same assembly that will define
`PackageLoadedEvent`. This means no cross-assembly lookup is required and the event carries
the richest possible data (session includes `PackageId`, `Version`, `LoadedTypes`,
`AssemblyLoadContext` reference). Observers get type scanning capability from the event
payload without needing a separate `IPackageTypeScanner` call.

**Structure**:
```csharp
public sealed record PackageLoadedEvent(
    Guid CorrelationId,
    DateTimeOffset LoadedAt,
    IReadOnlyList<PackageLoadSession> LoadedPackages);
```

**Alternatives considered**:
- `IReadOnlyList<ResolvedPackage>` (rejected: lives in `Nuplane.Abstractions`; `PackageLoadedEvent`
  is in `Nuplane.Loading.Abstractions` which can reference `Nuplane.Abstractions` — technically
  valid but loses the richer `PackageLoadSession` data observers actually need).
- A new thin `LoadedPackageInfo` record (rejected: unnecessary indirection when `PackageLoadSession`
  is already the right type at the right layer).

---

## D-005 — No TriggerType in PackageLoadedEvent

**Decision**: `PackageLoadedEvent` does NOT include `TriggerType`. It carries only
`CorrelationId`, `LoadedAt`, and `LoadedPackages`.

**Rationale**: `TriggerType` lives in `Nuplane.Runtime.Reconciliation.Models`.
`PackageLoadedEvent` lives in `Nuplane.Loading.Abstractions`. `Nuplane.Loading.Abstractions`
does not depend on `Nuplane.Runtime`; introducing that dependency would invert the correct
direction (`Nuplane.Runtime` → `Nuplane.Loading.Abstractions`, not the other way around).
`CorrelationId` is sufficient for observers to correlate a load event with a reconciliation
cycle ticket in their own logs.

**TriggerType stays in `Nuplane.Runtime.Reconciliation.Models`** — no migration needed.

**Alternatives considered**:
- Move `TriggerType` to `Nuplane.Abstractions` (rejected: no longer needed at that layer
  since events don't carry it; would be a premature move with no consumer).
- Include `TriggerType` as a `string` (rejected: loses type safety for no benefit).

---

## D-006 — Unloading Is Out of Scope

**Decision**: `PackageAutoLoadingObserver` does NOT attempt to unload packages.
No `PackageUnloadedEvent` is defined in this spec. `UnloadMiddleware` remains in the
runtime pipeline unchanged.

**Rationale**: `IPackageLoader.TryRemoveContext(string packageId, string version, ...)` in
`Nuplane.Loading.Abstractions/LoadingContracts.cs` requires both `packageId` AND `version`.
The observer's `PackageChangeSet.Removed` provides only `IReadOnlyList<string>` (package
IDs). There is no API-accessible way to retrieve the version of a loaded package from the
observer context. Implementing unloading here would require either (a) a version cache
maintained outside `IPackageLoader`, or (b) a new `Nuplane.Loading.Abstractions` API — both
are separate features.

**Deferral**: Unloading-via-observer (with `PackageUnloadedEvent`) will be addressed in a
dedicated future spec once the version-retrieval mechanism is designed.

**Alternatives considered**:
- Add a `Dictionary<string, string>` version cache inside `PackageAutoLoadingObserver`
  (rejected: fragile — misses remove events that happen before observer is registered;
  also crosses into runtime state management territory).

---

## D-007 — ILoadingEventDispatcher in Nuplane.Loading.Abstractions

**Decision**: Define `ILoadingEventDispatcher` in `Nuplane.Loading.Abstractions` with
`Task PublishLoadedAsync(PackageLoadedEvent evt, CancellationToken ct)`.
Provide `LoadingEventDispatcher` (concrete) in `Nuplane.Loading.Hosting` which fans out to
all registered `IPackageLoadingObserver` instances.

**Rationale**: Mirrors the existing `IObserverEventDispatcher` / `ObserverEventDispatcher`
pattern in `Nuplane.Runtime`. Separating the interface from the concrete dispatcher keeps
`Nuplane.Loading.Abstractions` free of DI framework dependencies. The concrete
`LoadingEventDispatcher` can be constructed in `Nuplane.Loading.Hosting` where
`IEnumerable<IPackageLoadingObserver>` is available via DI.

**Registration**: `NuplaneLoadingHostingServiceCollectionExtensions.AddNuplaneLoading` will
register `LoadingEventDispatcher` as `ILoadingEventDispatcher` (singleton) and register
`PackageAutoLoadingObserver` as `INuplaneObserver` (singleton).

**Alternatives considered**:
- Fan out directly inside `PackageAutoLoadingObserver` (rejected: couples observer to all
  loading observer registrations; prevents testability of dispatch logic in isolation).

---

## D-008 — Startup Cycle Implementation Pattern

**Decision**: In `ReconciliationHostedService.ExecuteAsync`, add a startup reconciliation
cycle call before the `using var timer = new PeriodicTimer(...)` line. Use the same
try/catch/log pattern as the periodic cycle loop. A startup cycle failure is non-fatal —
execution continues into the periodic timer loop.

**Implementation sketch**:
```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    // Startup cycle
    try
    {
        await _reconciliationService.TriggerAsync(
            new ReconciliationTrigger(TriggerType.Startup), stoppingToken);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Startup reconciliation cycle failed.");
    }

    // Periodic cycles
    using var timer = new PeriodicTimer(_options.ReconciliationInterval);
    while (await timer.WaitForNextTickAsync(stoppingToken))
    { ... }
}
```

**Alternatives considered**:
- Separate `IHostedService` for startup (rejected: unnecessary; startup is a one-time
  trigger that uses the same pipeline and cancellation token).
- `IHostApplicationLifetime.ApplicationStarted` callback (rejected: runs outside the
  hosted service lifecycle; cannot use the service's cancellation token directly).

---

## D-009 — IObserverEventDispatcher Test Helpers Unchanged

**Decision**: Existing `RecordingDispatcher` and `NullDispatcher` test helper classes that
implement `IObserverEventDispatcher` do NOT need new methods. `IObserverEventDispatcher`
is NOT extended with `PublishLoadedAsync` / `PublishUnloadedAsync` in this spec — those
events are now dispatched via `ILoadingEventDispatcher`, not `IObserverEventDispatcher`.

**Rationale**: The runtime dispatcher (`ObserverEventDispatcher`, `IObserverEventDispatcher`)
fans out to `INuplaneObserver` instances only. Loading events are a separate dispatch path
via `ILoadingEventDispatcher` → `IPackageLoadingObserver`. The two paths are independent.
No runtime test helper needs updating.

**Impact**: New test helpers `RecordingLoadingDispatcher` / `NullLoadingDispatcher` will be
needed in `Nuplane.Loading.Tests` (or `Nuplane.Loading.Hosting` tests) to test
`PackageAutoLoadingObserver` and `LoadingEventDispatcher` in isolation.
