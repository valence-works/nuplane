# Tasks: Host-Integrated Package Loading

**Input**: Design documents from `/specs/018-host-integrated-loading/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/host-integrated-loading-contract.md, quickstart.md

**Tests**: Required by the specification and constitution for changed loading behavior, public contracts, options validation, catalog metadata, assembly resolution, conflict handling, and LKG fallback.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel with other tasks in the same phase when files do not overlap
- **[Story]**: User story label for story phases only
- All task descriptions include exact file paths

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare shared fixtures and baseline contract tests used by later story phases.

- [x] T001 [P] Add host-integrated fixture marker type in test/Nuplane.Loading.Tests.Fixtures/HostIntegratedFixtureTypes.cs
- [x] T002 [P] Add second-version conflict fixture project in test/Nuplane.Loading.Tests.Fixtures.Conflict/Nuplane.Loading.Tests.Fixtures.Conflict.csproj
- [x] T003 Add conflict fixture source type in test/Nuplane.Loading.Tests.Fixtures.Conflict/ConflictFixtureTypes.cs
- [x] T004 Add conflict fixture project reference to test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj
- [x] T005 [P] Add public loading contract tests for load mode API shape in test/Nuplane.Loading.Tests/LoadingOwnershipContractTests.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Define shared models, validation, and internal load-mode selection required before any user story can be implemented.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [x] T006 Create public PackageLoadMode enum with XML documentation in src/Nuplane.Loading.Abstractions/PackageLoadMode.cs
- [x] T007 Extend LoadingOptions with DefaultLoadMode and package override collection in src/Nuplane.Loading/LoadingOptions.cs
- [x] T008 Add package override options model with XML documentation in src/Nuplane.Loading/PackageLoadModeOverrideOptions.cs
- [x] T009 [P] Add load mode validation tests in test/Nuplane.Loading.Tests/LoadingOptionsValidatorTests.cs
- [x] T010 Update LoadingOptionsValidator to validate supported modes and duplicate package overrides in src/Nuplane.Loading/LoadingOptionsValidator.cs
- [x] T011 Add internal PackageLoadModeSelection record in src/Nuplane.Loading/PackageLoadModeSelection.cs
- [x] T012 Add deterministic PackageLoadModeSelector in src/Nuplane.Loading/PackageLoadModeSelector.cs
- [x] T013 [P] Add package load mode selector tests in test/Nuplane.Loading.Tests/PackageLoadModeSelectorTests.cs
- [x] T014 Register PackageLoadModeSelector in src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs
- [x] T015 Extend PackageLoadSession with effective load mode metadata in src/Nuplane.Loading.Abstractions/PackageLoadSession.cs
- [x] T016 Update IPackageLoader method contracts to accept effective load mode selections in src/Nuplane.Loading.Abstractions/IPackageLoader.cs
- [x] T017 Update PackageAutoLoadingObserver to pass deterministic load mode selections to the loader in src/Nuplane.Loading/PackageAutoLoadingObserver.cs

**Checkpoint**: Foundation ready - user story implementation can now begin.

---

## Phase 3: User Story 1 - Load Framework-Integrated Packages (Priority: P1) 🎯 MVP

**Goal**: A host can mark packages as host-integrated and discover/use framework-integrated package assemblies without custom assembly resolver code.

**Independent Test**: Configure one package as host-integrated, load it through Nuplane, query the catalog, and verify framework-style by-name resolution and type discovery work without host resolver setup.

### Tests for User Story 1 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation.**

- [x] T018 [P] [US1] Add host-integrated loader tests for non-collectible framework-safe assemblies in test/Nuplane.Loading.Tests/PackageLoaderHostIntegratedTests.cs
- [x] T019 [P] [US1] Add host-integrated assembly resolver tests for simple-name and full-name success in test/Nuplane.Loading.Tests/HostIntegratedAssemblyResolverTests.cs
- [x] T020 [P] [US1] Add package assembly catalog metadata tests for host-integrated entries in test/Nuplane.Loading.Tests/PackageAssemblyCatalogHostIntegratedTests.cs
- [x] T021 [P] [US1] Add type finder discovery test for host-integrated catalog entries in test/Nuplane.Loading.Tests/PackageTypeFinderTests.cs

### Implementation for User Story 1

- [x] T022 [US1] Add non-collectible host-integrated graph load context in src/Nuplane.Loading/HostIntegratedPackageGraphLoadContext.cs
- [x] T023 [US1] Add host-integrated assembly resolution entry model in src/Nuplane.Loading/HostIntegratedAssemblyResolutionEntry.cs
- [x] T024 [US1] Add host-integrated assembly resolution catalog with generation publication in src/Nuplane.Loading/HostIntegratedAssemblyResolutionCatalog.cs
- [x] T025 [US1] Add Nuplane-owned assembly resolving bridge in src/Nuplane.Loading/HostIntegratedAssemblyResolver.cs
- [x] T026 [US1] Register host-integrated resolver services in src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs
- [x] T027 [US1] Update PackageLoader to create host-integrated contexts for HostIntegrated selections in src/Nuplane.Loading/PackageLoader.cs
- [x] T028 [US1] Update PackageAssemblyProvider to return assemblies from host-integrated contexts in src/Nuplane.Loading/PackageAssemblyProvider.cs
- [x] T029 [US1] Extend PackageAssemblies with load mode and framework-safety metadata in src/Nuplane.Loading.Abstractions/IPackageAssemblyCatalog.cs
- [x] T030 [US1] Update PackageAssemblyCatalog to populate host-integrated metadata in src/Nuplane.Loading/PackageAssemblyCatalog.cs
- [x] T031 [US1] Update package assembly catalog extensions to preserve new metadata in src/Nuplane.Loading.Abstractions/Extensions/PackageAssemblyCatalogExtensions.cs
- [x] T032 [US1] Add host-integrated load and resolver logs in src/Nuplane.Loading/PackageLoader.cs

**Checkpoint**: User Story 1 is functional and testable independently as the MVP.

---

## Phase 4: User Story 2 - Preserve Collectible Loading (Priority: P2)

**Goal**: Existing collectible loading remains available and is not silently changed by host-integrated support.

**Independent Test**: Load a package using default settings and verify existing collectible behavior, unloadability semantics, and catalog framework-safety metadata remain collectible-oriented.

### Tests for User Story 2 ⚠️

- [x] T033 [P] [US2] Add regression tests proving default load mode remains collectible in test/Nuplane.Loading.Tests/PackageLoaderTests.cs
- [x] T034 [P] [US2] Add collectible catalog metadata tests in test/Nuplane.Loading.Tests/PackageAssemblyCatalogTests.cs
- [x] T035 [P] [US2] Add unload coordinator regression tests for collectible sessions in test/Nuplane.Loading.Tests/PackageUnloadCoordinatorTests.cs

### Implementation for User Story 2

- [x] T036 [US2] Update PackageLoader collectible branch to preserve existing collectible context behavior in src/Nuplane.Loading/PackageLoader.cs
- [x] T037 [US2] Update PackageAssemblyLoadContext documentation for collectible mode semantics in src/Nuplane.Loading/PackageAssemblyLoadContext.cs
- [x] T038 [US2] Update PackageUnloadCoordinator to skip host-integrated contexts and preserve collectible unloading in src/Nuplane.Loading/PackageUnloadCoordinator.cs
- [x] T039 [US2] Update LoadingCatalog to report load mode in load state snapshots in src/Nuplane.Loading/LoadingCatalog.cs
- [x] T040 [US2] Update loading operational state projection for collectible versus host-integrated counts in src/Nuplane.Loading/LoadingOperationalStateContributor.cs

**Checkpoint**: User Stories 1 and 2 both work, and existing collectible consumers remain compatible.

---

## Phase 5: User Story 3 - Configure Load Mode Predictably (Priority: P3)

**Goal**: Hosts can set a default load mode and override load mode for individual packages while shared assembly policy remains independent.

**Independent Test**: Configure default `Collectible`, override one package to `HostIntegrated`, load multiple packages, and verify each package follows the intended mode while shared assembly policy still controls contract identity only.

### Tests for User Story 3 ⚠️

- [x] T041 [P] [US3] Add builder API tests for default load mode and package override configuration in test/Nuplane.Loading.Tests/LoadingRegistrationDeterminismTests.cs
- [x] T042 [P] [US3] Add auto-loading observer tests for default and per-package mode selection in test/Nuplane.Loading.Tests/PackageAutoLoadingObserverTests.cs
- [x] T043 [P] [US3] Add shared assembly independence tests across load modes in test/Nuplane.Loading.Tests/SharedAssemblyPolicyMatcherTests.cs

### Implementation for User Story 3

- [x] T044 [US3] Add fluent builder method for default load mode in src/Nuplane.Loading/Builder/NuplaneLoadingBuilder.cs
- [x] T045 [US3] Add fluent builder method for package load mode override in src/Nuplane.Loading/Builder/NuplaneLoadingBuilder.cs
- [x] T046 [US3] Update configuration binding documentation comments for load mode options in src/Nuplane.Loading/LoadingOptions.cs
- [x] T047 [US3] Update PackageAutoLoadingObserver diagnostics to include mode selection reason in src/Nuplane.Loading/PackageAutoLoadingObserver.cs
- [x] T048 [US3] Update NuplaneBuilderLoadingExtensions XML documentation for configured load modes in src/Nuplane.Loading/Builder/NuplaneBuilderLoadingExtensions.cs

**Checkpoint**: User Stories 1, 2, and 3 work independently with predictable configuration behavior.

---

## Phase 6: User Story 4 - Diagnose Conflicts and Resolution Failures (Priority: P4)

**Goal**: Operators receive deterministic diagnostics for conflicting, ambiguous, inactive, and failed host-integrated assembly resolution.

**Independent Test**: Load conflicting or ambiguous host-integrated package assemblies and verify activation/resolution fails deterministically with diagnostics identifying requested assembly names, candidate packages, versions, and failure stage.

### Tests for User Story 4 ⚠️

- [x] T049 [P] [US4] Add conflict activation tests for same simple name with different versions in test/Nuplane.Loading.Tests/PackageLoaderHostIntegratedConflictTests.cs
- [x] T050 [P] [US4] Add resolver diagnostics tests for not-found, ambiguity, and inactive entries in test/Nuplane.Loading.Tests/HostIntegratedAssemblyResolverDiagnosticsTests.cs
- [x] T051 [P] [US4] Add replacement LKG fallback tests in test/Nuplane.Loading.Tests/HostIntegratedAssemblyResolutionCatalogTests.cs
- [x] T052 [P] [US4] Add loading observability tests for conflict and resolution diagnostics in test/Nuplane.Loading.Tests/LoadingCatalogObservabilityTests.cs

### Implementation for User Story 4

- [x] T053 [US4] Add conflict detection before host-integrated visibility publication in src/Nuplane.Loading/HostIntegratedAssemblyResolutionCatalog.cs
- [x] T054 [US4] Add deterministic failure result details for host-integrated conflicts in src/Nuplane.Loading.Abstractions/PackageLoadResult.cs
- [x] T055 [US4] Update PackageLoader to preserve LKG visibility when host-integrated replacement fails in src/Nuplane.Loading/PackageLoader.cs
- [x] T056 [US4] Add resolver diagnostic outcome model in src/Nuplane.Loading/HostIntegratedAssemblyResolutionDiagnostic.cs
- [x] T057 [US4] Update HostIntegratedAssemblyResolver to emit success and failure diagnostics in src/Nuplane.Loading/HostIntegratedAssemblyResolver.cs
- [x] T058 [US4] Update LoadingFailureTracker to record host-integrated conflict and resolution failures in src/Nuplane.Loading/LoadingFailureTracker.cs
- [x] T059 [US4] Update LoadingCatalog health degradation for host-integrated activation conflicts in src/Nuplane.Loading/LoadingCatalog.cs

**Checkpoint**: All user stories are independently functional with deterministic diagnostics and fallback behavior.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Documentation, final contract alignment, and validation.

- [x] T060 [P] Update README loading guidance for Collectible versus HostIntegrated modes in README.md
- [x] T061 [P] Update wiki usage guidance for host-integrated loading configuration in docs/wiki/Usage-Guide.md
- [x] T062 [P] Update glossary with package load mode and host-integrated assembly terms in docs/wiki/Concepts-and-Glossary.md
- [x] T063 [P] Update XML docs on public loading catalog models in src/Nuplane.Loading.Abstractions/IPackageAssemblyCatalog.cs
- [x] T064 [P] Update XML docs on PackageAssemblyReference if metadata relationships change in src/Nuplane.Loading.Abstractions/PackageAssemblyReference.cs
- [x] T065 Run focused loading tests with `dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj`
- [x] T066 Run full solution tests with `dotnet test nuplane.sln`
- [x] T067 Validate quickstart scenarios against implemented behavior in specs/018-host-integrated-loading/quickstart.md

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1**: No dependencies; can start immediately.
- **Phase 2**: Depends on Phase 1 fixture and baseline setup.
- **Phase 3 (US1)**: Depends on Phase 2; delivers MVP host-integrated loading.
- **Phase 4 (US2)**: Depends on Phase 2; can run after or parallel with US1 after shared loader interfaces stabilize.
- **Phase 5 (US3)**: Depends on Phase 2; benefits from US1/US2 behavior for end-to-end verification.
- **Phase 6 (US4)**: Depends on US1 host-integrated resolver/catalog infrastructure.
- **Phase 7**: Depends on completed story behavior.

### User Story Dependencies

- **US1**: Requires foundational load mode models and selector.
- **US2**: Requires foundational load mode models; validates backward compatibility independently.
- **US3**: Requires foundational options models and selector; validates configuration path.
- **US4**: Requires US1 host-integrated resolution catalog and resolver.

### Suggested Story Order

1. **US1** - MVP host-integrated loading and by-name resolution.
2. **US2** - Backward compatibility for collectible behavior.
3. **US3** - Configuration default and per-package overrides.
4. **US4** - Conflict, ambiguity, and LKG diagnostics.

---

## Parallel Execution Examples

### Phase 1

- T001 can run in parallel with T002.
- T005 can run in parallel with fixture setup once expected public API names are agreed.

### Phase 2

- T009 and T013 can be written in parallel after T006-T008 are sketched.
- T011 and T012 can run in parallel because they create separate internal artifacts.

### User Story 1

- T018, T019, T020, and T021 can be written in parallel.
- T022, T023, and T024 can be implemented in parallel after T018-T020 define expected behavior.
- T028, T030, and T031 can run in parallel after T027 establishes loader session metadata.

### User Story 2

- T033, T034, and T035 can be written in parallel.
- T037 and T040 can run in parallel with T036 because they touch separate files.

### User Story 3

- T041, T042, and T043 can be written in parallel.
- T046 and T048 can run in parallel with builder implementation tasks.

### User Story 4

- T049, T050, T051, and T052 can be written in parallel.
- T056 and T058 can run in parallel after diagnostic outcome fields are agreed.

### Polish

- T060, T061, T062, T063, and T064 can run in parallel.

---

## Implementation Strategy

### MVP First (US1 Only)

1. Complete Phase 1 setup.
2. Complete Phase 2 foundations.
3. Complete Phase 3 host-integrated loading and resolver behavior.
4. Run `dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --filter "FullyQualifiedName~HostIntegrated|FullyQualifiedName~PackageAssemblyCatalogHostIntegrated|FullyQualifiedName~PackageTypeFinder"`.

### Incremental Delivery

1. Deliver US1 to prove framework-integrated packages can be loaded and resolved.
2. Deliver US2 to lock backward compatibility for collectible mode.
3. Deliver US3 to expose complete configuration ergonomics.
4. Deliver US4 to harden operations with deterministic diagnostics and LKG fallback.
5. Complete documentation and full validation.

### Validation Gates

- Every changed public/protected API has XML documentation.
- Every options property has a validator and runtime consumer.
- Host-integrated conflict behavior fails before visibility publication.
- LKG visibility remains active after replacement activation or visibility setup failure.
- `dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj` passes before full solution validation.
- `dotnet test nuplane.sln` passes or any unrelated failures are documented.
