# Contract: Optional Loader Boundary

## Interface Boundary

- Input: the active package set (package identity + active install path), loader options/policy, correlation ID.
- Output: per-package loader outcome (`Loaded`, `Failed`, `Skipped`) with reason codes.

## Behavioral Contract

- Loader integration MUST be optional and default-disabled.
- When enabled, loader actions MUST be isolated per package.
- Loader failures MUST NOT crash the host and MUST NOT corrupt the store.

## Error Contract

- Loader failures MUST emit correlation-linked diagnostics and a failure observer event with scoped target.

## Test Contract

- Verify known type is loadable from an activated package when loader is enabled.
- Verify injected loader failure is isolated and observable.
