# Contract: Feed Setup Validation And Diagnostics

## Validator

```csharp
internal sealed class NuplaneSetupOptionsValidator : IValidateOptions<NuplaneSetupOptions>
```

The existing validator remains the startup fail-fast validation surface. Keyed feed support may use a helper that validates `NuplaneFeedSetupDeclaration` instances, but validation policy must remain reachable through the options validation pipeline.

## Raw Configuration Contract

- `NuplaneSetupOptions` MUST remain data-only.
- A DI-registered declaration source MUST read raw `IConfiguration` with `NuplaneFeedSetupDeclarationReader`.
- `NuplaneSetupOptionsValidator` MUST consume that declaration source so keyed child names, mixed array/keyed declarations, and configuration paths are available during `ValidateOnStart()`.
- Validation MUST NOT rely solely on `NuplaneSetupOptions.Feeds` list binding for keyed feed rules because non-numeric feed keys are not represented by array binding.

## Validation Contract

- Setup validation MUST reject array entries with missing or whitespace `Name`.
- Setup validation MUST reject keyed entries whose inner `Name` is present and does not match the containing key.
- Setup validation MUST reject duplicate keyed declarations for the same feed name if the effective configuration can expose them.
- Setup validation MUST classify all-digit `Feeds` child keys as array entries before keyed-feed validation; there is no separate all-digit keyed-feed validation path.
- Setup validation MUST reject entries with both `ServiceIndex` and `DirectoryPath`.
- Setup validation MUST reject entries with neither `ServiceIndex` nor `DirectoryPath`.
- Setup validation MUST reject remote feed `ServiceIndex` values that are not valid absolute URIs.
- Setup validation MUST reject directory feed `DirectoryPath` values that are empty or whitespace.
- Setup validation MUST preserve the existing rule that `Directory.DebounceWindow` must be greater than zero.
- Setup validation MUST compare feed names case-insensitively.
- Same-name mixed array/keyed declarations MUST be warning diagnostics, not validation failures.

## Warning Diagnostic Contract

When an array declaration and keyed declaration resolve to the same feed name:

- The keyed declaration MUST be registered.
- The array declaration MUST be ignored for that feed.
- A warning MUST identify:
  - the canonical feed name
  - the keyed declaration path
  - the ignored array declaration path or index
- The warning MUST NOT contain credential secret values.

## Registration Contract

- Remote setup translation MUST register only effective remote declarations.
- Directory setup translation MUST register only effective directory declarations.
- A declaration with `DirectoryPath` MUST be ignored by the remote translator and handled by `Nuplane.Sources.Directory`.
- A declaration with `ServiceIndex` MUST be ignored by the directory translator and handled by the remote translator.
- The two translators MUST consume the same effective declaration rules to avoid duplicate or divergent registrations.

## Test Contract

- Validator fails on key/name mismatch.
- Validator fails on both source types.
- Validator fails on missing source type.
- Validator fails on invalid remote URI.
- Validator fails on blank directory path.
- Validator preserves existing array duplicate behavior.
- Mixed array/keyed same-name declarations produce a warning and one effective registration.
- Layered configuration override by keyed feed path produces one effective registration with the later value.
- Warning diagnostics redact credential values.
