# Tasks: Key-Based Feed Setup Configuration

**Input**: Design documents from `/specs/027-keyed-feed-config/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Include unit tests plus
contract and/or integration tests as applicable.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Confirm target files and create the shared test locations without changing behavior.

- [X] T001 Inspect existing configuration-driven feed tests and note reusable helpers in `test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs`
- [X] T002 [P] Inspect existing directory feed registration tests and decide whether to extend or create `test/Nuplane.Sources.Directory.Tests/Configuration/DirectoryFeedSetupConfigurationTests.cs`
- [X] T003 [P] Inspect current feed setup translator and options validator in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupConfiguration.cs` and `src/Nuplane/Setup/NuplaneSetupOptionsValidator.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared declaration model and reader foundation that every user story consumes.

**CRITICAL**: No user story implementation should start until these shared contracts exist.

- [X] T004 Create `NuplaneFeedSetupSourceShape` enum in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupSourceShape.cs`
- [X] T005 Create `NuplaneFeedSetupDeclaration` data model in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupDeclaration.cs`
- [X] T006 Create `NuplaneFeedSetupDiagnostic` data model in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupDiagnostic.cs`
- [X] T007 Create `NuplaneFeedSetupReadResult` data model in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupReadResult.cs`
- [X] T008 Create `NuplaneFeedSetupDeclarationReader` shell with section normalization in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupDeclarationReader.cs`
- [X] T009 Create `INuplaneSetupFeedDeclarationSource` abstraction in `src/Nuplane/Setup/INuplaneSetupFeedDeclarationSource.cs`
- [X] T010 Create `ConfigurationNuplaneSetupFeedDeclarationSource` shell in `src/Nuplane/Setup/ConfigurationNuplaneSetupFeedDeclarationSource.cs`
- [X] T011 Add reader contract test skeleton for empty setup sections in `test/Nuplane.Runtime.Tests/Configuration/NuplaneFeedSetupDeclarationReaderTests.cs`

**Checkpoint**: Shared types compile and user story tests can target one reader contract.

---

## Phase 3: User Story 1 - Configure Feeds By Name (Priority: P1) MVP

**Goal**: Operators can declare keyed remote and directory feeds without an inner `Name`; the key becomes the feed name and existing feed properties are preserved.

**Independent Test**: Configure `Nuplane:Setup:Feeds:{feedName}` entries for one remote feed and one directory feed, then verify service registration uses the key as the feed name and preserves `ServiceIndex`, `DirectoryPath`, include settings, credentials, and directory options.

### Tests for User Story 1

- [X] T012 [P] [US1] Add reader tests for keyed remote and keyed directory entries without `Name` in `test/Nuplane.Runtime.Tests/Configuration/NuplaneFeedSetupDeclarationReaderTests.cs`
- [X] T013 [P] [US1] Add configuration-driven keyed remote feed registration test in `test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs`
- [X] T014 [P] [US1] Add keyed directory feed registration test preserving `Directory:Watch` and `Directory:DebounceWindow` in `test/Nuplane.Sources.Directory.Tests/Configuration/DirectoryFeedSetupConfigurationTests.cs`

### Implementation for User Story 1

- [X] T015 [US1] Implement keyed child classification, key-derived names, property binding, and deterministic output in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupDeclarationReader.cs`
- [X] T016 [US1] Implement raw configuration declaration source in `src/Nuplane/Setup/ConfigurationNuplaneSetupFeedDeclarationSource.cs`
- [X] T017 [US1] Register raw setup feed declaration source for validators and translators in `src/Nuplane/Registration/NuplaneOptionsRegistrationServices.cs`
- [X] T018 [US1] Update remote setup translation to consume effective keyed declarations from `INuplaneSetupFeedDeclarationSource` in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupConfiguration.cs`
- [X] T019 [US1] Update directory setup translation to consume effective keyed declarations from `INuplaneSetupFeedDeclarationSource` in `src/Nuplane.Sources.Directory/Configuration/NuplaneDirectoryFeedSetupConfiguration.cs`
- [X] T020 [US1] Extend setup validation to accept keyed declarations without inner `Name` using raw declaration source in `src/Nuplane/Setup/NuplaneSetupOptionsValidator.cs`
- [X] T021 [US1] Run focused US1 tests with `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~ConfigurationDrivenRegistrationTests|FullyQualifiedName~NuplaneFeedSetupDeclarationReaderTests"` and `dotnet test test/Nuplane.Sources.Directory.Tests/Nuplane.Sources.Directory.Tests.csproj --filter "FullyQualifiedName~DirectoryFeedSetupConfigurationTests"`

**Checkpoint**: Keyed remote and directory feed configuration works without inner `Name`.

---

## Phase 4: User Story 2 - Preserve Existing Array Configuration (Priority: P2)

**Goal**: Existing array-based feed setup continues to behave as it does today.

**Independent Test**: Use `Nuplane:Setup:Feeds:0:Name` array-style configuration for remote and directory feeds and verify registration, include settings, credentials, and existing duplicate-name validation behavior are unchanged.

### Tests for User Story 2

- [X] T022 [P] [US2] Add reader tests proving all-digit child keys are array entries and use inner `Name` in `test/Nuplane.Runtime.Tests/Configuration/NuplaneFeedSetupDeclarationReaderTests.cs`
- [X] T023 [P] [US2] Add array compatibility regression tests for remote feed registration in `test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs`
- [X] T024 [P] [US2] Add array compatibility regression tests for directory feed registration in `test/Nuplane.Sources.Directory.Tests/Configuration/DirectoryFeedSetupConfigurationTests.cs`
- [X] T025 [P] [US2] Add duplicate array feed-name validation regression test in `test/Nuplane.Runtime.Tests/Configuration/NuplaneSetupOptionsValidatorTests.cs`

### Implementation for User Story 2

- [X] T026 [US2] Implement numeric child handling and array-name extraction in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupDeclarationReader.cs`
- [X] T027 [US2] Preserve array-only duplicate validation behavior in `src/Nuplane/Setup/NuplaneSetupOptionsValidator.cs`
- [X] T028 [US2] Verify remote translator ignores directory array declarations and handles remote array declarations in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupConfiguration.cs`
- [X] T029 [US2] Verify directory translator ignores remote array declarations and handles directory array declarations in `src/Nuplane.Sources.Directory/Configuration/NuplaneDirectoryFeedSetupConfiguration.cs`
- [X] T030 [US2] Run focused US2 tests with `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~ConfigurationDrivenRegistrationTests|FullyQualifiedName~NuplaneSetupOptionsValidatorTests|FullyQualifiedName~NuplaneFeedSetupDeclarationReaderTests"` and `dotnet test test/Nuplane.Sources.Directory.Tests/Nuplane.Sources.Directory.Tests.csproj --filter "FullyQualifiedName~DirectoryFeedSetupConfigurationTests"`

**Checkpoint**: Legacy array configuration remains functional.

---

## Phase 5: User Story 3 - Override Feeds Through Layered Configuration (Priority: P3)

**Goal**: Later configuration providers can override a same-named keyed feed without creating duplicate registrations.

**Independent Test**: Build configuration from multiple providers that define `Nuplane:Setup:Feeds:feedz.io`, verify the later provider's effective values are used, and verify exactly one feed named `feedz.io` is registered.

### Tests for User Story 3

- [X] T031 [P] [US3] Add layered keyed override reader tests for `ServiceIndex` and `IncludePatterns` in `test/Nuplane.Runtime.Tests/Configuration/NuplaneFeedSetupDeclarationReaderTests.cs`
- [X] T032 [P] [US3] Add layered keyed remote registration test verifying one `feedz.io` feed with the later `ServiceIndex` in `test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs`
- [X] T033 [P] [US3] Add keyed feed priority ordering regression test using existing `FeedResolutionPolicy` in `test/Nuplane.Runtime.Tests/Reconciliation/MultiFeedResolutionPolicyTests.cs`

### Implementation for User Story 3

- [X] T034 [US3] Implement case-insensitive effective declaration grouping by feed name in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupDeclarationReader.cs`
- [X] T035 [US3] Preserve standard provider precedence for keyed feed property values in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupDeclarationReader.cs`
- [X] T036 [US3] Ensure remote feed registration replaces or avoids duplicate same-name registrations from effective declarations in `src/Nuplane/Feeds/Registration/NuplaneFeedRegistrationServices.cs`
- [X] T037 [US3] Ensure directory feed registration replaces or avoids duplicate same-name registrations from effective declarations in `src/Nuplane.Sources.Directory/Registration/DirectorySourceRegistrationServices.cs`
- [X] T038 [US3] Run focused US3 tests with `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~ConfigurationDrivenRegistrationTests|FullyQualifiedName~NuplaneFeedSetupDeclarationReaderTests|FullyQualifiedName~MultiFeedResolutionPolicyTests"`

**Checkpoint**: Layered keyed configuration overrides by feed name without duplicate registrations.

---

## Phase 6: User Story 4 - Diagnose Ambiguous Feed Names (Priority: P4)

**Goal**: Ambiguous or conflicting feed names fail or warn deterministically with useful diagnostics.

**Independent Test**: Configure a keyed feed with mismatched inner `Name` and verify startup validation fails with both names and the configuration path; configure same-name array/keyed declarations and verify keyed wins with a warning diagnostic and no duplicate registration.

### Tests for User Story 4

- [X] T039 [P] [US4] Add key/name mismatch validation test in `test/Nuplane.Runtime.Tests/Configuration/NuplaneSetupOptionsValidatorTests.cs`
- [X] T040 [P] [US4] Add both-source-types and missing-source-type keyed validation tests in `test/Nuplane.Runtime.Tests/Configuration/NuplaneSetupOptionsValidatorTests.cs`
- [X] T041 [P] [US4] Add invalid `ServiceIndex` and blank `DirectoryPath` validation tests in `test/Nuplane.Runtime.Tests/Configuration/NuplaneSetupOptionsValidatorTests.cs`
- [X] T042 [P] [US4] Add mixed array/keyed same-name reader and registration tests in `test/Nuplane.Runtime.Tests/Configuration/NuplaneFeedSetupDeclarationReaderTests.cs`
- [X] T043 [P] [US4] Add warning diagnostic capture test for ignored mixed array declaration in `test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs`

### Implementation for User Story 4

- [X] T044 [US4] Implement key/name mismatch and source-type diagnostic creation in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupDeclarationReader.cs`
- [X] T045 [US4] Wire declaration diagnostics from raw declaration source into `IValidateOptions<NuplaneSetupOptions>` failure messages in `src/Nuplane/Setup/NuplaneSetupOptionsValidator.cs`
- [X] T046 [US4] Add source-generated warning log method for ignored mixed array feed declarations in `src/Nuplane/Setup/NuplaneSetupFeedDiagnosticReporter.cs`
- [X] T047 [US4] Emit warning diagnostics from setup options materialization without credential values in `src/Nuplane/Setup/NuplaneSetupFeedDiagnosticReporter.cs`
- [X] T048 [US4] Verify remote and directory setup translators consume the same effective declarations without duplicating warnings in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupConfiguration.cs` and `src/Nuplane.Sources.Directory/Configuration/NuplaneDirectoryFeedSetupConfiguration.cs`
- [X] T049 [US4] Run focused US4 tests with `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~NuplaneSetupOptionsValidatorTests|FullyQualifiedName~NuplaneFeedSetupDeclarationReaderTests|FullyQualifiedName~ConfigurationDrivenRegistrationTests"`

**Checkpoint**: Ambiguous feed configuration produces deterministic validation errors or warning diagnostics.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Documentation, samples, cleanup, and full validation.

- [X] T050 [P] Update keyed feed setup examples and migration guidance in `README.md`
- [X] T051 [P] Update configuration examples in `docs/posts/introducing-nuplane.md`
- [X] T052 [P] Update relevant wiki documentation under `docs/wiki/`
- [X] T053 [P] Update sample keyed feed configuration in `samples/Nuplane.Sample.AspNetCore/appsettings.json`
- [X] T054 Review feed setup reader, validators, and translators for duplicated parsing logic and extract only necessary local helpers in `src/Nuplane/Feeds/Setup/NuplaneFeedSetupDeclarationReader.cs`
- [X] T055 Run quickstart validation commands from `specs/027-keyed-feed-config/quickstart.md`
- [X] T056 Run full solution tests with `dotnet test nuplane.sln`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies.
- **Foundational (Phase 2)**: Depends on Setup; blocks all user stories.
- **User Story 1 (Phase 3)**: Depends on Foundational; MVP.
- **User Story 2 (Phase 4)**: Depends on Foundational; can run after or alongside US1 once shared reader shape exists.
- **User Story 3 (Phase 5)**: Depends on Foundational; easiest after US1 because it builds on keyed declarations.
- **User Story 4 (Phase 6)**: Depends on Foundational; can run after US1/US2 test surfaces exist.
- **Polish (Phase 7)**: Depends on completed target stories.

### User Story Dependencies

- **US1 Configure Feeds By Name**: No dependency on other stories after Foundation.
- **US2 Preserve Existing Array Configuration**: No dependency on other stories after Foundation, but shares reader files with US1.
- **US3 Override Feeds Through Layered Configuration**: Depends on keyed feed reading from US1 for simplest delivery.
- **US4 Diagnose Ambiguous Feed Names**: Depends on shared diagnostic model from Foundation and benefits from reader coverage from US1/US2.

### Within Each User Story

- Write/extend tests before implementation.
- Reader behavior before translator behavior.
- Translator behavior before service registration assertions.
- Validation diagnostics before startup failure assertions.
- Story complete before moving to the next priority unless work is explicitly parallelized across disjoint files.

---

## Parallel Opportunities

- T001-T003 can be done independently as inspection tasks.
- T004-T007 and T009-T010 create separate model/source files and can run in parallel.
- T012-T014 can be written in parallel across runtime and directory test projects.
- T022-T025 can be written in parallel across reader, registration, directory, and validator tests.
- T031-T033 can be written in parallel because they target separate test files.
- T039-T043 can be written in parallel across validator, reader, and registration diagnostic tests.
- T050-T053 can be done in parallel across documentation and sample files.

## Parallel Example: User Story 1

```bash
# Tests can be drafted together because they touch different files/projects:
Task: "T012 [US1] Add reader tests for keyed remote and keyed directory entries without Name in test/Nuplane.Runtime.Tests/Configuration/NuplaneFeedSetupDeclarationReaderTests.cs"
Task: "T013 [US1] Add configuration-driven keyed remote feed registration test in test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs"
Task: "T014 [US1] Add keyed directory feed registration test preserving Directory options in test/Nuplane.Sources.Directory.Tests/Configuration/DirectoryFeedSetupConfigurationTests.cs"
```

## Parallel Example: User Story 4

```bash
# Diagnostics tests can be split by boundary:
Task: "T039 [US4] Add key/name mismatch validation test in test/Nuplane.Runtime.Tests/Configuration/NuplaneSetupOptionsValidatorTests.cs"
Task: "T042 [US4] Add mixed array/keyed same-name reader and registration tests in test/Nuplane.Runtime.Tests/Configuration/NuplaneFeedSetupDeclarationReaderTests.cs"
Task: "T043 [US4] Add warning diagnostic capture test in test/Nuplane.Runtime.Tests/Configuration/ConfigurationDrivenRegistrationTests.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 and Phase 2.
2. Complete Phase 3 tests and implementation.
3. Validate keyed remote and directory feeds without inner `Name`.
4. Stop and review before expanding compatibility and diagnostics.

### Incremental Delivery

1. Foundation reader/model.
2. US1 keyed configuration support.
3. US2 legacy array compatibility regression.
4. US3 layered override behavior.
5. US4 diagnostics and warning behavior.
6. Documentation, samples, and full validation.

### Risk Notes

- `NuplaneFeedSetupDeclarationReader.cs` is shared by all stories; coordinate edits carefully if stories run in parallel.
- `NuplaneSetupOptionsValidator.cs` is touched by US1, US2, and US4; avoid overlapping edits without merging between phases.
- Remote and directory translators must consume the same reader output to avoid divergent registrations.

## Notes

- [P] tasks = different files, no dependency on an incomplete task.
- [Story] label maps each task to a user story for traceability.
- Every implementation task includes an exact file path.
- Tests should fail before implementation for changed behavior.
- Options classes remain data-only; validation stays in `IValidateOptions<T>` with existing `ValidateOnStart()` registration.
