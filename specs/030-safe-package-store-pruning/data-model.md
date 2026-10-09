# Data model

All persisted records are schema-versioned. Unknown/absent data is distinct from KnownEmpty. Immutable projections copy caller collections. Protocol files live in a reserved control directory, outside recognized completed install entries.

| Entity | Fields and validation |
|---|---|
| PhysicalRootIdentity | Platform/provider, volume/device identity, open-directory file identity. Obtained from native metadata; display path is descriptive. No lexical containment authority. |
| StateSlotIdentity | Physical parent-directory identity, native canonical stored basename and versioned observed name profile (encoding, actual case sensitivity and normalization behavior). No final symlink/hardlink ambiguity. Stable across state replacement; moving/renaming a slot requires quiescent migration. Component-wise authority lookup refuses enrolled-to-outside alias transitions. |
| RootMembershipRecord | SchemaVersion, RootIdentity, EnrollmentEpoch, Incomplete/Complete, ordered Members, explicit TargetMemberIds, optional PendingCommit, LedgerDigest. Incomplete retains the complete old/new member union. Complete contains exactly acknowledged targets, no pending, and requires independent validation of every member's exact active/LKG protection. |
| RootMember | MemberId, diagnostic configured locator and tagged binding: Declared, Prospective, ExistingUnprotected or Acknowledged. Prospective binds verified parent identity, observed native name profile and exact requested basename before a file exists. ExistingUnprotected binds an existing canonical slot, observed file identity/body digest and explicit protection absence; legacy StoreStateRecord has no protection revision and its first protected revision is 1. It is never Complete. Acknowledged binds canonical StateSlot, observed file identity, revision/body/protection digests. Observed file identity changes only through a verified transition/recovery. |
| PendingStateCommit | Root/epoch/member, prior membership status and canonical prior-ledger digest, unique publication ID, explicit prior union (AbsentAtProspectiveSlot, ExistingUnprotected or Acknowledged), next revision/body/protection digests, and optional observed staged/backup file identities. The first durable pending record has no artifact identities; after staged/backup files are created, flushed, and reopened, pending is republished with their observed identities before state publication. Backup identity requires a staged identity; prospective prior has no backup; an existing prior requires a backup whenever stage identity is recorded. Artifact identities are pairwise distinct and differ from the prior file identity. Recovery/cleanup may touch only artifacts matching recorded identities; an unbound artifact is preserved/refused. Exact prior with both artifact names absent, or with every bound artifact identity and payload verified, restores Complete only when the prior status was Complete and all members still validate; otherwise it remains Incomplete. Exact next is acknowledged only when it matches the recorded staged identity; missing/third evidence is refused. |
| PackageProtectionRecord | Nullable non-positional StoreStateRecord property; SchemaVersion, RootIdentity, Epoch, MemberId, Revision, completeness, ActiveGraphs, RecoverableGraphs, RetiredGraphs, LegacyUnknownRecovery. Missing property does not normalize to empty. |
| ProtectedGraphSnapshot | GraphId, generation/snapshot ID, root requests, complete selected nodes/edges, disposition and recovery-selection evidence. KnownEmpty has an explicit complete empty projection. |
| PackageInstallIdentity | Package ID, NuGet version, validated root-relative install name, native directory identity, completion identity, optional verified archive hash. Whole directory protection includes lazy support/native files. |
| RetiredGraph | Snapshot identity, retiring revision/epoch, explicit reason and proof it is no longer recoverable. Unknown legacy entitlement cannot be implicitly retired. |
| GraphUseRecord | Schema/root/epoch, unique UseId, immutable complete graph/install identities, sentinel identity, lifetime kind and diagnostic process identity. PID alone is never liveness proof. |
| RootOperationOwner | Non-forgeable reference to held root handle/lock/epoch; Open → Closing → Closed. Counted borrow creation and close linearize. Closing blocks new borrows and waits for old borrows. |
| GraphUseLeaseOwner | Non-forgeable release authority, immutable view and counted read pins. Published → Closing → Released; no release during a pinned read. Context lifetime owners are private to Loading. |
| PruneRequest | Configured root label, optional retention, Preview default or explicit Execute, expected enrollment epoch for Execute, correlation ID. No deletion-path field. |
| Inventory/Plan | Observed root/epoch/revisions, immutable completed installs, unknown entries, protection reasons and retention classifications. Preview is observational, never authority. |
| ExecutionReport | Fresh plan, operation outcome, per-install physical result, errors and completed facts. Quarantined/partial is distinct from Deleted; cancellation preserves prior completed outcomes. |

## Publication transitions

### State-slot name observations

The native provider resolves the configured component beneath a held parent, observes the single-link regular file identity, obtains its actual stored entry spelling, and reopens that canonical component beneath the same parent. Parent, file, and name-profile observations must agree across the operation. A returned full native path may supply a leaf candidate only; it grants no authority and is never reopened. Unix providers enumerate an independent held-parent directory stream rather than infer spelling from a diagnostic path. No managed case folding, Unicode normalization, or guessed Windows short-name expansion supplies identity.

Canonical spelling compares exactly. Native aliases may converge only after provider validation; a case-only rename changes the recorded slot spelling and requires quiescent migration. The observed final file identity is a separate revision token, not part of slot equality. Observing a replacement at the same slot does not itself authorize that replacement: the publication/recovery protocol must acknowledge it.

These internal descriptive records are not directly persisted with default `System.Text.Json` constructor discovery. T009 uses serializer-owned DTOs and validated conversion so internal constructors remain internal and malformed persisted identity data cannot bypass validation.

Missing-state initialization belongs only to explicitly quiescent, Incomplete enrollment after parent authority and root ownership are established. First durably declare all target members, then bind each prospective parent/profile/requested name and persist pending exact next payload/digests before exclusive publication. The first protected state already contains explicit Known closures; canonical slot and file identity are observed and verified after creation. A missing, unreadable, or legacy state never implies KnownEmpty; preview never initializes state. State files may live outside the install root: resolve their parents independently with component-wise no-follow authority, never lexical containment or diagnostic-path reopening.

Existing legacy states follow the distinct ExistingUnprotected prior branch. Protection absence is explicit Unknown evidence, never an empty closure; separately validate complete active and recoverable LKG graphs before preparing protected next content. Exact unchanged unprotected prior can roll back pending only while leaving enrollment Incomplete. During membership changes preserve old-only rows until durable retirement/migration evidence proves their complete active/LKG protection is safely transferred or retired; a smaller target list alone never removes those rows.

Known canonical state slots are unique across member IDs. Equality uses observed physical parent/profile/canonical name without managed folding. Prospective aliases still require native collision checks before publication. Structural pending-prior comparison includes complete nested protection payloads, not merely caller-supplied digest strings; digest verification remains independently mandatory. Equivalent graph collection ordering does not change payload identity. Complete and pending-next candidates reject unresolved legacy recovery even when both closure tags are Known.

### Coordinated transitions

1. **Enrollment:** Unenrolled → Incomplete(epoch) → validate/write all declared members → Complete(epoch). Fully quiescent throughout. Any interruption stays denied until exclusive recovery establishes complete authority.
2. **Membership change:** Complete(old epoch) → Incomplete(new epoch, complete old/new member information) → migrate/retire explicitly → Complete(new epoch). Old participants do not silently rejoin.
3. **State write:** acknowledged prior → pending prior/next → state plus protection replacement → verified next payload at same slot → acknowledged next. Exact old evidence rolls back pending; exact new evidence completes; ambiguous evidence refuses. All state writes follow this transition.
4. **Live use:** acquire exclusive sentinel → publish immutable graph record → first retained read → lifetime ownership → close after read pins and actual lifetime end → stale record cleanup under root. A passive local weak observer runs while collectible associations exist, independently of new admissions; it closes sentinels only after weak death. Process death releases OS ownership; persistent state protection remains.
5. **Prune:** own root → recover/validate authority → lock all members in stable order → reread states/leases/inventory → fresh plan → identity-checked relative quarantine → no-follow recursive removal → report actual facts → release states/root. Never wait for a busy lifetime sentinel while holding these locks.

## Digest and recovery rules

Canonical digests include schema, identities, epoch/revision and deterministically sorted graph/node projections, using the layers below to avoid self-reference. Specify normalization once in the serializer, including case-insensitive package IDs and case-sensitive platform identity bytes. Do not use JSON dictionary iteration order or timestamps as the only authority. Round-trip and byte/digest stability tests must cover equivalent input ordering.

The state-body digest includes every existing state field, including `UpdatedAt`, and excludes only the non-positional protection property. The protection digest binds that body digest and every persisted protection field except its own digest. The root-ledger digest binds every persisted ledger, member and pending-publication field except itself, including diagnostic paths as record data. Each layer uses domain-separated, length-delimited canonical input; dictionary and graph ordering cannot change its result.

### Canonical encoding revision 1

The core digest preimage starts with ASCII `NUPLANE-CANONICAL\0`, a UInt32-big-endian length-prefixed strict UTF-8 domain (`StoreStateBody`, `PackageProtection`, or `RootMembershipLedger`), and UInt32-big-endian encoding revision `1`. Every record field uses a fixed ascending UInt16 tag, UInt32 payload length, then payload bytes. Strings are strict UTF-8 without replacement fallback; nested sequence/map items are individually UInt32-length-prefixed after a UInt32 count. Nullable values have a distinct zero/one presence byte. Integers/enums are fixed-width signed big-endian (Int32 or Int64); booleans are one zero/one byte; GUIDs use RFC/network-order bytes. DateTimeOffset binds both UTC ticks and original offset ticks. Embedded SHA-256 digests use their validated 32 raw bytes. JSON layout, reflection order, culture and platform path normalization never enter the preimage.

State-body tags 1–7 follow the existing seven constructor fields. Protection tags 1–10 are schema, physical root, enrollment epoch, member ID, revision, body digest, active closure, recoverable closure, retired graphs and legacy-unknown marker. Ledger tags 1–8 are schema, physical root, epoch, status, members, targets, retirements and nullable pending commit. Nested records bind every field in their declared encoding order. Each layer omits only its own digest; nested protection digests remain bound by the ledger.

All nested record tags start at 1 and follow the exact order below. PhysicalRootIdentity uses its handle identity's three-field encoding directly. A nested complete protection value uses the same tags 1–10 as its standalone payload and appends its stored protection digest at tag 11. Binding tags are fixed: Declared=0, Prospective=1, ExistingUnprotected=2, Acknowledged=3; Declared has an empty variant payload.

| Nested value | Fields in ascending tag order |
|---|---|
| Physical file/root identity | provider; volume/device ID; file ID |
| Failure | package ID; stage; message; occurrence timestamp; correlation ID |
| Source snapshot | version; capture timestamp; nullable requests |
| Package request | package ID; version range; nullable feed; update policy; source name |
| Active descriptor | package ID; version; nullable feed; nullable source; install path; activation timestamp; activation correlation; graph ID; generation ID; role; root package IDs; dependency-of package IDs; discoverable |
| Activation graph | graph ID; generation ID; ordered root package IDs; ordered node package IDs; activation timestamp; correlation ID; status; nullable failure; nullable node-version map |
| Activation failure | stage; reason; message; nullable cycle path; nullable unsupported asset path |
| Protection closure | knowledge enum; nullable unknown reason; nullable graph sequence (presence byte remains explicit even though knowledge also constrains it) |
| Protected graph | snapshot GUID; graph ID; generation ID; disposition; physical roots; requested-root selections; nodes; edges; nullable recovery evidence |
| Requested-root selection | request; selected-node GUID |
| Node | node GUID; install identity |
| Install identity | physical root; package ID; version; exact root-relative path; directory identity; completion identity; nullable archive hash |
| Edge | from-node GUID; to-node GUID; requested package ID; requested version range; target framework; optional flag |
| Recovery selection | policy ID; source revision; selected-root GUIDs |
| Retired graph | snapshot GUID; graph ID; generation ID; retiring epoch; retiring revision; reason; proof digest |
| Member | member ID; diagnostic locator; binding tag; binding variant payload |
| Prospective binding | parent identity; name semantics; exact requested basename |
| Existing-unprotected binding | state slot; observed file identity; body digest; protection-absent flag |
| Acknowledged binding | state slot; observed file identity; complete nested protection value |
| State slot | parent identity; name semantics; exact canonical basename |
| Name semantics | profile ID; encoding enum; case-sensitive flag; normalization-insensitive flag |
| Retired member | member ID; complete prior acknowledged binding; retiring epoch; proof digest |
| Pending commit | physical root; epoch; member ID; prior binding record; complete next protection value; prior membership status; prior ledger digest; publication ID; nullable staged state file identity; nullable backup state file identity |
| Pending prior binding record | binding tag; binding variant payload |

Canonical map keys sort with `StringComparer.Ordinal` after folding. GUID-based set keys use lexicographic comparison of the 16 network-order bytes. Root identities, request selections and edges sort by lexicographic comparison of their complete framed canonical bytes; this includes all fields and retains equal repeated entries. Snapshot, node and retired-graph GUID keys are unique by construction. Recovery-selected GUIDs use network-byte ordering. These comparators are independent of culture and host filesystem lookup behavior.

The body uses the default serializer's existing optional descriptor/graph null-to-empty normalization. Every other nullable field remains distinct from empty. Case-insensitive dictionary keys and package-ID values use invariant uppercase, checked against ordinal-ignore-case equality; collisions refuse instead of being merged. Dictionary entries sort by canonical key, independent of insertion order. Source/graph map keys follow that comparer rule, while embedded source/graph labels, versions and ranges remain exact. Physical identities, native names, install paths and diagnostic locators remain exact strict UTF-8 bytes without folding or Unicode normalization. Runtime-dependent case behavior must agree across supported target frameworks before revision 1 is released.

Protected snapshots, roots, nodes, requested-root selections, edges, recovery-selected root IDs and retired graphs are set projections and sort by their canonical identity/full tuple. Allowed duplicate requests or edges retain their multiplicity. Existing body list fields remain sequences, including requests, descriptor/activation package lists and failure cycle paths. Ledger members, target IDs and retired members likewise remain ordered sequences. Order-independence does not permit rewriting these sequence semantics. Golden vectors and save/reload checks pin the revision; changing encoding, field tags or case policy requires an encoding revision change.

A pending transition records the prior file identity with its prior revision/digests. Its required next tuple contains revision/digests, because the next file identity need not be known before replacement. Recovery of exact next content at the unchanged slot acknowledges the newly observed identity; an optional staged identity is additional evidence, never a requirement to predict the replacement identity.

The failure-atomic writer's `.tmp`/`.bak` files can be recovery evidence. Do not delete ambiguous evidence before deciding exact old/new publication. A process termination test is not proof of power-loss durability. No multi-file atomic transaction is claimed.
