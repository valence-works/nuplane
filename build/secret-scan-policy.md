# Secret Handling Verification Gate

## Policy
- Credentials and secrets MUST NOT be committed to source control.
- Feed credentials MUST be provided by runtime configuration/secret providers.
- Repository artifacts MUST only contain credential references (for example, `credentialsRef`) and never raw secret values.

## Validation Rules
- CI MUST run a secret scanning step before merge.
- Pull requests MUST be blocked when secret scanner findings are high confidence and unresolved.
- False positives MUST be documented in scanner allowlist configuration with justification.

## Phase 3 Loading Guidance
- Shared assembly policy entries (`name`, `publicKeyToken`, `majorVersion`) are identity metadata, not credentials.
- Do not commit private keys, raw auth tokens, PATs, or inline feed passwords in loading or feed configuration examples.
- Continue using `secrets://...` references for credential material in sample/runtime configuration.

## Local Developer Check
- Run secret scan tooling before pushing changes.
- Verify `.env*` and secret-bearing local files remain ignored by `.gitignore`.
