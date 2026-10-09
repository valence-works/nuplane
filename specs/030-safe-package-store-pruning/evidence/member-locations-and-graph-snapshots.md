# Locked member locators and recoverable graph candidates

Status: local component qualification; hosted qualification and enrollment/runtime integration pending.

## Implemented boundary

`RootMembershipRegistry.WithCompleteMemberLocationsAsync` holds the root and every existing sorted
member lock, rereads the ledger, then replays every persisted absolute locator through the component-wise
native authority resolver. External state parents are supported. Final state links/hardlinks, duplicate native
slots, missing member locks, different reserved authority and changed paths/identities are refused. No member
state payload is read by locator replay. Callbacks must revalidate immediately before a later payload read.
The explicitly quiescent all-Declared Incomplete path maps metadata under the root lock before provisioning
its complete sorted member lock set. These callbacks grant no ordinary admission.

The private transaction retains the native ledger file identity from the same held-file read that decoded
the locked ledger. Revalidation requires both that identity and the exact digest. Only a verified own atomic
publication refreshes the pin, after reopening the expected staged identity. Thus an external member locator
cannot hide an identical-byte replacement of the ledger.

`PackageInstallIdentityReader` observes the existing feed/package/version install layout under a held root,
using no-follow native directories and the actual empty, single-link `.nuplane-ready` marker. It retains and
revalidates the directory/marker handles and computes completion identity in a distinct digest domain. It
performs no package payload reads, enumeration, writes, directory creation or lock acquisition. A supplied
archive hash is previously verified descriptive input, not a hash verification performed by this reader.
Reserved `.nuplane-store` and `.tmp` entries are checked using native identities/name profiles.

`RecoverableGraphSnapshotFactory` copies an actual selected graph, original root requests and an exact
unordered install-identity set into an immutable ActiveAndRecoverable candidate. It checks graph identity,
roots, complete reachability, endpoints, package/version association and known hashes. Original blank or
whitespace root requests retain Nuplane's latest-stable semantics; dependency edges retain native NuGet
range semantics. Empty target-framework text represents a framework-agnostic dependency group. The fixed
policy identifier is `nuplane.startup-use-last-known-good.graph-v1`.

## Actual producer/consumer proof

`CreateCandidate_ActualDependencyGraphAndNativeInstallsSurvivePayloadRoundTrip` uses the actual
`PackageDependencyGraphResolver` and package metadata reader against two fixture-owned native installs.
A blank root request resolves a framework-agnostic dependency with a bare minimum version, normalized by
the producer to `[2.0.0,)`. The native install reader observes those actual graph paths; the factory captures
that graph and the real state serializer persists/reloads the exact identities, original request and edges.
Only package acquisition delegates are substituted. This proves a graph/identity/serialization connection;
it does not prove enrollment, startup selection, historical LKG migration or operation admission.

## Review and causal checks

Root reviewed all delegated code and corrected latest-root/dependency range semantics, known-hash loss,
external-ledger cached revalidation and native ledger identity retention. Independent source review covered
the factory, native install reader, locator replay and CI row accounting. Native tests assert zero member
payload reads and actual held locks; mutation hooks assert that their replacement actually occurred.

Retained negative controls:

- `recovery-snapshot-range-regression-red-v1`: four expected failures before latest-root and dependency-range fixes.
- `member-locator-cached-ledger-red-v1`: expected failure before rereading the actual ledger during callbacks.
- `member-ledger-native-identity-red-v1`: expected failure before retaining the native ledger identity; identical bytes changed the file identity.

The first factory run also exposed a test's incorrect casing expectation. The first locator run had a static
helper compilation error, followed by a test that incorrectly observed an absent prospective file. The first
install-reader run had an overly exact marker-open-count assertion. Those failures are retained; their
corrections preserve input spelling, use the already observed prospective slot and assert that every open
is the actual completion marker.

## Qualification and remaining work

Artifact logs and source-input manifests live under the program's `native-filesystem-gates` artifact folder.
Before the final ledger-identity correction, `member-install-store-suite-v1` passed 413 cases, failed none,
and explicitly skipped 30 OS/casefold cases. `member-ledger-native-identity-green-v1` then passed all 39
applicable locator/publication/binding cases, with one explicit casefold skip, at source manifest
`3a9c568771ac909b780f1f8b34518266895049c198eacc88a45856c5bcf5784b`.

The hosted workflow now requires 25 exact rows for the new native locator/install/real-producer boundary:
16 named native cases, six distinct malformed-input cases, one Unix-only replacement case and two casefold
cases reserved for owned ext4 volumes. Ordinary Unix lanes require 23 passes/two skips; Windows requires
22 passes/three skips. Each owned Linux casefold lane requires all seven selected native cases without
skips. Local validator controls reject missing, duplicate, skipped-required and unexpected casefold outcomes.
Final local source manifest `3a9c568771ac909b780f1f8b34518266895049c198eacc88a45856c5bcf5784b`
passed the net8/net9/net10 core build with zero warnings/errors and the full Store suite: **413 passed,
zero failed, 30 explicit platform/casefold skips**. Build log SHA-256:
`d2f92393ee63635fe32a21a6148357c4f030298bff6b68d048f5c94ab5e81ade`;
Store log SHA-256: `f8d38b559669f66690ce6c6d28ae94a677a3d3ea22d478e943a353d70d9970e9`.
Independent final review found no remaining source blocker and confirmed all four transaction creation
sites pin the post-lock ledger identity. The exact new head must still pass those hosted lanes; prior `590f2b4` success qualifies only its earlier scope.

T025/T029/T063 remain partial. Bound-Incomplete migration, existing-owner publication handoff, full semantic
state verification, exact startup selector consumption, durable Complete enrollment, every runtime write and
admission-before-first-read remain required. Legacy unknown recovery remains explicitly unresolved; ordinary
success must not clear it. No pruning eligibility, runtime lease, deletion or release readiness is established here.

## Reviewed source pins

```text
7e13273b3e8ff326fe44c2b19b892c300f58baf2d40dfd9f9700699780615e8c  src/Nuplane/Store/Coordination/RootMembershipRegistry.cs
47a29607ea56124cb059c912e6d773a85da63fecc52c31a2486aaefcedde37d2  src/Nuplane/Store/Coordination/RootMembershipRegistry.Binding.cs
d0099a0011f028abdb8aa758409438a64af4f4652c8c5d358dd38d7c45e495cb  src/Nuplane/Store/Coordination/RootMembershipRegistry.LocatorReplay.cs
a37c4322496ef720da331671f1d8f62eba72bf62056a3b33d60e61242f07cc76  src/Nuplane/Store/Coordination/PackageInstallIdentityReader.cs
af082c6a1c07032737ddba549c32c4da98f1cf41523ebcd1dcb3c67001a60851  src/Nuplane/Store/Coordination/RecoverableGraphSnapshotFactory.cs
91d13029766d36ee62c83d8ded823061ad358bfb1dca7a09d612d1f5fd9e4818  test/Nuplane.Store.Tests/Coordination/RootMembershipLocatorReplayTests.cs
dea48ab244d3f333cca0765be3b09b9b0de14173fcac3332cfdec43ca99be0e9  test/Nuplane.Store.Tests/Coordination/PackageInstallIdentityReaderTests.cs
0c0d01f1600f60f263be6617a5f56d16d1a84e0a64553172b0407a0a0e388709  test/Nuplane.Store.Tests/Coordination/RecoverableGraphSnapshotFactoryTests.cs
e51cde24d17d157060e05da3eb40c48d3abee7502eaa7e74d2134bb43141a1e3  .github/workflows/validate.yml
```
