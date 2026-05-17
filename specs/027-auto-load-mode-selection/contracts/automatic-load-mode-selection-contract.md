# Contract: Automatic Load Mode Selection

## Scope

This contract defines configuration, advisor, package metadata, selection precedence, loading, diagnostics, and security behavior for automatic package load-mode selection.

## Configuration Contract

1. Loading configuration MUST expose an option-level load-mode selection policy.
2. The policy MUST support automatic advisor evaluation and explicit-only behavior.
3. The default policy MUST evaluate advisors and then fall back to `DefaultLoadMode`.
4. The effective runtime load mode MUST remain `Collectible` or `HostIntegrated`.
5. `PackageLoadMode.Auto` MUST NOT be introduced as an effective session/catalog mode for this feature.
6. Existing `DefaultLoadMode` and `PackageLoadModes` settings MUST continue to bind from configuration.
7. Package-specific `PackageLoadModes` overrides MUST remain highest-precedence per-package inputs.
8. Invalid policy values MUST fail `LoadingOptions` validation before loading begins.

## Advisor Contract

1. Nuplane MUST expose an `IPackageLoadModeAdvisor` contract from loading abstractions.
2. Advisors MUST evaluate a resolved package graph context after dependency graph resolution and before package graph loading.
3. Advisors MUST return bounded, deterministic, secret-safe results.
4. Advisors MUST NOT mutate package/store state.
5. Advisors MUST NOT perform network access during load-mode selection.
6. Advisors MUST NOT bypass source trust, package validation, package identity selection, or desired-state resolution.
7. The built-in metadata advisor MUST NOT hard-code package IDs or product-specific rules.

## Package Metadata Contract

1. Nuplane v1 package metadata MUST be read only from package-root `nuplane.json`.
2. Metadata MUST be read only from packages already installed through existing trusted source and integrity paths.
3. v1 metadata MUST support `schemaVersion`.
4. v1 metadata MUST support `loading.loadMode`.
5. v1 metadata MUST support `loading.scope`.
6. v1 metadata MUST support optional `loading.reason`.
7. `loading.loadMode` MUST accept `HostIntegrated` and `Collectible`.
8. `HostIntegrated` metadata MUST be treated as a requirement.
9. `Collectible` metadata MUST be treated only as a preference and MUST NOT force down from a `HostIntegrated` default or another host-integrated requirement.
10. `loading.scope` MUST support `DependencyClosure`.
11. `loading.scope` MAY support `PackageOnly`, but current graph loading MUST still produce one concrete graph mode.
12. Invalid metadata MUST be ignored for selection and reported as a degraded diagnostic.

## Selection Precedence Contract

1. The selector MUST evaluate package-specific app overrides before advisor results for the same package.
2. A same-package app override MUST suppress conflicting metadata and record `metadata-suppressed`.
3. Valid advisor requirements MUST be evaluated before fallback default mode.
4. If no override or valid advisor requirement applies, the package MUST use `DefaultLoadMode`.
5. If any effective package decision in a graph is `HostIntegrated`, the loadable dependency closure MUST load as `HostIntegrated`.
6. Packages promoted only by closure promotion MUST be identifiable with `dependency-closure`.
7. Metadata conflicts that cannot be represented exactly MUST resolve deterministically to `HostIntegrated` and record `metadata-conflict`.
8. The same graph packages, options, metadata, and advisors MUST produce the same effective graph mode on repeated runs.

## Loading Contract

1. `Collectible` graphs MUST preserve existing collectible loading behavior.
2. `HostIntegrated` graphs MUST preserve existing non-collectible, framework-safe host-integrated behavior.
3. Existing host-integrated assembly conflict validation MUST still run before visibility publication.
4. Failed host-integrated activation or visibility publication MUST preserve last-known-good visibility when available.
5. Packages with no loadable assemblies MUST continue to follow existing graph facade/support package behavior.

## Diagnostics Contract

1. `LoadingPackageDescriptor` MUST expose full advisor explanations first.
2. Runtime load sessions MUST keep only effective mode and minimal loading state.
3. Advisor explanations MUST include stable reason codes for `default`, `package-override`, `package-metadata`, `dependency-closure`, `metadata-invalid`, `metadata-suppressed`, and `metadata-conflict`.
4. Advisor explanations MUST identify package ID, package version, graph key or graph ID, requested metadata scope, advisor name, and final effective graph mode when available.
5. Package assembly catalog metadata MAY project a reduced explanation for assembly consumers.
6. Structured logs MUST report advisor evaluation, metadata discovery success, metadata parse failure, override suppression, conflict resolution, and final graph mode selection.
7. Diagnostics MUST not log secrets, feed credentials, full metadata payloads, or full exception stack traces at Information level.

## Documentation Contract

1. Documentation MUST show a complete package-root `nuplane.json` example.
2. Documentation MUST explain automatic selection policy and explicit-only opt-out.
3. Documentation MUST explain package override precedence over metadata.
4. Documentation MUST explain that existing `PackageLoadModes` overrides continue to work and can be removed gradually after package metadata ships.
5. Documentation MUST state that package metadata is trusted only as much as the package itself and does not bypass source/integrity validation.
