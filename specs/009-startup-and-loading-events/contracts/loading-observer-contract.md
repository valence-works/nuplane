# Loading Observer Contract: IPackageLoadingObserver

**Feature**: `009-startup-and-loading-events` | **Date**: 2026-03-05

This document describes the `IPackageLoadingObserver` interface introduced by this feature,
the `PackageLoadedEvent` record it receives, and the `ILoadingEventDispatcher` used to fan
out events to all registered observers.

---

## IPackageLoadingObserver

**Assembly**: `Nuplane.Loading.Abstractions`  
**Namespace**: `Nuplane.Loading.Abstractions`  
**File**: `src/Nuplane.Loading.Abstractions/IPackageLoadingObserver.cs`

```csharp
/// <summary>
/// Observer interface for host applications that want to react to package
/// loading events managed by the Nuplane loading domain.
/// All methods have default no-op implementations — implementors only override
/// what they need. Adding new methods in future is non-breaking.
/// </summary>
public interface IPackageLoadingObserver
{
    /// <summary>
    /// Called after a batch of packages has been successfully loaded into
    /// Assembly Load Contexts. Only fires when at least one package was loaded.
    /// </summary>
    /// <param name="evt">The loading event carrying session details for each loaded package.</param>
    /// <param name="cancellationToken">Host shutdown token.</param>
    Task OnPackagesLoadedAsync(
        PackageLoadedEvent evt,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
```

**Registration** (host application):
```csharp
services.AddSingleton<IPackageLoadingObserver, PluginDiscoveryObserver>();
```

Multiple implementations may be registered; all receive each event.

---

## PackageLoadedEvent

**Assembly**: `Nuplane.Loading.Abstractions`  
**Namespace**: `Nuplane.Loading.Abstractions`  
**File**: `src/Nuplane.Loading.Abstractions/Events/PackageLoadedEvent.cs`

```csharp
/// <summary>
/// Published after a batch of packages has been successfully loaded into
/// Assembly Load Contexts during a reconciliation cycle.
/// Only fired when at least one package was loaded.
/// </summary>
public sealed record PackageLoadedEvent(
    /// <summary>
    /// Correlation ID from the reconciliation cycle that triggered the load.
    /// Use this to correlate with runtime logs and cycle metrics.
    /// </summary>
    Guid CorrelationId,

    /// <summary>UTC timestamp recorded immediately after loading completed.</summary>
    DateTimeOffset LoadedAt,

    /// <summary>
    /// Load sessions for every package successfully loaded in this batch.
    /// Each session provides access to the loaded types and the AssemblyLoadContext.
    /// </summary>
    IReadOnlyList<PackageLoadSession> LoadedPackages);
```

**Invariants**:
- `LoadedPackages.Count >= 1` — event is never fired with an empty list.
- `CorrelationId` is the same ID that appears in the `IObserverEventDispatcher` events
  (`OnPackagesChangedAsync`) for the same reconciliation cycle — observers can correlate
  loading completion with the broader change notification.
- Does NOT carry `TriggerType` — use `CorrelationId` to look up trigger context from logs.

---

## ILoadingEventDispatcher

**Assembly**: `Nuplane.Loading.Abstractions`  
**Namespace**: `Nuplane.Loading.Abstractions`  
**File**: `src/Nuplane.Loading.Abstractions/ILoadingEventDispatcher.cs`

```csharp
/// <summary>
/// Fans out loading domain events to all registered
/// <see cref="IPackageLoadingObserver"/> instances.
/// Observer exceptions are caught and logged; they never interrupt the dispatch loop.
/// </summary>
public interface ILoadingEventDispatcher
{
    /// <summary>Publish a <see cref="PackageLoadedEvent"/> to all observers.</summary>
    Task PublishLoadedAsync(
        PackageLoadedEvent evt,
        CancellationToken cancellationToken);
}
```

**Concrete implementation**: `LoadingEventDispatcher` in `Nuplane.Loading.Hosting`  
**Registration**: Registered as singleton by `AddNuplaneLoading()`.

---

## Event Firing Contract

| Condition | Behaviour |
|-----------|-----------|
| One or more packages loaded successfully | `ILoadingEventDispatcher.PublishLoadedAsync` called once per cycle |
| Zero packages loaded (e.g. nothing new) | Dispatcher NOT called; no event fired |
| `IPackageLoader.LoadAsync` throws for some packages | Failing packages skipped; event fires for successful ones only (if any) |
| Observer throws in `OnPackagesLoadedAsync` | Exception caught, logged with observer type name; other observers continue |
| Host cancellation requested | `OperationCanceledException` propagates normally; partially-started observer calls may be cancelled |

---

## Migration Guide: From INuplaneObserver to IPackageLoadingObserver

**Before** (pattern from `Nuplane.Sample.AspNetCore/PluginDiscoveryObserver.cs`):
```csharp
public class PluginDiscoveryObserver : INuplaneObserver
{
    public async Task OnPackagesChangedAsync(
        PackageChangeSet changeSet, CancellationToken cancellationToken)
    {
        // Type scanning here — INCORRECT: assemblies may not be loaded yet
        foreach (var pkg in changeSet.Added)
        {
            var types = _scanner.GetExportedTypes(pkg.Id);
            Register(types);
        }
    }
}
```

**After** (correct pattern):
```csharp
// Implement IPackageLoadingObserver for assembly-dependent work
public class PluginDiscoveryObserver : INuplaneObserver, IPackageLoadingObserver
{
    // INuplaneObserver — audit log only; assemblies not yet loaded
    public Task OnPackagesChangedAsync(
        PackageChangeSet changeSet, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Package change set received: {Added} added, {Removed} removed.",
            changeSet.Added.Count, changeSet.Removed.Count);
        return Task.CompletedTask;
    }

    // IPackageLoadingObserver — assemblies are loaded; safe to scan types
    public Task OnPackagesLoadedAsync(
        PackageLoadedEvent evt, CancellationToken cancellationToken)
    {
        foreach (var session in evt.LoadedPackages)
        {
            var plugins = session.LoadedTypes
                .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract)
                .ToList();
            _registry.Register(plugins);
        }
        return Task.CompletedTask;
    }
}
```

**Registration**:
```csharp
// Register as both interfaces (or just IPackageLoadingObserver if INuplaneObserver not needed)
services.AddSingleton<PluginDiscoveryObserver>();
services.AddSingleton<INuplaneObserver>(sp => sp.GetRequiredService<PluginDiscoveryObserver>());
services.AddSingleton<IPackageLoadingObserver>(sp => sp.GetRequiredService<PluginDiscoveryObserver>());
```

---

## What INuplaneObserver Does NOT Change

`INuplaneObserver` in `Nuplane.Abstractions` is **unchanged** by this feature. No new methods
are added to it. Existing observer implementations compile and run without modification.

Loading events (`PackageLoadedEvent`) are a separate concern routed through the separate
`IPackageLoadingObserver` / `ILoadingEventDispatcher` path in `Nuplane.Loading.Abstractions`.

