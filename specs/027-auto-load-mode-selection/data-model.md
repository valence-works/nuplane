# Data Model: Automatic Load Mode Selection

## PackageLoadModeSelectionPolicy

Represents the option-level policy that controls whether Nuplane evaluates load-mode advisors.

**Fields**:
- `Automatic`: Evaluate registered advisors, then fall back to `LoadingOptions.DefaultLoadMode`.
- `ExplicitOnly`: Ignore advisor results and use package-specific overrides plus `LoadingOptions.DefaultLoadMode`.

**Validation rules**:
- Value must be a supported policy.
- Missing value defaults to `Automatic`.
- Policy values are not effective load modes and must never be recorded as package session load modes.

## LoadingOptions

Represents module-level package loading configuration.

**Fields**:
- `Enabled`: Existing loading enablement flag.
- `DeactivationTimeout`: Existing collectible-context deactivation timeout.
- `ActiveStoreRoot`: Existing active store root validation boundary.
- `DefaultLoadMode`: Existing concrete fallback load mode.
- `PackageLoadModes`: Existing package-specific load mode overrides.
- `LoadModeSelectionPolicy`: New option-level policy controlling advisor evaluation.
- `SharedAssemblies`: Existing shared assembly policy entries.

**Validation rules**:
- `DefaultLoadMode` must remain `Collectible` or `HostIntegrated`.
- `LoadModeSelectionPolicy` must be supported.
- Package override validation remains case-insensitive and duplicate-safe.
- Options remain data-only and are validated through `IValidateOptions<LoadingOptions>`.

## IPackageLoadModeAdvisor

Represents an extensible policy source that can produce load-mode advice for a resolved package graph.

**Fields / members**:
- `Name`: Stable advisor name used in diagnostics.
- `EvaluateAsync(context, cancellationToken)`: Produces zero or more advisor results for the graph.

**Relationships**:
- Registered in DI by concrete implementation first, then exposed through the interface.
- Consumed by `PackageLoadModeSelector` when `LoadModeSelectionPolicy` is `Automatic`.
- The built-in implementation is `PackageMetadataLoadModeAdvisor`.

**Validation rules**:
- Advisor output must be deterministic for the same graph package identities, install paths, metadata files, and configuration.
- Advisor diagnostics must be bounded and secret-safe.
- Advisors must not perform network access or mutate package/store state.

## LoadModeAdvisorContext

Represents the resolved graph context passed to advisors.

**Fields**:
- `GraphKey`: Deterministic load graph key.
- `Packages`: Resolved packages in the graph, including identity, version, feed/source name, install path, and source name.
- `LoadModeSelectionPolicy`: Current policy for diagnostic context.
- `DefaultLoadMode`: Current fallback concrete mode.
- `PackageOverrides`: Package-specific app overrides available for suppression diagnostics.

**Validation rules**:
- Graph key must be non-empty.
- Packages must be non-null and ordered deterministically before advisor evaluation.
- Advisors may read package install paths but must not write to them.

## NuplanePackageMetadata

Represents package-authored metadata read from package-root `nuplane.json`.

**Fields**:
- `SchemaVersion`: Integer schema version; v1 is required for this feature.
- `Loading`: Optional loading metadata object.
- `Loading.LoadMode`: `HostIntegrated` or `Collectible`.
- `Loading.Scope`: `DependencyClosure` or `PackageOnly`.
- `Loading.Reason`: Optional human-readable explanation.

**Relationships**:
- Owned by a single installed package identity/version.
- Read only by `PackageMetadataLoadModeReader`.
- Converted into `LoadModeAdvisorResult` by `PackageMetadataLoadModeAdvisor`.

**Validation rules**:
- Metadata is read only from package-root `nuplane.json`.
- Unsupported schema versions, malformed JSON, unsupported load modes, unsupported scopes, missing required loading fields, and unbounded payloads produce invalid metadata diagnostics.
- `HostIntegrated` is treated as a requirement.
- `Collectible` is treated only as a preference.
- Metadata cannot change package identity, source selection, trust validation, or desired state.

## LoadModeAdvisorResult

Represents one advisor output for one package.

**Fields**:
- `AdvisorName`: Stable advisor identifier.
- `PackageId`: Declaring package ID.
- `Version`: Declaring package version.
- `RequestedLoadMode`: Requested or preferred concrete mode.
- `Scope`: `DependencyClosure` or `PackageOnly`.
- `ReasonCode`: Stable machine-readable reason such as `package-metadata` or `metadata-invalid`.
- `Reason`: Optional human-readable reason, bounded and secret-safe.
- `IsValid`: Whether the result can influence selection.
- `Diagnostic`: Optional invalid/suppressed/conflict explanation.

**Relationships**:
- Produced by advisors.
- Consumed by `PackageLoadModeSelector`.
- Projected into `LoadingPackageDescriptor` explanations.

**Validation rules**:
- Invalid results never control effective load mode.
- Results are ordered by advisor name, package ID, package version, scope, and reason code for deterministic selection.
- Human-readable reasons are truncated or rejected when over the configured/built-in bound.

## EffectivePackageLoadModeDecision

Represents the selected concrete mode for a package before graph-wide promotion is projected.

**Fields**:
- `PackageId`
- `Version`
- `LoadMode`: `Collectible` or `HostIntegrated`.
- `ReasonCode`: `default`, `package-override`, `package-metadata`, `metadata-suppressed`, `metadata-invalid`, or `metadata-conflict`.
- `AdvisorResults`: Advisor inputs considered for this package.
- `SuppressedResults`: Advisor inputs ignored because an explicit package override won.
- `GraphKey`

**Relationships**:
- Built by `PackageLoadModeSelector`.
- Used to compute `EffectiveGraphLoadModeDecision`.

**Validation rules**:
- Explicit package override wins for the matching package.
- Package-authored `Collectible` never forces down from `HostIntegrated`.
- Invalid metadata is represented in diagnostics and ignored for selection.

## EffectiveGraphLoadModeDecision

Represents the selected concrete load mode for the whole resolved package graph.

**Fields**:
- `GraphKey`
- `LoadMode`: `Collectible` or `HostIntegrated`.
- `PackageDecisions`: Effective package decisions before and after promotion.
- `PromotedPackages`: Packages promoted because another package required `HostIntegrated`.
- `ConflictDiagnostics`: Metadata conflicts resolved by deterministic safest-mode behavior.

**Relationships**:
- Consumed by `PackageLoader` before choosing graph load context type.
- Projected into each `LoadingPackageDescriptor` for the graph.

**Validation rules**:
- If any effective package decision is `HostIntegrated`, every loadable package in the graph is promoted to `HostIntegrated`.
- Conflicts that cannot be represented as requested resolve to `HostIntegrated`.
- Decision is deterministic for identical graph packages, metadata, options, and advisors.

## LoadModeDecisionDiagnostic

Represents secret-safe explanation data exposed first through `LoadingPackageDescriptor`.

**Fields**:
- `GraphKey`
- `EffectiveGraphLoadMode`
- `EffectivePackageLoadMode`
- `ReasonCodes`
- `DeclaringPackageId`
- `DeclaringPackageVersion`
- `RequestedScope`
- `AdvisorName`
- `Message`

**Validation rules**:
- Diagnostics must not include secrets, feed credentials, full metadata payloads, or full exception stack traces at Information level.
- Diagnostics must include enough package identity and reason-code data to explain default fallback, package override, package metadata, closure promotion, invalid metadata, suppressed metadata, and conflict handling.
