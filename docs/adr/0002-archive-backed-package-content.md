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
- The coordinated installer copies its staged archive into the prepared directory and checks that the copy has the same sha512.

The archive is published with the install and shares its protection and pruning lifetime. Pruning deletes an install directory as one unit, through the full manifest bound for that candidate.

`.nuplane-archive` is a reserved root entry name:

- The coordinated installer refuses a package that ships it, as it already refuses packages that ship `.nuplane-ready` and `.nuplane-content-hash`.
- The ordinary installer refuses it too. It keeps its released behavior of overwriting package entries named like the marker and hash files.

Rejected: reading the archive from the feed while it still matches. Remote downloads are deleted after extraction, and a directory feed may replace or drop a version while it stays active.

### The active package record is the trust anchor

`ActivePackageDescriptor.ArchiveHash`, projected as `ActivePackage.ArchiveHash`, records the acquisition hash when the package is activated. It is persisted in the state file and is part of the descriptor's activation shape.

The reader compares the archive bytes against this value, never against `.nuplane-content-hash`. A file beside the archive can be replaced together with it.

### Verify the exact bytes that are served

`IPackageArchiveReader.OpenAsync(ActivePackage)` works in four steps:

1. It reads the whole archive with ordinary file access. `MaximumArchiveBytes` bounds the read.
2. It hashes those bytes and compares the result with `ArchiveHash`.
3. It returns a `PackageArchive` that serves entries only from the verified in-memory copy.
4. Any failure throws `PackageArchiveException` with a `Reason`: `HashNotRecorded`, `ArchiveMissing`, `ArchiveTooLarge`, `HashMismatch` or `InvalidArchive`. The failure is also logged as a warning.

No filesystem semantics take part in the safety argument, so the reader needs no qualification. It reads without store admission, even when the store is enrolled. Published installs are never written in place, so a concurrent prune can only make the read fail as missing or truncated, and both are refusals.

### Entry scope and paths

- Every file entry is exposed, and the host decides which prefix it serves.
- Names are used as the archive stores them, as Nuplane extracts them. Current `dotnet pack` stores names literally, so no percent-decoding is applied.
- Lookups are case-insensitive. A request path accepts `/` or `\`.
- These are refused, and an entry that fails them is not reachable: rooted paths, empty segments, `.` and `..` segments, `:` and NUL.
- When two entries share a path after normalization, neither is exposed.

### Selection and coexistence

The reader is a separate service that `AddNuplane` registers in every composition. A host selects it by calling it, with no option or profile switch. `PackageContent`, with an operation borrow or graph-use lease, remains the extracted-store reader for enrolled hosts.

### Caching

Verified archives stay in an in-memory LRU cache keyed by package, version, archive path and hash.

- `Nuplane:PackageArchives:CacheBytes` bounds the cache: 64 MiB by default, and 0 turns it off.
- Nothing is cached on disk, so the cache adds no filesystem requirement.

## Consequences

### Positive

- Hosts in containers and on xfs, btrfs, tmpfs and Windows arm64 read admitted package content.
- Tampering is detected by byte comparison, independently of the filesystem.
- Retention and protection rules need no change.

### Negative and accepted trade-offs

- Each install stores its archive as well as its extracted files, so it uses roughly twice the disk space.
- A verified archive is held in memory while it is cached or in use.
- Installs made, and packages activated, before this change have no kept archive or recorded hash. They are refused until they are reinstalled. Before GA, no backfill is provided.
- The reader trusts the state file. A writer who can change the state file can also change which packages are active, so this matches the existing trust boundary.

## Validation

- Unit tests cover verification, each refusal reason, path normalization and the cache.
- `AddNuplane` acceptance tests with a directory feed and no enrolled store cover serving content and refusing modified, replaced and missing archives. In those tests, replacing the archive and its hash file together is still refused.
- A coordinated-install test checks that the archive is kept and that the reserved name is refused.
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
