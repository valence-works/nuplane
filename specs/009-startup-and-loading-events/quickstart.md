# Quickstart: Startup Reconciliation & Loading Events

**Branch**: `009-startup-and-loading-events` | **Date**: 2026-03-05

This quickstart shows how to validate the startup loading flow and the new
`IPackageLoadingObserver.OnPackagesLoadedAsync` event once the feature is implemented.

---

## Prerequisites

- Feature branch `009-startup-and-loading-events` built successfully (`dotnet build`)
- Sample project works locally (see `samples/Nuplane.Sample.AspNetCore/README.md` or
  existing quickstart for 007/008)
- `samples/Nuplane.Sample.AspNetCore/appsettings.Development.json` points at a valid
  drop folder containing at least one package

---

## Scenario 1 — Startup Cycle Fires Automatically

**Goal**: Verify that a reconciliation cycle runs immediately on host startup (before the
first periodic tick) with `TriggerType.Startup`, and that packages are loaded within 5 s.

### Steps

1. Start the sample host:
   ```bash
   cd samples/Nuplane.Sample.AspNetCore
   dotnet run
   ```

2. Observe the console output. Within the first few seconds, before any `Scheduled` cycle
   log appears, you should see a log line containing `TriggerType = Startup`:

   ```
   info: Nuplane.ReconciliationHostedService[0]
         Starting startup reconciliation cycle.
   info: Nuplane.Runtime.ReconciliationService[0]
         Reconciliation cycle started. CorrelationId=<guid> TriggerType=Startup
   ...
   info: Nuplane.Runtime.ReconciliationService[0]
         Reconciliation cycle completed. CorrelationId=<guid>
   ```

3. Subsequently the periodic `Scheduled` cycles should appear at the configured interval.

### Pass Criteria

- A `TriggerType=Startup` cycle log appears before the first `TriggerType=Scheduled` log.
- No exception is logged for the startup cycle.
- If the drop folder contains packages, an `OnPackagesLoadedAsync` log appears (see Scenario 2).

---

## Scenario 2 — OnPackagesLoadedAsync Fires with Correct Payload

**Goal**: Verify that `PluginDiscoveryObserver.OnPackagesLoadedAsync` is called after packages
are loaded, with a non-empty `LoadedPackages` list containing valid `PackageLoadSession` entries.

### Setup

Ensure at least one package is present in the drop folder:
```bash
ls samples/Nuplane.Sample.AspNetCore/drop-folder/
# Should show one or more .nupkg / package directories
```

### Steps

1. Start the host (or restart with a clean package folder):
   ```bash
   dotnet run
   ```

2. In the console, look for the observer log from `PluginDiscoveryObserver`:
   ```
   info: Nuplane.Sample.AspNetCore.PluginDiscoveryObserver[0]
         Packages loaded. Count=1 CorrelationId=<guid>
   ```
   (This log is added to `PluginDiscoveryObserver.OnPackagesLoadedAsync` as part of this feature.)

3. Copy an additional package into the drop folder while the host is running to trigger a
   periodic cycle load:
   ```bash
   cp path/to/Nuplane.Sample.Plugin.1.0.0.nupkg samples/Nuplane.Sample.AspNetCore/drop-folder/
   ```

4. Within one reconciliation interval the periodic cycle fires, loads the new package, and
   `OnPackagesLoadedAsync` fires again.

### Pass Criteria

- `OnPackagesLoadedAsync` is called at least once.
- `evt.LoadedPackages.Count >= 1`.
- `evt.CorrelationId` matches the `CorrelationId` in the immediately preceding
  `ReconciliationService` cycle log.
- No `OnPackagesChangedAsync` type-scanning code runs (audit log only).

---

## Scenario 3 — Sample App Uses IPackageLoadingObserver

**Goal**: Verify that `PluginDiscoveryObserver` now implements `IPackageLoadingObserver`
and that `Program.cs` registers it correctly.

### Steps

1. Open `samples/Nuplane.Sample.AspNetCore/PluginDiscoveryObserver.cs` and confirm:
   - Class declaration: `public class PluginDiscoveryObserver : INuplaneObserver, IPackageLoadingObserver`
   - `OnPackagesChangedAsync` contains only an audit log (no type scanning).
   - `OnPackagesLoadedAsync` contains the type scanning and plugin registration logic.

2. Open `samples/Nuplane.Sample.AspNetCore/Program.cs` and confirm:
   - `EnableAutomaticReconciliation = true` is set.
   - `PluginDiscoveryObserver` is registered for both `INuplaneObserver` **and**
     `IPackageLoadingObserver`.

3. Run the sample and exercise a type-scan scenario:
   ```bash
   dotnet run
   # Navigate to the plugin endpoint (see sample README) to verify plugin types were registered
   ```

### Pass Criteria

- No compile errors in the sample project.
- Plugin types discovered correctly via `OnPackagesLoadedAsync`.

---

## Running the Targeted Unit Tests

Once implemented, run the new test suites directly:

```bash
# Startup cycle ordering test
dotnet test test/Nuplane.Runtime.Tests/ \
  --filter "FullyQualifiedName~StartupCycle" --logger "console;verbosity=normal"

# PackageAutoLoadingObserver unit tests
dotnet test test/Nuplane.Loading.Tests/ \
  --filter "FullyQualifiedName~PackageAutoLoadingObserver" --logger "console;verbosity=normal"

# LoadingEventDispatcher unit tests
dotnet test test/Nuplane.Loading.Tests/ \
  --filter "FullyQualifiedName~LoadingEventDispatcher" --logger "console;verbosity=normal"

# End-to-end integration test
dotnet test test/Nuplane.Integration.Tests/ \
  --filter "FullyQualifiedName~StartupLoadingEvent" --logger "console;verbosity=normal"
```

All four commands should exit 0 with no failures.

