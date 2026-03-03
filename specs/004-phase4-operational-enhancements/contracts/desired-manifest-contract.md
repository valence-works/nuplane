# Contract: Desired Manifest Input

## Interface Boundary

- Input: a desired manifest artifact (e.g., JSON) describing desired packages.
- Output: a deterministic set of `PackageRequest` items (exact versions) and a manifest read/parse outcome.

## Behavioral Contract

- Identical manifest content MUST yield identical desired package requests.
- Manifest packages MUST use exact versions.
- Duplicate package IDs within a manifest are invalid.

## Error Contract

- Unreadable or invalid manifest MUST result in a degraded, non-mutating outcome.
- Errors MUST be correlation-linked and include an explicit reason code.

## Test Contract

- Verify deterministic parsing/projection for identical manifest content.
- Verify invalid JSON/schema produces degraded non-mutating cycle behavior.
