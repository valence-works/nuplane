# Contract: Deterministic Canary Selection

## Interface Boundary
- Canary evaluator input:
  - `rolloutId`
  - sorted eligible node identities
  - target percentage
  - optional deterministic salt
  - correlation ID
- Canary evaluator output:
  - selected node set
  - selected count
  - deterministic selection checksum/fingerprint
  - progression status/outcome code

## Behavioral Contract
- Selection must be deterministic and stable for identical canonical input values.
- Selected nodes must always be a subset of eligible nodes.
- Percentage increases expand selection deterministically without affecting out-of-scope nodes.
- Non-eligible nodes must never receive canary-targeted activation.

## Error Contract
- Empty eligible node set for percentage rollout emits `canary.invalid_eligible_set`.
- Invalid percentage values emit `canary.invalid_percentage`.
- Canonicalization failures emit explicit deterministic-selection failure code.

## Test Contract
- Verify identical canonical inputs across repeated cycles produce identical selected node sets.
- Verify percentage step-up expands selection deterministically.
- Verify non-eligible nodes never appear in selected result.
- Verify invalid input paths emit explicit failure codes and non-mutating outcomes.
