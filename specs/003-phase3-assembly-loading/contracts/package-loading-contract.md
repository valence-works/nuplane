# Contract: Package Loading Boundary

## Interface Boundary
- Runtime passes active package set to loading boundary with:
  - package ID and version
  - active install path from deterministic store
  - current correlation ID
  - shared assembly policy snapshot
- Loading boundary returns per-package load outcomes:
  - `Loaded` / `LoadFailed`
  - deterministic decision metadata
  - diagnostic reason code when failed

## Behavioral Contract
- Loading is optional and feature-flag/config gated.
- Each active package receives an isolated load context/session.
- Load behavior for unchanged package inputs is idempotent across repeated cycles.
- Failure for one package MUST NOT block load processing for other packages.

## Error Contract
- Load failures are classified as `load`-stage diagnostics with package identity and correlation ID.
- Loading failures MUST NOT mutate store active-pointer or LKG state.
- Missing/invalid active path yields explicit failure outcome, never silent skip.

## Test Contract
- Must verify one active package creates one load session.
- Must verify repeated identical cycles do not duplicate sessions.
- Must verify package-local load failure does not block unrelated package loads.
- Must verify load failures are observable with correlation-linked diagnostics.
