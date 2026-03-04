# Contract: Optional Loader Boundary

## Interface Boundary

- Input: the active package set (package identity + active install path), loader options/policy, correlation ID.
- Output: per-package loader outcome (`Loaded`, `Failed`, `Skipped`) with reason codes.
- Adapter path: runtime boundary delegates to optional loading module when enabled.

## Behavioral Contract

- Loader integration MUST be optional and default-disabled.
- When enabled, loader actions MUST be isolated per package.
- Loader failures MUST NOT crash the host and MUST NOT corrupt the store.
- Loader failure MUST NOT roll back successful package activation unless host policy explicitly requires it.

## Error Contract

- Loader failures MUST emit correlation-linked diagnostics and a failure observer event with scoped target.
- Failure reason codes MUST identify package and loader stage.

## Test Contract

- Verify known type is loadable from an activated package when loader is enabled.
- Verify injected loader failure is isolated and observable.
- Verify loader-disabled mode emits deterministic `Skipped` outcomes without side effects.
