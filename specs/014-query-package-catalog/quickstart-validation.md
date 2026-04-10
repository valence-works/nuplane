# Quickstart Validation Evidence — Queryable Package Catalog

**Feature**: `014-query-package-catalog`  
**Date**: 2026-04-10  
**Status**: PASS

## Validation Steps

| # | Scenario | Status | Evidence |
|---|----------|--------|----------|
| 1 | Active package catalog persists and round-trips trusted provenance | PASS | `StoreRegistryTests` covers persisted `ActivePackageDescriptor` data and JSON/store round-trip behavior. |
| 2 | Active package catalog reads are deterministic and active-only | PASS | `ActivePackageCatalogTests` verifies deterministic ordering, active-only projection, and descriptor issue detection. |
| 3 | Package catalog degradation surfaces in operational state | PASS | `PackageCatalogHealthTests` verifies `package-catalog-issues:*` degraded reasons through `OperationalSnapshotProjector`. |
| 4 | Loading catalog distinguishes disabled, stale, loaded, and failed states | PASS | `LoadingCatalogTests` verifies disabled, stale, loaded, and failed package projections. `PackageLoaderCatalogCandidateTests` verifies deterministic scan candidates. `LoadingCatalogHealthTests` verifies stale-loading degraded reporting. |
| 5 | Loading-owned assembly queries respect active-package boundaries and hide discovered-type identities | PASS | `LoadingCatalogBoundaryTests` verifies only active packages are projected, scan candidates stay under the active install path, and public loading contracts expose assembly metadata rather than discovered types. `PackageAssemblyCatalogTests` verifies the sane-default assembly catalog only returns active loaded packages, supports active-package-by-id plus exact package/version reads, and returns empty or null results when loading is disabled, stale, inactive, or not loaded. |
| 6 | Loading-owned observability emits stale/divergence logs and metrics | PASS | `LoadingCatalogObservabilityTests` verifies `ReasonCode=loading-stale` and `ReasonCode=loading-divergence` structured logs plus loading/degraded metric tags. |
| 7 | Admin composition keeps package, loading, and state reads separate | PASS | `AdminPackageCatalogCompositionTests`, `AdminLoadingCatalogCompositionTests`, `AdminOperationalStateCompositionTests`, `AdminCompositionCleanBreakTests`, `AdminReadEndpointContractTests`, `AdminEndpointOwnershipContractTests`, and `OperationalStateSnapshotTests` verify separate in-process reads, clean-break endpoint ownership, and the state-only operational model. |
| 8 | Operational-state contributors enrich the core state surface without re-coupling admin | PASS | `OperationalStateContributorIntegrationTests` verifies the loading contributor is discovered through DI and surfaces `loading-stale:*` degraded reasons through the core operational snapshot. |
| 9 | Sample query-first assets, including `/catalog/assemblies`, `/catalog/assemblies/{packageId}`, `/catalog/assemblies/{packageId}/{version}`, and explicit `/catalog/plugins` discovery, still build | PASS | `dotnet pack samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj -c Debug --nologo` and `dotnet build samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj -c Debug --nologo` both succeeded after the sample active-package assembly route refresh. |
| 10 | Existing runtime, loading, integration, and store behavior remains intact | PASS | `dotnet test nuplane.sln` completed successfully after the feature changes. |
| 11 | Secret scan remains clean | PASS | `./build/validate-secrets.sh` reported no committed credentials. |

## Focused Test Commands

```bash
dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj --filter "FullyQualifiedName~StoreRegistryTests"
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~ActivePackageCatalogTests|FullyQualifiedName~PackageCatalogHealthTests|FullyQualifiedName~AdminPackageCatalogCompositionTests|FullyQualifiedName~AdminLoadingCatalogCompositionTests|FullyQualifiedName~AdminOperationalStateCompositionTests|FullyQualifiedName~OperationalSnapshotProjectionTests|FullyQualifiedName~OperationalStateSnapshotTests"
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --nologo
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --nologo
dotnet pack samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj -c Debug --nologo
dotnet build samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj -c Debug --nologo
./build/validate-secrets.sh
```

## Focused Test Results

| Test Project | Filter | Passed | Failed |
|---|---|---|---|
| Nuplane.Store.Tests | `StoreRegistryTests` | 13 | 0 |
| Nuplane.Runtime.Tests | active catalog + package health + admin composition + operational state | 19 | 0 |
| Nuplane.Loading.Tests | loading catalog + boundaries + observability + scan candidates + loading health + ownership + assembly catalog | 76 | 0 |
| Nuplane.Integration.Tests | active catalog consistency/restart/query-first + loading route/restart + admin route ownership/state contributor + module registration compatibility | 92 | 0 |
| Sample assets | plugin pack + sample host build | 2 commands | 0 failures |
| Secret scan | repository credential-pattern scan | 1 command | 0 failures |

## Full Solution Validation

```text
Test summary: total: 468, failed: 0, succeeded: 468, skipped: 0
Build succeeded in 52,9s
```

## Secret Validation

```text
[validate-secrets] scanning repository for credential-like patterns...
[validate-secrets] OK - no committed source credentials detected.
```

