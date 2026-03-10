# Tasks: Module Pattern Expansion

**Input**: Design documents from `/specs/013-module-pattern-expansion/`
**Prerequisites**: `plan.md` (required), `spec.md` (required), `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Each user story includes unit tests and boundary tests (contract and/or integration), plus regression coverage for high-risk duplicate-registration and wrapper-removal paths.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare project scaffolding, docs anchors, and validation artifacts used by the feature.

- [x] T001 Add feature scope and migration framing for module pattern expansion in docs/roadmap.md
- [x] T002 Add public feature overview and module-registration migration note in README.md
- [x] T003 [P] Add the new `Nuplane.Sources.Directory.Hosting` project entry in nuplane.sln
- [x] T004 [P] Create the feature validation evidence stub in specs/013-module-pattern-expansion/quickstart-validation.md

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared ownership and safety infrastructure that MUST be complete before any user story can be implemented.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [x] T005 Create shared module replacement-state helpers for deterministic last-registration-wins behavior in src/Nuplane/Registration/ModuleRegistrationState.cs
- [x] T006 [P] Move loading options ownership from abstractions into the loading module in src/Nuplane.Loading/LoadingOptions.cs and src/Nuplane.Loading.Abstractions/LoadingOptions.cs
- [x] T007 [P] Move loading options validator ownership into the loading module in src/Nuplane.Loading/LoadingOptionsValidator.cs and src/Nuplane.Loading.Abstractions/LoadingOptionsValidator.cs
- [x] T008 Add reusable loading registration and `ValidateOnStart()` wiring in src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs and src/Nuplane.Loading/Extensions/LoadingOptionsValidation.cs
- [x] T009 [P] Preserve trusted-source and credential delegation boundaries in src/Nuplane.Sources.Directory/Registration/DirectorySourceRegistrationServices.cs
- [x] T010 [P] Add store/LKG regression coverage proving module-registration refactors do not mutate transactional behavior in test/Nuplane.Store.Tests/Transactions/PackageTransactionCoordinatorTests.cs
- [x] T011 [P] Add baseline observability compatibility coverage for module registration changes in test/Nuplane.Integration.Tests/Reconciliation/ModuleRegistrationCompatibilityTests.cs
- [x] T012 Update project references for the loading ownership split in src/Nuplane.Loading/Nuplane.Loading.csproj and src/Nuplane.Loading.Hosting/Nuplane.Loading.Hosting.csproj

**Checkpoint**: Foundation ready; user stories can start.

---

## Phase 3: User Story 1 - Align Optional Module Ownership (Priority: P1) 🎯 MVP

**Goal**: Make module-specific options, hosted services, registration helpers, and implementation types live in module-owned packages instead of core.

**Independent Test**: Review the loading and directory modules after the move, verify module-owned options/hosted services/registration helpers, and confirm core runtime still resolves without duplicating module orchestration logic.

### Tests for User Story 1 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [x] T013 [P] [US1] Add ownership-boundary tests for loading and directory modules in test/Nuplane.Runtime.Tests/Reconciliation/ModuleOwnershipBoundaryTests.cs
- [x] T014 [P] [US1] Add loading implementation ownership regression tests in test/Nuplane.Loading.Tests/LoadingOwnershipContractTests.cs
- [x] T015 [US1] Extend core runtime isolation regression coverage in test/Nuplane.Runtime.Tests/Reconciliation/CoreRuntimeRegistrationIsolationTests.cs

### Implementation for User Story 1

- [x] T016 [P] [US1] Move loading event-dispatch implementation into the loading module in src/Nuplane.Loading/LoadingEventDispatcher.cs and src/Nuplane.Loading.Hosting/LoadingEventDispatcher.cs
- [x] T017 [P] [US1] Move package auto-loading observer ownership into the loading module in src/Nuplane.Loading/PackageAutoLoadingObserver.cs and src/Nuplane.Loading.Hosting/PackageAutoLoadingObserver.cs
- [x] T018 [P] [US1] Normalize directory module extension wiring around module-owned helpers in src/Nuplane.Sources.Directory/NuplaneDirectorySourceServiceCollectionExtensions.cs
- [x] T019 [US1] Apply shared replacement semantics to loading and directory registration services in src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs and src/Nuplane.Sources.Directory/Registration/DirectorySourceRegistrationServices.cs
- [x] T020 [US1] Remove remaining module-specific ownership from core registration plumbing in src/Nuplane/Feeds/Registration/NuplaneFeedRegistrationServices.cs and src/Nuplane/Nuplane.csproj

**Checkpoint**: User Story 1 is independently functional and testable.

---

## Phase 4: User Story 2 - Register Modules Directly (Priority: P2)

**Goal**: Expose module-owned direct registration APIs that consumers can use without discovering core internals, with deterministic duplicate-registration behavior.

**Independent Test**: Register directory-source and loading directly from their module packages, confirm the module behavior is available, and verify re-registration remains deterministic with no duplicate hosted services or observers.

### Tests for User Story 2 ⚠️

- [x] T021 [P] [US2] Add loading direct-registration determinism tests in test/Nuplane.Loading.Tests/LoadingRegistrationDeterminismTests.cs
- [x] T022 [P] [US2] Add directory direct-registration determinism tests in test/Nuplane.Sources.Directory.Tests/DirectorySourceRegistrationDeterminismTests.cs
- [x] T023 [US2] Add integration coverage for module-owned registration without core implementation reach-through in test/Nuplane.Integration.Tests/Reconciliation/ModuleRegistrationCompatibilityTests.cs

### Implementation for User Story 2

- [x] T024 [P] [US2] Add the module-owned loading `IServiceCollection` extension surface in src/Nuplane.Loading/NuplaneLoadingServiceCollectionExtensions.cs
- [x] T025 [P] [US2] Add loading configuration-binding overloads and direct-registration helpers in src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs
- [x] T026 [P] [US2] Refactor loading builder APIs to delegate to the direct module registration path in src/Nuplane.Loading.Hosting/Builder/NuplaneBuilderLoadingExtensions.cs
- [x] T027 [P] [US2] Update directory direct registration to replace prior module state deterministically in src/Nuplane.Sources.Directory/NuplaneDirectorySourceServiceCollectionExtensions.cs and src/Nuplane.Sources.Directory/Registration/DirectorySourceRegistrationServices.cs
- [x] T028 [US2] Extend registration/configuration coverage for direct module surfaces in test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs

**Checkpoint**: User Stories 1 and 2 are independently functional and testable.

---

## Phase 5: User Story 3 - Resolve Builder Convenience Ownership (Priority: P3)

**Goal**: Move module-specific fluent APIs into module-owned builder integration packages, remove superseded core wrappers, and document the long-term builder ownership rule.

**Independent Test**: Use the module-owned builder integration package for directory-source, confirm it delegates to shared module registration services, and verify core no longer owns module-specific builder conveniences.

### Tests for User Story 3 ⚠️

- [x] T029 [P] [US3] Add directory builder integration contract tests in test/Nuplane.Runtime.Tests/Configuration/DirectoryBuilderIntegrationTests.cs
- [x] T030 [P] [US3] Add override-precedence regression tests for direct-plus-builder registration in test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs
- [x] T031 [US3] Add integration coverage for wrapper removal and module-owned builder usage in test/Nuplane.Integration.Tests/Reconciliation/ModuleRegistrationCompatibilityTests.cs

### Implementation for User Story 3

- [x] T032 [P] [US3] Create the directory builder integration project in src/Nuplane.Sources.Directory.Hosting/Nuplane.Sources.Directory.Hosting.csproj
- [x] T033 [P] [US3] Add module-owned directory builder extensions in src/Nuplane.Sources.Directory.Hosting/Builder/NuplaneBuilderDirectoryExtensions.cs
- [x] T034 [P] [US3] Move directory setup-configuration translation into the builder integration package in src/Nuplane.Sources.Directory.Hosting/Configuration/NuplaneDirectoryFeedSetupConfiguration.cs
- [x] T035 [US3] Remove core directory builder wrappers from src/Nuplane/Feeds/Builder/NuplaneFeedBuilder.cs and src/Nuplane/Feeds/Setup/NuplaneFeedSetupConfiguration.cs
- [x] T036 [US3] Document the steady-state builder ownership and migration policy in docs/coding-conventions.md and README.md

**Checkpoint**: All user stories are independently functional and testable.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final consistency, samples, and end-to-end validation across stories.

- [x] T037 [P] Update sample consumers to use module-owned direct registration and builder packages in samples/Nuplane.Sample.AspNetCore/Program.cs and samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj
- [x] T038 [P] Finalize repository module-boundary guidance in docs/roadmap.md and specs/013-module-pattern-expansion/contracts/module-registration-contract.md
- [x] T039 [P] Finalize builder-ownership guidance in specs/013-module-pattern-expansion/contracts/builder-integration-contract.md and specs/013-module-pattern-expansion/quickstart.md
- [x] T040 Run quickstart validation and capture the evidence in specs/013-module-pattern-expansion/quickstart-validation.md

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: Starts immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1 and blocks all user stories.
- **Phase 3 (US1)**: Depends on Phase 2 completion.
- **Phase 4 (US2)**: Depends on Phase 2 completion.
- **Phase 5 (US3)**: Depends on Phase 2 completion.
- **Phase 6 (Polish)**: Depends on the user stories selected for release being complete.

### User Story Dependencies

- **US1 (P1)**: Starts after Phase 2; establishes the ownership baseline used by the rest of the feature.
- **US2 (P2)**: Starts after Phase 2; reuses the shared replacement and loading-registration foundation but remains independently testable.
- **US3 (P3)**: Starts after Phase 2; depends on shared registration services and remains independently testable once module-owned builder APIs exist.

### Within Each User Story

- Tests MUST be written first and fail before implementation.
- Ownership and contract tests come before moving code.
- Module registration services come before public extension surfaces.
- Public API and wrapper removal happen only after module-owned replacements are in place.
- A story is complete only when its independent test criteria pass.

---

## Parallel Opportunities

- Setup: `T003`, `T004` can run in parallel.
- Foundational: `T006`, `T007`, `T009`, `T010`, `T011` can run in parallel after `T005`; `T012` follows once ownership targets are known.
- US1: `T013` and `T014` can run in parallel; `T016`, `T017`, `T018` can run in parallel before `T019`.
- US2: `T021` and `T022` can run in parallel; `T024`, `T025`, `T026`, `T027` can run in parallel after the foundational loading split is in place.
- US3: `T029` and `T030` can run in parallel; `T032`, `T033`, `T034` can run in parallel before `T035`.
- Polish: `T037`, `T038`, `T039` can run in parallel before `T040`.

### Parallel Example: User Story 1

```bash
# Tests in parallel
T013 + T014

# Ownership moves in parallel
T016 + T017 + T018
```

### Parallel Example: User Story 2

```bash
# Tests in parallel
T021 + T022

# Registration-surface work in parallel
T024 + T025 + T026 + T027
```

### Parallel Example: User Story 3

```bash
# Tests in parallel
T029 + T030

# Builder package work in parallel
T032 + T033 + T034
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 and Phase 2.
2. Complete Phase 3 (US1).
3. Validate module ownership and core isolation independently.
4. Stop and verify the ownership baseline before taking on new public surfaces.

### Incremental Delivery

1. Finish Setup + Foundational to lock in shared module-registration behavior.
2. Deliver US1 to establish the ownership baseline.
3. Deliver US2 to expose direct registration APIs with deterministic replacement behavior.
4. Deliver US3 to move builder conveniences out of core and finalize migration guidance.
5. Finish Polish with sample updates and quickstart validation.

### Parallel Team Strategy

1. Team completes Setup + Foundational together.
2. After Phase 2:
   - Engineer A: US1 ownership alignment
   - Engineer B: US2 direct registration surfaces
   - Engineer C: US3 builder integration package
3. Merge only after each story passes its independent tests.

---

## Notes

- `[P]` tasks are parallelizable when they touch different files and depend only on completed prerequisites.
- `[USx]` labels map tasks to specific user stories for traceability.
- Every task names an explicit file or tightly coupled file group.
- Trusted source handling, transactional rollback/LKG protection, and baseline observability are covered in foundational tasks `T009`-`T011`.
- The intended MVP scope is **User Story 1** after Setup and Foundational are complete.