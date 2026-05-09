# Feature Specification: Host-Integrated Package Loading

**Feature Branch**: `018-host-integrated-loading`  
**Created**: 2026-05-09  
**Status**: Draft  
**Input**: User description: "Add a first-class host-integrated package load mode to Nuplane so packages that contribute DI registrations, EF Core providers or migrations, ASP.NET endpoints, CShells/Elsa shell features, hosted services, options, validators, or similar framework-integrated types can be loaded for application lifetime without host-specific AssemblyLoadContext or resolver plumbing. Distinguish shared assemblies/type identity from package load mode/lifetime and from assembly resolution visibility. Preserve existing collectible behavior for isolated or scan-only plugin scenarios. Allow default and per-package load mode configuration. Ensure host-integrated assemblies are safe for non-collectible framework code, framework Assembly.Load by name can resolve active host-integrated package assemblies when appropriate, package graph dependency resolution remains deterministic, version conflicts surface clear diagnostics, and docs explain collectible versus host-integrated modes."

## Problem

Nuplane can load package assemblies for dynamic runtime scenarios, but framework-integrated packages expose a different requirement than isolated plugin scanning. Packages that register services, provide database migrations, contribute web endpoints, expose shell features, or participate in host frameworks may later be resolved and used by framework code that lives outside the original package loading boundary.

Shared assembly policy solves contract type identity by ensuring host/plugin abstractions match, but it does not by itself define package assembly lifetime or make package assemblies discoverable when framework code resolves an assembly by name. Hosts currently need custom assembly loading or resolution plumbing for these scenarios, which makes Nuplane harder to adopt and risks runtime failures when non-collectible framework code interacts with collectible plugin assemblies.

## Goals

- Provide an explicit host-integrated loading mode for packages intended to participate in application-lifetime framework behavior.
- Keep shared assembly policy independent from package load mode so contract identity remains configurable without implying package lifetime.
- Make active host-integrated package assemblies discoverable to host/framework code when resolving by assembly name.
- Preserve existing collectible package loading for isolated, scan-only, or unloadable plugin scenarios.
- Surface deterministic diagnostics for version conflicts, ambiguous assembly resolution, and failed assembly resolution.
- Document when to use host-integrated loading, when to use collectible loading, and how shared assemblies relate to both modes.

## Non-Goals

- Automatically detecting every framework integration pattern and changing package load behavior without explicit host configuration.
- Making host-integrated packages unloadable with the same guarantees as collectible packages.
- Replacing source trust validation, package graph resolution, or existing reconciliation safety behavior.
- Requiring host applications to register custom assembly load or assembly resolving handlers.
- Silently changing existing consumers from collectible loading to host-integrated loading.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Load Framework-Integrated Packages (Priority: P1)

As a host application owner, I want to mark runtime packages as host-integrated so services, migrations, endpoints, shell features, hosted services, options, and validators can be discovered and used by host frameworks for the application lifetime without custom assembly loading code.

**Why this priority**: This is the primary value of the feature and removes the current adoption blocker for framework-integrated package scenarios.

**Independent Test**: Configure one package as host-integrated, load it through Nuplane, and verify host/framework code can discover and activate its contributed types without any host-specific resolver logic.

**Acceptance Scenarios**:

1. **Given** a configured host-integrated package containing framework-contributed types, **When** Nuplane loads the package, **Then** the host receives assemblies that are safe for framework integration and can discover the contributed types.
2. **Given** a host-integrated package containing a database migrations assembly, **When** framework code resolves that migrations assembly by name, **Then** the expected active package assembly is resolved without host custom code.
3. **Given** a host-integrated package contributes shell features, **When** the host enumerates package assemblies, **Then** shell feature implementations are discoverable and activatable for the application lifetime.

---

### User Story 2 - Preserve Collectible Loading (Priority: P2)

As a host application owner, I want existing collectible loading scenarios to keep working so isolated or scan-only plugin packages can still be unloaded or treated with stronger isolation where framework integration is not required.

**Why this priority**: Backward compatibility prevents existing Nuplane consumers from being surprised by changed assembly lifetime or isolation behavior.

**Independent Test**: Configure a package for collectible loading and verify Nuplane returns collectible package assemblies using the same observable behavior expected by existing consumers.

**Acceptance Scenarios**:

1. **Given** a package configured for collectible loading, **When** Nuplane loads the package, **Then** the package remains isolated from host-integrated assembly resolution behavior.
2. **Given** an existing consumer relying on collectible package behavior, **When** the feature is enabled with default settings unchanged, **Then** the consumer observes no silent switch to host-integrated loading.

---

### User Story 3 - Configure Load Mode Predictably (Priority: P3)

As an operations or platform maintainer, I want to set the default package load mode and override it for specific packages so mixed workloads can use host-integrated loading only where needed.

**Why this priority**: Real hosts may combine framework-integrated packages with isolated plugins, so load behavior must be explicit and controllable.

**Independent Test**: Configure a default load mode and a package-specific override, load multiple packages, and verify each package follows the intended mode.

**Acceptance Scenarios**:

1. **Given** a default package load mode is configured, **When** Nuplane autoloads packages without overrides, **Then** each package uses the configured default mode.
2. **Given** a package-specific load mode override is configured, **When** Nuplane loads that package, **Then** the override takes precedence over the default mode.
3. **Given** shared assemblies are configured, **When** packages use different load modes, **Then** shared assembly contract identity remains governed by shared assembly policy, not by load mode selection.

---

### User Story 4 - Diagnose Conflicts and Resolution Failures (Priority: P4)

As an operator, I want clear diagnostics when assembly resolution is ambiguous, conflicting, or fails so package loading problems can be understood without inspecting host-specific assembly loader internals.

**Why this priority**: Host-integrated packages increase assembly visibility, so deterministic conflict handling and diagnostics are required for safe operations.

**Independent Test**: Load packages that create ambiguous assembly names or incompatible versions and verify Nuplane reports a deterministic failure with actionable diagnostics.

**Acceptance Scenarios**:

1. **Given** two active host-integrated packages expose conflicting versions of the same assembly identity, **When** framework code resolves that assembly by name, **Then** Nuplane applies deterministic conflict handling and emits a clear diagnostic outcome.
2. **Given** framework code requests an assembly name that is not available from active host-integrated packages, **When** resolution fails, **Then** diagnostics identify the requested assembly and the relevant active package context.

### Edge Cases

- A package is configured as host-integrated while one of its dependencies has a conflicting assembly identity already active from another host-integrated package.
- A package uses shared assemblies for contract identity but is still loaded in collectible mode.
- A host-integrated package references a shared assembly supplied by the host/default context.
- Framework code requests an assembly by simple name and multiple active package assemblies could match.
- Framework code requests an assembly by full name with a version that does not match any active package assembly.
- A package fails to load after package graph resolution succeeds but before all host-integrated assemblies are visible.
- A reconciliation update replaces a previously active host-integrated package version while framework code may still hold references to the old assembly.
- Configuration specifies an unsupported load mode value or conflicting default/per-package values.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Nuplane MUST expose an explicit package load mode concept with at least collectible and host-integrated modes.
- **FR-002**: Nuplane MUST allow hosts to configure the default load mode used for autoloaded packages.
- **FR-003**: Nuplane MUST allow hosts to override load mode for an individual package when package-level configuration is available.
- **FR-004**: Nuplane MUST preserve shared assembly policy as an independent configuration area that controls contract/type identity without implying package load mode.
- **FR-005**: Nuplane MUST expose assemblies for active host-integrated packages through the existing package assembly catalog with metadata that identifies load mode and framework-integration safety.
- **FR-006**: Nuplane MUST ensure assemblies returned for host-integrated packages are not collectible assemblies returned to non-collectible host/framework code.
- **FR-007**: Nuplane MUST make active host-integrated package assemblies resolvable by assembly name for framework code when the requested identity uniquely matches an active host-integrated assembly.
- **FR-008**: Nuplane MUST keep package dependency graph resolution consistent with the active package graph for both direct packages and transitive dependencies.
- **FR-009**: Nuplane MUST reject activation when active host-integrated packages would expose conflicting versions of the same assembly simple name, and MUST expose clear diagnostics for conflicts, ambiguities, and failed resolution.
- **FR-010**: Nuplane MUST NOT require host applications to implement custom assembly load context or assembly resolving handlers for intended host-integrated packages.
- **FR-011**: Nuplane MUST preserve existing collectible loading behavior for packages configured or defaulted to collectible mode.
- **FR-012**: Nuplane MUST validate load mode configuration values at startup or configuration activation time and fail with actionable errors for invalid values.
- **FR-013**: Nuplane MUST document the behavioral difference between collectible and host-integrated modes, including lifetime and isolation tradeoffs.
- **FR-014**: Nuplane MUST document that shared assemblies solve type identity but do not replace load mode selection or assembly resolution visibility.
- **FR-015**: Nuplane MUST log the selected load mode, active assembly identities, resolution decisions, conflicts, and failed resolution attempts without logging secrets or credentials.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation/apply flows MUST remain idempotent when repeated with identical package requests and load mode configuration.
- **OSR-002**: Update flows MUST preserve transactional behavior and last-known-good fallback semantics when a host-integrated package fails to load or become resolvable, including keeping the previous last-known-good assembly resolution visibility active when replacement activation or visibility setup fails.
- **OSR-003**: Source trust validation, package integrity checks, and secret handling MUST remain unchanged and apply before packages become host-integrated or collectible.
- **OSR-004**: Observability MUST include structured diagnostics for chosen load mode, package assembly identity, dependency graph identity, assembly resolution source, ambiguity, conflict, and failure.
- **OSR-005**: Tests MUST cover host-integrated loading, collectible loading compatibility, shared assembly identity independence, assembly-name resolution, version conflicts, invalid configuration, and framework-style discovery scenarios.

### Key Entities

- **Package Load Mode**: The configured lifetime and framework integration behavior for package assemblies, such as collectible or host-integrated.
- **Shared Assembly Policy**: The host-controlled list or rule set that determines which assemblies are shared for contract/type identity.
- **Host-Integrated Package Assembly**: An active package assembly intended to be stable for framework use during the application lifetime and visible to assembly-name resolution.
- **Package Assembly Catalog**: The existing discovery surface that exposes active package assemblies to consumers and includes metadata indicating load mode and whether assemblies are safe for framework integration.
- **Assembly Resolution Entry**: A deterministic mapping from an assembly identity to the active host-integrated package assembly that can satisfy it; replacement updates the mapping only after successful activation and visibility setup, otherwise the last-known-good mapping remains active.
- **Resolution Diagnostic**: A structured outcome that explains successful resolution, ambiguity, conflict, or failure.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A host can load a framework-integrated package and discover contributed framework types without adding host-specific assembly loading or resolving code.
- **SC-002**: A framework request for an active host-integrated package assembly by simple or full name resolves to the expected assembly when the identity is unambiguous.
- **SC-003**: A package containing database migrations can be loaded in host-integrated mode and its migrations assembly can be resolved by framework code by name.
- **SC-004**: A shell-style host can discover and activate shell feature implementations from host-integrated Nuplane packages.
- **SC-005**: Collectible mode remains available and covered by tests that verify existing collectible behavior is not silently changed.
- **SC-006**: Shared assembly policy remains independent from load mode and tests prove contract type identity works across load modes.
- **SC-007**: Host-integrated package assemblies returned to framework code are verified as non-collectible and do not trigger collectible-to-non-collectible reference failures.
- **SC-008**: Ambiguous or conflicting assembly identities produce deterministic diagnostics that identify the requested assembly and the conflicting package assemblies.
- **SC-009**: Invalid load mode configuration fails validation with a clear message before packages are activated under an unintended mode.
- **SC-010**: User documentation includes guidance for choosing collectible versus host-integrated loading and explains how shared assemblies relate to both modes.

## Assumptions

- Existing consumers should keep the current default loading behavior unless they explicitly opt into host-integrated loading or configure it as their default.
- Host-integrated packages may remain loaded for the process lifetime and should be documented as lower isolation than collectible packages.
- Explicit configuration is preferred over automatic inference of framework-integrated package intent.
- Package source trust and graph resolution happen before load mode behavior is applied.

## Clarifications

- **2026-05-09**: Host-integrated assembly conflicts fail activation when active host-integrated packages expose different versions of the same assembly simple name.
- **2026-05-09**: Host-integrated package replacement follows last-known-good fallback; the previous resolution visibility remains active if replacement activation or visibility setup fails.
- **2026-05-09**: Host-integrated assemblies are exposed through the existing package assembly catalog with load mode and framework-safety metadata.
