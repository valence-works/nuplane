# Contract: Graph-Scoped Assembly Loading

## Component

`PackageGraphLoadContext`, `PackageLoader`, `LoadingCatalog`, and `IPackageAssemblyCatalog`.

## Purpose

Load each active graph generation into a single collectible assembly load context so root assemblies can bind to dependency assemblies while host-shared contracts remain shared.

## Input

- Active graph activation records.
- Active package descriptors with graph role metadata.
- Selected runtime assembly assets for every graph node.
- Shared assembly policy configuration.
- Host target framework override, when configured.

## Output

- Loaded graph sessions.
- Discoverable root assembly entries.
- Dependency support assembly entries.
- Graph-aware load state and bind diagnostics.

## Behavioral Contract

1. The loader MUST create one collectible load context per active graph generation.
2. The load context MUST index graph assemblies by simple name and full assembly identity before loading root assemblies.
3. The load context MUST resolve configured host-shared assemblies from the host/default context before probing graph package assemblies.
4. The load context MUST resolve non-shared dependency assemblies from the graph's selected support assemblies.
5. `IPackageAssemblyCatalog` MUST expose root/discoverable assemblies for feature discovery by default.
6. Dependency-only assemblies MUST remain available for binding and diagnostics but MUST NOT become independent discoverable package roots by default.
7. Packages that are both explicit roots and dependencies MUST retain both roles and remain discoverable.
8. Independent graph load contexts MUST allow different selected versions of the same dependency package to load side-by-side.
9. Graph load preparation MUST fail before publish when required native or runtime-specific assets are unsupported.
10. Load state MUST identify graph id, generation id, root package, dependency package, assembly path, and failure reason for load/bind failures.
11. Old graph generations MUST remain collectible when no host holds `Assembly`, `Type`, or other runtime references.

## Failure Contract

Load or bind failure MUST degrade load state for the affected root graph without deleting active package state. Load-preparation failure before publish MUST preserve last-known-good state when present. The diagnostic MUST distinguish install/path failures, unsupported native/runtime-specific assets, assembly load failures, shared assembly policy mismatches, and dependency bind failures.

## Test Contract

- A vertical-slice fixture configures only a root package, loads the resolved graph, and reflects root assembly metadata that requires a dependency assembly without `FileNotFoundException`.
- Root assembly referencing dependency assembly loads and reflects without `FileNotFoundException`.
- Host-shared contract assembly resolves from host context.
- Dependency package assembly is not returned as an independent feature root by default.
- Explicitly desired package that is also a dependency is discoverable as a root.
- Two unrelated graphs use separate collectible load contexts.
- Two independent graphs can load different versions of the same dependency package side-by-side.
- Unsupported required native/runtime-specific asset fails graph load preparation before publish.
- Replaced graph generation unloads after runtime references are released.
- Missing support assembly produces graph-aware bind diagnostic.
