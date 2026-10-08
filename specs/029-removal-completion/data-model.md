# Data Model: Reconciliation Completion After Removals

No persisted entity, field, schema, or public model is added or changed.

The existing completion payload remains the pair already accepted by `INuplaneObserver.OnPackagesReconciledAsync`:

- `PackageChangeSet`: computed adds, updates, and removals for this cycle.
- `IReadOnlyList<ResolvedPackage>`: the exact packages successfully applied during the cycle; it is allowed to be empty when a removal committed.

The observer can reread the existing persisted `StoreStateRecord` after commit. Empty successful applications do not imply an empty change set or a healthy cycle.
