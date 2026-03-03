# Feature Specification: Architecture & Code Quality Review

**Feature Branch**: `005-architecture-code-quality-review`
**Created**: 2026-03-03
**Status**: ✅ Complete — retroactively documented

---

## Overview

A full architecture and code quality review of the Nuplane solution (`src/` projects), identifying actionable improvements across four tiers:

1. Architecture improvements
2. Code quality & file organisation
3. Infrastructure improvements
4. Testing recommendations

---

## User Scenarios & Testing

### User Story 1 — Clean, Idiomatic Architecture (Priority: P1)

As a contributor or future maintainer, the codebase should be composable, testable, and follow idiomatic .NET patterns so that future changes can be made safely and quickly.

**Acceptance Scenarios**:

1. **Given** the existing god-class `ReconciliationService`, **When** it is decomposed into a middleware pipeline, **Then** each stage is independently testable and the orchestrator is a thin coordinator.
2. **Given** sealed concrete classes with no interfaces, **When** interfaces are extracted and registered in DI, **Then** unit tests can mock any dependency in isolation.
3. **Given** DRY violations (duplicated `VersionKey`, triplicated `SelectVersion`), **When** shared types are promoted, **Then** a single canonical implementation exists in each case.

---

### User Story 2 — Safe, Standard Configuration Validation (Priority: P1)

As a library consumer, configuration errors should be detected at startup with clear, actionable messages, using idiomatic .NET options validation patterns.

**Acceptance Scenarios**:

1. **Given** options classes with embedded `IsValid()` methods, **When** they are replaced with `IValidateOptions<T>` validators, **Then** options classes remain data-only and validation is composable.
2. **Given** `ValidateOnStart()` is wired for all required options, **When** invalid configuration is provided, **Then** the host fails to start with a structured error message before processing any requests.

---

### User Story 3 — Observable, Debuggable Runtime (Priority: P2)

As an operator, structured logs and distributed trace correlation should integrate with standard .NET observability infrastructure.

**Acceptance Scenarios**:

1. **Given** a custom in-memory `ReconciliationLogger`, **When** it is replaced with `ILogger<T>` and `[LoggerMessage]`, **Then** logs route to any configured `ILoggerFactory` sink.
2. **Given** `AsyncLocal<string?>` correlation, **When** `System.Diagnostics.Activity` / `ActivitySource` is integrated, **Then** correlation IDs participate in W3C trace context and OpenTelemetry pipelines.

---

### User Story 4 — Full XML Documentation (Priority: P2)

As a library consumer, all public types and members should have XML documentation so that IntelliSense and generated docs provide useful guidance.

**Acceptance Scenarios**:

1. **Given** `<GenerateDocumentationFile>true</GenerateDocumentationFile>` and `TreatWarningsAsErrors`, **When** the solution builds, **Then** zero CS1591 (missing XML doc) warnings are produced.

---

## Requirements

### Tier 1 — Architecture Improvements

- **FR-001**: `ReconciliationService` MUST be decomposed into a `ReconciliationPipeline` with 9 discrete middleware stages (each in its own file in `Reconciliation/Middleware/`). `ReconciliationService` becomes a thin orchestrator that invokes the pipeline. A `ReconciliationCycleContext` data bag is passed through all stages.
- **FR-002**: Shared `VersionKey` MUST be promoted to `Nuplane.Runtime/Versioning/VersionKey.cs`; duplicate copies in `DesiredActualDiffEngine.cs` and `FeedResolutionPolicy.cs` MUST be removed.
- **FR-003**: Shared `NuGetVersionRangeParser.SelectVersion()` MUST be extracted to `Nuplane.NuGet/Versioning/NuGetVersionRangeParser.cs`; triplicated copies MUST be removed.
- **FR-004**: The simpler `Nuplane.NuGet` `MultiFeedPackageResolver` MUST be deleted; the `Nuplane.Runtime` version is canonical.
- **FR-005**: `I`-prefixed interfaces MUST be extracted for all sealed concrete dependency classes registered in DI. `ReconciliationService` and its future middleware stages MUST depend on interfaces, not concretes.
- **FR-006**: `ObserverNotifier` and `PackageChangeEventPublisher` MUST be consolidated into a single `ObserverEventDispatcher` with three methods: `PublishChangingAsync`, `PublishChangedAsync`, `NotifyPackageFailedAsync`.
- **FR-007**: `ReconciliationHealthEvaluator` MUST replace its 4-overload chain with a single `Evaluate(ReconciliationHealthInput input)` method.
- **FR-008**: `INuGetPackageResolver` MUST be moved to `Nuplane.Abstractions` as `IPackageResolver`, decoupling `Nuplane.Runtime` from `Nuplane.NuGet` at compile time.
- **FR-009**: `StoreRegistryOptions` MUST be introduced; `StoreRegistry` MUST be injected via DI rather than manually constructed.

### Tier 2 — Code Quality & File Organisation

- **FR-010**: Multi-type files MUST be split to one-type-per-file. All public types that are not tightly coupled result types of a single class MUST live in their own file.
- **FR-011**: `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` MUST be set in root `Directory.Build.props` and all resulting warnings MUST be resolved.
- **FR-012**: `<GenerateDocumentationFile>true</GenerateDocumentationFile>` MUST be set in `src/Directory.Build.props` and all public types and members MUST have XML doc comments (`<summary>`, `<param>`, `<returns>`, `<exception>`).
- **FR-013**: The abandoned `src/Nuplane.Hosting.Loading/` directory (no `.csproj`, no source, not in solution) MUST be deleted.
- **FR-014**: Enum placement convention MUST be documented in `docs/coding-conventions.md`.

### Tier 3 — Infrastructure Improvements

- **FR-015**: `ReconciliationLogger` MUST wrap `ILogger<T>` with `[LoggerMessage]` source-generated structured log methods. An in-memory capture path MUST be retained for test assertions.
- **FR-016**: `CorrelationContext` MUST integrate with `System.Diagnostics.Activity` / `ActivitySource`. When a listener is active, a named `Activity` is started per cycle. When no listener is registered, the existing `AsyncLocal<string?>` fallback continues to work.
- **FR-017**: `CancellationToken` MUST be forwarded consistently inside all `foreach` loop bodies across the solution (specifically: `PackageCleanupService`, `DesiredStateAggregator`, `PackageApplyExecutor`, `ReconciliationService`).
- **FR-018**: `ReconciliationRetryPolicy` pass-through methods MUST be removed; callers use `ExecuteAsync` directly.

### Tier 4 — Testing Recommendations (Deferred)

- **FR-019**: Focused unit tests MUST be written for each middleware stage handler after Phase C stabilises (tracked in follow-on spec `006-test-backfill`).
- **FR-020**: Isolated unit tests with mocked interfaces MUST be written for previously untestable concretes (tracked in `006-test-backfill`).
- **FR-021**: A `test/Nuplane.Loading.Tests/` project MUST be created with tests for assembly load context, policy matching, and unload lifecycle (tracked in `006-test-backfill`).

### Operational & Safety Requirements

- **OSR-001**: All changes MUST keep the build green with zero warnings.
- **OSR-002**: All existing tests (52+) MUST continue to pass after each phase.
- **OSR-003**: No public API surface changes without a semantic version impact statement.
- **OSR-004**: Options validation MUST use the .NET options pipeline (`IValidateOptions<T>` with `ValidateOnStart()`); `IsValid()` methods on options classes are prohibited. *(See also FR-022 below.)*
- **OSR-005**: Lessons identified during this review MUST be encoded in the project Constitution and spec/plan/tasks templates to prevent recurrence.

### Cross-Cutting Requirements (emerged during execution)

- **FR-022**: Options validation MUST be migrated from `IsValid()` instance methods to `IValidateOptions<T>` validators with `ValidateOnStart()` across all options types. Options classes MUST remain data-only. *(Codified in Constitution §VII; OSR-012 added to spec 001.)*
- **FR-023**: The Constitution MUST be updated with §VI (Specification & Task Decomposition Discipline) and §VII (Options Validation Pipeline Discipline). Dependent spec/plan/tasks templates MUST be updated to reflect both principles.

---

## Success Criteria

- **SC-001**: `dotnet build Nuplane.sln` produces 0 errors and 0 warnings with `TreatWarningsAsErrors=true`.
- **SC-002**: `dotnet test Nuplane.sln` passes all 52+ tests with 0 failures.
- **SC-003**: No type in any `src/` project exposes an `IsValid()` method for options validation.
- **SC-004**: Every sealed concrete service class registered in DI has a corresponding `I`-prefixed interface.
- **SC-005**: `ReconciliationService.TriggerManualAsync` delegates to a `ReconciliationPipeline` composed of discrete middleware stages.
- **SC-006**: `VersionKey` exists in exactly one location (`Nuplane.Runtime/Versioning/`); `NuGetVersionRangeParser` exists in exactly one location (`Nuplane.NuGet/Versioning/`).
- **SC-007**: All public types and members in `src/` have XML doc `<summary>` comments and the documentation file is generated on build.
- **SC-008**: Constitution is at v1.2.0 or higher and includes both §VI and §VII.

