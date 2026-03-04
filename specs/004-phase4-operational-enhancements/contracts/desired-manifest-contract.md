# Contract: Desired Manifest Input

## Interface Boundary

- Input: shared desired manifest artifact (JSON) describing exact package requests.
- Producer: any trusted desired-state publisher (operator pipeline).
- Consumer: Phase 4 manifest reader/source implementation in runtime.
- Output:
	- deterministic projected `PackageRequest` set
	- `DesiredManifestReadResult` status/reason metadata

## Behavioral Contract

- Identical manifest content MUST yield identical desired package requests.
- Manifest packages MUST use exact versions.
- Duplicate package IDs within a manifest are invalid.
- Manifest projection ordering MUST be stable for identical content.

## Error Contract

- Unreadable or invalid manifest MUST result in a degraded, non-mutating outcome.
- Errors MUST be correlation-linked and include an explicit reason code.
- Failure MUST emit observer failure event with scoped target (`manifest`) and reason code.

## Test Contract

- Verify deterministic parsing/projection for identical manifest content.
- Verify invalid JSON/schema produces degraded non-mutating cycle behavior.
- Verify duplicate package IDs are rejected with explicit reason code.
