# Contract: Cleanup Policy and Retention Safety

## Interface Boundary
- Cleanup component receives successful-cycle context and retention policy.
- Cleanup component emits per-version keep/delete outcomes.

## Behavioral Contract
- Automatic cleanup runs only after successful reconciliation cycles.
- Manual-only mode disables automatic deletion.
- Retention supports:
  - keep last N versions
  - keep versions younger than N days
- When both retention rules are configured, keep semantics are UNION (`count OR age`).
- Last-known-good versions are always protected from deletion.

## Safety Contract
- Cleanup failures MUST NOT change active pointers or invalidate runtime state.
- Cleanup outcomes must be observable with correlation ID and reasons.

## Error Contract
- Deletion failures are recorded as maintenance diagnostics.
- Cleanup component failures do not crash host process.

## Test Contract
- Must verify union retention behavior with both policies set.
- Must verify LKG versions are never deleted.
- Must verify manual-only mode performs no automatic deletion.
- Must verify deletion failure isolation and diagnostics emission.
