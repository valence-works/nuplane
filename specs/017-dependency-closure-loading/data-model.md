# Data Model: Dependency Closure Loading

## DesiredPackageRoot

Represents one package explicitly requested by configuration or another desired source.

**Fields**

- `PackageId`: NuGet package identifier.
- `RequestedVersionRange`: Original configured version range or latest marker.
- `RequestedFeedName`: Optional feed preference from the request.
- `UpdatePolicy`: Existing package update policy.
- `Source`: Desired source attribution.
- `TargetFramework`: Host target framework used for dependency and asset selection.

**Relationships**

- Anchors one `ResolvedPackageGraph`.
- Maps to one root `ResolvedPackageNode`.

## ResolvedPackageGraph

Deterministic dependency closure for one desired root or a compatible set of desired roots.

**Fields**

- `GraphId`: Stable identity derived from sorted root identities, selected node identities, dependency edges, target framework, and source decisions.
- `GenerationId`: Activation generation for this graph.
- `TargetFramework`: Framework used for dependency group and asset selection.
- `Roots`: Desired root package nodes.
- `Nodes`: All selected package nodes, sorted deterministically.
- `Edges`: Dependency relationships, sorted deterministically.
- `SourceDecisions`: Feed/local source decisions for each node.
- `CreatedAtUtc`: Graph resolution timestamp for diagnostics only; not part of deterministic identity.

**Rules**

- Same desired roots, selected nodes, edges, sources, and target framework produce the same `GraphId`.
- Graph is publishable only when every required node is resolved, acquired, validated, and installed.
- Active state is keyed by package id for this feature, so one active set cannot publish multiple versions of the same package id side-by-side. Independent roots that resolve incompatible versions of the same dependency package id fail with graph-conflict diagnostics until package id/version-keyed active state is introduced.
- Graph resolution fails if dependency metadata contains a cycle.

## ResolvedPackageNode

One package identity/version selected for a graph.

**Fields**

- `PackageId`
- `Version`
- `Role`: `Root`, `Dependency`, or `RootAndDependency`.
- `InstallPath`
- `SourceKind`: `RemoteFeed` or `LocalDirectory`.
- `SourceName`
- `PackageContentHash`: Existing or newly captured integrity/hash value where available.
- `RuntimeAssets`: Selected runtime assembly paths relative to install path.
- `DiscoverableAssets`: Runtime assemblies considered root feature discovery candidates.
- `SupportAssets`: Runtime assemblies available only for binding/support.

**Rules**

- Dependency-only nodes are not discoverable roots by default.
- Nodes with `RootAndDependency` remain discoverable because they were explicitly desired.
- A node may be referenced by multiple edges and roots.

## DependencyEdge

Relationship from one selected package node to another.

**Fields**

- `FromPackageId`
- `FromVersion`
- `ToPackageId`
- `RequestedVersionRange`
- `SelectedVersion`
- `DependencyGroupTargetFramework`
- `Optional`: Whether NuGet metadata marks the dependency optional, if available.

**Rules**

- Edges are part of graph identity.
- Unsatisfied non-optional edges fail graph resolution.
- Dependency cycles fail graph resolution and are captured in failure diagnostics.
- Duplicate edges are retained deterministically without selecting the same package node more than once.

## GraphActivationRecord

Persisted active-state record for one active graph generation.

**Fields**

- `GraphId`
- `GenerationId`
- `RootPackageIds`
- `NodePackageIds`
- `NodeVersionsByPackageId`: Selected node versions keyed by package id.
- `ActivatedAtUtc`
- `CorrelationId`
- `Status`: `Active`, `Stale`, `Failed`, or `Replaced`
- `Failure`: Optional `GraphResolutionFailure` or activation failure summary.

**Relationships**

- References active package descriptors by package id/version.
- Used by cleanup to decide whether installed packages remain referenced.
- Cleanup decisions must compare both package id and selected version so replaced graph generations do not retain stale dependency closures after a dependency version changes.

## ActivePackage Graph Metadata

Extension to active package read/persisted models.

**Fields**

- `GraphId`
- `GraphGenerationId`
- `PackageRole`
- `RootPackageIds`
- `DependencyOfPackageIds`
- `Discoverable`: Boolean root discovery marker.

**Rules**

- Durable active package models must not contain runtime `Assembly`, `Type`, or `AssemblyLoadContext` objects.
- Legacy compatibility models may map missing graph metadata to root/discoverable defaults.

## PackageGraphLoadSession

In-memory runtime state for one active graph generation.

**Fields**

- `GraphId`
- `GenerationId`
- `LoadContext`
- `LoadedAssembliesByPath`
- `LoadedAssembliesByName`
- `RootAssemblyEntries`
- `SupportAssemblyEntries`
- `LoadStatus`
- `Failures`
- `RequiredUnsupportedAssets`: Native or runtime-specific assets required by the graph but unsupported by Nuplane.

**Rules**

- Collectible and unloadable after hosts release runtime objects.
- Uses host-shared assembly policy before graph assembly probing.
- Graph assembly indexing skips unmanaged/native DLL candidates that are not managed assemblies; required unsupported native/runtime-specific assets are handled by load-preparation validation instead of causing best-effort assembly indexing to fail.
- Fails load preparation before publish when required native or runtime-specific assets are unsupported.
- In-memory only; never serialized.

## PackageAssemblyEntry

Host-facing assembly projection.

**Fields**

- `PackageId`
- `Version`
- `GraphId`
- `GenerationId`
- `AssemblyPath`
- `Assembly`
- `IsDiscoverableRoot`
- `IsSupportAssembly`

**Rules**

- `Assembly` is exposed only through in-process loading APIs.
- Flattening helpers should return discoverable root assemblies by default unless explicitly designed otherwise.

## GraphResolutionFailure

Diagnostic record for failures before or during graph activation/loading.

**Fields**

- `RootPackageId`
- `RootVersionRange`
- `PackageId`
- `RequestedVersionRange`
- `FailureStage`: `Metadata`, `VersionSelection`, `Acquisition`, `Validation`, `Install`, `Activation`, `Load`, or `Bind`
- `SourceName`
- `ReasonCode`
- `Message`
- `CorrelationId`
- `CyclePath`: Ordered package id/version path when failure is caused by a dependency cycle.
- `UnsupportedAssetPath`: Package-relative asset path when failure is caused by unsupported required native or runtime-specific assets.

**Rules**

- Failure records must identify the desired root affected by a dependency failure.
- Graph failures preserve LKG where available.
