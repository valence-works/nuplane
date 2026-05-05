# Feature Specification: Dependency Closure Loading

**Feature Branch**: `017-dependency-closure-loading`  
**Created**: 2026-05-05  
**Status**: Draft  
**Input**: User description: "Resolve NuGet package dependency closures for desired package roots and load each resolved package graph into a collectible AssemblyLoadContext so dependencies between runtime packages are visible while host-shared contracts remain shared."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Reconcile Dependency Closures (Priority: P1)

As an operator, when I configure one desired NuGet package root such as `Elsa.ServiceBus.MassTransit.RabbitMq`, Nuplane MUST resolve, acquire, install, and activate that package's NuGet dependency closure without requiring every transitive dependency to be listed manually in configuration.

**Why this priority**: This is the primary missing behavior. A runtime package cannot be treated as deployable configuration if the operator must reverse-engineer and pin all of its dependencies in `IncludePatterns`.

**Independent Test**: Configure a test feed with a root package that depends on a second package. Request only the root package. Run reconciliation and verify that both packages are installed, the root is marked as desired, the dependency is marked as dependency-only, and repeated reconciliation is idempotent.

**Acceptance Scenarios**:

1. **Given** a configured NuGet feed containing `Plugin.Root` version `1.0.0` with a dependency on `Plugin.Dependency` version `1.0.0`, **When** reconciliation runs for desired package `Plugin.Root [1.0.0]`, **Then** Nuplane installs and activates both packages.
2. **Given** a dependency package was installed only because a root package requires it, **When** active package state is read, **Then** the dependency is distinguishable from the explicitly desired root.
3. **Given** the same feed contents and desired package inputs are reconciled twice, **When** the second cycle completes, **Then** no package is reacquired or reactivated solely because it is part of the dependency graph.
4. **Given** a root package dependency cannot be resolved from the configured trusted feeds, **When** reconciliation runs, **Then** Nuplane records a root-level resolution failure, preserves the last-known-good active graph, and does not publish a partial graph.

---

### User Story 2 - Load Related Packages Together (Priority: P1)

As a host loading runtime package assemblies, I need all packages in one resolved dependency graph to be visible to each other at runtime while host-owned contracts remain shared from the host context.

**Why this priority**: Resolving and installing dependencies is insufficient if the root package assembly is loaded into an isolated context that cannot bind to dependency package assemblies. This is the failure mode observed when `Elsa.ServiceBus.MassTransit.RabbitMq` could not bind to `Elsa.ServiceBus.MassTransit`.

**Independent Test**: Install a root package whose assembly references a dependency package assembly and exposes an attribute or type that causes reflection to bind that dependency. Query package assemblies and run feature discovery. Verify no `FileNotFoundException` occurs and the dependency assembly is loaded from the same graph load context.

**Acceptance Scenarios**:

1. **Given** a root package assembly references a dependency package assembly, **When** the root package assembly is reflected by a host, **Then** dependency assembly resolution succeeds without the dependency being referenced by the host application.
2. **Given** a configured host-shared assembly such as a contract or abstraction assembly is requested by a package, **When** the package graph load context resolves that assembly, **Then** the assembly identity comes from the host context according to the shared assembly policy.
3. **Given** two unrelated root packages have independent dependency graphs, **When** assemblies are loaded, **Then** each graph has an independent collectible load context unless graph unification is required by a deterministic conflict policy.
4. **Given** a graph generation is replaced by a later generation, **When** hosts release runtime objects from the old generation, **Then** the old collectible load context can unload.

---

### User Story 3 - Discover Root Features Without Scanning Dependencies (Priority: P2)

As a host using Nuplane for feature discovery, I want dependency-only package assemblies available for binding but not treated as independent feature roots unless they were explicitly configured as desired packages.

**Why this priority**: Dependency packages often provide shared infrastructure and should not automatically become host-visible feature roots. Scanning every dependency as a plugin increases false positives and makes host semantics dependent on package implementation details.

**Independent Test**: Configure a root package with a dependency package that also contains public types. Query package assemblies intended for feature discovery and verify root assemblies are included as discoverable entries while dependency assemblies are available only for resolution/supporting metadata.

**Acceptance Scenarios**:

1. **Given** a dependency package exists only as part of a root package graph, **When** the host asks for package assemblies for feature discovery, **Then** the root package assemblies are returned as discoverable entries and dependency assemblies are not surfaced as independent plugin roots.
2. **Given** the dependency package is also explicitly configured as a desired package, **When** reconciliation runs, **Then** Nuplane may treat it as both a dependency and a desired root with deterministic graph ownership metadata.
3. **Given** load-state diagnostics are requested, **When** a dependency assembly failed to load, **Then** the diagnostic identifies the affected root graph and the dependency package that failed.

### Edge Cases

- A dependency version range has no satisfiable version in any configured feed.
- Two desired roots require incompatible versions of the same dependency.
- Two desired roots require the same dependency version from different feeds with different configured priority.
- A dependency package exists in the local package directory and the same package exists in a remote feed.
- A dependency package has framework-specific dependency groups that do not include the host target framework.
- A dependency package contains only `ref/` assets, only unsupported `lib/` assets, or no managed assemblies.
- A package contains native or runtime-specific assets that cannot be satisfied by the initial graph loading implementation.
- A dependency cycle or duplicate dependency edge appears in package metadata.
- A dependency package is removed from a feed after it was part of the last-known-good graph.
- A root package is removed from desired configuration while another root still depends on one of its dependencies.
- A stale active descriptor points to an old install path after packages are reinstalled under a different state root; implementation should compose with the stale-install-path fix from `fix/loading-catalog-missing-install-path`.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: A `PackageDependencyGraphResolver` component MUST resolve a complete dependency graph for each desired NuGet package root by reading NuGet package dependency metadata from configured package sources and by applying NuGet version range semantics for dependency edges.
- **FR-002**: The dependency graph resolver MUST select dependency versions deterministically using existing feed priority, package identity, version range, pre-release, and target framework rules already established for direct package requests.
- **FR-003**: The dependency graph resolver MUST use the host target framework when choosing NuGet dependency groups and package asset groups, with an explicit override path matching existing loading target-framework override behavior.
- **FR-004**: A `ResolvedPackageGraph` model MUST represent desired roots, dependency nodes, dependency edges, selected versions, source decisions, target framework compatibility, and graph identity/generation information.
- **FR-005**: Reconciliation middleware MUST convert desired package roots into resolved package graphs before acquisition and MUST acquire every package node required by the graph before publishing active state.
- **FR-006**: Active package state MUST persist whether a package node is an explicit desired root, dependency-only, or both, and MUST persist enough graph membership metadata for loading, diagnostics, cleanup, and idempotent reconciliation.
- **FR-007**: Reconciliation publish behavior MUST be transactional at the graph level: if any required node in a graph cannot be resolved, acquired, validated, or installed, the graph MUST NOT be partially published.
- **FR-008**: Package cleanup/orphan detection MUST account for dependency graph membership so a dependency package is retained while any active graph still requires it and is eligible for cleanup only when no active graph references it.
- **FR-009**: A `PackageGraphLoadContext` component MUST create one collectible `AssemblyLoadContext` per active package graph generation and MUST make all selected runtime assemblies in that graph available to each other through one graph-scoped resolution policy.
- **FR-010**: The graph load context MUST apply an explicit host-shared assembly policy before probing package graph assemblies so configured contract and host-owned abstraction assemblies resolve from the host context.
- **FR-011**: Package assembly selection MUST distinguish discoverable root assemblies from dependency-only support assemblies while preserving support assemblies for runtime binding.
- **FR-012**: `IPackageAssemblyCatalog` and load-state query surfaces MUST expose graph-aware diagnostics that identify the root package, dependency package, selected version, feed/source decision, install path, load context generation, and failure reason.
- **FR-013**: If dependency resolution introduces new configuration, every new options type MUST remain data-only and MUST be validated with `IValidateOptions<T>` plus startup fail-fast behavior through `ValidateOnStart()`. The default behavior MUST resolve dependencies automatically for remote NuGet package roots.
- **FR-014**: Directory-sourced `.nupkg` roots MUST continue to reconcile successfully. If dependency metadata from a local package requires packages that are not locally present, the graph resolver MUST either resolve them from configured trusted remote feeds or fail with a graph-level diagnostic; it MUST NOT silently ignore missing dependencies.
- **FR-015**: The implementation MUST provide unit, contract, and integration tests for dependency graph resolution, graph-level reconciliation, graph-scoped assembly loading, host-shared assembly resolution, failure/LKG behavior, and directory package regression coverage.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Dependency graph reconciliation MUST be deterministic and idempotent for repeated identical desired package inputs, feed contents, local directory contents, and target framework selection.
- **OSR-002**: Graph activation MUST preserve last-known-good behavior. A failed graph resolution, acquisition, validation, installation, or load preparation MUST leave the previously active graph generation intact when one exists.
- **OSR-003**: Dependency resolution MUST use only explicitly configured trusted package sources and existing credentials. Package metadata and package content MUST pass the same source and integrity validation applied to direct package acquisitions.
- **OSR-004**: Observability MUST include structured logs and metrics for graph resolution start/finish, selected versions, feed decisions, dependency conflicts, graph activation, graph load context creation, assembly bind failures, and graph unload attempts.
- **OSR-005**: Load-state health MUST distinguish graph resolution/acquisition failures from assembly load/bind failures and MUST identify the affected desired root package.
- **OSR-006**: Runtime object exposure MUST remain in-process only. Durable, serialized, or remote read models MUST NOT contain `Assembly`, `Type`, `AssemblyLoadContext`, or other unload-sensitive runtime objects.
- **OSR-007**: The implementation MUST include regression coverage for the observed scenario where a root package can be installed but reflection fails because a dependency assembly from a sibling package is not visible to the root assembly.

### Key Entities

- **Desired Package Root**: A package explicitly requested by configuration or another desired source. It owns feature discovery semantics and anchors one or more resolved graph generations.
- **Package Dependency Node**: A resolved package identity/version selected because it is either a desired root, a dependency of a desired root, or both.
- **Dependency Edge**: A relationship from one package node to another with the original NuGet dependency range and selected version decision.
- **Resolved Package Graph**: The deterministic graph produced from one or more desired roots, including nodes, edges, source decisions, target framework, compatibility status, and graph identity.
- **Graph Activation Record**: Persisted active-state metadata that identifies the active graph generation, root packages, dependency nodes, install paths, and last-known-good status.
- **Package Graph Load Context**: A collectible assembly load context that loads and resolves all runtime assemblies for one active graph generation while deferring host-shared assemblies to the host context.
- **Discoverable Assembly Entry**: A package assembly entry associated with an explicit desired root and intended for host feature discovery.
- **Support Assembly Entry**: A dependency assembly entry available to the graph load context for binding but not surfaced as an independent feature root by default.
- **Graph Resolution Failure**: A diagnostic record describing why a graph could not be resolved or activated, including root package, dependency edge, requested range, feed/source, and failure category.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A host can configure only `Elsa.ServiceBus.MassTransit.RabbitMq [3.8.0-preview,)`; Nuplane resolves and installs the required `Elsa.ServiceBus.MassTransit` dependency automatically without adding it as a separate desired package entry.
- **SC-002**: Reflection or feature discovery over the RabbitMQ package graph does not throw `FileNotFoundException` for the dependency assembly when the dependency package exists in the resolved graph.
- **SC-003**: Repeating reconciliation with unchanged package inputs and feed contents produces the same active graph identities and performs no unnecessary reacquire/reactivate work.
- **SC-004**: If a dependency cannot be resolved, the resulting diagnostic names the desired root, dependency package, requested version range, searched source(s), and failure reason, and the previous active graph remains available.
- **SC-005**: Dependency-only assemblies are available for runtime binding but are not returned as independent feature discovery roots unless also explicitly configured as desired roots.
- **SC-006**: Automated tests cover graph resolution, directory package behavior, graph-level LKG, graph-scoped assembly loading, host-shared assembly policy, dependency conflict failure, and the observed missing sibling dependency regression.

## Clarifications

### Session 2026-05-05

- Q: Should Nuplane load all packages into the default `AssemblyLoadContext`? -> A: No. Use graph-scoped collectible load contexts and keep an explicit host-shared assembly policy for contracts/abstractions.
- Q: Should dependency packages be manually listed in `IncludePatterns`? -> A: No. Operators should configure desired roots; Nuplane owns dependency closure resolution.
- Q: Should dependency-only packages be scanned as independent feature roots? -> A: No. They must be available for binding but not treated as root plugins unless explicitly desired.

## Assumptions

- Existing version range resolution behavior from `011-version-range-resolution` remains the basis for direct package roots and dependency version choices.
- Existing remote feed acquisition, local directory package acquisition, and source validation mechanisms remain the trusted acquisition path.
- The implementation can add NuGet dependency metadata reading where needed but should not shell out to `dotnet restore`.
- The current stale active install path fix is expected to land separately; this feature should not reintroduce absolute-path staleness when graph metadata changes.
- Native/runtime-specific asset support may be limited initially to existing Nuplane runtime asset selection behavior unless implementation research identifies a required extension.
