# Data Model: Startup Reconciliation & Loading Events

**Branch**: `009-startup-and-loading-events` | **Date**: 2026-03-05

---

## New Types in `Nuplane.Loading.Abstractions`

### PackageLoadedEvent

**File**: `src/Nuplane.Loading.Abstractions/Events/PackageLoadedEvent.cs`  
**Namespace**: `Nuplane.Loading.Abstractions`

```csharp
/// <summary>
/// Published after a batch of packages has been successfully loaded into
/// Assembly Load Contexts. Only fired when at least one package was loaded.
/// </summary>
public sealed record PackageLoadedEvent(
    /// <summary>Correlation ID from the reconciliation cycle that triggered loading.</summary>
    Guid CorrelationId,
    /// <summary>UTC timestamp recorded immediately after loading completed.</summary>
    DateTimeOffset LoadedAt,
    /// <summary>Sessions for every package successfully loaded in this batch.</summary>
    IReadOnlyList<PackageLoadSession> LoadedPackages);
```

**Notes**:
- `PackageLoadSession` is already defined in `Nuplane.Loading.Abstractions`; no new
  project dependency is required.
- Does not carry `TriggerType` — `CorrelationId` is sufficient for cross-cutting correlation.
  `TriggerType` stays in `Nuplane.Runtime.Reconciliation.Models`.

---

### IPackageLoadingObserver

**File**: `src/Nuplane.Loading.Abstractions/IPackageLoadingObserver.cs`  
**Namespace**: `Nuplane.Loading.Abstractions`

```csharp
/// <summary>
/// Observer interface for host applications that want to react to package
/// loading events. All methods have default no-op implementations so that
/// implementors only override what they need.
/// </summary>
public interface IPackageLoadingObserver
{
    /// <summary>
    /// Called after a batch of packages has been loaded successfully.
    /// </summary>
    Task OnPackagesLoadedAsync(
        PackageLoadedEvent evt,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
```

**Notes**:
- Registered by the host application (e.g. `services.AddSingleton<IPackageLoadingObserver, MyObserver>()`).
- Separate from `INuplaneObserver` — loading events are a loading-domain concern.
- Default implementations prevent breaking changes when future event methods are added.

---

### ILoadingEventDispatcher

**File**: `src/Nuplane.Loading.Abstractions/ILoadingEventDispatcher.cs`  
**Namespace**: `Nuplane.Loading.Abstractions`

```csharp
/// <summary>
/// Fans out loading domain events to all registered <see cref="IPackageLoadingObserver"/>
/// instances. Follows the same pattern as <c>IObserverEventDispatcher</c> in the runtime.
/// </summary>
public interface ILoadingEventDispatcher
{
    Task PublishLoadedAsync(
        PackageLoadedEvent evt,
        CancellationToken cancellationToken);
}
```

---

## New Types in `Nuplane.Loading.Hosting`

### PackageAutoLoadingObserver

**File**: `src/Nuplane.Loading.Hosting/PackageAutoLoadingObserver.cs`  
**Namespace**: `Nuplane.Loading.Hosting`

```csharp
internal sealed class PackageAutoLoadingObserver : INuplaneObserver
{
    private readonly IPackageLoader _loader;
    private readonly ILoadingEventDispatcher _dispatcher;
    private readonly ILogger<PackageAutoLoadingObserver> _logger;

    public PackageAutoLoadingObserver(
        IPackageLoader loader,
        ILoadingEventDispatcher dispatcher,
        ILogger<PackageAutoLoadingObserver> logger)
    { ... }

    // INuplaneObserver no-ops for change/health/metrics
    public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
        => Task.CompletedTask;

    // Loading is triggered via OnReconciliationCompletedAsync or similar hook
    // Exact hook TBD during implementation based on reconciliation context access.
    // Fires ILoadingEventDispatcher.PublishLoadedAsync after successful load batch.
}
```

**Responsibilities**:
1. Receives the set of packages to load (from `changeSet.Added` / `changeSet.Updated`).
2. Calls `IPackageLoader.LoadAsync` for each package.
3. Collects successful `PackageLoadSession` results.
4. Dispatches `PackageLoadedEvent` via `ILoadingEventDispatcher` if any sessions succeeded.
5. Logs failures per-package without interrupting the batch.

**Note**: The exact `INuplaneObserver` method used to trigger loading will be confirmed
during implementation. `OnPackagesChangedAsync` is the primary candidate since `PackageChangeSet`
carries `Added` and `Updated` collections.

---

### LoadingEventDispatcher

**File**: `src/Nuplane.Loading.Hosting/LoadingEventDispatcher.cs`  
**Namespace**: `Nuplane.Loading.Hosting`

```csharp
internal sealed class LoadingEventDispatcher : ILoadingEventDispatcher
{
    private readonly IReadOnlyList<IPackageLoadingObserver> _observers;
    private readonly ILogger<LoadingEventDispatcher> _logger;

    public LoadingEventDispatcher(
        IEnumerable<IPackageLoadingObserver> observers,
        ILogger<LoadingEventDispatcher> logger)
    {
        _observers = observers.ToList();
        _logger = logger;
    }

    public async Task PublishLoadedAsync(PackageLoadedEvent evt, CancellationToken ct)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.OnPackagesLoadedAsync(evt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Observer {Observer} threw in OnPackagesLoadedAsync.",
                    observer.GetType().Name);
            }
        }
    }
}
```

**Registration** (in `NuplaneLoadingHostingServiceCollectionExtensions`):
```csharp
services.AddSingleton<ILoadingEventDispatcher, LoadingEventDispatcher>();
services.AddSingleton<INuplaneObserver, PackageAutoLoadingObserver>();
```

---

## Modified Types

### ReconciliationHostedService *(startup cycle)*

**File**: `src/Nuplane/ReconciliationHostedService.cs`

**Change**: Add a startup reconciliation cycle before the periodic timer loop.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    // Startup cycle — non-fatal if it fails
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

    // Periodic cycles (unchanged)
    using var timer = new PeriodicTimer(_options.ReconciliationInterval);
    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
        // ... existing periodic cycle block unchanged
    }
}
```

---

### NuplaneLoadingHostingServiceCollectionExtensions

**File**: `src/Nuplane.Loading.Hosting/NuplaneLoadingHostingServiceCollectionExtensions.cs`

**Change**: Remove `NuplaneLoadingAdapter as IPackageLoaderBoundary` registration.
Add `LoadingEventDispatcher` and `PackageAutoLoadingObserver` registrations.

---

## Deleted Types

| Type | File | Reason |
|------|------|--------|
| `PackageLoadingMiddleware` | `src/Nuplane.Runtime/Reconciliation/Middleware/PackageLoadingMiddleware.cs` | Loading moves to Loading domain |
| `IPackageLoaderBoundary` | `src/Nuplane.Runtime/Loading/IPackageLoaderBoundary.cs` | Dead code after middleware removal |
| `PackageLoaderBoundaryEntry` | `src/Nuplane.Runtime/Loading/IPackageLoaderBoundary.cs` | Dead code after middleware removal |
| `PackageLoaderBoundaryResult` | `src/Nuplane.Runtime/Loading/IPackageLoaderBoundary.cs` | Dead code after middleware removal |
| `NoOpPackageLoaderBoundary` | `src/Nuplane.Runtime/Loading/IPackageLoaderBoundary.cs` | Dead code after middleware removal |
| `NuplaneLoadingAdapter` | `src/Nuplane.Loading.Hosting/NuplaneLoadingAdapter.cs` | Adapted `IPackageLoaderBoundary`; no longer needed |

---

## Event Flow (revised)

```
ReconciliationHostedService.ExecuteAsync
  │
  ├─ [startup] TriggerAsync(TriggerType.Startup)
  │     └─ Pipeline: DesiredState → Resolution → TrustGate → DiffAndChange
  │                  → Transaction → Unload → Cleanup → HealthMetrics
  │
  └─ [periodic] PeriodicTimer.WaitForNextTickAsync
        └─ Pipeline: (same as above, TriggerType.Scheduled)

INuplaneObserver.OnPackagesChangedAsync (called by ObserverEventDispatcher)
  │
  └─ PackageAutoLoadingObserver (in Nuplane.Loading.Hosting)
        ├─ IPackageLoader.LoadAsync per package in changeSet.Added / changeSet.Updated
        ├─ Collect successful PackageLoadSession results
        └─ ILoadingEventDispatcher.PublishLoadedAsync(PackageLoadedEvent)
              └─ Per registered IPackageLoadingObserver.OnPackagesLoadedAsync
```

---

## Invariants / Constraints

| Constraint | Enforced by |
|---|---|
| `PackageLoadedEvent` only fires when at least one session succeeded | `if (sessions.Count > 0)` guard in `PackageAutoLoadingObserver` |
| Observer exceptions never interrupt the dispatch loop | `catch` per observer in `LoadingEventDispatcher` |
| Startup cycle failure is non-fatal | `catch (Exception)` in `ReconciliationHostedService.ExecuteAsync` |
| Startup cycle cannot run concurrently with periodic cycle | `EnableSingleFlight` / `inFlight` guard in `ReconciliationService` |
| `PackageLoadedEvent` payload always carries correlation ID | Passed through via `changeSet.CorrelationId` or `context.CorrelationId` |
| Existing `INuplaneObserver` implementors compile unchanged | No new required methods on `INuplaneObserver` |
| Unloading not implemented | Deferred; `TryRemoveContext` requires version not available in changeSet |
