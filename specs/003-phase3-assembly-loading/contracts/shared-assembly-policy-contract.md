# Contract: Shared Assembly Policy

## Interface Boundary
- Host config provides shared assembly entries containing:
  - `name`
  - `publicKeyToken`
  - `majorVersion`
- Loader evaluates shared policy before package-local resolver.

## Behavioral Contract
- Match key for shared assemblies is strong identity: `name + publicKeyToken + majorVersion`.
- If a request matches policy, assembly resolves from shared host context.
- If no match exists, loader resolves using package-local dependency resolver.
- Matching behavior is deterministic for repeated identical inputs.

## Error Contract
- Ambiguous or invalid policy entries produce explicit policy diagnostics.
- Failed shared-policy resolution does not bypass into permissive name-only matching.
- Contract mismatch outcomes are recorded with package, assembly identity, and correlation ID.

## Test Contract
- Must verify valid strong-identity match reuses shared host assembly.
- Must verify mismatched token or major version does not match shared policy.
- Must verify no shared-policy entry falls back to package-local resolution.
- Must verify deterministic outcomes across repeated cycles.
