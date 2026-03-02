# Quickstart Validation Evidence — Phase 2 Advanced Feeds & Governance

**Date**: 2026-03-02  
**Branch**: `002-phase2-feed-governance`

## Command execution results

### 1. US1/US2/US3 runtime targeted tests
- Command: `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~MultiFeedResolutionPolicyTests|FullyQualifiedName~MultiFeedTieBreakRegressionTests|FullyQualifiedName~MultiFeedRetryPolicyTests|FullyQualifiedName~FeedTrustPolicyEvaluatorTests|FullyQualifiedName~FeedRuleDesiredSourceTests"`
- Result: **Passed** (10/10)

### 2. US1 integration targeted tests
- Command: `dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~FeedResolutionContractTests|FullyQualifiedName~StrictFeedOutageIsolationTests|FullyQualifiedName~MultiFeedRetryExhaustionTests"`
- Result: **Passed** (4/4)

### 3. US2 integration targeted tests
- Command: `dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~TrustPolicyContractTests|FullyQualifiedName~LockFileEnforceModeTests|FullyQualifiedName~LockFileStrictModeTests"`
- Result: **Passed** (3/3)

### 4. US3 integration targeted tests
- Command: `dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~FeedRuleDryRunParityTests|FullyQualifiedName~FeedRuleMaxLimitTests|FullyQualifiedName~CleanupExecutionModeTests"`
- Result: **Passed** (3/3)

### 5. US2/US3 store targeted tests
- Command: `dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj --filter "FullyQualifiedName~CleanupPolicyUnionRetentionTests|FullyQualifiedName~CleanupLkgProtectionRegressionTests|FullyQualifiedName~LockHashMismatchLkgRegressionTests"`
- Result: **Passed** (3/3)

### 6. Full regression
- Command: `dotnet test nuplane.sln`
- Result: **Passed** (38/38)
- Note: One existing warning remained in `Nuplane.NuGet.Tests` test discovery (`No test is available ... Nuplane.NuGet.Tests.dll`).

### 7. Secret validation gate
- Command: `./build/validate-secrets.sh`
- Result: **Passed** (`OK - no committed source credentials detected.`)

## Conclusion
Phase 2 quickstart verification completed successfully. Multi-feed determinism, trust/override governance, lock modes/integrity boundaries, feed-rule dry-run behavior, and cleanup retention/LKG safety passed targeted and full-regression validation, and credential handling checks passed with the updated secret-scan rules.
