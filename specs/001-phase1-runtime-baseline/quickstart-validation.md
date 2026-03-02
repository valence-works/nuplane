# Quickstart Validation Evidence — Phase 1 Runtime Baseline

**Date**: 2026-03-02  
**Branch**: `001-phase1-runtime-baseline`

## Command execution results

### 1. Baseline/idempotent reconciliation
- Command: `dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~DesiredStateReconciliationTests"`
- Result: **Passed** (1/1)

### 2. Deterministic diff + duplicate resolution
- Command: `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~DesiredActualDiffEngineTests"`
- Result: **Passed** (2/2)

### 3. Source outage fallback + partial isolation + retry exhaustion
- Command: `dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~SourceOutageFallbackTests|FullyQualifiedName~PartialFailureIsolationTests|FullyQualifiedName~RetryExhaustionTests"`
- Result: **Passed** (3/3)

### 4. Observer contract + health recovery fresh-read rule
- Command: `dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~ObserverContractTests|FullyQualifiedName~HealthRecoveryTests"`
- Result: **Passed** (2/2)

### 5. Observer exception isolation
- Command: `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~ObserverIsolationTests"`
- Result: **Passed** (1/1)

### 6. Full regression
- Command: `dotnet test nuplane.sln`
- Result: **Passed** (19/19)

### 7. Secret validation gate
- Command: `./build/validate-secrets.sh`
- Result: **Passed** (`OK - no committed source credentials detected.`)

### 8. Central package version verification
- Command: `grep -RInE "PackageReference\s+Include=.*Version=" --include='*.csproj' .`
- Result: **No inline versions found**.

## Conclusion
Phase 1 quickstart verification completed successfully. Runtime behavior and cross-cutting release checks (tests, dependency centralization, and secret scanning) passed.