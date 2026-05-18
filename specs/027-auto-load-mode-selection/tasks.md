# Tasks: Automatic Load Mode Selection

**Input**: Design documents from `/specs/027-auto-load-mode-selection/`  
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/automatic-load-mode-selection-contract.md, quickstart.md

**Tests**: Required by the specification and constitution for changed loading behavior, public contracts, options validation, advisor parsing/precedence, graph promotion, diagnostics, and the provider-style regression.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel with other tasks in the same phase when files do not overlap
- **[Story]**: User story label for story phases only
- All task descriptions include exact file paths

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare shared test support and public contract expectations without changing runtime behavior.

- [X] T001 [P] Add metadata package fixture helpers for package-root `nuplane.json` in test/Nuplane.Loading.Tests/PackageMetadataTestSupport.cs
- [X] T002 [P] Add public contract tests for automatic load-mode API shape in test/Nuplane.Loading.Tests/LoadingOwnershipContractTests.cs
- [X] T003 [P] Add provider-style graph fixture helpers for metadata-driven host-integrated loading in test/Nuplane.Loading.Tests/PackageLoaderGraphRegressionTests.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Define shared policy, advisor, decision, and diagnostic contracts required before any user story can be implemented.

**CRITICAL**: No user story work can begin until this phase is complete.

- [X] T004 Create public PackageLoadModeSelectionPolicy enum with XML documentation in src/Nuplane.Loading.Abstractions/PackageLoadModeSelectionPolicy.cs
- [X] T005 Extend LoadingOptions with LoadModeSelectionPolicy in src/Nuplane.Loading/LoadingOptions.cs
- [X] T006 [P] Add load-mode selection policy validation tests in test/Nuplane.Loading.Tests/LoadingOptionsValidatorTests.cs
- [X] T007 Update LoadingOptionsValidator to validate LoadModeSelectionPolicy in src/Nuplane.Loading/LoadingOptionsValidator.cs
- [X] T008 [P] Add fluent builder policy configuration tests in test/Nuplane.Loading.Tests/LoadingRegistrationDeterminismTests.cs
- [X] T009 Add fluent builder method for load-mode selection policy in src/Nuplane.Loading/Builder/NuplaneLoadingBuilder.cs
- [X] T010 Add public IPackageLoadModeAdvisor contract with XML documentation in src/Nuplane.Loading.Abstractions/IPackageLoadModeAdvisor.cs
- [X] T011 [P] Add public LoadModeAdvisorContext model with XML documentation in src/Nuplane.Loading.Abstractions/LoadModeAdvisorContext.cs
- [X] T012 [P] Add public LoadModeAdvisorResult model with XML documentation in src/Nuplane.Loading.Abstractions/LoadModeAdvisorResult.cs
- [X] T013 [P] Add public LoadModeDecisionDiagnostic model with XML documentation in src/Nuplane.Loading.Abstractions/LoadModeDecisionDiagnostic.cs
- [X] T014 Add internal PackageLoadModeDecision model in src/Nuplane.Loading/PackageLoadModeDecision.cs
- [X] T015 Add internal PackageGraphLoadModeDecision model in src/Nuplane.Loading/PackageGraphLoadModeDecision.cs
- [X] T016 Register PackageLoadModeSelector dependencies and advisor collection plumbing in src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs

**Checkpoint**: Foundational contracts, options, and registrations are ready for story work.

---

## Phase 3: User Story 1 - Package Authors Declare Load Requirements Once (Priority: P1) MVP

**Goal**: A package can declare `HostIntegrated` dependency-closure loading in package-root `nuplane.json`, and Nuplane loads the graph as `HostIntegrated` without app-specific package overrides.

**Independent Test**: Build a synthetic graph whose root package has valid metadata, configure loading with no package-specific overrides, load the graph, and verify all loadable packages are `HostIntegrated` with `package-metadata` and `dependency-closure` explanations.

### Tests for User Story 1

> NOTE: Write these tests first and ensure they fail before implementation.

- [X] T017 [P] [US1] Add metadata reader tests for valid package-root `nuplane.json` in test/Nuplane.Loading.Tests/PackageMetadataLoadModeReaderTests.cs
- [X] T018 [P] [US1] Add metadata advisor tests for `HostIntegrated` dependency-closure results in test/Nuplane.Loading.Tests/PackageMetadataLoadModeAdvisorTests.cs
- [X] T019 [P] [US1] Add selector tests for metadata-driven graph promotion and no-metadata fallback in test/Nuplane.Loading.Tests/PackageLoadModeSelectorTests.cs
- [X] T020 [P] [US1] Add loader regression test for generic provider-style metadata-driven HostIntegrated closure in test/Nuplane.Loading.Tests/PackageLoaderHostIntegratedTests.cs

### Implementation for User Story 1

- [X] T021 [US1] Add internal NuplanePackageMetadata model in src/Nuplane.Loading/NuplanePackageMetadata.cs
- [X] T022 [US1] Implement package-root metadata reader in src/Nuplane.Loading/PackageMetadataLoadModeReader.cs
- [X] T023 [US1] Implement built-in metadata advisor in src/Nuplane.Loading/PackageMetadataLoadModeAdvisor.cs
- [X] T024 [US1] Update PackageLoadModeSelector to evaluate advisor results before fallback default in src/Nuplane.Loading/PackageLoadModeSelector.cs
- [X] T025 [US1] Update PackageLoader to consume graph load-mode decisions before choosing collectible or host-integrated graph contexts in src/Nuplane.Loading/PackageLoader.cs
- [X] T026 [US1] Register built-in metadata advisor in src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs

**Checkpoint**: User Story 1 is functional and testable independently as the MVP.

---

## Phase 4: User Story 2 - App Authors Override Explicitly (Priority: P1)

**Goal**: Existing explicit `PackageLoadModes` configuration remains authoritative, including suppressing same-package metadata while preserving graph promotion caused by other effective host-integrated requirements.

**Independent Test**: Configure package-specific overrides that conflict with package metadata and verify the explicit override wins for the matching package while diagnostics record suppression and closure promotion remains deterministic.

### Tests for User Story 2

- [X] T027 [P] [US2] Add selector tests for explicit HostIntegrated override winning over Collectible metadata in test/Nuplane.Loading.Tests/PackageLoadModeSelectorTests.cs
- [X] T028 [P] [US2] Add selector tests for explicit Collectible override suppressing HostIntegrated metadata on the same package in test/Nuplane.Loading.Tests/PackageLoadModeSelectorOverrideTests.cs
- [X] T029 [P] [US2] Add loader tests for explicit override closure promotion matching existing behavior in test/Nuplane.Loading.Tests/PackageLoaderHostIntegratedTests.cs

### Implementation for User Story 2

- [X] T030 [US2] Update PackageLoadModeSelector to apply package overrides before same-package advisor results in src/Nuplane.Loading/PackageLoadModeSelector.cs
- [X] T031 [US2] Update PackageLoadModeSelector to emit metadata-suppressed diagnostics for ignored same-package advisor results in src/Nuplane.Loading/PackageLoadModeSelector.cs
- [X] T032 [US2] Update PackageLoader graph promotion to preserve dependency-closure reason codes for packages promoted by another package in src/Nuplane.Loading/PackageLoader.cs

**Checkpoint**: User Story 2 works independently and preserves app-author control over load mode.

---

## Phase 5: User Story 3 - Keep Collectible Loading As The Safe Default Path (Priority: P2)

**Goal**: Graphs without a host-integration requirement remain collectible by fallback, and hosts can disable automatic advisor evaluation through explicit-only policy.

**Independent Test**: Load graphs with no metadata, with Collectible preference metadata, and with automatic selection disabled; verify the effective mode remains `Collectible` when appropriate and no host-integrated resolution entries are published.

### Tests for User Story 3

- [X] T033 [P] [US3] Add selector tests for Collectible metadata preference not forcing down from HostIntegrated in test/Nuplane.Loading.Tests/PackageLoadModeSelectorPolicyTests.cs
- [X] T034 [P] [US3] Add selector tests for ExplicitOnly policy ignoring metadata in test/Nuplane.Loading.Tests/PackageLoadModeSelectorPolicyTests.cs
- [X] T035 [P] [US3] Add collectible fallback loader regression tests in test/Nuplane.Loading.Tests/PackageLoaderTests.cs
- [X] T036 [P] [US3] Add host-integrated resolution catalog absence tests for collectible fallback graphs in test/Nuplane.Loading.Tests/PackageLoaderHostIntegratedTests.cs

### Implementation for User Story 3

- [X] T037 [US3] Update PackageLoadModeSelector to skip advisor evaluation when policy is ExplicitOnly in src/Nuplane.Loading/PackageLoadModeSelector.cs
- [X] T038 [US3] Update PackageLoadModeSelector to treat package-authored Collectible as preference-only in src/Nuplane.Loading/PackageLoadModeSelector.cs
- [X] T039 [US3] Update PackageLoader to avoid host-integrated visibility publication for collectible fallback graphs in src/Nuplane.Loading/PackageLoader.cs
- [X] T040 [US3] Update NuplaneBuilderLoadingExtensions XML documentation for automatic selection policy behavior in src/Nuplane.Loading/Builder/NuplaneBuilderLoadingExtensions.cs

**Checkpoint**: User Story 3 works independently and protects existing collectible/isolation scenarios.

---

## Phase 6: User Story 4 - Explain Load Mode Decisions (Priority: P2)

**Goal**: Operators can query loading state and read logs to understand default fallback, package override, package metadata, dependency-closure promotion, invalid metadata, suppressed metadata, and conflict decisions.

**Independent Test**: Load graphs covering all reason codes and verify `LoadingPackageDescriptor` plus structured logs expose stable, secret-safe explanations.

### Tests for User Story 4

- [X] T041 [P] [US4] Add metadata reader tests for malformed JSON, unsupported schema, unsupported load mode, unsupported scope, missing fields, and oversized metadata in test/Nuplane.Loading.Tests/PackageMetadataLoadModeReaderTests.cs
- [X] T042 [P] [US4] Add selector tests for metadata conflicts resolving deterministically to HostIntegrated in test/Nuplane.Loading.Tests/PackageLoadModeSelectorConflictTests.cs
- [X] T043 [P] [US4] Add loading catalog descriptor explanation tests for all reason codes in test/Nuplane.Loading.Tests/LoadingCatalogTests.cs
- [X] T044 [P] [US4] Add observability tests for advisor evaluation and metadata diagnostics in test/Nuplane.Loading.Tests/LoadingCatalogObservabilityTests.cs

### Implementation for User Story 4

- [X] T045 [US4] Update PackageMetadataLoadModeReader to return metadata-invalid diagnostics for malformed or unsupported metadata in src/Nuplane.Loading/PackageMetadataLoadModeReader.cs
- [X] T046 [US4] Update PackageLoadModeSelector to resolve metadata conflicts with HostIntegrated and metadata-conflict diagnostics in src/Nuplane.Loading/PackageLoadModeSelector.cs
- [X] T047 [US4] Extend LoadingPackageDescriptor with load-mode decision diagnostics in src/Nuplane.Loading.Abstractions/LoadingPackageDescriptor.cs
- [X] T048 [US4] Update LoadingCatalog to project load-mode decision diagnostics onto descriptors in src/Nuplane.Loading/LoadingCatalog.cs
- [X] T049 [US4] Add structured load-mode advisor logging methods in src/Nuplane.Loading/LoadingLogger.cs
- [X] T050 [US4] Wire advisor, metadata, override-suppression, conflict, and final graph-mode logs in src/Nuplane.Loading/PackageLoadModeSelector.cs

**Checkpoint**: User Story 4 works independently with deterministic operator-facing explanations.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Documentation, contract alignment, and validation across all stories.

- [X] T051 [P] Update README loading guidance for automatic selection and package-root metadata in README.md
- [X] T052 [P] Update wiki usage guidance for automatic load-mode selection in docs/wiki/Usage-Guide.md
- [X] T053 [P] Update glossary with automatic selection, advisor, and Nuplane package metadata terms in docs/wiki/Concepts-and-Glossary.md
- [X] T054 [P] Update package authoring metadata guidance in docs/wiki/Package-Authoring.md
- [X] T055 Run quickstart validation scenarios and record notes in specs/027-auto-load-mode-selection/quickstart.md
- [X] T056 Run focused loading tests with `dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj`
- [X] T057 Run full solution tests with `dotnet test nuplane.sln`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1**: No dependencies; can start immediately.
- **Phase 2**: Depends on Phase 1 setup; blocks all user story work.
- **Phase 3 (US1)**: Depends on Phase 2; delivers MVP metadata-driven host-integrated graph selection.
- **Phase 4 (US2)**: Depends on Phase 2; can proceed after selector contracts are stable and complements US1 behavior.
- **Phase 5 (US3)**: Depends on Phase 2; can proceed after policy and selector contracts are stable.
- **Phase 6 (US4)**: Depends on US1-US3 reason-code production paths.
- **Phase 7**: Depends on completed story behavior.

### User Story Dependencies

- **US1**: Requires foundational advisor, policy, and decision contracts.
- **US2**: Requires foundational advisor and selector contracts; validates explicit override precedence.
- **US3**: Requires foundational policy and selector contracts; validates default/disabled behavior.
- **US4**: Requires decision data produced by US1-US3 and projects it to descriptors/logs.

### Parallel Opportunities

- T001, T002, and T003 can run in parallel.
- T006, T008, and T011-T013 can run in parallel after T004-T005 are sketched.
- T017-T020 can run in parallel because they target separate test files.
- T027-T029 can run in parallel because they cover distinct override behavior.
- T033-T036 can run in parallel because they cover policy and loader fallback behavior in separate tests.
- T041-T044 can run in parallel because they cover reader, selector, catalog, and observability surfaces.
- T051-T054 can run in parallel during documentation polish.

## Parallel Example: User Story 1

```text
Task: "Add metadata reader tests for valid package-root `nuplane.json` in test/Nuplane.Loading.Tests/PackageMetadataLoadModeReaderTests.cs"
Task: "Add metadata advisor tests for `HostIntegrated` dependency-closure results in test/Nuplane.Loading.Tests/PackageMetadataLoadModeAdvisorTests.cs"
Task: "Add selector tests for metadata-driven graph promotion and no-metadata fallback in test/Nuplane.Loading.Tests/PackageLoadModeSelectorTests.cs"
Task: "Add loader regression test for generic provider-style metadata-driven HostIntegrated closure in test/Nuplane.Loading.Tests/PackageLoaderHostIntegratedTests.cs"
```

## Parallel Example: User Story 4

```text
Task: "Add metadata reader tests for malformed JSON, unsupported schema, unsupported load mode, unsupported scope, missing fields, and oversized metadata in test/Nuplane.Loading.Tests/PackageMetadataLoadModeReaderTests.cs"
Task: "Add selector tests for metadata conflicts resolving deterministically to HostIntegrated in test/Nuplane.Loading.Tests/PackageLoadModeSelectorConflictTests.cs"
Task: "Add loading catalog descriptor explanation tests for all reason codes in test/Nuplane.Loading.Tests/LoadingCatalogTests.cs"
Task: "Add observability tests for advisor evaluation and metadata diagnostics in test/Nuplane.Loading.Tests/LoadingCatalogObservabilityTests.cs"
```

## Implementation Strategy

### MVP First (US1 Only)

1. Complete Phase 1 setup.
2. Complete Phase 2 foundations.
3. Complete Phase 3 metadata reader, metadata advisor, selector, and loader integration.
4. Run `dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --filter "FullyQualifiedName~PackageMetadataLoadMode|FullyQualifiedName~PackageLoadModeSelector|FullyQualifiedName~PackageLoaderHostIntegrated"`.

### Incremental Delivery

1. Deliver US1 to prove package-authored metadata can promote a graph to `HostIntegrated`.
2. Deliver US2 to lock explicit app override precedence and suppression diagnostics.
3. Deliver US3 to preserve collectible fallback and explicit-only policy.
4. Deliver US4 to expose complete decision explanations.
5. Complete documentation and full validation.

### Validation Gates

- Every changed public/protected API has XML documentation.
- New options remain data-only and are validated through `IValidateOptions<LoadingOptions>`.
- `LoadingPackageDescriptor` can explain every required reason code.
- Existing explicit host-integrated override tests and collectible loading tests still pass.
