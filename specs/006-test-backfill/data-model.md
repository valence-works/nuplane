# Data Model: Test Backfill

**Branch**: `006-test-backfill` | **Date**: 2026-03-03

This spec introduces one new production type and no new entities in the store or persistence layer.

---

## New Type: DesiredAggregateResult

**Location**: `src/Nuplane.Runtime/Reconciliation/Models/DesiredAggregateResult.cs`
**Kind**: `record` (immutable value type)

| Field | Type | Description |
|-------|------|-------------|
| `Requests` | `IReadOnlyList<PackageRequest>` | Deterministically ordered aggregated requests from all healthy sources. Same content previously returned directly from `AggregateAsync`. |
| `SourceErrors` | `IReadOnlyDictionary<string, Exception>` | Keyed by source type name (same key as used in `DesiredStateReadMiddleware` error recording). Empty when all sources succeed. Never null. |

**Validation rules**: None — purely a data container. `Requests` and `SourceErrors` are both non-null by record construction convention.

**State transitions**: Not stateful. Created once per `AggregateAsync` invocation, consumed immediately by callers.

---

## Contract Change: IDesiredStateAggregator

**Location**: `src/Nuplane.Runtime/Reconciliation/IDesiredStateAggregator.cs`

| Before | After |
|--------|-------|
| `Task<IReadOnlyList<PackageRequest>> AggregateAsync(...)` | `Task<DesiredAggregateResult> AggregateAsync(...)` |

**Callers affected**:
- `DesiredStateAggregator.AggregateAsync` — implementation updated; catch per-source, populate `SourceErrors`
- `DesiredStateReadMiddleware` — unpacks `result.Requests` for `context.DesiredRequests`; logs/records source errors from `result.SourceErrors`

---

## New Projects (no new data entities)

| Project | Kind | Purpose |
|---------|------|---------|
| `test/Nuplane.Loading.Tests.Fixtures/` | Class library (`net10.0`) | Provides `FixtureMarker` (minimal exportable type) so tests can resolve a real DLL path without hardcoding |
| `test/Nuplane.Loading.Tests/` | xUnit test project (`net10.0`) | Unit tests for `Nuplane.Loading` and `Nuplane.Loading.Abstractions` |

---

## No New Entities

All other additions in this spec are test classes — they introduce no production entities, no store schema changes, and no new configuration properties.
