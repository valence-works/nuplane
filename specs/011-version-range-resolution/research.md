# Research: Version Range Resolution

**Feature**: 011-version-range-resolution  
**Date**: 2026-03-08 (revised)

## R-001: NuGet V3 Version Enumeration via NuGet.Protocol

**Decision**: Use the `NuGet.Protocol` client library to enumerate available versions for a package from NuGet V3 feeds.

**Rationale**: The `NuGet.Protocol` package provides `FindPackageByIdResource` which handles service index resolution, `PackageBaseAddress` flat container queries, authentication, and retry logic. This is the same approach used by the official NuGet CLI and Visual Studio. Using the protocol library avoids re-implementing HTTP calls, service index parsing, and error handling that the NuGet team already maintains.

`FindPackageByIdResource.GetAllVersionsAsync()` returns `IEnumerable<NuGetVersion>` — pre-parsed, pre-validated version objects — eliminating the need for manual SemVer validation of feed response strings.

**Alternatives considered**:
- Manual HTTP calls to `PackageBaseAddress/{lowerId}/index.json` — works but re-implements protocol logic already available in `NuGet.Protocol`. Fragile when feeds have non-standard configurations or require authentication.
- `SearchQueryService` — designed for UI/autocomplete, not reliable for complete version listing.
- `RegistrationsBaseUrl` — overweight for version enumeration, adds pagination complexity.

## R-002: Version Range Parsing & Selection via NuGet.Versioning

**Decision**: Use the `NuGet.Versioning` package for version range parsing (`VersionRange.Parse` / `VersionRange.TryParse`) and best-match selection (`VersionRange.FindBestMatch`).

**Rationale**: `NuGet.Versioning` is the canonical implementation of NuGet version range syntax. It handles all documented range notations (exact, bounded, open, floating), pre-release filtering, and SemVer 2.0.0 comparison. Using this package:

- Guarantees compatibility with NuGet ecosystem tooling (dotnet CLI, Visual Studio, nuget.org).
- Eliminates the need for a custom `ParsedVersionRange` struct and custom range evaluation logic.
- Provides `NuGetVersion` type with complete SemVer 2.0.0 comparison (including build metadata handling and pre-release precedence).
- Replaces the existing `VersionKey` struct for version comparison in the resolution pipeline (though `VersionKey` remains available for other codebase uses).

**Supported formats** (handled by `VersionRange.Parse`):

| Notation | Meaning | Example |
|----------|---------|---------|
| `1.0.0` | Minimum version (>= 1.0.0) | Resolves to highest >= 1.0.0 |
| `[1.0.0]` | Exact match | Resolves to exactly `1.0.0` |

> **Nuplane override**: In `IncludePatterns` configuration, bare version `x.y.z` is treated as exact match `[x.y.z]`, NOT as NuGet's default minimum version `[x.y.z, )`. The `NuGetVersionRangeEvaluator` MUST detect bare version strings (parsed by `IncludePatternParser`) and wrap them as `[x.y.z]` before calling `VersionRange.Parse()`. This intentional override is specified in spec.md US2-AS4 and the version-range-evaluator contract.
| `[1.0.0, 2.0.0)` | Range: `>= 1.0.0` and `< 2.0.0` | Best match within range |
| `[1.0.0, 2.0.0]` | Range: `>= 1.0.0` and `<= 2.0.0` | Best match within range |
| `(1.0.0, 2.0.0)` | Range: `> 1.0.0` and `< 2.0.0` | Best match within range |
| `(1.0.0,)` | Open upper bound: `> 1.0.0` | Highest version above `1.0.0` |
| `[1.0.0,)` | Open upper bound: `>= 1.0.0` | Highest version at or above `1.0.0` |
| *(empty)* | No constraint — resolve to latest stable | Highest stable version |

**Alternatives considered**:
- Custom `ParsedVersionRange` struct + `VersionKey` comparisons (previous plan) — achievable but re-implements well-tested logic from `NuGet.Versioning`. Adds maintenance burden and risk of subtle compatibility differences with NuGet ecosystem behavior.

## R-003: Include Pattern Parsing Strategy

**Decision**: Parse version range as a whitespace-separated suffix on the `IncludePatterns` string, after the package identity glob. Detection uses the presence of version range syntax characters (`[`, `(`, or a digit after the last whitespace).

**Rationale**: Unchanged from previous analysis. The current `PackagePatternMatcher` uses `*` and `?` as wildcard characters. Version range syntax uses `[`, `(`, `]`, `)`, and digits — there is no ambiguity between glob patterns and version ranges. This parsing is pure string manipulation with no NuGet library dependency, so it stays in `Nuplane.Runtime`.

**Split algorithm**:
1. Trim the pattern string
2. Find the last segment that starts with `[`, `(`, or a digit preceded by whitespace
3. Everything before is the package glob; everything from that point is the version range
4. If no version range is detected, the entire string is the package glob (resolve to latest)

**Edge cases handled**:
- `"MyPackage"` → glob=`MyPackage`, range=*(empty/latest)*
- `"MyPackage [1.0.0, 2.0.0)"` → glob=`MyPackage`, range=`[1.0.0, 2.0.0)`
- `"MyPackage 1.0.0"` → glob=`MyPackage`, range=`1.0.0`
- `"MyPackage.* [1.0.0,)"` → glob=`MyPackage.*`, range=`[1.0.0,)`
- `"*"` → glob=`*`, range=*(empty/latest)*
- `"* [1.0.0, 2.0.0)"` → glob=`*`, range=`[1.0.0, 2.0.0)`

## R-004: Version List Caching Strategy

**Decision**: Decorator pattern with `CachedFeedVersionEnumerator` wrapping `IFeedVersionEnumerator`. Cache key: `{feedName}:{lowercasePackageId}`. TTL configurable via `FeedResolutionOptions.VersionCacheTtl` (default: 5 minutes). In-memory `ConcurrentDictionary` with timestamped entries.

**Rationale**: Unchanged from previous analysis. The reconciliation cycle runs on a `PollInterval` (default 10 seconds). Without caching, every cycle would issue a `FindPackageByIdResource.GetAllVersionsAsync` call per package per feed. The decorator pattern keeps the caching concern separate from the NuGet.Protocol-based enumeration logic, and the cache lives in `Nuplane.Runtime` (no NuGet dependency required for caching).

**Cache invalidation**: TTL-based only. When the timestamp of a cached entry exceeds the TTL, the next access triggers a fresh enumeration. No explicit invalidation API — the TTL is short enough that stale data self-corrects within one TTL window.

## R-005: Version Selection via NuGet.Versioning

**Decision**: Use `NuGet.Versioning.VersionRange.FindBestMatch(IEnumerable<NuGetVersion>)` for version selection, wrapped in an `IVersionRangeEvaluator` abstraction implemented in `Nuplane.NuGet`.

**Algorithm** (delegated to NuGet.Versioning):
1. Parse the version range string via `VersionRange.Parse()` (or treat empty as "latest").
2. For "latest" (empty range): select the highest stable `NuGetVersion` from the list (filter out pre-release, take max).
3. For explicit ranges: call `VersionRange.FindBestMatch(versions)` which returns the best matching `NuGetVersion`.
4. Pre-release filtering is handled by `NuGet.Versioning` based on range specification.

**Pre-release semantics**: When the range explicitly references a pre-release tag (e.g., `[1.0.0-beta.1, 2.0.0)`), `NuGet.Versioning` includes pre-release versions in matching. When the range has no pre-release tag, only stable versions are matched. This matches NuGet ecosystem behavior exactly.

**Alternatives considered**:
- Custom selection using `VersionKey` comparisons (previous plan) — functional but risks subtle differences from NuGet ecosystem behavior, especially around pre-release precedence edge cases.

## R-006: Nuplane.NuGet Project Boundary

**Decision**: Create a new `Nuplane.NuGet` class library project that carries the `NuGet.Versioning` and `NuGet.Protocol` dependencies and implements Nuplane abstractions defined in `Nuplane.Runtime`.

**Project responsibilities**:
- `NuGetFeedVersionEnumerator : IFeedVersionEnumerator` — version enumeration via NuGet.Protocol
- `NuGetVersionRangeEvaluator : IVersionRangeEvaluator` — version range parsing and selection via NuGet.Versioning

**Abstraction interfaces** (in `Nuplane.Runtime`):
- `IFeedVersionEnumerator` — enumerate available versions for a package from a feed
- `IVersionRangeEvaluator` — parse a version range string and select the best matching version from a list

**Rationale**: Isolating NuGet SDK dependencies in a dedicated project:
- Keeps `Nuplane.Runtime` lightweight (no transitive NuGet SDK dependency tree).
- Follows the existing project decomposition pattern (e.g., `Nuplane.Sources.Directory` isolates file-system concerns).
- Makes it possible to substitute a different feed protocol implementation in the future (e.g., for non-NuGet feeds) without changing core runtime code.
- The main DI project (`Nuplane`) references `Nuplane.NuGet` and registers implementations.

**Dependencies**:
- `Nuplane.NuGet` → `Nuplane.Runtime` (project reference, for abstractions)
- `Nuplane.NuGet` → `NuGet.Versioning` (NuGet package)
- `Nuplane.NuGet` → `NuGet.Protocol` (NuGet package)
- `Nuplane` → `Nuplane.NuGet` (project reference, for DI registration)

## R-007: Feed Version Enumeration Pattern

**Decision**: Use `NuGet.Protocol`'s `SourceRepository` and `FindPackageByIdResource` for version enumeration rather than manual HTTP calls.

**Pattern**:
```csharp
var source = new PackageSource(feed.ServiceIndex);
var repository = Repository.Factory.GetCoreV3(source);
var resource = await repository.GetResourceAsync<FindPackageByIdResource>(ct);
var versions = await resource.GetAllVersionsAsync(packageId, cacheContext, NullLogger.Instance, ct);
```

**Rationale**: `FindPackageByIdResource` encapsulates:
- Service index parsing and `PackageBaseAddress` resource resolution
- HTTP GET to `{baseAddress}/{lowerId}/index.json`
- JSON response parsing → `IEnumerable<NuGetVersion>`
- Proper error handling for HTTP failures
- Credential/authentication support through `PackageSource` configuration

This replaces the need for manual `ResolvePackageBaseAddressAsync` calls and `System.Text.Json` parsing of version list responses. The existing `NuGetRemotePackageAcquirer` continues to use its own HTTP approach for package download (unchanged).

**Error handling**:
- Package not found: `FindPackageByIdResource` returns empty enumerable
- Feed error (4xx/5xx): propagated as `FatalProtocolException`
- Network failure: propagated as appropriate exception
- The `CachedFeedVersionEnumerator` decorator catches and propagates these; does NOT serve stale data after TTL expiry
