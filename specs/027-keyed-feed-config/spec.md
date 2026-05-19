# Feature Specification: Key-Based Feed Setup Configuration

**Feature Branch**: `027-keyed-feed-config`
**Created**: 2026-05-18
**Status**: Draft
**Input**: User description: "Change Nuplane feed setup configuration from array-based feeds to key-based feeds, while preserving backwards compatibility with the current array format."

## Problem

Nuplane setup configuration currently declares feeds as an array under `Nuplane:Setup:Feeds`. This works when a host has one configuration file, but it behaves poorly with layered .NET configuration providers because array entries merge by numeric position rather than by logical feed identity. A later provider that intends to override `feedz.io` may instead override `Feeds:0`, partially merge into the wrong feed, or leave duplicate entries depending on provider order and array length.

Nuplane feeds are logically named resources. Configuration should allow operators to address a feed by its name so mounted or later-loaded configuration can override the same feed deterministically.

## Goals

- Support a key-based `Nuplane:Setup:Feeds` object where each child key is the canonical feed name.
- Preserve the existing array-based `Nuplane:Setup:Feeds` format for backwards compatibility.
- Allow key-based remote feeds and directory-backed feeds to use the same feed properties supported today.
- Make mixed, layered, or conflicting declarations deterministic and diagnosable.
- Prefer key-based examples in user documentation because they compose predictably with layered .NET configuration.

## Non-Goals

- Removing support for the existing array-based feed setup format.
- Changing feed source trust policy, package resolution policy, or credential secret-handling semantics.
- Making JSON object declaration order semantically meaningful.
- Changing duplicate feed-name behavior for purely array-based configuration unless necessary to prevent ambiguous mixed-format registration.
- Replacing the existing feed priority model.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Configure Feeds By Name (Priority: P1)

As a host operator, I want to declare setup feeds as a keyed object so each feed is configured by its logical name instead of by its array position.

**Why this priority**: This is the core feature and fixes the configuration layering problem without requiring existing users to migrate immediately.

**Independent Test**: Configure remote and directory feeds under `Nuplane:Setup:Feeds:{feedName}` with no inner `Name` property, start configuration-driven Nuplane registration, and verify each feed is registered with the key as its name and with all configured properties preserved.

**Acceptance Scenarios**:

1. **Given** a key-based remote feed named `nuget.org`, **When** Nuplane reads setup configuration, **Then** the registered feed name is `nuget.org` and the configured `ServiceIndex`, `IncludePatterns`, `IncludeAll`, and `Credentials` values are applied.
2. **Given** a key-based directory feed named `local-packages`, **When** `Nuplane.Sources.Directory` reads setup configuration, **Then** the directory feed is registered with `local-packages` as its name and the configured `DirectoryPath`, include settings, and directory watcher options are applied.
3. **Given** a key-based feed entry without a `Name` property, **When** configuration is read, **Then** the entry remains valid and the containing key is used as the feed name.

---

### User Story 2 - Preserve Existing Array Configuration (Priority: P2)

As an existing Nuplane user, I want my current array-based feed setup configuration to keep working so upgrading Nuplane does not force an immediate configuration rewrite.

**Why this priority**: Backwards compatibility is required for safe adoption and should be independently verifiable.

**Independent Test**: Use the current `Nuplane:Setup:Feeds:0:Name` array shape with remote and directory feeds and verify the same registration results as before the feature.

**Acceptance Scenarios**:

1. **Given** existing array-based feed configuration, **When** Nuplane reads setup configuration, **Then** feed registration, include pattern handling, directory options, and credentials behave as they did before this feature.
2. **Given** duplicate feed names in array-only configuration, **When** Nuplane reads setup configuration, **Then** duplicate handling follows the current behavior unless a separate documented validation rule already applies.

---

### User Story 3 - Override Feeds Through Layered Configuration (Priority: P3)

As a platform maintainer, I want later configuration providers to override a same-named feed by key without creating duplicate feed registrations or relying on matching array indexes.

**Why this priority**: Layered configuration is the motivating operational scenario for the new format.

**Independent Test**: Build configuration from a base provider and a later provider where both define `Nuplane:Setup:Feeds:feedz.io`; verify the later provider's feed properties win according to normal .NET key precedence and exactly one `feedz.io` feed is registered.

**Acceptance Scenarios**:

1. **Given** a base configuration defines `Nuplane:Setup:Feeds:feedz.io:ServiceIndex` and a later configuration provider defines the same key with a different service index, **When** Nuplane reads setup configuration, **Then** only one `feedz.io` feed is registered and it uses the effective layered configuration value.
2. **Given** one provider contributes include patterns and a later provider overrides the same keyed feed, **When** Nuplane reads setup configuration, **Then** include patterns follow standard .NET configuration binding for that keyed path and do not produce a second feed with the same name.

---

### User Story 4 - Diagnose Ambiguous Feed Names (Priority: P4)

As an operator, I want clear diagnostics when feed names are ambiguous or conflicting so configuration mistakes fail predictably.

**Why this priority**: Keyed configuration introduces a second declaration shape, so conflicts must not silently register unintended sources.

**Independent Test**: Configure a keyed feed where the containing key and inner `Name` property disagree, then verify startup or configuration activation fails with a message identifying both names and the configuration path.

**Acceptance Scenarios**:

1. **Given** a keyed feed entry `Nuplane:Setup:Feeds:feedz.io` with `Name` set to `other-feed`, **When** Nuplane validates setup configuration, **Then** validation fails with a clear key/name mismatch error.
2. **Given** effective configuration contains both an array-style and keyed-style declaration for the same feed name, **When** Nuplane translates setup configuration, **Then** keyed declaration wins deterministically for that feed name, no duplicate registration is created, and a warning diagnostic identifies the ignored array declaration.

### Edge Cases

- `Nuplane:Setup:Feeds` has no children.
- A keyed feed key is empty, whitespace, or only present because a provider emitted an empty path segment.
- A feed child key is all digits and must be interpreted as an array entry rather than a keyed feed name.
- A keyed feed contains both `ServiceIndex` and `DirectoryPath`.
- A keyed feed contains neither `ServiceIndex` nor `DirectoryPath`.
- A keyed feed includes `Name` with different casing but the same logical name as the containing key.
- A keyed feed uses `IncludeAll` and `IncludePatterns` together.
- `IncludePatterns` has duplicate, blank, or layered array entries.
- Directory-backed keyed feed options include `Directory:Watch` and `Directory:DebounceWindow`.
- A mixed configuration contains one array feed named `feedz.io` and one keyed feed named `feedz.io`.
- Feed names differ only by case across array and keyed declarations.
- Feed resolution priorities are configured separately from setup feed declarations.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The setup feed configuration translator MUST support key-based feeds under `Nuplane:Setup:Feeds:{feedName}` in addition to the existing `Nuplane:Setup:Feeds:{index}` array form.
- **FR-002**: For key-based feed entries, the containing configuration key MUST be the canonical feed name.
- **FR-003**: For key-based feed entries, the `Name` property MUST be optional.
- **FR-004**: If a key-based feed entry includes `Name`, setup validation MUST require it to match the containing key using Nuplane's feed-name comparison rules; mismatches MUST fail with a message that includes the containing key, the supplied `Name`, and the configuration path.
- **FR-005**: Key-based feed entries MUST support the existing setup properties `ServiceIndex`, `DirectoryPath`, `IncludePatterns`, `IncludeAll`, `Credentials`, and `Directory`.
- **FR-006**: Remote feed setup registration MUST translate key-based entries with `ServiceIndex` into the same feed registration outcome as equivalent array-based entries.
- **FR-007**: Directory feed setup registration in `Nuplane.Sources.Directory` MUST translate key-based entries with `DirectoryPath` into the same directory source registration outcome as equivalent array-based entries.
- **FR-008**: Setup translation MUST classify feed children as array entries when the child key is all digits and as keyed entries when the child key is non-numeric; all-digit feed names are not supported in the key-based format.
- **FR-009**: Array-based feed entries MUST continue to require their existing `Name` value and preserve current array-only duplicate handling unless an existing validator already rejects the configuration.
- **FR-010**: Key-based setup translation MUST NOT rely on JSON object order to define feed resolution behavior.
- **FR-011**: Feed resolution order for both array-based and key-based setup feeds MUST continue to use existing feed priority configuration: lower configured priority values are considered first, and feeds with equal or missing priority are ordered deterministically by feed name.
- **FR-012**: Documentation MUST describe how operators configure feed priority separately from keyed feed declarations when feed resolution order matters.
- **FR-013**: When effective configuration contains both array-style and keyed-style declarations for the same feed name, setup translation MUST register a single feed where the keyed declaration takes precedence and MUST emit an actionable warning diagnostic about the ignored array declaration.
- **FR-014**: Mixed array/keyed duplicate detection MUST compare feed names case-insensitively to match existing feed registration semantics.
- **FR-015**: Validation MUST reject key-based feed entries with empty or whitespace feed keys; all-digit `Feeds` child keys MUST be classified as array entries before keyed-feed validation is applied.
- **FR-016**: Validation MUST reject any feed entry that specifies both `ServiceIndex` and `DirectoryPath`.
- **FR-017**: Validation MUST reject any feed entry that specifies neither `ServiceIndex` nor `DirectoryPath`.
- **FR-018**: Validation MUST reject `ServiceIndex` values that are missing, relative, or not valid absolute URIs for remote feed entries.
- **FR-019**: Validation MUST reject `DirectoryPath` values that are empty or whitespace for directory feed entries.
- **FR-020**: `IncludePatterns` and `IncludeAll` behavior MUST remain unchanged for both configuration shapes, including current handling of blank, duplicate, and wildcard pattern values.
- **FR-021**: Setup translation MUST expose useful diagnostics for skipped, overridden, ambiguous, or conflicting feed declarations without logging credential secret values.
- **FR-022**: User documentation, README examples, and relevant wiki pages MUST prefer the key-based feed setup format and include a before/after migration example.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation/apply flows MUST remain idempotent for repeated identical effective feed configuration, regardless of whether feeds were declared by array or by key.
- **OSR-002**: Update flows MUST preserve existing transactional behavior and last-known-good fallback semantics; invalid feed setup configuration MUST fail before package source changes are applied under an unintended feed identity.
- **OSR-003**: Source trust and credential validation requirements MUST remain unchanged; keyed feed support MUST NOT change allowed sources, integrity checks, credential lookup, or secret redaction behavior.
- **OSR-004**: Observability MUST include structured diagnostics for key/name mismatches, mixed array/keyed duplicate resolution warnings, invalid source-type declarations, and feed registration identity, without logging credential secret values.
- **OSR-005**: Tests MUST cover key-based remote feeds, key-based directory feeds, existing array feeds, missing `Name`, matching `Name`, mismatched `Name`, layered overrides, include pattern preservation, directory option preservation, deterministic priority-based ordering, and mixed array/keyed declarations.

### Key Entities

- **Setup Feed Declaration**: The effective configuration entry under `Nuplane:Setup:Feeds`, expressed either as a numeric array entry or a named keyed entry.
- **Feed Name**: The canonical logical identity for a feed. In keyed entries it comes from the configuration key; in array entries it comes from the existing `Name` property.
- **Remote Feed Declaration**: A setup feed declaration with `ServiceIndex`, optional `Credentials`, and include selection settings.
- **Directory Feed Declaration**: A setup feed declaration with `DirectoryPath`, include selection settings, and directory watcher settings.
- **Feed Priority**: Existing Nuplane feed resolution metadata that orders candidate feeds; it remains separate from setup feed declaration order.
- **Feed Setup Diagnostic**: A validation or registration diagnostic that identifies ambiguous, overridden, or invalid feed setup configuration without exposing secrets.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A host can declare at least one remote feed using `Nuplane:Setup:Feeds:{feedName}` without an inner `Name` property and Nuplane registers it under `{feedName}`.
- **SC-002**: A host can declare at least one directory feed using `Nuplane:Setup:Feeds:{feedName}` and `Nuplane.Sources.Directory` preserves directory watcher settings.
- **SC-003**: Existing array-based feed setup examples continue to register feeds successfully without user changes.
- **SC-004**: A later configuration provider can override `Nuplane:Setup:Feeds:feedz.io:ServiceIndex` and the effective registration contains exactly one `feedz.io` feed with the later value.
- **SC-005**: A key/name mismatch fails validation with a diagnostic that identifies both names and the affected configuration path.
- **SC-006**: Mixed array/keyed declarations for the same feed name produce exactly one registered feed and a warning diagnostic describing the keyed override and ignored array declaration.
- **SC-007**: Candidate feed ordering remains deterministic and is verified by tests using existing feed priority configuration and name-based tie-breaking.
- **SC-008**: Documentation includes key-based feed setup examples, a before/after migration example, and an explanation of why keyed configuration is preferred for layered .NET configuration.

## Assumptions

- Existing feed priority configuration is the correct mechanism for feed resolution order; keyed feed declarations do not introduce an `Order` or `Priority` property inside each feed entry.
- Feed names are compared case-insensitively where Nuplane currently treats feed names case-insensitively.
- The effective `IConfiguration` view may contain both numeric and non-numeric children under `Feeds`, so setup translation should classify entries by child key rather than by assuming the entire section has one shape.
- All-digit child keys under `Nuplane:Setup:Feeds` are reserved for array entries and cannot be used as key-based feed names.
- Keyed-feed validation requires access to raw `IConfiguration` feed child paths because standard list binding cannot preserve non-numeric keyed children or their configuration paths.
- Keyed declarations take precedence over array declarations with the same feed name only when both shapes are present in the same effective configuration.
- Array-only duplicate behavior remains unchanged to avoid adding an unrelated breaking change.

## Clarifications

- **2026-05-18**: Q: How should same-name mixed array/keyed feed declarations be handled? → A: Keyed declaration wins, array declaration is ignored for that feed, and Nuplane emits a warning diagnostic.
- **2026-05-18**: Q: How should numeric-looking feed child keys be interpreted? → A: All-digit child keys are always array entries; keyed feed names must not be all digits.
- **2026-05-18**: Feed resolution order for key-based setup uses existing feed priority configuration: lower priority first, then feed name for deterministic tie-breaking. JSON object order is not semantic.
- **2026-05-18**: A `Name` property inside a keyed feed is allowed only when it matches the containing key; mismatch is a configuration error.
- **2026-05-18**: If a mixed array/keyed effective configuration declares the same feed name in both shapes, the keyed declaration wins and Nuplane emits a warning diagnostic instead of registering duplicates.
