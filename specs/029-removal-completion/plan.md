# Implementation Plan: Reconciliation Completion After Removals

**Branch**: `029-removal-completion` | **Date**: 2026-10-08 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/029-removal-completion/spec.md`

## Summary

Change the existing completion-dispatch condition in `HealthAndMetricsMiddleware` so a committed removal notifies observers even when the successful-applied list is empty. Preserve the original payload, ordering, result construction, health evaluation, and `next()` flow. Clarify `INuplaneObserver` XML documentation and add focused middleware, loading-observer, and real file-backed reconciliation regressions.

## Technical Context

**Language/Version**: C# / .NET 8, 9, 10 production targets; tests target .NET 10
**Primary Dependencies**: Existing Nuplane runtime, Abstractions, Loading, xUnit/NSubstitute test stack
**Storage**: Existing file-backed `StoreRegistry` for the integration regression; no schema change
**Testing**: `Nuplane.Runtime.Tests`, `Nuplane.Loading.Tests`, `Nuplane.Integration.Tests`
**Target Platform**: Supported Nuplane .NET hosts
**Project Type**: Multi-target .NET libraries
**Performance Goals**: No additional work on cycles that have neither successful applies nor removals
**Constraints**: No public signature change, configuration change, host dependency, package install, or unload policy change
**Scale/Scope**: One dispatch predicate, one XML contract clarification, focused regression coverage

## Constitution Check

- Deterministic reconciliation: **Pass**. The condition reads the already-computed change set and applied list; it does not change state transitions.
- Transactional store safety: **Pass**. Dispatch remains in `HealthAndMetricsMiddleware`, after `CleanupMiddleware` persists the merged active set.
- Source integrity: **Pass**. Source and integrity behavior are untouched.
- Observability: **Pass**. Existing health, metric, log, failure, and correlation accounting remain unchanged.
- Test discipline: **Pass**. Unit boundary tests and a file-backed real-pipeline regression cover the changed path and quiet controls; mutation/revert proves causality.
- Decomposition discipline: **Pass**. No new option, public API, class, or deployable artifact; one predicate, documentation, and test files.
- Options validation discipline: **Pass / not applicable**. No options are introduced or changed.

## Design and Source Findings

The current code dispatches `PublishChangedAsync` for any non-empty `Added`, `Updated`, or `Removed` set, then dispatches `PublishReconciledAsync` only for non-empty `ApplyResult.AppliedPackages`. The minimal additive trigger is `AppliedPackages.Count > 0 || ChangeSet.Removed.Count > 0`; additions and updates without successful apply do not qualify. Delivery remains subject to existing cancellation and observer exception-isolation semantics; no durable exactly-once guarantee is added. Empty/failed-only cycles with no removals stay quiet.

`CleanupMiddleware` persists `context.MergedActive` before calling the next middleware. `HealthAndMetricsMiddleware` runs after it, so a registered completion observer can reread authoritative state after the commit. Keep its actual successful-applied list, even when empty. Failure IDs and degraded status are assembled independently later in the same middleware and must remain untouched.

`PackageAutoLoadingObserver.OnPackagesReconciledAsync` already runs its inactive-context pass when `Removed` or `Updated` is non-empty, rereads the authoritative store, and calls the loader's existing `UnloadContextsNotActive`. It needs a focused empty-applied removal regression; this feature adds no retirement or collection behavior.

## Project Structure

```text
src/Nuplane/Reconciliation/Middleware/HealthAndMetricsMiddleware.cs
src/Nuplane.Abstractions/INuplaneObserver.cs
test/Nuplane.Runtime.Tests/Reconciliation/Middleware/HealthAndMetricsMiddlewareTests.cs
test/Nuplane.Loading.Tests/PackageAutoLoadingObserverTests.cs
test/Nuplane.Integration.Tests/Reconciliation/RemovalCompletionIntegrationTests.cs
specs/029-removal-completion/
```

**Structure Decision**: Modify the core completion boundary and its existing observer contract documentation; test using each package's established unit/integration test project.

## Validation Strategy

Run focused tests for the three affected projects, then production builds for Nuplane and Nuplane.Abstractions for `net8.0`, `net9.0`, and `net10.0`. Run the real integration case against a unique temporary state file; it must verify the state file is empty inside the registered observer before callback return. Temporarily revert only the new `Removed.Count > 0` part of the predicate and show the real removal-to-zero regression fails, then restore and rerun it. Use the shared dotnet build-slot wrapper; do not run the full solution absent a failure requiring broader diagnosis.

## Complexity Tracking

No constitution violations or new complexity.
