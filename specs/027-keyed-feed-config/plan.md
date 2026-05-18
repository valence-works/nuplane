# Implementation Plan: Key-Based Feed Setup Configuration

**Branch**: `027-keyed-feed-config` | **Date**: 2026-05-18 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/027-keyed-feed-config/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

Nuplane setup configuration currently reads `Nuplane:Setup:Feeds` as an array, which is fragile under layered .NET configuration providers because feeds merge by numeric index. This feature adds a shared setup-feed reader that classifies numeric children as legacy array entries and non-numeric children as keyed feed declarations, derives keyed feed names from the configuration key, validates conflicts through the existing options validation pipeline with access to raw `IConfiguration` paths, and lets the remote-feed and directory-feed setup translators consume the same effective declaration set. Existing array configuration remains valid; same-name mixed array/keyed declarations register the keyed declaration and emit a warning.

## Technical Context

**Language/Version**: C# / .NET 8.0, 9.0, 10.0 source libraries; tests target .NET 10.0  
**Primary Dependencies**: Microsoft.Extensions.Configuration, Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Options, Microsoft.Extensions.Logging; existing Nuplane builder and feed registration APIs  
**Storage**: N/A for this feature; configuration is translated into runtime options and desired-state sources  
**Testing**: xUnit, Microsoft.Extensions.Configuration in-memory providers, Microsoft.Extensions.Options validation, existing service registration assertions  
**Target Platform**: Cross-platform .NET libraries consumed by host applications  
**Project Type**: Library/runtime infrastructure  
**Performance Goals**: Setup feed parsing is startup-time work over the effective feed section and should be linear in feed declaration count; no reconciliation-cycle overhead after registration  
**Constraints**: Preserve array-based compatibility; do not rely on JSON object order; keep directory-specific registration in `Nuplane.Sources.Directory`; keep validation in `IValidateOptions<T>` and startup fail-fast paths; do not log credential secret values  
**Scale/Scope**: Typically 0-20 configured feeds per host, with layered providers such as `appsettings.json`, environment variables, and mounted configuration files

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **Deterministic reconciliation**: PASS - This is a startup configuration translation change. Effective feed declarations are de-duplicated deterministically by feed name, mixed array/keyed conflicts choose the keyed declaration, and candidate feed ordering remains governed by existing feed priority then feed name.
- **Transactional store safety**: PASS - No store mutation behavior changes. Invalid feed setup fails during configuration validation/registration before reconciliation applies package changes, preserving existing LKG behavior.
- **Source integrity**: PASS - Feed source validation remains explicit: each feed must specify exactly one source type, remote service indexes must be absolute URIs, directory paths must be non-blank, and credential values are treated as references without secret logging.
- **Observability**: PASS - Mixed array/keyed same-name declarations emit a structured warning. Validation diagnostics include paths and identities for mismatches, duplicate/conflicting source types, invalid URIs, and invalid directory paths without credential secrets.
- **Test discipline**: PASS - Plan includes unit/registration tests for keyed remote feeds, keyed directory feeds, array compatibility, key/name mismatch, numeric-key classification, layered overrides, mixed declarations, include patterns, directory options, and deterministic ordering.
- **Decomposition discipline**: PASS - The shared feed setup reader, validation integration, remote translator, directory translator, tests, and docs are separate artifacts. Configuration properties already exist and continue to have concrete consumers.
- **Options validation discipline**: PASS - Existing `NuplaneSetupOptionsValidator` and `ValidateOnStart()` remain the validation path. The options class stays data-only; a DI-registered raw setup feed declaration source gives validation access to keyed feed paths that standard list binding cannot preserve.

## Project Structure

### Documentation (this feature)

```text
specs/027-keyed-feed-config/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── feed-setup-reader-contract.md
│   └── feed-setup-validation-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane/
│   ├── Feeds/
│   │   └── Setup/
│   │       ├── NuplaneFeedSetupConfiguration.cs        # update: consume effective remote declarations
│   │       ├── NuplaneFeedSetupDeclaration.cs          # new: effective feed declaration model
│   │       ├── NuplaneFeedSetupDeclarationReader.cs    # new: classify array/keyed children and merge declarations
│   │       └── NuplaneFeedSetupOptions.cs              # keep existing array entry shape
│   ├── Observability/
│   │   └── ReconciliationLogger.cs                     # extend or add source-generated warning for mixed setup declarations if reused here
│   ├── Registration/
│   │   ├── NuplaneOptionsRegistrationServices.cs       # bind non-feed setup options and feed declarations without losing keyed shape
│   │   └── NuplaneSetupConfigurationServices.cs        # consume setup feed reader output
│   └── Setup/
│       ├── INuplaneSetupFeedDeclarationSource.cs       # new: exposes raw effective feed declarations to validators/translators
│       ├── ConfigurationNuplaneSetupFeedDeclarationSource.cs # new: reads raw IConfiguration once through shared reader
│       ├── NuplaneSetupOptions.cs                      # keep data-only; retain `Feeds` for legacy/bound effective declarations
│       └── NuplaneSetupOptionsValidator.cs             # validate effective feed declarations and names
├── Nuplane.Sources.Directory/
│   └── Configuration/
│       └── NuplaneDirectoryFeedSetupConfiguration.cs   # update: consume effective directory declarations
└── Nuplane.Runtime/
    └── Feeds/
        └── Policy/
            └── FeedResolutionPolicy.cs                 # no behavior change; ordering contract referenced by tests

test/
├── Nuplane.Runtime.Tests/
│   └── Configuration/
│       ├── ConfigurationDrivenRegistrationTests.cs      # extend: keyed remote, array compatibility, layered override, mixed declarations
│       └── NuplaneSetupOptionsValidatorTests.cs         # extend: key/name mismatch and source-type validation
└── Nuplane.Sources.Directory.Tests/
    └── Configuration/
        └── DirectoryFeedSetupConfigurationTests.cs      # new or extend: keyed directory feeds and directory options

docs/
├── wiki/                                               # update relevant setup/configuration pages when present
└── posts/introducing-nuplane.md                        # update examples if still current

README.md                                              # prefer keyed setup examples and migration note
samples/Nuplane.Sample.AspNetCore/appsettings.json      # update sample config if it uses setup feeds
```

**Structure Decision**: Keep setup feed declaration parsing in the `Nuplane` package because both the core remote-feed translator and `Nuplane.Sources.Directory` already depend on `Nuplane.Feeds.Setup` types. Directory-backed registration remains in `Nuplane.Sources.Directory`, preserving module ownership. The shared reader returns effective declarations so both translators apply identical name, shape, and mixed-format rules.

## Phase 0 Research Summary

See [research.md](research.md). Key decisions:

- Classify all-digit feed children as legacy array entries; non-numeric children are keyed feed entries.
- Derive keyed feed names from the configuration key; optional inner `Name` must match.
- For same-name mixed array/keyed declarations, keyed wins and the array declaration is ignored with a warning.
- Continue using existing `FeedPriorities` for resolution order; do not add per-feed order metadata.
- Preserve `NuplaneSetupOptions` as a data-only options type and implement keyed feed support through a shared reader plus a DI-registered raw declaration source consumed by validation and translators.

## Phase 1 Design Summary

See [data-model.md](data-model.md), [contracts/feed-setup-reader-contract.md](contracts/feed-setup-reader-contract.md), [contracts/feed-setup-validation-contract.md](contracts/feed-setup-validation-contract.md), and [quickstart.md](quickstart.md).

## Post-Design Constitution Re-evaluation

All gates remain PASS after Phase 1 design.

- **Deterministic reconciliation**: Effective declarations are deterministic, de-duplicated by case-insensitive feed name, and resolution ordering remains priority-based.
- **Transactional store safety**: The design only affects startup/configuration registration; invalid declarations fail before reconciliation runs.
- **Source integrity**: Validation preserves exactly-one-source-type checks and absolute URI/directory path constraints.
- **Observability**: Warning/error contracts define precise diagnostics without secret values.
- **Test discipline**: Contracts and quickstart define targeted unit and boundary tests for the changed behavior.
- **Decomposition discipline**: New artifacts map to one concern each: read/classify, validate, translate remote, translate directory, document.
- **Options validation discipline**: Validation remains in `IValidateOptions<NuplaneSetupOptions>` with `ValidateOnStart()`, and raw keyed declaration context is supplied by DI rather than by adding validation methods to options classes.

## Complexity Tracking

No constitution violations to justify. All gates pass.
