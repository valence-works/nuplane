# Contract: Lock File Modes and Integrity

## Lock File Schema Contract
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
- Activation MUST fail when downloaded artifact hash does not match lock entry hash.
- Hash mismatch MUST NOT switch active pointer away from LKG.

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
