# Implementation Plan: Dependency Closure Loading

**Branch**: `017-dependency-closure-loading` | **Date**: 2026-05-05 | **Spec**: [spec.md](spec.md)  
**Input**: Feature specification from `/specs/017-dependency-closure-loading/spec.md`

## Summary

Nuplane must treat configured packages as desired roots, resolve each root's NuGet dependency closure, acquire every required package node transactionally, persist graph membership metadata, and load each active graph generation into one collectible assembly load context. The approach is to add graph-resolution models and services to runtime/reconciliation, extend persisted active state with root/dependency graph metadata, and replace per-package assembly binding with graph-scoped loading that keeps host-shared contracts in the host context while allowing independent root graphs to load different dependency versions side-by-side.

The first implementation milestone is a required vertical slice, not a model layer: configure only one root package, automatically acquire its dependency package, publish graph metadata, load both packages in one graph-scoped collectible load context, and reflect root assembly metadata that requires the dependency assembly without `FileNotFoundException`.

## Technical Context

**Language/Version**: C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`  
**Primary Dependencies**: Microsoft.Extensions.DependencyInjection/Options/Logging/Hosting, NuGet.Protocol and NuGet.Versioning already used by feed version resolution, System.Runtime.Loader, xUnit, NSubstitute  
**Storage**: File-backed Nuplane store state and package install directories under configured state/package roots; no database  
**Testing**: `dotnet test` with xUnit suites under `test/`, including runtime, integration, loading, directory source, and store tests  
**Target Platform**: Cross-platform .NET host applications on macOS, Linux, and Windows  
**Project Type**: Multi-project .NET package management/loading library  
**Performance Goals**: Resolve dependency metadata deterministically for typical graphs without repeated package acquisition/loading on unchanged graph inputs; keep graph assembly probing bounded to selected assemblies in active graph generations
**Constraints**: Preserve deterministic reconciliation, transactional store/LKG behavior, trusted source boundaries, query-first host APIs, collectible load contexts, in-process-only exposure of runtime objects, and module ownership boundaries; do not shell out to `dotnet restore`; directory packages must keep working
**Scale/Scope**: Typically 1-50 desired roots with small NuGet dependency graphs across 1-5 configured trusted feeds; implementation touches `Nuplane`, `Nuplane.Abstractions`, `Nuplane.Loading`, `Nuplane.Loading.Abstractions`, `Nuplane.Sources.Directory`, and related tests

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **Deterministic reconciliation**: PASS. The graph resolver produces sorted deterministic `ResolvedPackageGraph` results from desired roots, feed contents, dependency ranges, source decisions, and target framework selection. Repeated cycles with unchanged inputs preserve graph identity and avoid unnecessary reacquire/reactivate work.
- **Transactional store safety**: PASS. Graph resolution, cycle detection, acquisition, validation, installation, and load preparation happen before publish. A graph is activated only after every required node is ready; failures preserve last-known-good graph generation and do not publish partial graphs.
- **Source integrity**: PASS. Dependency metadata and content are read only from configured trusted package sources or local directory packages, using existing feed credentials and package validation paths. No implicit external source or `dotnet restore` source discovery is introduced.
- **Observability**: PASS. The design requires structured graph-resolution, feed-decision, cycle-failure, activation, load-context, native/runtime-asset failure, bind-failure, and unload logs/metrics with desired root and dependency package identifiers.
- **Test discipline**: PASS. The spec requires unit, contract, integration, and regression tests covering dependency graph resolution, directory behavior, LKG, graph-scoped assembly loading, host-shared policy, unsatisfiable dependency failure, side-by-side independent graph dependency versions, cycle diagnostics, unsupported native/runtime asset failure, and the observed missing sibling dependency failure.
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
│   ├── ActivePackageDescriptor.cs               # Persist compatible graph metadata
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
│   │       └── PackageResolutionResult.cs       # Extend with graph results/failures
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
│   ├── PackageLoader.cs                         # Load graph generations and prepare assets
│   ├── PackageUnloadCoordinator.cs              # Track graph generation unloads
│   ├── PackageAssemblyProvider.cs               # Serve assemblies from graph sessions
│   ├── AssemblyScanCandidateProjector.cs        # Root vs support assembly projection
│   ├── LoadingCatalog.cs                        # Graph-aware load-state
│   └── PackageAssemblyCatalog.cs                # Root discoverability + support binding
└── Nuplane.Sources.Directory/
    └── DirectoryNupkgDesiredSource.cs           # Preserve local root behavior; expose metadata when needed

test/
├── Nuplane.Runtime.Tests/
│   ├── Feeds/PackageDependencyGraphResolverTests.cs
│   ├── Feeds/PackageDependencyGraphCycleTests.cs
│   ├── Feeds/PackageDependencyGraphSideBySideVersionTests.cs
│   ├── Feeds/PackageDependencyGraphTrustPolicyTests.cs
│   ├── Feeds/PackageDependencyGraphTargetFrameworkTests.cs
│   └── Operational/ActivePackageGraphMetadataTests.cs
├── Nuplane.Store.Tests/
│   └── State/GraphActivationStateSerializationTests.cs
├── Nuplane.Integration.Tests/
│   ├── Reconciliation/DependencyClosureReconciliationTests.cs
│   ├── Reconciliation/DependencyClosureLkgTests.cs
│   ├── Reconciliation/DirectoryDependencyClosureRegressionTests.cs
│   └── Loading/RootAndDependencyDiscoveryTests.cs
├── Nuplane.Loading.Tests/
│   ├── PackageGraphLoadContextTests.cs
│   ├── PackageGraphSideBySideVersionLoadingTests.cs
│   ├── PackageGraphSharedAssemblyPolicyTests.cs
│   ├── PackageLoaderGraphRegressionTests.cs
│   ├── PackageGraphNativeAssetFailureTests.cs
│   ├── PackageGraphUnloadTests.cs
│   └── PackageAssemblyCatalogGraphTests.cs
└── Nuplane.Sources.Directory.Tests/
```

**Structure Decision**: Keep NuGet dependency closure logic in the existing runtime/feed area because NuGet.Protocol/Versioning support already lives there after version range resolution. Keep graph activation and persistence in `Nuplane` reconciliation/store code. Keep runtime assembly objects and graph-scoped load contexts in `Nuplane.Loading`, with public host contracts remaining in `Nuplane.Loading.Abstractions`.

## Delivery Stages

1. **Stage 0 - MVP vertical slice (P0, US1, US2, US3 minimal)**: Build the synthetic root/dependency fixture and make one normal reconciliation/loading path pass with only the root package configured. This stage must include dependency acquisition, graph activation metadata, graph-scoped assembly binding, and root/support assembly projection.
2. **Stage 1 - Graph resolution and persistence hardening (US1)**: Add broader graph models, dependency metadata resolution, cycle detection, graph-level package acquisition, active-state graph metadata, graph-aware cleanup, and failure/LKG diagnostics beyond the MVP path.
3. **Stage 2 - Graph-scoped loading hardening (US2)**: Replace per-package assembly context binding with one collectible load context per active graph generation, apply host-shared assembly policy first, support side-by-side independent graph dependency versions, fail unsupported required native/runtime-specific assets during load preparation, and produce graph-aware load state.
4. **Stage 3 - Discovery semantics and hardening (US3)**: Distinguish discoverable root assemblies from dependency-only support assemblies, preserve both roles for explicit-root dependency nodes, update catalog projections, add observability and health details, and validate directory/local package regressions.

## Post-Design Constitution Re-evaluation

- **Deterministic reconciliation**: PASS. `research.md` chooses deterministic dependency group selection, feed priority, cycle failure, side-by-side independent graph behavior, and graph identity rules; `data-model.md` defines graph identity, ordered nodes/edges, and failure records.
- **Transactional store safety**: PASS. `graph-reconciliation-contract.md` requires all-node acquisition before publish and LKG preservation on graph resolution, acquisition, validation, install, and load-preparation failures.
- **Source integrity**: PASS. `dependency-graph-resolution-contract.md` restricts metadata and content to configured trusted sources and existing credentials.
- **Observability**: PASS. Contracts and data model define graph-level diagnostics for resolution, cycle failure, activation, load preparation, bind failures, and unload.
- **Test discipline**: PASS. `quickstart.md` and `tasks.md` require unit, integration, contract, and regression tests before implementation tasks.
- **Decomposition discipline**: PASS. Tasks are grouped by independently testable stories and each task maps to one artifact or tightly coupled file group.
- **Options validation discipline**: PASS. No new options are required; any later option addition must use the validator topology in FR-013.

## Complexity Tracking

No constitution violations to justify. The graph-scoped load context is additional architecture, but it is the simpler viable model compared with default-context loading or cross-context sibling probing because it preserves unloadability, package graph isolation, and host-shared contract identity.
