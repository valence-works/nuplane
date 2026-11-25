# ADR-0002: Archive-backed package content

- Status: Accepted
- Date: 2026-11-25
- Owner: Sipke Schoorstra
- Related issues: [#145](https://github.com/valence-works/nuplane/issues/145), [#144](https://github.com/valence-works/nuplane/issues/144)
- Builds on: [ADR-0001](0001-lock-file-artifact-provenance.md)

## Context

Hosts read files of installed packages; Elsa Studio serves the browser files of its add-on modules this way. The only protected reader reads the coordinated immutable extracted store, enrolled. Its safety rests on native filesystem semantics, such as held-parent name lookup, inode identity and the name profile. So it qualifies only ext4 and APFS on local disks and NTFS on Windows x64. It refuses overlayfs, which every container uses, as well as xfs, btrfs, tmpfs and Windows arm64.

ADR-0001 defines a canonical artifact hash: `sha512:<base64>` over every byte of the exact acquired `.nupkg`. A byte comparison against that hash proves that content is the admitted package's content, whatever the filesystem.

The owner decided on 2026-11-25 that Nuplane serves admitted content from the acquired archive, checked against that hash.

## Decision drivers

- Read admitted package content on any filesystem a host can write to, with no enrollment.
- Refuse modified, replaced and missing archives with a diagnostic that names the package and the reason.
- Never let retention or pruning remove an archive that an active or protected install needs.
- Keep the extracted-store profile unchanged for hosts that enroll it.
- Keep the consumer API small.

## Decisions

### Keep the archive inside the install directory

Both installers write the exact acquired archive to `<install>/.nuplane-archive` before the completion marker:

- The ordinary installer copies the source into staging, then hashes and extracts that copy.
- The coordinated installer copies its staged archive into the prepared directory. It hashes the bytes as it writes them and refuses unless they have the staged archive's sha512, then checks the written file's length and link count.

The archive is published with the install and shares its protection and pruning lifetime. Pruning deletes an install directory as one unit, through the full manifest bound for that candidate.

`.nuplane-archive` is a reserved root entry name:

- The coordinated installer refuses a package that ships it, as it already refuses packages that ship `.nuplane-ready` and `.nuplane-content-hash`.
- The ordinary installer refuses it too. It keeps its released behavior of overwriting package entries named like the marker and hash files.

Rejected: reading the archive from the feed while it still matches. Remote downloads are deleted after extraction, and a directory feed may replace or drop a version while it stays active.

### The state file is the trust anchor

The state file records each install's archive hash in two places:

- `ActivePackageDescriptor.ArchiveHash`, projected as `ActivePackage.ArchiveHash`, for the active install of each package;
- `StoreStateRecord.ArchiveHashesByPackage`, for every package version the state has activated, active or not. The key is `packageId/version`, folded with Unicode NFC and invariant case in both directions. It leaves out the feed and the install path: a NuGet package version names one artifact (ADR-0001), so no feed, fallback feed, renamed feed, install path or spelling of the install root can miss the record. An entry is never replaced once written.

The rules:

- **First activation:** the first time a package version becomes active, its hash comes from its install's acquisition record, `.nuplane-content-hash`. That record was computed from the archive bytes when the archive was acquired. The value is written to both places.
- **Later activations:** the recorded value is kept from then on. This covers re-resolution of the active install, restarts, and a rollback to a retained install. Re-reading the hash file beside the archive never replaces a recorded value. So replacing an install's archive and its hash file together is refused, whether the install is active or is reactivated later.
- **Digests:** the protection digest covers both places in coordinated compositions. Each field is written only when present, so states without recorded hashes keep their existing digests.
- **Different bytes for a recorded version:** a package version acquired again with different bytes, from any feed, stays refused (`HashMismatch`). This happens only when a version is republished or a second feed carries a different build of it, which ADR-0001 already treats as an artifact change. Publish a new version instead.

Every write of the state sets each descriptor's hash to the recorded value, so the two places always agree. The reader compares the archive bytes against the active descriptor's value. It never compares them against `.nuplane-content-hash`.

### Verify the exact bytes that are served

`IPackageArchiveReader.OpenAsync(ActivePackage)` works in four steps:

1. It reads the whole archive with ordinary file access. `MaximumArchiveBytes` bounds the read.
2. It hashes those bytes and compares the result with `ArchiveHash`.
3. It returns a `PackageArchive` that serves entries only from the verified in-memory copy.
4. Any failure throws `PackageArchiveException` with a `Reason`: `HashNotRecorded`, `ArchiveMissing`, `ArchiveUnreadable`, `ArchiveTooLarge`, `HashMismatch` or `InvalidArchive`. The refusal is logged as a warning when it is made; repeats within the five seconds it is remembered are not logged again.

No filesystem semantics take part in the safety argument, so the reader needs no qualification. It reads without store admission, even when the store is enrolled. Published installs are never written in place, so a concurrent prune can only make the read fail as missing or truncated, and both are refusals.

### Entry scope and paths

- Every file entry is exposed, and the host decides which prefix it serves.
- Names are used as the archive stores them; `\` in a name is read as a separator, as NuGet reads it. Current `dotnet pack` stores names literally, so no percent-decoding is applied.
- Lookups are case-insensitive. A request path accepts `/` or `\`.
- These are refused, and an entry that fails them is not reachable: rooted paths, empty segments, `.` and `..` segments, `:` and NUL.
- When two entries share a path after normalization, neither is exposed.

### Selection and coexistence

The reader is a separate service that `AddNuplane` registers in every composition. A host selects it by calling it, with no option or profile switch. `PackageContent`, with an operation borrow or graph-use lease, remains the extracted-store reader for enrolled hosts.

### Caching

Verified archives stay in an in-memory LRU cache keyed by package, version, archive path and hash.

- `Nuplane:PackageArchives:CacheBytes` bounds the cache: 64 MiB by default, and 0 turns it off. An archive larger than the cache is read and verified on every open, and a cache smaller than the archives a host serves evicts and re-reads them; size `CacheBytes` for the working set. Loads of different archives run concurrently, and each holds up to `MaximumArchiveBytes` until it is verified; a limit on concurrent loads was tried and removed, because a stalled read would hold its slot and block healthy packages.
- Nothing is cached on disk, so the cache adds no filesystem requirement.

## Consequences

### Positive

- Hosts in containers and on xfs, btrfs, tmpfs and Windows arm64 read admitted package content.
- Tampering is detected by byte comparison, independently of the filesystem.
- Retention and protection rules need no change.

### Negative and accepted trade-offs

- Each install stores its archive as well as its extracted files, so it uses roughly twice the disk space.
- A verified archive is held in memory while it is cached or in use.
- `ArchiveHashesByPackage` keeps one entry per package version the state has activated and never removes entries, so it grows by one line for each version a host activates.
- The first activation of a package version trusts that install's acquisition record, as loading its assemblies trusts its extracted files. An install planted in the install root for a version this state has never activated is outside what the reader can detect; Enforce and Strict lock files (ADR-0001) pin that version's hash against the feed.
- Only the open is bounded. A read that stalls on a hung network or FUSE mount holds the shared load until it completes. Each caller's wait is bounded only by its own cancellation token.
- A refusal is remembered for five seconds, so a repaired archive is served again after that delay.
- Installs made before this change have no kept archive and are refused with `ArchiveMissing` until they are reinstalled. Before GA, no backfill of archives is provided.
- A package activated before this change gets its recorded hash at its next reconciliation, from its acquisition record (first activation rule).
- The reader trusts the state file. A writer who can change the state file can also change which packages are active, so this matches the existing trust boundary.

## Validation

- Unit tests cover verification, each refusal reason, path normalization and the cache.
- `AddNuplane` acceptance tests with a directory feed and no enrolled store cover serving content and refusing modified, replaced and missing archives. Replacing the archive and its hash file together is still refused, including after a restart and reconciliation, after a rollback to a forged retained install, when the install root is reached through another spelling, when a renamed feed shares the install directory, and when another feed serves the same version over a forged install.
- A coordinated-install test checks that the archive is kept and served by the reader, and that the reserved name is refused during entry planning.
- Registration tests resolve the reader in ordinary and coordinated compositions and bind `Nuplane:PackageArchives`.
- A pruning test checks that protected installs keep their archive bytes and that pruned installs lose theirs with the directory.
- CI runs the acceptance tests:
  - inside a Linux container, asserting overlayfs;
  - on owned Linux xfs, btrfs, ext4 and tmpfs mounts;
  - on Windows arm64;
  - in the existing platform matrix.

## Revisit conditions

- Revisit if archives too large to hold in memory must be served.
- Revisit if ADR-0001's artifact hash changes algorithm.
- Revisit if a host needs the reader to re-check that a package is still active on every open.
