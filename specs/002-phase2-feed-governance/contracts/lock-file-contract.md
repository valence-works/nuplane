# Contract: Lock File Modes and Integrity

## Lock File Schema Contract
Schema `2.0` is the current enforceable schema.

Minimum entry fields:
- `id`
- `version`
- `feed`
- `hash`
- `timestamp`

## Mode Contract
- `generate`: produce lock file from resolved package set for successful cycle.
- `enforce`: use lock entries as authoritative package version/feed inputs.
- `strict`: fail package when required lock entry is missing.

## Integrity Contract
- `hash` MUST use `sha512:<standard-padded-base64>` and identify every byte of the exact acquired `.nupkg` archive.
- Version, feed, and hash constraints MUST be applied before package acquisition returns to graph construction.
- Strict mode MUST cover every acquired root and dependency; host-provided dependencies are excluded because Nuplane does not acquire them.
- Activation MUST fail when downloaded artifact hash does not match lock entry hash.
- Hash mismatch MUST NOT switch active pointer away from LKG.
- Schema `1.0` and malformed or missing schema `2.0` hashes MUST NOT be silently trusted by enforce or strict mode.

## Dry-Run Contract
- Dry-run executes lock checks exactly as apply mode.
- Dry-run reports all lock outcomes but performs no state mutation.

## Error Contract
- Missing lock entries and hash mismatches produce explicit, stage-classified diagnostics with correlation IDs.

## Test Contract
- Must verify enforce mode ignores live version drift.
- Must verify strict mode fails missing lock entries.
- Must verify hash mismatch blocks activation and preserves active state.
- Must verify dry-run lock outcomes match apply outcomes for the same inputs.
