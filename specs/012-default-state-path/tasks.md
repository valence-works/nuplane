# Tasks: Default State Path

**Input**: Design documents from `/specs/012-default-state-path/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/store-persistence-configuration.md, quickstart.md

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Include unit tests plus integration/configuration-boundary tests as applicable.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., [US1], [US2], [US3])
- Include exact file paths in descriptions

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Establish shared option shapes and the resolved persistence model used by all stories.

- [ ] T001 Extend `StoreRegistryOptions` with `UseInMemoryStore` and updated XML docs in `src/Nuplane.Store/State/StoreRegistryOptions.cs`
- [ ] T002 [P] Extend `NuplaneSetupOptions` with `UseInMemoryStore` and updated XML docs in `src/Nuplane/Setup/NuplaneSetupOptions.cs`
- [ ] T003 [P] Create `EffectiveStorePersistenceSettings` and `StorePersistenceMode` in `src/Nuplane.Store/State/EffectiveStorePersistenceSettings.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core configuration, validation, transactional safety, trusted-boundary, and observability groundwork that MUST be complete before any user story work can begin.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T004 Add `UseInMemoryStore()` builder API in `src/Nuplane/Builder/NuplaneBuilder.cs`
- [ ] T005 Update setup-to-store translation, `StoreRegistry` construction, and `StoreRegistryOptions` `ValidateOnStart()` wiring in `src/Nuplane/NuplaneServiceCollectionExtensions.cs`
- [ ] T006 Implement `StoreRegistryOptionsValidator` in `src/Nuplane.Store/State/StoreRegistryOptionsValidator.cs`
- [ ] T007 Update `NuplaneSetupOptionsValidator` for blank-path and in-memory/path conflict rules in `src/Nuplane/Setup/NuplaneSetupOptionsValidator.cs`
- [ ] T008 [P] Add `NuplaneSetupOptionsValidator` regression tests in `test/Nuplane.Runtime.Tests/Configuration/NuplaneSetupOptionsValidatorTests.cs`
- [ ] T009 [P] Add `StoreRegistryOptionsValidator` regression tests in `test/Nuplane.Store.Tests/State/StoreRegistryOptionsValidatorTests.cs`
- [ ] T010 Add persisted-write failure regression coverage for transactional safety in `test/Nuplane.Store.Tests/State/StoreRegistryTests.cs`
- [ ] T011 [P] Document the local-only persistence boundary and no-secret handling in `specs/012-default-state-path/contracts/store-persistence-configuration.md`
- [ ] T012 [P] Add first-activation logging assertions for effective persistence mode observability in `test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs`

**Checkpoint**: Foundation ready - user story implementation can now begin.

---

## Phase 3: User Story 1 - Persist State By Default (Priority: P1) 🎯 MVP

**Goal**: Persist reconciliation state automatically to `.nuplane/store-state.json` when no explicit path is configured.

**Independent Test**: Start a host with no `StateFilePath` configured, complete a successful reconciliation, restart the host, and verify state reloads from the default path.

### Tests for User Story 1 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T013 [P] [US1] Add default-path save/load tests in `test/Nuplane.Store.Tests/State/StoreRegistryTests.cs`
- [ ] T014 [P] [US1] Add configuration-boundary tests for default-path resolution and precedence in `test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs`
- [ ] T015 [US1] Add restart-load regression tests for implicit default persistence in `test/Nuplane.Integration.Tests/Reconciliation/StartupLoadingEventIntegrationTests.cs`

### Implementation for User Story 1

- [ ] T016 [US1] Implement default-path derivation and full-path normalization in `src/Nuplane.Store/State/EffectiveStorePersistenceSettings.cs`
- [ ] T017 [US1] Update `StoreRegistry` to lazily resolve/load effective persisted settings on first store access and log default/configured persisted modes in `src/Nuplane.Store/State/StoreRegistry.cs`
- [ ] T018 [US1] Finalize service registration to resolve effective persisted settings for DI-created registries in `src/Nuplane/NuplaneServiceCollectionExtensions.cs`

**Checkpoint**: User Story 1 should be functional and testable independently.

---

## Phase 4: User Story 2 - Explicit Ephemeral Mode (Priority: P1)

**Goal**: Let operators intentionally disable persistence with `UseInMemoryStore=true` while keeping restart behavior explicitly ephemeral.

**Independent Test**: Configure `UseInMemoryStore=true`, run reconciliation, restart the host, and verify no state file is created or reloaded.

### Tests for User Story 2 ⚠️

- [ ] T019 [P] [US2] Add explicit in-memory mode tests in `test/Nuplane.Store.Tests/State/StoreRegistryTests.cs`
- [ ] T020 [P] [US2] Add setup/builder precedence tests for `UseInMemoryStore` in `test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs`
- [ ] T021 [US2] Add restart regression tests proving explicit in-memory mode starts empty in `test/Nuplane.Integration.Tests/Reconciliation/StartupLoadingEventIntegrationTests.cs`

### Implementation for User Story 2

- [ ] T022 [US2] Implement explicit in-memory builder configuration in `src/Nuplane/Builder/NuplaneBuilder.cs`
- [ ] T023 [US2] Update setup translation and precedence handling for `UseInMemoryStore` in `src/Nuplane/NuplaneServiceCollectionExtensions.cs`
- [ ] T024 [US2] Update `StoreRegistry` to honor explicit in-memory mode on first store access and emit the corresponding activation log in `src/Nuplane.Store/State/StoreRegistry.cs`

**Checkpoint**: User Stories 1 and 2 should both work independently.

---

## Phase 5: User Story 3 - Fail Fast On Invalid Persistence Configuration (Priority: P2)

**Goal**: Reject invalid or conflicting persistence configuration at startup before runtime services begin processing.

**Independent Test**: Configure blank or conflicting persistence settings, start the host, and verify startup fails with descriptive validation errors.

### Tests for User Story 3 ⚠️

- [ ] T025 [P] [US3] Add setup-validator tests for blank paths and in-memory/path conflicts in `test/Nuplane.Runtime.Tests/Configuration/NuplaneSetupOptionsValidatorTests.cs`
- [ ] T026 [P] [US3] Add store-validator tests for blank paths and in-memory/path conflicts in `test/Nuplane.Store.Tests/State/StoreRegistryOptionsValidatorTests.cs`
- [ ] T027 [US3] Add startup fail-fast boundary tests for invalid persistence config in `test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs`

### Implementation for User Story 3

- [ ] T028 [US3] Implement setup-surface persistence conflict validation in `src/Nuplane/Setup/NuplaneSetupOptionsValidator.cs`
- [ ] T029 [US3] Implement runtime/store persistence conflict validation in `src/Nuplane.Store/State/StoreRegistryOptionsValidator.cs`
- [ ] T030 [US3] Register `StoreRegistryOptionsValidator` and enforce store-options fail-fast startup in `src/Nuplane/NuplaneServiceCollectionExtensions.cs`

**Checkpoint**: All user stories should now be independently functional.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final documentation alignment and end-to-end validation across user stories.

- [ ] T031 [P] Align persistence configuration examples and validation notes in `specs/012-default-state-path/quickstart.md`
- [ ] T032 Run quickstart validation scenarios from `specs/012-default-state-path/quickstart.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1: Setup** — No dependencies; starts immediately.
- **Phase 2: Foundational** — Depends on Phase 1; blocks all user story work.
- **Phase 3: User Story 1** — Depends on Phase 2.
- **Phase 4: User Story 2** — Depends on Phase 2; can proceed after Phase 2 independently of US1, though it benefits from the shared effective-settings model.
- **Phase 5: User Story 3** — Depends on Phase 2; can proceed independently of US1/US2 once foundational validation scaffolding exists.
- **Phase 6: Polish** — Depends on completion of the desired user stories.

### User Story Dependencies

- **US1**: No dependency on other user stories; this is the MVP.
- **US2**: No dependency on US1 for functional correctness; it shares the same resolved-settings infrastructure established in Phase 2.
- **US3**: No dependency on US1 or US2 for its validation logic; it depends only on the foundational options pipeline wiring.

### Within Each User Story

- Tests MUST be written and fail before implementation.
- Resolved-settings and options models before service-registration updates.
- Service-registration updates before runtime behavior assertions.
- Runtime behavior before end-to-end restart validation.

### Parallel Opportunities

- **Phase 1**: T002 and T003 can run in parallel after T001.
- **Phase 2**: T008, T009, T011, and T012 can run in parallel after T004–T007 skeletons exist.
- **US1**: T013 and T014 can run in parallel; T015 follows once default-path behavior exists in tests.
- **US2**: T019 and T020 can run in parallel; T021 follows after runtime behavior is implemented.
- **US3**: T025 and T026 can run in parallel; T027 follows once validators are wired.
- **Polish**: T031 is independent of T032.

---

## Parallel Example: User Story 1

```bash
# Launch US1 tests together:
Task: "Add default-path save/load tests in test/Nuplane.Store.Tests/State/StoreRegistryTests.cs"
Task: "Add configuration-boundary tests for default-path resolution and precedence in test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs"

# Then implement separate artifacts in parallel where safe:
Task: "Implement default-path derivation and full-path normalization in src/Nuplane.Store/State/EffectiveStorePersistenceSettings.cs"
Task: "Finalize service registration to resolve effective persisted settings for DI-created registries in src/Nuplane/NuplaneServiceCollectionExtensions.cs"
```

---

## Parallel Example: User Story 2

```bash
# Launch US2 tests together:
Task: "Add explicit in-memory mode tests in test/Nuplane.Store.Tests/State/StoreRegistryTests.cs"
Task: "Add setup/builder precedence tests for UseInMemoryStore in test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs"

# Then implement separate artifacts in parallel where safe:
Task: "Implement explicit in-memory builder configuration in src/Nuplane/Builder/NuplaneBuilder.cs"
Task: "Update setup translation and precedence handling for UseInMemoryStore in src/Nuplane/NuplaneServiceCollectionExtensions.cs"
```

---

## Parallel Example: User Story 3

```bash
# Launch US3 validator tests together:
Task: "Add setup-validator tests for blank paths and in-memory/path conflicts in test/Nuplane.Runtime.Tests/Configuration/NuplaneSetupOptionsValidatorTests.cs"
Task: "Add store-validator tests for blank paths and in-memory/path conflicts in test/Nuplane.Store.Tests/State/StoreRegistryOptionsValidatorTests.cs"

# Then implement validators in parallel:
Task: "Implement setup-surface persistence conflict validation in src/Nuplane/Setup/NuplaneSetupOptionsValidator.cs"
Task: "Implement runtime/store persistence conflict validation in src/Nuplane.Store/State/StoreRegistryOptionsValidator.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup.
2. Complete Phase 2: Foundational.
3. Complete Phase 3: User Story 1.
4. Validate restart persistence with the default `.nuplane/store-state.json` path.
5. Stop and review before adding explicit in-memory mode and extra validation stories.

### Incremental Delivery

1. Deliver US1 to eliminate the accidental in-memory default.
2. Deliver US2 to restore ephemeral behavior as an explicit opt-out.
3. Deliver US3 to harden invalid-configuration handling and startup fail-fast behavior.
4. Finish with quickstart validation and documentation alignment.

### Parallel Team Strategy

1. One developer completes Phase 1 and foundational wiring.
2. After Phase 2, split story work:
   - Developer A: US1 default-path runtime and restart tests
   - Developer B: US2 explicit in-memory mode and builder/config tests
   - Developer C: US3 validators and startup fail-fast tests

---

## Notes

- [P] tasks touch different files and have no dependency on unfinished tasks.
- Each task maps to exactly one file or one tightly-coupled artifact.
- `UseInMemoryStore` must have both runtime consumers and validator coverage.
- The direct `StoreRegistry(IStoreStateSerializer, string? stateFilePath)` constructor remains available for tests that intentionally model in-memory behavior.
- Persisted-mode write failures are not a soft warning path; regression coverage must prove the operation fails.