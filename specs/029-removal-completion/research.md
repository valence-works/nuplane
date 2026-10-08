# Research: Reconciliation Completion After Removals

## Decision: Extend the existing completion condition with committed removals

**Rationale**: `HealthAndMetricsMiddleware` already owns completion dispatch and runs after `CleanupMiddleware` persists the next active state. Dispatch when `applyResult.AppliedPackages.Count > 0 || changeSet.Removed.Count > 0`. Preserve the original change set and the exact applied list. Do not synthesize package applications.

**Alternatives considered**:
- Add a new completion API or removal-specific callback: rejected because existing observers should receive this already-committed lifecycle event and no public signature change is needed.
- Dispatch for every completed cycle: rejected because no-change empty idle and failed-only cycles must remain notification-free.
- Move dispatch earlier in the middleware chain: rejected because observers need the committed state.

## Decision: Keep failure and health accounting on its existing path

**Rationale**: completion is a host notification, not a success signal. Failed package IDs, degraded evaluation, and metrics already derive from apply/source/loader results after dispatch. A removal-plus-failure regression must prove notification does not clear failure status.

## Decision: Reuse existing loading retirement behavior

**Rationale**: the optional loading observer already rereads authoritative `ActiveVersionById` and requests removal through `UnloadContextsNotActive` for removal-containing change sets. Tests should invoke the existing observer with an empty applied list and an empty authoritative active set. Actual collection/unload guarantees remain owned by Foundation #2362.

## Source anchors

- `src/Nuplane/Reconciliation/Middleware/HealthAndMetricsMiddleware.cs`: ordering, current condition, failure and result assembly.
- `src/Nuplane/Reconciliation/Middleware/CleanupMiddleware.cs`: active state persistence before downstream middleware.
- `src/Nuplane/Store/State/StoreRegistry.cs`: file-backed persistence and active-state commit.
- `src/Nuplane.Loading/PackageAutoLoadingObserver.cs`: authoritative active-state read and current retirement method.
- `src/Nuplane.Abstractions/INuplaneObserver.cs`: existing public observer contract.
- `test/Nuplane.Integration.Tests/ReconciliationServiceFactory.cs`: real middleware pipeline with injectable source, store, and dispatcher.

No unresolved technical questions or new dependencies were identified.
