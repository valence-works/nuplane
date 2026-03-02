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

## Outcomes

* multiple feeds + priority/fallback
* feed trust policies
* lock-file mode
* cleanup policies
* optional rule-based feed discovery (controlled “wildcards”)

## Specs to extract

* `nuplane-multi-feed.md`
* `nuplane-lockfile.md`
* `nuplane-cleanup.md`
* `nuplane-feed-rules.md`

### Feed rule-based desired source (opt-in)

* prefix includes (no regex by default)
* max package count
* version pinning (e.g., pin major)
* dry-run mode (produce diff without apply)

---

# Phase 3 — Nuplane.Loading (Optional Assembly Loading)

## Outcomes

* optional ALC loader module
* unload attempt + reporting (best-effort)
* shared contract assemblies

## Spec to extract

* `nuplane-loading.md`

Important: unload is not guaranteed; runtime must report unload outcome.

---

# Phase 4 — Operational Enhancements

* channels (prod/staging/canary)
* staged rollouts
* advanced integrity (signatures, strict trust requirements)
* richer admin endpoints (optional package)

---

## Cross-Cutting Guarantees

1. Deterministic store layout
2. Atomic activation per package
3. Per-package transactional updates
4. Last-known-good fallback
5. Host-neutral change events (no plugin model)
6. Polling-based observation (no push assumptions)
7. Clear diagnostics on failure