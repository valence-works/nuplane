# Implementation Plan: Local Directory Feeds + Watchers (No Separate "Drop Folder")

**Branch**: `008-local-feeds-and-watchers` | **Date**: 2026-03-04 | **Spec**: `/specs/008-local-feeds-and-watchers/spec.md`
**Input**: Feature specification from `/specs/008-local-feeds-and-watchers/spec.md`

## Summary

Refactor “drop folder” semantics into first-class local directory feeds that participate in feed resolution and acquisition, while preserving current reconciliation/store semantics. Ensure local directory feeds can (1) contribute desired package requests, (2) supply artifacts without requiring any remote feeds, (3) trigger near real-time reconciliation via file watchers, and (4) still converge via scheduled reconciliation polling. Eliminate the current “no remote feeds configured + directory desired source” exception path by making directory-originating requests resolvable from the local feed.

## Technical Context

**Language/Version**: C# on .NET multi-targeting (`net8.0;net9.0;net10.0`)  
**Primary Dependencies**: `Microsoft.Extensions.*` (DI/Options/Hosting/Logging), `System.IO.FileSystemWatcher`, `System.Threading.Channels`, xUnit, NSubstitute  
**Storage**: Node-local filesystem store with transactional activation semantics (stage/validate/publish/atomic switch + LKG fallback)  
**Testing**: `dotnet test` (xUnit) with unit tests under `test/*Tests` and integration tests in `test/Nuplane.Integration.Tests`  
**Target Platform**: Cross-platform .NET hosts (Linux/macOS/Windows)  
**Project Type**: Multi-project .NET library/runtime + optional hosting/sample apps  
**Performance Goals**: Directory-change triggered reconcile start within 2s for most events (`SC-001`); convergence within one scheduled interval in degraded watcher environments (`SC-002`)  
**Constraints**: Deterministic/idempotent reconciliation, bounded retries/backoff, no store corruption, explicit trust boundaries for local sources, and no unbounded watcher-triggered reconcile storms  
**Scale/Scope**: One host instance per node, multiple feeds (remote + local), with local watcher inputs augmenting scheduled reconciliation

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Design Gate Assessment

- Deterministic reconciliation: PASS — watcher signals are coalesced (bounded channel + debounce window) and scheduled reconciliation remains periodic; identical observed inputs must yield identical outcomes.
- Transactional store safety: PASS — design explicitly preserves stage/validate/publish/atomic-switch semantics and LKG fallback; observer triggers only schedule reconciliation and never mutate store directly.
- Source & supply chain integrity: PASS — only explicitly configured local directory feeds may contribute desired state; local artifacts remain subject to source trust allowlists and validation before activation.
- Observability & operability: PASS — reconciliation already uses correlation-linked logging/metrics/health; this feature adds explicit trigger attribution (scheduled vs directory change) and watcher degraded-state signaling.
- Test & contract discipline: PASS — plan requires unit tests for watcher coalescing + partial-write handling, integration coverage for local-directory-only operation, and a regression test for the current “no remote feeds configured + package dropped” failure.
- Decomposition discipline: PASS — mechanism changes (local feed definition + resolver eligibility) are separated from drivers (watcher hosted service + periodic polling), and each new option has both a consumer and a validator.
- Options validation discipline: PASS — new/updated options remain data-only and are validated via `IValidateOptions<T>` with `ValidateOnStart()` when required; “no feeds configured” is explicitly supported (idle mode) and must not be rejected by startup validation.

### Post-Design Re-Check

- Deterministic reconciliation: PASS
- Transactional store safety: PASS
- Source & supply chain integrity: PASS
- Observability & operability: PASS
- Test & contract discipline: PASS
- Decomposition discipline: PASS
- Options validation discipline: PASS

No constitution violations require exception tracking.

## Project Structure

### Documentation (this feature)

```text
specs/008-local-feeds-and-watchers/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane.Abstractions/                 # request + resolution models
├── Nuplane.Runtime/                      # reconciliation + resolver policy + observability
├── Nuplane.Sources.Directory/            # directory desired-state discovery
├── Nuplane/                              # DI extension methods + hosted services
├── Nuplane.Hosting/                      # optional host-level helpers (feed config, samples)
└── Nuplane.Store/                        # transactional activation + state

test/
├── Nuplane.Runtime.Tests/
├── Nuplane.Integration.Tests/
├── Nuplane.Store.Tests/
└── Nuplane.Loading.Tests/

samples/
└── Nuplane.Sample.AspNetCore/            # uses directory drop flow; validates local-directory-only mode
```

**Structure Decision**: Use the existing multi-project Nuplane architecture. Implement the feature by evolving the existing directory desired-source registration into a local-directory feed participant in feed resolution, and by extending existing resolver/observability components rather than introducing parallel “drop folder” subsystems.

## Complexity Tracking

No constitution gate violations or complexity exceptions identified.
