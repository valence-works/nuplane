# Quickstart — Phase 1 Runtime Baseline

## Goal
Validate deterministic reconciliation, transactional activation with LKG fallback, source outage behavior, and observability signals for the Phase 1 baseline.

## Preconditions
- .NET 8 SDK installed.
- Feature branch checked out: `001-phase1-runtime-baseline`.
- A configured local store root path.
- One configured trusted feed.
- One configured directory desired source.
- Package ID allowlist configured.

## Verification command set

Run from repository root:

```bash
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~DesiredStateReconciliationTests"
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~DesiredActualDiffEngineTests"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~SourceOutageFallbackTests|FullyQualifiedName~PartialFailureIsolationTests|FullyQualifiedName~RetryExhaustionTests"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~ObserverContractTests|FullyQualifiedName~HealthRecoveryTests"
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~ObserverIsolationTests"
dotnet test nuplane.sln
./build/validate-secrets.sh
```

## 1) Baseline successful cycle
1. Configure explicit desired package requests and run one manual reconciliation trigger.
2. Verify:
   - Add/update/remove diff is computed deterministically.
   - Package transactions complete and `state.json` is persisted.
   - `OnPackagesChangingAsync` then `OnPackagesChangedAsync` events are emitted.
   - Correlation ID is present in logs/metrics/events.

## 2) Duplicate desired ID resolution
1. Provide duplicate package IDs from multiple desired inputs for a cycle.
2. Verify selected package follows highest-version-wins with source-name tie-break when versions match.

## 3) Source outage fallback
1. Run a successful cycle to establish source snapshot.
2. Simulate source unavailability for one desired source.
3. Verify:
   - Last successful snapshot is reused for that source.
   - Cycle continues (no host crash).
   - Health reports degraded.

## 4) Transaction failure and LKG
1. Inject a failure at `validate` or `publish` stage for one package.
2. Verify:
   - Active pointer remains on last-known-good package version.
   - Failure record includes stage/message/timestamp/correlationId.
   - Other package transactions continue per cycle policy.

## 5) Health recovery
1. From degraded state, restore source availability and remove injected transaction failures.
2. Run a fully successful cycle with fresh reads for all configured sources.
3. Verify health transitions back to healthy.

## 6) Single-flight enforcement
1. Trigger overlapping manual reconciliation requests while one cycle is in progress.
2. Verify only one cycle runs; additional triggers are skipped and logged.

## Expected Test Evidence
- Unit tests for deterministic diff, duplicate resolution, and retry/backoff boundaries.
- Integration/contract tests for runtime-store, runtime-nuget, and runtime-source boundaries.
- Regression tests for LKG fallback and degraded-to-healthy transition semantics.

## Expected command outcomes
- All `dotnet test` commands succeed with 0 failed tests.
- `dotnet test nuplane.sln` passes full regression.
- `./build/validate-secrets.sh` reports no potential committed credentials.
