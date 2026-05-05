# Contract: Graph Reconciliation and Active State

## Component

`PackageResolutionMiddleware`, `PackageApplyExecutor`, `StoreRegistry`, and cleanup services.

## Purpose

Acquire, validate, install, publish, and clean up packages at dependency graph boundaries.

## Input

- `ResolvedPackageGraph` results from graph resolution.
- Current store state and active package descriptors.
- Package install root and transaction staging directories.
- Cleanup policy.

## Output

- Active graph activation records.
- Active package descriptors with graph role metadata.
- Feed/source decisions and graph diagnostics.
- Preserved last-known-good graph state on failure.

## Behavioral Contract

1. Reconciliation MUST acquire all graph nodes before publishing any graph node as active.
2. Reconciliation MUST validate every acquired package through existing trust and content validation paths.
3. Reconciliation MUST install graph nodes through the transaction coordinator.
4. Reconciliation MUST publish graph activation records and active package descriptors in one store update.
5. A package descriptor MUST identify whether it is a desired root, dependency-only, or both.
6. Active state MUST include graph id and generation id for every active package node.
7. Cleanup MUST retain installed packages referenced by any active graph.
8. Cleanup MAY remove installed packages no longer referenced by active graphs according to existing cleanup policy.
9. Failed resolution/acquisition/validation/install/load preparation MUST preserve the previous active graph generation when available.
10. First activation failure with no LKG MUST record diagnostics and leave the failed root inactive.

## Failure Contract

Graph failure MUST NOT produce partial active graphs. Diagnostics MUST be visible through reconciliation results, operational state, load state where applicable, logs, and metrics.

## Test Contract

- Graph activation publishes all nodes atomically.
- Failed dependency acquisition leaves prior active graph intact.
- Dependency cycle failure leaves prior active graph intact and records cycle-path diagnostics.
- Unsupported required native/runtime-specific asset failure leaves prior active graph intact.
- First activation failure publishes no partial active package descriptors.
- Cleanup retains dependency package while any active graph references it.
- Cleanup releases dependency package after last referencing graph is removed/replaced.
- Active package catalog maps root/dependency role metadata correctly.
- Store restart preserves graph activation metadata and package roles.
