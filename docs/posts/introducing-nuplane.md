# Introducing Nuplane: NuGet Packages as a Runtime Primitive

Most .NET applications treat NuGet packages as a build-time concept. You add a reference, run `dotnet restore`, compile, and ship. The packages are baked into the binary. If you want to update them, you redeploy.

That works fine for most cases. But there's a class of problems where it doesn't — where you actually need to extend a running application without taking it down. Hot-reload plugin systems. SaaS platforms that let tenants customize behavior. Workflow engines where operators need to deploy new steps without a maintenance window. Internal tool hosts that should just pick up new capabilities as they're pushed.

For those cases, the usual pattern is to roll your own packaging and loading system: copy some DLLs to a folder, write some `AssemblyLoadContext` plumbing, build a file watcher, figure out rollback, and add enough logging to understand what happened when things go wrong. It's not impossible, but it's a lot of infrastructure to carry for what is fundamentally a solved problem.

Nuplane is a .NET library that handles this infrastructure. It treats NuGet packages as a runtime primitive — something your application can install, update, and load while it's running.

---

## What Nuplane Actually Does

At its core, Nuplane implements a deterministic control loop:

1. Determine the **desired package set** (from a local folder, a NuGet feed, or both)
2. Compare against **current state** (what's already installed and active)
3. Compute the diff
4. Apply transactional per-package changes to a local store
5. Optionally load the resulting assemblies into isolated contexts
6. Emit change events your host can react to

That loop runs on a configurable poll interval, or fires immediately when a file watcher detects a new `.nupkg` in a watched directory. The result is that you can drop a package file into a folder and have its types available in the running process within about a second.

A few properties make this more than just a wrapper around `AssemblyLoadContext`:

**Deterministic storage.** Packages live in a predictable on-disk structure. Nuplane manages active version pointers, staging, and prior versions independently. Updates are atomic: download to staging, validate, move to the immutable store, switch the active pointer, persist state. If anything in that chain fails, the previous version stays active.

**Last-known-good (LKG) protection.** When an update fails — bad package, missing dependency, load error — Nuplane preserves the last successfully applied version rather than leaving the system in a half-updated state. The package stays active; the failure is logged and surfaced through health checks.

**Host neutrality.** Nuplane does not define plugin semantics, mutate your DI container, or tell your application what to do with loaded types. It resolves and installs packages, loads assemblies, and fires events. What you call those packages — plugins, feature modules, extensions, rule sets — and how you activate and configure them is entirely your problem. That's a deliberate boundary.

---

## The Drop-Folder Workflow

The fastest way to understand Nuplane is through its drop-folder behavior. You configure a directory as a feed, enable the file watcher, and add the optional loading module:

```csharp
var builder = WebApplication.CreateBuilder(args);
var nuplaneConfiguration = builder.Configuration.GetSection("Nuplane");

builder.Services.AddNuplane(nuplaneConfiguration, nuplane =>
{
    nuplane.AddDirectoryFeedsFromConfiguration(nuplaneConfiguration);
    nuplane.AutoloadPackages(nuplaneConfiguration.GetSection("Loading"));
    nuplane.OnPackagesChanged<PackageChangeObserver>();
    nuplane.OnPackagesChanged<PluginDiscoveryObserver>();
});
```

The configuration side of this declares the folder feed, the watcher behavior, and loading settings:

```json
{
  "Nuplane": {
    "Setup": {
      "AutomaticReconciliation": true,
      "PollInterval": "00:01:00",
      "Feeds": [
        {
          "Name": "local-packages",
          "DirectoryPath": "packages",
          "IncludePatterns": ["*"],
          "Directory": {
            "Watch": true,
            "DebounceWindow": "00:00:01"
          }
        }
      ]
    },
    "Loading": {
      "Enabled": true,
      "SharedAssemblies": [
        {
          "Name": "MyApp.Abstractions",
          "PublicKeyToken": "31bf3856ad364e35",
          "MajorVersion": 1
        }
      ]
    }
  }
}
```

The `SharedAssemblies` list is how you tell Nuplane which contracts — your plugin interfaces, your shared model types — should be resolved from the host rather than loaded fresh per-package. This is the standard mechanism for making types from different isolated load contexts assignable to each other, which is the usual stumbling block when you first roll your own plugin loading.

Once that's wired up, the workflow is:

```bash
# Build the plugin package
dotnet pack MyPlugin/MyPlugin.csproj -c Release

# Drop it into the watched folder
cp MyPlugin/bin/Release/MyPlugin.1.0.0.nupkg packages/
```

Nuplane's watcher fires (with a 1-second debounce), reconciliation runs, assemblies load, and your registered observers are called.

---

## Reacting to Package Changes

Observers in Nuplane are simple: implement `INuplaneObserver` and register it with `OnPackagesChanged<T>()`. The interface gives you callbacks for the start of a change, the completion, and per-package failures.

```csharp
internal sealed class PackageChangeObserver(ILogger<PackageChangeObserver> logger)
    : INuplaneObserver
{
    public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct)
    {
        logger.LogInformation(
            "Packages changing. Added={AddedCount}, Updated={UpdatedCount}, CorrelationId={CorrelationId}",
            changeSet.Added.Count,
            changeSet.Updated.Count,
            changeSet.CorrelationId);

        return Task.CompletedTask;
    }

    public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
    {
        logger.LogInformation(
            "Packages changed. Added={AddedCount}, Updated={UpdatedCount}, Removed={RemovedCount}",
            changeSet.Added.Count,
            changeSet.Updated.Count,
            changeSet.Removed.Count);

        return Task.CompletedTask;
    }

    public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
    {
        logger.LogWarning(exception, "Package operation failed for {PackageId}", packageId);
        return Task.CompletedTask;
    }
}
```

One important thing about observers: they're invalidation signals, not the authoritative source of state. The recommended pattern is to treat an observer callback as a trigger to re-query the catalog, not as an event to replay or accumulate. This matters in practice because it keeps your host from building a fragile event-history model that diverges from actual runtime state.

---

## Reading State from the Catalog

When you need the current state of loaded packages and types, Nuplane exposes a set of query surfaces rather than expecting you to track state through observer callbacks.

The loading module provides two surfaces you'll use frequently:

**`IPackageAssemblyCatalog`** — the primary surface for loading-enabled hosts. It gives you the currently active loaded assemblies across all active packages, or for a specific package ID.

**`IPackageTypeFinder`** — convenience wrapper over the assembly catalog that applies assignability filtering. Hand it an interface type and a package ID and it returns all implementations currently loaded from that package.

A typical host-owned plugin discovery service looks like this:

```csharp
internal sealed class PluginCatalog(
    IPackageAssemblyCatalog packageAssemblyCatalog,
    IPackageTypeFinder packageTypeFinder)
{
    public async Task<IReadOnlyList<DiscoveredPlugin>> DiscoverAsync(CancellationToken ct)
    {
        var discovered = new List<DiscoveredPlugin>();

        foreach (var package in (await packageAssemblyCatalog.GetPackagedAssembliesAsync(ct))
                     .Where(p => p.AssemblyReferences.Count > 0))
        {
            var pluginTypes = await packageTypeFinder.FindTypesAsync(
                typeof(IPlugin), package.PackageId, ct);

            foreach (var pluginType in pluginTypes)
            {
                discovered.Add(new DiscoveredPlugin(
                    package.PackageId,
                    package.Version,
                    pluginType));
            }
        }

        return discovered;
    }
}
```

That's the full pattern: Nuplane handles the package lifecycle, you query what's loaded, and you do whatever your application needs with the resulting types. The assembly loading boundary and the shared assembly configuration handle the cross-context type compatibility for you.

If you don't need full loading and only want package inventory visibility — maybe you're tracking which versions are active for observability, or coordinating a package state across services — you can use the core runtime without the loading module at all.

---

## Remote Feed Support

The drop-folder workflow is the easiest to demonstrate, but Nuplane also supports NuGet v3 remote feeds with polling:

```csharp
builder.Services.AddNuplane(nuplane =>
{
    nuplane.PollEvery(TimeSpan.FromMinutes(1));
    nuplane.AddFeed("nuget.org", feed =>
    {
        feed.FromUri("https://api.nuget.org/v3/index.json");
        feed.Include("MyCompany.Plugins.*");
    });
});
```

Or declaratively in configuration, pointing at nuget.org or an internal feed with an include pattern to filter which packages Nuplane tracks:

```json
{
  "Name": "nuget.org",
  "ServiceIndex": "https://api.nuget.org/v3/index.json",
  "IncludePatterns": ["MyCompany.Plugins.*"]
}
```

One thing worth noting about include patterns: a feed with no patterns defined contributes nothing. This is intentional. The default is restrictive — you have to be explicit about what a feed is authoritative for. This matters when you're mixing internal and public feeds and you don't want accidental resolution crossover.

---

## Where Nuplane Fits

The cases where Nuplane earns its keep tend to share a few properties: the host is long-running, extensions come from sources outside the host's own deployment, and the cost of a restart is real.

**Hot-reload plugin systems.** This is the primary use case the sample demonstrates. Package your plugins as `.nupkg` files, point Nuplane at a watched folder, and you have a working plugin loading system with transactional safety and LKG fallback built in.

**Modular feature delivery in SaaS platforms.** If your application is split into independently versioned feature packages, Nuplane lets you update a single feature at runtime. The others keep running. This is useful when different parts of the product are on different release cadences and you need fine-grained control over what's live.

**Per-tenant customization.** Load per-tenant behavior packages dynamically, isolated per load context. Each tenant's package set is independent; a bad package from one tenant doesn't affect others. The isolation model here is the same `AssemblyLoadContext` isolation .NET provides — Nuplane wires it up and manages the shared contract boundaries.

**Workflow and rule engines.** Operators need to deploy new steps, validators, or routing rules without a maintenance window. With Nuplane, those are packages. Push a new version; the engine picks it up live.

**Internal platform hosting.** Push a new package to a shared internal folder; every host watching that folder reconciles automatically. No CI/CD pipeline change, no coordinated deployment.

---

## What to Watch Out For

Nuplane is infrastructure, and like most infrastructure it requires you to think about the edges.

**Assembly unload is not guaranteed.** .NET's `AssemblyLoadContext` unload is cooperative and can fail if live references remain. Nuplane tracks unload-pending state and retries, but if your host holds references to types from an old assembly version, you can end up with multiple versions loaded simultaneously. Design your plugin activation patterns accordingly.

**It does not sandbox untrusted code.** If you're loading packages from external or user-provided sources, enforcement of what that code can do is your responsibility. Nuplane has hooks for package validation (hash checks, signature checks, allowlists), but it does not run loaded code in a restricted context.

**Kubernetes needs persistent storage.** Nuplane keeps its package store and state file on disk. If a pod restarts with an empty filesystem, it will re-download and re-extract packages from scratch. For production deployments on Kubernetes, mount a persistent volume for both the package install root and the state file. A `StatefulSet` with per-replica volumes is the recommended shape.

**Omitted include patterns mean no packages.** If you configure a feed without `IncludePatterns` or `IncludeAll`, that feed contributes nothing. It's a breaking change from earlier behavior, and it's worth knowing about before you wonder why nothing is resolving.

---

## The Module Layout

Nuplane is split into focused packages so you only take what you need:

| Package | What it adds |
|---|---|
| `Nuplane` | Core composition and DI setup surface |
| `Nuplane.Runtime` | Reconciliation engine |
| `Nuplane.Store` | Deterministic on-disk package store |
| `Nuplane.NuGet` | NuGet v3 feed integration |
| `Nuplane.Sources.Directory` | Directory-backed feed and file watcher |
| `Nuplane.Admin` + `Nuplane.Admin.Api` | HTTP admin and query routes |
| `Nuplane.Loading` + `Nuplane.Loading.Api` | Optional assembly loading and load-state surfaces |

The loading module is genuinely optional — if all you need is package reconciliation and state tracking without loading assemblies into your process, the baseline modules work without it.

---

## Trying It Out

The repository includes a working ASP.NET Core sample that demonstrates the full drop-folder to type discovery loop. The setup is:

```bash
# Start the host
dotnet run --project samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj

# In another terminal, pack the sample plugin
dotnet pack samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj -c Debug

# Drop it into the watched folder
mkdir -p packages
cp samples/Nuplane.Sample.Plugin/bin/Debug/Nuplane.Sample.Plugin.1.0.0.nupkg packages/
```

Then query `/catalog/plugins`. The `HelloPlugin` type shows up in the response, discovered live in the running process with no restart. The whole loop from file drop to discoverable type takes about a second.

---

Nuplane is a focused piece of infrastructure for a problem that comes up more often than .NET's standard tooling addresses. If you're building something that needs NuGet packages at runtime — plugins, tenant customizations, live-updateable feature modules — it's worth a look. The repository is at [github.com/valence-works/nuplane](https://github.com/valence-works/nuplane).
