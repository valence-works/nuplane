# Tasks: Tolerate Facade Packages

**Input**: Design documents from `/specs/019-tolerate-facade-packages/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md

**Tests**: Test tasks are required because this changes package graph loading behavior.

## Phase 1: Setup

- [x] T001 Create Spec Kit feature branch and specification in `specs/019-tolerate-facade-packages/spec.md`.
- [x] T002 Create requirement checklist in `specs/019-tolerate-facade-packages/checklists/requirements.md`.

---

## Phase 2: Foundational

- [x] T003 Define no-assembly graph-member classification in `specs/019-tolerate-facade-packages/research.md` and `specs/019-tolerate-facade-packages/data-model.md`.

---

## Phase 3: User Story 1 - Load Graphs With Facade Dependencies (Priority: P1)

**Goal**: A graph with loadable packages and no-assembly dependencies loads successfully.

**Independent Test**: Run `dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj`.

### Tests for User Story 1

- [x] T004 [US1] Add collectible graph regression coverage in `test/Nuplane.Loading.Tests/PackageLoaderGraphRegressionTests.cs`.
- [x] T005 [US1] Add host-integrated graph regression coverage in `test/Nuplane.Loading.Tests/PackageLoaderHostIntegratedTests.cs`.

### Implementation for User Story 1

- [x] T006 [US1] Update graph assembly selection in `src/Nuplane.Loading/PackageLoader.cs` to skip no-assembly graph members.
- [x] T007 [US1] Update host-integrated publication in `src/Nuplane.Loading/PackageLoader.cs` to use only loadable graph members.
- [x] T008 [US1] Add diagnostic logging for skipped no-assembly graph members in `src/Nuplane.Loading/PackageLoader.cs`.

---

## Phase 4: User Story 2 - Preserve Diagnostics For Real Failures (Priority: P2)

**Goal**: Genuine loader failures still fail and remain visible.

**Independent Test**: Run `dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj`.

### Tests for User Story 2

- [x] T009 [US2] Add all-no-assembly graph failure coverage in `test/Nuplane.Loading.Tests/PackageLoaderGraphRegressionTests.cs`.

### Implementation for User Story 2

- [x] T010 [US2] Keep missing path, incompatible framework, ambiguous assembly, and load exception behavior unchanged in `src/Nuplane.Loading/PackageLoader.cs`.

---

## Phase 5: Validation

- [x] T011 Run targeted loading test project.
- [x] T012 Restore solution and run full solution test suite.
