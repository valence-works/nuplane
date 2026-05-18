# Nuplane Operational Stability Roadmap

**Created**: 2026-05-18  
**Status**: Roadmap artifact for Speckit decomposition  
**Source Incident**: Elsa Pro Docker startup failure while resolving runtime packages from Feedz during `docker compose up`.

## Purpose

This document captures the operational, stability, performance, and efficiency improvements needed to make Nuplane suitable for production Docker and server workloads where package resolution must be deterministic, diagnosable, and resilient to feed outages.

It is intentionally broader than a single Speckit feature. Use it to create a sequence of focused Speckit specs, each with its own `spec.md`, `plan.md`, `tasks.md`, tests, and validation.

## Incident Summary

An Elsa Pro Docker deployment configured Nuplane to load PostgreSQL, Quartz PostgreSQL, and RabbitMQ extension packages at startup. The application container failed during startup reconciliation because it could not open HTTPS connections to the Elsa Feedz feed:

- Feed: `https://f.feedz.io/elsa-workflows/elsa-3/nuget/index.json`
- Root packages:
  - `Elsa.Persistence.EFCore.PostgreSql`
  - `Elsa.Scheduling.Quartz.EFCore.PostgreSql`
  - `Elsa.ServiceBus.MassTransit.RabbitMq`
- Primary failure: remote NuGet service index unavailable from the container.
- Final host symptom: `TaskCanceledException` from startup hosted service after reconciliation retries.

The application had a local packages directory mounted, but it was empty. When populated with every restored dependency package, Nuplane treated all cached `.nupkg` files as desired roots, causing a large startup request count. The immediate workaround was to pre-populate only configured root `.nupkg` files and let Nuplane resolve those three roots from the local directory feed.

## Current Code Map

### Feed Resolution

- `src/Nuplane/Feeds/MultiFeedPackageResolver.cs`
  - `ResolveAsync`: orders feed candidates and tries each feed.
  - `ResolveVersionAsync`: local directory feeds require explicit versions; remote feeds enumerate versions.
  - `ResolveInstallPathAsync`: local feeds extract from `.nupkg`; remote feeds delegate acquisition.
- `src/Nuplane/Feeds/Policy/FeedResolutionPolicy.cs`
  - Orders candidates by explicit feed, priority, and name.
- `src/Nuplane/Feeds/Versioning/NuGetFeedVersionEnumerator.cs`
  - Uses NuGet.Protocol to enumerate all versions.
- `src/Nuplane/Feeds/Versioning/CachedFeedVersionEnumerator.cs`
  - In-memory TTL cache and single-flight for version lists.
- `src/Nuplane/Feeds/NuGetRemotePackageAcquirer.cs`
  - Downloads packages.
  - Fetches service index again to find `PackageBaseAddress`.

### Desired State and Directory Feeds

- `src/Nuplane.Sources.Directory/DirectoryNupkgDesiredSource.cs`
  - Scans `.nupkg` files and emits `PackageRequest` roots.
  - Keeps highest version per package in a directory source.
- `src/Nuplane.Sources.Directory/Registration/DirectorySourceRegistrationServices.cs`
  - Registers a directory feed as both a feed and desired source.
- `src/Nuplane/Sources/FeedRuleDesiredSource.cs`
  - Converts feed include patterns into desired root requests.
- `src/Nuplane/Sources/DesiredStateAggregator.cs`
  - Deduplicates package IDs by source name, then version range text.

### Reconciliation and Startup

- `src/Nuplane/Hosting/NuplaneStartupHostedService.cs`
  - Blocks host startup by enqueuing a startup trigger and waiting.
- `src/Nuplane/Hosting/ReconciliationTriggerDispatcherHostedService.cs`
  - Dispatches queued triggers and completes awaiting callers.
- `src/Nuplane/Reconciliation/ReconciliationService.cs`
  - Builds and runs the reconciliation pipeline.
- `src/Nuplane/Reconciliation/Middleware/DesiredStateReadMiddleware.cs`
  - Reads desired sources with fallback to last successful source snapshots.
- `src/Nuplane/Reconciliation/Middleware/PackageResolutionMiddleware.cs`
  - Invokes package resolution.
- `src/Nuplane/Reconciliation/PackageApplyExecutor.cs`
  - Resolves roots, graph dependencies, and executes package transactions.
- `src/Nuplane/Reconciliation/PackageDependencyGraphResolver.cs`
  - Expands dependencies from installed package metadata.

### Store, LKG, Locking, and Loading

- `src/Nuplane/Store/State/StoreRegistry.cs`
  - Persists active versions, LKG, failures, source snapshots, and active graphs.
- `src/Nuplane/Store/State/StoreStateRecord.cs`
  - Persisted state shape.
- `src/Nuplane/Reconciliation/LockFileCoordinator.cs`
  - Evaluates resolved packages against lock file entries.
- `src/Nuplane/Reconciliation/LockFile/LockFileOptions.cs`
  - Lock modes: Generate, Enforce, Strict.
- `src/Nuplane.Loading/PackageLoader.cs`
  - Loads resolved graph assemblies.
- `src/Nuplane.Loading/HostIntegratedAssemblyResolver.cs`
  - Makes host-integrated assemblies visible to default framework resolution.

## Product Goals

1. Exact-version startup should not need remote version enumeration.
2. Local/package-directory feeds should serve as deterministic package caches without automatically making every package a desired root.
3. Production hosts should be able to run in offline or locked modes.
4. Feed outages should degrade or fail according to explicit policy, not by accident.
5. Startup failures should surface domain diagnostics, not generic host cancellation.
6. Remote feed operations should avoid redundant service index fetches and unnecessary network calls.
7. LKG state should be usable for startup recovery when active package artifacts are still valid.
8. Resolution and acquisition should remain deterministic, transactional, observable, and testable without real network calls.

## Non-Goals

- Do not replace Nuplane's feed trust, package activation, graph persistence, or runtime loading ownership with full `dotnet restore`.
- Do not add distributed locking or cluster leader election.
- Do not make host startup silently ignore required persistence packages by default.
- Do not require hosts to generate MSBuild project files for runtime package resolution.
- Do not remove current ranged-version support; make it explicit that ranges need remote/catalog metadata unless satisfied by a policy-approved cache.

## Roadmap Tracks

### Track 1 - Exact-Version Fast Path

**Problem**: Even exact root package requests currently flow through remote version enumeration for remote feeds. Exact versions should be resolvable without listing every available version.

**Desired behavior**:

- Classify version requests as:
  - exact bracketed version, e.g. `[1.2.3]`
  - bare exact version, e.g. `1.2.3`
  - range/floating version, e.g. `[1.2.0,)`
  - empty/latest policy
- For exact roots, probe local directory feeds first when policy allows.
- If exact local `.nupkg` exists, resolve and extract locally without remote enumeration.
- If exact local `.nupkg` is missing, continue to remote acquisition according to policy.
- Remote exact acquisition should not enumerate all versions; it can attempt to acquire the exact version directly and classify 404 as package-not-found.

**Likely implementation points**:

- Add an internal version classifier near `Nuplane.Versioning`.
- Update `MultiFeedPackageResolver.ResolveVersionAsync`.
- Add an exact acquisition path to `IRemotePackageAcquirer` or pass exact selected versions to current acquisition without enumerator involvement.
- Update `FeedResolutionDecision` with `VersionSelectionKind`.

**Acceptance criteria**:

- Exact local package resolves without calling `IFeedVersionEnumerator`.
- Exact remote package resolves by direct acquisition without calling `IFeedVersionEnumerator`.
- Exact missing local package falls back to remote when fallback policy allows.
- Range requests preserve current range behavior.

**Tests**:

- `MultiFeedPackageResolverTests.ResolveAsync_ExactVersion_LocalFeedHit_DoesNotEnumerateRemote`
- `MultiFeedPackageResolverTests.ResolveAsync_ExactVersion_RemoteDirectAcquire_DoesNotEnumerateVersions`
- `MultiFeedPackageResolverTests.ResolveAsync_Range_RemoteStillEnumeratesVersions`

### Track 2 - Directory Feed Roles: Cache vs Desired Roots

**Problem**: A directory feed currently doubles as a desired-state source. This makes a package cache operationally dangerous because every cached dependency can become a root request.

**Desired behavior**:

Directory-backed feeds should have an explicit role:

- `Cache`: packages are available to satisfy resolution but do not produce desired root requests.
- `Desired`: packages produce desired root requests.
- `DesiredAndCache`: current combined behavior.

**Likely implementation points**:

- Add `Role` to `NuplaneDirectoryFeedSetupOptions` and builder options.
- In `DirectorySourceRegistrationServices.RegisterFeed`, always register the feed definition but only register `IDesiredPackageSource` for desired roles.
- Keep directory watcher configuration independent from desired root emission.

**Acceptance criteria**:

- Cache-only directory feed contributes zero desired requests.
- Cache-only directory feed can resolve an exact package.
- Desired directory feed preserves current highest-version-per-package behavior.
- Existing config without `Role` remains backward-compatible.

**Tests**:

- `DirectoryBuilderIntegrationTests.CacheRole_RegistersFeedButNoDesiredSource`
- `LocalDirectoryFeedContractTests.CacheOnlyFeed_ResolvesExactPackage`
- `DirectoryNupkgDesiredSourceTests.DesiredRole_EmitsHighestVersionOnly`

### Track 3 - Local Preference, Offline Mode, and Remote Fallback Policy

**Problem**: Feed ordering is priority/name based. Production operators need policy that expresses operational intent: prefer local artifacts, run offline, or allow remote fallback.

**Proposed options**:

```json
{
  "Nuplane": {
    "Setup": {
      "FeedResolution": {
        "PreferLocalFeedsForExactVersions": true,
        "OfflineMode": false,
        "RemoteFallbackMode": "WhenLocalMisses"
      }
    }
  }
}
```

**Option semantics**:

- `PreferLocalFeedsForExactVersions`: exact packages present locally are selected before remote candidates.
- `OfflineMode`: remote feeds are not contacted during resolution or acquisition.
- `RemoteFallbackMode`:
  - `Never`
  - `WhenLocalMisses`
  - `Always`

**Likely implementation points**:

- Extend `FeedResolutionOptions`.
- Update `FeedResolutionPolicy.OrderCandidates` or add a resolver-level candidate partition.
- Add explicit decision paths:
  - `exact-local-cache-hit`
  - `offline-local-miss`
  - `remote-fallback-after-local-miss`
  - `remote-disabled-by-policy`

**Acceptance criteria**:

- Local exact package wins over remote feed when present and preference is enabled.
- Offline mode fails fast on local miss with no remote calls.
- Remote fallback behavior is deterministic and visible in decisions.

**Tests**:

- `MultiFeedResolutionPolicyTests.LocalExactPreference_BeatsRemotePriority`
- `MultiFeedPackageResolverTests.OfflineMode_LocalMiss_DoesNotCallRemote`
- `MultiFeedPackageResolverTests.RemoteFallbackModeNever_LocalMiss_FailsWithPolicyDecision`

### Track 4 - Startup Failure Diagnostics and Policy

**Problem**: Startup reconciliation failures can surface as generic `TaskCanceledException`, hiding the package/feed failure operators need.

**Desired behavior**:

- Startup failures throw a Nuplane domain exception:
  - `NuplaneStartupReconciliationException`
- Exception includes:
  - correlation ID
  - trigger type
  - failed package IDs
  - feed decisions
  - source outage count
  - original reconciliation exception when available
- Startup policy controls host behavior:
  - `FailHost`
  - `StartDegraded`
  - `UseLastKnownGood`

**Likely implementation points**:

- Extend `ReconciliationRunResult` or introduce a richer internal startup outcome.
- Update `NuplaneStartupHostedService.StartAsync`.
- Update `ReconciliationTriggerDispatcherHostedService` to preserve completed failure context instead of reducing to cancellation when possible.
- Add source-generated logs for startup failed/degraded/LKG outcomes.

**Acceptance criteria**:

- Feed outage during startup reports package ID, feed name, URL, and decision path.
- Operator sees the original resolver/acquisition failure, not only `TaskCanceledException`.
- `FailHost` remains the default for startup reconciliation.
- `StartDegraded` does not activate new failed packages.

**Tests**:

- `StartupCycleTests.StartAsync_WhenStartupResolutionFails_ThrowsNuplaneStartupReconciliationException`
- `StartupCycleTests.StartAsync_WhenWaitCancelledBeforeDispatch_ThrowsOperationCanceledException`
- `StartupCycleTests.StartAsync_WhenPolicyStartDegraded_ReturnsWithoutHostFailure`

### Track 5 - Service Index and Acquisition Efficiency

**Problem**: `NuGetRemotePackageAcquirer` fetches the service index to discover `PackageBaseAddress` every time acquisition needs a package. This duplicates work and increases outage exposure.

**Desired behavior**:

- Cache `PackageBaseAddress` per feed with TTL.
- Single-flight concurrent service index discovery.
- Configure service index and package download timeouts.
- Reuse NuGet HTTP cache unless explicitly disabled.
- Consider delegating package download to NuGet.Protocol APIs to align with version enumeration.

**Proposed options**:

```json
{
  "Nuplane": {
    "Setup": {
      "FeedResolution": {
        "ServiceIndexCacheTtl": "00:30:00",
        "ServiceIndexTimeout": "00:00:10",
        "PackageDownloadTimeout": "00:02:00",
        "DisableNuGetHttpCache": false
      }
    }
  }
}
```

**Likely implementation points**:

- Add `IServiceIndexResourceCache` or internal `PackageBaseAddressCache`.
- Inject `HttpClient` using `IHttpClientFactory` if the package can take the dependency cleanly.
- Replace static `HttpClient` if lifecycle/configuration requires it.
- Add structured acquisition metrics:
  - service index cache hit/miss
  - package download duration
  - package bytes downloaded
  - acquisition source: remote/local/cache

**Acceptance criteria**:

- Multiple acquisitions from one feed load service index once within TTL.
- Concurrent acquisitions single-flight service index discovery.
- Timeout failures include feed, package ID, version, and operation stage.

**Tests**:

- `NuGetRemotePackageAcquirerTests.AcquireAsync_CachesPackageBaseAddress`
- `NuGetRemotePackageAcquirerTests.AcquireAsync_ConcurrentCalls_SingleFlightServiceIndex`
- `NuGetRemotePackageAcquirerTests.AcquireAsync_Timeout_ReportsOperationContext`

### Track 6 - Last-Known-Good Startup Recovery

**Problem**: Nuplane persists active graphs and LKG versions but does not use them aggressively enough to avoid cold-start remote feed dependency when artifacts are already present and valid.

**Desired behavior**:

- On startup, if desired inputs match active graph roots and local installed artifacts are valid, Nuplane can use the active/LKG graph without remote resolution.
- Policy determines whether LKG startup is allowed.
- LKG startup emits an explicit degraded or recovered state.
- Background reconciliation may refresh after host starts if policy allows.

**Proposed options**:

```json
{
  "Nuplane": {
    "Reconciliation": {
      "StartupFailurePolicy": "FailHost",
      "UseLastKnownGoodOnFeedOutage": false,
      "AllowBackgroundRefreshAfterLkgStartup": true
    }
  }
}
```

**Likely implementation points**:

- Add a pre-resolution startup middleware or service that reads `StoreStateRecord.ActiveGraphsByIdNormalized`.
- Validate install paths and `.nuplane-ready` markers.
- Ensure graph roots still satisfy desired root IDs and exact versions.
- Feed outages can fall back to LKG only when policy allows.
- Loading should use persisted graph descriptors without requiring remote resolution.

**Acceptance criteria**:

- Valid LKG graph starts host without remote feed calls when policy allows.
- Missing artifact invalidates LKG shortcut and falls back/fails according to policy.
- Changed desired root package invalidates LKG shortcut.
- Health state records degraded startup when LKG was used because feeds were unavailable.

**Tests**:

- `ActivePackageCatalogRestartIntegrationTests.Startup_WithValidLkgAndFeedOutage_UsesLkg`
- `ActivePackageCatalogRestartIntegrationTests.Startup_WithMissingLkgArtifact_FailsOrFallsBackByPolicy`
- `StartupLoadingEventIntegrationTests.LkgStartup_LoadsPersistedHostIntegratedGraph`

### Track 7 - Lock File as Offline Execution Contract

**Problem**: Lock files can enforce versions after resolution, but they do not currently prevent unnecessary remote discovery or define an offline execution contract.

**Desired behavior**:

- Strict lock mode should be able to drive exact resolution without remote version enumeration.
- Optional lock entries include enough metadata to locate package artifacts in local caches.
- Offline strict mode fails with a missing-artifact list, not remote feed errors.

**Potential lock file additions**:

- `sourceKind`: `local-directory`, `remote-feed`, `package-cache`
- `packageFileName`
- `contentHash`
- `dependencyGraphId`
- `runtimeAssets`

**Acceptance criteria**:

- Strict lock + cache-only directory resolves all locked packages without remote calls.
- Strict lock + missing local package reports every missing artifact.
- Existing lock files remain readable.

**Tests**:

- `LockFileStrictModeTests.StrictOffline_UsesLockedExactVersionsWithoutRemoteEnumeration`
- `LockFileStrictModeTests.StrictOffline_MissingArtifactsReportsAllMissingPackages`

### Track 8 - Operational Observability Surface

**Problem**: Existing feed decisions are useful but too low visibility for operators during startup failures.

**Desired behavior**:

- Feed decision logs for failures are elevated enough for production diagnosis.
- Admin operational state exposes:
  - last startup outcome
  - LKG usage
  - offline mode
  - feed outage classifications
  - per-feed cache stats
  - package acquisition stats
- Failure records preserve structured reason codes, not just exception messages.

**Likely implementation points**:

- Extend `FeedResolutionDecision`.
- Extend `OperationalStateSnapshot`.
- Add reason-code taxonomy:
  - `feed-service-index-unavailable`
  - `package-exact-local-miss`
  - `remote-disabled-by-policy`
  - `package-not-found`
  - `package-download-timeout`
  - `startup-lkg-used`

**Acceptance criteria**:

- A startup feed outage can be diagnosed from logs and admin state without stack trace parsing.
- Feed decisions distinguish version enumeration, service index discovery, and package acquisition failures.
- Metrics can answer whether startup used local cache, remote feed, or LKG.

**Tests**:

- `OperationalStateSnapshotTests.StartupFailure_IncludesFeedDecisionSummary`
- `AdminOperationalStateCompositionTests.FeedCacheStats_AreComposed`
- `ReconciliationLoggerTests.FeedOutage_UsesStructuredReasonCode`

## Speckit Decomposition Recommendation

Create these feature specs in order:

1. `028-exact-version-local-fast-path`
   - Tracks: 1 and a small slice of 3.
   - Outcome: exact local packages do not touch remote feeds.

2. `029-directory-feed-cache-role`
   - Track: 2.
   - Outcome: package cache no longer explodes desired root count.

3. `030-offline-and-remote-fallback-policy`
   - Track: 3.
   - Outcome: operators can explicitly control local/remote behavior.

4. `031-startup-failure-diagnostics`
   - Track: 4.
   - Outcome: domain exception and useful startup failure details.

5. `032-remote-feed-efficiency`
   - Track: 5.
   - Outcome: cached service index/base-address discovery and acquisition timeouts.

6. `033-lkg-startup-recovery`
   - Track: 6.
   - Outcome: valid active graphs can recover startup under allowed outage policies.

7. `034-lock-file-offline-contract`
   - Track: 7.
   - Outcome: strict lock can become an offline execution contract.

8. `035-operational-observability-expansion`
   - Track: 8.
   - Outcome: admin/log/metric surfaces make resolution behavior inspectable.

## Cross-Cutting Requirements

### Determinism

- Identical desired inputs, feed metadata, package cache contents, and lock files must produce identical selected package identities.
- Local-vs-remote preference must be explicit and deterministic.
- LKG shortcut must validate desired root compatibility before reuse.

### Transaction Safety

- No package should become active until resolution, trust/lock gates, acquisition/extraction, and transaction staging succeed.
- Any failed resolution or acquisition must preserve existing active/LKG state.
- LKG startup must not mutate active package state unless an explicit refresh succeeds.

### Security and Supply Chain

- Offline/cache behavior must not bypass feed trust policy.
- Lock file strict mode must enforce hashes when available.
- Do not log credentials or package source secrets.
- Local package cache paths may be logged, but avoid leaking secret-bearing URLs.

### Performance

- Exact local resolution should be O(number of configured local feeds), not O(remote version count).
- Service index discovery should be cached and single-flight.
- Directory scans should avoid expanding dependency caches into desired roots.
- Startup should avoid repeated remote calls across root packages that share a feed.

### Operability

- Failure messages must name package ID, version/range, feed name, feed URL where safe, operation stage, and reason code.
- Startup policy must be explicit in logs.
- Health/degraded state should identify source outage, feed outage, lock failure, acquisition failure, loading failure, and LKG usage separately.

## Suggested Data Model Additions

### Version Request Classification

```csharp
internal enum VersionRequestKind
{
    EmptyOrLatest,
    Exact,
    Range
}

internal sealed record VersionRequestClassification(
    VersionRequestKind Kind,
    string OriginalText,
    string? ExactVersion);
```

### Directory Feed Role

```csharp
public enum DirectoryFeedRole
{
    DesiredAndCache,
    Desired,
    Cache
}
```

### Startup Failure Policy

```csharp
public enum StartupFailurePolicy
{
    FailHost,
    StartDegraded,
    UseLastKnownGood
}
```

### Remote Fallback Mode

```csharp
public enum RemoteFallbackMode
{
    Never,
    WhenLocalMisses,
    Always
}
```

### Startup Reconciliation Exception

```csharp
public sealed class NuplaneStartupReconciliationException : Exception
{
    public string CorrelationId { get; }
    public IReadOnlyList<string> FailedPackageIds { get; }
    public IReadOnlyList<FeedResolutionDecision> FeedDecisions { get; }
}
```

## Validation Strategy

### Unit Tests

- Feed resolution exact/range classification.
- Local preference and offline policies.
- Directory role registration.
- Service index cache behavior.
- Startup exception construction.

### Integration Tests

- Docker-like startup with local package cache and unreachable remote feed.
- Cache-only directory containing many dependencies should not increase desired root count.
- Startup with valid LKG and remote outage.
- Strict lock + offline cache.

### No Real Network Tests

- Use existing `TestNuGetFeedServer` for HTTP feed behavior.
- Use NSubstitute for `IFeedVersionEnumerator` and `IRemotePackageAcquirer`.
- Use temp directories and generated `.nupkg` files for local feed tests.

### Manual Validation Scenario

1. Configure a cache-only local feed mounted at `/app/packages`.
2. Configure exact root packages.
3. Disable network access to remote feed.
4. Start host.
5. Verify startup resolves from local cache or LKG according to policy.
6. Verify logs include selected policy and no remote service-index failure when offline/local-only mode is active.

## Migration and Compatibility

- Existing directory feed config without `Role` should preserve current behavior unless a major version change deliberately changes the default.
- Existing lock files must remain readable.
- Existing ranged include patterns must continue to work.
- Existing `UnavailableFeeds`, feed priorities, and `StopOnFirstSuccessfulFeed` semantics must be preserved or explicitly superseded by documented new policy.
- Default startup policy should remain conservative: fail host when required startup reconciliation fails.

## Open Questions for Speckit

1. Should `PreferLocalFeedsForExactVersions` default to true?
2. Should directory feed `Role` default to current `DesiredAndCache`, or should a future major version default to `Cache`?
3. Should exact remote acquisition use direct flat-container URL discovery, NuGet.Protocol APIs, or both behind an abstraction?
4. Should LKG startup require lock-file strict mode, or can active graph validation be sufficient?
5. Should startup `StartDegraded` be allowed when packages are marked required by host integration?
6. How should feed trust policy represent cache-only local packages originally downloaded from remote feeds?
7. Should admin operational state expose raw feed URLs, redacted URLs, or only feed names?

## Definition of Done for the Roadmap

This roadmap is fully implemented when:

- Exact configured packages can start from local cache without remote enumeration.
- Package cache directories no longer create accidental root package explosions.
- Operators can enforce offline/locked startup behavior.
- Feed outages produce structured, package-specific diagnostics.
- Remote acquisition avoids redundant service-index work.
- Valid LKG active graphs can recover startup when policy allows.
- Admin/log/metric surfaces explain whether packages came from local cache, remote feed, lock file, or LKG.
