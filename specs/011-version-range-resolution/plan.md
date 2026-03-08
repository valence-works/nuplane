# Implementation Plan: Version Range Resolution

**Branch**: `011-version-range-resolution` | **Date**: 2026-03-08 (revised) | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/011-version-range-resolution/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

Nuplane currently hardcodes a version range of `[1.0.0,)` when resolving packages from feed `IncludePatterns`, then extracts only the lower bound as the concrete version. This feature replaces that behavior with proper version resolution: querying NuGet V3 feeds for all available versions of a package using `NuGet.Protocol`, selecting the best match using `NuGet.Versioning`, and caching enumeration results with a configurable TTL. A new `Nuplane.NuGet` class library isolates the NuGet SDK dependencies behind runtime abstractions. Changes span `Nuplane.Runtime` (abstractions, caching, pattern parsing), `Nuplane.NuGet` (NuGet-specific implementations), and `Nuplane` (DI registration), with a new `IValidateOptions<T>` validator for version range syntax at startup.

## Technical Context

**Language/Version**: C# / .NET 8.0, 9.0, 10.0 (multi-target)
**Primary Dependencies**: Microsoft.Extensions.{Options, Logging, DependencyInjection, Configuration} v10.0.3; NuGet.Versioning, NuGet.Protocol (in `Nuplane.NuGet` only)
**Storage**: File-system-based package install root (no database)
**Testing**: xUnit 2.9.3, NSubstitute 5.3.0, coverlet.collector 8.0.0
**Target Platform**: Cross-platform .NET library (consumed by ASP.NET Core hosts and similar)
**Project Type**: Library (NuGet-distributed runtime package management infrastructure)
**Performance Goals**: Version enumeration HTTP calls cached per TTL; reconciliation cycle latency not materially increased beyond single HTTP round-trip per uncached package/feed
**Constraints**: NuGet SDK dependencies (`NuGet.Versioning`, `NuGet.Protocol`) isolated in `Nuplane.NuGet` project; core `Nuplane.Runtime` retains no direct NuGet SDK dependency
**Scale/Scope**: Typically 1–50 packages across 1–5 feeds per host

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **Deterministic reconciliation**: ✅ PASS — Version lists are SemVer-sorted before selection (OSR-001). Given the same feed contents and version range, the same version is always selected. Cache TTL produces deterministic results within a window. Idempotent: repeated cycles with unchanged feed data select the same version.

- **Transactional store safety**: ✅ PASS — Version resolution is a read-only pre-download step; the existing stage/validate/publish/atomic-switch flow in `MultiFeedPackageResolver` is preserved. On resolution failure, LKG is preserved (OSR-002). No-LKG first-resolution failures skip the package with a warning, not corrupt state.

- **Source integrity**: ✅ PASS — Enumeration uses `NuGet.Protocol`'s `FindPackageByIdResource`, which resolves through the configured service index (OSR-003). Feed credentials apply via NuGet.Protocol's `PackageSource` configuration. The `NuGet.Versioning` and `NuGet.Protocol` packages are well-maintained, widely-used first-party Microsoft packages. Version validation is handled by `NuGetVersion.TryParse()`.

- **Observability**: ✅ PASS — `FeedResolutionDecision` extended with enumerated version count and cache hit/miss (FR-008). Structured logs include package ID, requested range, resolved version, feed name, and enumeration duration (OSR-004). Metric for resolution outcomes (success/failure/no-match).

- **Test discipline**: ✅ PASS — OSR-005 mandates unit tests for: exact version, open range, bounded range, latest, no-match, invalid syntax, pre-release exclusion/inclusion. Integration tests cover end-to-end enumeration through selection. Existing test patterns (xUnit, NSubstitute, `IValidateOptions<T>` validator tests) provide clear templates.

- **Decomposition discipline**: ✅ PASS — Each FR names a concrete architectural element: `FeedRuleDesiredSource` (FR-001/002), version enumeration in `Nuplane.NuGet` (FR-003), version selection via `NuGet.Versioning` in `Nuplane.NuGet` (FR-004), `MultiFeedPackageResolver` (FR-006), `IValidateOptions<T>` validator (FR-007). NuGet dependencies isolated in `Nuplane.NuGet`; abstractions in `Nuplane.Runtime`; DI wiring in `Nuplane`. Mechanism (parsing, enumeration, selection, caching) and driver (reconciliation cycle invocation) are separate. Configuration properties (`VersionCacheTtl`) have explicit consumer tasks (FR-010).

- **Options validation discipline**: ✅ PASS — FR-007 requires `IValidateOptions<T>` validator for version range syntax with `ValidateOnStart()`. FR-010 requires `IValidateOptions<T>` validation for `VersionCacheTtl` (non-negative). Options classes remain data-only. Follows established pattern from `FeedResolutionOptionsValidator` and `NuplaneSetupOptionsValidator`.

## Project Structure

### Documentation (this feature)

```text
specs/011-version-range-resolution/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
└── tasks.md             # Phase 2 output (/speckit.tasks command)
```

### Source Code (repository root)

```text
src/
├── Nuplane/
│   ├── NuplaneServiceCollectionExtensions.cs    # DI registration (add Nuplane.NuGet services + validator)
│   └── Options/Validation/
│       └── FeedResolutionOptionsValidator.cs    # Extended: VersionCacheTtl validation
├── Nuplane.Abstractions/
│   └── PackageRequest.cs                        # Unchanged (VersionRange already supports empty = latest)
├── Nuplane.NuGet/                               # NEW project (NuGet SDK dependencies)
│   ├── Nuplane.NuGet.csproj                     # References: NuGet.Versioning, NuGet.Protocol, Nuplane.Runtime
│   ├── NuGetFeedVersionEnumerator.cs            # IFeedVersionEnumerator impl via NuGet.Protocol
│   └── NuGetVersionRangeEvaluator.cs            # IVersionRangeEvaluator impl via NuGet.Versioning
└── Nuplane.Runtime/
    ├── Feeds/
    │   ├── MultiFeedPackageResolver.cs          # Modified: call IFeedVersionEnumerator + IVersionRangeEvaluator
    │   ├── NuGetRemotePackageAcquirer.cs        # Unchanged (download remains exact-version)
    │   ├── Configuration/
    │   │   └── FeedResolutionOptions.cs         # Extended: add VersionCacheTtl property
    │   └── Versioning/                          # NEW directory for version resolution abstractions
    │       ├── IFeedVersionEnumerator.cs         # NEW: interface for querying feed for available versions
    │       ├── IVersionRangeEvaluator.cs         # NEW: interface for version range parsing + selection
    │       ├── CachedFeedVersionEnumerator.cs    # NEW: TTL-based cache decorator (no NuGet dependency)
    │       ├── PackageVersionList.cs             # NEW: version list data record
    │       └── VersionResolutionResult.cs        # NEW: result record for version resolution
    ├── Reconciliation/Models/
    │   └── FeedResolutionDecision.cs             # Extended: add EnumeratedVersionCount, CacheHit properties
    ├── Sources/
    │   └── FeedRuleDesiredSource.cs              # Modified: parse version range from IncludePatterns
    └── Versioning/
        ├── NuGetVersionRangeParser.cs            # Simplified or removed: range parsing now in Nuplane.NuGet
        ├── VersionKey.cs                          # Unchanged (used elsewhere in codebase)
        └── IncludePatternParser.cs               # NEW: separate package glob from version range suffix

test/
├── Nuplane.NuGet.Tests/                         # NEW test project
│   ├── NuGetFeedVersionEnumeratorTests.cs       # Version enumeration via NuGet.Protocol tests
│   └── NuGetVersionRangeEvaluatorTests.cs       # Version range evaluation via NuGet.Versioning tests
└── Nuplane.Runtime.Tests/
    ├── Versioning/
    │   ├── IncludePatternParserTests.cs          # NEW: pattern parsing tests
    │   └── CachedFeedVersionEnumeratorTests.cs  # NEW: cache decorator tests
    ├── Sources/
    │   └── FeedRuleDesiredSourceTests.cs         # Extended: version range from patterns
    ├── Feeds/
    │   └── MultiFeedPackageResolverTests.cs      # Extended: end-to-end resolution with enumeration
    └── Configuration/
        └── FeedResolutionOptionsValidatorTests.cs # Extended: VersionCacheTtl + range syntax validation
```

**Structure Decision**: NuGet SDK dependencies (`NuGet.Versioning`, `NuGet.Protocol`) are isolated in a new `Nuplane.NuGet` project, following the existing decomposition pattern (e.g., `Nuplane.Sources.Directory` isolates file-system concerns). Runtime abstractions (`IFeedVersionEnumerator`, `IVersionRangeEvaluator`) and framework-agnostic code (caching decorator, pattern parser) stay in `Nuplane.Runtime`. The main `Nuplane` DI project references `Nuplane.NuGet` and registers implementations.

## Post-Design Constitution Re-evaluation

*Re-checked after Phase 1 design revision (NuGet.Versioning + NuGet.Protocol via Nuplane.NuGet project).*

All 7 gates remain **✅ PASS**. No new concerns introduced by the revised architecture:

- **Deterministic reconciliation**: `NuGet.Versioning.VersionRange.FindBestMatch()` produces deterministic results for the same inputs. Version list ordering delegated to NuGet SDK's built-in comparison.
- **Transactional store safety**: Version enumeration remains read-only; existing stage/validate/publish flow untouched.
- **Source integrity**: `NuGet.Protocol` resolves through the configured service index. `NuGet.Versioning` and `NuGet.Protocol` are first-party Microsoft packages, widely used and well-maintained. New NuGet package dependencies are isolated in `Nuplane.NuGet` — core runtime has no direct NuGet SDK dependency.
- **Observability**: Data model adds `EnumeratedVersionCount` and `CacheHit` to `FeedResolutionDecision`; contracts specify structured log entries.
- **Test discipline**: All contracts include explicit test contract sections. NuGet-specific tests in `Nuplane.NuGet.Tests`; caching and parsing tests in `Nuplane.Runtime.Tests`.
- **Decomposition discipline**: Clean separation: `Nuplane.Runtime` defines abstractions (`IFeedVersionEnumerator`, `IVersionRangeEvaluator`); `Nuplane.NuGet` provides NuGet-specific implementations; `Nuplane` wires DI. Caching decorator and pattern parser remain in Runtime (no NuGet dependency needed).
- **Options validation discipline**: `VersionCacheTtl` validation via `IValidateOptions<T>` unchanged. Version range syntax validation uses `IVersionRangeEvaluator.IsValidRange()` (backed by `VersionRange.TryParse()`).

## Complexity Tracking

No constitution violations to justify. All gates pass.
