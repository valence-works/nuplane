# Quickstart — Phase 4 Cluster-Convergent Runtime Loading (Lean)

## Goal

Validate deterministic convergence, safe transactional apply, optional loader integration, and optional admin operations for Phase 4.

## Preconditions

- Repo root: `nuplane/main`
- Branch: `004-phase4-operational-enhancements`
- .NET SDK capable of building repo targets (`net8.0`, `net9.0`, `net10.0`)
- Two runnable host instances (samples or equivalent host apps)

## Validation Profile

- Profile: `phase4-convergent-loading-baseline`
- Replicas: 2+
- Desired input: shared manifest with exact package versions
- Determinism window: 20 unchanged cycles
- Failure injections: manifest invalid, source outage, package acquisition failure, loader failure, manual trigger unavailable/rejected

## Command Set

Run from repository root:

```bash
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter "FullyQualifiedName~DesiredManifest|FullyQualifiedName~DesiredAggregation|FullyQualifiedName~LoaderBoundary|FullyQualifiedName~OperationalSnapshot"
dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj --filter "FullyQualifiedName~ManifestConvergence|FullyQualifiedName~DesiredSourceOutageIsolation|FullyQualifiedName~ManualReconcile|FullyQualifiedName~LoaderFailureIsolation"
dotnet test Nuplane.sln
./build/validate-secrets.sh
```

## Scenario 1: Deterministic manifest projection

1. Provide shared manifest with exact versions.
2. Start two replicas with identical desired-source config.
3. Execute repeated cycles without changing inputs.
4. Verify both replicas compute identical requested package set and no-op/idempotent apply outcomes.

## Scenario 2: Manifest update to new exact version

1. Upload package artifact first.
2. Update manifest last to point to new exact version.
3. Trigger manual reconcile or wait one polling interval.
4. Verify both replicas eventually activate new version while preserving transactional semantics.

## Scenario 3: Multi-source deterministic aggregation and outage isolation

1. Configure at least two desired sources (manifest + directory/feed-like source).
2. Introduce duplicate package ID across sources.
3. Verify deterministic duplicate winner and reason codes.
4. Take one source offline.
5. Verify degraded, non-mutating behavior for impacted scope with unchanged unrelated active packages.

## Scenario 4: Optional loader boundary behavior

1. Enable loader boundary and activate package containing known type.
2. Verify type load succeeds through loader boundary.
3. Inject load failure for one package.
4. Verify host remains alive, failure is scoped to package, and observer event/log/metric signals are emitted.

## Scenario 5: Optional admin operational surface

1. Query operational snapshot endpoint/service.
2. Verify active set + last reconcile + health are internally consistent.
3. Trigger manual reconcile through admin boundary.
4. Verify outcome appears in logs/metrics/health/snapshot with matching correlation context.
5. Exercise rejected/unavailable trigger path and verify explicit non-mutating outcome code.

## Expected Evidence

- Correlation-linked structured logs for manifest/source/acquisition/loader/admin operations.
- Metrics baseline covering failures by stage/reason and cycle outcomes.
- Health transitions (`Healthy` <-> `Degraded`) matching injected faults and recovery.
- Observer events emitted for each failure class with scoped target and reason code.

## Success Criteria Mapping

- `SC-001`: all replicas converge within poll interval + retry window under healthy sources.
- `SC-002`: no store corruption/LKG violation across injected failure matrix.
- `SC-003`: admin read+trigger end-to-end timing meets 95/100 within 120s in validation run.
