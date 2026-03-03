# Quickstart — Phase 3 Optional Package Loading

## Goal
Validate optional package loading, strong-identity shared assembly behavior, bounded deactivation + best-effort unload lifecycle, retry-on-cycle semantics for `UnloadPending`, and degraded health/observability outcomes.

## Preconditions
- .NET 8 SDK installed.
- Feature branch checked out: `003-phase3-assembly-loading`.
- Deterministic package store available with at least two active packages.
- Loading feature explicitly enabled in host configuration.
- Shared assembly policy configured with strong identity (`name`, `publicKeyToken`, `majorVersion`) for `Nuplane.Abstractions` (or equivalent contract assembly).
- Deactivation timeout configured to a finite value.

## Validation Profile

- Profile name: `phase3-loading-baseline`.
- Dataset: 20 active packages with valid dependencies, including 5 with overlapping dependency names and 2 with shared-contract references.
- Cycle window: 10 consecutive reconciliation cycles with identical desired/active inputs.
- Failure injection: at least 5 controlled failures spanning load failures, unload failures, and deactivation timeouts.

## Verification command set

Run from repository root:

```bash
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj \
	--filter "FullyQualifiedName~PackageLoadingSessionTests|FullyQualifiedName~SharedAssemblyPolicyTests|FullyQualifiedName~UnloadPendingRetryTests|FullyQualifiedName~LoadingHealthProjectionTests"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj \
	--filter "FullyQualifiedName~PackageLoadingContractTests|FullyQualifiedName~SharedAssemblyPolicyContractTests|FullyQualifiedName~UnloadLifecycleContractTests"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj \
	--filter "FullyQualifiedName~DeactivationTimeoutContinuationTests|FullyQualifiedName~LoadFailureIsolationTests|FullyQualifiedName~RepeatedCycleIdempotenceTests"
dotnet test nuplane.sln
./build/validate-secrets.sh
```

## 1) Optional loading enablement
1. Start with loading disabled; run a reconciliation cycle.
2. Verify no load sessions are created.
3. Enable loading and rerun cycle.
4. Verify active packages are loaded from active store paths and outcomes are reported.

## 2) Shared assembly strong-identity behavior
1. Configure shared policy entry with matching `name`, `publicKeyToken`, and `majorVersion`.
2. Run load cycle; verify shared contract assembly resolves from host context.
3. Alter token or major version to mismatch.
4. Verify shared policy no longer matches and package-local resolution path is used.

## 3) Removal unload lifecycle with bounded timeout
1. Remove one package from desired state.
2. Simulate deactivation timeout.
3. Verify unload attempt still executes and timeout outcome is logged.
4. Verify package enters `UnloadPending` when unload cannot complete.

## 4) Retry and recovery behavior
1. Keep an unload-blocking reference active for the removed package.
2. Run multiple cycles and verify unload retry occurs each cycle.
3. Release the blocking reference and run next cycle.
4. Verify package transitions to `Unloaded` and pending count decreases.

## 5) Health and observability
1. While any package is `UnloadPending`, verify health reports `Degraded`.
2. Verify structured logs include package identity, correlation ID, load/unload outcome, and reason code.
3. Verify metrics include load success/failure counts, unload pending gauge, and timeout outcome counts.
4. After all pending unloads complete, verify health returns to `Healthy`.

## Expected Test Evidence
- Unit tests for load session lifecycle, strong-identity policy matching, timeout handling, and retry transitions.
- Integration/contract tests for loading boundary, shared policy boundary, and unload lifecycle boundary.
- Regression tests for non-blocking partial failures and idempotent repeated cycles.

## Expected command outcomes
- All targeted test commands pass with 0 failed tests.
- Full solution test pass (`dotnet test nuplane.sln`).
- Secret validation script reports no committed credentials.

## Success Criteria Validation Checks
1. Calculate per-cycle load success ratio under `phase3-loading-baseline` and verify `SC-001` threshold (>=99%) is met.
2. For injected failures, verify observer callbacks and correlation-linked logs/metrics/health identify package and failure cause for 100% of cases (`SC-004`).
