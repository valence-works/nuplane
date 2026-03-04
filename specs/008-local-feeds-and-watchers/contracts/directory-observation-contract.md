# Contract: Directory Observation (Watchers)

## Interface Boundary

- A directory observation driver watches one configured local directory feed path and emits “directory changed” signals.
- The driver does not apply state directly; it triggers reconciliation through the existing reconciliation service boundary.

## Behavioral Contract

- Watcher events (create/change/delete/rename) MUST be coalesced:
  - repeated or bursty notifications MUST not cause unbounded reconciliation invocations
  - effective behavior is “at most one trigger per debounce window”
- Partial-write safety MUST be deterministic:
  - a `.nupkg` that is still being written must not be treated as a valid artifact
  - the system must retry safely with bounded backoff and produce diagnostics when stability cannot be achieved

## Degraded/Fallback Contract

- If watcher establishment fails (permissions, invalid path, OS limitations):
  - scheduled reconciliation remains active (convergence still occurs)
  - an explicit degraded signal is emitted (health + operator snapshot + logs)
    - the operator snapshot MUST include a degraded reason via `source-outages:N` (N>0) for cycle(s) where observation is degraded
  - the system must not silently stop reconciling

## Observability Contract

- Directory observation MUST emit:
  - a startup log indicating watcher enabled/disabled and effective debounce window
  - degraded-state logs with the local directory feed name and last error
  - trigger attribution for directory-based reconciliation cycles

## Test Contract

- Must cover:
  - debounce/coalescing behavior under multiple event sequences
  - partial-write handling (bounded retries; deterministic outcome)
  - degraded watcher behavior does not prevent scheduled reconciliation
