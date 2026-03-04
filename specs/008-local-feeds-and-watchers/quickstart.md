# Quickstart — Local Directory Feeds + Watchers

## Goal

Validate that local directory feeds are treated as first-class artifact sources, that directory changes trigger reconciliation quickly (watchers), and that scheduled reconciliation still converges when watchers are degraded or unavailable. Validate that “no remote feeds configured” no longer causes unhandled exceptions for directory-originating packages.

## Preconditions

- Repo root: `nuplane/main`
- Branch: `008-local-feeds-and-watchers`
- .NET SDK capable of building repo targets (`net8.0`, `net9.0`, `net10.0`)
- A `.nupkg` available to drop (e.g., build `samples/Nuplane.Sample.Plugin`)

## Validation Profile

- Profile: `local-directory-feeds-watchers`
- Inputs:
  - one local directory feed directory
  - zero remote feeds (baseline regression scenario)
- Triggers:
  - directory change watcher trigger (primary)
  - scheduled poll trigger (fallback)
- Evidence:
  - explicit trigger attribution (scheduled vs directory change)
  - no unhandled exceptions
  - degraded watcher health signal when watcher cannot be established

## Command Set

Run from repository root:

```bash
# Run unit tests (expected to include new coverage for directory feed resolution + watcher coalescing)

dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj

# Run integration tests (expected to include a local-directory-only regression scenario)

dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj

# Full solution validation

dotnet test Nuplane.sln
./build/validate-secrets.sh
```

## Scenario 1: Local-directory-only operation (regression for “no remote feeds”)

1. Build a sample package:
   - `dotnet build samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj`
2. Start the sample host (or any host configured with a local directory feed) without configuring any remote feeds.
   - Use `samples/Nuplane.Sample.AspNetCore` as the reference host.
3. Drop a `.nupkg` into the configured directory feed path.
4. Verify:
   - a reconciliation cycle runs,
   - feed resolution selects the local directory feed,
   - no `InvalidOperationException: No available feed could resolve package ...` is thrown,
   - the outcome is observable (logs/health/metrics) with correlation id.

## Scenario 2: Watcher-triggered reconciliation (near real time)

1. Configure the local directory feed with watcher enabled and a bounded debounce window.
2. Start the host.
3. Create or copy a `.nupkg` into the directory.
4. Verify:
   - reconciliation is triggered quickly (target: within 2 seconds for most events),
   - repeated change notifications do not create reconcile storms,
   - trigger attribution indicates `DirectoryChange`.

## Scenario 3: Partial-write handling

1. Start the host with watcher enabled.
2. Copy a `.nupkg` into the directory slowly (or simulate by writing to a temp file then moving).
3. Verify:
   - the system does not process a partially written file as a valid artifact,
   - bounded retries occur (with clear diagnostics if the file never stabilizes),
   - once stable, the package becomes eligible and a reconciliation cycle can succeed.

## Scenario 4: Watcher degraded fallback (scheduled convergence still works)

1. Configure a directory path that cannot be watched (permissions/invalid path) OR temporarily disable watcher establishment.
2. Start the host.
3. Verify:
   - directory observation health is `Degraded` with last error recorded,
   - scheduled reconciliation continues to run on the configured poll interval,
   - convergence still occurs within one scheduled interval when the directory becomes available again.

## Scenario 5: No feeds configured (explicit idle mode)

1. Start a host with no configured feeds (no remote feed definitions and no local directory feed definitions).
2. Verify:
   - the runtime enters explicit idle mode,
   - health/diagnostics indicate “no reconciliation inputs configured”,
   - no startup validation fails solely due to missing feeds.

## Expected Evidence

- Correlation-linked structured logs for:
  - trigger type (`Scheduled` vs `DirectoryChange`)
  - feed resolution decisions (selected feed + candidates)
  - watcher enabled/degraded state changes
- Metrics baseline:
  - trigger counts by type
  - cycle duration
  - failures by stage/reason
- Health:
  - explicit degraded watcher signal when applicable
  - explicit idle-mode signal when no feeds are configured

## Success Criteria Mapping

- `SC-001`: watcher-driven triggers within the 2s target window.
- `SC-002`: convergence via scheduled polling when watchers are unreliable/unavailable.
- `SC-003`: zero unhandled exceptions in local-directory-only operation.
- `SC-004`: explicit idle-mode signal when no feeds are configured.
