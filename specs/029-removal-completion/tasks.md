# Tasks: Reconciliation Completion After Removals

**Input**: Design documents from `/specs/029-removal-completion/`
**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/reconciliation-completion-contract.md`

**Tests**: Required for changed middleware behavior and runtime/store boundaries.

## Phase 1: Setup

No project or dependency setup is needed; the implementation uses existing projects and APIs.

## Phase 2: Foundational

No blocking infrastructure changes are needed.

## Phase 3: User Story 1 - Observe Committed Package Removal (Priority: P1)

**Goal**: Notify existing completion observers after removal commits even when no package was successfully applied.

**Independent Test**: The real reconciliation integration test observes the committed file-backed active state from inside the registered completion observer.

- [X] T001 [US1] Add removal-only event ordering/payload and removal-plus-failure accounting tests in `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/HealthAndMetricsMiddlewareTests.cs`.
- [X] T002 [US1] Add empty-applied removal retirement regression to `test/Nuplane.Loading.Tests/PackageAutoLoadingObserverTests.cs` using the existing authoritative-state/retirement path.
- [X] T003 [US1] Add real file-backed removal-to-zero reconciliation regression in `test/Nuplane.Integration.Tests/Reconciliation/RemovalCompletionIntegrationTests.cs`.
- [X] T004 [US1] Extend the existing completion condition in `src/Nuplane/Reconciliation/Middleware/HealthAndMetricsMiddleware.cs` to include a non-empty committed removal set.
- [X] T005 [US1] Clarify the existing empty-applied removal case in `src/Nuplane.Abstractions/INuplaneObserver.cs` XML documentation.

## Phase 4: User Story 2 - Preserve Quiet Cycles (Priority: P2)

**Goal**: Keep empty unchanged idle and failed-only cycles without removals notification-free while retaining the existing successful-applied completion behavior.

**Independent Test**: Middleware boundary tests verify no completion for an empty applied list without removals and verify successful-applied completion remains unchanged.

- [X] T006 [US2] Add quiet empty/failed-only and successful-applied no-change boundary controls in `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/HealthAndMetricsMiddlewareTests.cs`.

## Phase 5: Polish and Cross-Cutting Validation

- [X] T007 Run focused Runtime, Loading, and Integration tests; run the six affected Nuplane and Nuplane.Abstractions target builds; perform the condition-only mutation/revert proof recorded in `specs/029-removal-completion/quickstart.md`.

## Dependencies & Execution Order

- T001, T002, and T003 establish the removal contract before changing production behavior.
- T004 and T005 can follow the tests; T004 makes the causal integration test pass.
- T006 can be authored alongside T001 only if edits to the shared middleware test file are sequenced; run after T001.
- T007 depends on all implementation and test tasks.
