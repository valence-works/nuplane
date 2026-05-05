# Quickstart: Dependency Closure Loading Validation

## Goal

Validate that Nuplane can install a desired package root, automatically install its dependency closure, and load the resulting graph so root assemblies can bind to dependency assemblies.

## Scenario A: Remote Root With Remote Dependency

1. Create or use a test NuGet V3 feed containing:
   - `Plugin.Dependency` `1.0.0`
   - `Plugin.Root` `1.0.0`, with a NuGet dependency on `Plugin.Dependency [1.0.0]`
2. Configure Nuplane with only the root package in `IncludePatterns`:

   ```json
   {
     "Nuplane": {
       "Feeds": [
         {
           "Name": "test-feed",
           "Url": "https://localhost:5005/v3/index.json",
           "IncludePatterns": [
             "Plugin.Root [1.0.0]"
           ]
         }
       ]
     }
   }
   ```

3. Run reconciliation.
4. Verify active package state contains:
   - `Plugin.Root` as root/discoverable
   - `Plugin.Dependency` as dependency-only/support
   - one shared graph id/generation for both packages
5. Query `IPackageAssemblyCatalog`.
6. Verify reflection over `Plugin.Root` succeeds without a missing assembly error for `Plugin.Dependency`.

## Scenario B: Elsa RabbitMQ Package

1. Configure only the RabbitMQ package root:

   ```json
   {
     "Nuplane": {
       "Feeds": [
         {
           "Name": "elsa-3",
           "Url": "https://f.feedz.io/elsa-workflows/elsa-3/nuget/index.json",
           "IncludePatterns": [
             "Elsa.ServiceBus.MassTransit.RabbitMq [3.8.0-preview,)"
           ]
         }
       ]
     }
   }
   ```

2. Start the host.
3. Trigger reconciliation.
4. Hit a host endpoint that forces feature discovery, such as `/health` in the Elsa Pro Server scenario.
5. Verify no `FileNotFoundException` is thrown for `Elsa.ServiceBus.MassTransit`.

## Scenario C: Local Directory Root With Dependency

1. Place `SamplePackage.0.0.1.nupkg` in the configured local package directory.
2. Ensure the sample package declares a dependency on `SampleDependency [1.0.0]`.
3. Provide `SampleDependency.1.0.0.nupkg` in the local directory or in a configured trusted feed.
4. Run reconciliation.
5. Verify both packages are installed and share one graph id.
6. Remove `SampleDependency.1.0.0.nupkg` and clear any remote source that could provide it.
7. Run reconciliation again.
8. Verify Nuplane records a graph resolution failure and preserves the previous active graph if one exists.

## Required Automated Validation

Run the focused test suites:

```bash
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj
dotnet test test/Nuplane.Sources.Directory.Tests/Nuplane.Sources.Directory.Tests.csproj
```

Run the full solution before opening the implementation PR:

```bash
dotnet test Nuplane.sln
```

## Expected Diagnostics

- Graph resolution logs include root package id, dependency package id, requested version range, selected version, source, and target framework.
- Reconciliation logs include graph id, generation id, package count, activation outcome, and LKG preservation outcome.
- Load-state diagnostics include graph id, generation id, root package id, dependency package id, assembly path, and bind/load failure reason.
- Metrics include dependency graph resolution success/failure, graph activation success/failure, assembly load success/failure, and graph unload attempts.
