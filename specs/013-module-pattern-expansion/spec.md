# Feature Specification: Module Pattern Expansion

**Feature Branch**: `[013-module-pattern-expansion]`  
**Created**: 2026-03-09  
**Status**: Draft  
**Input**: User description: "Create a new spec for both next steps as suggested." 

## Clarifications

### Session 2026-03-09

- Q: What should happen if a consumer registers the same module through both a direct module API and a builder convenience wrapper? → A: Last registration wins and replaces the earlier module registration behavior.
- Q: What is the long-term ownership model for module-specific builder conveniences? → A: Builder conveniences move to a module-owned builder integration package, and any core wrapper is transitional only.
- Q: How should the loading capability be packaged under the module boundary rules? → A: A loading implementation package owns direct registration, and a separate loading builder integration package owns fluent conveniences.
- Q: What should happen to core compatibility wrappers once module-owned APIs exist? → A: Remove core compatibility wrappers in this feature once module-owned APIs exist.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Align Optional Module Ownership (Priority: P1)

As a Nuplane maintainer, I want optional and source-specific capabilities to live behind a consistent module boundary so that package ownership, registration surfaces, and future refactors are predictable.

**Why this priority**: This removes architectural drift across packages and prevents core packages from retaining module-specific implementation details, which is the highest-leverage prerequisite for future module work.

**Independent Test**: Can be fully tested by reviewing one completed module migration, confirming module-owned options/registration surfaces, and verifying that core composition still works without duplicating module implementation logic.

**Acceptance Scenarios**:

1. **Given** a capability that is treated as an optional module, **When** its package ownership is reviewed, **Then** the module's options, hosted services, and direct registration helpers are all assigned to module-owned packages rather than the core composition package.
2. **Given** a module that needs core runtime infrastructure, **When** its boundary is defined, **Then** the module depends only on stable core contracts and runtime services, the loading capability keeps direct registration in a loading implementation package, and any loading-specific fluent conveniences live in a separate loading builder integration package rather than in core.

---

### User Story 2 - Register Modules Directly (Priority: P2)

As a package consumer, I want each module to expose a direct registration surface from the module package so that I can opt into a capability without discovering hidden core implementation details.

**Why this priority**: Direct module registration is the operational contract for optional packages and is required before the module pattern is useful to downstream consumers.

**Independent Test**: Can be tested independently by registering a module through its own package API and verifying that the module's behavior is available without relying on internal core types.

**Acceptance Scenarios**:

1. **Given** a consumer referencing a module package, **When** they search for how to enable that module, **Then** the module package itself exposes the supported registration entrypoint and documents the required core prerequisite.
2. **Given** a module registration entrypoint in the module package, **When** it is used alongside core registration, **Then** the most recent registration wins, any earlier module registration is replaced deterministically, and the result does not create duplicate runtime triggers or conflicting registrations.

---

### User Story 3 - Resolve Builder Convenience Ownership (Priority: P3)

As a contributor, I want a clear policy for builder-level convenience APIs so that module-specific fluent APIs do not drift between core packages and module packages.

**Why this priority**: This is lower priority than direct module ownership because it concerns ergonomics rather than baseline module isolation, but it is required to prevent the same boundary confusion from recurring.

**Independent Test**: Can be tested independently by evaluating one module-specific builder convenience path, documenting the supported ownership model, and confirming that unsupported alternatives have an explicit migration or compatibility policy.

**Acceptance Scenarios**:

1. **Given** a builder convenience for a module capability, **When** its ownership is reviewed, **Then** it is classified as either a module-owned integration surface or an explicit core compatibility wrapper with a documented long-term direction.
2. **Given** the existing directory feed builder convenience, **When** the feature is completed, **Then** the repository documents that the steady-state home for that API is a module-owned builder integration package and defines the expected migration behavior for any transitional core wrapper.

### Edge Cases

- A module requires core runtime trigger or reconciliation infrastructure but should still own its hosted-service and registration behavior.
- A consumer registers the same module through both a direct module extension and a builder convenience wrapper; the later registration replaces the earlier module registration state without duplicating hosted services, triggers, or options consumers.
- A module has no builder integration package yet but still needs a supported direct registration story.
- A refactor improves module ownership for one module while leaving another optional module on an older pattern.

### Assumptions

- The loading capability remains an optional module and should follow the same ownership rules already being applied to the directory-source module.
- Core runtime trigger ingress and other generic runtime infrastructure remain part of the core runtime/composition layer and are reusable by multiple modules.
- Transitional compatibility wrappers are acceptable if they preserve current consumer behavior while steering ownership toward a module-owned surface.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The repository MUST define a documented architectural rule set for Nuplane modules that assigns module-specific options objects, hosted services, registration helpers, and module-owned tests to the module's own package set rather than to the core `Nuplane` package.
- **FR-002**: The loading capability MUST be reviewed and brought into conformance with the module rule set through explicit package ownership boundaries in which a loading implementation package owns its options, direct registration surfaces, and loading-specific implementation types, while a separate loading builder integration package owns any loading-specific fluent conveniences.
- **FR-003**: Each optional or source-specific module MUST expose at least one module-owned direct registration surface in the module package that provides the supported enablement path for that module.
- **FR-004**: The core `Nuplane` package MUST retain only generic runtime composition, feed abstractions, and compatibility-level convenience wrappers; it MUST NOT remain the owner of module-specific hosted services, module-specific options, or module-specific registration services after the boundary is defined.
- **FR-005**: The repository MUST use a single supported ownership model for builder-level module conveniences in which module-specific fluent APIs live in module-owned builder integration packages.
- **FR-006**: The directory-source module MUST record that its fluent builder convenience moves into a dedicated module-owned builder integration package as the steady-state design.
- **FR-007**: Any transitional compatibility wrapper retained during implementation MUST delegate to a module-owned registration service, MUST avoid duplicating module implementation logic, and MUST be removed by the end of this feature once the replacement module-owned APIs and builder integration packages exist.
- **FR-008**: Module-specific registration APIs MUST define duplicate-registration behavior so that repeated registration through direct and convenience paths is deterministic, the most recent registration replaces the earlier module registration behavior, and the resulting service graph does not create duplicate hosted services, duplicate event dispatchers, or conflicting options consumers.
- **FR-009**: Module ownership guidance in repository documentation MUST include package responsibilities, supported registration surfaces, the expected placement of module-specific tests, and the removal of superseded core wrappers once module-owned replacements are available.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Module-boundary refactors MUST preserve deterministic reconciliation behavior for identical desired-state and runtime inputs.
- **OSR-002**: Registration-surface changes MUST remain non-mutating with respect to store activation and rollback semantics; no module boundary change may weaken transactional apply or LKG behavior.
- **OSR-003**: Module-owned registration helpers for source-related capabilities MUST continue to enforce explicit source trust, validation, and credential handling boundaries.
- **OSR-004**: Module registration and ownership changes MUST preserve existing logs, metrics, and health signals, or define equivalent replacements when responsibilities move between packages.
- **OSR-005**: The feature MUST include unit and contract coverage for module-owned registration paths, duplicate-registration behavior, and any moved hosted-service or debounce primitives.

### Key Entities *(include if feature involves data)*

- **Module Package Set**: The group of packages that collectively own one logical capability, including direct registration surfaces, options, implementation types, and tests.
- **Module Registration Surface**: The supported public entrypoint that enables a module directly from its own package.
- **Core Compatibility Wrapper**: A convenience API retained in the core package that forwards into a module-owned registration surface while avoiding ownership of module implementation details.
- **Builder Integration Package**: A module-owned package that is the steady-state home for fluent builder conveniences without forcing the module implementation package to depend on the core builder assembly.
- **Loading Implementation Package**: The loading module package that owns loading-specific options, runtime services, and direct registration APIs without taking a dependency on builder-specific fluency concerns.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Maintainers can identify the owning package for options, hosted services, and registration APIs for both the directory-source and loading capabilities in under 5 minutes using repository documentation alone.
- **SC-002**: A consumer can enable a module through a module-owned direct registration API without referencing internal core types or reading core implementation files.
- **SC-003**: All moved or newly defined module registration paths build and pass their module-scoped automated tests with no duplicate-registration regressions.
- **SC-004**: Repository documentation states that module-specific builder conveniences belong in module-owned builder integration packages, that superseded core wrappers are removed once replacements exist, and contributors can apply that rule consistently to new modules without opening a clarification issue.
