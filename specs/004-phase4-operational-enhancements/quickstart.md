# Quickstart — Phase 4 Operational Enhancements

## Goal
Validate Phase 4 behavior for channel isolation, explicit staged promotion, deterministic canary rollout selection, integrity enforcement gates, optional admin operational surface, and non-mutating failure handling.

## Preconditions
- .NET 8 SDK installed.
- Feature branch checked out: `004-phase4-operational-enhancements`.
- Baseline reconciliation/store behavior from Phases 1–3 passing in local environment.
- Distinct channel configurations for `prod`, `staging`, and `canary`.
- Deterministic node identity inputs available for canary selection tests.
- Integrity rules configured for at least one required verification in enforce mode.

## Validation Profile
- Profile name: `phase4-operational-governance-baseline`.
- Channels: 3 (`prod`, `staging`, `canary`) with disjoint desired package sets.
- Package set: 30 total packages, including staged candidates and mixed compliance for integrity checks.
- Node set: 50 eligible nodes for canary channel validation.
- Cycle window: 20 identical-input cycles for determinism checks.
- Failure injection: channel misconfiguration, promotion failure, integrity failure, admin trigger unavailable.

## Verification command set
Run from repository root:

```bash
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj \
  --filter "FullyQualifiedName~ChannelIsolation|FullyQualifiedName~StagedPromotion|FullyQualifiedName~CanarySelectionDeterminism|FullyQualifiedName~IntegrityActivationGate"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj \
  --filter "FullyQualifiedName~ChannelRolloutContract|FullyQualifiedName~CanarySelectionContract|FullyQualifiedName~IntegrityAdminContract"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj \
  --filter "FullyQualifiedName~PromotionFailureIsolation|FullyQualifiedName~ChannelMisconfigurationDegraded|FullyQualifiedName~ManualReconcileObservability"
dotnet test Nuplane.sln
./build/validate-secrets.sh
```

## 1) Channel isolation and misconfiguration handling
1. Configure disjoint desired package sets per channel.
2. Run reconciliation with `prod` selected.
3. Verify only `prod`-scoped packages are evaluated/activated.
4. Select a channel with empty desired sources.
5. Verify cycle is non-mutating and health reports degraded with explicit misconfiguration reason.

## 2) Staging and explicit promotion
1. Introduce a newer package version with staging enabled.
2. Verify candidate is staged and remains inactive.
3. Issue explicit operator promotion request.
4. Verify atomic active switch, LKG preservation, and promoted state.
5. Inject promotion failure for one package/node.
6. Verify current active remains unchanged, candidate marked failed, and unrelated operations continue.

## 3) Deterministic canary rollout
1. Configure canary rollout with rollout ID, eligible nodes, and target percentage.
2. Run repeated cycles with identical inputs.
3. Verify selected node set is identical across cycles.
4. Increase target percentage.
5. Verify deterministic expansion of selected nodes and no activation on non-eligible nodes.

## 4) Integrity enforcement
1. Configure enforce-mode integrity rules.
2. Attempt activation with mixed compliant/non-compliant package set.
3. Verify compliant packages remain eligible.
4. Verify non-compliant packages are blocked with explicit policy-failure outcomes and non-mutating active state.

## 5) Optional admin surface
1. Read package inventory, state, and health snapshot through admin surface.
2. Verify snapshot consistency for active/staged/reconcile outcome data.
3. Trigger manual reconciliation.
4. Verify outcome is observable via logs/metrics/snapshot changes.

## Expected evidence
- Correlation-linked logs for channel scope, staging/promotion, canary selection, integrity outcomes, and admin trigger outcomes.
- Metrics for staged/promoted counts, canary-selected nodes, integrity failures, and degraded-cycle reasons.
- Health state transitions demonstrating degraded behavior for channel misconfiguration and recovery when resolved.

## Success Criteria Validation Checks
1. Validate channel isolation for 100% of activation actions across 20 cycles (`SC-001`).
2. Validate staged inactivity until explicit promotion and atomic promoted switch with fallback on failure (`SC-002`).
3. Validate deterministic canary selection and zero non-eligible activations (`SC-003`).
4. Validate all non-compliant packages are blocked while compliant packages remain eligible (`SC-004`).
5. Validate operator read+trigger workflows complete within 2 minutes in at least 95% of acceptance runs (`SC-005`).
