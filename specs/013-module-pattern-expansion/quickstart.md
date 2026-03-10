# Quickstart — Module Pattern Expansion

## Goal
Validate that directory-source and loading follow the same module-boundary rules: module-owned direct registration, module-owned options/validators/hosted services, builder integration packages for fluent APIs, last-registration-wins semantics, and no duplicate hosted services or observers.

## Preconditions
- .NET SDK installed with support for the solution target frameworks.
- Feature branch checked out: `013-module-pattern-expansion`.
- Repository restored successfully.
- Temporary core wrappers removed or reduced to migration-only delegation before final verification.

## Verification command set

Run from repository root:

```bash
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj \
	--filter "FullyQualifiedName~ConfigurationDrivenRegistrationTests|FullyQualifiedName~CoreRuntimeRegistrationIsolationTests|FullyQualifiedName~ModuleOwnershipBoundaryTests|FullyQualifiedName~DirectoryBuilderIntegrationTests|FullyQualifiedName~FeedSelectionRegistrationTests"
dotnet test test/Nuplane.Sources.Directory.Tests/Nuplane.Sources.Directory.Tests.csproj \
	--filter "FullyQualifiedName~DirectoryObservationContractTests|FullyQualifiedName~DirectorySourceRegistrationDeterminismTests"
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj \
	--filter "FullyQualifiedName~PackageAutoLoadingObserverTests|FullyQualifiedName~LoadingEventDispatcherTests|FullyQualifiedName~LoadingRegistrationDeterminismTests"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj \
	--filter "FullyQualifiedName~DirectoryWatcherDegradedFallbackIntegrationTests|FullyQualifiedName~ModuleRegistrationCompatibilityTests"
dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj \
	--filter "FullyQualifiedName~PackageTransactionCoordinatorTests"
dotnet test nuplane.sln
./build/validate-secrets.sh
```

## 1) Verify core remains generic and module loading stays optional
1. Register only `AddNuplane(...)` without loading or directory modules.
2. Confirm core runtime services resolve and reconciliation still runs.
3. Confirm loading-specific services are absent unless the loading module is explicitly registered.

## 2) Verify direct module registration surfaces
1. Register the directory module through its module-owned `IServiceCollection` extension.
2. Register the loading module through its module-owned direct registration extension.
3. Confirm each module activates without referencing internal core implementation types.
4. Confirm module options are owned and validated by the module path that consumes them.

## 3) Verify builder integration delegation
1. Register loading through `AutoloadPackages(...)`.
2. Register directory-source through `AddDirectoryFeed(name, path, configure?)` from `Nuplane.Sources.Directory.Hosting`.
3. Register config-driven directory feeds through `AddDirectoryFeedsFromConfiguration(configuration)`.
4. Confirm the builder APIs delegate to the same registration services used by direct registration.
5. Confirm no module-specific orchestration logic remains in `src/Nuplane`.

## 4) Verify duplicate-registration determinism
1. Register the same module through both direct and builder paths with different option values.
2. Confirm the most recent registration wins.
3. Confirm only one effective hosted-service graph remains for that module.
4. Confirm observers and event dispatchers are not duplicated.

## 5) Verify observability and safety preservation
1. Exercise directory watcher registration and confirm degraded-fallback health behavior still reports correctly.
2. Exercise loading observer registration and confirm load events still dispatch correctly.
3. Re-run store transactional safety tests to confirm registration refactors do not affect LKG behavior.
4. Run the secret validation script to ensure no credential-handling regression was introduced.

## Expected Test Evidence
- Runtime tests proving core/module ownership boundaries.
- Directory tests proving debounce behavior and duplicate-registration determinism.
- Loading tests proving singleton-safe registration and observer/event-dispatch behavior.
- Integration tests proving health/degradation and compatibility behavior after wrapper removal.
- Store regression tests proving transactional semantics remain unchanged.

## Expected command outcomes
- All targeted test commands pass with 0 failed tests.
- Full solution test pass (`dotnet test nuplane.sln`).
- Secret validation script reports no committed credentials.
- Final public API/docs point consumers to module-owned direct registration and module-owned builder integration packages only.