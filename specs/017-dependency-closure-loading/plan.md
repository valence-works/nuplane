# Implementation Plan: Dependency Closure Loading

**Branch**: `017-dependency-closure-loading` | **Date**: 2026-05-05 | **Spec**: [spec.md](spec.md)  
**Input**: Feature specification from `/specs/017-dependency-closure-loading/spec.md`

## Summary

Nuplane must treat configured packages as desired roots, resolve each root's NuGet dependency closure, acquire every required package node transactionally, persist graph membership metadata, and load each active graph generation into one collectible assembly load context. The core approach is to add graph-resolution models and services to the runtime/reconciliation layer, extend persisted active state with root/dependency graph metadata, and replace per-package assembly load context resolution with graph-scoped loading that keeps host-shared contracts in the host context.

## Technical Context

**Language/Version**: C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`  
**Primary Dependencies**: Microsoft.Extensions.DependencyInjection/Options/Logging/Hosting, NuGet.Protocol and NuGet.Versioning already used by feed version resolution, System.Runtime.Loader, xUnit, NSubstitute  
**Storage**: File-backed Nuplane store state and package install directories under configured state/package roots; no database  
**Testing**: `dotnet test` with xUnit suites under `test/`, including runtime, integration, loading, directory source, and store tests  
**Target Platform**: Cross-platform .NET host applications on macOS, Linux, and Windows  
**Project Type**: Multi-project .NET package management/loading library  
**Performance Goals**: Resolve dependency metadata with bounded feed calls and caching where practical; avoid repeated acquisition/loading on unchanged graph inputs; keep graph assembly probing bounded to active graph assemblies  
**Constraints**: Preserve deterministic reconciliation, transactional store/LKG behavior, trusted source boundaries, query-first host APIs, collectible load contexts, and in-process-only exposure of runtime objects; do not shell out to `dotnet restore`; directory packages must keep working  
**Scale/Scope**: Typically 1-50 desired roots with small NuGet dependency graphs across 1-5 feeds; implementation touches `Nuplane`, `Nuplane.Abstractions`, `Nuplane.Loading`, `Nuplane.Loading.Abstractions`, `Nuplane.Sources.Directory`, and related tests

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **Deterministic reconciliation**: PASS. The graph resolver produces a sorted, deterministic `ResolvedPackageGraph` from desired roots, feed contents, dependency ranges, and target framework selection. Repeated cycles with unchanged inputs preserve graph identity and avoid unnecessary reacquire/reactivate work.
- **Transactional store safety**: PASS. Graph resolution and acquisition happen before publish. A graph is activated only after every required node is resolved, acquired, validated, and installed. Failures preserve last-known-good graph generation and do not publish partial graphs.
- **Source integrity**: PASS. Dependency metadata and content are read only from configured trusted package sources or local directory packages, using existing feed credentials and package validation paths. No implicit external source or `dotnet restore` source discovery is introduced.
- **Observability**: PASS. The plan requires structured graph-resolution, feed-decision, activation, load-context, bind-failure, and unload logs/metrics with desired root and dependency package identifiers.
- **Test discipline**: PASS. The spec requires unit, contract, integration, and regression tests covering dependency graph resolution, directory behavior, LKG, graph-scoped assembly loading, host-shared policy, conflict failure, and the observed missing sibling dependency failure.
- **Decomposition discipline**: PASS. Work decomposes into graph models/resolver, reconciliation graph activation, store persistence, loading graph context, catalog projection, observability, and focused tests. Mechanisms and drivers are separated.
- **Options validation discipline**: PASS. No new required options are planned. If implementation adds options, FR-013 requires data-only options validated by `IValidateOptions<T>` and `ValidateOnStart()`.

## Project Structure

### Documentation (this feature)

```text
specs/017-dependency-closure-loading/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── dependency-graph-resolution-contract.md
│   ├── graph-reconciliation-contract.md
│   └── graph-loading-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane.Abstractions/
│   ├── ActivePackage.cs                         # Extend with root/dependency graph metadata
│   ├── ActivePackageDescriptor.cs               # Persist compatibility graph metadata
│   └── ResolvedPackage.cs                       # Preserve resolved package identity/install data
├── Nuplane/
│   ├── Feeds/
│   │   ├── PackageDependencyGraphResolver.cs    # NEW graph resolver using NuGet metadata
│   │   ├── IPackageDependencyGraphResolver.cs   # NEW internal/runtime contract
│   │   └── Versioning/                          # Reuse existing version range/feed enumeration behavior
│   ├── Reconciliation/
│   │   ├── Middleware/PackageResolutionMiddleware.cs
│   │   ├── PackageApplyExecutor.cs
│   │   └── Models/
│   │       ├── ResolvedPackageGraph.cs          # NEW graph model
│   │       ├── ResolvedPackageNode.cs           # NEW node model
│   │       ├── DependencyEdge.cs                # NEW edge model
│   │       └── PackageResolutionResult.cs       # Extend with graph results
│   ├── Operational/ActivePackageCatalogMapper.cs
│   └── Store/
│       ├── State/StoreStateRecord.cs            # Persist graph activation records
│       └── Cleanup/PackageCleanupService.cs     # Respect graph references
├── Nuplane.Loading.Abstractions/
│   ├── PackageAssemblyReference.cs              # Extend with graph/discoverability metadata
│   ├── PackageLoadState.cs                      # Extend with graph diagnostics
│   └── IPackageAssemblyCatalog.cs               # Preserve host-facing surface
├── Nuplane.Loading/
│   ├── PackageGraphLoadContext.cs               # NEW graph-scoped collectible ALC
│   ├── PackageAssemblyLoadContext.cs            # Replace or wrap per-assembly behavior
│   ├── PackageLoader.cs                         # Load graph generations
│   ├── AssemblyScanCandidateProjector.cs        # Root vs support assembly projection
│   ├── LoadingCatalog.cs                        # Graph-aware load-state
│   └── PackageAssemblyCatalog.cs                # Root discoverability + support binding
└── Nuplane.Sources.Directory/
    └── DirectoryNupkgDesiredSource.cs           # Preserve local root behavior; expose metadata when needed

test/
├── Nuplane.Runtime.Tests/
│   ├── Feeds/PackageDependencyGraphResolverTests.cs
│   ├── Reconciliation/GraphPackageResolutionTests.cs
│   └── Store/GraphActivationStateTests.cs
├── Nuplane.Integration.Tests/
│   ├── Reconciliation/DependencyClosureReconciliationTests.cs
│   └── Reconciliation/DirectoryDependencyClosureRegressionTests.cs
├── Nuplane.Loading.Tests/
│   ├── PackageGraphLoadContextTests.cs
│   ├── PackageAssemblyCatalogGraphTests.cs
│   └── PackageLoaderGraphRegressionTests.cs
└── Nuplane.Sources.Directory.Tests/
    └── DirectoryNupkgDependencyMetadataTests.cs
```

**Structure Decision**: Keep NuGet dependency closure logic in the existing runtime/feed area because NuGet.Protocol/Versioning support already lives there after version range resolution. Keep graph activation and persistence in `Nuplane` reconciliation/store code. Keep runtime assembly objects and graph-scoped load contexts in `Nuplane.Loading`, with public host contracts remaining in `Nuplane.Loading.Abstractions`.

## Delivery Stages

1. **Stage 1 - Graph resolution and persistence (US1)**: Add graph models, dependency metadata resolution, graph-level package acquisition, active-state graph metadata, graph-aware cleanup, and failure/LKG diagnostics.
2. **Stage 2 - Graph-scoped loading (US2)**: Replace per-package assembly context binding with one collectible load context per active graph generation, apply host-shared assembly policy first, and produce graph-aware load state.
3. **Stage 3 - Discovery semantics and hardening (US3)**: Distinguish discoverable root assemblies from dependency-only support assemblies, update catalog projections, add observability and health details, and validate directory/local package regressions.

## Post-Design Constitution Re-evaluation

- **Deterministic reconciliation**: PASS. `research.md` chooses deterministic dependency group selection, feed priority, and graph identity rules; `data-model.md` defines graph identity and ordered nodes/edges.
- **Transactional store safety**: PASS. `graph-reconciliation-contract.md` requires all-node acquisition before publish and LKG preservation on graph failure.
- **Source integrity**: PASS. `dependency-graph-resolution-contract.md` restricts metadata and content to configured trusted sources and existing credentials.
- **Observability**: PASS. Contracts and data model define graph-level diagnostics for resolution, activation, load, bind, and unload.
- **Test discipline**: PASS. `quickstart.md` and `tasks.md` require unit, integration, contract, and regression tests before implementation tasks.
- **Decomposition discipline**: PASS. Tasks are grouped by independently testable stories and each task maps to one artifact or tightly coupled file group.
- **Options validation discipline**: PASS. No new options are required; any later option addition must use the validator topology in FR-013.

## Complexity Tracking

No constitution violations to justify. The graph-scoped load context is additional architecture, but it is the simpler viable model compared with default-context loading or cross-context sibling probing because it preserves unloadability and package graph isolation.
