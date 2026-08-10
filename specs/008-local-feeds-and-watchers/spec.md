# Feature Specification: Local Directory Feeds + Watchers (No Separate "Drop Folder")

**Feature Branch**: `008-local-feeds-and-watchers`  
**Created**: 2026-03-04  
**Status**: Draft  
**Input**: User description: "Analyze current Nuplane runtime design and specs, focusing on feed resolution and directory drop-folder behavior. Produce a new/updated specification markdown file describing a design where drop folders act as feed sources and are observed in real time, and where configured feeds are also polled. The spec should address the reported exception when no feeds are configured and a package is dropped. Do not implement code; only write the spec (md) into an appropriate location in /specs (suggest next phase or new spec). Include requirements, user stories, edge cases, and acceptance criteria. Consider how file watchers might apply to local directory feeds, and how polling applies to all feeds."

## Glossary *(normative definitions for this spec)*

- **Feed**: A configured source of packages that Nuplane can use to *acquire artifacts* and/or *derive desired state*.

- **Feed kind**: The category of a feed. This feature defines (at minimum):
  - **Remote feed**: Network-accessed package source.
  - **Local directory feed**: A folder on disk containing `.nupkg` artifacts.

- **Local directory feed (legacy alias: “drop folder”)**: A local feed that presents `.nupkg` files as package artifacts and (optionally) as desired-state inputs.
  - The term “drop folder” is considered legacy/user-facing wording; internally and in documentation, Nuplane standardizes on “local directory feed”.

- **Eligibility**: A feed’s capability in a particular role:
  - **Discoverable**: can be scanned to produce desired package requests (e.g., enumerate `.nupkg` files).
  - **Acquirable**: can provide the actual package artifact bytes for activation (e.g., local `.nupkg` file path).
  - **Watchable**: can emit near real-time change signals suitable for triggering reconciliation (typically local directory feeds).
  - **Pollable**: can be checked periodically to ensure convergence (remote feeds and local feeds).

- **Desired state**: The set of packages that Nuplane intends to have active, computed from configured inputs.

- **Resolution**: The decision-making step that selects an eligible feed for each desired package request.

- **Acquisition**: The step that determines and records the artifact *location* for activation.
  - For a local directory feed, this means locating the `.nupkg` file on disk and **extracting its contents** to a versioned install directory under the configured package install root (`{PackageInstallRoot}/{feedName}/{packageId}/{version}/`, superseded by issue #56) so that the loader can resolve assemblies from the extracted content. The `ResolvedPackage.InstallPath` MUST point to this extracted directory, NOT a synthetic path.
  - For a remote feed, this is typically a remote feed identifier/URI and a concrete version, followed by download and extraction.
  - This feature does not require implementing new NuGet download/extraction mechanics for remote feeds; it requires that directory-originating requests are resolvable without remote feeds, produce a real on-disk install path, and produce explicit outcomes.

- **TriggerType**: The category of driver that initiated a reconciliation cycle: `Scheduled`, `DirectoryChange`, `Manual`, or `Startup`.

- **TriggerSource**: Optional stable identifier for where the trigger came from.
  - For `DirectoryChange`, this MUST be the local directory feed name (not a filesystem path).
  - For `Scheduled`, this SHOULD be omitted.
  - For `Manual`, this MAY be an operator-provided label.

- **Observation**: Detecting that an input’s state has changed (watchers for local directory feeds, polling for remote feeds).

- **Convergence**: Reconciling actual state to desired state on a schedule (even if no observation event occurred).

## Refactor Intent & Current-State Mapping *(mandatory for implementation)*

This feature is a **refactoring of the existing codebase**, not a greenfield subsystem.

### Problem being fixed (current behavior)

Today, Nuplane has:

- A directory-based desired-state source (`DirectoryNupkgDesiredSource`) that **produces desired package requests** from `.nupkg` filenames.
- A multi-feed resolver (`MultiFeedPackageResolver`) that **assumes all packages must be resolved via configured remote feeds**.

When no feeds are configured and a `.nupkg` is placed into the directory source, reconciliation fails with an exception similar to:

- `InvalidOperationException: No available feed could resolve package '...'`

This means “local packages” are incorrectly treated as “identifiers that must be fetched remotely”, rather than artifacts already present.

### Target behavior (after refactor)

- A **local directory feed** is a feed: it can both **contribute desired packages** and **provide the artifact** for acquisition.
- Nuplane MUST be able to run with **zero remote feeds configured** and still activate packages provided by local directory feeds.

### Compatibility note (non-normative)

- Existing integrations and docs may still refer to “drop folders”. This feature standardizes the concept as a *local directory feed*.
- The intent is to preserve the current user-visible experience (“drop a `.nupkg` into a folder”) while fixing the architecture so that the folder is treated as a feed (artifact source) rather than merely a list of IDs.

### Implementation guidance (non-normative but explicit)

An implementation agent SHOULD strongly prefer reshaping and reusing existing components rather than adding parallel abstractions:

- Evolve the current directory source registration toward a “local directory feed” concept.
- Evolve the current resolver/acquisition path so that it can satisfy a request from **local feed artifacts** without consulting remote feed definitions.
- Keep reconciliation pipeline stages, transactional/LKG behavior, and observability wiring intact unless explicitly required below.

### In scope

- Unify “drop folder” semantics into the feed model (“local directory feed”).
- Ensure local directory feeds can supply artifacts directly (no remote feed required).
- Ensure all configured feeds are observed:
  - local directory feeds: near real-time observation + scheduled convergence
  - remote feeds: scheduled polling/convergence
- Ensure “no feeds configured” is handled deterministically (idle mode vs fail-fast).

### Out of scope (for this feature)

- Changing Nuplane’s store format, transaction semantics, last-known-good guarantees, or cleanup behavior.
- Introducing new package formats beyond `.nupkg`.
- Designing a full NuGet v3 client implementation (this feature is about architecture and correct source selection, not protocol completeness).
- Major trust-policy redesign (existing allowlisting and trust checks remain; this feature only ensures local feeds participate correctly).

## Conceptual Model *(non-normative)*

This section explains the intended mental model and vocabulary. Requirements below remain the source of truth.

### Core idea: feeds are artifact sources

- A **feed** is any configured source of package artifacts that Nuplane can acquire and activate.
- A feed may be **remote** (networked service) or **local** (directory on disk).
- A feed is not just an “endpoint to download from”; it is also a unit of trust, policy, observability, and troubleshooting.

### Desired state is computed from configured inputs

- **Desired state** is the set of packages that Nuplane intends to make active.
- Desired state may be derived from multiple configured inputs (examples: local directory feeds, feed rules, manifests, explicit package lists).
- Each desired package request MUST carry a source attribution that identifies the originating configured input (for example: which feed or which manifest produced it).

### Resolution chooses an eligible feed; acquisition uses that feed

- **Resolution** selects an eligible feed for each desired package.
  - If the desired request explicitly targets a feed, resolution is constrained to that feed.
  - If the request does not specify a feed, resolution searches across eligible feeds according to deterministic policy.
- **Acquisition** retrieves the package artifact from the selected feed.
  - For a local directory feed, acquisition means “use the local artifact” (no remote download is required).
  - For a remote feed, acquisition means “retrieve from the remote feed”.

### Observation and convergence are separate mechanisms

- **Observation** answers: “has any configured input changed?”
  - Local directory feeds can typically support near real-time observation.
  - Remote feeds typically do not provide push notifications and require polling.
- **Convergence** answers: “given the current snapshot of all inputs, does actual state match desired state?”
  - Scheduled reconciliation performs convergence regardless of whether observation detected a change.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Drop a package into a local directory feed and see it picked up quickly (Priority: P1)

As an operator/developer, I can drop a `.nupkg` into a configured **local directory feed** and Nuplane reacts in near real time by reconciling desired state, without me needing to wait for the next scheduled poll.

**Why this priority**: The local-directory flow is the fastest feedback loop for local development and air-gapped or side-loaded deployments. If real-time observation isn’t reliable, the feature loses most of its value.

**Independent Test**: Start a host with a configured local directory feed and at least one allowed package ID; add a `.nupkg` file and observe that a reconcile cycle is triggered and the package becomes active (or fails safely with a visible outcome) within a short, bounded time.

**Acceptance Scenarios**:

1. **Given** Nuplane is running with a configured local directory feed, **When** a valid `.nupkg` file is created in that directory, **Then** Nuplane triggers a reconciliation cycle and records an observable outcome referencing the newly discovered package.
2. **Given** Nuplane is running with a configured local directory feed, **When** a `.nupkg` is replaced with a different version of the same package, **Then** Nuplane triggers reconciliation and deterministically selects the desired version according to the directory-feed rules.
3. **Given** Nuplane is running with a configured local directory feed, **When** a `.nupkg` is deleted from that directory, **Then** Nuplane triggers reconciliation and the desired-state change is reflected (removal request) in the next reconcile outcome.

---

### User Story 2 - Poll feeds and converge even without file events (Priority: P2)

As an operator, I can configure one or more feeds (remote and/or local) and Nuplane checks all configured feeds on a schedule to discover and resolve desired packages over time, even if there are no file-system events.

**Why this priority**: Scheduled checks are the reliability backbone. They ensure convergence even when real-time observation misses events, when services restart, or when running on platforms with limited file notification fidelity.

**Independent Test**: Configure one or more feeds and at least one desired package that must be resolved from those feeds; verify that periodic reconciliation occurs and that changes in feed availability produce deterministic, observable outcomes without corrupting active state.

**Acceptance Scenarios**:

1. **Given** at least one feed is configured, **When** Nuplane runs over multiple scheduled intervals, **Then** it triggers reconciliation cycles according to the configured schedule and produces correlation-linked outcomes.
2. **Given** a remote feed is temporarily unavailable, **When** a scheduled reconciliation runs, **Then** affected package resolutions fail explicitly and safely, and unaffected packages continue according to configured feed policy.

---

### User Story 3 - Local-directory-only operation works (Priority: P3)

As an operator, I can run Nuplane with only local directory feeds configured (no remote feeds) and still safely reconcile packages dropped into those directories without startup validation failures or runtime exceptions.

**Why this priority**: This is required for offline/air-gapped scenarios and matches the mental model: “local directory feeds are feeds”. It also directly addresses the reported exception when no feeds are configured.

**Independent Test**: Run Nuplane with no configured remote feeds but with a configured local directory feed, then drop a `.nupkg`. Verify that the system reconciles using local sources only and produces a safe, observable outcome.

**Acceptance Scenarios**:

1. **Given** no remote feeds are configured and at least one local directory feed is configured, **When** a `.nupkg` file is added to the local directory feed, **Then** Nuplane reconciles successfully (or fails safely for policy reasons) and does not throw an unhandled exception.
2. **Given** no feeds are configured at all, **When** Nuplane starts, **Then** it runs in an explicit “idle mode” with an explicit health/diagnostic signal. (This feature selects idle mode rather than fail-fast.)

---

### Edge Cases

- A `.nupkg` file is copied into the directory slowly (appears before the write completes); Nuplane avoids reading partial files and retries safely.
- Multiple file events arrive for the same file (create + change + rename); Nuplane combines these into a single effective change and does not start an unbounded number of reconciliations.
- The directory does not exist at startup but is created later; Nuplane begins observing it once available (or clearly documents a fail-fast choice).
- The directory is on a network share or environment with unreliable change notifications; scheduled checks still converge.
- A `.nupkg` filename doesn’t match the expected naming convention; it is ignored with an observable diagnostic (not a crash).
- A package is dropped that is not allowlisted or violates source trust; the system rejects it deterministically and surfaces the reason.

## Requirements *(mandatory)*

### Assumptions

- Nuplane standardizes on **feeds** as the single abstraction for “where packages come from”.
- A feed may be:
  - **remote** (e.g., NuGet v3 service index), or
  - **local directory** (a folder containing `.nupkg` artifacts).
- Local directory feeds are eligible for real-time observation (watchers) in addition to scheduled polling.
- If a package can be satisfied from a local directory feed, Nuplane does not require any remote feed to be configured.

### Functional Requirements

- **FR-001**: The system MUST support configuring feeds of multiple types, including remote feeds and local directory feeds.

- **FR-002**: The system MUST treat local directory feeds as first-class sources that can both (a) contribute desired-state requests and (b) provide eligible artifact locations for acquisition/activation.

- **FR-003**: The system MUST detect changes in configured local directory feeds in near real time and trigger a reconciliation cycle when `.nupkg` file state changes are detected (create/update/delete/rename).

- **FR-004**: The system MUST continue to support scheduled reconciliation, and scheduled reconciliation MUST apply to all configured feeds (remote + local) to ensure convergence even when real-time detection is unavailable or misses events.

- **FR-005**: The system MUST define deterministic “directory change → reconciliation trigger” behavior:
  - repeated change notifications for the same underlying file operation MUST not cause an unbounded number of reconciliation cycles,
  - incomplete/partially-written files MUST not be processed as valid package inputs,
  - and repeated identical directory states MUST produce identical reconcile outcomes.

- **FR-006**: The system MUST support running with zero configured remote feeds when at least one local directory feed is configured.

- **FR-007**: The system MUST NOT fail startup validation solely due to the absence of remote feeds.

- **FR-008**: When no eligible feed (remote or local) can provide an artifact for a desired package request, the system MUST fail that package’s resolution with an explicit, actionable outcome (not an unhandled exception).

- **FR-009**: If no feeds are configured, Nuplane MUST run in an explicit “idle mode” and MUST emit a clear diagnostic/health signal indicating that no reconciliation inputs are configured.

- **FR-010**: The system MUST attribute each desired package request to its originating feed and surface that attribution in reconcile diagnostics.

- **FR-011**: The system MUST expose an operator-visible record of why reconciliation ran (scheduled vs directory change), suitable for troubleshooting and auditing.

- **FR-012**: For any desired package that originates from a local directory feed, Nuplane MUST treat that local feed as an eligible acquisition source. Specifically, the resolver MUST locate the `.nupkg` artifact in the feed directory, extract it to a versioned install directory under the feed path, and set `ResolvedPackage.InstallPath` to the extracted directory so that the loader can find assemblies on disk. A synthetic or placeholder path MUST NOT be used.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation triggered by directory changes and by scheduled runs MUST be deterministic and idempotent for identical observed inputs (same local file set + same remote feed state + same configuration).

- **OSR-002**: Package activation MUST preserve transactional last-known-good behavior: failures during acquisition/validation/activation MUST not corrupt active state.

- **OSR-003**: Source & supply-chain integrity MUST remain explicit for local directory feeds:
  - only configured, trusted local directory feeds MAY contribute desired state,
  - packages discovered from a local directory feed MUST still be subject to source trust rules (allowed source names and allowed package IDs),
  - and any integrity checks available for local artifacts MUST be run before activation.

- **OSR-004**: Observability MUST cover both triggers and outcomes:
  - logs for each trigger (scheduled vs directory change) with a correlation identifier,
  - diagnostics for invalid packages or unreadable directories,
  - measurements for trigger counts, reconciliation durations, and failure reasons,
  - and a health signal that indicates when real-time directory detection is degraded/unavailable.
  - degraded watcher establishment MUST surface via:
    - health: overall runtime health transitions to Degraded and the operator snapshot includes a degraded reason via `source-outages:N` (N>0) for the cycle(s) where observation is degraded,
    - logs: include the local directory feed name and last watcher error with correlation context,
    - and scheduled convergence remains active.

- **OSR-005**: The feature MUST include automated tests:
  - tests for deterministic combination of repeated directory change notifications and partial-write handling,
  - tests proving local-directory-only mode works with zero remote feeds,
  - and a regression test for the reported “no feeds configured + package dropped” exception (fails before fix, passes after).

- **OSR-006**: If real-time directory detection cannot be established (permissions, path invalid, OS limitations), Nuplane MUST fall back to scheduled convergence and MUST emit a degraded signal; it MUST NOT silently stop reconciling.

#### Requirement Clarifications (non-normative)

- FR-002/FR-012 "acquisition" for local directory feeds is satisfied when the resolver (a) locates the `.nupkg` in the feed directory, (b) extracts it to a versioned install directory on disk, and (c) sets `ResolvedPackage.InstallPath` to that extracted directory. The loader relies on `InstallPath` pointing to a real directory containing assemblies; a synthetic or placeholder path will cause a loader boundary failure. This spec does not require introducing new remote acquisition mechanics for remote feeds.

## Key Entities *(include if feature involves data)*

- **Feed (Remote/Local)**: A configured source of packages, either remote (network) or local directory.
- **Reconcile Trigger**: A recorded event that caused a reconciliation cycle (scheduled run vs directory change), including time and correlation identifier.
- **Directory Observation Status**: Health-relevant state describing whether real-time observation is active, degraded, or disabled, with last error when degraded.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In a validation run of 100 local directory feed events (create/replace/delete) across at least 10 distinct packages, at least 95% of changes cause a reconciliation cycle to start within 2 seconds of the directory change being completed.

- **SC-002**: In a validation run where real-time directory change notifications are intentionally unreliable/unavailable, scheduled reconciliation still results in 100% of eligible directory changes being reflected in reconciliation outcomes within one configured scheduled interval plus one retry window.

- **SC-003**: In a validation run with zero configured remote feeds and at least one configured local directory feed, dropping a valid `.nupkg` produces 0 unhandled exceptions across 50 consecutive drop events.

- **SC-004**: In a validation run with no feeds configured, 100% of startups enter idle mode and surface an explicit health/diagnostic signal within one scheduled reconciliation interval.
