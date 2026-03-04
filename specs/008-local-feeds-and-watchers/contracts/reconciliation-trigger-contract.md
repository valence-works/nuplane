# Contract: Reconciliation Trigger Attribution

## Interface Boundary

- Reconciliation can be invoked by multiple drivers:
  - scheduled polling (`ReconciliationHostedService`)
  - directory change observation (watcher-based hosted service)
  - manual triggers (existing operational surface)
  - startup (host initialization / first cycle)

## Behavioral Contract

- Every reconciliation cycle MUST be attributable to a trigger type:
  - `Scheduled` for periodic ticks
  - `DirectoryChange` when initiated by local directory watcher
  - `Manual` when initiated by an operator/operational surface
  - `Startup` for the first automatic cycle (if applicable)
- “Single-flight skipped” cycles MUST still record the attempted trigger and correlation context.

## Observability Contract

- Logs MUST include:
  - `CorrelationId`
  - `TriggerType`
  - optional `TriggerSource` (e.g., feed name / directory path)
- Metrics MUST include:
  - trigger counts by type
  - cycle duration distribution
  - failure counts by stage/reason

## Test Contract

- Must cover:
  - trigger type propagation to logs/observer events
  - directory change triggers do not create unbounded concurrency
  - scheduled triggers still fire while watcher is degraded
