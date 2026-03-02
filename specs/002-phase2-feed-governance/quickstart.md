# Quickstart — Phase 2 Advanced Feeds & Governance

## Goal
Validate deterministic multi-feed behavior, trust/override governance, lock-file reproducibility modes, controlled feed-rule discovery with dry-run, and cleanup retention safety.

## Preconditions
- .NET 8 SDK installed.
- Feature branch checked out: `002-phase2-feed-governance`.
- At least three configured feeds with deterministic priority values.
- Trust levels configured for feeds (`Trusted`, `Restricted`, `Untrusted`).
- Restricted validator pipeline configured.
- Lock file path configured for generate/enforce/strict modes.
- Feed rules configured with prefix filters and hard max package limits.
- Cleanup policy configured with LKG protection enabled.

## Verification command set

Run from repository root:

```bash
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~MultiFeedResolution|FullyQualifiedName~FeedTrustPolicy"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~MultiFeedDeterminism|FullyQualifiedName~StrictFeedOutageIsolation"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~LockFileEnforceMode|FullyQualifiedName~LockFileStrictMode|FullyQualifiedName~LockHashMismatch"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~FeedRuleDryRun|FullyQualifiedName~FeedRuleMaxLimit"
dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj --filter "FullyQualifiedName~CleanupRetentionPolicy|FullyQualifiedName~LkgProtection"
dotnet test nuplane.sln
./build/validate-secrets.sh
```

## 1) Deterministic multi-feed resolution
1. Configure overlapping package availability across multiple feeds.
2. Run repeated cycles with unchanged inputs.
3. Verify selected feed/version is identical for each package across all runs.
4. Verify tie-break uses lexicographically smallest feed name when priority+version are equal.

## 2) Strict mode outage scope
1. Enable strict mode.
2. Simulate outage of one required feed.
3. Verify only packages requiring that feed fail explicitly; unrelated packages continue.
4. Verify degraded health and correlated diagnostics are emitted.

## 3) Trust policy and override auditability
1. Attempt install from restricted feed with validator failure.
2. Verify package is blocked and policy failure is recorded.
3. Attempt install from untrusted feed without override; verify block.
4. Add scoped per-package or per-feed-rule override with reason; verify policy pass and reason appears in diagnostics.

## 4) Lock-file modes and integrity
1. Generate lock file from a known successful cycle.
2. Enable enforce mode and change available feed versions.
3. Verify lock-defined versions still resolve and activate.
4. Enable strict mode and remove one required lock entry; verify explicit package failure.
5. Inject hash mismatch; verify activation fails and active state remains safe.

## 5) Feed-rule dry-run and limits
1. Configure feed rule with prefix and max package limit.
2. Execute dry-run cycle.
3. Verify full policy/validator/lock checks execute, outcomes are reported, and no state mutation occurs.
4. Verify max package limit is enforced deterministically.

## 6) Cleanup retention and LKG protection
1. Configure both retention count and age thresholds.
2. Run successful reconciliation to trigger automatic cleanup.
3. Verify versions satisfying either retention rule are kept.
4. Verify LKG versions are never deleted.
5. Inject cleanup deletion failure; verify runtime state remains stable and diagnostics capture failure.

## Expected Test Evidence
- Unit tests for deterministic feed ordering and trust policy transitions.
- Integration/contract tests for lock modes, dry-run parity, feed-rule output boundaries, and strict outage behavior.
- Store tests for retention union semantics and LKG deletion protection.
- Regression tests for hash mismatch and scoped override audit behavior.

## Expected command outcomes
- All targeted test commands pass with 0 failed tests.
- Full solution test pass (`dotnet test nuplane.sln`).
- Secret validation script reports no committed credentials.
