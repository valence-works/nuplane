# Contract: Feed Trust and Override Policy

## Interface Boundary
- Runtime trust policy component receives candidate package + selected feed metadata.
- Trust policy emits allow/block outcome with detailed rationale.

## Behavioral Contract
- `Trusted`: package may proceed without additional validator requirements.
- `Restricted`: package must pass configured validator pipeline before eligibility.
- `Untrusted`: package is blocked unless explicit scoped override exists.
- Scoped override types:
  - per-package
  - per-feed-rule
- Any untrusted override MUST include operator-provided reason text.

## Audit & Observability Contract
- Every policy decision emits structured diagnostic fields:
  - feed trust level
  - policy outcome
  - validator result summary (if applicable)
  - override scope and reason (if used)
  - correlation ID

## Error Contract
- Policy failures are deterministic and non-mutating.
- Policy component failures are reported as reconciliation diagnostics and do not bypass trust enforcement.

## Test Contract
- Must verify restricted package rejection when validators fail.
- Must verify untrusted package rejection without override.
- Must verify untrusted package allow with scoped override + reason.
- Must verify override reason appears in diagnostics.
