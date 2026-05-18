# Data Model: Key-Based Feed Setup Configuration

**Feature**: 027-keyed-feed-config
**Date**: 2026-05-18

## Entities

### Setup Feed Section

Represents the effective `Nuplane:Setup:Feeds` configuration section after all providers have been layered.

| Field | Type | Description |
|-------|------|-------------|
| Path | string | Configuration path, normally `Nuplane:Setup:Feeds` |
| Children | IConfigurationSection[] | Direct child sections keyed by either numeric array index or feed name |

**Validation rules**:
- No validation is performed at this aggregate level beyond child classification.
- All-digit child keys are interpreted as array entries.
- Non-numeric child keys are interpreted as keyed entries.

### Setup Feed Declaration

Effective representation of one feed declaration, independent of whether it came from array or keyed configuration.

| Field | Type | Description |
|-------|------|-------------|
| Name | string | Canonical feed name used for registration |
| SourceShape | enum | `Array` or `Keyed` |
| ConfigurationPath | string | Original child configuration path for diagnostics |
| ArrayIndex | int? | Numeric index when declared through the array shape |
| Key | string? | Keyed feed name when declared through the keyed shape |
| Options | NuplaneFeedSetupOptions | Existing property bag for feed setup values |
| IgnoredArrayDeclarations | IReadOnlyList<SetupFeedDeclaration> | Array declarations ignored because a keyed declaration with the same name wins |

**Validation rules**:
- `Name` must be non-empty after shape-specific name resolution.
- Keyed declarations derive `Name` from `Key`.
- Keyed declarations with an inner `Name` must match `Key` using Nuplane feed-name comparison rules.
- Array declarations use the existing inner `Name`.
- All-digit keys cannot be keyed feed names.

### Setup Feed Declaration Source

DI-registered source that exposes raw effective setup feed declarations to validators and setup translators.

| Field | Type | Description |
|-------|------|-------------|
| ConfigurationPath | string | Root setup or feeds section path used to read declarations |
| ReadResult | NuplaneFeedSetupReadResult | Effective declarations and diagnostics produced from raw configuration |

**Validation rules**:
- The source must read from raw `IConfiguration`, not from list-bound `NuplaneSetupOptions.Feeds`.
- The source must preserve keyed child names and configuration paths for validation diagnostics.
- Validators consume this source while remaining in the `IValidateOptions<T>` pipeline.

### Remote Feed Declaration

A setup feed declaration with `ServiceIndex` and no `DirectoryPath`.

| Field | Type | Description |
|-------|------|-------------|
| Name | string | Canonical feed name |
| ServiceIndex | string | Absolute NuGet service index URI |
| Credentials | string? | Credential reference |
| IncludeAll | bool | Whether all packages are included |
| IncludePatterns | IReadOnlyList<string> | Include patterns copied from setup configuration |

**Validation rules**:
- `ServiceIndex` must be an absolute URI.
- `DirectoryPath` must be absent or blank.
- Credential values are references and must not be emitted as secret material in logs.

### Directory Feed Declaration

A setup feed declaration with `DirectoryPath` and no `ServiceIndex`.

| Field | Type | Description |
|-------|------|-------------|
| Name | string | Canonical feed name |
| DirectoryPath | string | Local directory path for `.nupkg` files |
| IncludeAll | bool | Whether all packages are included |
| IncludePatterns | IReadOnlyList<string> | Include patterns copied from setup configuration |
| Watch | bool | Directory watcher setting |
| DebounceWindow | TimeSpan | Directory watcher debounce window |

**Validation rules**:
- `DirectoryPath` must be non-empty.
- `ServiceIndex` must be absent or blank.
- `DebounceWindow` must be greater than zero, preserving current validation behavior.

### Feed Setup Diagnostic

Structured diagnostic emitted or returned when setup feed declarations are ambiguous or invalid.

| Field | Type | Description |
|-------|------|-------------|
| Severity | enum | `Warning` or `Error` |
| Code | string | Stable diagnostic code for tests and logs |
| Message | string | Human-readable diagnostic |
| ConfigurationPath | string | Path to the relevant feed declaration |
| FeedName | string? | Feed name when one can be resolved |

**Diagnostic cases**:
- Key/name mismatch: error.
- Empty or whitespace feed name: error.
- All-digit feed child key: array classification.
- Both `ServiceIndex` and `DirectoryPath`: error.
- Neither `ServiceIndex` nor `DirectoryPath`: error.
- Invalid `ServiceIndex`: error.
- Blank `DirectoryPath`: error.
- Same-name array and keyed declarations: warning; keyed wins.

## Relationships

```text
Nuplane:Setup:Feeds
    |
    v
NuplaneFeedSetupDeclarationReader
    |
    v
INuplaneSetupFeedDeclarationSource
    |
    +-- array child ("0") -> Setup Feed Declaration (SourceShape=Array, Name from inner Name)
    |
    +-- keyed child ("feedz.io") -> Setup Feed Declaration (SourceShape=Keyed, Name from key)
    |
    v
Effective declarations keyed by feed name
    |
    +-- Remote declarations -> NuplaneFeedSetupConfiguration -> builder.AddFeed(...)
    |
    +-- Directory declarations -> NuplaneDirectoryFeedSetupConfiguration -> builder.AddDirectoryFeed(...)
    |
    v
FeedResolutionOptions / desired sources / directory source registration
```

## State Transitions

1. Raw configuration children are classified as array or keyed declarations.
2. Shape-specific name rules produce a canonical feed name.
3. Declarations are grouped case-insensitively by canonical feed name.
4. If a group contains keyed declarations, the effective keyed declaration wins over array declarations.
5. Ignored array declarations are recorded for warning diagnostics.
6. Effective declarations are validated.
7. Valid declarations are translated into the existing builder registration surfaces.
