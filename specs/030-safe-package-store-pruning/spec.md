# Feature Specification: Safe Manual Package-Store Pruning

**Feature Branch**: `108-safe-package-store-pruning`
**Created**: 2026-10-09
**Status**: Draft — requirements prepared; design and implementation acceptance pending
**Input**: Deliver Nuplane #108 end to end: inventory, retention planning and explicit safe deletion, protecting active, last-known-good and in-use package graphs across hosts sharing a physical install root.

Program: [Foundation #2500](https://github.com/elsa-workflows/elsa-foundation/issues/2500). Owning feature: [Nuplane #108](https://github.com/valence-works/nuplane/issues/108). The [root decision checkpoint](https://github.com/valence-works/nuplane/issues/108#issuecomment-6076600594) selects coordinated multi-state enrollment within existing delivery authorization. It supersedes the earlier owner-answer hold without waiving safety proof. This spec defines acceptance, not achieved behavior.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Inspect unused packages without deleting files (Priority: P1)

An operator inspects a configured package store and previews retention decisions. The report distinguishes installed packages, protected graphs, retained packages, deletion candidates and unknown protection. Inspection does not require the operator to trust that a package marked eligible was actually removed.

**Why this priority**: Operators need an explainable view before requesting deletion.

**Independent Test**: Inspect an isolated store with active, last-known-good, in-use and unused packages; compare all reported paths and reasons with the known fixture and prove package bytes unchanged.

**Acceptance Scenarios**:

1. **Given** installed versions and complete protection, **When** the operator requests the default operation, **Then** it produces a dry-run report and deletes no package.
2. **Given** no retention policy, **When** planning runs, **Then** all installed versions remain retained and there are no deletion candidates.
3. **Given** unreadable or legacy protection with unknown closure, **When** inspection runs, **Then** the report identifies uncertainty and execution is refused; unknown is never normalized to known empty.

### User Story 2 - Coordinate independent hosts sharing a store (Priority: P1)

An operator enrolls all persistent state files associated with an existing physical install root while its users are quiescent. Each participating host subsequently protects both its current and recoverable graphs, including while that host is offline.

**Why this priority**: A host's own state lock cannot protect another host using a different state file.

**Independent Test**: Two real Nuplane compositions with distinct persistent states use one owned physical root. Their complete graph protection is visible to maintenance, including offline last-known-good references; a third unregistered participant cannot access the enrolled store unsafely.

**Acceptance Scenarios**:

1. **Given** two enrolled states and shared dependencies, **When** either host reconciles and maintenance plans, **Then** both states' complete active and last-known-good graphs are protected.
2. **Given** an enrolled root, **When** an upgraded host omits its local pruning option, **Then** it still participates in coordination and protection; local opt-out cannot bypass root admission.
3. **Given** a non-enrolled root, **When** ordinary existing Nuplane operations run, **Then** their existing configuration and cleanup behavior remain supported and no pruning protocol is silently enrolled.
4. **Given** an existing root with unknown other persistent states, **When** enrollment is requested without an explicit quiescent operator declaration covering all states, **Then** enrollment is refused.

### User Story 3 - Keep packages readable during overlapping generations (Priority: P1)

A host can load a candidate or continue serving an older generation while newer packages become active. Maintenance must preserve the complete package graph for every reader, including later dependency and native-library reads.

**Why this priority**: Active-state pointers alone do not describe draining, failed or candidate generations.

**Independent Test**: Gate the real loading path before its earliest package-path read, publish graph protection and attempt maintenance concurrently; then load and retain an older generation through reload and verify every protected file remains readable.

**Acceptance Scenarios**:

1. **Given** a candidate built from an older snapshot, **When** another host changes active state, **Then** the candidate's graph remains protected from its first read through the relevant load lifetime.
2. **Given** a retired collectible context still alive after unload was requested, **When** pruning runs, **Then** its graph remains protected until actual context death is established.
3. **Given** host-integrated or other noncollectible loading, **When** a package is superseded or removed, **Then** its graph remains protected until process exit.
4. **Given** partial loading or a callback failure, **When** some assemblies may already have loaded, **Then** protection survives according to the actual context lifetime rather than being released on the exception alone.

### User Story 4 - Execute a freshly checked maintenance request (Priority: P2)

An operator explicitly requests deletion using the configured store and retention policy. Nuplane checks current protection and reports what it actually removed, retained, refused or failed to remove.

**Why this priority**: Reclaiming unused storage is the requested outcome; a report alone does not complete this feature.

**Independent Test**: Execute against owned isolated stores, change protection after preview, race reconciliation and test failures. Compare actual on-disk state with every reported outcome.

**Acceptance Scenarios**:

1. **Given** an earlier preview, **When** a graph becomes protected before execution, **Then** a fresh locked plan retains it regardless of the old preview.
2. **Given** unused eligible packages, **When** explicit execution succeeds, **Then** only freshly eligible packages are physically removed and all active, recoverable and in-use graphs remain usable.
3. **Given** a denied deletion or cancellation after some deletions, **When** execution ends, **Then** the report preserves actual partial results and never labels an unsuccessful deletion as removed.
4. **Given** the same request repeated, **When** the already deleted packages are absent, **Then** execution is idempotent and protected graphs remain intact.

### User Story 5 - Recover safely after participant failure (Priority: P2)

An operator can inspect and maintain a store after a host crashes without permanently pinning abandoned in-process uses or losing persistent recovery protection.

**Why this priority**: Durable protection must distinguish a crashed live-use claim from persistent active and recovery state.

**Independent Test**: Kill controlled child processes at admission/publication/load boundaries, then recover and prune the isolated root. Check that no package read preceded protection and that offline active/LKG graphs still materialize.

**Acceptance Scenarios**:

1. **Given** a crashed reader, **When** maintenance establishes its exclusive stale-use evidence, **Then** abandoned live-use protection can be reaped without discarding that state's active/LKG protection.
2. **Given** incomplete enrollment or state publication after a crash, **When** another operation opens the root, **Then** recovery either establishes complete authoritative protection or denies normal runtime package-path reads/writes and refuses deletion until verified recovery.
3. **Given** an uncertain live-use record, **When** it cannot be classified safely, **Then** deletion is refused with a diagnostic reason.

### Edge Cases

- Multiple configured path strings or symbolic links identify the same physical root; containment cannot rely on lexical prefix checks.
- A candidate directory, graph path, membership record or state file is replaced with a link while maintenance is pending.
- State files live outside the install root; offline state is unreadable, truncated, incompatible or lacks complete active/LKG paths.
- Different graphs share a dependency; equal package IDs and versions occur under different exact install paths.
- Initial enrollment is requested without the required quiescence declaration: it must be refused. The operator must keep ordinary users stopped through complete publication; Nuplane cannot detect an undisclosed reader.
- Enrolled runtime access occurs with pruning disabled locally or after an interrupted membership publication.
- A reentrant loader callback runs under reconciliation ownership; a child continuation outlives or misuses the borrowed scope.
- A graph contains no assemblies, scanning proves no load occurred, or context creation/load fails partway through.
- A busy live-use sentinel must not cause maintenance to wait while holding root/state locks.
- A crash occurs between durable record publication, acquiring lifetime protection and first package read.
- Native dependencies are resolved lazily after managed load; host-integrated contexts remain noncollectible.
- Windows denies deletion or stale-record cleanup while a handle remains open.
- Legacy/custom state stores and custom admin/loader implementations do not implement optional protection capabilities.
- Retention, inventory or confirmation inputs are malformed; unknown directories or external paths are never arbitrary deletion targets.

## Requirements *(mandatory)*

### Functional Requirements

The constitution requires prescriptive architectural requirements. Names below define responsibilities for plan review; final public signatures are designed separately before implementation.

- **FR-001**: A package-store inventory service MUST inspect only the configured physical install root and identify exact installed package paths, known package identity/metadata and unknown entries without changing package files or presenting cleanup eligibility as deletion.
- **FR-002**: A pure retention planner MUST classify inventory against complete protection and explicit retention policy. Its absent-policy behavior MUST keep all versions. It MUST report reasons and separate unknown protection from a proven empty set.
- **FR-003**: A manual pruning service MUST expose default dry-run and separately explicit execution. Its capability MUST be additive to existing admin operations, preserving custom `INuplaneAdminOperations` implementations. It MUST NOT accept arbitrary deletion paths.
- **FR-004**: A physical-root coordinator MUST serialize enrolled-root admission and mutations independently of per-state lock identities, resolve supported filesystem aliases consistently and refuse unsupported or uncertain physical identity.
- **FR-005**: A durable root-membership registry MUST record multiple independent persistent state files and the protocol/schema identity. Existing-root enrollment MUST be explicit and require an operator declaration that all users are quiescent and all persistent states are supplied. Ordinary access MUST NOT silently enroll or replace incomplete membership. The existing-root cutover MUST keep all users stopped from the quiescent declaration through durable complete membership publication; runtime access resumes only after that publication. Enrollment MUST NOT claim concurrent access across this cutover is safe or that unknown readers can be detected.
- **FR-006**: An enrolled-root admission service MUST be consulted by every upgraded runtime access path regardless of local prune enablement. Unregistered/unsupported participants and incomplete/mismatched enrollment MUST deny normal managed package-path reads/writes until verified recovery establishes complete authority. Explicit quiescent enrollment/recovery may inspect files only under its exclusive root/state ownership to establish protection; it MUST NOT load packages or delete installs while authority is incomplete. Non-enrolled roots MUST preserve existing ordinary behavior.
- **FR-007**: A protection-state publisher MUST persist complete exact active and last-known-good dependency graph/install paths without breaking the existing positional state constructor or legacy dictionary normalization. Unknown legacy closure MUST remain unknown for pruning. Root membership MUST have explicit incomplete/complete publication state and an enrollment identity binding member protection. Each state commit MUST atomically include its complete protection metadata; mismatched enrollment/schema/revisions or interrupted membership publication MUST recover to complete authority or refuse deletion, never expose a falsely empty protection window.
- **FR-008**: A protection reader MUST include every enrolled state's active/LKG closure, including offline hosts, and all live graph-use records. Incomplete, unreadable or inconsistent authority MUST refuse execution.
- **FR-009**: A scoped coordination owner MUST acquire physical-root ownership before state locks, acquire multiple state locks deterministically where needed, and expose typed borrowing tied to its exact root, acquired handle and valid lifetime. Expired or unrelated scopes MUST NOT authorize access. A process-wide or unvalidated ambient ownership flag MUST NOT bypass locking.
- **FR-010**: Reconciliation, installation, resolver/metadata scanning, restore/startup recovery and loading integrations MUST establish admission and protection before their earliest package-path read, including callbacks and lazy managed/native resolution. This explicitly includes `PackageInstallStore`, `NuGetRemotePackageAcquirer`, `MultiFeedPackageResolver`, `NuplanePackageMetadataReader`, `PackageContent`, direct `NuplaneRestore`/`RestoreComposition`, `LastKnownGoodStartupRecoveryService`, `PackageAutoLoadingObserver`, `PackageLoader`, `PackageGraphLoadContext`, activation gates and observers receiving package paths. Retained path use MUST hold an explicit valid graph-use lease for its full read lifetime; a callback cannot retain only borrowed operation ownership after return. Mechanism and each driver integration MUST have separate implementation tasks.
- **FR-011**: A package-graph use-lease service MUST publish complete immutable graph-path protection before first read and retain it for actual usage lifetime. Noncollectible uses MUST remain protected until process exit. Collectible uses MUST remain protected until actual weak-reference death, including partial load failures. An unload request alone MUST NOT release protection.
- **FR-012**: A live-use recovery service MUST distinguish live, stale and unknown records using crash-safe ownership evidence. Maintenance MUST probe live ownership without waiting under root/state locks. Ambiguous records or failed stale cleanup MUST refuse deletion; persistent active/LKG protection MUST survive stale-use reaping.
- **FR-013**: The pruning executor MUST acquire valid root/protection-state ownership, freshly reread membership, state, leases and inventory, replan, revalidate containment/identity, then retain required ownership through each actual deletion. A prior preview MUST NOT authorize deletion after protection changes.
- **FR-014**: The executor MUST restrict deletion to known eligible immutable install directories within the admitted root. Unknown entries, traversal, link escapes, replaced identities and protected paths MUST NOT be deleted. Staging, incomplete or unrecognized installs MUST be excluded. The filesystem deletion/quarantine primitive MUST refuse links/reparse points and identity swaps, validate the exact completed-install identity without following links at deletion, and prevent recursive traversal outside that identity. It MUST preserve state metadata and source trust configuration.
- **FR-015**: Per-candidate result models MUST distinguish retained, refused, actually deleted, already absent and failed outcomes. Cancellation and partial IO failure MUST retain completed facts. A successful policy decision MUST NOT be reported as successful physical deletion.
- **FR-016**: Registration/options artifacts MUST use concrete-first interface aliases and `IValidateOptions<T>` plus `ValidateOnStart()` for required configuration. Every property MUST have a named consumer and applicable validation. Existing automatic cleanup defaults MUST remain unchanged; no timer or automatic prune driver is introduced.
- **FR-017**: Operator documentation MUST explain enrollment/quiescence, coordinated upgrades, unsupported legacy/custom participants, exact dry-run/execute use, conservative refusal, lifetime retention, partial failure and recovery. Public API changes MUST include migration and semantic-version assessment.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Inspection and dry-run MUST delete no packages. Repeated execution against identical current state MUST be idempotent.
- **OSR-002**: Membership/protection publication MUST be crash-safe. Active and LKG materialization MUST remain available across failed reconciliation, pruning and process restart; incomplete authority MUST prevent deletion rather than guess.
- **OSR-003**: Package acquisition/source trust and integrity validation MUST keep their existing boundaries. Maintenance MUST use configured authority only and MUST NOT broaden trusted feeds or log credentials.
- **OSR-004**: Structured diagnostics MUST correlate maintenance operations and explain admission/protection refusal, retained graphs, actual deletion and partial failures. Logs/metrics/health MUST distinguish completed, refused and failed operations; automatic cleanup metrics MUST NOT count planned candidates as physical deletes.
- **OSR-005**: Unit and real-component boundary tests MUST cover all changed runtime/store/loading contracts. First demonstrate non-destructive two-composition admission and before-read protection, then actual deletion in exclusively owned isolated roots. Retain causal synchronization and negative/regression controls rather than timing-only assertions.
- **OSR-006**: Acceptance MUST include cross-process lock/admission races, crash boundaries, distinct-state/offline-LKG graphs, aliases and path replacement, stale-preview replanning, partial IO/cancellation and repeated deletion on Linux/macOS as available and Windows. A primitive lock probe or mocked planner is insufficient physical-deletion evidence.
- **OSR-007**: Nuplane MUST remain host-neutral with no Elsa or CShells dependency. This unit MUST consume existing unload lifecycle mechanisms conservatively and MUST NOT duplicate Foundation #2362's unload policy, cache eviction or Attention work.
- **OSR-008**: No customer/user package store MAY be mutated for development acceptance. Package publication and downstream final acceptance MUST follow the program's ordered release/provenance gates; this spec alone accepts neither.

### Key Entities *(include if feature involves data)*

- **Physical store root**: The filesystem location whose package inventory and accesses share one coordination authority, including its supported aliases.
- **Root enrollment**: Durable protocol identity and complete membership of persistent states associated with that root.
- **Protection snapshot**: Complete active/LKG graph paths for all enrolled states plus live uses, or an explicit unknown state.
- **Graph-use lease**: A unique immutable set of exact package paths protected for a reader's actual lifetime.
- **Retention plan**: Inventory classification under current protection and explicit retention policy; a preview is not deletion authority.
- **Execution report**: Actual per-candidate results and aggregate operation outcome, including partial failures and refusals.

## Assumptions and Boundaries

- Root engineering selected multi-state coordination within standing user authorization; no additional human decision is a prerequisite to design. Complete implementation and safety proof remain required before readiness.
- An operator can stop all users and identify every persistent state for existing-root enrollment. This is a fully quiescent cutover, not a rolling marker-publication race: no user resumes until membership is durably complete. Ordinary root checks do not establish that already running unenrolled readers have stopped. Nuplane cannot discover a legacy or external process that ignores the protocol; such users must not share an enrolled store during maintenance. This is an explicit supported-client limitation, not a claim that the runtime detects them.
- Custom implementations without required optional protection capability remain supported for ordinary non-enrolled operation; their use cannot silently authorize deletion from an enrolled root.
- Manual inventory/preview is useful independently, but report-only, always-refused, single-state-only or offline-only pruning does not satisfy this feature's agreed end-to-end outcome.
- Retention values are explicit operator inputs; no unsolicited age/version expiry policy is selected.
- API signatures, filesystem identity mechanism, state upgrade/recovery transitions and lease ownership handoff require source-grounded design in the plan before code.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In all acceptance fixtures, default inspection and absent-retention operations remove zero packages and report every installed/protected/unknown entry accurately.
- **SC-002**: Two independent persistent states sharing one root can reconcile, load and recover while all active/LKG and in-use graphs remain readable; a third unregistered or locally opted-out client cannot bypass enrolled-root protection.
- **SC-003**: Explicit maintenance physically removes eligible unused installs, with zero protected-path deletions across overlap, stale-preview, dependency-sharing, alias and concurrent-operation scenarios.
- **SC-004**: Every reported deletion matches an absent on-disk install, and every injected deletion failure/cancellation is reported accurately without erasing earlier outcomes.
- **SC-005**: All supported crash-boundary scenarios recover either complete authority or a clear refusal, preserving active/LKG recovery. A live retired context remains readable; genuinely ended use eventually ceases to pin otherwise eligible installs.
- **SC-006**: Operators can follow documented enrollment, preview, execution and refusal recovery using the supported public maintenance capability; platform acceptance includes actual Windows deletion behavior and existing ordinary-operation regressions remain green.
