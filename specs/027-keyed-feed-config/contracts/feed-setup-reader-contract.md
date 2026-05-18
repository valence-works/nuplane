# Contract: Feed Setup Declaration Reader

## Interface

```csharp
public static class NuplaneFeedSetupDeclarationReader
{
    public static IReadOnlyList<NuplaneFeedSetupDeclaration> Read(IConfiguration setupOrFeedsSection);
}
```

**Location**: `src/Nuplane/Feeds/Setup/`  
**Consumers**:
- `NuplaneFeedSetupConfiguration` for remote feeds
- `NuplaneDirectoryFeedSetupConfiguration` for directory feeds
- `NuplaneSetupOptionsValidator` or its validation adapter for setup feed validation
- `INuplaneSetupFeedDeclarationSource` for preserving raw keyed declarations across DI validation and translation

## Behavioral Contract

- The reader MUST accept either the `Nuplane:Setup` section or the `Nuplane:Setup:Feeds` section.
- The reader MUST inspect direct children of `Feeds`.
- Child keys made only of digits MUST be treated as array entries.
- Child keys not made only of digits MUST be treated as keyed entries.
- Array entries MUST use the existing inner `Name` property as the canonical feed name.
- Keyed entries MUST use the child key as the canonical feed name.
- Keyed entries MAY include an inner `Name` only when it matches the child key using Nuplane feed-name comparison rules.
- Effective declarations MUST be grouped by feed name using case-insensitive comparison.
- If a group contains one keyed declaration and one or more array declarations, the keyed declaration MUST be returned as the effective declaration and the ignored array declarations MUST be available for warning diagnostics.
- The reader MUST preserve existing feed property values: `ServiceIndex`, `DirectoryPath`, `IncludePatterns`, `IncludeAll`, `Credentials`, and `Directory`.
- The reader MUST NOT emit or log credential secret values.
- The reader MUST be usable from a DI-registered declaration source so validation can see keyed child paths that `NuplaneSetupOptions.Feeds` list binding cannot represent.

## Ordering Contract

- The reader MUST NOT rely on JSON object order for semantic feed resolution behavior.
- Returned declarations MAY be sorted deterministically by feed name for stable tests and diagnostics.
- Feed resolution order remains the responsibility of `FeedResolutionPolicy` using existing feed priority configuration and name-based tie-breaking.

## Error Contract

- The reader MAY collect invalid declarations for validation rather than throwing immediately.
- Validation failures MUST identify the configuration path and feed name when available.
- A key/name mismatch MUST be reported as an error by validation.
- Same-name mixed array/keyed declarations MUST NOT throw; they produce an effective keyed declaration plus a warning diagnostic.

## Test Contract

- Reads keyed remote feed without inner `Name`.
- Reads keyed directory feed without inner `Name`.
- Reads keyed feed with matching inner `Name`.
- Reports keyed feed with mismatched inner `Name`.
- Preserves legacy array feed behavior.
- Treats all-digit child keys as array entries.
- Treats non-numeric child keys as keyed entries.
- Handles same-name array/keyed declarations by returning only keyed as effective.
- Preserves `IncludePatterns`, `IncludeAll`, `Credentials`, and `Directory` values.
- Produces deterministic output for the same effective configuration.
