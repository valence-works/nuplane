# Phase 0 Research — Local Directory Feeds + Watchers

## Decision 1: Represent a local directory feed using `FeedDefinition` + `file://` service index
- Decision: Treat local directory feeds as feed definitions by using `FeedDefinition.ServiceIndex` with a `file://` URI that points to a directory (e.g., `file:///var/nuplane/drop/`).
- Rationale: Minimizes public API surface churn and lets the existing `FeedResolutionOptions.Feeds` list remain the single “feeds” abstraction, while still allowing a deterministic resolver to consider local and remote candidates.
- Alternatives considered:
  - Add a new `LocalDirectoryFeedDefinition` type (rejected for this phase: larger cross-cutting changes to `FeedResolutionPolicy`, resolver contracts, and trust policy plumbing).
  - Keep directory as “desired-state source only” (rejected: does not fix the current exception path when no remote feeds exist and violates the spec’s “feeds are artifact sources” model).

## Decision 2: Directory-originating requests explicitly target the local directory feed
- Decision: For packages discovered from a local directory feed, emit `PackageRequest.FeedName = <localFeedName>` (explicit feed targeting) and keep `PackageRequest.SourceName` as the attribution string.
- Rationale: Guarantees that resolution is deterministic and succeeds even with zero remote feeds configured, and ensures attribution is available for diagnostics.
- Alternatives considered:
  - Leave `FeedName = null` and rely on “search all feeds” (rejected: fails when only local feed exists unless it is present in the candidate list, and complicates determinism for local-only scenarios).

## Decision 3: Preserve “drop folder” UX, but standardize terminology as “local directory feed”
- Decision: Keep existing host extension entry points usable for current samples (`AddNuplaneDirectorySource`) but evolve them to behave as a local directory feed registration.
- Rationale: Keeps the existing sample/app config stable while aligning architecture with the feed model.
- Alternatives considered:
  - Introduce a brand-new extension method and deprecate old immediately (rejected: unnecessary churn for this refactor-only phase).

## Decision 4: Watchers trigger reconciliation via coalesced signals + bounded work
- Decision: Continue using an `IHostedService`/`BackgroundService` watcher with bounded channel capacity and a configurable debounce window; coalesce multiple file events into a single effective “directory changed” trigger.
- Rationale: Prevents reconcile storms and keeps directory-driven behavior deterministic under noisy event streams.
- Alternatives considered:
  - Trigger reconciliation directly from event handlers (rejected: risks unbounded concurrency and reentrancy).
  - Unbounded queue of events (rejected: can grow without bound under rapid file churn).

## Decision 5: Partial-write safety is enforced by a stability probe + bounded retry
- Decision: Treat “file change detected” as a signal to reconcile, but require directory scanning/acquisition logic to avoid consuming partially written `.nupkg` files by using a bounded stability probe (e.g., retry opening/reading metadata with backoff until stable or timeout).
- Rationale: File watchers can observe files before writes complete; safety requires deterministic handling that does not interpret partial artifacts as valid.
- Alternatives considered:
  - Require producers to write-then-rename from a temp file (rejected: cannot be assumed across all environments).

## Decision 6: Scheduled reconciliation remains the convergence backbone for all feeds
- Decision: Keep periodic reconciliation (`ReconciliationHostedService` with `PeriodicTimer`) as the convergence mechanism for both remote and local feeds; watchers are an optimization for low-latency local changes.
- Rationale: Ensures convergence even when file notifications are unreliable (network shares, permission issues, OS limitations).
- Alternatives considered:
  - Watchers-only for local feeds (rejected: fragile; violates convergence requirement).

## Decision 7: Idle mode is explicit and non-fatal
- Decision: When no feeds are configured (no remote feeds and no local directory feeds), the runtime enters an explicit “idle mode” (no-op reconciliation) and emits a health/diagnostic signal.
- Rationale: Aligns with FR-009 and avoids surprising fail-fast behavior.
- Alternatives considered:
  - Throw on startup / fail DI validation (rejected: the spec explicitly chooses idle mode).

## Decision 8: Tests focus on the current regression and the new trigger/eligibility boundary
- Decision: Require:

  - Pass the nupkg path directly as InstallPath (rejected: the loader expects a directory with extracted assemblies, not a ZIP file).
  - Have a separate "acquisition step" middleware extract the nupkg (rejected: increases pipeline complexity; the resolver is the right place because it already knows the feed type and has access to the feed URI).
  - Have the loader extract the nupkg itself (rejected: violates separation of concerns; the loader should not know about feed types or nupkg archives).
- Alternatives considered:
- Rationale: The package loader (`PackageLoader.ResolveMainAssemblyPath`) requires `InstallPath` to be a real directory on disk containing assemblies. A `.nupkg` is a ZIP archive; without extraction, the loader has no directory to scan for DLLs. The original implementation produced a synthetic path (`/packages/{id}/{version}`) for all feeds, which caused `DirectoryNotFoundException` at the loader boundary.
- Decision: When a `file://` feed resolves a package, the resolver MUST locate the `.nupkg` file by conventional name (`{id}.{version}.nupkg`) and extract it to `{PackageInstallRoot}/{feedName}/{id}/{version}/` (superseded by issue #56; the feed directory is never written to). The `ResolvedPackage.InstallPath` MUST point to this extracted directory. Extraction is idempotent (skip if directory already exists).
## Decision 9: Local feed resolution must extract nupkg to produce a real install path
  - a regression test proving "directory-only + no remote feeds" does not throw and yields an explicit outcome,
  - unit tests for watcher coalescing and debounce behavior,
  - unit/boundary tests for partial-write handling and deterministic retry bounds,
  - integration coverage using the existing sample flow.
- Rationale: This feature changes a high-risk boundary (desired sources → resolution) and adds an event-driven driver; both require deterministic regression and boundary tests per the constitution.
- Alternatives considered:
  - Integration-only validation (rejected: slower and less precise for determinism and edge-case handling).
