# Feature Specification: Automatic Load Mode Selection

**Feature Branch**: `027-auto-load-mode-selection`  
**Created**: 2026-05-14  
**Status**: Draft  
**Input**: User description: "Create a Speckit specification for smarter automatic package load-mode selection using package-declared Nuplane metadata, advisor precedence, explicit user overrides, dependency-closure promotion, diagnostics, backwards compatibility, failure handling, trust considerations, migration guidance, and regression coverage for the Quartz SQLite provider metadata scenario."

## Problem

Nuplane supports `Collectible` and `HostIntegrated` package load modes. `Collectible` is the default isolated/unloadable path. `HostIntegrated` is required when packages participate in framework/default assembly load context behavior, assembly-qualified type resolution, application-lifetime services, schedulers, data providers, migrations, endpoints, or similar host-integrated patterns.

Today app authors must know which package roots require `HostIntegrated` and configure `Loading:PackageLoadModes` manually. Once any package in a resolved graph is configured as `HostIntegrated`, Nuplane promotes the whole dependency closure, which is correct. The brittle part is that every application must rediscover the package-specific load-mode requirement.

An observed Quartz SQLite graph failed under `DefaultLoadMode=Collectible` when provider metadata later tried to resolve an assembly-qualified type name for `Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite`. The same graph worked when relevant roots were explicitly configured as `HostIntegrated`, because closure promotion made the framework-visible graph available. Nuplane should let packages declare this requirement once, then make load mode a deterministic policy decision during graph loading.

## Clarifications

### Session 2026-05-17

- Q: Should automatic selection be exposed as a new public `PackageLoadMode.Auto` value or as a separate policy while effective load modes remain concrete? → A: Use a separate option-level automatic selection policy; effective `PackageLoadMode` values remain limited to `Collectible` and `HostIntegrated`.
- Q: Where should v1 package-authored Nuplane metadata live inside the package? → A: Probe only package-root `nuplane.json` in v1.
- Q: Which public diagnostic surface should carry full advisor explanations first? → A: Expose full advisor explanations first through `LoadingPackageDescriptor`; runtime sessions keep only effective mode and minimal state.
- Q: Should package-authored `Collectible` metadata be able to force a graph down from a host-configured `HostIntegrated` default? → A: No. `Collectible` metadata is only a preference; it never forces a graph down from a `HostIntegrated` default or another host-integrated requirement.

## Goals

- Let package authors declare Nuplane load-mode requirements once in package metadata.
- Let app authors get correct load-mode behavior automatically without hard-coding package IDs into Nuplane core or each host application.
- Preserve explicit app overrides as authoritative inputs.
- Preserve isolated/collectible loading for graphs that do not require host integration.
- Promote a dependency closure to `HostIntegrated` when any effective advisor result requires graph-wide host integration.
- Expose secret-safe diagnostics that explain the selected effective graph mode and the reasons that contributed to it.
- Keep the feature extensible for future advisors while the first advisor focuses on package-declared metadata.

## Non-Goals

- Hard-coding Elsa, Quartz, EF Core, SQLite, provider package IDs, or any other package-specific knowledge into Nuplane core.
- Inferring every possible framework integration pattern from package contents in the first implementation.
- Replacing existing source trust, package validation, dependency graph resolution, or host-integrated assembly resolution behavior.
- Allowing package-authored metadata to bypass explicit app configuration, source policy, integrity validation, or package trust decisions.
- Introducing side-by-side graph loading semantics beyond the dependency-closure behavior already supported by Nuplane.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Package Authors Declare Load Requirements Once (Priority: P1)

As a package author, I want my Nuplane-compatible package to declare that it requires host-integrated dependency-closure loading, so every Nuplane host can select the correct load mode without duplicating package-specific app configuration.

**Why this priority**: This removes the repeated app-by-app trivia that caused the observed failure while keeping package knowledge with the package that owns the runtime integration requirement.

**Independent Test**: Build a synthetic package graph where the root package contains valid Nuplane metadata declaring `HostIntegrated` with dependency-closure scope. Configure loading with no package-specific overrides, reconcile and load the graph, and verify every loadable package in the graph is loaded as `HostIntegrated` with a diagnostic reason pointing to package metadata.

**Acceptance Scenarios**:

1. **Given** a resolved package graph whose root package declares `HostIntegrated` with dependency-closure scope in Nuplane metadata, **When** Nuplane evaluates load mode for the graph, **Then** the effective graph load mode is `HostIntegrated` without app-specific package override configuration.
2. **Given** a resolved package graph whose packages have no Nuplane metadata and no package-specific overrides, **When** Nuplane evaluates load mode, **Then** the graph uses the configured default/fallback behavior.
3. **Given** a generic provider-style package graph where a root package declares a host-integrated dependency-closure requirement, **When** the graph is loaded with automatic selection enabled, **Then** the effective graph mode is `HostIntegrated` and framework/default-context resolution scenarios can be satisfied.

---

### User Story 2 - App Authors Override Explicitly (Priority: P1)

As an app author, I want explicit load-mode configuration to remain authoritative so I can force host integration, force isolation where acceptable, or work around a package metadata problem without waiting for a package update.

**Why this priority**: Automatic policy must not remove the host operator's control over runtime lifetime and integration tradeoffs.

**Independent Test**: Configure package-specific load-mode overrides that conflict with package metadata and verify the explicit override is the selected package decision before graph promotion is computed.

**Acceptance Scenarios**:

1. **Given** a package has no Nuplane metadata and app configuration explicitly sets that package to `HostIntegrated`, **When** Nuplane evaluates the graph, **Then** the dependency closure loads as `HostIntegrated`, matching today's behavior.
2. **Given** package metadata recommends or requests `Collectible` but app configuration explicitly sets the package to `HostIntegrated`, **When** Nuplane evaluates the graph, **Then** the explicit app override wins and the graph loads as `HostIntegrated`.
3. **Given** package metadata requests `HostIntegrated` for a package but app configuration explicitly sets that same package to `Collectible`, **When** no other graph member requires host integration, **Then** the package-specific app override wins and Nuplane records a diagnostic that package metadata was suppressed by explicit configuration.
4. **Given** one graph member is explicitly set to `Collectible` but another graph member effectively requires `HostIntegrated`, **When** Nuplane evaluates graph promotion, **Then** the graph loads as `HostIntegrated` and diagnostics identify both the explicit collectible input and the dependency-closure promotion reason.

---

### User Story 3 - Keep Collectible Loading As The Safe Default Path (Priority: P2)

As an app author running isolated plugins or scan-only packages, I want packages with no host-integration requirement to remain collectible so unloadability and isolation are not weakened unnecessarily.

**Why this priority**: Nuplane must preserve existing isolated plugin scenarios and avoid silently making all packages application-lifetime packages.

**Independent Test**: Reconcile and load a graph with no package metadata and no explicit override under the existing collectible fallback configuration; verify the graph remains `Collectible` and no host-integrated resolution entries are published.

**Acceptance Scenarios**:

1. **Given** a graph has no explicit override and no advisor requires host integration, **When** load mode selection completes, **Then** the graph uses the configured fallback load mode, with `Collectible` preserving existing default behavior.
2. **Given** a graph remains `Collectible`, **When** loading succeeds, **Then** Nuplane does not publish host-integrated assembly resolution entries for that graph.
3. **Given** an app opts out of automatic advisor selection through configuration, **When** packages include Nuplane metadata, **Then** Nuplane ignores advisor decisions and uses explicit package overrides plus fallback behavior while recording that automatic selection is disabled.

---

### User Story 4 - Explain Load Mode Decisions (Priority: P2)

As an operator, I want the active/loading catalog and logs to explain why a graph was loaded as `HostIntegrated` or `Collectible`, so I can diagnose package metadata, explicit overrides, dependency-closure promotion, conflicts, and invalid metadata without inspecting loader internals.

**Why this priority**: Automatic policy increases convenience only if the resulting decisions are transparent and deterministic.

**Independent Test**: Load graphs covering default fallback, package override, package metadata, dependency-closure promotion, invalid metadata, and conflicting metadata; verify queryable loading state and logs expose stable reason codes and package identities.

**Acceptance Scenarios**:

1. **Given** graph mode is selected because of package metadata, **When** loading catalog data is queried, **Then** the descriptor or diagnostic explains `package-metadata`, the declaring package, the requested scope, and the resulting effective graph mode.
2. **Given** graph mode is selected because one package caused dependency-closure promotion, **When** loading state is queried, **Then** each promoted package can be distinguished from the package that originally required `HostIntegrated`.
3. **Given** invalid metadata is encountered, **When** reconciliation and loading continue, **Then** diagnostics include the invalid metadata reason without crashing reconciliation.
4. **Given** conflicting metadata appears in one graph, **When** load mode selection completes, **Then** Nuplane chooses a deterministic result and emits a clear conflict diagnostic.

### Edge Cases

- A package metadata file is missing, empty, malformed JSON, too large, or encoded in an unsupported way.
- A metadata file contains an unsupported schema version, unknown load mode, unknown scope, missing required fields, or conflicting duplicated declarations.
- Multiple packages in one graph declare conflicting requirements, such as one requiring `HostIntegrated` and another recommending `Collectible`.
- A package-specific app override conflicts with package metadata on the same package.
- A package-specific app override on one graph member conflicts with graph-wide promotion caused by another graph member.
- A package declares dependency-closure host integration but has no loadable assemblies itself while another package in the graph is loadable.
- A package declares metadata from a source that has not yet passed existing source trust, integrity, or lock policy validation.
- A graph is replaced by a later generation whose metadata changes the effective load mode.
- Automatic selection is disabled or unavailable while package metadata exists.
- Package metadata declares `HostIntegrated` but host-integrated assembly visibility validation fails because of an assembly conflict.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `LoadingOptions` MUST expose a separate automatic load-mode selection policy that can be enabled or disabled by app configuration; the default policy SHOULD evaluate package-declared metadata and fall back to the configured default load mode when no advisor requires another mode.
- **FR-002**: The automatic selection policy MUST preserve the existing package-specific `PackageLoadModes` override collection as the highest-precedence per-package input.
- **FR-003**: The effective runtime load mode recorded for sessions, assembly catalogs, and loading catalogs MUST remain concrete: `Collectible` or `HostIntegrated`; `PackageLoadMode.Auto` MUST NOT be introduced as a final effective load mode for this feature.
- **FR-004**: Nuplane MUST introduce a load-mode advisor model that evaluates a resolved package graph after dependency graph resolution and before package graph loading.
- **FR-005**: The first built-in advisor MUST read explicit Nuplane package metadata from installed/resolved package contents and MUST NOT hard-code package IDs or product-specific rules.
- **FR-006**: Nuplane package metadata v1 MUST use a package-root `nuplane.json` file as the only probed metadata location, because it is package-owned runtime metadata that Nuplane can discover from the extracted package without invoking build or content-file conventions.
- **FR-007**: The v1 metadata schema MUST support at least `loading.loadMode`, `loading.scope`, and `loading.reason`.
- **FR-008**: The v1 metadata schema MUST support `loadMode` values of `HostIntegrated` and `Collectible`, with `HostIntegrated` treated as a requirement and `Collectible` treated only as a lower-priority preference that never forces a graph down from a `HostIntegrated` default or another host-integrated requirement unless made explicit by app configuration.
- **FR-009**: The v1 metadata schema MUST support a `DependencyClosure` scope that applies a load-mode requirement to every loadable package in the resolved graph containing the declaring package.
- **FR-010**: The v1 metadata schema MAY support a `PackageOnly` scope for package-local recommendations, but graph loading MUST still use a single effective graph mode when the current loader cannot mix modes inside one graph.
- **FR-011**: The load-mode selector MUST evaluate inputs in deterministic precedence order: package-specific app override for the declaring package, advisor requirements, default/fallback load mode, then dependency-closure promotion.
- **FR-012**: If any effective package decision in a resolved graph is `HostIntegrated`, the loader MUST promote the whole loadable dependency closure to `HostIntegrated`, preserving the graph promotion semantics already used for explicit overrides.
- **FR-013**: If metadata conflicts cannot be represented as requested in one graph, Nuplane MUST prefer the deterministic mode least likely to cause runtime framework resolution failure, which is `HostIntegrated`, and MUST emit a conflict diagnostic naming all conflicting declarations.
- **FR-014**: If a package-specific app override suppresses metadata on the same package, Nuplane MUST use the app override and record a diagnostic that identifies the suppressed metadata requirement.
- **FR-015**: Invalid or unreadable package metadata MUST NOT crash reconciliation or package loading; Nuplane MUST ignore the invalid advisor result for selection purposes and record a degraded diagnostic tied to the affected package.
- **FR-016**: Package metadata MUST be evaluated only after the package has been resolved and installed through existing trusted source and integrity paths.
- **FR-017**: Package metadata MUST NOT be allowed to expand source access, bypass package trust, alter desired package identity/version selection, or grant additional host permissions beyond influencing load-mode selection.
- **FR-018**: `LoadingPackageDescriptor` MUST expose the effective load mode and full advisor explanation first, including stable reason codes such as `default`, `package-override`, `package-metadata`, `dependency-closure`, `metadata-invalid`, `metadata-suppressed`, and `metadata-conflict`; package assembly catalog metadata MAY project a reduced explanation when useful for assembly consumers.
- **FR-019**: The `LoadingPackageDescriptor` advisor explanation MUST identify the declaring package ID/version, requested metadata scope, selected graph key or graph ID, and final effective graph mode when those values are available.
- **FR-020**: Structured logs MUST be emitted for advisor evaluation, metadata discovery success, metadata parse failure, override suppression, conflict resolution, and final graph mode selection without logging secrets, feed credentials, or unbounded metadata payloads.
- **FR-021**: The feature MUST include migration guidance explaining that existing `PackageLoadModes` overrides continue to work and can be removed gradually once packages ship trusted Nuplane metadata.
- **FR-022**: Documentation MUST include authoring guidance for package metadata, including the canonical `nuplane.json` location, schema examples, allowed values, dependency-closure scope, and the security/trust model.
- **FR-023**: The implementation MUST include regression coverage modeling the Quartz SQLite class of failure as a generic package graph whose root declares `HostIntegrated` dependency-closure metadata; the test MUST assert the effective graph mode becomes `HostIntegrated` without app-specific package overrides.
- **FR-024**: Tests MUST cover no metadata/no override fallback, explicit `HostIntegrated` override, package metadata host-integration promotion, explicit app override winning over metadata, conflicting metadata, invalid metadata, automatic selection disabled, and observability reason codes.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation and loading flows MUST remain deterministic and idempotent for repeated identical package graphs, package metadata, and app configuration.
- **OSR-002**: Load-mode selection changes MUST preserve existing transactional and last-known-good behavior; a failed load or failed host-integrated visibility publication MUST leave the previous active graph available when one exists.
- **OSR-003**: Source trust and package validation MUST remain unchanged; package-authored metadata is trusted only to the same degree as the package that contains it and only after the package passes existing validation.
- **OSR-004**: Observability MUST include structured diagnostics and catalog-visible explanations for advisor results, override precedence, dependency-closure promotion, metadata invalidity, metadata conflicts, and final graph mode.
- **OSR-005**: Automated tests MUST include unit coverage for advisor parsing/precedence, selector conflict handling, graph promotion, diagnostics, and integration-style loader coverage for metadata-driven `HostIntegrated` graph loading.

### Key Entities *(include if feature involves data)*

- **Automatic Load Mode Selection Policy**: App configuration that decides whether Nuplane evaluates advisors before falling back to the configured default load mode.
- **Package Load Mode Advisor**: A deterministic source of load-mode recommendations for packages or graphs. The first built-in advisor reads package-declared Nuplane metadata.
- **Nuplane Package Metadata**: Package-authored metadata stored in package-root `nuplane.json` that can declare loading requirements, scope, and a human-readable reason.
- **Load Mode Advisor Result**: A structured result containing package identity, requested load mode, scope, source, reason code, optional human reason, and validity status.
- **Effective Package Load Mode Decision**: The selected concrete mode for one package after applying explicit app override, valid advisor results, and fallback behavior.
- **Effective Graph Load Mode Decision**: The selected concrete mode for the whole resolved graph after dependency-closure promotion.
- **Load Mode Decision Diagnostic**: Secret-safe diagnostic data exposed first through `LoadingPackageDescriptor` that explains inputs, precedence, conflicts, invalid metadata, suppressed metadata, and final selection while runtime sessions keep only effective mode and minimal loading state.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In acceptance tests, 100% of graphs with no metadata and no package-specific override use the configured default/fallback load mode.
- **SC-002**: In acceptance tests, an explicit package override to `HostIntegrated` promotes the whole dependency closure exactly as current behavior does.
- **SC-003**: In acceptance tests, a package-root `nuplane.json` declaring `HostIntegrated` with dependency-closure scope causes the graph to load as `HostIntegrated` without app-specific package override configuration.
- **SC-004**: In acceptance tests, explicit app overrides win over conflicting metadata on the same package, and the suppressed metadata is visible in diagnostics.
- **SC-005**: In conflict tests, multiple incompatible metadata declarations in one graph produce the same deterministic effective mode and the same stable reason codes on every run.
- **SC-006**: In invalid-metadata tests, reconciliation and loading do not crash, the invalid metadata does not control selection, and a clear diagnostic identifies the affected package.
- **SC-007**: The generic Quartz SQLite regression model loads the dependency closure as `HostIntegrated` when the root declares host-integrated dependency-closure metadata.
- **SC-008**: Loading catalog descriptors can explain for every loaded package whether its effective mode came from fallback, package override, package metadata, dependency-closure promotion, or conflict handling.
- **SC-009**: Existing tests for collectible loading, explicit host-integrated overrides, no-assembly facade dependencies, and host-integrated assembly conflict handling continue to pass.
- **SC-010**: Documentation gives package authors a complete metadata example and gives app authors a migration path from `PackageLoadModes` overrides to package-declared metadata.

## Assumptions

- The automatic policy is intended to be available by default for metadata-aware packages, with `Collectible` remaining the fallback when no advisor requires host integration.
- Automatic selection is an option-level policy, not a third public effective load mode.
- Explicit package-specific overrides are the strongest per-package input, but graph-level closure promotion may still make other graph members host-integrated when another effective requirement in the graph requires it.
- Package metadata is advisory policy input, not a security boundary or permission grant.
- Package-authored `Collectible` metadata expresses preference only; app authors use explicit package overrides when they want to force collectible behavior.
- Nuplane reads package-root `nuplane.json` metadata from already installed/extracted packages; it does not require NuGet restore or build-time asset processing for this feature.
- `HostIntegrated` is considered the safest deterministic conflict result when the alternative could cause framework/default-context resolution failures, even though it has weaker unloadability than `Collectible`.

## Recommended Metadata Shape

```json
{
  "schemaVersion": 1,
  "loading": {
    "loadMode": "HostIntegrated",
    "scope": "DependencyClosure",
    "reason": "Uses framework type resolution and runtime scheduler integration."
  }
}
```
