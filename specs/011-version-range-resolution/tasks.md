# Tasks: Version Range Resolution

**Input**: Design documents from `/specs/011-version-range-resolution/`
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Include unit tests plus
contract and/or integration tests as applicable.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- Source: `src/` at repository root
- Tests: `test/` at repository root
- Paths follow plan.md project structure

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the new `Nuplane.NuGet` project and test project, wire project references

- [ ] T001 Create `src/Nuplane.NuGet/Nuplane.NuGet.csproj` with multi-target frameworks (net8.0;net9.0;net10.0), NuGet.Versioning and NuGet.Protocol package references, Nuplane.Runtime project reference, and add to `nuplane.sln`
- [ ] T002 [P] Create `test/Nuplane.NuGet.Tests/Nuplane.NuGet.Tests.csproj` with xUnit 2.9.3, NSubstitute 5.3.0, coverlet.collector 8.0.0 dependencies, Nuplane.NuGet project reference, and add to `nuplane.sln`
- [ ] T003 [P] Add project reference from `src/Nuplane/Nuplane.csproj` to `src/Nuplane.NuGet/Nuplane.NuGet.csproj`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Abstractions, data records, and shared configuration that ALL user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

**Operational notes**:
- Trusted source/secret handling: Version enumeration reuses the same feed `ServiceIndex` and credentials as package download — no additional endpoints introduced (OSR-003). Handled by `NuGet.Protocol`'s `PackageSource` configuration.
- Transactional rollback/LKG: Version resolution is a read-only pre-download step. Existing stage/validate/publish/atomic-switch flow in `MultiFeedPackageResolver` is preserved (OSR-002).
- Baseline observability: `FeedResolutionDecision` extended with `EnumeratedVersionCount` and `CacheHit` (FR-008). Structured log entries for version resolution added during US1 implementation (OSR-004).

- [ ] T004 [P] Create `PackageVersionList` record (PackageId, FeedName, Versions, EnumeratedAt) in `src/Nuplane.Runtime/Feeds/Versioning/PackageVersionList.cs`
- [ ] T005 [P] Create `VersionResolutionResult` record (Success, SelectedVersion, CandidateCount, FailureReason) in `src/Nuplane.Runtime/Feeds/Versioning/VersionResolutionResult.cs`
- [ ] T006 [P] Create `IFeedVersionEnumerator` interface with `EnumerateVersionsAsync(FeedDefinition, string, CancellationToken)` in `src/Nuplane.Runtime/Feeds/Versioning/IFeedVersionEnumerator.cs`
- [ ] T007 [P] Create `IVersionRangeEvaluator` interface with `SelectBestMatch(string, IReadOnlyList<string>)` and `IsValidRange(string)` in `src/Nuplane.Runtime/Feeds/Versioning/IVersionRangeEvaluator.cs`
- [ ] T008 [P] Create `ParsedIncludePattern` record and `IncludePatternParser` static class (split package glob from version range suffix per contract) in `src/Nuplane.Runtime/Versioning/IncludePatternParser.cs`
- [ ] T009 [P] Add `VersionCacheTtl` property (TimeSpan, default 5 minutes) to `FeedResolutionOptions` in `src/Nuplane.Runtime/Feeds/Configuration/FeedResolutionOptions.cs`
- [ ] T010 [P] Add `EnumeratedVersionCount` (int) and `CacheHit` (bool) properties to `FeedResolutionDecision` in `src/Nuplane.Runtime/Reconciliation/Models/FeedResolutionDecision.cs`
- [ ] T011 Extend `FeedResolutionOptionsValidator` to validate `VersionCacheTtl` is non-negative (TimeSpan.Zero allowed = caching disabled) in `src/Nuplane/Options/Validation/FeedResolutionOptionsValidator.cs`
- [ ] T012 Add unit tests for `VersionCacheTtl` validation (valid duration, zero, negative fails) in `test/Nuplane.Runtime.Tests/Configuration/FeedResolutionOptionsValidatorTests.cs`

**Checkpoint**: Foundation ready — user story implementation can now begin

---

## Phase 3: User Story 1 — Latest Version by Default (Priority: P1) 🎯 MVP

**Goal**: When no version is specified in `IncludePatterns`, resolve and install the latest stable version from the feed instead of defaulting to `1.0.0`

**Independent Test**: Configure a feed with `IncludePatterns: ["MyPackage"]` (no version). Reconcile. Verify the latest stable version is resolved and installed — not `1.0.0`.

### Tests for User Story 1 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T013 [P] [US1] Unit tests for `IncludePatternParser`: all parsing rules from contract (no version, with version range, wildcards, bare version, whitespace trimming) in `test/Nuplane.Runtime.Tests/Versioning/IncludePatternParserTests.cs`
- [ ] T014 [P] [US1] Unit tests for `NuGetVersionRangeEvaluator`: empty range → highest stable, exact match, bounded range, open upper bound, exclusive/inclusive bounds, pre-release exclusion/inclusion, empty list failure, unparseable versions skipped, `IsValidRange` valid/invalid, deterministic output in `test/Nuplane.NuGet.Tests/NuGetVersionRangeEvaluatorTests.cs`
- [ ] T015 [P] [US1] Unit tests for `NuGetFeedVersionEnumerator`: version list SemVer-sorted ascending, empty feed returns empty list (not exception), feed errors propagate in `test/Nuplane.NuGet.Tests/NuGetFeedVersionEnumeratorTests.cs`
- [ ] T016 [P] [US1] Unit tests for `CachedFeedVersionEnumerator`: cache hit within TTL, cache miss after TTL expiry, `TimeSpan.Zero` disables caching, thread safety, `EnumeratedAt` reflects original enumeration timestamp, errors propagate (no stale data after TTL) in `test/Nuplane.Runtime.Tests/Versioning/CachedFeedVersionEnumeratorTests.cs`

### Implementation for User Story 1

- [ ] T017 [P] [US1] Implement `NuGetFeedVersionEnumerator` (`IFeedVersionEnumerator`) using `NuGet.Protocol`'s `FindPackageByIdResource.GetAllVersionsAsync`, convert `NuGetVersion` results to SemVer-sorted strings in `src/Nuplane.NuGet/NuGetFeedVersionEnumerator.cs`
- [ ] T018 [P] [US1] Implement `NuGetVersionRangeEvaluator` (`IVersionRangeEvaluator`) using `NuGet.Versioning.VersionRange.FindBestMatch` for explicit ranges and max stable filter for empty range (latest), `IsValidRange` via `VersionRange.TryParse` in `src/Nuplane.NuGet/NuGetVersionRangeEvaluator.cs`
- [ ] T019 [P] [US1] Implement `CachedFeedVersionEnumerator` decorator wrapping `IFeedVersionEnumerator` with `ConcurrentDictionary` cache keyed by `{feedName}:{lowercasePackageId}`, TTL from `FeedResolutionOptions.VersionCacheTtl` in `src/Nuplane.Runtime/Feeds/Versioning/CachedFeedVersionEnumerator.cs`
- [ ] T020 [P] [US1] Modify `FeedRuleDesiredSource` to use `IncludePatternParser.Parse()` for each `IncludePatterns` entry, emit `PackageRequest` with parsed `VersionRange` (empty string for no-version patterns), remove hardcoded `[1.0.0,)` default (FR-002) in `src/Nuplane.Runtime/Sources/FeedRuleDesiredSource.cs`
- [ ] T021 [P] [US1] Modify `MultiFeedPackageResolver` to invoke `IFeedVersionEnumerator` + `IVersionRangeEvaluator` for concrete version resolution before download, populate `FeedResolutionDecision.EnumeratedVersionCount` and `CacheHit`, add structured log entries for resolution outcomes (package ID, range, selected version, feed, duration, cache hit/miss) per OSR-004, publish a resolution outcome metric (counter by tag: success/failure/no-match) per OSR-004, preserve LKG on resolution failure per OSR-002 in `src/Nuplane.Runtime/Feeds/MultiFeedPackageResolver.cs`
- [ ] T022 [US1] Register version resolution services in DI: `NuGetFeedVersionEnumerator` as inner `IFeedVersionEnumerator`, `CachedFeedVersionEnumerator` as decorator, `NuGetVersionRangeEvaluator` as `IVersionRangeEvaluator`, with `ValidateOnStart()` for `FeedResolutionOptions` in `src/Nuplane/NuplaneServiceCollectionExtensions.cs`
- [ ] T023 [US1] Extend `FeedRuleDesiredSourceTests` to verify: patterns without version suffix emit empty `VersionRange`, patterns with wildcard and no version emit empty `VersionRange` in `test/Nuplane.Runtime.Tests/Sources/FeedRuleDesiredSourceTests.cs`
- [ ] T024 [US1] Extend `MultiFeedPackageResolverTests` with end-to-end latest version resolution test: mock `IFeedVersionEnumerator` returning multiple versions, verify highest stable is selected, `FeedResolutionDecision` populated with `EnumeratedVersionCount` and `CacheHit` in `test/Nuplane.Runtime.Tests/Feeds/MultiFeedPackageResolverTests.cs`

**Checkpoint**: Packages without version constraints now resolve to latest stable. MVP is functional and independently testable.

---

## Phase 4: User Story 2 — Explicit Version Range in Configuration (Priority: P1)

**Goal**: Operators can specify a version range in `IncludePatterns` (e.g., `"MyPackage [1.0.0, 2.0.0)"`) and Nuplane resolves the best matching version from the feed within those constraints

**Independent Test**: Configure `IncludePatterns: ["MyPackage [1.0.0, 2.0.0)"]`. Reconcile against a feed with versions `1.0.0`, `1.5.0`, `2.0.0`, `3.0.0`. Verify `1.5.0` is resolved.

### Tests for User Story 2 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T025 [P] [US2] Unit tests for `IncludePatterns` version range syntax validation: valid NuGet ranges pass, invalid syntax fails, empty range (no version) passes, bare version passes in `test/Nuplane.Runtime.Tests/Configuration/FeedResolutionOptionsValidatorTests.cs`
- [ ] T026 [P] [US2] Extend `FeedRuleDesiredSourceTests` for version range parsing: exact version `[2.0.0]`, bounded range `[1.0.0, 2.0.0)`, bare version `1.0.0`, wildcard with range `MyPackage.* [1.0.0,)` in `test/Nuplane.Runtime.Tests/Sources/FeedRuleDesiredSourceTests.cs`
- [ ] T027 [P] [US2] Extend `MultiFeedPackageResolverTests` for range-based resolution: bounded range selects best match, exact version resolves, no-match returns failure with diagnostic, bare version resolves in `test/Nuplane.Runtime.Tests/Feeds/MultiFeedPackageResolverTests.cs`

### Implementation for User Story 2

- [ ] T028 [US2] Extend `FeedResolutionOptionsValidator` to validate version range syntax in `IncludePatterns` entries using `IVersionRangeEvaluator.IsValidRange()`, reject invalid ranges at startup with descriptive error per FR-007 in `src/Nuplane/Options/Validation/FeedResolutionOptionsValidator.cs`

**Checkpoint**: Operators can pin versions or constrain ranges via configuration. Invalid syntax is caught at startup.

---

## Phase 5: User Story 3 — Reconciliation Updates to Latest Within Range (Priority: P2)

**Goal**: When a new version appears on the feed that satisfies the configured range (or no range = latest), the next reconciliation cycle resolves to the newer version

**Independent Test**: Configure `[1.0.0, 2.0.0)`. Reconcile (resolves `1.0.0`). Add `1.5.0` to feed. Reconcile again. Verify active version updated to `1.5.0`.

### Tests for User Story 3 ⚠️

- [ ] T029 [US3] Add reconciliation re-resolution tests: (1) updated feed with new version within range → resolves to newer version, (2) new version outside range → active version unchanged, (3) latest (no range) with newer version on feed → updates to newer version in `test/Nuplane.Runtime.Tests/Feeds/MultiFeedPackageResolverTests.cs`

**Checkpoint**: System stays current with feed updates within configured constraints.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Cleanup deprecated code, validate end-to-end scenarios

- [ ] T030 [P] Simplify or remove `NuGetVersionRangeParser.SelectVersion` (version selection now delegated to `Nuplane.NuGet` via `IVersionRangeEvaluator`) in `src/Nuplane.Runtime/Versioning/NuGetVersionRangeParser.cs`
- [ ] T031 Run `quickstart.md` validation scenarios: latest version resolution, pinned version `[x.y.z]`, bounded range `[x, y)`, bare version shorthand, cache TTL configuration, invalid syntax startup rejection, and verify existing `DirectoryNupkgDesiredSource` tests pass (FR-009 regression check)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **User Story 1 (Phase 3)**: Depends on Foundational phase completion
- **User Story 2 (Phase 4)**: Depends on User Story 1 completion (validator uses `IVersionRangeEvaluator` implemented in US1)
- **User Story 3 (Phase 5)**: Depends on User Story 1 completion (tests verify reconciliation behavior built in US1)
- **Polish (Phase 6)**: Depends on all user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational (Phase 2) — no user story dependencies
- **User Story 2 (P1)**: Depends on US1 (validator uses `IVersionRangeEvaluator` implementation, test assertions rely on resolution pipeline being operational)
- **User Story 3 (P2)**: Can start after US1 (tests verify reconciliation re-resolution; no new implementation code beyond US1/US2)

### Within Each User Story

- Tests MUST be written and FAIL before implementation
- Data records → interfaces → implementations → integration
- Modify existing components after new components are ready
- DI wiring after all implementations exist
- Extended integration tests after modifications

### Parallel Opportunities

- **Phase 2**: T004–T010 are all [P] (independent data records, interfaces, config extensions)
- **Phase 3**: T013–T016 (all unit tests) are [P]; T017–T021 (all different files) are [P]; T022 blocks on T017–T019
- **Phase 4**: T025–T027 (all test files) are [P]
- **Phase 6**: T030 is independent of T031

---

## Parallel Example: User Story 1

```text
# Batch 1 — Write all unit tests in parallel (all new files):
T013: IncludePatternParser tests       → test/Nuplane.Runtime.Tests/Versioning/IncludePatternParserTests.cs
T014: NuGetVersionRangeEvaluator tests → test/Nuplane.NuGet.Tests/NuGetVersionRangeEvaluatorTests.cs
T015: NuGetFeedVersionEnumerator tests → test/Nuplane.NuGet.Tests/NuGetFeedVersionEnumeratorTests.cs
T016: CachedFeedVersionEnumerator tests → test/Nuplane.Runtime.Tests/Versioning/CachedFeedVersionEnumeratorTests.cs

# Batch 2 — Implement all components in parallel (all different files):
T017: NuGetFeedVersionEnumerator      → src/Nuplane.NuGet/NuGetFeedVersionEnumerator.cs
T018: NuGetVersionRangeEvaluator      → src/Nuplane.NuGet/NuGetVersionRangeEvaluator.cs
T019: CachedFeedVersionEnumerator     → src/Nuplane.Runtime/Feeds/Versioning/CachedFeedVersionEnumerator.cs
T020: FeedRuleDesiredSource (modify)  → src/Nuplane.Runtime/Sources/FeedRuleDesiredSource.cs
T021: MultiFeedPackageResolver (modify) → src/Nuplane.Runtime/Feeds/MultiFeedPackageResolver.cs

# Batch 3 — DI wiring (depends on T017–T019):
T022: NuplaneServiceCollectionExtensions → src/Nuplane/NuplaneServiceCollectionExtensions.cs

# Batch 4 — Integration tests (depend on modifications):
T023: FeedRuleDesiredSourceTests (extend) → test/Nuplane.Runtime.Tests/Sources/FeedRuleDesiredSourceTests.cs
T024: MultiFeedPackageResolverTests (extend) → test/Nuplane.Runtime.Tests/Feeds/MultiFeedPackageResolverTests.cs
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (create Nuplane.NuGet + test project)
2. Complete Phase 2: Foundational (abstractions, records, config extensions)
3. Complete Phase 3: User Story 1 (latest version resolution pipeline)
4. **STOP and VALIDATE**: Packages without version constraints resolve to latest stable
5. Deploy/demo if ready — operators immediately get correct latest-version behavior

### Incremental Delivery

1. Setup + Foundational → Foundation ready
2. Add User Story 1 → Test independently → Deploy (MVP — fixes the core bug of defaulting to `1.0.0`)
3. Add User Story 2 → Test independently → Deploy (adds version pinning and range constraints)
4. Add User Story 3 → Test independently → Deploy (confirms reconciliation picks up feed updates)
5. Each story adds value without breaking previous stories

### Key Risk: FR-009 Regression

Directory-sourced packages (`DirectoryNupkgDesiredSource`) MUST continue using exact versions from `.nupkg` filenames. The version range resolution pipeline applies only to remote NuGet feeds. Existing `DirectoryNupkgDesiredSource` tests serve as regression guards — verify they pass after each phase.

---

## Notes

- [P] tasks = different files, no dependencies on incomplete tasks
- [Story] label maps task to specific user story for traceability
- Each user story is independently completable and testable
- NuGet SDK dependencies (`NuGet.Versioning`, `NuGet.Protocol`) are isolated in `Nuplane.NuGet` — core `Nuplane.Runtime` retains no direct NuGet SDK dependency
- `NuGetVersionRangeParser.SelectVersion` (existing) is superseded by `IVersionRangeEvaluator` — cleanup deferred to Polish phase
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
