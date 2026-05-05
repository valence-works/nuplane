# Quickstart: Dependency Closure Loading Validation

## Goal

Validate that Nuplane can install a desired package root, automatically install its dependency closure, and load the resulting graph so root assemblies can bind to dependency assemblies.

## Scenario 0: Required MVP Vertical Slice

This scenario is the minimum acceptable implementation gate. It must pass before broader edge cases are considered complete.

1. Create a deterministic test NuGet V3 feed containing:
   - `Plugin.Dependency` `1.0.0`, exposing a public type such as `Plugin.Dependency.DependencyMarker`.
   - `Plugin.Root` `1.0.0`, declaring a NuGet dependency on `Plugin.Dependency [1.0.0]`.
2. Ensure `Plugin.Root` contains reflection metadata that forces the runtime to bind `Plugin.Dependency`, such as an assembly/type attribute, exported type, base type, or implemented interface that references `Plugin.Dependency.DependencyMarker`.
3. Configure Nuplane with only the root package:

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
       ],
       "Loading": {
         "Enabled": true
       }
     }
   }
   ```

4. Run normal startup reconciliation and loading.
5. Verify active package state contains `Plugin.Root` as root/discoverable and `Plugin.Dependency` as dependency-only/support.
6. Query `IPackageAssemblyCatalog` and reflect the root assembly metadata that references `Plugin.Dependency`.
7. Verify no `FileNotFoundException` or dependency `TypeLoadException` is thrown.
8. Verify default feature discovery sees `Plugin.Root` and does not treat `Plugin.Dependency` as an independent root.

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

## Scenario D: Independent Graphs With Different Dependency Versions

1. Create a test feed containing:
   - `Plugin.Dependency` `1.0.0`
   - `Plugin.Dependency` `2.0.0`
   - `Plugin.RootA` `1.0.0`, depending on `Plugin.Dependency [1.0.0]`
   - `Plugin.RootB` `1.0.0`, depending on `Plugin.Dependency [2.0.0]`
2. Configure Nuplane with `Plugin.RootA [1.0.0]` and `Plugin.RootB [1.0.0]` as desired roots.
3. Run reconciliation and graph loading.
4. Verify both roots activate successfully with independent graph ids/generations.
5. Verify each graph load context resolves its own selected dependency version and feature discovery exposes only the explicitly desired root assemblies.

## Scenario E: Dependency Cycle Failure

1. Create package metadata where `Plugin.A [1.0.0]` depends on `Plugin.B [1.0.0]` and `Plugin.B [1.0.0]` depends on `Plugin.A [1.0.0]`.
2. Configure Nuplane with `Plugin.A [1.0.0]` as the desired root.
3. Run reconciliation.
4. Verify graph resolution fails before acquisition, diagnostics include the detected cycle path, and the previous active graph remains available if one exists.

## Scenario F: Unsupported Required Native Asset Failure

1. Create a root package graph whose selected runtime assets include a required native or runtime-specific asset unsupported by the initial graph loader.
2. Configure Nuplane with that package as the desired root.
3. Run reconciliation and graph load preparation.
4. Verify activation fails before publishing the new graph, load-state diagnostics identify the unsupported asset, and the previous active graph remains available if one exists.

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
- Graph resolution failure logs include dependency cycle path when package metadata contains a cycle.
- Reconciliation logs include graph id, generation id, package count, activation outcome, and LKG preservation outcome.
- Load-state diagnostics include graph id, generation id, root package id, dependency package id, assembly path, unsupported asset path when applicable, and bind/load failure reason.
- Metrics include dependency graph resolution success/failure, graph activation success/failure, assembly load success/failure, and graph unload attempts.
