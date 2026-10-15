# Manual package-store maintenance

Nuplane can prune recognized, completed installs from a shared package store only
after every client participates in its coordinated admission protocol. Ordinary
Nuplane configuration remains supported. It does not become eligible for pruning
just because the maintenance package is installed.

## Configure participation

Configure the package root and each client's state file through trusted host
configuration. Every client sharing that physical root must declare the same
complete roster through `UseCoordinatedPackageStore(completeRoster,
allClientsParticipate: true)`. A member has a configured root label, a stable
member ID and an absolute state-file location. Include offline clients.

The complete-roster flag is the operator's assertion. Nuplane cannot discover
undeclared legacy clients or stop them on the operator's behalf. Stop every user
of the store before enrollment, migration or enrollment recovery, and keep them
stopped until the result is verified `Complete`.

Add the optional in-process surface with
`services.AddNuplanePackageStoreMaintenance()` from `Nuplane.Admin`.
`AddNuplane` and `AddNuplaneAdmin` do not enable it automatically. The built-in
native admission provider is required. Unsupported filesystems, custom components
that cannot participate and uncertain identity/locking evidence refuse access.
Do not bypass those refusals by deleting control files.

## Enroll and upgrade an existing store

1. Call `PreviewEnrollmentAsync(rootLabel)`. The report is a non-quiescent
   observation: it does not establish exclusive access or authorize a later write.
   Retain the observed root identity, epoch, `CurrentMemberIds`, state identities
   and state-body digests. `CurrentMemberIds` is empty for an unenrolled root and
   null when the current roster could not be observed.
2. Stop every store user, including offline/restarting services, and confirm the
   complete configured roster. For first enrollment call `EnrollAsync` with epoch
   `1`, an empty expected current roster, the complete intended roster and
   `QuiescentCutoverConfirmed = true`.
3. Unknown historical recovery closure requires complete, source-bound graph
   evidence or explicit retirement of that member's unknown historical recovery
   selections through `RetireLegacyRecoveryMemberIds`. Retirement still requires
   verifiable active closure and preserves active protection. Neither missing
   metadata nor a successful ordinary reconciliation proves an empty LKG closure.
4. Verify a `Complete` result before restarting clients. A refusal or interrupted
   operation leaves the store unavailable to ordinary coordinated package IO.
   Observe the persisted state and use `RecoverEnrollmentAsync` with its exact
   current epoch/roster while quiescence continues.

For roster changes or state-file relocation, configure the complete intended
roster and call `MigrateAsync` using the exact observed current roster, physical
root and epoch. Removing a member requires its ID in `RetireMemberIds`; omission
from the intended roster alone is insufficient. A successful migration advances
the enrollment epoch. An interrupted migration is recovered explicitly, using
the persisted current roster rather than guessing it from the new configuration.
Keep prior state locations available until recovery or migration completes.

Recovery evidence is descriptive input, never a filesystem capability. Nuplane
independently verifies its source slot, state file identity, body digest, graph
selection and native install identities while holding ownership. Do not edit the
state JSON to manufacture evidence or clear uncertainty.

## Preview and execute

`InspectAsync` returns the current membership, protection, use records and bounded
inventory. `PruneAsync` defaults to `Preview`. Omitting retention retains every
install. `PackageStoreRetentionPolicy(N)` keeps the newest `N` inactive,
unprotected version groups per package across feeds. Equivalent NuGet versions
form one group; every install tied in a retained group is kept. Active, LKG and
live-use protection always wins over retention.

```csharp
var policy = new PackageStoreRetentionPolicy(2);
var preview = await maintenance.PruneAsync(new(Retention: policy), cancellationToken);
if (preview.Status != "Preview" || preview.RootIdentity is null ||
    preview.EnrollmentEpoch is null || !preview.InventoryComplete)
    throw new InvalidOperationException("The store is not ready for execution.");

// Request explicit execution only after the operator has reviewed the preview.
var confirmation = new PackageStorePruneConfirmation(preview.RootLabel,
    preview.RootIdentity, preview.EnrollmentEpoch.Value, policy);
var result = await maintenance.PruneAsync(new(preview.RootLabel, policy,
    PackageStorePruneMode.Execute, confirmation), cancellationToken);
```

Execution freshly verifies membership, every state, complete catalog protection
and inventory under retained ownership. After exact root-and-epoch confirmation,
it runs verified graph-use recovery without waiting, keeps live uses protected,
revalidates the catalog, then inspects and replans. `InspectAsync` and Preview remain read-only;
a stale use can therefore make their protection result Unknown until confirmed
execution performs recovery. The preview's candidate list is never execution
input. Root or epoch changes refuse the request; newly protected packages survive
fresh replanning. Unknown/staging/control entries, links and identity replacements
are preserved or cause conservative refusal.

Read candidate `Classification`, `Attempt` and `PhysicalOutcome` separately.
`Eligible` describes policy only. Preview has no physical outcome. Executed results
retain observed deletions, quarantines, partial removal and journal phases even
when later work fails or cancellation arrives. Cancellation before admission can
throw normally; after physical work begins it returns observed partial facts.
Repeat explicit execution can resume verified recovery work. Do not manually erase
its journal or quarantine.

## Optional HTTP endpoints

Call `MapNuplanePackageStoreMaintenance()` from `Nuplane.Admin.Api` explicitly and
apply the host's authorization policy to the returned route group. The default
prefix is `/nuplane/admin/store`:

| Method | Suffix | Operation |
|---|---|---|
| GET | `/enrollment?rootLabel=default` | Non-quiescent enrollment observation |
| POST | `/enrollment` | Explicit quiescent enrollment |
| POST | `/enrollment/migrate` | Explicit quiescent roster/state-location migration |
| POST | `/enrollment/recover` | Exact incomplete enrollment/migration recovery |
| GET | `/inspection?rootLabel=default` | Current inspection |
| POST | `/prune` | Preview by default, explicitly confirmed Execute |

Requests select configured labels and contain no arbitrary root or candidate paths.
Unknown JSON fields are rejected. Invalid requests return 400, conservative
admission refusals return 409, and failed execution returns 500 with its observed
report. A returned cancellation/partial report must still be inspected.

No API registers a pruning timer. Collectible loading retains graph use until
actual context lifetime ends; the passive lifetime observer does not initiate GC,
unload or deletion. Noncollectible uses remain protected for process lifetime.

See [API and migration notes](package-store-maintenance-api-migration.md) and the
[qualification evidence](../specs/030-safe-package-store-pruning/evidence/public-maintenance.md).

For correlated logs, actual physical-outcome metrics and process-local operational health, see [maintenance observability](package-store-maintenance-observability.md).
