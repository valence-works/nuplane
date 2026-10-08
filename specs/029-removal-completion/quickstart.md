# Quickstart: Reconciliation Completion After Removals

## Focused validation

Run from the Nuplane repository root through the normal shared dotnet wrapper:

```sh
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter FullyQualifiedName~HealthAndMetricsMiddlewareTests
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --filter FullyQualifiedName~PackageAutoLoadingObserverTests
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter FullyQualifiedName~RemovalCompletionIntegrationTests
```

The integration test seeds a unique file-backed store with one active package, runs the normal reconciliation pipeline with no desired packages, and reads the persisted state from inside the registered completion observer. It asserts that the active set is already empty and that the callback has the removal with no applied packages. Delivery remains within Nuplane’s existing cancellation and observer-isolation semantics.

## Mutation proof

Temporarily remove only `|| changeSet.Removed.Count > 0` from the middleware completion predicate. The integration regression must fail because the callback is not received. Restore the predicate and rerun that test to green. Do not commit the mutation.

## Production target builds

```sh
dotnet build src/Nuplane/Nuplane.csproj --no-restore -f net8.0
dotnet build src/Nuplane/Nuplane.csproj --no-restore -f net9.0
dotnet build src/Nuplane/Nuplane.csproj --no-restore -f net10.0
dotnet build src/Nuplane.Abstractions/Nuplane.Abstractions.csproj --no-restore -f net8.0
dotnet build src/Nuplane.Abstractions/Nuplane.Abstractions.csproj --no-restore -f net9.0
dotnet build src/Nuplane.Abstractions/Nuplane.Abstractions.csproj --no-restore -f net10.0
```
