# Quickstart — Queryable Package Catalog

## Goal
Validate that Nuplane exposes a query-first active package catalog from core runtime services, a separate loading catalog from the optional loading module, an operational-state surface that remains distinct from package inventory, and a clean ownership split where core admin does not define loading routes.

## Preconditions
- .NET SDK installed with support for the solution target frameworks.
- Feature branch checked out: `014-query-package-catalog`.
- Repository restored successfully.
- A writable local package directory is available for the sample host.
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

Focused loading-catalog validation commands:

```bash
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --filter "FullyQualifiedName~LoadingCatalogTests"
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --filter "FullyQualifiedName~LoadingCatalogBoundaryTests"
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --filter "FullyQualifiedName~LoadingCatalogObservabilityTests"
```

## 1) Validate the active package catalog after reconcile
1. Start a host with Nuplane core runtime configured and reconcile at least one package.
2. Query the standalone active package catalog directly from host code or through `GET /nuplane/admin/packages`.
3. Confirm every active package entry contains package ID, active version, provenance, install path, and activation time.
4. Confirm the response is deterministic and ordered consistently across repeated identical reads.

## 2) Validate restart recovery without observer replay
1. Stop the host after a successful reconcile.
2. Restart the host without changing desired-state inputs.
3. Query the active package catalog before any new reconcile completes.
4. Confirm the catalog matches the pre-restart active package inventory exactly.

## 3) Validate active-versus-retained separation
1. Reconcile to a newer package version while leaving an older retained version on disk for rollback/cleanup.
2. Query the active package catalog again.
3. Confirm only the currently active version appears even though the older package files remain on disk.
4. Remove a package from desired state and confirm it disappears from the active catalog while retained cleanup artifacts remain excluded.

## 4) Validate loading ownership and availability states
1. Run the host without the loading module or `Nuplane.Loading.Api` installed/mapped.
2. Confirm the core-admin surface still exposes `/nuplane/admin/packages`, `/nuplane/admin/state`, and `/nuplane/admin/reconcile`, but no `/nuplane/admin/loading` route is present.
3. Run the host with the loading module installed but disabled and confirm the standalone loading catalog reports `Disabled`.
4. If the loading-owned HTTP package is installed, query `GET /nuplane/admin/loading` and confirm it mirrors the standalone loading catalog rather than a core-admin wrapper.
5. Restart a loading-enabled host before any current-process refresh and confirm the loading catalog reports `Stale` while the active package catalog remains readable.

## 5) Validate package-versus-loading divergence
1. Reconcile a package that activates successfully but fails to load.
2. Query the active package catalog and confirm the package remains active.
3. Query the loading catalog and confirm the same package reports `Failed` with diagnostics and no discovered-type data.
4. Confirm operational-state reads remain separate and report the degraded condition without redefining package availability.

## 6) Validate scan-candidate driven discovery in the sample
1. Start `samples/Nuplane.Sample.AspNetCore` with optional loading enabled.
2. Pack and drop `Nuplane.Sample.Plugin` into the configured directory feed.
3. Query `/catalog/packages` for the authoritative active package inventory, `/catalog/loading` for active assembly scan candidates, and `/catalog/plugins` for the sample's explicit host-owned `IPlugin` discovery output.
4. Confirm `/catalog/plugins` only returns plugin types from active loaded packages with scan candidates and that each entry identifies the package, version, discovered plugin type, and candidate assemblies used for scanning.
5. Confirm `PackageChangeObserver` is used only for logging/invalidation, while `PluginDiscoveryObserver` re-queries the loading catalog instead of relying on discovered-type payloads from Nuplane.

## 7) Validate loading observability
1. Query the loading catalog while loading is disabled and confirm a structured read log emits `ReasonCode=loading-disabled`.
2. Restart a loading-enabled host before refresh and confirm a structured read log emits `ReasonCode=loading-stale` plus degraded loading-read metrics.
3. Force one package to fail loading while it remains active and confirm a structured read log emits `ReasonCode=loading-divergence` plus degraded loading-read metrics.

## Expected test evidence
- Runtime tests proving active package catalog persistence, ordering, and operational-state separation.
- Store tests proving atomic descriptor persistence and active-versus-retained behavior.
- Loading tests proving disabled, stale, loaded, and failed loading states plus deterministic scan candidates.
- Loading observability tests proving stale and divergence logs/metrics stay owned by the loading module.
- Admin/API tests proving core-admin routes remain loading-free and loading HTTP composition is owned by the loading package.
- Sample validation proving query-first scan-candidate discovery.

## Expected outcomes
- All automated test commands pass.
- Active package reads work immediately after restart from persisted state.
- Loading reads clearly distinguish `Disabled`, `Stale`, `Loaded`, and `Failed` scenarios at the correct layer, while module absence is represented by missing loading composition rather than a core-admin placeholder.
- The sample demonstrates host-owned discovery from catalog-provided scan guidance.

