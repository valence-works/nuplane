# Quickstart Validation Evidence — Module Pattern Expansion

**Feature**: `013-module-pattern-expansion`  
**Date**: 2025-07-15  
**Status**: PASS

## Validation Steps

| # | Scenario | Status | Evidence |
|---|----------|--------|----------|
| 1 | Core remains generic, loading stays optional | PASS | `CoreRuntimeRegistrationIsolationTests` (1 test) — core resolves and reconciles without loading. `Nuplane.csproj` no longer references `Nuplane.Sources.Directory`. |
| 2 | Direct module registration surfaces work | PASS | `AddNuplaneDirectorySource` (directory) and `AddNuplaneLoading` (loading) register modules without builder. `ModuleOwnershipBoundaryTests` (5 tests), `LoadingRegistrationDeterminismTests` (4 tests), `DirectorySourceRegistrationDeterminismTests` (5 tests). |
| 3 | Builder integration delegation works | PASS | `DirectoryBuilderIntegrationTests` (6 tests) — `AddDirectoryFeed` delegates to `DirectorySourceRegistrationServices`. `ConfigurationDrivenRegistrationTests` (3 new tests) — `AddDirectoryFeedsFromConfiguration` registers feeds from config. `FeedSelectionRegistrationTests` (3 tests updated to `AddDirectoryFeed`). |
| 4 | Duplicate-registration determinism | PASS | `ModuleRegistrationCompatibilityTests` (8 tests) — builder+direct coexistence, last-registration-wins, no duplicate hosted services. `DirectoryBuilderIntegrationTests.ReRegistration_ReplacesEarlierFeed`. |
| 5 | Observability and safety preservation | PASS | `DirectoryWatcherDegradedFallbackIntegrationTests` (3 tests) — degraded fallback health. `PackageAutoLoadingObserverTests` (8 tests) — load event dispatch. `PackageTransactionCoordinatorTests` (9 tests) — LKG transactional safety. Secret scan clean. |

## Test Commands

```bash
dotnet test test/Nuplane.Runtime.Tests/ --filter "FullyQualifiedName~ConfigurationDrivenRegistrationTests|FullyQualifiedName~CoreRuntimeRegistrationIsolationTests|FullyQualifiedName~ModuleOwnershipBoundaryTests|FullyQualifiedName~DirectoryBuilderIntegrationTests|FullyQualifiedName~FeedSelectionRegistrationTests"
dotnet test test/Nuplane.Sources.Directory.Tests/ --filter "FullyQualifiedName~DirectoryObservationContractTests|FullyQualifiedName~DirectorySourceRegistrationDeterminismTests"
dotnet test test/Nuplane.Loading.Tests/ --filter "FullyQualifiedName~PackageAutoLoadingObserverTests|FullyQualifiedName~LoadingEventDispatcherTests|FullyQualifiedName~LoadingRegistrationDeterminismTests"
dotnet test test/Nuplane.Integration.Tests/ --filter "FullyQualifiedName~DirectoryWatcherDegradedFallbackIntegrationTests|FullyQualifiedName~ModuleRegistrationCompatibilityTests"
dotnet test test/Nuplane.Store.Tests/ --filter "FullyQualifiedName~PackageTransactionCoordinatorTests"
dotnet test nuplane.sln
./build/validate-secrets.sh
```

## Targeted Test Results

| Test Project | Filter | Passed | Failed |
|---|---|---|---|
| Nuplane.Runtime.Tests | ConfigurationDriven, CoreIsolation, ModuleOwnership, DirectoryBuilder, FeedSelection | 40 | 0 |
| Nuplane.Sources.Directory.Tests | DirectoryObservation, DirectoryDeterminism | 7 | 0 |
| Nuplane.Loading.Tests | AutoLoading, EventDispatcher, LoadingDeterminism | 18 | 0 |
| Nuplane.Integration.Tests | DegradedFallback, ModuleCompatibility | 11 | 0 |
| Nuplane.Store.Tests | TransactionCoordinator | 9 | 0 |

## Full Solution Test

```
Passed!  - Failed:     0, Passed:    43, Skipped:     0, Total:    43 - Nuplane.Store.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    25, Skipped:     0, Total:    25 - Nuplane.NuGet.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11 - Nuplane.Sources.Directory.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   345, Skipped:     0, Total:   345 - Nuplane.Runtime.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    46, Skipped:     0, Total:    46 - Nuplane.Loading.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    81, Skipped:     0, Total:    81 - Nuplane.Integration.Tests.dll (net10.0)

Total: 551 tests, 0 failed, 0 skipped.
```

## Secret Validation

```
[validate-secrets] scanning repository for credential-like patterns...
[validate-secrets] OK - no committed source credentials detected.
```
