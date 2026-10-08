# Reconciliation Completion Observer Contract

No method signature changes. This documents when the existing callback is emitted.

`INuplaneObserver.OnPackagesReconciledAsync(changeSet, appliedPackages, ct)` is called after the current reconciliation pipeline has persisted the next active state when either:

1. at least one package was successfully applied; or
2. the computed change set contains a committed package removal.

The same `PackageChangeSet` is used for `OnPackagesChangedAsync` and the completion callback. `OnPackagesChangedAsync` precedes `OnPackagesReconciledAsync`. The applied list is the actual successful apply result and may be empty for removal-only completion. Failures elsewhere in the cycle remain represented in the reconciliation result and health state; completion is not a success-only signal.

An empty change set plus an empty applied list does not trigger completion. This contract does not promise durable exactly-once delivery across process failure or cancellation after persistence. Observer registration order, cancellation behavior, exception isolation, and the middleware continuation contract are unchanged.
