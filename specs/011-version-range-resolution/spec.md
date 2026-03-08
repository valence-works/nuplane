# Feature Specification: Version Range Resolution

**Feature Branch**: `011-version-range-resolution`  
**Created**: 2026-03-07  
**Status**: Draft  
**Input**: User description: "The ability to specify the desired version (using version range syntax). When no version (range) specified, always use the latest version from the feed."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Latest Version by Default (Priority: P1)

As an operator configuring a feed with `IncludePatterns`, when I list a package ID without specifying a version, Nuplane MUST resolve and install the latest available version of that package from the feed — not default to `1.0.0`.

**Why this priority**: This is the most common usage pattern. Operators expect packages to resolve to their latest version unless explicitly pinned. The current hardcoded `[1.0.0,)` default silently installs an outdated version if a newer one exists, or fails entirely if `1.0.0` is unavailable on the feed.

**Independent Test**: Configure a feed with an `IncludePatterns` entry for a package that has multiple versions published (e.g., `1.0.0`, `2.0.0`, `3.0.0`). Start reconciliation. Verify the latest version (`3.0.0`) is resolved and installed.

**Acceptance Scenarios**:

1. **Given** a feed with `IncludePatterns: ["MyPackage"]` and no version specified, **When** reconciliation runs, **Then** the system resolves `MyPackage` to the latest stable version available on the feed.
2. **Given** a feed with `IncludePatterns: ["MyPackage"]` and the feed contains versions `1.0.0`, `1.1.0`, `2.0.0`, **When** reconciliation runs, **Then** the system resolves to `2.0.0` (the latest stable version).
3. **Given** a feed with `IncludePatterns: ["MyPackage"]` and the feed is unreachable, **When** reconciliation runs, **Then** the system reports a resolution failure for that package with diagnostic details, and no version is installed from that feed.

---

### User Story 2 - Explicit Version Range in Configuration (Priority: P1)

As an operator, I want to specify a version range for a package in `IncludePatterns` so that Nuplane resolves the best matching version from the feed within my constraints.

**Why this priority**: Equally critical to latest-version resolution. Operators with stability requirements need to pin packages to specific versions or constrain version ranges (e.g., only `1.x` releases, or an exact version).

**Independent Test**: Configure a feed with a versioned include pattern like `MyPackage [1.0.0, 2.0.0)`. Start reconciliation. Verify the highest version within the `[1.0.0, 2.0.0)` range is resolved.

**Acceptance Scenarios**:

1. **Given** a feed with `IncludePatterns: ["MyPackage [1.0.0, 2.0.0)"]` and the feed contains versions `1.0.0`, `1.5.0`, `2.0.0`, `3.0.0`, **When** reconciliation runs, **Then** the system resolves to `1.5.0`.
2. **Given** a feed with `IncludePatterns: ["MyPackage [2.0.0]"]` (exact version), **When** reconciliation runs, **Then** the system resolves to exactly `2.0.0`.
3. **Given** a feed with `IncludePatterns: ["MyPackage [5.0.0, 6.0.0)"]` and no versions in that range exist, **When** reconciliation runs, **Then** the system reports a resolution failure indicating no matching version exists for the specified range.
4. **Given** a feed with `IncludePatterns: ["MyPackage 2.1.0"]` (bare version, no brackets), **When** reconciliation runs, **Then** the system resolves to exactly `2.1.0`.

---

### User Story 3 - Reconciliation Updates to Latest Within Range (Priority: P2)

As an operator, when a new version of a package is published to the feed that satisfies my configured version range (or no range, meaning "latest"), Nuplane MUST reconcile to the newer version on the next reconciliation cycle.

**Why this priority**: Ensures the system remains current with feed updates. Without this, operators would need to manually reconfigure after every upstream release.

**Independent Test**: Configure a package with range `[1.0.0, 2.0.0)`. Reconcile initially (resolves `1.0.0`). Add `1.5.0` to the feed. Trigger another reconciliation. Verify the active version is updated to `1.5.0`.

**Acceptance Scenarios**:

1. **Given** a package previously resolved to `1.0.0` with range `[1.0.0, 2.0.0)`, **When** version `1.5.0` appears on the feed and reconciliation runs, **Then** the active package is updated to `1.5.0`.
2. **Given** a package previously resolved to `1.5.0` with range `[1.0.0, 2.0.0)`, **When** version `2.0.0` appears on the feed and reconciliation runs, **Then** the active package remains `1.5.0` (because `2.0.0` is outside the range).
3. **Given** a package resolved to latest (no version constraint), **When** a newer version appears on the feed and reconciliation runs, **Then** the active package is updated to the newer version.

---

### Edge Cases

- What happens when a version range is syntactically invalid (e.g., `[abc, def)`)? The system MUST reject the pattern at configuration validation time and prevent startup.
- What happens when the feed returns no versions at all for a package ID? The system MUST report a resolution failure and not install any version for that package.
- What happens when the feed returns only pre-release versions and no stable versions? The system MUST fail resolution with a diagnostic message ("no stable versions available"); pre-release versions MUST be excluded unless the version range explicitly references a pre-release tag. The operator can opt in by specifying a range like `[1.0.0-beta.1, )`.
- What happens when multiple feeds provide different versions of the same package? Existing deterministic feed priority and tie-break rules (from `MultiFeedPackageResolver`) continue to apply — the highest-priority feed's resolution wins.
- What happens when the `IncludePatterns` entry uses a wildcard glob pattern (e.g., `*` or `MyPackage.*`) combined with a version range? The version range applies uniformly to all packages matched by the glob pattern.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The `IncludePatterns` configuration MUST support an optional version range suffix on each pattern entry, using NuGet version range syntax (e.g., `"MyPackage [1.0.0, 2.0.0)"`, `"MyPackage [2.0.0]"`, `"MyPackage 1.0.0"`). The `FeedRuleDesiredSource` MUST parse each pattern to separate the package identity glob from the version range component.

- **FR-002**: When a package pattern has no version range specified (e.g., `"MyPackage"` or `"*"`), the `FeedRuleDesiredSource` MUST emit a `PackageRequest` with an empty `VersionRange` to signal "resolve to latest." The hardcoded default of `"[1.0.0,)"` MUST be removed.

- **FR-003**: A version enumeration component MUST query a NuGet V3 feed to list all available versions for a given package ID, using the NuGet client protocol libraries. The enumeration MUST validate each returned version string; malformed entries MUST be discarded with a warning log. The enumeration MUST return validated versions in deterministic SemVer-sorted order.

- **FR-004**: A version selection component MUST evaluate the list of available versions against the requested version range and select the best matching version. For a bounded range, "best matching" means the highest version satisfying the range constraints. For an empty range (latest), "best matching" means the highest stable version available.

- **FR-005**: Pre-release versions MUST be excluded from version selection unless the version range explicitly references a pre-release tag (e.g., `[1.0.0-beta.1, 2.0.0)`).

- **FR-006**: The `MultiFeedPackageResolver` MUST invoke the version enumeration and selection components to resolve a concrete version before downloading the package, replacing the current `NuGetVersionRangeParser.SelectVersion` lower-bound extraction approach.

- **FR-007**: An `IValidateOptions<T>` validator MUST validate version range syntax in `IncludePatterns` entries at startup, registered with `ValidateOnStart()`. Invalid version ranges MUST prevent startup with a descriptive error message.

- **FR-008**: The version resolution result (selected version, candidate count, feed source, cache hit/miss) MUST be recorded in `FeedResolutionDecision` for observability.

- **FR-009**: Directory-sourced packages (`DirectoryNupkgDesiredSource`) MUST continue to use exact versions extracted from `.nupkg` filenames. Version range resolution applies only to remote NuGet feeds.

- **FR-010**: The version enumeration component MUST cache version lists per package ID per feed with a configurable TTL (default: 5 minutes). A `VersionCacheTtl` configuration property MUST be added to feed resolution options. The cache MUST be invalidated when the TTL expires, and fresh enumeration MUST occur on the next resolution request. The `VersionCacheTtl` property MUST be validated by an `IValidateOptions<T>` validator (non-negative duration) with `ValidateOnStart()`.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Version resolution MUST be deterministic — given the same feed contents and version range, the same version MUST always be selected. Version lists MUST be sorted using consistent SemVer ordering before selection. Repeated reconciliation cycles with unchanged feed contents MUST produce identical results (idempotency).

- **OSR-002**: If version resolution fails (no matching version, feed unreachable, enumeration error), the reconciler MUST preserve the last-known-good active version and MUST record the failure with diagnostic details (feed name, package ID, requested range, error reason). If no last-known-good version exists (first-ever resolution), the package MUST be skipped (not installed) and a warning MUST be logged with full diagnostic details. Partial version resolution failures MUST NOT corrupt the active package set or block reconciliation of other packages.

- **OSR-003**: Feed version enumeration MUST use only explicitly configured and trusted feed sources. The enumeration endpoint MUST be derived from the same service index used for package download — no additional external endpoints are introduced. Existing feed credentials apply to version enumeration requests. Version strings received from feeds MUST be validated as well-formed SemVer before use; malformed strings MUST be discarded and logged as warnings to defend against compromised feed responses.

- **OSR-004**: Version resolution MUST emit structured log entries including: package ID, requested version range, resolved version (or failure reason), feed name, and enumeration duration. A metric for resolution outcomes (success/failure/no-match) MUST be published.

- **OSR-005**: All changes to version resolution, version enumeration, version selection, and pattern parsing MUST include automated tests. Unit tests MUST cover: exact version, open range, bounded range, latest (no range), no matching version, invalid range syntax, pre-release exclusion, and pre-release inclusion. Integration/contract tests MUST cover end-to-end resolution from feed enumeration through version selection.

### Key Entities

- **VersionRange**: Represents a parsed version constraint (exact version, bounded range, or "latest"). Carries the original string expression and the parsed bounds for evaluation.
- **PackageVersionList**: An ordered list of available versions for a given package ID from a specific feed. Used as input to version selection.
- **VersionResolutionResult**: The outcome of resolving a version range against available versions — the selected version, or a failure reason if no match exists.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Packages configured without a version constraint resolve to the latest stable version on every reconciliation cycle — not to a hardcoded default.
- **SC-002**: Packages configured with a version range resolve to the highest version satisfying the range on every reconciliation cycle.
- **SC-003**: Operators can constrain package versions via configuration without code changes or redeployment beyond updating the configuration file.
- **SC-004**: Invalid version range syntax is caught at startup, preventing misconfigured systems from running.
- **SC-005**: Version resolution decisions are visible in logs and metrics, enabling operators to audit which version was selected and why.
- **SC-006**: Existing directory-source package resolution behavior is unchanged — no regressions in local `.nupkg` workflows.

## Clarifications

### Session 2026-03-07

- Q: Should version enumeration results be cached across reconciliation cycles? → A: Cache version lists with a configurable TTL (default 5 minutes).
- Q: When resolving "latest" and only pre-release versions exist, what should happen? → A: Fail resolution with a diagnostic message ("no stable versions available").
- Q: Should version ranges be specified inline in IncludePatterns strings, or as a separate structured configuration property? → A: Inline in IncludePatterns strings (e.g., "MyPackage [1.0.0, 2.0.0)") — backward-compatible, no schema change.
- Q: When version resolution fails and there is no last-known-good version, what should happen? → A: Skip the package (do not install anything) and log a warning with full diagnostic details.
- Q: Should enumerated version strings be validated before being used for selection? → A: Validate each version string is well-formed SemVer; discard malformed entries with a warning log.

## Assumptions

- NuGet V3 feeds expose a `PackageBaseAddress` resource that supports listing available versions for a package ID (standard NuGet V3 protocol).
- The existing `IncludePatterns` syntax uses simple glob patterns today; appending a version range after the package ID pattern (separated by whitespace) is a backward-compatible extension — existing patterns without version ranges continue to work as before (but now resolve to latest instead of `1.0.0`).
- SemVer 2.0.0 ordering is the standard for version comparison, consistent with NuGet conventions.
- The system does not need to support authentication changes for version enumeration — the same credentials used for package download apply to version listing.
- NuGet-specific functionality (version range parsing, version enumeration, version selection) is provided by a dedicated `Nuplane.NuGet` class library that carries the dependency on NuGet client packages (`NuGet.Versioning`, `NuGet.Protocol`). Core runtime abstractions remain in `Nuplane.Runtime` with no direct NuGet SDK dependency.
