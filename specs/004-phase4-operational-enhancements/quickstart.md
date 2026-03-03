# Quickstart — Phase 4 Cluster-Convergent Runtime Loading (Lean)

## Goal

Validate Phase 4 behavior for:

- deterministic desired manifest input (exact versions)
- deterministic multi-source aggregation
- startup + polling reconciliation plus explicit trigger
- optional loader boundary integration
- optional admin operational surface
- non-mutating, LKG-preserving failure handling

## Preconditions

- .NET 8 SDK installed.
- Feature branch checked out: `004-phase4-operational-enhancements`.
- Baseline reconciliation/store behavior from Phases 1–3 passing locally.

## Validation Profile

- Profile name: `phase4-convergent-loading-baseline`.
- Replicas: 2+ host instances pointing at the same desired manifest.
- Desired state: exact versions only.
- Cycle window: 20 identical-input cycles for determinism checks.
- Failure injection: manifest invalid, desired source outage, acquisition failure, loader failure, admin trigger unavailable.

## Verification command set

Run from repository root:

```bash
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj \
  --filter "FullyQualifiedName~DesiredManifest|FullyQualifiedName~DesiredAggregation|FullyQualifiedName~LoaderBoundary|FullyQualifiedName~OperationalSnapshot"

dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj \
  --filter "FullyQualifiedName~ManifestConvergence|FullyQualifiedName~DesiredSourceOutageIsolation|FullyQualifiedName~ManualReconcile|FullyQualifiedName~LoaderFailureIsolation"

dotnet test Nuplane.sln
./build/validate-secrets.sh
```

## 1) Desired manifest determinism

1. Configure a desired manifest containing 5–10 packages pinned to exact versions.
2. Start two host instances configured to read the same manifest.
3. Run multiple reconciliation cycles with no input changes.
4. Verify both hosts compute the same desired set and remain idempotent.

## 2) Manifest update → eventual convergence

1. Upload a new package version to the configured source.
2. Update the manifest to reference the new exact version.
3. Trigger reconcile (or wait for polling interval).
4. Verify each host eventually activates the new version with transactional/LKG safety.

## 3) Multi-source aggregation determinism + outage isolation

1. Configure two desired sources (e.g., manifest + directory desired source).
2. Introduce a duplicate package ID across sources.
3. Verify deterministic tie-break behavior and reason codes.
4. Simulate one source being unavailable.
5. Verify degraded outcome and non-mutating behavior for impacted requests.

## 4) Optional loader boundary

1. Activate a package containing a known type.
2. Enable the optional loader boundary.
3. Verify the host can load the known type from the active package.
4. Inject a loader failure (e.g., invalid assembly) and verify:
   - host does not crash
   - failure is observable (event + correlation)

## 5) Optional admin surface

1. Read operational snapshot/state via the admin surface.
2. Verify snapshot is internally consistent and reflects the current active set.
3. Trigger manual reconcile.
4. Verify the outcome is observable and reflected in snapshot/telemetry.

## Expected evidence

- Correlation-linked logs for manifest/source outcomes, acquisition/activation outcomes, loader outcomes, and admin trigger outcomes.
- Metrics for degraded reasons and failure counts by stage.
- Health state transitions demonstrating degraded behavior on invalid manifest/source outage and recovery when resolved.

## Success Criteria Validation Checks

1. Validate replica convergence on identical manifest (`SC-001`).
2. Validate LKG preservation and non-corruption under injected failures (`SC-002`).
3. Validate admin read+trigger workflow timing (`SC-003`).
