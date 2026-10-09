# Admission and retained-read contracts

Names below are the selected public contract shapes; implementation must supply XML documentation and preserve existing interfaces/positional records. Optional companions do not add required members to current consumer implementations.

## Core capability surface

| Contract | Operation / ownership |
|---|---|
| IPackageStoreAdmission | AcquireConfiguredRootOperationAsync(kind, cancellation); AcquireForInstallPathsAsync(paths, kind, cancellation). Returns async-disposable admission; Unknown throws typed refusal before package content I/O. |
| PackageStoreRootOperationAdmission | Status Unenrolled/Enrolled, immutable Root identity and optional opaque Owner. Unenrolled has no fabricated owner and preserves existing behavior. |
| PackageStorePathAdmission | Classifies the entire immutable path set, deduplicates physical roots, acquires them in stable order, exposes BorrowFor an included Enrolled path; unwinds in reverse on error/cancellation. No partial load authorization. |
| PackageStoreOperationOwner | Sealed/non-public constructor, Root/Epoch, Borrow(), idempotent DisposeAsync(). Closing rejects new borrows and awaits counted existing borrows before OS unlock. |
| PackageStoreOperationBorrow | Sealed/non-public constructor, exact root/owner/lifetime, idempotent Dispose(). Caller disposes; callee consumes without disposing caller's borrow. |
| IPackageGraphUseLeaseProvider | AcquireAsync(live borrow, validated complete graph snapshot, cancellation). Snapshot can be pending or committed; copies identities and publishes use record before retained graph reads. |
| PackageGraphUseLeaseOwner | Release authority and immutable Lease view. DisposeAsync closes new read pins, drains authorized reads, then releases sentinel. Loading takes ownership explicitly. |
| PackageGraphUseLease | Non-disposable view, exact immutable graph/install identities; scoped readers validate and count a read pin. A stale view or wrong path/root is refused. |
| PackageStoreAdmissionException | Typed reason: UnknownAuthority, UnsupportedFilesystem, RootMismatch, IncompleteEnrollment, StateMismatch, ExpiredScope, UnsupportedParticipant. No refusal is converted into permissive Unenrolled. |

Opaque carryable handles and participant companions belong in `Nuplane.Abstractions`; only core may construct them via a narrow friend-assembly declaration. Core implementations/native adapter/persistence live in `Nuplane`. Loading consumes capabilities without a core dependency on Loading. No public constructor or mutable identity field can mint admission.

Short ownership covers transient resolve/install/metadata work before a graph is known. Retained graph access requires a lease before its first read; it cannot retain only a root borrow after callback return. Root-owner release never releases a transferred graph lease. A lease owner contains no strong Assembly/context or callback capable of rooting one.

## Optional integration companions

- `IScopedPackageResolver : IPackageResolver` adds `ResolveAsync(request, borrow, cancellation)`; legacy-only resolvers are refused on Enrolled roots before invocation.
- `IScopedNuplaneObserver : INuplaneObserver` adds owner-bearing counterparts of changing/changed/failed/reconciled callbacks. `IPackagePathIndependentNuplaneObserver` is an explicit alternative for observers that never open or retain paths. Unclassified observers are refused on Enrolled roots.
- `IScopedObserverEventDispatcher : IObserverEventDispatcher` adds matching owner-bearing dispatch methods; it creates/drains callback borrows under the existing owner. No nested root lock acquisition.
- `IScopedPackageMetadataReader : IPackageMetadataReader` adds `Read(id, version, installPath, borrow)`. Built-in admitted parsing bypasses the legacy static wrapper only after capability validation.
- Internal `IScopedPackageLoader` adds graph loading with borrow and transferred graph-lease owner. Failure releases only if no created context/resolver can retain paths; otherwise attach conservative lifetime protection.
- Optional coordinated-store publication companion accepts the explicit borrow and complete next protection snapshot; existing `IStoreRegistry` signatures remain. Enrolled writes without the companion/owner refuse; the reconciliation driver never reacquires root inside state publication.
- `IPackageProtectionStateSerializer : IStoreStateSerializer` explicitly opts a serializer into enrolled protection publication. The default serializer implements it. An unclassified custom serializer refuses before enrollment; every declared serializer still undergoes saved-payload round-trip/digest validation, and metadata loss keeps authority incomplete. No required member is added to IStoreStateSerializer.
- Loading advisor/gate context records receive non-positional read-only graph-lease properties; retain their positional constructors. Enrolled custom path users explicitly declare scoped participation and consume that capability; unsupported participants refuse before callbacks. Built-in path-independent gates/advisors declare that contract. This is a participation promise, not interception of arbitrary third-party filesystem calls.
- Acquisition/desired-state contributor companions follow the same explicit borrow rule. Validate the entire enrolled participant set before its first package I/O; custom participant failure cannot be discovered only after partial load.

## Driver obligations

Every row receives its own implementation task and meaningful before-read proof. Passing one row's tests does not accept another.

| Driver | Required boundary |
|---|---|
| ReconciliationService.TriggerAsync | Root admission before per-state lock, disk refresh, local feed scan/contributor/resolver. Store owner on ReconciliationCycleContext explicitly; keep through awaited callbacks. |
| PackageResolutionMiddleware / MultiFeedPackageResolver | Consume explicit borrow; propagate through cache probes, local metadata/hash and remote acquisition; no recursive admission. |
| NuGetRemotePackageAcquirer / PackageInstallStore | Verify scoped root before completion/hash probes, staging/extraction/publication; no completion-marker or trust-policy bypass. Existing unscoped static calls deny Enrolled/Unknown. |
| Desired-state contributors / activation participants | Scope-aware companion or explicit path independence; selected graph lease before loading advisors and gates. |
| CleanupMiddleware / StoreRegistry | Atomically commit active/LKG/protection and root pending/ack transition under existing owner; preserve new property in every copy/serializer. |
| ObserverEventDispatcher | Pass exact owner to every awaited scoped observer, reject unsupported participants; finish/drain before owner close. |
| LastKnownGoodStartupRecoveryService | Root before state; recover complete exact LKG snapshot and lease before callback/metadata/load; unknown fallback denied. |
| PackageAutoLoadingObserver / PackageLoadModeSelector | Lease published before first metadata advisor/gate/candidate scan; transfer release owner into actual Loading lifetime. |
| PackageLoader / PackageGraphLoadContext | Protect complete graph including partial failures and lazy managed/native loads; weak death or noncollectible process exit controls release. |
| NuplanePackageMetadataReader / PackageContent | Physical metadata-only authority probe for legacy path calls; scoped reader validates capabilities and uses no-follow admitted core. |
| NuplaneRestore / RestoreComposition | Standalone admission before resolve/install/metadata; ordinary Unenrolled use stays compatible. |
| Direct loading catalogs/scanners | Independent admission or valid explicit owner/lease before directory/content enumeration; no assumption observer initiated them. |
| NuplaneStore | Pure state/descriptor reads remain observational. New OpenActivePackageSnapshotAsync reads descriptors, acquires sorted roots, rereads stable state and validates membership before exposing admitted snapshot. Raw paths are not leases. |
| NuplaneHostIntegratedLoader | New scoped snapshot/path-admission overloads protect every graph before load and retain noncollectible leases until process exit. Legacy overloads classify the complete request and refuse Enrolled/Unknown before any partial load. |

## Static reader compatibility

Keep existing two-argument `PackageContent.TryReadFile` / `TryFindByExtension`: return null on Enrolled/Unknown before payload I/O. Preserve support for extracted directories and `.nupkg` archives. Authority discovery traverses original path components using metadata-only held handles, records any enrolled ancestor before an alias, and refuses an enrolled-to-outside/different-authority transition as Unknown. A resolved target/parent walk alone is not enough. Validated configured-root aliases converging to the same physical identity are supported; final install/member/content links are refused. For an archive, validate its physical parent and final file without following links; hardlink ambiguity is Unknown, and no archive payload opens before admission. Missing or ambiguous targets are Unknown. Add overloads accepting either a live operation borrow or a graph-lease view. Scoped overloads return null for ordinary missing content and throw typed admission refusal for invalid scope/authority. Validate a relative member path and open relative to verified handles; no arbitrary traversal.

Keep `NuplanePackageMetadataReadResult`'s four-field constructor/deconstruction. Add an init-only refusal discriminator/factory; do not relabel refusal as Missing. `NuplaneStore` state observation is not a package scan and may run without root ownership. Loading from its result is a separate admitted operation.

## Lifetime sweep

Attach a weak context observation to core-owned lease state without storing a context/assembly/delegate strong reference. A passive local observer runs only while collectible associations exist and closes a lease after actual weak death independently of future admission. It owns cancellation/drain, uses a fixed internal observation cadence, and initiates no GC/unload, user callback or file deletion. It is separate from manual pruning and does not become an automatic prune timer. Admission may additionally sweep. Prove an idle live owner process releases a dead context's sentinel without another owner operation. Cross-process maintenance probes the OS sentinel without waiting. A live sentinel with valid immutable record protects every node; uncertain/missing record or failed stale cleanup denies deletion. Persistent active/LKG snapshots survive use reaping.
