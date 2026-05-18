# Research: Key-Based Feed Setup Configuration

**Feature**: 027-keyed-feed-config  
**Date**: 2026-05-18

## R-001: Feed Section Shape Detection

**Decision**: Classify each direct child of `Nuplane:Setup:Feeds` independently. All-digit child keys are legacy array entries. Non-numeric child keys are key-based feed entries.

**Rationale**: .NET configuration arrays are represented as numeric child keys. Classifying each child independently supports effective configurations that contain both legacy array entries and keyed entries after providers are layered. Reserving all-digit keys for arrays avoids heuristics and makes shape detection deterministic.

**Alternatives considered**:
- Treat the entire `Feeds` section as either array or object based on the first child. This fails for mixed effective configuration.
- Allow all-digit keyed feed names when the entry has no `Name`. This creates ambiguous parsing for valid array entries and makes tests depend on property presence.
- Add a marker property to distinguish numeric keyed feeds. This adds schema complexity for a narrow edge case.

## R-002: Canonical Feed Name For Keyed Entries

**Decision**: For key-based entries, the configuration key is the canonical feed name. Inner `Name` is optional but must match the key when supplied.

**Rationale**: The goal is layered override by feed identity. Letting an inner `Name` override the key would reintroduce ambiguity and make `Nuplane:Setup:Feeds:{feedName}` misleading. Allowing a matching inner `Name` supports gradual migration and copied legacy entries while still validating intent.

**Alternatives considered**:
- Let inner `Name` take precedence. Rejected because the path key would no longer be authoritative.
- Ignore inner `Name` silently. Rejected because mismatches likely indicate a bad migration or wrong provider path.
- Forbid inner `Name` in keyed entries. Rejected because it makes migration from array examples unnecessarily brittle.

## R-003: Mixed Array/Keyed Same-Name Declarations

**Decision**: If the effective configuration contains both an array entry and a keyed entry for the same feed name, register only the keyed entry and emit a warning diagnostic identifying the ignored array declaration.

**Rationale**: Keyed declarations are the preferred migration target and compose better under layered providers. Choosing keyed-wins prevents duplicate registrations while allowing operators to add keyed overrides without immediately deleting old array configuration from every source. A warning keeps the configuration visible and actionable.

**Alternatives considered**:
- Fail startup. Safer but less migration-friendly and would make staged config rollout harder.
- Keep both when source types differ. Rejected because a feed name is a logical identity, not a source-type tuple.
- Last provider wins by enumeration order. Rejected because provider ordering is already reflected in the effective keyed path and should not be inferred from child enumeration order.

## R-004: Feed Ordering And Priority

**Decision**: Do not add ordering metadata to setup feed entries. Continue using existing `FeedResolutionOptions.FeedPriorities` for feed resolution order. Missing or equal priority values tie-break by feed name.

**Rationale**: The codebase already has explicit feed priority configuration and `FeedResolutionPolicy` orders by priority then feed name. Reusing it avoids semantic dependence on JSON object order and keeps setup feed declarations focused on source configuration.

**Alternatives considered**:
- Add `Priority` to each setup feed entry. Rejected because it duplicates existing priority configuration and creates precedence questions.
- Use JSON object order. Rejected because object order is not a reliable semantic contract across providers.
- Sort only by key. Rejected because explicit priority already exists for cases where feed order matters.

## R-005: Options Binding And Validation Topology

**Decision**: Preserve `NuplaneSetupOptions` as a data-only options object and keep validation in `NuplaneSetupOptionsValidator`. Add a shared feed setup declaration reader for raw `IConfiguration` so keyed entries are not lost through list binding.

**Rationale**: The existing options binder maps `Feeds` to `List<NuplaneFeedSetupOptions>`, which cannot represent non-numeric keyed entries correctly. A raw configuration reader can classify and merge array/keyed declarations before translators and validators consume effective declarations. Keeping validation in `IValidateOptions<T>` preserves the repository constitution and existing startup fail-fast model.

**Alternatives considered**:
- Replace `Feeds` with a dictionary on `NuplaneSetupOptions`. Rejected because it would break existing array binding and force more migration.
- Maintain parallel `List` and `Dictionary` properties. Rejected because it increases drift risk and still relies on binder semantics.
- Duplicate parsing in remote and directory translators. Rejected because mixed-format behavior and diagnostics must stay consistent.
