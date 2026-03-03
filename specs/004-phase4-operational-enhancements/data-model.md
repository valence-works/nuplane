# Data Model — Phase 4 Operational Enhancements

## Entity: ChannelPolicy
- Purpose: Defines reconciliation/activation boundary for one channel.
- Fields:
  - `channelName` (enum/string, required; `prod|staging|canary` minimum set)
  - `desiredSourceRefs` (list, required)
  - `enabled` (bool, required)
  - `priority` (int, optional)
  - `configuredAt` (datetime, required)
- Validation rules:
  - `channelName` must be unique per policy set.
  - At least one desired source is required for configured channels.
  - Missing/empty desired source list yields non-mutating degraded cycle outcome.

## Entity: StagedReleaseCandidate
- Purpose: Represents a resolved package version prepared for later activation.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `channelName` (string, required)
  - `candidateState` (enum: `Staged`, `PromotionRequested`, `Promoted`, `PromotionFailed`, `Superseded`)
  - `stagedAt` (datetime, required)
  - `promotionRequestedAt` (datetime, optional)
  - `promotionOutcomeCode` (string, optional)
  - `correlationId` (string, required)
- Validation rules:
  - Candidate may transition to `Promoted` only after explicit operator promotion request.
  - `PromotionFailed` must preserve existing active version and LKG.

## Entity: PromotionRequest
- Purpose: Operator action artifact that authorizes staged candidate promotion.
- Fields:
  - `requestId` (string, required)
  - `channelName` (string, required)
  - `packageId` (string, required)
  - `requestedBy` (string, required)
  - `requestedAt` (datetime, required)
  - `status` (enum: `Accepted`, `Rejected`, `Completed`, `Failed`)
  - `reason` (string, optional)
- Validation rules:
  - Request must reference an existing `StagedReleaseCandidate` in `Staged` state.
  - Duplicate active request for same package/channel is not permitted.

## Entity: CanaryRolloutPlan
- Purpose: Defines controlled rollout boundaries for canary activation.
- Fields:
  - `rolloutId` (string, required)
  - `channelName` (string, required)
  - `eligibleNodeIds` (set<string>, required)
  - `targetPercentage` (decimal, required, range 0-100)
  - `currentState` (enum: `Planned`, `InProgress`, `Completed`, `Paused`, `Failed`)
  - `updatedAt` (datetime, required)
  - `correlationId` (string, required)
- Validation rules:
  - `eligibleNodeIds` cannot be empty for percentage-based rollout.
  - `targetPercentage` changes are monotonic-increase within one active rollout unless a new rollout is created.

## Entity: CanarySelectionInput
- Purpose: Canonical deterministic input used to compute selected canary nodes.
- Fields:
  - `rolloutId` (string, required)
  - `eligibleNodeIdsSorted` (ordered list<string>, required)
  - `targetPercentage` (decimal, required)
  - `selectionSalt` (string, optional)
  - `computedAt` (datetime, required)
- Validation rules:
  - Inputs must be canonicalized deterministically before selection.
  - Identical input values must produce identical selected node set.

## Entity: CanarySelectionResult
- Purpose: Captures the deterministic node selection result for one cycle/rollout.
- Fields:
  - `rolloutId` (string, required)
  - `selectedNodeIds` (set<string>, required)
  - `selectedCount` (int, required)
  - `targetPercentage` (decimal, required)
  - `outcomeCode` (string, required)
  - `correlationId` (string, required)
- Validation rules:
  - `selectedNodeIds` must be subset of `eligibleNodeIds`.
  - `selectedCount` must equal deterministic percentage projection for input set.

## Entity: IntegrityRuleSet
- Purpose: Defines trust and integrity checks required before activation.
- Fields:
  - `ruleSetId` (string, required)
  - `requireTrustedFeed` (bool, required)
  - `requireHashValidation` (bool, required)
  - `requireSignatureValidation` (bool, required)
  - `publisherAllowlistRef` (string, optional)
  - `enforcementMode` (enum: `Enforce`, `Audit`)
- Validation rules:
  - `enforcementMode=Enforce` blocks activation on failed required check.
  - Rule set changes apply at next reconciliation cycle.

## Entity: IntegrityEvaluationRecord
- Purpose: Records pre-activation integrity outcome for one package candidate.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `channelName` (string, required)
  - `ruleSetId` (string, required)
  - `status` (enum: `Passed`, `Failed`)
  - `failureReasonCode` (string, optional)
  - `evaluatedAt` (datetime, required)
  - `correlationId` (string, required)
- Validation rules:
  - `Failed` status must prevent activation and preserve active/LKG pointers.

## Entity: OperationalSnapshot
- Purpose: Operator-facing point-in-time state projection.
- Fields:
  - `snapshotAt` (datetime, required)
  - `channelName` (string, required)
  - `activePackages` (list, required)
  - `stagedCandidates` (list, required)
  - `canaryStatus` (object, required)
  - `lastReconcileOutcome` (object, required)
  - `healthState` (enum: `Healthy`, `Degraded`)
  - `correlationId` (string, required)
- Validation rules:
  - Snapshot must be internally consistent for one correlation scope.
  - Missing/empty channel configuration reflects `Degraded` health with explicit reason.

## Relationships
- One `ChannelPolicy` has many `StagedReleaseCandidate` records.
- One `StagedReleaseCandidate` may have many `PromotionRequest` attempts.
- One `CanaryRolloutPlan` has one or more `CanarySelectionResult` records over time.
- One `CanarySelectionInput` produces one deterministic `CanarySelectionResult` per evaluation.
- One `IntegrityRuleSet` governs many `IntegrityEvaluationRecord` entries.
- One `OperationalSnapshot` aggregates channel, candidate, canary, and integrity outcomes.

## State Transitions

### Staged release lifecycle
1. `Staged`
2. `PromotionRequested` (explicit operator action)
3. `Promoted` OR `PromotionFailed`
4. `Superseded` when replaced by newer staged candidate

### Canary rollout lifecycle
1. `Planned`
2. `InProgress`
3. `Completed` OR `Paused` OR `Failed`

### Integrity gating lifecycle
1. `EvaluationStarted`
2. `Passed` OR `Failed`
3. On `Failed` -> activation blocked, non-mutating outcome recorded
