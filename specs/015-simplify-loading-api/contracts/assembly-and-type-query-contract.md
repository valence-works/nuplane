# Contract — Assemblies and Optional Type Finding

## Purpose
Define the in-process runtime convenience contracts for assembly access and optional type finding over the current active loaded package set.

## Ownership
- Contract package: `src/Nuplane.Loading.Abstractions`
- Implementation package: `src/Nuplane.Loading`
- Typical consumers: sample hosts and downstream applications that need runtime inspection
- Explicit non-ownership: durable admin/load-state/read-model surfaces

## Proposed public contract

```csharp
public interface IPackageAssemblyCatalog
{
    Task<IReadOnlyList<PackageAssemblies>> GetAssembliesAsync(CancellationToken cancellationToken);
    Task<PackageAssemblies?> GetAssembliesAsync(string packageId, CancellationToken cancellationToken);
}

public interface IPackageTypeFinder
{
    Task<IReadOnlyList<Type>> FindTypesAsync<TInterface>(string packageId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Type>> FindTypesAsync(Type interfaceType, string packageId, CancellationToken cancellationToken);
}

public sealed record PackageAssemblies(
    string PackageId,
    string Version,
    IReadOnlyList<Assembly> Assemblies,
    IReadOnlyList<PackageAssemblyReference> AssemblyReferences);
```

## Default assembly-surface semantics
- `IPackageAssemblyCatalog` is the default loading-enabled host surface after active packages.
- The all-packages read returns only active packages whose current package load state is `Loaded`.
- The package-ID read returns the current active loaded version for that package identifier, or `null` when the package is inactive, not loaded, disabled, or stale.
- Ordering must remain deterministic for repeated identical inputs.
- Public exact-version/provider-style assembly methods are removed rather than retained as advanced public escape hatches.

## Optional type-finding semantics
- `IPackageTypeFinder` is public but secondary: default guidance explains it only after assemblies.
- Type finding is a convenience layer over assembly access and must follow the same active-package semantics as `IPackageAssemblyCatalog`.
- Scans are best-effort: uninspectable assemblies or types are skipped, warnings/logs are emitted, and resolvable matches are returned.
- Public synchronous or exact-version type-finding methods are removed.
- The contract must not redefine host-specific plugin or application semantics; it only filters runtime types for a caller-supplied interface/base type.

## Unload-sensitive runtime object rules
- `Assembly`, `Type`, and derived reflection artifacts are allowed only on these in-process runtime convenience surfaces.
- Callers must use returned runtime objects immediately and avoid caching them beyond the current reconciliation cycle.
- These contracts must document collectible `AssemblyLoadContext` implications prominently.
- No durable or remote DTO may embed runtime objects from these contracts.

## Internalization rules
- `IPackageAssemblyProvider` is removed from the public host model.
- If exact-version materialization logic still exists for internal runtime reasons, it stays inside `src/Nuplane.Loading` and is not exposed as a public abstraction.
- Low-level loader/unload/session/result types are internal runtime infrastructure and not part of this public query contract.
- The legacy `IPackageTypeScanner` surface is internal-only; hosts learn only `IPackageTypeFinder` as the optional secondary query surface.
- Sample/plugin discovery remains host-owned and now refreshes from core invalidation plus canonical query surfaces rather than from a public loading-observer API.

## Validation and test obligations
- Loading tests must prove disabled/stale reads return empty assembly or type results without forcing callers through mechanics-first APIs.
- Loading tests must prove only currently loaded active packages surface through `IPackageAssemblyCatalog`.
- Type-finding tests must prove best-effort logging/skip behavior and the absence of exact-version public methods.
- Sample validation must prove assemblies-first usage with optional type finding as a secondary step.

