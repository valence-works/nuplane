# Contract: Store State and Activation Boundary

## Store Layout Contract
```text
root/
  state.json
  packages/{id}/{version}/...
  current/{id} -> ../packages/{id}/{version}
  staging/
```

## Activation Transaction Contract
1. Download/extract to `staging/`.
2. Validate identity/version/integrity policy.
3. Publish immutable package content to `packages/{id}/{version}`.
4. Atomically switch `current/{id}` pointer.
5. Persist `state.json` with active + LKG + failure metadata.

## Safety Contract
- If any step fails, active pointer MUST remain on previous LKG version.
- Store MUST never expose partially published active content.
- Re-running same transaction input MUST be idempotent and non-corrupting.

## `state.json` Minimum Contract
- `activeVersionById`
- `lastKnownGoodById`
- `lastFailureById` with stage/message/timestamp/correlationId
- `lastSuccessfulSourceSnapshots`

## Test Contract
- Must verify atomic switch behavior under injected failures.
- Must verify persisted state survival across process restart.
- Must verify no pointer change on validation/publish failures.
