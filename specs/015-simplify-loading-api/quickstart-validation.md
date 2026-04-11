# Quickstart Validation — Loading & Query API Simplification

## Validation Summary

- Date: 2026-04-11
- Feature: `015-simplify-loading-api`
- Status: Complete

## Command Evidence

### Targeted test commands

```bash
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --no-restore
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --no-restore
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --no-restore
dotnet build samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj --no-restore
```

- `Nuplane.Loading.Tests`: passed (`70/70`)
- `Nuplane.Runtime.Tests`: passed (`320/320`)
- `Nuplane.Integration.Tests`: passed (`93/93`)
- `Nuplane.Sample.AspNetCore`: build passed

### Full validation commands

```bash
dotnet test nuplane.sln --no-restore
dotnet pack samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj -c Debug --no-restore
./build/validate-secrets.sh
```

- `dotnet test nuplane.sln --no-restore`: passed (`553/553`)
- `dotnet pack samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj -c Debug --no-restore`: passed
- `./build/validate-secrets.sh`: passed (`OK - no committed source credentials detected.`)

## Story Validation Notes

### US1 — Simplified host-facing taxonomy

- `IActivePackageCatalog` remains the canonical active-package entry point and admin packages compose `ActivePackagesSnapshot` directly.
- Loading-owned HTTP composition now maps `MapNuplaneLoadState` at `GET /nuplane/admin/load-state`.
- `IPackageAssemblyCatalog` and `IPackageTypeFinder` remain the only public loading-enabled runtime convenience surfaces.

### US2 — Architecture simplification and internalization

- Removed public mechanics-first service registrations for `IPackageLoader`, `IPackageAssemblyProvider`, `IPackageUnloadCoordinator`, `ILoadingEventDispatcher`, `ILoadingFailureTracker`, `IPackageTypeScanner`, and `ILoadingCatalog`.
- Internalized low-level loading bookkeeping and event types behind `src/Nuplane.Loading` and internal `Nuplane.Loading.Abstractions` seams.
- Removed obsolete admin compatibility wrappers and the retired loading endpoint DTO/file set.
- Reworked sample plugin discovery to refresh from canonical query surfaces via `INuplaneObserver` invalidation instead of a public loading-observer API.

## Observations

- The public loading abstractions surface now teaches only load state, assemblies, and optional type finding; exact-version/provider/session-style mechanics remain internal-only.
- Canonical route ownership is explicit: core admin owns `/packages`, `/state`, and `/reconcile`; loading owns `/load-state`.
- Runtime-only `Assembly`/`Type` access stays on in-process query contracts only, while durable/read-model shapes use `PackageAssemblyReference`.

