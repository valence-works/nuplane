# Quickstart — Loading & Query API Simplification

## Goal
Validate that Nuplane’s host-facing loading/query architecture is teachable through four concepts only: active packages, load state, assemblies, and optional type finding. Confirm that admin/loading ownership remains clean, public provider/exact-version mechanics are removed, and unload-sensitive runtime objects stay confined to in-process convenience surfaces.

## Preconditions
- .NET SDK installed with support for the solution target frameworks.
- Feature branch checked out: `015-simplify-loading-api`.
- Repository restored successfully.
- A writable local package directory is available for `samples/Nuplane.Sample.AspNetCore`.
- The sample plugin can be packed locally for end-to-end validation.

## Verification command set

Run from repository root:

```bash
dotnet pack samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj -c Debug
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj
dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj
dotnet test nuplane.sln
./build/validate-secrets.sh
```

Focused validation commands:

```bash
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --filter "FullyQualifiedName~LoadState"
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --filter "FullyQualifiedName~PackageAssemblyCatalog"
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --filter "FullyQualifiedName~PackageTypeFinder"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~Admin"
```

## 1) Validate the default host decision path
1. Review the public contracts and default docs/sample entry points.
2. Confirm a new host can answer the four default questions in order:
   - What packages are active?
   - What is their current load state?
   - Which runtime assemblies can be inspected now?
   - If desired, which matching types are currently discoverable?
3. Confirm the default onboarding path teaches `IActivePackageCatalog` first, then load state only as needed, then `IPackageAssemblyCatalog`, and only afterward `IPackageTypeFinder`.
4. Confirm default guidance does not need provider, scanner, candidate, descriptor, loader, coordinator, or session vocabulary.

## 2) Validate active package reads stay canonical
1. Start a host with Nuplane core runtime configured and reconcile at least one package.
2. Query the active package inventory directly and through `GET /nuplane/admin/packages`.
3. Confirm the primary method name is `GetActivePackagesAsync` and the returned model is `ActivePackagesSnapshot` containing `ActivePackage` records.
4. Confirm repeated identical reads remain deterministic and exclude retained rollback copies or removed versions.

## 3) Validate load-state naming and ownership
1. Run the host with the loading module installed and mapped through the loading-owned API package.
2. Query the standalone load-state service and `GET /nuplane/admin/load-state`.
3. Confirm the canonical naming is load-state terminology (`IPackageLoadStateCatalog`, `GetLoadStateAsync`, `PackageLoadStateSnapshot`, `PackageLoadState`, `PackageLoadStatus`).
4. Confirm `MapNuplaneLoadState` is owned by `Nuplane.Loading.Api`, not by `Nuplane.Admin.Api`.
5. Run the host without loading installed or without mapping the loading-owned API package and confirm core admin still exposes `/nuplane/admin/packages`, `/nuplane/admin/state`, and `/nuplane/admin/reconcile`, but no load-state route is present.

## 4) Validate assemblies as the default loading-enabled runtime surface
1. Start `samples/Nuplane.Sample.AspNetCore` with loading enabled.
2. Pack and drop `Nuplane.Sample.Plugin` into the configured directory feed.
3. Query the sample’s active package and load-state endpoints first.
4. Query the sample assembly endpoint backed by `IPackageAssemblyCatalog`.
5. Confirm the assembly catalog returns only active packages whose load-state status is currently `Loaded`.
6. Confirm the public model is `PackageAssemblies` and that any durable companion assembly metadata uses `PackageAssemblyReference` instead of `AssemblyScanCandidate`.
7. Confirm the public assembly surface no longer exposes exact-version/provider-style methods; hosts can query all active loaded packages or the current active loaded version by package ID only.

## 5) Validate optional type finding stays secondary
1. Review the public type-finding contract and sample usage.
2. Confirm the interface name is `IPackageTypeFinder` and that docs present it after assemblies rather than before them.
3. Query or exercise the sample’s type-finding convenience path only after confirming assembly access works.
4. Confirm type finding remains best-effort and host-neutral: Nuplane may filter assignable runtime types, but host-owned plugin/application semantics remain outside Nuplane’s public contract.
5. Confirm public type-finding APIs do not expose synchronous exact-version methods.

## 6) Validate unload-sensitive runtime object boundaries
1. Review all durable or remotely exposed read models returned from admin and load-state routes.
2. Confirm those models contain no `Assembly`, `Type`, or derived reflection artifacts.
3. Review the in-process contracts for `IPackageAssemblyCatalog` and `IPackageTypeFinder`.
4. Confirm they explicitly warn that returned runtime objects are immediate-use/no-cache values tied to collectible assembly load contexts.
5. Force a reconcile/update that unloads or supersedes a package and confirm tests/documentation cover safe host behavior around released runtime references.

## 7) Validate retired public mechanics and internalization targets
1. Inspect the public abstractions package surface after implementation.
2. Confirm `IPackageAssemblyProvider` is removed from the public host model.
3. Confirm public exact-version assembly/type methods are removed.
4. Confirm low-level loading orchestration abstractions (`IPackageLoader`, `IPackageUnloadCoordinator`, event/session/result bookkeeping) are internalized, merged, or deleted unless a documented internal-only safety boundary remains.
5. Confirm no alias or compatibility layer survives only to preserve retired vocabulary.

## Expected test evidence
- Runtime tests proving active package naming updates and deterministic inventory behavior.
- Loading tests proving load-state renames, assembly-catalog defaults, optional type-finder behavior, disabled/stale/failed cases, and removal of exact-version/provider public paths.
- API tests proving `MapNuplaneLoadState`/`GET /nuplane/admin/load-state` ownership and continued loading-free core admin composition.
- Integration tests proving active package, load state, and assemblies remain query-first under restart and failure scenarios.
- Sample validation proving assemblies-first onboarding and optional type finding as a secondary convenience.

## Expected outcomes
- All automated test commands pass.
- Default host integration guidance is explainable with only the four intended concepts.
- No public route or contract reintroduces retired mechanics-first vocabulary as a parallel mental model.
- Durable/remote models stay unload-safe, while runtime assembly/type convenience remains explicit about lifecycle constraints.

