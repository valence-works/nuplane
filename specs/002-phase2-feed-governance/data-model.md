# Data Model — Phase 2 Advanced Feeds & Governance

## Entity: FeedDefinition
- Purpose: Configures a candidate package source for multi-feed resolution.
- Fields:
  - `name` (string, required, unique)
  - `serviceIndex` (uri, required)
  - `trustLevel` (enum: `Trusted`, `Restricted`, `Untrusted`)
  - `priority` (int, required; lower number = higher priority)
  - `credentialsRef` (string, optional runtime secret reference)
- Validation rules:
  - `name` must be unique.
  - `serviceIndex` must be absolute HTTPS URI.
  - `priority` must be deterministic and finite.

## Entity: FeedResolutionDecision
- Purpose: Records deterministic feed selection for one package in one cycle.
- Fields:
  - `packageId` (string, required)
  - `requestedFeed` (string, optional)
  - `candidateFeeds` (list<string>, required)
  - `selectedFeed` (string, optional when unresolved)
  - `selectedVersion` (string, optional when unresolved)
  - `decisionPath` (string, required; ordered rationale)
  - `correlationId` (string, required)
- Validation rules:
  - Selection order must follow `explicit feed -> priority -> version -> feed name`.

## Entity: TrustPolicyEvaluation
- Purpose: Captures policy outcomes for package eligibility.
- Fields:
  - `packageId` (string, required)
  - `feedName` (string, required)
  - `trustLevel` (enum, required)
  - `validatorResults` (list<ValidatorResult>, optional)
  - `overrideScope` (enum: `none`, `package`, `feed-rule`)
  - `overrideReason` (string, required when overrideScope != `none`)
  - `status` (enum: `allowed`, `blocked`)
- Validation rules:
  - `Restricted` requires successful validator results.
  - `Untrusted` requires explicit scoped override and reason.

## Entity: LockFile
- Purpose: Represents deterministic reproducibility input/output.
- Fields:
  - `schemaVersion` (string, required)
  - `generatedAt` (datetime, required)
  - `packages` (list<LockEntry>, required)
- Validation rules:
  - Package IDs in lock file are unique.

## Entity: LockEntry
- Purpose: Immutable package lock record used for enforce/strict modes.
- Fields:
  - `id` (string, required)
  - `version` (string, required)
  - `feed` (string, required)
  - `hash` (string, required)
  - `timestamp` (datetime, required)
- Validation rules:
  - Enforce mode uses lock version/feed over live resolution.
  - Strict mode requires entry existence for each desired package.
  - Hash mismatch blocks activation.

## Entity: FeedRule
- Purpose: Controlled desired-state discovery rule for feeds.
- Fields:
  - `name` (string, required, unique)
  - `feed` (string, required)
  - `includeIdPrefixes` (list<string>, required, non-empty)
  - `maxPackages` (int, required, > 0)
  - `versionPolicy` (enum: `latest`, `pinned-major`, `exact`)
  - `enabled` (bool, required)
- Validation rules:
  - Prefix-only matching in Phase 2 (no regex).
  - Rule output must be deterministic and capped by `maxPackages`.

## Entity: CleanupPolicy
- Purpose: Defines historical package retention behavior.
- Fields:
  - `retainLastNVersions` (int, optional)
  - `retainYoungerThanDays` (int, optional)
  - `mode` (enum: `automatic`, `manual-only`)
  - `protectLkg` (bool, required = true)
- Validation rules:
  - Automatic cleanup runs only after successful reconciliation.
  - Retention uses union semantics when both count and age are configured.
  - LKG versions are never eligible for deletion.

## Entity: CleanupActionResult
- Purpose: Captures maintenance execution outcomes per package/version.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `action` (enum: `kept`, `deleted`, `blocked`)
  - `reason` (string, required)
  - `correlationId` (string, required)
  - `timestamp` (datetime, required)

## Relationships
- `FeedDefinition` participates in many `FeedResolutionDecision` records.
- `FeedResolutionDecision` and `TrustPolicyEvaluation` jointly determine package eligibility.
- `LockFile` contains many `LockEntry` records used during resolution/enforcement.
- `FeedRule` produces desired package candidates consumed by reconciliation.
- `CleanupPolicy` governs many `CleanupActionResult` records.

## State Transitions

### Package decision lifecycle
1. `Requested`
2. `ResolvedFeedVersion`
3. `PolicyEvaluated`
4. `LockEvaluated`
5. `EligibleForApply` or `Blocked`
6. `Applied` or `Failed` (with LKG preserved)

### Cleanup lifecycle
1. `CandidateIdentified`
2. `ProtectedByRetentionOrLkg` or `EligibleForDeletion`
3. `Deleted` or `DeletionFailed`
4. Failure keeps runtime active state unchanged and records diagnostics.
