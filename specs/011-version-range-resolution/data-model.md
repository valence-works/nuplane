# Data Model: Version Range Resolution

**Feature**: 011-version-range-resolution  
**Date**: 2026-03-08 (revised)

## Entities

### ParsedIncludePattern

Represents a parsed `IncludePatterns` entry split into its package glob and optional version range components.

| Field | Type | Description |
|-------|------|-------------|
| PackageGlob | string | The package identity glob pattern (e.g., `MyPackage`, `MyPackage.*`, `*`) |
| VersionRange | string | The version range suffix, or empty string for "resolve to latest" |
| OriginalPattern | string | The original unparsed pattern string for diagnostics |

**Validation rules**:
- `PackageGlob` must not be empty or whitespace.
- `VersionRange`, if non-empty, must be syntactically valid NuGet version range notation.
- Validated at startup via `IValidateOptions<T>`.

**Produced by**: `IncludePatternParser`  
**Consumed by**: `FeedRuleDesiredSource` (to create `PackageRequest` objects with correct `VersionRange`)

---

> **Note**: Version range parsing and evaluation uses `NuGet.Versioning.VersionRange` internally within `Nuplane.NuGet`. There is no custom `ParsedVersionRange` data model entity — the NuGet SDK type is the canonical representation.

---

### PackageVersionList

An ordered list of available versions for a given package from a specific feed.

| Field | Type | Description |
|-------|------|-------------|
| PackageId | string | The package identifier |
| FeedName | string | The feed that produced this list |
| Versions | IReadOnlyList\<string\> | The available version strings, sorted ascending by SemVer |
| EnumeratedAt | DateTimeOffset | The timestamp when the versions were enumerated |

**Validation rules**:
- `Versions` is sorted ascending by SemVer ordering.
- All entries are validated as well-formed version strings (malformed entries discarded during enumeration).

**Produced by**: `NuGetFeedVersionEnumerator` (or `CachedFeedVersionEnumerator`)  
**Consumed by**: `IVersionRangeEvaluator.SelectBestMatch()` (implemented by `NuGetVersionRangeEvaluator` in `Nuplane.NuGet`)

---

### VersionResolutionResult

The outcome of resolving a version range against available versions.

| Field | Type | Description |
|-------|------|-------------|
| Success | bool | Whether a matching version was found |
| SelectedVersion | string? | The concrete version string selected, or null on failure |
| CandidateCount | int | The total number of versions evaluated |
| FailureReason | string? | Diagnostic reason when no version matched |

**Produced by**: `IVersionRangeEvaluator.SelectBestMatch()` (implemented by `NuGetVersionRangeEvaluator` in `Nuplane.NuGet`)  
**Consumed by**: `MultiFeedPackageResolver.ResolveAsync()` (to determine download version)

---

### FeedResolutionDecision (extended)

Existing entity extended with version enumeration observability fields.

| Field | Type | Description | Status |
|-------|------|-------------|--------|
| PackageId | string | Package identifier | Existing |
| RequestedFeed | string? | Feed name from the request | Existing |
| CandidateFeeds | IReadOnlyList\<string\> | Feed candidates considered | Existing |
| SelectedFeed | string? | Feed that provided the resolution | Existing |
| SelectedVersion | string? | Resolved version | Existing |
| DecisionPath | string | How the decision was reached | Existing |
| CorrelationId | string | Reconciliation cycle correlation ID | Existing |
| FeedUnavailable | bool | Whether the feed was marked unavailable | Existing |
| FailureReason | string? | Reason for failure | Existing |
| **EnumeratedVersionCount** | **int** | **Number of versions returned by feed** | **New** |
| **CacheHit** | **bool** | **Whether the version list was served from cache** | **New** |

---

### FeedResolutionOptions (extended)

Existing options entity extended with version cache configuration.

| Field | Type | Description | Default | Status |
|-------|------|-------------|---------|--------|
| Feeds | List\<FeedDefinition\> | Feed definitions | [] | Existing |
| FeedPriorities | Dictionary\<string, int\> | Feed priority map | {} | Existing |
| UnavailableFeeds | HashSet\<string\> | Unavailable feed names | {} | Existing |
| PolicyMode | FeedResolutionPolicyMode | Resolution policy | Fallback | Existing |
| DeterministicFeedOrder | bool | Deterministic ordering | true | Existing |
| StopOnFirstSuccessfulFeed | bool | Stop on first success | false | Existing |
| ValidateDeterministicOrdering | bool | Validate ordering | true | Existing |
| PackageInstallRoot | string? | Install root path | null | Existing |
| **VersionCacheTtl** | **TimeSpan** | **TTL for version enumeration cache** | **5 minutes** | **New** |

**Validation rules** (new):
- `VersionCacheTtl` must be non-negative (`TimeSpan.Zero` disables caching).

## Relationships

```
IncludePatterns (string[])
    │
    ▼ IncludePatternParser.Parse()
ParsedIncludePattern
    │
    ├── PackageGlob → PackagePatternMatcher (existing)
    │
    └── VersionRange ─────────────────────────┐
                                               │
PackageVersionList ────────────────────────────┤
    │                                          │
    │                    IVersionRangeEvaluator.SelectBestMatch()
    │                    (NuGetVersionRangeEvaluator in Nuplane.NuGet)
    │                                          │
    │                                          ▼
    │                                    VersionResolutionResult
    │                                          │
    ▼                                          ▼
IFeedVersionEnumerator                   MultiFeedPackageResolver.ResolveAsync()
  ├─ NuGetFeedVersionEnumerator                │
  │  (Nuplane.NuGet)                           ▼
  └─ CachedFeedVersionEnumerator         FeedResolutionDecision (extended)
     (Nuplane.Runtime)
```
