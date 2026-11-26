# Quickstart — Queryable Package Catalog

Run the sample with the .NET 10 SDK. Start the app from the repository root:

```bash
dotnet run --project samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj --no-launch-profile -- --urls http://127.0.0.1:5086
```

In another terminal at the repository root, pack the plugin and copy it into the folder watched by
the sample:

```bash
dotnet pack samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj -c Debug
mkdir -p samples/Nuplane.Sample.AspNetCore/packages
cp samples/Nuplane.Sample.Plugin/bin/Debug/Nuplane.Sample.Plugin.1.0.0.nupkg samples/Nuplane.Sample.AspNetCore/packages/
```

Wait a few seconds for the watcher to reconcile, then run the catalog requests:

```bash
curl -fsS http://127.0.0.1:5086/catalog/packages
curl -fsS http://127.0.0.1:5086/catalog/load-state
curl -fsS http://127.0.0.1:5086/catalog/assemblies
curl -fsS http://127.0.0.1:5086/catalog/assemblies/Nuplane.Sample.Plugin
curl -fsS http://127.0.0.1:5086/catalog/plugins
```

| Route | Expected response |
| --- | --- |
| `/catalog/packages` | Active package records with package ID, version, feed, and install path. Expect `Nuplane.Sample.Plugin` version `1.0.0`. |
| `/catalog/load-state` | Current-process loading status and per-package results. Expect a successfully loaded `Nuplane.Sample.Plugin`. |
| `/catalog/assemblies` | Loaded packages with `loadedAssemblies` and `selectedAssemblyReferences`. |
| `/catalog/assemblies/Nuplane.Sample.Plugin` | The current loaded version of that package and its assembly records. |
| `/catalog/plugins` | Sample-owned discovery results. Expect `Nuplane.Sample.Plugin.HelloPlugin`. |

The assembly route addresses the current loaded version by package ID. An ID absent from the loaded
catalog returns HTTP 404:

```bash
curl -sS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5086/catalog/assemblies/Missing.Plugin
```

The sample maps the admin package inventory, operational state, and manual reconciliation endpoints
through `Nuplane.Admin.Api`. It maps the load-state endpoint through `Nuplane.Loading.Api`.

```bash
curl -fsS http://127.0.0.1:5086/nuplane/admin/packages
curl -fsS http://127.0.0.1:5086/nuplane/admin/state
curl -fsS http://127.0.0.1:5086/nuplane/admin/load-state
curl -fsS -X POST http://127.0.0.1:5086/nuplane/admin/reconcile
```

The first response has the active inventory, the second has operational state, and the third
mirrors the loading catalog. The manual reconcile response reports the cycle outcome. When the
sample is idle it completes with HTTP 200.

The sample discovers plugins by querying `IPackageAssemblyCatalog`. Hosts that want assignability
filtering for one active package can use `IPackageTypeFinder.FindTypesAsync`. Observer callbacks
signal changes. The catalogs supply current state.

## Run the catalog tests

```bash
dotnet test nuplane.sln -c Release
```

The test suite covers persisted inventory, active versus retained packages, loading availability,
and separation of the admin and loading HTTP routes. See the
[Usage Guide](../../docs/wiki/Usage-Guide.md#query-first-integration-guidance) for production
integration.
