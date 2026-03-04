# Implementation Plan: Test Backfill

**Branch**: `006-test-backfill` | **Date**: 2026-03-03 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/006-test-backfill/spec.md`

## Summary

Deliver the Phase F test backfill deferred by spec 005: (1) focused unit tests for each of the 9 middleware pipeline stages, (2) isolated unit tests for the 5 concretes that became testable after interface extraction, and (3) a new `Nuplane.Loading.Tests` project with 4 test classes covering assembly load/unload lifecycle. The one contract change required: `IDesiredStateAggregator.AggregateAsync` returns a new `DesiredAggregateResult` type that exposes per-source errors alongside the aggregated request list, enabling the error-isolation test case. All tests use xUnit with NSubstitute for mocking interfaces and verifying call order (see Decision 1).

## Technical Context

**Language/Version**: C# 13 / .NET 10  
**Primary Dependencies**: xUnit 2.9.3, NSubstitute 5.3.0, `Microsoft.NET.Test.Sdk`, `coverlet.collector` (all centrally managed via `Directory.Packages.props`)  
**Storage**: N/A for test code; `LockFileCoordinatorTests` uses `Path.GetTempFileName()` for transient JSON lock files  
**Testing**: xUnit with NSubstitute for mocking interfaces and verifying call order/arguments (see Decision 1)  
**Target Platform**: net10.0 (consistent with all other projects in the solution)  
**Project Type**: Test projects (xUnit) + one minimal fixture class library (`Nuplane.Loading.Tests.Fixtures`)  
**Performance Goals**: All new test classes complete in under 30 seconds; no live I/O or network access except the transient temp-file in `LockFileCoordinatorTests`  
**Constraints**: `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true` inherited from `test/Directory.Build.props`; zero warnings at first commit  
**Scale/Scope**: ~72 new test cases across 3 new source file groups; 2 new projects (`Nuplane.Loading.Tests`, `Nuplane.Loading.Tests.Fixtures`); 1 contract change (`IDesiredStateAggregator`)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **Deterministic reconciliation**: ✅ Not applicable — this spec introduces no reconciliation logic. All new tests are deterministic by design (OSR-001); no reconciliation paths are changed.
- **Transactional store safety**: ✅ Not applicable — no store transaction paths are added or modified. The `DesiredAggregateResult` contract change does not touch any store write path.
- **Source integrity**: ✅ Not applicable — no trusted-source boundaries, package validation steps, or credentials are introduced. Tests use in-memory stubs and temp files only (OSR-003).
- **Observability**: ✅ Not applicable for test-only additions. The `DesiredAggregateResult` change improves observability of source read failures (errors now surfaced, not swallowed) — this is additive and constitution §IV-compliant.
- **Test discipline**: ✅ This spec IS the test discipline delivery — all 19 FRs define new test files. The one contract change (FR-010, `DesiredAggregateResult`) includes its own test coverage by definition.
- **Decomposition discipline**: ✅ Each FR maps to exactly one test class file (or one new project file for FR-015). No FR conflates mechanism and driver. All configuration used in tests is consumed by the tests themselves — no orphan config.
- **Options validation discipline**: ✅ Not applicable — no new options types are introduced.

**Gate result**: PASS. No violations. Confirmed clear to proceed.

**Post-design re-evaluation**: The `DesiredAggregateResult` contract change (FR-010) was identified during Phase 0 research and is additive with respect to all constitution gates. It moves source-read failures from silent exception propagation to explicit `SourceErrors` exposure, improving §IV (Observability). The change is internal to `Nuplane.Runtime` — `IDesiredStateAggregator` does not live in `Nuplane.Abstractions` and has no external consumers. Gate confirmed PASS post-design.

## Project Structure

### Documentation (this feature)

```text
specs/006-test-backfill/
├── plan.md              ← this file
├── research.md          ← Phase 0 output
├── data-model.md        ← Phase 1 output
├── quickstart.md        ← Phase 1 output
└── tasks.md             ← Phase 2 output (/speckit.tasks command)
```

### Source Code Changes

```text
src/
└── Nuplane.Runtime/
    └── Reconciliation/
        ├── IDesiredStateAggregator.cs          ← contract change: return Task<DesiredAggregateResult>
        ├── DesiredStateAggregator.cs           ← implementation update: catch per-source, populate SourceErrors
        └── Models/
            └── DesiredAggregateResult.cs       ← NEW: record { Requests, SourceErrors }
    └── Middleware/
        └── DesiredStateReadMiddleware.cs       ← caller update: unpack DesiredAggregateResult

test/
├── Nuplane.Loading.Tests.Fixtures/
│   └── Nuplane.Loading.Tests.Fixtures.csproj  ← NEW: minimal class library, fixture DLL for ALC tests
│
├── Nuplane.Loading.Tests/
│   └── Nuplane.Loading.Tests.csproj           ← NEW: xUnit test project
│   ├── PackageLoaderTests.cs
│   ├── PackageUnloadCoordinatorTests.cs
│   ├── SharedAssemblyPolicyMatcherTests.cs
│   └── PackageAssemblyLoadContextTests.cs
│
└── Nuplane.Runtime.Tests/
    ├── Reconciliation/
    │   ├── DesiredStateAggregatorTests.cs      ← NEW
    │   ├── AllowlistGateTests.cs               ← NEW
    │   └── Middleware/
    │       ├── DesiredStateReadMiddlewareTests.cs
    │       ├── PackageResolutionMiddlewareTests.cs
    │       ├── TrustAndLockGateMiddlewareTests.cs
    │       ├── PackageLoadingMiddlewareTests.cs
    │       ├── DiffAndChangeEventMiddlewareTests.cs
    │       ├── TransactionExecutionMiddlewareTests.cs
    │       ├── UnloadMiddlewareTests.cs
    │       ├── CleanupMiddlewareTests.cs
    │       └── HealthAndMetricsMiddlewareTests.cs
    ├── LockFile/
    │   └── LockFileCoordinatorTests.cs         ← NEW
    ├── Packages/
    │   └── PackageCleanupServiceTests.cs       ← NEW (resides in Nuplane.Store.Tests or Nuplane.Runtime.Tests — see note)
    └── Sources/
        └── DesiredSourceSnapshotCacheTests.cs  ← NEW
```

> **Note on PackageCleanupServiceTests placement**: `PackageCleanupService` lives in `src/Nuplane.Store/`. Tests for it should reside in `test/Nuplane.Store.Tests/` not `Nuplane.Runtime.Tests/`. FR-013's path `Packages/PackageCleanupServiceTests.cs` refers to `test/Nuplane.Store.Tests/Packages/PackageCleanupServiceTests.cs`.

**Structure Decision**: Additive changes only. Two new projects are added to the solution. All new test namespaces align with the source namespaces they test. No source project structure changes beyond the `DesiredAggregateResult` model addition and `IDesiredStateAggregator` interface update.

## Complexity Tracking

No constitution violations. No complexity justification required.
