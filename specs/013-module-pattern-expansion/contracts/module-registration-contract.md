# Contract — Module Registration Surfaces

## Purpose
Define the supported public registration surfaces for optional Nuplane modules after the boundary refactor.

## Core prerequisite
- Consumers must register the core runtime separately through `services.AddNuplane(...)`.
- Module registration APIs must not hide or duplicate core runtime composition.

## Directory-source module contract
- Owning implementation project: `src/Nuplane.Sources.Directory`
- Required direct surface: `services.AddNuplaneDirectorySource(Action<DirectorySourceOptions> configure)`
- Required behavior:
  - Registers the directory desired-state source, trusted feed wiring, and directory observation trigger behavior from the module package.
  - Preserves `DirectorySourceOptions` ownership and module-owned hosted-service registration.
  - Does not require consumers to reach into `Nuplane` internals.

## Loading module contract
- Owning implementation project: `src/Nuplane.Loading`
- Required direct surface: a module-owned `IServiceCollection` extension for loading registration in the loading implementation package.
- Expected direct-surface behavior:
  - Registers loading options, validators, loader/unload services, and loading observer dispatch infrastructure through module registration services.
  - Works without requiring the fluent `NuplaneBuilder` surface.
  - Keeps builder-specific ergonomics out of the implementation package.

## Duplicate registration semantics
- If a consumer registers the same module through both a direct module API and a builder convenience API, the last registration wins.
- Re-registration must replace earlier module registration state deterministically.
- Re-registration must not leave duplicate hosted services, duplicate event dispatchers, duplicate observers, or conflicting options consumers in the final service graph.

## Validation and startup contract
- Module options remain plain data objects.
- Module-owned validators must implement `IValidateOptions<T>`.
- Runtime-required module options must be registered with `ValidateOnStart()`.

## Wrapper retirement contract
- Core compatibility wrappers may exist only while the replacement module-owned surface is being introduced.
- By feature completion, superseded core wrappers for module-specific registration must be removed.
- Documentation must identify the replacement module-owned surface before wrapper removal.