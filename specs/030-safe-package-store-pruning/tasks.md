# Tasks: Safe Manual Package-Store Pruning

**Status:** Independently reviewed and root-accepted implementation checklist, 2026-10-09. Setup is accepted; foundational implementation is in progress. No deletion or platform gate is claimed.

**Inputs:** Accepted design at `b86d9affa982a645d9afe6329229a5a22ef10c38`, product baseline `eb2cf6c2ee1f79dc2c45fb83cc415bbe4856d0d4`; [spec](spec.md), [plan](plan.md), [research](research.md), [data model](data-model.md), [admission contract](contracts/admission.md), [maintenance contract](contracts/maintenance.md), [validation guide](quickstart.md). No external review-credit dependency.

This revision preserves the selected complete-enrollment rule: every member must have complete active **and recoverable LKG** closure. Unknown legacy LKG leaves enrollment incomplete and ordinary package IO denied until verifiable closure is supplied or the state is explicitly retired during a quiescent migration. Existing-root cutover is fully quiescent; Nuplane does not claim to detect undisclosed legacy users. Unknown never becomes empty. No automatic pruning driver or pruning timer is introduced. The selected passive weak-death observer is permitted and must not initiate GC, unload or deletion.

## Task format and cadence

Task IDs are sequential. No task is implicitly parallel; the root delegates independent artifacts only after prerequisites settle. Story labels `[US1]`–`[US5]` appear only in the five user-story phases; setup, foundation, and final qualification tasks have no story label. Each task names the exact intended artifact path. A grouped path is allowed only for one tightly coupled contract/artifact group. Interface companions live with the interface-owning module: persisted membership/protection records and companions to core serializer/dispatcher/metadata interfaces stay in `src/Nuplane`; public advisor/gate interfaces and their context records stay in `src/Nuplane.Loading.Abstractions`; runtime/internal scoped-loader enforcement stays in `src/Nuplane.Loading`; neutral opaque scope handles, their capability interfaces, pure shared models and companions extending existing abstraction interfaces may live in `src/Nuplane.Abstractions`. No serializer or loader implementation contract is added to Abstractions. Every new DI service has an interface, concrete-first registration and factory aliases. All public/protected APIs require XML documentation. Opaque capabilities are not publicly mintable. All driver rows in the admission contract require before-first-read proof; one driver passing does not accept another.

The Nuplane Constitution requires automated coverage for changed logic and boundaries and at least one regression for a bug fix. The Speckit task-generation guidance requires tests for each story and does not require a failing-before run absent an adopted TDD cadence. Therefore tests are required, owned as separate tasks, and run with the corresponding implementation/story gate; this checklist makes no unsupported TDD or pre-implementation red-test claim. Test targets are the existing `test/Nuplane.Store.Tests`, `test/Nuplane.Runtime.Tests`, `test/Nuplane.Loading.Tests`, and `test/Nuplane.Integration.Tests` projects. Tests target `net10.0`; production projects target `net8.0;net9.0;net10.0`.

## Phase 1: Setup

**Purpose:** Establish isolated, deterministic test inputs. No pruning behavior changes.

- [X] T001 Add reusable unique temporary physical-root/state-slot fixture in `test/Shared/PackageStoreFixture.cs`; own all paths, reject traversal, never reference a user store or network feed, and clean up with idiomatic disposal. Link this one shared source explicitly from each test project that consumes it.
- [X] T002 Add a controllable child executable in the tightly coupled `test/Nuplane.PackageStore.TestHost/Nuplane.PackageStore.TestHost.csproj` and `test/Nuplane.PackageStore.TestHost/Program.cs`; own temp paths, expose named crash/read gates and a finite command protocol, and keep it non-packable. No production maintenance behavior is implemented here.
- [X] T003 Add process launcher/gate fixture in `test/Nuplane.Integration.Tests/Fixtures/PackageStoreParticipantProcess.cs`; build/copy the test-host artifact through the integration project, launch without a shell, expose deterministic readiness/release/termination and capture child exit/output, and drain/terminate owned children on disposal.
- [X] T004 Add early confinement/disposal tests in `test/Nuplane.Store.Tests/PackageStoreFixtureTests.cs`; prove unique roots, traversal/rooted-path rejection, child slot creation, link refusal and cleanup without changing a sibling fixture.
- [X] T005 Add real process smoke tests in `test/Nuplane.Integration.Tests/PackageStoreParticipantProcessTests.cs`; prove readiness, controlled release, child exit/output capture, cancellation and disposal termination before the later crash tests rely on this harness.

## Phase 2: Foundational mechanisms

**Purpose:** Implement identity, complete enrollment, safe state publication, and lifetime ownership. Do not add inventory deletion or recursive deletion in this phase.

### Shared and module-owned contracts / records

- [X] T006 Add the neutral opaque capability family and immutable graph/install identities in the tightly coupled one-type-per-file group `src/Nuplane.Abstractions/PackageStoreProtection/` (with narrow core friend access in `src/Nuplane.Abstractions/Nuplane.Abstractions.csproj`): IPackageStoreAdmission, root/path admission, owner/borrow, graph-use provider/owner/view and typed refusal. Keep authority-bearing constructors non-public with narrow core friend access; public descriptive identity values grant no authority; no persisted ledger records or implementation dependencies.
- [X] T007 Add optional resolver companion in `src/Nuplane.Abstractions/IScopedPackageResolver.cs` extending IPackageResolver; a scoped call consumes an exact live borrow without reacquiring root ownership.
- [X] T008 Add optional acquisition companion beside IRemotePackageAcquirer in `src/Nuplane/Feeds/IScopedRemotePackageAcquirer.cs`; propagate the exact borrow before local completion/hash/staging reads. Keep this companion with the existing core-owned interface.
- [ ] T009 Add schema-versioned root enrollment, member, state-protection, active/LKG/retired graph, pending-publication and recovery records in `src/Nuplane/Store/Coordination/PackageStoreProtectionRecords.cs`; preserve Unknown versus KnownEmpty and immutable copied collections.
- [X] T010 Add optional protection-payload serializer companion beside `IStoreStateSerializer` in `src/Nuplane/Store/State/IPackageProtectionStateSerializer.cs`; preserve the required serializer interface and reject silent metadata loss.
- [ ] T011 Add optional coordinated publication companion beside IStoreRegistry in `src/Nuplane/Store/State/ICoordinatedStoreRegistry.cs`; accept a live borrow and complete next protection without changing required registry members.
- [X] T012 Add optional metadata-reader scope companion beside internal `IPackageMetadataReader` in `src/Nuplane/Metadata/IScopedPackageMetadataReader.cs`; do not extend the reader interface.
- [X] T013 Add internal scoped-loader companion in `src/Nuplane.Loading/IScopedPackageLoader.cs` extending internal IPackageLoader via existing friend access; keep this enforcement helper in the Loading implementation, not Abstractions.
- [X] T014 Add optional scoped observer contract beside `INuplaneObserver` in the tightly coupled `src/Nuplane.Abstractions/IScopedNuplaneObserver.cs` and `src/Nuplane.Abstractions/IPackagePathIndependentNuplaneObserver.cs`; it may reference only the neutral opaque scope handle.
- [X] T015 Add optional contributor scope companion beside `IDesiredStateContributor` in the tightly coupled `src/Nuplane.Abstractions/IScopedDesiredStateContributor.cs` and `src/Nuplane.Abstractions/IPackagePathIndependentDesiredStateContributor.cs`.
- [X] T016 Add optional scoped activation-gate contract beside IPackageActivationGate in the tightly coupled `src/Nuplane.Loading.Abstractions/IScopedPackageActivationGate.cs` and `src/Nuplane.Loading.Abstractions/IPackagePathIndependentActivationGate.cs`; include explicit path-independent participation, using neutral graph-lease views only.
- [X] T017 Add optional scoped load-mode advisor contract beside IPackageLoadModeAdvisor in the tightly coupled `src/Nuplane.Loading.Abstractions/IScopedPackageLoadModeAdvisor.cs` and `src/Nuplane.Loading.Abstractions/IPackagePathIndependentLoadModeAdvisor.cs`; include explicit path-independent participation and use the neutral graph-lease view.
- [X] T018 Add non-positional graph-lease context data in `src/Nuplane.Loading.Abstractions/PackageActivationContext.cs`; preserve its positional constructor/deconstruction.
- [X] T019 Add non-positional graph-lease context data in `src/Nuplane.Loading.Abstractions/LoadModeAdvisorContext.cs`; preserve its positional constructor/deconstruction.
- [X] T020 Add dispatcher scope overload/companion beside core `IObserverEventDispatcher` in `src/Nuplane/Events/IScopedObserverEventDispatcher.cs`; keep callbacks awaited and existing dispatcher contract source-compatible.

### Physical authority and publication mechanisms

- [ ] T021 Define narrow platform identity/handle-relative filesystem operations in `src/Nuplane/Store/Coordination/IPhysicalStoreFileSystem.cs`; separate non-destructive identity/admission probes from the recursive deletion primitive.
- [ ] T022 Implement Darwin/Linux identity and no-follow metadata adapter in `src/Nuplane/Store/Coordination/UnixPhysicalStoreFileSystem.cs`; unsupported capabilities return an explicit refusal.
  - Partial foundation: provider-owned handles, metadata/control/lock operations and the fixed-signature Darwin creation shim are implemented and locally qualified; [native increment evidence](evidence/native-unix-foundation.md) records exact scope, preserved failures and negative controls. T021/T022 stay open for remaining filesystem operations and hosted/platform qualification.
- [ ] T023 Implement Windows identity and reparse-safe metadata adapter in `src/Nuplane/Store/Coordination/WindowsPhysicalStoreFileSystem.cs`; unsupported capabilities return an explicit refusal.
  - Partial foundation: Windows x64/local NTFS metadata, ancestry, bounded control IO and nonblocking handle-retaining locks are implemented; [Windows increment evidence](evidence/native-windows-foundation.md) records source review, local compilation, a causal parser mutation and exact-commit hosted qualification (11 Windows cases, zero skips). Remaining native operations and full admission/lifetime/deletion qualification are still open.
- [ ] T024 Implement stable root and state-slot identity in `src/Nuplane/Store/Coordination/PhysicalStoreIdentity.cs`; state-slot identity is parent-directory identity plus validated basename/case policy, while observed final state-file identity may change only after a verified atomic replacement.
- [ ] T025 Implement componentwise metadata-only configured-root authority discovery in `src/Nuplane/Store/Coordination/PackageStoreAuthorityResolver.cs`; check every enrolled ancestor before resolving aliases, support configured aliases only when native identity agrees, and return Unknown for escape/different-root/final package/member/content links or archive hardlink ambiguity.
- [ ] T026 Implement configured single-root and multi-path admission in `src/Nuplane/Store/Coordination/PackageStoreAdmission.cs` as IPackageStoreAdmission; resolve and deduplicate physical roots, acquire in stable order, unwind partial acquisition in reverse, and refuse Unknown before payload IO. Do not shadow the neutral PackageStorePathAdmission handle type.
- [ ] T027 Implement trusted configured-label resolution in `src/Nuplane/Store/Maintenance/PackageStoreRootResolver.cs`; consume the sole new RootLabel option and existing FeedResolutionOptions.PackageInstallRoot, bind configuration identity and expose no arbitrary request paths.
- [ ] T028 Implement root-before-state locking and deterministic member ordering in `src/Nuplane/Store/Coordination/PhysicalStoreLock.cs`; never wait for a live-use sentinel while holding root/state locks.
- [ ] T029 Implement root membership registry and complete/incomplete authority lookup in `src/Nuplane/Store/Coordination/RootMembershipRegistry.cs`; metadata-proven absence of any reserved authority ancestor is Unenrolled, but missing records inside a present reserved control directory, malformed/unsupported/ambiguous/mismatched or incomplete authority are Unknown.
- [ ] T030 Add nullable non-positional protection metadata to `src/Nuplane/Store/State/StoreStateRecord.cs`; preserve constructor/deconstruction and legacy normalization.
- [ ] T031 Implement the optional payload extension in core `src/Nuplane/Store/State/StoreStateSerializer.cs`; round-trip protection when the serializer implements T010 and refuse completion when metadata is dropped.
- [ ] T032 Implement canonical state/protection digest calculation in `src/Nuplane/Store/Coordination/ProtectionDigest.cs`; define sort, normalization and case rules once.
- [ ] T033 Implement pending enrollment/state-publication recovery in `src/Nuplane/Store/Coordination/RootMembershipRecovery.cs`; reconcile exact prior/next evidence at an unchanged slot, refresh observed file identity only after verified replacement, and refuse third/missing evidence.
- [ ] T034 Implement atomic held-directory state-file replacement in `src/Nuplane/Store/State/AtomicFileWriter.cs`; retain existing `.tmp`/`.bak` recovery contract while validating the held parent and exact state slot with no-follow, handle-relative operations. Do not claim power-loss durability beyond tests.
- [X] T035 Implement counted root owner and borrow lifetime in the tightly coupled `src/Nuplane/Store/Coordination/PackageStoreOperationState.cs` and `src/Nuplane/Store/Coordination/IPackageStoreOperationPathValidator.cs`; back the opaque owner without a type-name collision, reject new borrows on close, and drain borrows plus in-flight validation before releasing an already-held ownership primitive. Native acquisition is T028, not this counted-state unit.
- [ ] T036 Implement immutable graph-use record publication and nonblocking stale-use inspection in `src/Nuplane/Store/Coordination/PackageGraphUseLeaseProvider.cs`; malformed, missing, mismatched or uncleanable use records deny deletion.
- [ ] T037 Implement graph-use owner/view read-pin lifetime in `src/Nuplane/Store/Coordination/PackageGraphUseLeaseOwner.cs`; prevent new pins after close and release only after all pins and lifetime evidence end.
- [ ] T038 Add only selected maintenance root-label option in `src/Nuplane/Store/Maintenance/PackageStoreMaintenanceOptions.cs`; property is `RootLabel`, default `default`, consumed by the configured root resolver. Do not add coordination/pruning-enable options.
- [ ] T039 Implement `IValidateOptions<PackageStoreMaintenanceOptions>` in `src/Nuplane/Store/Maintenance/PackageStoreMaintenanceOptionsValidator.cs`; validate label/root compatibility and duplicate/blank labels.
- [ ] T040 Register root resolver and validated maintenance options in `src/Nuplane/Registration/NuplaneStorePersistenceRegistrationServices.cs`; use `ValidateOnStart()` and concrete-first aliases without changing existing cleanup defaults.

### Foundational tests

- [ ] T041 Add physical identity/state-slot tests in `test/Nuplane.Store.Tests/PhysicalStoreIdentityTests.cs`; include ordinary atomic replacement at one stable slot and refusal for changed parent/basename/link ambiguity.
- [ ] T042 Add authority and configured-alias tests in `test/Nuplane.Store.Tests/PackageStoreAuthorityResolverTests.cs`; prove ancestor checks precede alias resolution and no payload read occurs on Unknown.
- [ ] T043 Add multi-root ordering/unwind tests in `test/Nuplane.Store.Tests/PackageStorePathAdmissionTests.cs`; include duplicate aliases for one root, reverse acquisition completion, cancellation, and partial-acquire cleanup.
- [ ] T044 Add serializer/ledger/digest/pending recovery tests in `test/Nuplane.Store.Tests/RootMembershipRegistryTests.cs`; cover legacy data, KnownEmpty, custom serializer dropping metadata, stable digest, crashes at each publication point, and prior/next/third evidence.
- [ ] T045 (**Partial:** counted-state tests accepted; native ordering pending.) Add root-before-state lock tests in `test/Nuplane.Store.Tests/PhysicalStoreLockTests.cs` and counted borrow/close tests in `test/Nuplane.Store.Tests/Coordination/PackageStoreOperationStateTests.cs`; native ordering remains separate from the deterministic state-unit proof.
- [ ] T046 Add held-directory atomic-writer tests in `test/Nuplane.Store.Tests/AtomicFileWriterIdentityTests.cs`; preserve existing failure-atomic `.tmp`/`.bak` behavior and prove state-file replacement changes observed file identity without changing slot identity.
- [ ] T047 Add graph lease/sentinel/read-pin tests in `test/Nuplane.Store.Tests/PackageGraphUseLeaseProviderTests.cs`; cover publish-before-read, nonblocking busy status, close/read overlap, stale/missing/mismatched records, and persistent active/LKG protection after stale-use reaping.
- [ ] T048 Add package maintenance option validation/registration tests in `test/Nuplane.Runtime.Tests/PackageStoreMaintenanceOptionsTests.cs`; assert the sole new option is consumed and startup validation runs.

**Foundation gate:** Do not implement recursive physical deletion. Root may proceed to stories only after design review and foundational non-destructive identity/admission tests establish the supported provider surface.

## Phase 3: User Story 1 — Inspect unused packages without deleting files (Priority P1)

**Goal:** Provide explainable inventory, pure retention planning and default dry-run. Package bytes remain unchanged.

**Independent test criterion:** Inspect an isolated owned store with active, LKG, live, unused, staging and unknown entries; verify exact classifications and unchanged package bytes.

- [ ] T049 [US1] Implement completed-install inventory using the existing feed install store in `src/Nuplane/Store/Maintenance/PackageStoreInventory.cs`; distinguish immutable completed installs from staging/control/state/unknown entries without mutation.
- [ ] T050 [US1] Implement pure retention classification in `src/Nuplane/Store/Maintenance/PackageStoreRetentionPlanner.cs`; absent retention keeps all, active/LKG/live protection overrides retention, and Unknown never becomes empty.
- [ ] T051 [US1] Implement read-only inspection orchestration in `src/Nuplane/Store/Maintenance/PackageStoreInspectionService.cs`; resolve configured root, acquire root/member ownership, report revisions and uncertainty, and do not delete.
- [ ] T052 [US1] Add additive maintenance operation request/result contracts in `src/Nuplane.Admin/INuplanePackageStoreMaintenanceOperations.cs`; preserve INuplaneAdminOperations and exclude arbitrary paths. Include enrollment preview/explicit quiescent enrollment/recovery, inspect and default-preview prune; execute confirmation binds expected epoch and retention.
- [ ] T053 [US1] Implement the manual preview/retention application service in `src/Nuplane.Admin/PackageStoreMaintenanceService.cs`; validate retention DTOs at this boundary and add no hosted/timer prune path.
- [ ] T054 [US1] Register the optional service in `src/Nuplane.Admin/NuplaneAdminServiceCollectionExtensions.cs` using concrete-first/interface alias registration.
- [ ] T055 [US1] Add explicit configured-label-only endpoints in `src/Nuplane.Admin.Api/NuplaneAdminEndpointExtensions.cs`; do not accept candidate paths.
- [ ] T056 [US1] Add inventory tests in `test/Nuplane.Store.Tests/PackageStoreInventoryTests.cs`; assert recognized/staging/unknown classification and unchanged package bytes.
- [ ] T057 [US1] Add pure planner tests in `test/Nuplane.Store.Tests/PackageStoreRetentionPlannerTests.cs`; assert absent-policy keep-all, NuGet version ordering, exact protection/refusal reasons and Unknown refusal.
- [ ] T058 [US1] Add inspection service tests in `test/Nuplane.Runtime.Tests/PackageStoreInspectionServiceTests.cs`; cover default Preview, configured root, request validation and legacy custom Admin compatibility.
- [ ] T059 [US1] Add real API preview tests in `test/Nuplane.Integration.Tests/PackageStoreInspectionApiTests.cs`; prove label-only requests and unchanged package bytes.

## Phase 4: User Story 2 — Coordinate independent hosts sharing a store (Priority P1)

**Goal:** Enroll every persistent state through a fully quiescent cutover and publish complete active plus recoverable LKG protection before runtime access resumes.

**Independent test criterion:** Two real compositions with distinct states share one root. Offline active/LKG closures remain protected, an upgraded composition participates without a local prune flag, and unknown LKG keeps enrollment incomplete and ordinary package IO denied.

- [ ] T060 [US2] Implement quiescent enrollment orchestration in `src/Nuplane/Store/Coordination/PackageStoreEnrollmentService.cs`; hold all declared users stopped from Incomplete through verified Complete publication.
- [ ] T061 [US2] Implement membership/state-slot migration in `src/Nuplane/Store/Coordination/PackageStoreMembershipMigrationService.cs`; require full old/new member sets, stable lock order, explicit quiescent retirement and no silent re-enrollment.
- [ ] T062 [US2] Extend active graph projection in `src/Nuplane/Operational/ActivePackageCatalogMapper.cs` to preserve exact selected nodes, edges and install identity.
- [ ] T063 [US2] Extend recoverable graph snapshot construction in `src/Nuplane/Store/Coordination/RecoverableGraphSnapshotFactory.cs` so complete historical LKG install paths are captured on verified transitions; legacy LKG without verifiable closure remains Unknown.
- [ ] T064 [US2] Integrate root pending/acknowledgement and protection commits into `src/Nuplane/Store/State/StoreRegistry.cs`; every enrolled state write uses T033 and refreshes file identity only after verified replacement.
- [ ] T065 [US2] Add state compatibility and copy tests in `test/Nuplane.Store.Tests/StoreStateRecordCompatibilityTests.cs`; preserve positional construction, deconstruction, old JSON and new protection metadata through registry copies.
- [ ] T066 [US2] Add enrollment/LKG migration tests in `test/Nuplane.Store.Tests/PackageStoreEnrollmentTests.cs`; include unknown active/LKG, KnownEmpty, unknown legacy after ordinary success, supplied verifiable closure, explicit quiescent retirement, and atomic replacement.
- [ ] T067 [US2] Add real two-composition enrollment tests in `test/Nuplane.Integration.Tests/SharedPackageRootEnrollmentTests.cs`; include offline state, shared dependency, absent local prune setting, unknown third participant refusal before ordinary package IO, and failure during quiescent publication.

## Phase 5: User Story 3 — Keep packages readable during overlapping generations (Priority P1)

**Goal:** Establish admission before every earliest package read and retain graph protection through callbacks, lazy reads and actual loading-context lifetime.

**Independent test criterion:** A real before-read gate races an older selected graph with a new state publication and maintenance attempt; protection remains through reload, lazy dependency/native read, callback failure and actual collectible/noncollectible lifetime.

Each driver integration is separate from the mechanism and from other drivers as required by FR-010.

### Reconciliation and package path drivers

- [ ] T068 [US3] Integrate root admission before state locking/refresh in `src/Nuplane/Reconciliation/ReconciliationService.cs`; carry the exact owner through the awaited pipeline.
- [ ] T069 [US3] Add scoped borrow propagation to `src/Nuplane/Reconciliation/Middleware/ReconciliationCycleContext.cs`; nested calls borrow the existing owner without reacquiring root ownership.
- [ ] T070 [US3] Add scoped package resolution in `src/Nuplane/Reconciliation/Middleware/PackageResolutionMiddleware.cs`; legacy-only custom resolvers refuse enrolled roots before invocation.
- [ ] T071 [US3] Add owner-aware local cache/feed resolver access in `src/Nuplane/Feeds/MultiFeedPackageResolver.cs` before the earliest local package read.
- [ ] T072 [US3] Add owner-aware acquisition in `src/Nuplane/Feeds/NuGetRemotePackageAcquirer.cs` before completion/hash probes, staging, extraction and publication; preserve trust/hash boundaries.
- [ ] T073 [US3] Add scoped operations to `src/Nuplane/Feeds/PackageInstallStore.cs`; path-only calls refuse enrolled/unknown roots before content access.
- [ ] T074 [US3] Add scoped desired-state contribution in `src/Nuplane/Sources/DesiredStateAggregator.cs`; validate contributors before first package IO.
- [ ] T075 [US3] Add scope consumption to `src/Nuplane/Capabilities/CapabilityDesiredStateContributor.cs`; metadata reads use the supplied owner before IO.
- [ ] T076 [US3] Enforce scoped or explicitly path-independent activation-gate participation before callbacks in `src/Nuplane.Loading/PackageLoader.cs`; validate the complete gate set before first package IO. This task follows the later lease-transfer integration in the same file; add no core-to-Loading dependency or invented core activation dispatcher.
- [ ] T077 [US3] Add borrowed-owner callback dispatch beside `IObserverEventDispatcher` in `src/Nuplane/Events/ObserverEventDispatcher.cs`; await all callbacks before operation-owner close.
- [ ] T078 [US3] Add active/LKG protection publication under the borrowed owner in `src/Nuplane/Reconciliation/Middleware/CleanupMiddleware.cs`; do not reacquire root ownership.
- [ ] T079 [US3] Add root-before-state graph admission in `src/Nuplane/Hosting/LastKnownGoodStartupRecoveryService.cs`; resolve exact persisted recovery graph and lease before callback, metadata or load.
- [ ] T080 [US3] Add scoped metadata reads to `src/Nuplane/Metadata/NuplanePackageMetadataReader.cs` using the T012 companion; preserve current validation behavior.
- [ ] T081 [US3] Add a non-positional init-only refusal discriminator/factory in `src/Nuplane/Metadata/NuplanePackageMetadataReadResult.cs`; retain the four-field constructor/deconstruction and never relabel authority refusal as Missing.
- [ ] T082 [US3] Add metadata-result compatibility tests in `test/Nuplane.Runtime.Tests/NuplanePackageMetadataReadResultCompatibilityTests.cs`; exercise the existing four-argument constructor/deconstruction and distinguish refusal from Missing after the additive result implementation.
- [ ] T083 [US3] Add owner-aware read to `src/Nuplane/PackageContent.cs`; retain existing static compatibility surface while refusing enrolled/unknown paths without a scope.
- [ ] T084 [US3] Add scoped direct restore handling in `src/Nuplane/NuplaneRestore.cs`; host-free direct calls use the same admission-before-read contract.

### Loading lifetime and passive observation

- [ ] T085 [US3] Implement core weak graph-use lifetime observer in `src/Nuplane/Store/Coordination/PackageGraphUseLifetimeObserver.cs`; observe weak death passively while collectible associations exist, independent of future admission, without GC, unload, callbacks, or pruning.
- [ ] T086 [US3] Implement Loading-owned attachment of leases to actual contexts in `src/Nuplane.Loading/PackageGraphLoadContext.cs`; preserve whole graph through lazy/native reads and actual collectible weak death; noncollectible uses last until process exit.
- [ ] T087 [US3] Integrate before-advisor/gate/loading lease acquisition in `src/Nuplane.Loading/PackageLoader.cs`; partial load and callback faults keep protection until actual context lifetime is resolved.
- [ ] T088 [US3] Add weak context association only in `src/Nuplane.Loading/PackageAutoLoadingObserver.cs`; do not own the passive sweeper here.
- [ ] T089 [US3] Add core observer and admission registrations in `src/Nuplane/Registration/NuplaneRuntimeRegistrationServices.cs`.

- [ ] T090 [US3] Add OpenActivePackageSnapshotAsync in `src/Nuplane/NuplaneStore.cs`; retain unscoped pure state/descriptor observation, acquire sorted roots after descriptor observation, reread stable state and validate membership before exposing an admitted snapshot. Returned raw paths alone are never leases.
- [ ] T091 [US3] Add scoped restore composition entry point in `src/Nuplane/Restore/RestoreComposition.cs`; preserve the existing host-free registration behavior.
- [ ] T092 [US3] Add Loading attachment registration in `src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs`; leave the passive observer registered by core.
- [ ] T093 [US3] Add before-advisor graph admission in `src/Nuplane.Loading/PackageLoadModeSelector.cs`; ensure metadata advisors cannot read before the complete graph lease is published.
- [ ] T094 [US3] Add lease-aware assembly materialization in `src/Nuplane.Loading/PackageAssemblyCatalog.cs`; preserve exact graph protection while returning package assemblies.
- [ ] T095 [US3] Add lease-aware publication/reads in `src/Nuplane.Loading/LoadingCatalog.cs`; ensure catalog refresh and stale/loading snapshot paths cannot outlive or expose package graph use without the associated lease.
- [ ] T096 [US3] Add lifetime-safe type scanning in `src/Nuplane.Loading/PackageTypeScanner.cs`; do not release protection while returned types or lazy package reads can still use the graph.
- [ ] T097 [US3] Add admitted host-integrated loading in `src/Nuplane.Loading/NuplaneHostIntegratedLoader.cs`; static path/state entry points refuse enrolled or unknown stores before reads and retain noncollectible graph protection until process exit.
- [ ] T098 [US3] Add admitted snapshot tests in `test/Nuplane.Runtime.Tests/NuplaneStoreSnapshotAdmissionTests.cs`; prove pure observations still work unscoped, sorted-root acquisition plus stable state reread, refusal on membership/revision mismatch and drain of admitted snapshot ownership.
- [ ] T099 [US3] Add host-integrated loader admission tests in `test/Nuplane.Loading.Tests/NuplaneHostIntegratedLoaderAdmissionTests.cs`; prove complete-request preflight before any partial load, scoped snapshot success, enrolled/unknown legacy refusal, and noncollectible graph lifetime.

### Runtime and lifetime tests

- [ ] T100 [US3] Add reconciliation before-read regression tests in `test/Nuplane.Runtime.Tests/ReconciliationPackageStoreAdmissionTests.cs`; cover feed/cache resolver, custom resolver, contributor, activation participant, observer, cleanup publication and cancellation.
- [ ] T101 [US3] Add direct restore/content/startup LKG admission tests in `test/Nuplane.Runtime.Tests/PackagePathAdmissionTests.cs`; every denied path must refuse before its first package read.
- [ ] T102 [US3] Add Loading overlap/partial-failure tests in `test/Nuplane.Loading.Tests/PackageGraphUseLifetimeTests.cs`; prove lease publication precedes first read and unload request alone does not release it.
- [ ] T103 [US3] Add separate passive idle living-process weak-death test in `test/Nuplane.Runtime.Tests/PackageGraphUseWeakDeathTests.cs`; controlled test GC may establish death, but production observer must not initiate it and no later admission may be needed.
- [ ] T104 [US3] Add separate idle child-process sentinel-release test in `test/Nuplane.Integration.Tests/PackageGraphUseProcessExitTests.cs`; process exit releases OS ownership without a later operation while persistent active/LKG protection remains.
- [ ] T105 [US3] Add real overlapping-generation before-read test in `test/Nuplane.Integration.Tests/OverlappingPackageGraphProtectionTests.cs`; gate the first actual package read, commit a newer generation, then prove both old and new graph files remain readable.

**Mandatory non-destructive gate:** Before any recursive-delete implementation, pass the actual two-composition/before-read protection test and review its evidence. Failure or timeout leaves US4 native deletion work unstarted; do not substitute a mocked planner or identity probe.

## Phase 6: User Story 4 — Execute a freshly checked maintenance request (Priority P2)

**Goal:** Add explicit physical deletion only after US3's non-destructive gate. Always re-read and replan under current root/all-state ownership.

**Independent test criterion:** Execute against owned isolated roots after protection changes since preview; compare actual on-disk results with per-candidate reports and prove no protected or unknown entry was removed.

- [ ] T106 [US4] Implement fresh locked execution planning in `src/Nuplane/Store/Maintenance/PackageStoreExecutionService.cs`; reread membership, every state, leases and inventory, then replan; preview IDs never authorize deletion.
- [ ] T107 [US4] Implement exact completed-install quarantine and no-follow recursive deletion in `src/Nuplane/Store/Coordination/PhysicalStoreDeletionExecutor.cs`; validate native directory identity at deletion, refuse links/reparse points and identity swaps, and never traverse outside the admitted install identity.
- [ ] T108 [US4] Add a per-candidate execution outcome builder in `src/Nuplane/Store/Maintenance/PackageStoreExecutionOutcomeBuilder.cs`; preserve actual completed facts and distinguish quarantined, partially removed, deleted, absent, retained, refused and failed.
- [ ] T109 [US4] Add candidate outcome model in `src/Nuplane.Admin/PackageStoreMaintenanceResult.cs`; cancellation and IO faults preserve completed facts and never call policy eligibility “deleted.”
- [ ] T110 [US4] Add separate explicit execution application service in `src/Nuplane.Admin/PackageStoreExecutionService.cs`; require explicit Execute intent, configured root label, expected enrollment epoch and confirmation binding the validated retention request. Changed epoch/configuration refuses; changed protection causes fresh locked replanning.
- [ ] T111 [US4] Add no-follow native executor tests in `test/Nuplane.Store.Tests/PhysicalStoreDeletionExecutorTests.cs`; verify exact identity, reparse/link refusal, traversal resistance, staging exclusion, partial failure and identity replacement. Run only after the non-destructive gate.
- [ ] T112 [US4] Add stale-preview/concurrent-reconciliation tests in `test/Nuplane.Integration.Tests/PackageStoreFreshExecutionTests.cs`; protection added after preview must survive fresh execution, and reports must match actual filesystem state.
- [ ] T113 [US4] Add execute endpoint/report tests in `test/Nuplane.Integration.Tests/PackageStoreExecutionApiTests.cs`; require explicit execute, preserve partial results and prove repeated execution is idempotent.

## Phase 7: User Story 5 — Recover safely after participant failure (Priority P2)

**Goal:** Recover only from exact durable evidence; reap crashed live uses without losing persistent active/LKG protection.

**Independent test criterion:** Terminate controlled child participants at enrollment, state-publication and live-use boundaries; the next owner either proves complete authority or refuses runtime access/deletion, then safely recovers the isolated root.

- [ ] T114 [US5] Add deterministic child-process crash/read points to `test/Nuplane.PackageStore.TestHost/Program.cs` for incomplete enrollment, pending state publication, live-use sentinel and first-read boundaries; drive actual Nuplane APIs, not a parallel simulated ledger.
- [ ] T115 [US5] Implement stale-use exclusive probe and cleanup in `src/Nuplane/Store/Coordination/StalePackageGraphUseRecovery.cs`; uncertain PID/sentinel evidence remains Unknown and persistent state protection is untouched.
- [ ] T116 [US5] Integrate root recovery before ordinary admission in `src/Nuplane/Store/Coordination/RootMembershipRegistry.cs`; exact prior/next publication is recoverable, third/missing evidence remains denied.
- [ ] T117 [US5] Add process-crash recovery tests in `test/Nuplane.Integration.Tests/PackageStoreCrashRecoveryTests.cs`; verify all crash points, offline LKG, no read before lease publication, persistent protection after stale-use reap and refusal on ambiguous sentinel/state evidence.
- [ ] T118 [US5] Add partial deletion/cancellation restart tests in `test/Nuplane.Integration.Tests/PackageStorePartialExecutionRecoveryTests.cs`; returned reports and surviving quarantine/recovery evidence must match actual removed/remaining files after restart. Do not introduce a report database.

## Phase 8: Final qualification and documentation

**Purpose:** Complete cross-platform, package/API compatibility and operational documentation gates; no task claims stable release or customer-store proof.

- [ ] T119 Add operator enrollment/upgrade/recovery/preview/execute guidance in `docs/package-store-maintenance.md`; state quiescent cutover, unsupported legacy/custom participants, conservative refusal, passive lifetime behavior and partial-result meanings.
- [ ] T120 Add admission/lifetime/crash/deletion integration jobs on Ubuntu, macOS and Windows in `.github/workflows/validate.yml`; preserve existing state-persistence jobs, build the real test host and run actual platform tests. Unavailable/unsupported providers are explicit refusals, never silently successful destructive acceptance.
- [ ] T121 Add public API/migration and semantic-version assessment in `docs/package-store-maintenance-api-migration.md`; preserve existing required interfaces and constructor semantics.
- [ ] T122 Add deterministic concurrency/order examples and phase-by-phase test cadence to `specs/030-safe-package-store-pruning/quickstart.md`; identify which focused tests run per phase and serialize heavy commands through the build-slot wrapper.
- [ ] T123 Add Linux/macOS/Windows filesystem and cross-process acceptance results to `specs/030-safe-package-store-pruning/evidence/platform-acceptance.md`; distinguish primitive probes, child-process evidence and actual physical deletion; record unsupported providers as refused.
- [ ] T124 Add package-family and actual-host integration acceptance plan to `specs/030-safe-package-store-pruning/evidence/package-and-host-acceptance.md`; no Elsa/CShells coupling, no user-store mutation, no release claim until downstream gates pass.
- [ ] T125 Run Release builds for every production target framework (`net8.0`, `net9.0`, `net10.0`) for changed Nuplane projects and record exact command/results in `specs/030-safe-package-store-pruning/evidence/builds.md`.
- [ ] T126 Run focused Store, Runtime, Loading and Integration test projects serially and record per-project counts/artifacts in `specs/030-safe-package-store-pruning/evidence/tests.md`.
- [ ] T127 Run all required OS/process/deletion gates available on the runner and list unavailable platforms as pending, never passed, in `specs/030-safe-package-store-pruning/evidence/platform-acceptance.md`.
- [ ] T128 Review final diff against FR/OSR and run formatting/static/architecture checks required by the accepted plan; summarize exact commit and link the evidence files from `specs/030-safe-package-store-pruning/quickstart.md`.

## Dependencies and execution

The phases are ordered: setup → foundational mechanisms/tests → read-only preview → complete enrollment → runtime admission/lifetime → reviewed non-destructive gate → physical execution → crash/restart qualification → final API/platform/host acceptance. A task's earlier mechanism dependencies must exist before editing its driver. Same-file edits are serial; the activation participation task follows lease transfer in PackageLoader even though it is listed among driver obligations.

The root coordinates bounded delegation of independent artifacts only after their contracts settle, reviews every result, and owns integration/QA. Cross-platform metadata adapters and distinct test files can be delegated independently. Shared capabilities, fixtures, state publication and registrations each have one writer. Heavy build/test/restore commands remain serial through the build-slot wrapper.

Before any recursive-delete implementation or executor tests, the real two-composition/overlapping-generation before-read tests must pass and their evidence receive root review. Identity probes and mocked planners cannot replace that gate. Platform acceptance remains pending until actual runner evidence exists.

## Implementation strategy

Deliver reviewable increments: safe identity/admission and complete durable protection; explainable read-only preview; every runtime driver and actual lifetime proof; then native deletion; crash recovery and platform qualification. US1 is independently useful, but the feature is incomplete until explicit execution, recovery and supported-platform proof pass. Preserve failed evidence, check host load before diagnosing timing-shaped failures, and use only owned isolated stores.

## Accepted contract/counting increment

T006–T008, T010, T012–T020 and T035 are accepted for their bounded contract/counting scope; see [evidence](evidence/contracts-and-counted-scope.md). T045 remains partial: six deterministic counted-state tests pass, but native root-before-state locking is unimplemented and unproved. The optional interfaces alone enforce no runtime driver; those tasks remain open.
