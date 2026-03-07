# Tasks: Runtime Folder & Namespace Reorganization

**Input**: Design documents from `/specs/010-runtime-folder-reorganization/`
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, quickstart.md

**Tests**: This is a pure structural refactor with zero behavior changes. No new tests are required
(OSR-005). Existing tests must pass after each move with only `using` statement and file location
changes. Compilation and full test suite passage serve as the verification mechanism.

**Organization**: Tasks are grouped by user story (each story = one logical move). Each move must
leave the solution in a compilable, all-tests-green state (OSR-002).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3, US4)
- Include exact file paths in descriptions

## Path Conventions

- **Source root**: `src/Nuplane.Runtime/`
- **Test root**: `test/Nuplane.Runtime.Tests/`
- **Solution file**: `nuplane.sln`

---

## Phase 1: Setup

**Purpose**: Establish the target folder structure and verify the pre-reorganization baseline.

- [ ] T001 Verify pre-reorganization baseline by running `dotnet build nuplane.sln` and `dotnet test nuplane.sln` — all must pass before any moves begin
- [x] T002 [P] Create new folder `src/Nuplane.Runtime/Feeds/`
- [x] T003 [P] Create new folder `src/Nuplane.Runtime/Feeds/Policy/`
- [x] T004 [P] Create new folder `src/Nuplane.Runtime/Feeds/Configuration/`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: N/A — this feature is a pure structural refactor. There are no shared infrastructure,
secret-handling, transactional, or observability changes. Observability infrastructure (`Events/`,
`Health/`, `Observability/`, `Operational/`) is explicitly excluded (OSR-004). No DI registration
logic changes are needed (D-007) — only `using` statement updates in registration files.

**⚠️ NOTE**: This phase is intentionally empty. Setup (Phase 1) directly enables user story work.

---

## Phase 3: User Story 1 — Separate Feed Acquisition into Dedicated Folder (Priority: P1) 🎯 MVP

**Goal**: Move all feed acquisition types (~11 files) from `Reconciliation/`, `Reconciliation/FeedPolicy/`, and `Configuration/` into the new `Feeds/` folder hierarchy. Update namespace declarations in `Trust/Feeds/` files. Update all `using` statements across `src/` and `test/`. Retire the `Nuplane.Runtime.Reconciliation.FeedPolicy` namespace entirely.

**Independent Test**: After completing this move, the entire solution compiles (`dotnet build nuplane.sln`), all existing tests pass (`dotnet test nuplane.sln`), every feed acquisition type resolves under `Nuplane.Runtime.Feeds` (or sub-namespaces `Policy` and `Configuration`), and `namespace Nuplane.Runtime.Reconciliation.FeedPolicy` appears nowhere in `src/`.

### Move Feed Acquisition Files (Reconciliation → Feeds)

- [x] T005 [P] [US1] Move `src/Nuplane.Runtime/Reconciliation/MultiFeedPackageResolver.cs` to `src/Nuplane.Runtime/Feeds/MultiFeedPackageResolver.cs` and update namespace to `Nuplane.Runtime.Feeds`
- [x] T006 [P] [US1] Move `src/Nuplane.Runtime/Reconciliation/NuGetRemotePackageAcquirer.cs` to `src/Nuplane.Runtime/Feeds/NuGetRemotePackageAcquirer.cs` and update namespace to `Nuplane.Runtime.Feeds`
- [x] T007 [P] [US1] Move `src/Nuplane.Runtime/Reconciliation/NuGetPackageResolver.cs` to `src/Nuplane.Runtime/Feeds/NuGetPackageResolver.cs` and update namespace to `Nuplane.Runtime.Feeds`
- [x] T008 [P] [US1] Move `src/Nuplane.Runtime/Reconciliation/INuGetPackageResolver.cs` to `src/Nuplane.Runtime/Feeds/INuGetPackageResolver.cs` and update namespace to `Nuplane.Runtime.Feeds`
- [x] T009 [P] [US1] Move `src/Nuplane.Runtime/Reconciliation/NoEligibleFeedException.cs` to `src/Nuplane.Runtime/Feeds/NoEligibleFeedException.cs` and update namespace to `Nuplane.Runtime.Feeds`
- [x] T010 [P] [US1] Move `src/Nuplane.Runtime/Reconciliation/AcquisitionOutcomeEntry.cs` to `src/Nuplane.Runtime/Feeds/AcquisitionOutcomeEntry.cs` and update namespace to `Nuplane.Runtime.Feeds`

### Move Feed Policy Files (Reconciliation/FeedPolicy → Feeds/Policy)

- [x] T011 [P] [US1] Move `src/Nuplane.Runtime/Reconciliation/FeedPolicy/FeedResolutionPolicy.cs` to `src/Nuplane.Runtime/Feeds/Policy/FeedResolutionPolicy.cs` and update namespace to `Nuplane.Runtime.Feeds.Policy`
- [x] T012 [P] [US1] Move `src/Nuplane.Runtime/Reconciliation/FeedPolicy/FeedUnavailableException.cs` to `src/Nuplane.Runtime/Feeds/Policy/FeedUnavailableException.cs` and update namespace to `Nuplane.Runtime.Feeds.Policy`
- [x] T013 [US1] Remove empty folder `src/Nuplane.Runtime/Reconciliation/FeedPolicy/` (FR-009)

### Move Feed Configuration Files (Configuration → Feeds/Configuration)

- [x] T014 [P] [US1] Move `src/Nuplane.Runtime/Configuration/FeedResolutionOptions.cs` to `src/Nuplane.Runtime/Feeds/Configuration/FeedResolutionOptions.cs` and update namespace to `Nuplane.Runtime.Feeds.Configuration`
- [x] T015 [P] [US1] Move `src/Nuplane.Runtime/Configuration/FeedResolutionPolicyMode.cs` to `src/Nuplane.Runtime/Feeds/Configuration/FeedResolutionPolicyMode.cs` and update namespace to `Nuplane.Runtime.Feeds.Configuration`
- [x] T016 [P] [US1] Move `src/Nuplane.Runtime/Configuration/FeedCredentialOptionsValidator.cs` to `src/Nuplane.Runtime/Feeds/Configuration/FeedCredentialOptionsValidator.cs` and update namespace to `Nuplane.Runtime.Feeds.Configuration`
- [x] T017 [US1] Verify `src/Nuplane.Runtime/Configuration/ManifestOptions.cs` remains in place with namespace `Nuplane.Runtime.Configuration` (FR-013)

### Update Namespaces in Trust/Feeds (Stay in Place, Namespace-Only Changes)

- [x] T018 [P] [US1] Update namespace in `src/Nuplane.Runtime/Trust/Feeds/FeedTrustPolicyEvaluator.cs` from `Nuplane.Runtime.Reconciliation.FeedPolicy` to `Nuplane.Runtime.Feeds.Policy`
- [x] T019 [P] [US1] Update namespace in `src/Nuplane.Runtime/Trust/Feeds/UntrustedOverridePolicy.cs` from `Nuplane.Runtime.Reconciliation.FeedPolicy` to `Nuplane.Runtime.Feeds.Policy`
- [x] T020 [P] [US1] Update namespace in `src/Nuplane.Runtime/Trust/Feeds/IFeedTrustPolicyEvaluator.cs` from `Nuplane.Runtime.Reconciliation.FeedPolicy` to `Nuplane.Runtime.Feeds.Policy`
- [x] T021 [P] [US1] Update namespace in `src/Nuplane.Runtime/Trust/Feeds/RestrictedFeedValidatorPipeline.cs` from `Nuplane.Runtime.Reconciliation.FeedPolicy` to `Nuplane.Runtime.Feeds.Policy`
- [x] T022 [P] [US1] Update namespace in `src/Nuplane.Runtime/Trust/Feeds/FeedTrustPolicyOutcome.cs` from `Nuplane.Runtime.Reconciliation.Models` to `Nuplane.Runtime.Feeds.Policy`
- [x] T023 [P] [US1] Update namespace in `src/Nuplane.Runtime/Trust/Feeds/FeedTrustPolicyOptions.cs` from `Nuplane.Runtime.Configuration` to `Nuplane.Runtime.Feeds.Configuration` (D-003)

### Update `using` Statements Across src/ for US1

- [x] T024 [US1] Update `using` statements in all files under `src/Nuplane.Runtime/Reconciliation/` that reference moved feed types — add `using Nuplane.Runtime.Feeds;`, `using Nuplane.Runtime.Feeds.Policy;`, and/or `using Nuplane.Runtime.Feeds.Configuration;` as needed; keep existing `using Nuplane.Runtime.Reconciliation;` where files still reference remaining types (D-004, D-008)
- [x] T025 [US1] Update `using` statements in all files under `src/Nuplane.Runtime/Trust/` that reference moved feed types or retired feed policy namespace — replace `using Nuplane.Runtime.Reconciliation.FeedPolicy;` with `using Nuplane.Runtime.Feeds.Policy;` and add `using Nuplane.Runtime.Feeds.Configuration;` where needed
- [x] T026 [US1] Update `using` statements in all files under `src/Nuplane.Runtime/Sources/` that reference moved feed types — add `using Nuplane.Runtime.Feeds;` and/or `using Nuplane.Runtime.Feeds.Configuration;` where needed
- [x] T027 [US1] Update `using` statements in all other `src/` projects (`src/Nuplane/`, `src/Nuplane.Admin/`, `src/Nuplane.Admin.Api/`, etc.) that reference old feed namespaces — replace `using Nuplane.Runtime.Reconciliation.FeedPolicy;` with `using Nuplane.Runtime.Feeds.Policy;` and add new feed `using` statements as needed
- [x] T028 [US1] Update `using` statements in DI registration files (e.g., service collection extensions) to resolve moved feed types via new namespaces (D-007)

### Update `using` Statements in test/ for US1

- [x] T029 [US1] Update `using` statements in `test/Nuplane.Runtime.Tests/Reconciliation/FeedTrustPolicyEvaluatorTests.cs` — replace `using Nuplane.Runtime.Reconciliation.FeedPolicy;` with `using Nuplane.Runtime.Feeds.Policy;`
- [x] T030 [US1] Update `using` statements in `test/Nuplane.Runtime.Tests/Reconciliation/MultiFeedResolutionPolicyTests.cs` — add `using Nuplane.Runtime.Feeds;` and `using Nuplane.Runtime.Feeds.Policy;`
- [x] T031 [US1] Update `using` statements in `test/Nuplane.Runtime.Tests/Reconciliation/MultiFeedRetryPolicyTests.cs` — add `using Nuplane.Runtime.Feeds;`
- [x] T032 [US1] Update `using` statements in `test/Nuplane.Runtime.Tests/Reconciliation/MultiFeedTieBreakRegressionTests.cs` — add `using Nuplane.Runtime.Feeds;` and `using Nuplane.Runtime.Feeds.Policy;`
- [x] T033 [US1] Update `using` statements in `test/Nuplane.Runtime.Tests/Reconciliation/RemoteFeedDownloadContractTests.cs` — add `using Nuplane.Runtime.Feeds;` and `using Nuplane.Runtime.Feeds.Policy;`
- [x] T034 [US1] Update `using` statements in `test/Nuplane.Runtime.Tests/Reconciliation/LocalDirectoryFeedContractTests.cs` — add `using Nuplane.Runtime.Feeds;` and `using Nuplane.Runtime.Feeds.Policy;`
- [x] T035 [US1] Update `using` statements in `test/Nuplane.Runtime.Tests/Configuration/FeedCredentialOptionsValidatorTests.cs` — replace `using Nuplane.Runtime.Configuration;` (for feed config types) with `using Nuplane.Runtime.Feeds.Configuration;`
- [x] T036 [US1] Update `using` statements in any remaining test files under `test/` that reference old feed namespaces (scan `test/Nuplane.Integration.Tests/` and other test projects)

### Verification for US1

- [x] T037 [US1] Run `dotnet build nuplane.sln` — must compile with zero errors
- [x] T038 [US1] Run `dotnet test nuplane.sln` — all existing tests must pass
- [x] T039 [US1] Verify `namespace Nuplane.Runtime.Reconciliation.FeedPolicy` appears nowhere in `src/` (SC-005 partial)
- [x] T040 [US1] Verify `src/Nuplane.Runtime/Reconciliation/FeedPolicy/` folder no longer exists (SC-006 partial)

**Checkpoint**: Move 1 complete — Feed acquisition types are consolidated in `Feeds/`. Solution compiles and all tests pass. The `Reconciliation.FeedPolicy` namespace is fully retired.

---

## Phase 4: User Story 2 — Consolidate Desired-State Sources (Priority: P2)

**Goal**: Move all desired-state source types (7 files) from `Desired/`, `Reconciliation/`, and `Reconciliation/Models/` into the existing `Sources/` folder. Eliminate the `Desired/` folder entirely. Update all `using` statements. Retire the `Nuplane.Runtime.Desired` namespace.

**Independent Test**: After completing this move, all desired-state types resolve from `Sources/`, the `Desired/` folder no longer exists, the solution compiles, and all tests pass.

### Move Desired-State Files (Desired → Sources)

- [x] T041 [P] [US2] Move `src/Nuplane.Runtime/Desired/DesiredManifestPackageSource.cs` to `src/Nuplane.Runtime/Sources/DesiredManifestPackageSource.cs` and update namespace to `Nuplane.Runtime.Sources`
- [x] T042 [P] [US2] Move `src/Nuplane.Runtime/Desired/DesiredManifestReader.cs` to `src/Nuplane.Runtime/Sources/DesiredManifestReader.cs` and update namespace to `Nuplane.Runtime.Sources`
- [x] T043 [US2] Remove empty folder `src/Nuplane.Runtime/Desired/` (FR-008)

### Move Desired-State Files (Reconciliation → Sources)

- [x] T044 [P] [US2] Move `src/Nuplane.Runtime/Reconciliation/DesiredStateAggregator.cs` to `src/Nuplane.Runtime/Sources/DesiredStateAggregator.cs` and update namespace to `Nuplane.Runtime.Sources`
- [x] T045 [P] [US2] Move `src/Nuplane.Runtime/Reconciliation/IDesiredStateAggregator.cs` to `src/Nuplane.Runtime/Sources/IDesiredStateAggregator.cs` and update namespace to `Nuplane.Runtime.Sources`

### Move Desired-State Model Files (Reconciliation/Models → Sources)

- [x] T046 [P] [US2] Move `src/Nuplane.Runtime/Reconciliation/Models/StaticDesiredSource.cs` to `src/Nuplane.Runtime/Sources/StaticDesiredSource.cs` and update namespace to `Nuplane.Runtime.Sources`
- [x] T047 [P] [US2] Move `src/Nuplane.Runtime/Reconciliation/Models/DesiredAggregateResult.cs` to `src/Nuplane.Runtime/Sources/DesiredAggregateResult.cs` and update namespace to `Nuplane.Runtime.Sources`
- [x] T048 [P] [US2] Move `src/Nuplane.Runtime/Reconciliation/Models/DesiredReadResult.cs` to `src/Nuplane.Runtime/Sources/DesiredReadResult.cs` and update namespace to `Nuplane.Runtime.Sources`
- [x] T049 [US2] Verify `src/Nuplane.Runtime/Reconciliation/Models/` still contains 10 remaining model files (D-009) — folder must NOT be removed

### Update `using` Statements Across src/ for US2

- [x] T050 [US2] Update `using` statements in all files under `src/Nuplane.Runtime/Reconciliation/` that reference moved desired-state types — add `using Nuplane.Runtime.Sources;` where needed; keep `using Nuplane.Runtime.Reconciliation;` and `using Nuplane.Runtime.Reconciliation.Models;` where files still reference remaining types (D-004)
- [x] T051 [US2] Update `using` statements in all files under `src/Nuplane.Runtime/Sources/` that reference types from old namespaces — replace `using Nuplane.Runtime.Desired;` with `using Nuplane.Runtime.Sources;` (or remove if already in-namespace); replace `using Nuplane.Runtime.Reconciliation.Models;` for moved types with `using Nuplane.Runtime.Sources;`
- [x] T052 [US2] Update `using` statements in all files under `src/Nuplane.Runtime/Feeds/` that reference moved desired-state types — add `using Nuplane.Runtime.Sources;` where needed
- [x] T053 [US2] Update `using` statements in all other `src/` projects that reference `using Nuplane.Runtime.Desired;` — replace with `using Nuplane.Runtime.Sources;`

### Update `using` Statements in test/ for US2

- [x] T054 [US2] Update `using` statements in all files under `test/Nuplane.Runtime.Tests/Desired/` — replace `using Nuplane.Runtime.Desired;` with `using Nuplane.Runtime.Sources;` and update any `using Nuplane.Runtime.Reconciliation.Models;` references for moved types
- [x] T055 [US2] Update `using` statements in `test/Nuplane.Runtime.Tests/Reconciliation/DesiredStateAggregatorTests.cs` — add `using Nuplane.Runtime.Sources;`
- [x] T056 [US2] Update `using` statements in any remaining test files under `test/` that reference `using Nuplane.Runtime.Desired;` or moved desired-state types (scan all test projects)

### Verification for US2

- [x] T057 [US2] Run `dotnet build nuplane.sln` — must compile with zero errors
- [x] T058 [US2] Run `dotnet test nuplane.sln` — all existing tests must pass
- [x] T059 [US2] Verify `namespace Nuplane.Runtime.Desired` appears nowhere in `src/` (SC-005 partial)
- [x] T060 [US2] Verify `using Nuplane.Runtime.Desired` appears nowhere in `src/` or `test/` (SC-005 partial)
- [x] T061 [US2] Verify `src/Nuplane.Runtime/Desired/` folder no longer exists (SC-006 partial)

**Checkpoint**: Move 2 complete — Desired-state source types are consolidated in `Sources/`. The `Desired/` folder and `Nuplane.Runtime.Desired` namespace are fully retired.

---

## Phase 5: User Story 3 — Move Trust Gates to Trust Folder (Priority: P3)

**Goal**: Move `AllowlistGate.cs` and `IAllowlistGate.cs` from `Reconciliation/` to `Trust/` with namespace `Nuplane.Runtime.Trust`. Update all `using` statements. This completes the logical separation of all three layers.

**Independent Test**: After completing this move, `AllowlistGate.cs` and `IAllowlistGate.cs` are in `Trust/`, the solution compiles, and all tests pass.

### Move Trust Gate Files (Reconciliation → Trust)

- [x] T062 [P] [US3] Move `src/Nuplane.Runtime/Reconciliation/AllowlistGate.cs` to `src/Nuplane.Runtime/Trust/AllowlistGate.cs` and update namespace to `Nuplane.Runtime.Trust`
- [x] T063 [P] [US3] Move `src/Nuplane.Runtime/Reconciliation/IAllowlistGate.cs` to `src/Nuplane.Runtime/Trust/IAllowlistGate.cs` and update namespace to `Nuplane.Runtime.Trust`

### Update `using` Statements Across src/ for US3

- [x] T064 [US3] Update `using` statements in all files under `src/Nuplane.Runtime/Reconciliation/` that reference `AllowlistGate` or `IAllowlistGate` — add `using Nuplane.Runtime.Trust;` where needed (D-008)
- [x] T065 [US3] Update `using` statements in all other `src/` projects that reference trust gate types via `using Nuplane.Runtime.Reconciliation;` — add `using Nuplane.Runtime.Trust;` where needed

### Update `using` Statements in test/ for US3

- [x] T066 [US3] Update `using` statements in `test/Nuplane.Runtime.Tests/Reconciliation/AllowlistGateTests.cs` — add `using Nuplane.Runtime.Trust;`; update or remove `using Nuplane.Runtime.Reconciliation;` if no longer needed

### Verification for US3

- [x] T067 [US3] Run `dotnet build nuplane.sln` — must compile with zero errors
- [x] T068 [US3] Run `dotnet test nuplane.sln` — all existing tests must pass

**Checkpoint**: Move 3 complete — All three layers (Feeds, Sources, Trust) are cleanly separated from Reconciliation.

---

## Phase 6: User Story 4 — Update Test Folder Structure (Priority: P4)

**Goal**: Mirror the source folder reorganization in the test project. Move test files that directly test moved source types to matching test folders. Verify all test `using` statements are correct.

**Independent Test**: After completing this update, test files referencing moved types compile with updated `using` statements, and test folders mirror the new source structure.

### Move Test Files from Desired/ to Sources/ (Mirror Source Move 2)

- [x] T069 [P] [US4] Move `test/Nuplane.Runtime.Tests/Desired/DesiredAggregationContractTests.cs` to `test/Nuplane.Runtime.Tests/Sources/DesiredAggregationContractTests.cs`
- [x] T070 [P] [US4] Move `test/Nuplane.Runtime.Tests/Desired/DesiredAggregationDeterminismTests.cs` to `test/Nuplane.Runtime.Tests/Sources/DesiredAggregationDeterminismTests.cs`
- [x] T071 [P] [US4] Move `test/Nuplane.Runtime.Tests/Desired/DesiredAggregationDuplicateRegressionTests.cs` to `test/Nuplane.Runtime.Tests/Sources/DesiredAggregationDuplicateRegressionTests.cs`
- [x] T072 [P] [US4] Move `test/Nuplane.Runtime.Tests/Desired/DesiredManifestParserTests.cs` to `test/Nuplane.Runtime.Tests/Sources/DesiredManifestParserTests.cs`
- [x] T073 [P] [US4] Move `test/Nuplane.Runtime.Tests/Desired/DesiredManifestProjectionDeterminismTests.cs` to `test/Nuplane.Runtime.Tests/Sources/DesiredManifestProjectionDeterminismTests.cs`
- [x] T074 [US4] Remove empty folder `test/Nuplane.Runtime.Tests/Desired/`

### Move Test Files from Reconciliation/ (Mirror Source Moves 2 & 3)

- [x] T075 [P] [US4] Move `test/Nuplane.Runtime.Tests/Reconciliation/DesiredStateAggregatorTests.cs` to `test/Nuplane.Runtime.Tests/Sources/DesiredStateAggregatorTests.cs`
- [x] T076 [P] [US4] Move `test/Nuplane.Runtime.Tests/Reconciliation/AllowlistGateTests.cs` to `test/Nuplane.Runtime.Tests/Trust/AllowlistGateTests.cs`

### Verification for US4

- [x] T077 [US4] Run `dotnet build nuplane.sln` — must compile with zero errors
- [x] T078 [US4] Run `dotnet test nuplane.sln` — all existing tests must pass
- [x] T079 [US4] Verify `test/Nuplane.Runtime.Tests/Desired/` folder no longer exists

**Checkpoint**: Test folder structure mirrors source reorganization. All tests pass.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Final validation of all success criteria, cleanup, and documentation.

- [ ] T080 Verify SC-001: `src/Nuplane.Runtime/Reconciliation/` file count is reduced by at least 50% compared to pre-reorganization (~35 files)
- [x] T081 Verify SC-002: Run full `dotnet test nuplane.sln` — 100% of existing tests pass with zero changes to test assertions or logic
- [ ] T082 Verify SC-003: Confirm solution compiled with zero errors after each individual move (Feeds, Sources, Trust)
- [x] T083 Verify SC-004: Developer navigation — all feed types in `Feeds/`, all desired-state types in `Sources/`, all trust types in `Trust/`
- [x] T084 Verify SC-005: Zero remaining references to retired namespaces — `grep -r "Nuplane.Runtime.Desired" src/ test/ --include="*.cs"` and `grep -r "Nuplane.Runtime.Reconciliation.FeedPolicy" src/ test/ --include="*.cs"` return no results
- [x] T085 Verify SC-006: `src/Nuplane.Runtime/Desired/` and `src/Nuplane.Runtime/Reconciliation/FeedPolicy/` folders no longer exist
- [x] T086 Verify OSR-004: `Events/`, `Health/`, `Observability/`, and `Operational/` folders are untouched
- [x] T087 Run quickstart.md validation scenarios 1–5 from `specs/010-runtime-folder-reorganization/quickstart.md`

Focused follow-up status (2026-03-07):
- `T001` remains open because pre-reorganization baseline validation cannot be reproduced on current tree state.
- `T080` remains open. Measured count from git history is `31 -> 21` for root-level `Reconciliation/*.cs` files (32.3% reduction), and `63 -> 49` recursively (22.2% reduction), which does not satisfy the current "at least 50%" criterion.
- `T082` remains open because the repository history does not provide verifiable evidence of a build pass after each individual move step (Feeds, Sources, Trust) as separate checkpoints.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Empty — no blocking prerequisites for a structural refactor
- **US1: Feeds Move (Phase 3)**: Depends on Setup (Phase 1) — creates `Feeds/` folders
- **US2: Sources Move (Phase 4)**: Depends on US1 completion (D-001 — sequential moves avoid conflicting intermediate states)
- **US3: Trust Move (Phase 5)**: Depends on US2 completion (sequential per D-001)
- **US4: Test Mirroring (Phase 6)**: Depends on US1, US2, US3 completion — mirrors all source moves
- **Polish (Phase 7)**: Depends on all user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Setup — establishes `Feeds/` folder and `Nuplane.Runtime.Feeds.Policy` namespace
- **User Story 2 (P2)**: Depends on US1 completion — avoids conflicting namespace state with feed policy types (D-001)
- **User Story 3 (P3)**: Depends on US2 completion — completes logical separation sequentially (D-001)
- **User Story 4 (P4)**: Depends on US1 + US2 + US3 — mirrors all source reorganization in test project

### Within Each User Story

1. Move files and update their namespace declarations (parallelizable across files)
2. Remove empty folders
3. Update `using` statements in `src/` (must follow file moves)
4. Update `using` statements in `test/` (must follow file moves)
5. Build verification (`dotnet build`)
6. Test verification (`dotnet test`)

### Parallel Opportunities

**Within US1 (Phase 3)**:
- T005–T010 (6 feed files) can all move in parallel
- T011–T012 (2 policy files) can move in parallel
- T014–T016 (3 config files) can move in parallel
- T018–T023 (6 Trust/Feeds namespace updates) can all run in parallel

**Within US2 (Phase 4)**:
- T041–T042 (2 Desired files) can move in parallel
- T044–T045 (2 Reconciliation files) can move in parallel
- T046–T048 (3 Model files) can move in parallel

**Within US3 (Phase 5)**:
- T062–T063 (2 trust gate files) can move in parallel

**Within US4 (Phase 6)**:
- T069–T073 (5 Desired test files) can move in parallel
- T075–T076 (2 Reconciliation test files) can move in parallel

---

## Parallel Example: User Story 1

```bash
# Launch all feed file moves together (different files, no dependencies):
Task T005: Move MultiFeedPackageResolver.cs to Feeds/
Task T006: Move NuGetRemotePackageAcquirer.cs to Feeds/
Task T007: Move NuGetPackageResolver.cs to Feeds/
Task T008: Move INuGetPackageResolver.cs to Feeds/
Task T009: Move NoEligibleFeedException.cs to Feeds/
Task T010: Move AcquisitionOutcomeEntry.cs to Feeds/

# Then launch all Trust/Feeds namespace updates together:
Task T018: Update FeedTrustPolicyEvaluator.cs namespace
Task T019: Update UntrustedOverridePolicy.cs namespace
Task T020: Update IFeedTrustPolicyEvaluator.cs namespace
Task T021: Update RestrictedFeedValidatorPipeline.cs namespace
Task T022: Update FeedTrustPolicyOutcome.cs namespace
Task T023: Update FeedTrustPolicyOptions.cs namespace
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (create folders, verify baseline)
2. Complete Phase 3: User Story 1 — Feed acquisition move
3. **STOP and VALIDATE**: `dotnet build` + `dotnet test` — feeds are separated, solution green
4. This is a meaningful deliverable: the largest batch of misplaced files is resolved

### Incremental Delivery

1. Complete Setup → Folders created
2. Add User Story 1 (Feeds) → Build + test green → **Deliverable**: Feed separation complete
3. Add User Story 2 (Sources) → Build + test green → **Deliverable**: Desired-state consolidation complete
4. Add User Story 3 (Trust) → Build + test green → **Deliverable**: All three layers separated
5. Add User Story 4 (Tests) → Build + test green → **Deliverable**: Test mirroring complete
6. Each move is independently compilable and deployable (OSR-002)

### Sequential Execution (Required)

Per D-001, moves MUST execute sequentially: Feeds → Sources → Trust. This avoids conflicting
intermediate states — particularly between the Feeds move (which affects `Reconciliation/FeedPolicy/`
namespace) and the Trust move (which affects files in `Trust/Feeds/` that currently use
`Reconciliation.FeedPolicy` namespace).

---

## Notes

- [P] tasks = different files, no dependencies on incomplete tasks within the same phase
- [Story] label maps task to specific user story for traceability
- This is a zero-behavior-change refactor — no new tests required (OSR-005)
- `using` statement updates follow conservative approach (D-004): ADD new, REMOVE old only when no remaining types referenced
- Files referencing ALL three moved concerns need `using` for all three new namespaces (D-008)
- DI registrations need only `using` statement updates, not logic changes (D-007)
- `ManifestOptions.cs` stays in `Configuration/` (FR-013)
- `ReconciliationOptions.cs` stays in `Reconciliation/Configuration/` (FR-014)
- `Reconciliation/Models/` retains 10 files after desired-state models move out (D-009)
- Commit after each completed user story (move) for clean rollback points

