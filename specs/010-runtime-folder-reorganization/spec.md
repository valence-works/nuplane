# Feature Specification: Runtime Folder & Namespace Reorganization

**Feature Branch**: `010-runtime-folder-reorganization`  
**Created**: 2026-03-07  
**Status**: Draft  
**Input**: User description: "Reorganize the Nuplane.Runtime project folder and namespace structure to separate three tangled logical layers — Feed Acquisition, Trust, and Reconciliation Orchestration — into distinct, well-bounded folders and namespaces."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Separate Feed Acquisition into Dedicated Folder (Priority: P1)

As a developer working on the Nuplane.Runtime codebase, I want all feed acquisition types (feed resolution, remote package acquisition, feed policy) moved from the `Reconciliation/` folder into a new `Feeds/` folder with a `Nuplane.Runtime.Feeds` namespace, so that I can reason about, navigate, and modify feed acquisition logic without wading through unrelated reconciliation orchestration code.

**Why this priority**: Feed acquisition files represent the largest batch of misplaced files (~11 files across `Reconciliation/`, `Reconciliation/FeedPolicy/`, and `Configuration/`). Moving them first creates the `Feeds/` folder and establishes the pattern for subsequent moves. It also decouples the most frequently changing concern (feed resolution) from the orchestration layer.

**Independent Test**: After completing this move, the entire solution compiles, all existing tests pass, and every feed acquisition type resolves under `Nuplane.Runtime.Feeds` (or its sub-namespaces `Policy` and `Configuration`). A developer can open the `Feeds/` folder and find all feed acquisition types in one location.

**Acceptance Scenarios**:

1. **Given** the current codebase with feed acquisition types in `Reconciliation/`, **When** a developer opens the `Feeds/` folder after the reorganization, **Then** they find `MultiFeedPackageResolver.cs`, `NuGetRemotePackageAcquirer.cs`, `NuGetPackageResolver.cs`, `INuGetPackageResolver.cs`, `NoEligibleFeedException.cs`, and `AcquisitionOutcomeEntry.cs` all under namespace `Nuplane.Runtime.Feeds`.
2. **Given** feed policy files currently in `Reconciliation/FeedPolicy/`, **When** the reorganization is complete, **Then** `FeedResolutionPolicy.cs` and `FeedUnavailableException.cs` reside in `Feeds/Policy/` under namespace `Nuplane.Runtime.Feeds.Policy`.
3. **Given** feed configuration files currently in `Configuration/`, **When** the reorganization is complete, **Then** `FeedResolutionOptions.cs`, `FeedResolutionPolicyMode.cs`, and `FeedCredentialOptionsValidator.cs` reside in `Feeds/Configuration/` under namespace `Nuplane.Runtime.Feeds.Configuration`.
4. **Given** files in `Trust/Feeds/` with namespace `Nuplane.Runtime.Reconciliation.FeedPolicy` (`FeedTrustPolicyEvaluator`, `UntrustedOverridePolicy`, `IFeedTrustPolicyEvaluator`, `RestrictedFeedValidatorPipeline`, `FeedTrustPolicyOutcome`), **When** the reorganization is complete, **Then** those files have their namespace updated to `Nuplane.Runtime.Feeds.Policy`.
5. **Given** any file in `src/` or `test/` that references the old feed namespaces, **When** the reorganization is complete, **Then** all `using` statements are updated to reference the new namespaces and the solution compiles without errors.

---

### User Story 2 - Consolidate Desired-State Sources (Priority: P2)

As a developer, I want all desired-state source types consolidated into the existing `Sources/` folder under namespace `Nuplane.Runtime.Sources`, so that the concept of "where desired state comes from" is represented in one location rather than scattered across four separate places.

**Why this priority**: Desired-state source scattering (across `Sources/`, `Desired/`, `Reconciliation/`, and `Reconciliation/Models/`) is a significant navigation burden. Consolidating them is the second-largest improvement to code organization and eliminates the entire `Desired/` folder.

**Independent Test**: After completing this move, all desired-state types resolve from `Sources/`, the `Desired/` folder no longer exists, the solution compiles, and all tests pass.

**Acceptance Scenarios**:

1. **Given** `DesiredManifestPackageSource.cs` and `DesiredManifestReader.cs` currently in `Desired/`, **When** the reorganization is complete, **Then** both files reside in `Sources/` under namespace `Nuplane.Runtime.Sources`.
2. **Given** `DesiredStateAggregator.cs` and `IDesiredStateAggregator.cs` currently in `Reconciliation/`, **When** the reorganization is complete, **Then** both files reside in `Sources/` under namespace `Nuplane.Runtime.Sources`.
3. **Given** `StaticDesiredSource.cs`, `DesiredAggregateResult.cs`, and `DesiredReadResult.cs` currently in `Reconciliation/Models/`, **When** the reorganization is complete, **Then** all three files reside in `Sources/` under namespace `Nuplane.Runtime.Sources`.
4. **Given** the `Desired/` folder, **When** all files are moved, **Then** the `Desired/` folder is removed entirely.
5. **Given** any file referencing `using Nuplane.Runtime.Desired;` or `using Nuplane.Runtime.Reconciliation.Models;` for moved types, **When** the reorganization is complete, **Then** the `using` statements are updated to `using Nuplane.Runtime.Sources;` and the solution compiles.

---

### User Story 3 - Move Trust Gates to Trust Folder (Priority: P3)

As a developer, I want the allowlist gate types moved from `Reconciliation/` to the existing `Trust/` folder under namespace `Nuplane.Runtime.Trust`, so that all trust-related logic lives together and the `Reconciliation/` folder contains only orchestration concerns.

**Why this priority**: This is the smallest move (2 files) but completes the logical separation of all three layers. It depends on the feed move being complete first to avoid conflicting intermediate states.

**Independent Test**: After completing this move, `AllowlistGate.cs` and `IAllowlistGate.cs` are in `Trust/`, the solution compiles, and all tests pass.

**Acceptance Scenarios**:

1. **Given** `AllowlistGate.cs` and `IAllowlistGate.cs` currently in `Reconciliation/`, **When** the reorganization is complete, **Then** both files reside in `Trust/` under namespace `Nuplane.Runtime.Trust`.
2. **Given** files in `Reconciliation/` or elsewhere that reference `AllowlistGate` or `IAllowlistGate` via `using Nuplane.Runtime.Reconciliation;`, **When** the reorganization is complete, **Then** those files have `using Nuplane.Runtime.Trust;` added and the solution compiles.

---

### User Story 4 - Update Test Folder Structure (Priority: P4)

As a developer, I want the test project folder structure in `test/Nuplane.Runtime.Tests/` to mirror the source reorganization where applicable, so that test files are easy to find relative to the production code they test.

**Why this priority**: Test organization follows source organization. This story ensures consistency but is lower priority because tests will still compile even if their folder structure doesn't perfectly mirror source — only `using` statements are strictly required to change.

**Independent Test**: After completing this update, test files referencing moved types compile with updated `using` statements, and any test folder mirroring is consistent with the new source structure.

**Acceptance Scenarios**:

1. **Given** test files that reference old namespaces for moved types, **When** the reorganization is complete, **Then** all test `using` statements reference the new namespaces.
2. **Given** test files organized by the old folder structure, **When** the reorganization is complete, **Then** test folders mirror the new source folder structure where applicable.

---

### Edge Cases

- What happens when a file in `Reconciliation/` uses types from ALL three moved concerns (feeds, sources, trust)? It must receive `using` statements for all three new namespaces.
- What happens when a `using Nuplane.Runtime.Reconciliation;` statement covers both moved AND remaining types? The existing `using` must be kept AND new `using` statements added for the moved namespaces.
- What happens when `Configuration/ManifestOptions.cs` is the only remaining file in `Configuration/`? The folder stays with just that file — it is NOT moved.
- What happens when `Reconciliation/Models/` still has files after the desired-state types are moved? The folder remains with the non-moved model files intact.
- What happens when feed trust policy files in `Trust/Feeds/` get a new namespace but stay in the same physical folder? The files remain in `Trust/Feeds/` but their namespace changes from `Nuplane.Runtime.Reconciliation.FeedPolicy` to `Nuplane.Runtime.Feeds.Policy`.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The reorganization MUST create a new `Feeds/` folder under `Nuplane.Runtime/` containing `MultiFeedPackageResolver.cs`, `NuGetRemotePackageAcquirer.cs`, `NuGetPackageResolver.cs`, `INuGetPackageResolver.cs`, `NoEligibleFeedException.cs`, and `AcquisitionOutcomeEntry.cs`, each with namespace `Nuplane.Runtime.Feeds`.
- **FR-002**: The reorganization MUST create a `Feeds/Policy/` subfolder containing `FeedResolutionPolicy.cs` and `FeedUnavailableException.cs`, each with namespace `Nuplane.Runtime.Feeds.Policy`.
- **FR-003**: The reorganization MUST create a `Feeds/Configuration/` subfolder containing `FeedResolutionOptions.cs`, `FeedResolutionPolicyMode.cs`, and `FeedCredentialOptionsValidator.cs`, each with namespace `Nuplane.Runtime.Feeds.Configuration`.
- **FR-004**: The reorganization MUST update the namespace of files in `Trust/Feeds/` (`FeedTrustPolicyEvaluator.cs`, `UntrustedOverridePolicy.cs`, `IFeedTrustPolicyEvaluator.cs`, `RestrictedFeedValidatorPipeline.cs`, `FeedTrustPolicyOutcome.cs`) from `Nuplane.Runtime.Reconciliation.FeedPolicy` to `Nuplane.Runtime.Feeds.Policy`.
- **FR-005**: The reorganization MUST move `DesiredManifestPackageSource.cs` and `DesiredManifestReader.cs` from `Desired/` to `Sources/` with namespace `Nuplane.Runtime.Sources`.
- **FR-006**: The reorganization MUST move `DesiredStateAggregator.cs` and `IDesiredStateAggregator.cs` from `Reconciliation/` to `Sources/` with namespace `Nuplane.Runtime.Sources`.
- **FR-007**: The reorganization MUST move `StaticDesiredSource.cs`, `DesiredAggregateResult.cs`, and `DesiredReadResult.cs` from `Reconciliation/Models/` to `Sources/` with namespace `Nuplane.Runtime.Sources`.
- **FR-008**: The reorganization MUST remove the empty `Desired/` folder after all files are moved out.
- **FR-009**: The reorganization MUST remove the empty `Reconciliation/FeedPolicy/` folder after all files are moved out.
- **FR-010**: The reorganization MUST move `AllowlistGate.cs` and `IAllowlistGate.cs` from `Reconciliation/` to `Trust/` with namespace `Nuplane.Runtime.Trust`.
- **FR-011**: The reorganization MUST update ALL `using` statements across the entire codebase (all projects in `src/` and `test/`) to reference the new namespaces for moved types. Specifically:
  - `using Nuplane.Runtime.Reconciliation;` for feed types → add `using Nuplane.Runtime.Feeds;`
  - `using Nuplane.Runtime.Reconciliation.FeedPolicy;` → `using Nuplane.Runtime.Feeds.Policy;`
  - `using Nuplane.Runtime.Configuration;` for feed config types → add `using Nuplane.Runtime.Feeds.Configuration;`
  - `using Nuplane.Runtime.Desired;` → `using Nuplane.Runtime.Sources;`
  - `using Nuplane.Runtime.Reconciliation.Models;` for moved models → add `using Nuplane.Runtime.Sources;`
  - `using Nuplane.Runtime.Reconciliation;` for trust gate types → add `using Nuplane.Runtime.Trust;`
- **FR-012**: Files that remain in `Reconciliation/` and consume moved types MUST receive new `using` statements for the moved namespaces. Existing `using Nuplane.Runtime.Reconciliation;` statements MUST NOT be removed from files that still reference types remaining in that namespace.
- **FR-013**: `Configuration/ManifestOptions.cs` MUST remain in `Configuration/` with its existing namespace `Nuplane.Runtime.Configuration`.
- **FR-014**: `Reconciliation/Configuration/ReconciliationOptions.cs` MUST remain in `Reconciliation/Configuration/` with its existing namespace.
- **FR-015**: Test folder structure in `test/Nuplane.Runtime.Tests/` MUST mirror the source folder changes where test files correspond to moved source files.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: The reorganization MUST NOT alter any runtime behavior — this is a pure structural and namespace refactor. All existing tests MUST pass without modification to test logic (only `using` statements and file locations may change).
- **OSR-002**: Each move (Feeds, Sources, Trust) MUST be independently compilable — after completing any single move and its associated `using` statement updates, the full solution MUST compile and all tests MUST pass. This enables incremental delivery and rollback to any intermediate state.
- **OSR-003**: The reorganization MUST NOT change any public API surface. All type names, method signatures, and public contracts MUST remain identical; only namespace paths change.
- **OSR-004**: Observability infrastructure (`Events/`, `Health/`, `Observability/`, `Operational/`) MUST NOT be affected by this reorganization.
- **OSR-005**: All moved files MUST be covered by compilation verification (solution builds without errors) and all existing unit, integration, and contract tests MUST pass after each move is complete. No new tests are required for the reorganization itself, but test file locations and `using` statements must be updated.

### Key Entities

- **Feed Acquisition Types**: Types responsible for resolving, acquiring, and applying policy to NuGet package feeds (`MultiFeedPackageResolver`, `NuGetRemotePackageAcquirer`, `NuGetPackageResolver`, `INuGetPackageResolver`, `NoEligibleFeedException`, `AcquisitionOutcomeEntry`, `FeedResolutionPolicy`, `FeedUnavailableException`, `FeedResolutionOptions`, `FeedResolutionPolicyMode`, `FeedCredentialOptionsValidator`).
- **Feed Trust Policy Types**: Types evaluating trust policy for feeds, currently in `Trust/Feeds/` (`FeedTrustPolicyEvaluator`, `UntrustedOverridePolicy`, `IFeedTrustPolicyEvaluator`, `RestrictedFeedValidatorPipeline`, `FeedTrustPolicyOutcome`).
- **Desired-State Source Types**: Types representing where desired package state comes from (`DesiredManifestPackageSource`, `DesiredManifestReader`, `DesiredStateAggregator`, `IDesiredStateAggregator`, `StaticDesiredSource`, `DesiredAggregateResult`, `DesiredReadResult`).
- **Trust Gate Types**: Types implementing allowlist-based trust gates (`AllowlistGate`, `IAllowlistGate`).
- **Reconciliation Orchestration Types** (stay in place): Types coordinating the reconciliation pipeline (`ReconciliationService`, `PackageApplyExecutor`, `DesiredActualDiffEngine`, etc.).

## Assumptions

- The reorganization is purely structural — no logic, signatures, or behavior changes.
- The three moves (Feeds, Sources, Trust) can be performed sequentially, each leaving the solution in a compilable state.
- Files in `Trust/Feeds/` physically stay in that folder but get their namespace updated to align with the feed policy namespace convention.
- `using` statement cleanup follows a conservative approach: only ADD new `using` statements when a file still references types in the old namespace for non-moved types; REMOVE old `using` statements only when the file no longer references ANY types from that namespace.
- Test files that directly test moved types will have their folder locations updated to mirror source; test files that only consume moved types will only have `using` statements updated.
- The `Reconciliation/Models/` folder will still contain files after the desired-state models are moved — only the three specified model files are moved.
- No DI registration changes are needed — service registration calls reference types by their fully qualified or imported names, and updated `using` statements will resolve them.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The `Reconciliation/` folder file count is reduced by at least 50% compared to its pre-reorganization count (~35 files), with moved files correctly placed in `Feeds/`, `Sources/`, and `Trust/`.
- **SC-002**: 100% of existing tests pass after the complete reorganization with zero changes to test assertions or logic — only file locations and `using` statements change.
- **SC-003**: The full solution compiles with zero errors after each individual move (Feeds, Sources, Trust), enabling incremental delivery.
- **SC-004**: A developer searching for any feed acquisition type finds it within the `Feeds/` folder hierarchy; searching for any desired-state source type finds it within `Sources/`; searching for any trust gate type finds it within `Trust/`.
- **SC-005**: Zero remaining references to old namespaces (`Nuplane.Runtime.Desired`, `Nuplane.Runtime.Reconciliation.FeedPolicy`) exist in the codebase after completion — these namespaces are fully retired.
- **SC-006**: The `Desired/` folder and `Reconciliation/FeedPolicy/` folder no longer exist in the project after reorganization.

