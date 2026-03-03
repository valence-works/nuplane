# Nuplane — Roadmap & Master Specification

## Vision

**Nuplane** is a clean, OSS, host-neutral **NuGet runtime control plane** for .NET applications.

It provides:
- NuGet v3 feed + local directory desired-state sources
- Deterministic local package store
- Desired vs actual reconciliation (polling-based)
- Transactional per-package updates with LKG fallback
- Change events for host integration (e.g., Elsa, CShells)

It does **not** define a plugin programming model.
It does **not** impose activation semantics.
It is infrastructure only.

## Phase 1 Implementation Status (2026-03-02)

Current repository behavior aligns with the Phase 1 runtime baseline:
- deterministic desired-vs-actual reconciliation with idempotent repeat cycles
- per-package transactional apply flow with last-known-good preservation on failures
- snapshot fallback for desired-source outages with explicit degraded cycle outcomes
- bounded retry/backoff execution and strict allowlist gating before resolution
- observer callbacks (`Changing -> Failed* -> Changed`) with correlation propagation and exception isolation
- baseline observability via structured cycle logging, metrics facade, and degraded/healthy evaluation

Release-readiness checks completed in Phase 1 include central package version verification and secret-scan policy/script coverage.

## Phase 2 Scope (Advanced Feeds & Governance)

Phase 2 extends the runtime baseline with feed governance and reproducibility controls:
- deterministic multi-feed resolution with explicit tie-break ordering and bounded retries
- strict/fallback feed policy modes with outage isolation for impacted packages only
- trust policy enforcement for trusted/restricted/untrusted feeds and auditable untrusted overrides
- lock-file generate/enforce/strict modes with package hash validation boundaries
- controlled feed-rule discovery, deterministic dry-run parity, and cleanup retention safety with LKG protection

Implementation notes:
- reconciliation remains idempotent and deterministic for identical inputs
- policy and lock failures are non-mutating and must preserve LKG active pointers
- diagnostics include correlation-linked policy, lock, and cleanup outcomes for operator triage

---

## Naming & Packages

**Repo name:** `nuplane`  
**Primary packages:**
- `Nuplane.Runtime` — orchestration + reconciliation loop + registry + events
- `Nuplane.Store` — deterministic store + staging + atomic activation + cleanup
- `Nuplane.NuGet` — NuGet.Protocol integration (feeds, resolve, download)
- `Nuplane.Hosting` — `IHostBuilder`/DI extensions and options wiring
- `Nuplane.Sources.Directory` — desired-state source from `.nupkg` drop folder

**Optional (Phase 3+):**
- `Nuplane.Loading` — optional assembly loading/unload attempts (ALC-based)

---

## Repo Layout

```

nuplane/
README.md
LICENSE
docs/
    roadmap.md                      # this file
specs/                          # extracted spec-kit feature specs live here
    nuplane-runtime.md
    nuplane-store.md
    nuplane-nuget.md
    nuplane-sources-directory.md
    nuplane-observability.md
    nuplane-loading.md
src/
    Nuplane.Abstractions/           # small shared contracts (models/interfaces)
    Nuplane.Runtime/
    Nuplane.Store/
    Nuplane.NuGet/
    Nuplane.Hosting/
    Nuplane.Sources.Directory/
    Nuplane.Loading/                # optional module (Phase 3+)
samples/
    Nuplane.Sample.Console/
    Nuplane.Sample.AspNetCore/
test/
    Nuplane.Runtime.Tests/
    Nuplane.Store.Tests/
    Nuplane.NuGet.Tests/
    Nuplane.Integration.Tests/
build/
NuGet.config
Directory.Build.props
Directory.Packages.props

````

Notes:
- Keep `Nuplane.Abstractions` minimal: options-free contracts and pure models only.
- No Elsa/CShells references anywhere in `src/`. Host integrations live in their own repos/packages.

### Dependency Management Policy

- The repository uses NuGet Central Package Management via `Directory.Packages.props`.
- Project files MUST reference shared dependencies without inline `Version` attributes unless explicitly justified.
- Shared package versions are managed centrally to keep package graphs consistent across all Nuplane modules.

---

# Phase 1 — Core Runtime (Production-Ready Baseline)

## Outcomes
- Deterministic local package store
- Explicit desired packages
- Directory `.nupkg` desired source
- Single NuGet feed support
- Polling reconciliation
- Transactional per-package updates + LKG fallback
- Change events
- Observability baseline
- Integrity validation hooks (pipeline)

## Specs to extract (Spec Kit)
- `nuplane-runtime.md`
- `nuplane-store.md`
- `nuplane-nuget.md` (phase-1 subset)
- `nuplane-sources-directory.md`
- `nuplane-observability.md`

---

## Phase 1.1 — Core Contracts (Nuplane.Abstractions)

### Core models
- `FeedDefinition { Name, ServiceIndex, TrustLevel, Credentials? }`
- `PackageRequest { Id, VersionRange, FeedName?, UpdatePolicy }`
- `ResolvedPackage { Id, Version, FeedName, InstallPath, InstalledAt }`
- `PackageChangeSet { Added, Updated, Removed, CorrelationId, Timestamp }`

### Desired-state source contract
```csharp
public interface IDesiredPackageSource
{
    Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct);
}
````

### Observer contract

```csharp
public interface INuplaneObserver
{
    Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct);
    Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct);
    Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct);
}
```

---

## Phase 1.2 — Nuplane.Store (Deterministic Store + Transactions)

### Store layout (deterministic)

```
root/
  state.json
  packages/{id}/{version}/...
  current/{id} -> ../packages/{id}/{version}
  staging/
```

### Transaction semantics (per package)

Update:

1. download to staging
2. validate
3. move to immutable `packages/{id}/{version}`
4. atomically switch `current/{id}`
5. update `state.json`

Failure:

* do not switch pointer
* keep last-known-good active
* record diagnostics

### state.json (minimum)

* active version per id
* last-known-good version per id
* last failure per id (stage + message + timestamp)

---

## Phase 1.3 — Nuplane.Runtime (Reconciler + Registry + Loop)

### Reconciliation algorithm

1. Aggregate desired state (explicit + desired sources)
2. Resolve versions (Nuplane.NuGet or directory-derived)
3. Diff desired vs registry:

   * Added / Updated / Removed
4. Apply per-package transactions (via Store)
5. Emit change events
6. Record failures without crashing host

### Polling

* configurable interval (default 60s)
* manual trigger API

### Required guarantees

* no store corruption on partial failures
* retries are bounded + backoff policy-driven
* idempotent apply (safe to re-run same cycle)

---

## Phase 1.4 — Nuplane.Sources.Directory (Folder-Based Desired State)

### DirectoryNupkgDesiredSource

* watches a folder for `.nupkg`
* extracts id + version
* produces desired requests (exact version) or (range policy-driven)

Behavior:

* add `.nupkg` => desired add
* replace with newer version => desired update
* delete `.nupkg` => desired removal

Safety knobs (must-have)

* allowlist/denylist
* max package count
* duplicate-id policy (error vs highest-wins)
* optional “pinned major” rule

---

## Phase 1.5 — Nuplane.NuGet (Single Feed)

### Supported (phase 1)

* one v3 feed
* resolve best version for VersionRange
* download package + dependencies into store staging
* basic caching (local + HTTP if available)

Implementation foundation:

* NuGet.Protocol / NuGet Client SDK

---

## Phase 1.6 — Observability (Baseline)

* structured logs (per cycle + per package transaction)
* correlation id per cycle
* metrics:

  * active packages
  * updates/adds/removes
  * transaction duration
  * failures by stage
* health:

  * healthy if desired packages active and no outstanding failures
  * degraded if failures present

---

## Phase 1 Acceptance Criteria

* Load from single feed using explicit PackageRequests
* Load from directory `.nupkg` desired source
* Detect updates within poll interval and switch atomically
* Handle failure with LKG fallback
* Remove packages (desired no longer includes them) and update registry
* Emit accurate change events
* Persist stable state.json

---

# Phase 2 — Advanced Feeds & Governance

## Strategic Goal

Phase 2 transforms Nuplane from a single-feed runtime engine into a **multi-source, policy-aware package control plane** suitable for production environments with internal + external feeds.

This phase focuses on:

* Deterministic resolution across multiple feeds
* Governance controls to prevent accidental mass ingestion
* Reproducibility (lock mode)
* Safe cleanup of historical versions

---

## 2.1 Multi-Feed Support

### Objectives

* Support multiple NuGet v3 feeds
* Support feed priority and fallback
* Support feed-level trust classification
* Resolve packages deterministically when multiple feeds contain the same ID

---

### Feed Resolution Model

Each `PackageRequest` may:

* Specify a specific feed
* Allow resolution across all feeds (in priority order)

Resolution rules:

1. If `FeedName` specified → only query that feed
2. If not specified:

   * Iterate feeds in priority order
   * First feed returning a matching version wins
3. If multiple feeds contain matching versions:

   * Highest version wins within priority constraints
   * Feed priority breaks ties

FeedDefinition extended:

```csharp
public record FeedDefinition(
    string Name,
    Uri ServiceIndex,
    FeedTrustLevel TrustLevel,
    int Priority = 0,
    FeedCredentials? Credentials = null
);
```

---

### Failure Semantics

* If highest-priority feed is unavailable:

  * Fail if strict mode enabled
  * Fallback if fallback policy allows
* Feed availability must not corrupt state
* Feed failures recorded in reconciliation diagnostics

---

### Acceptance Criteria

* Multiple feeds can be configured
* Resolution is deterministic and reproducible
* Feed outage does not corrupt store
* Feed priority is respected

---

## 2.2 Feed Trust Policies

### Objective

Introduce governance controls per feed.

FeedTrustLevel:

```csharp
public enum FeedTrustLevel
{
    Trusted,
    Restricted,
    Untrusted
}
```

Policies:

* Trusted: no extra validation required
* Restricted: requires validator pipeline success
* Untrusted: disallowed unless explicitly overridden

---

### Validator Pipeline Enforcement

Validators may enforce:

* Signature requirement
* Publisher allowlist
* Hash verification
* Metadata checks

---

### Acceptance Criteria

* Restricted feed packages must pass validators
* Untrusted feeds cannot install without explicit override
* Violations emit failure events

---

## 2.3 Lock File Mode (Deterministic Reproducibility)

### Objective

Support reproducible deployments.

Lock file records:

* Package ID
* Resolved Version
* Feed source
* Hash
* Timestamp

Example:

```json
{
  "packages": [
    {
      "id": "My.Plugin",
      "version": "1.2.3",
      "feed": "Internal",
      "hash": "sha512-..."
    }
  ]
}
```

---

### Modes

* Generate: write lock file from current resolved state
* Enforce: ignore version ranges and use lock file versions
* Strict: fail if lock file missing packages

---

### Acceptance Criteria

* Lock file can reproduce identical store
* Enforce mode ignores feed version changes
* Hash mismatch fails installation

---

## 2.4 Cleanup Policies

### Objective

Prevent unbounded disk growth.

Policies:

* Retain last N versions per package
* Retain versions younger than N days
* Manual-only cleanup mode
* Protect LKG versions

Cleanup runs:

* After successful reconciliation
* As background maintenance job

---

### Acceptance Criteria

* Old versions are safely removed
* LKG is never deleted
* Cleanup failures do not break runtime

---

## 2.5 Feed Rule-Based Desired Source (Controlled Wildcards)

### Objective

Allow rule-based discovery from feeds while preventing runaway ingestion.

Example configuration:

```csharp
options.Desired.FromFeedRules(r =>
{
    r.Feed = "Internal";
    r.IncludeIdPrefix("Company.");
    r.MaxPackages = 50;
    r.PinMajorVersions = true;
});
```

---

### Constraints

* Prefix-only matching (no regex in Phase 2)
* Hard max package limit required
* Must define version policy (latest, pinned major, etc.)
* Must support dry-run mode

---

### Dry-Run Mode

Produces:

* ChangeSet without applying
* Intended for operator validation

---

### Acceptance Criteria

* Rule-based desired state produces deterministic results
* Hard limits enforced
* Dry-run reports accurate diff

---

# Phase 3 — Nuplane.Loading (Optional Assembly Loading)

## Strategic Goal

Provide optional in-process assembly loading for hosts that do not wish to implement their own loader.

This module is entirely optional and isolated.

---

## 3.1 Module Responsibilities

* Load assemblies from active package folder
* Use unloadable AssemblyLoadContext
* Support shared contract assemblies
* Attempt unload when package removed
* Report unload success/failure

---

## 3.2 Loading Model

Each active package:

* Gets its own AssemblyLoadContext
* Uses AssemblyDependencyResolver for dependency resolution
* May share specific host assemblies

Shared assembly policy:

```csharp
options.Loading.SharedAssemblies.Add("Nuplane.Abstractions");
```

---

## 3.3 Unload Semantics

When package removed:

1. Deactivate (host-driven)
2. Dispose load context
3. Force GC attempt
4. Report unload result

Unload is:

* Best-effort
* Not guaranteed
* Explicitly reported

---

## 3.4 Failure Handling

If unload fails:

* Package marked `UnloadPending`
* Host may choose to restart
* Failure logged

### Phase 3 Operator Notes

Operational guidance for optional loading:

* Keep loading opt-in and default-disabled for hosts that own loading behavior.
* Configure a bounded deactivation timeout before unload attempts.
* Treat `UnloadPending` as actionable degraded state and retry unload each reconciliation cycle.
* Configure shared contract assemblies by strong identity (`name + public key token + major version`).
* Capture per-cycle evidence with correlation IDs for load/unload outcomes and timeout events.

### Phase 3 Validation Profile

Recommended baseline for acceptance validation:

* Profile: `phase3-loading-baseline`
* Dataset: 20 active packages with valid dependencies, including overlapping dependency names and shared-contract references
* Window: 10 identical reconciliation cycles for idempotence checks
* Failure injection: load failures, unload failures, and deactivation timeout events
* Required evidence: observer callbacks + correlation-linked logs/metrics/health snapshots

---

## Acceptance Criteria

* Assemblies load correctly from package store
* Shared assemblies respected
* Unload attempt executed
* Outcome observable

---

# Phase 4 — Cluster-Convergent Runtime Loading (Lean)

## Strategic Goal

Make Nuplane practical for real applications that want to load packages at startup and at runtime across multiple identical replicas, converging on a shared desired state over time, without introducing distributed coordination inside Nuplane itself.

---

## 4.1 Shared Desired Manifest (Exact Versions)

Support a deterministic desired-state manifest as an optional desired-state input.

Purpose:
- all replicas that read the same manifest eventually converge to the same active package set
- exact version pinning is the initial default for determinism and operability

The manifest can be hosted in a shared location (directory, blob/object storage, or HTTPS) and should be updated atomically (upload packages first, write/update manifest last).

---

## 4.2 Automatic + Explicit Reconciliation

Allow:
- automatic reconciliation at startup and via polling
- explicit reconciliation triggers (in-process API and optional REST)

Polling remains the robustness baseline; explicit triggers enable near real-time updates after package uploads.

---

## 4.3 Optional Loader SDK Integration

Support an optional Loader SDK (separate module) that can load assemblies/types/services from activated packages.

Nuplane remains host-neutral:
- hosts may opt into loader integration
- loader failures are observable and must not crash the host

---

## 4.4 Integrity (Pragmatic Baseline)

Support pragmatic baseline integrity for runtime loading scenarios:
- deterministic acquisition and activation boundaries
- observable non-mutating failures with last-known-good preservation

Advanced signing/trust policies can be introduced later if needed.

---

## 4.5 Admin API (Optional Package)

Separate package:

* `Nuplane.Admin.AspNetCore`

Endpoints:

* GET /nuplane/packages
* GET /nuplane/state
* POST /nuplane/reconcile
* GET /nuplane/health

Authentication left to host.

---

## Acceptance Criteria

* Replicas converge to the same active package set given the same desired manifest
* Reconciliation is safe, deterministic, and observable (logs/metrics/health/events)
* Admin endpoints expose accurate state and can trigger reconcile
* Loader integration is optional and safe

---

# Phase 5 — Progressive Delivery & Rollouts (Deferred)

## Strategic Goal

Add production-grade rollout controls for environments and fleets that require progressive delivery, blast-radius control, and deliberate promotion workflows.

## 5.1 Channels (Environment Segmentation)

Support multiple package channels, such as:
- prod
- staging
- canary

Feeds, rules, and desired inputs may be channel-scoped.

## 5.2 Staged Promotion

Allow staged promotion workflow:
1. acquire + validate new version
2. stage without activation
3. wait for explicit promotion
4. activate atomically with LKG preservation

## 5.3 Canary Rollout

Allow fleet canary behavior:
- activation only on a subset of nodes
- gradual rollout percentage
- deterministic selection for identical inputs

Cluster integration remains host-driven (Nuplane does not become a cluster orchestrator).

## 5.4 Advanced Integrity & Governance

Support stronger integrity/governance requirements where needed:
- signature verification
- strict trust enforcement
- mandatory hash verification
- feed-level required validator configuration

---

# Phase 2–4 Completion Definition

Nuplane becomes:

* Multi-feed aware
* Governance-capable
* Reproducible
* Operationally mature
* Optionally self-loading
* Safe in production environments