# Native graph-use publication and admitted acquisition

## Implemented boundary

The internal `PackageGraphUseRecordStore` consumes an already-held root, expected
native identity/epoch and descriptive complete active graph. It verifies the
reserved control directory's identity, canonical name, profile and volume;
exclusively creates and flushes an empty single-link sentinel; and acquires its
native exclusive lock without waiting before staging the record. The strict
codec's exact bytes are flushed and reopened, then atomically published with
no-replace semantics and reopened again. Stage handles close before publication,
including on Windows. Final record identity/bytes/digest, absent stage,
sentinel and root/control bindings must all verify before ownership returns.

The returned internal owner retains the sentinel file wrapper and lock. Release
leaves immutable record/sentinel artifacts for separate stale-use inspection.
Cancellation and failures, including a provider that publishes and then throws,
return no owner and release unreturned resources. Cleanup errors preserve the
primary error and uncertain ownership evidence. The primitive neither acquires
root/member locks nor grants package-read or deletion authority.

`PackageGraphUseInstallBinding` runs inside the existing fully verified native
root callback. It replays each original absolute install locator under the exact
locked Complete ledger and independently walks its feed/package/version suffix
relative to the held root, retaining the completion marker. The suffix is only a
hint: both walks must identify the same native directory. Duplicate paths/native
installs are refused. All observations remain retained and replayable until
callback-local disposal. No package payload/hash bytes are read.

The complete candidate join preserves the exact selected node objects and their
original paths rather than flattening native observations into package/version
pairs. The shared graph factory validates full closure, request multiplicity,
edges, roots, archive-hash agreement and deterministic graph ID. It now supports
active-only selection without inventing a recovery revision. Root nodes must
name the same exact install paths as their selected nodes. These path checks
matter because the deterministic graph ID omits install paths.

`PackageGraphUseLeaseAcquisition.AcquireForRootAsync` joins binding, publication
and counted ownership for a complete graph in one physical root, matching this
work unit's scope. It constructs the immutable snapshot from the validated
complete selection, publishes and revalidates native evidence, then waits for
the root callback's final full member-state replay before returning any read
lease. Replay refusal releases unreturned sentinel ownership. Expired and
path-restricted borrows cannot expand into root-wide acquisition. Root-operation
release does not release the retained graph sentinel; closing the graph owner
drains read pins before unlock.

## Local proof and review

The first combined native gate passed 44 cases with zero failures/skips. After
independent review identified the observation/graph path-pairing gap, the exact
join and root/node path checks were added. The acquisition gate then passed
**48 cases, zero failures/skips**: 19 native publication/binding/acquisition,
22 graph-factory and seven existing root-callback cases. Its unchanged 865-input
manifest SHA-256 is
`942fcb63d2d1c664683714d22ac9efc3acc2cc95b65e74705d15676acfefe1a3`;
TRX SHA-256 is
`0df454ea18dd823cf04360376bb3b01eecf9139a1d221608b78e88b404352367`.

The real acquisition fixture has two independently persisted enrolled states and
an actual dependency resolver result. Tests reopen the native published record,
read a dependency under a lease pin after releasing root-operation ownership,
verify native sentinel contention until pin drain, and alter another member
after publication to prove final replay refuses and releases the unreturned
sentinel. Publisher tests cover actual busy locking, no-replace races, staged
byte tampering, sentinel tampering, invalid input before mutation and cancellation
before/after publication.

A compiled causal control removed only the comparison between the full-path
native target and root-relative completed-install identity. The changed-path,
same-graph-ID regression then failed because no exception was thrown. Source
was restored byte-for-byte. Control TRX SHA-256 is
`5dc1219d4a51b96f9f90044066ed81f822d8cb19059b134553e39ba3af66291a`.
Independent and root source review closed the pairing finding; later final
verification must run restored source, not the control build.

The hosted workflow adds an exact-name/multiplicity gate for all 19 native cases
on each supported lane. Its parser accepted the actual selected TRX and rejected
seven missing/duplicate/skipped/failed/unexpected/empty/substituted-result
controls. All 20 embedded Python blocks compile and the new shell step parses.
Hosted execution of this increment remains required.

## Passive lifetime observer and final local qualification

The passive observer now waits on collectible-work transitions while idle and
creates its timer only while collectible ownership remains pending. Concurrent
observers atomically claim each dead lifetime once. The last claim stops polling
even if a claimed release is still draining; a subsequent registration restarts
observation. Claimed release tasks drain during disposal, while unclaimed and
failed ownership remains process-retained for a later observer or diagnosis.
A release-completion wake preserves the existing pending timer wait rather than
starting a second `PeriodicTimer` wait.

Root and independent source review found no remaining concrete product defect.
The initial observer gate exposed a definite-assignment compilation error; the
next exposed a test-held strong reference preventing collection. Both failed
runs are preserved. The corrected tests use a no-inline lifetime holder and add
live-association timer disposal/retention/resumption coverage. The final focused
run passed **20 cases with zero failures/skips**. Timer creation is observable in
that disposal test; exact private waiter installation is not synchronized, so it
does not alone prove that internal branch deterministically.

A second compiled causal control changed only `nextTick ??=` to `nextTick =`.
The abandoned-wait regression failed with the expected `PeriodicTimer`
`InvalidOperationException`. The original source was restored byte-for-byte
(SHA-256 `2fb38c704b8d2cc9d39c46fea2770d117b2390c9dd4325210cfb1ffefffe4254`);
control TRX SHA-256 is
`19a166f414af90645988293ef8dc932a0d4400cc82880d84deebc7ff71fea8d1`.
The test-only final review confirmed the holder and retention/resumption coverage;
the timer-wait synchronization limitation above remains explicitly recorded.

After both controls were restored, the final Release Loading/core build passed
on **.NET 8, 9 and 10 with zero warnings/errors**. The complete Store suite passed
**584 cases, zero failures, 38 explicit platform skips**. Both gates retained
the identical unchanged 866-input manifest SHA-256
`9c9f9cb9f39603c764ed234bcfbd239eadebcbac256ab63eb3d19217f097bbbd`.
Final Store TRX SHA-256 is
`0481bbaa464d6bec2d1de2deaa4e60c70d59758e83074f08be6de60c3ed8309a`.
The pre-existing `RootMembershipRecordTests` xUnit2031 analyzer warning remains
in the test project; it is not a production build warning.

The final workflow's 20 embedded Python blocks compile and both affected shell
steps parse. Actual final-suite results qualify exactly **19 native publication,
binding and acquisition cases** and **17 lifetime/observer cases**. Each validator
rejects all seven missing, duplicate, skipped, failed, unexpected, empty and
substituted-result controls. Workflow SHA-256 is
`6ccebffc2f7b95fe91f0d007ff1e30f676b94b92ce7049b282859a76907cb2ae`.
Hosted execution on every platform remains pending for this new increment;
older-head success does not qualify these changes.

## Remaining acceptance

T036 and T047 remain open. This internal acquisition path is not yet registered
as the public provider or connected to production Loading and every retained
reader. Nonblocking stale-use inspection/reaping, actual overlapping generations
in two DI compositions before first retained package I/O, remaining scoped
drivers and manual pruning/quarantine/crash recovery still require their own
proof. Fixture reads here do not qualify the actual Loading boundary. No
recursive deletion is introduced; stable releases and Foundation host/e2e
adoption remain downstream program requirements. Accepted tasks remain 32/128.
