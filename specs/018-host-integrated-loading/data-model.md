# Data Model: Host-Integrated Package Loading

## PackageLoadMode

Represents the configured package assembly lifetime and framework integration behavior.

**Fields**:
- `Collectible`: Existing unloadable/isolation-oriented mode for isolated or scan-only package scenarios.
- `HostIntegrated`: Application-lifetime framework integration mode for packages whose assemblies must be safe for host/framework code and visible to by-name assembly resolution.

**Validation rules**:
- Values must be one of the supported modes.
- Missing values use the configured default mode.
- Existing default remains `Collectible` unless explicitly changed.

## LoadingOptions

Represents module-level package loading configuration.

**Fields**:
- `Enabled`: Existing loading enablement flag.
- `DeactivationTimeout`: Existing collectible-context deactivation timeout.
- `ActiveStoreRoot`: Existing active store root validation boundary.
- `SharedAssemblies`: Existing shared assembly policy entries.
- `DefaultLoadMode`: New default mode for autoloaded packages.
- `Packages`: Optional package-specific load mode overrides keyed by package identity.

**Validation rules**:
- `DefaultLoadMode` must be supported.
- Package override identifiers must be non-empty and unique case-insensitively.
- Package override load modes must be supported.
- Shared assembly validation remains independent from load mode validation.

## PackageLoadModeOverride

Represents load mode configuration for one package.

**Fields**:
- `PackageId`: Package identifier to match against resolved package identity.
- `LoadMode`: Mode applied to that package when it is autoloaded.

**Relationships**:
- Belongs to `LoadingOptions`.
- Overrides `LoadingOptions.DefaultLoadMode` for matching package identities.

**Validation rules**:
- `PackageId` must be non-empty.
- At most one override may exist for a package ID using case-insensitive comparison.
- `LoadMode` must be supported.

## PackageLoadModeSelection

Represents the resolved load mode decision for a package during a reconciliation cycle.

**Fields**:
- `PackageId`: Package identity.
- `Version`: Resolved package version.
- `LoadMode`: Effective mode after applying default and package-specific overrides.
- `SelectionReason`: Whether the mode came from a default or package override.
- `GraphKey`: Active package graph key when the package is loaded with its dependency graph.

**Relationships**:
- Consumed by `PackageLoader` when creating load contexts.
- Recorded in load state and package assembly catalog metadata.

**Validation rules**:
- Selection is deterministic for the same package set and configuration.
- Package override wins over default mode.

## HostIntegratedAssemblyResolutionEntry

Represents a framework-visible mapping from assembly identity to an active host-integrated package assembly.

**Fields**:
- `AssemblySimpleName`: Simple assembly name.
- `AssemblyFullName`: Full assembly identity when available.
- `Version`: Assembly version.
- `AssemblyPath`: Durable path for the loaded assembly.
- `PackageId`: Owning package identifier.
- `PackageVersion`: Owning package version.
- `GraphKey`: Owning graph key.
- `Generation`: Active visibility generation.

**Relationships**:
- Created only for active host-integrated package assemblies.
- Used by the Nuplane-owned assembly resolution bridge.
- Referenced by diagnostics for success, conflict, ambiguity, and failure.

**Validation rules**:
- Active entries must not contain conflicting versions for the same simple name.
- Replacement entries become visible only after successful activation and visibility setup.
- Last-known-good entries remain active if replacement setup fails.

## PackageAssemblies Metadata

Represents catalog-visible metadata attached to active package assemblies.

**Fields**:
- `PackageId`: Existing package identifier.
- `Version`: Existing active package version.
- `Assemblies`: Existing loaded assembly instances.
- `AssemblyReferences`: Existing durable assembly references.
- `LoadMode`: Effective load mode for the package.
- `FrameworkIntegrationSafe`: Whether returned assemblies are safe for non-collectible host/framework code.

**Validation rules**:
- `FrameworkIntegrationSafe` is true for host-integrated package assemblies.
- `FrameworkIntegrationSafe` is false for collectible package assemblies.
- Metadata must match the effective load mode recorded for the active load session.

## ResolutionDiagnostic

Represents the observable outcome of a host-integrated assembly resolution decision.

**Fields**:
- `CorrelationId`: Reconciliation or loading cycle correlation identifier when available.
- `RequestedAssemblyName`: Assembly identity requested by framework code.
- `Outcome`: Success, not found, conflict, ambiguity, or inactive.
- `SelectedAssemblyPath`: Selected assembly path for successful resolution.
- `CandidateAssemblies`: Candidate identities considered for ambiguous or conflicting requests.
- `Message`: Actionable diagnostic text.

**Validation rules**:
- Failure diagnostics must identify the requested assembly name.
- Conflict diagnostics must identify the conflicting packages and assembly versions.
- Diagnostics must not include secrets or credentials.
