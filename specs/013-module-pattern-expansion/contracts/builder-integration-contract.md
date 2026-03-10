# Contract — Builder Integration Ownership

## Purpose
Define where fluent builder conveniences live and how they interact with module-owned registration services.

## Ownership rule
- Module-specific fluent APIs belong in module-owned builder integration packages, not in `src/Nuplane`.
- Builder integration packages may depend on core builder abstractions, but module implementation packages must remain usable through direct `IServiceCollection` registration alone.

## Loading builder contract
- Owning project: `src/Nuplane.Loading.Hosting`
- Supported builder surfaces:
  - `NuplaneBuilder.AutoloadPackages(...)`
  - `NuplaneBuilder.OnPackagesLoaded<T>()`
- Required behavior:
  - Delegate to shared loading registration services.
  - Preserve `ValidateOnStart()` and singleton-safe loading service registration.
  - Avoid duplicating loading-specific orchestration logic that belongs in `src/Nuplane.Loading`.

## Directory builder contract
- Owning project: `src/Nuplane.Sources.Directory.Hosting`
- Supported builder surfaces:
  - `NuplaneBuilder.AddDirectoryFeed(name, path, configure?)`
  - `NuplaneBuilder.AddDirectoryFeedsFromConfiguration(configuration)`
- **Status**: Implemented. Core wrappers (`NuplaneFeedBuilder.FromDirectory`) removed.
- Required behavior:
  - Delegate to shared directory module registration services.
  - Preserve directory watcher debounce, trust wiring, and module-owned options behavior.
  - Remove superseded core builder wrappers by the end of the feature.

## Delegation contract
- Builder conveniences may configure module options, but they must not own module runtime behavior.
- Builder conveniences and direct registration must converge on one shared registration implementation per module.
- If both paths are used, the last registration wins according to the module registration contract.

## Documentation contract
- Module documentation must name the builder integration package explicitly.
- Migration notes must tell consumers when a core builder API has moved and which module-owned package replaces it.
- **Status**: Complete. `docs/coding-conventions.md` (Module Ownership section), `README.md`, and `docs/roadmap.md` updated.