# Package-store maintenance API and migration notes

The maintenance surface is additive. `INuplaneAdminOperations` retains its existing
contract. `INuplanePackageStoreMaintenanceOperations` is a separately registered
optional service, and its HTTP endpoints are separately mapped. Existing ordinary
Nuplane clients remain compatible without opting into coordinated pruning.

| Package | Responsibility |
|---|---|
| `Nuplane` | Native admission, enrollment, membership/state recovery, protection and manual maintenance kernels |
| `Nuplane.Loading` | Attach graph-use lifetime to actual loading contexts |
| `Nuplane.Admin` | Optional public manual enrollment/migration/inspection/prune requests and reports |
| `Nuplane.Admin.Api` | Optional HTTP mapping over those operations |
| `CShells.Nuplane` | Composition integration in the CShells repository; consumes released Nuplane packages |

No Elsa or CShells dependency is introduced into Nuplane. Caller-supplied root,
install and state-slot identities are observations, independently verified by the
core. Public graph transport records do not grant package access.

## Compatibility boundary

Coordinated participation freezes the host's complete member declarations. Every
client of a shared root must use compatible admission/protection behavior; mixing
an enrolled store with an unaware legacy writer is unsupported. Custom serializers
must preserve the protection metadata. Custom observers/resolvers/loaders must
participate or explicitly satisfy the relevant path-independent contract. Do not
mark a component path-independent merely to suppress a refusal.

An observer that refreshes a package-backed catalog after loading can implement
`IPostLoadingNuplaneObserver`. Its scoped callbacks run under reconciliation's
existing owner; the additional `OnPackageLoadingCompletedAsync` callback runs after
that owner and the store lock close, deferred loading finishes and loading failures
are published. Catalog reads then acquire fresh admission. The applied-package list
describes successful application, so check loading/catalog results rather than
assuming every package loaded successfully. This notification is coordinated-only;
ordinary hosts retain `OnPackagesReconciledAsync` behavior. Existing observer and
dispatcher interfaces gain no required members.

Existing state-body construction/deconstruction remains compatible. Missing
protection in old state is read as unknown, not empty. Ordinary reconciliation does
not silently erase historical recovery uncertainty. Enrollment is an explicit
quiescent upgrade and validates both active and recoverable closure before Complete
publication. [The operator guide](package-store-maintenance.md) describes supplied
evidence and explicit retirement.

Membership changes and relocated state slots require explicit migration with the
exact current roster and epoch. The persisted incomplete transaction retains the
prior membership needed for recovery. Do not downgrade an enrolled store to an
older client that cannot interpret its persisted schema. Restore a separately
verified backup under full quiescence if rollback is necessary.

## Release assessment

The repository currently uses `0.0.N` releases. This work follows that existing
sequence with the next publication candidate, `0.0.12`; it does not infer a new
`0.1.0` policy from the feature's size. New APIs are optional and existing required
interfaces are preserved. Coordinated pruning deliberately introduces a stricter
opt-in operational and persisted-schema boundary, which is documented above.

The CShells adapter update follows its own existing release sequence (`0.0.31`)
and must pin the published Nuplane family consistently. Foundation adopts only
after both public package families are published and qualified. These numbers are
publication targets until the release and public archives have been verified.

Qualification must distinguish local source/test results, supported native
platform results, actual process interruption/deletion results, public archive
verification and downstream host results. A green precursor revision does not
qualify a later migration or lifetime change.
