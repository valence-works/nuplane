# Research: Automatic Load Mode Selection

## Decision: Use An Option-Level Automatic Selection Policy

**Decision**: Add a separate `LoadingOptions` policy for automatic load-mode selection instead of adding `PackageLoadMode.Auto`.

**Rationale**: Existing sessions, catalogs, and loaders already treat `PackageLoadMode` as the concrete runtime mode. Keeping `PackageLoadMode` limited to `Collectible` and `HostIntegrated` avoids ambiguous effective states and lowers migration risk for existing switch logic, validators, and catalog consumers.

**Alternatives considered**:
- Add `PackageLoadMode.Auto`: rejected because it could leak into effective load-state contracts and force every consumer to handle a non-runtime mode.
- Always evaluate advisors with no policy option: rejected because app authors need an explicit opt-out for diagnostics, migration, and compatibility testing.

## Decision: Default To Metadata-Aware Automatic Selection

**Decision**: The policy defaults to evaluating advisors, then falling back to `DefaultLoadMode` when no advisor requires host integration.

**Rationale**: Package-authored metadata is opt-in by the package author and solves the observed app-by-app configuration problem only if hosts benefit without adding a second opt-in. Packages without metadata keep the existing fallback behavior.

**Alternatives considered**:
- Default to explicit-only behavior: rejected because it would make package-authored metadata inert until every app opts in.
- Remove `DefaultLoadMode`: rejected because hosts still need a fallback for packages with no metadata.

## Decision: Package-Root `nuplane.json` Is The Only V1 Metadata Location

**Decision**: Probe only package-root `nuplane.json` for v1.

**Rationale**: The metadata is package-owned runtime policy that Nuplane can read from the extracted install path. Avoiding `build/` and `contentFiles/` keeps discovery deterministic and avoids importing build-time or content-file conventions into loading.

**Alternatives considered**:
- Probe `build/nuplane.json`: deferred as future compatibility work because `build/` carries NuGet build asset semantics that are not needed for runtime loading.
- Probe `contentFiles/any/any/nuplane.json`: rejected for v1 because content files imply consumer project behavior, not Nuplane runtime policy.

## Decision: Expose A Public Advisor Contract

**Decision**: Add a public `IPackageLoadModeAdvisor` contract in loading abstractions and register built-in advisors through the loading module.

**Rationale**: The spec explicitly forbids package-specific hard-coding while preferring an advisor model. A public contract lets hosts or future Nuplane modules add policy sources without changing core selector code. The built-in metadata advisor remains the only required v1 advisor.

**Alternatives considered**:
- Keep advisors internal: rejected because it would make future advisor extension require changes inside Nuplane loading.
- Use configuration-only metadata rules: rejected because the feature goal is package-authored declaration, not another host-maintained package list.

## Decision: Explicit Package Overrides Suppress Same-Package Advisor Results

**Decision**: Existing `PackageLoadModes` overrides have highest precedence for the matching package. Suppressed metadata is recorded as a diagnostic.

**Rationale**: App authors must remain authoritative for compatibility, rollback, and emergency mitigation. Diagnostics keep the suppression visible so package metadata problems can be fixed rather than hidden.

**Alternatives considered**:
- Let package metadata override app configuration: rejected because package-authored metadata must not remove host control over runtime lifetime and integration.
- Treat conflicts as fatal: rejected because explicit overrides are the established escape hatch and should remain operationally useful.

## Decision: `HostIntegrated` Metadata Is A Requirement; `Collectible` Metadata Is Preference-Only

**Decision**: Package-authored `HostIntegrated` metadata can require host integration. Package-authored `Collectible` metadata only expresses a lower-priority preference and never forces a graph down from a `HostIntegrated` default or another host-integrated requirement.

**Rationale**: Host integration is often required to avoid framework/default-context failures. A package author can safely declare that stronger requirement. Forcing collectible behavior would weaken a host's chosen integration policy and could reintroduce runtime resolution failures.

**Alternatives considered**:
- Allow `Collectible` metadata to force down from `HostIntegrated`: rejected because it lets package metadata reduce the host-selected integration level.
- Remove `Collectible` from v1 metadata: rejected because preference diagnostics can help explain that a package is isolation-friendly when the host fallback is collectible.

## Decision: Invalid Metadata Is Degraded And Ignored For Selection

**Decision**: Malformed, unsupported, unreadable, or oversized metadata does not crash reconciliation or loading. The advisor returns an invalid result that is ignored for selection and projected into diagnostics.

**Rationale**: Metadata quality should not make package reconciliation brittle. Existing fallback and explicit override behavior can continue while operators receive actionable diagnostics.

**Alternatives considered**:
- Fail graph loading on invalid metadata: rejected because a metadata typo should not make otherwise valid packages unavailable unless the host explicitly chooses fail-closed behavior in a future policy.
- Silently ignore invalid metadata: rejected because package authors and operators need to see why automatic selection did not apply.

## Decision: Expose Full Explanations Through `LoadingPackageDescriptor`

**Decision**: `LoadingPackageDescriptor` is the first public surface for full advisor explanations. Runtime sessions keep effective mode and minimal loading state.

**Rationale**: Loading descriptors already carry loading status, diagnostics, effective mode, context key, and scan guidance. They are the right operator-facing surface for "why" data without putting policy history into lower-level runtime session state.

**Alternatives considered**:
- Store explanations directly on `PackageLoadSession`: rejected because sessions are lower-level process state and should stay compact.
- Expose explanations only through package assembly catalog metadata: rejected because assembly consumers may not need full policy history.
- Add a separate query surface: deferred until there is evidence that loading descriptors cannot carry the explanation cleanly.
