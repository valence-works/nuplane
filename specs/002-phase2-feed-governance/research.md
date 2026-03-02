# Phase 0 Research — Phase 2 Advanced Feeds & Governance

## Decision 1: Multi-feed deterministic resolution order
- Decision: Resolve with deterministic precedence `explicit feed (if provided) -> feed priority -> highest matching version -> lexicographically smallest feed name`.
- Rationale: Preserves reproducibility and gives an unambiguous tie-break rule for equal-priority/equal-version candidates.
- Alternatives considered: Configuration-order tie-break (rejected: easier drift), fail on tie (rejected: lower availability).

## Decision 2: Strict feed outage failure scope
- Decision: In strict mode, fail only packages that require unavailable feed(s), continue unrelated packages.
- Rationale: Aligns with failure isolation and LKG-first safety while still enforcing strict policy for impacted packages.
- Alternatives considered: Fail full cycle (rejected: unnecessary blast radius), silent skip (rejected: hides policy failures).

## Decision 3: Trust-policy enforcement model
- Decision: Support `Trusted`, `Restricted`, and `Untrusted` feeds; restricted feeds require validator pipeline success; untrusted feeds require explicit per-package or per-feed-rule override with required reason.
- Rationale: Implements least-privilege governance and creates auditable exceptions.
- Alternatives considered: Global untrusted override (rejected: broad risk), no overrides (rejected: blocks controlled emergency usage).

## Decision 4: Lock-file reproducibility model
- Decision: Implement lock modes `generate`, `enforce`, `strict`; include package ID/version/feed/hash/timestamp in lock entries; fail activation on hash mismatch.
- Rationale: Ensures deterministic replay and integrity validation under feed drift.
- Alternatives considered: Version-only lock (rejected: weaker integrity/supply-chain guarantees), enforce-only mode without strict (rejected: weak completeness guarantees).

## Decision 5: Dry-run semantics
- Decision: Dry-run executes full resolution, policy, validator, and lock checks but performs no state mutations.
- Rationale: Produces operator-accurate outcomes and avoids false confidence before apply.
- Alternatives considered: Diff-only dry-run (rejected: hides policy failures), configurable partial checks (rejected: inconsistent operator expectations).

## Decision 6: Feed-rule discovery boundaries
- Decision: Support prefix-only rules, required hard max package limit, explicit version policy, and deterministic output ordering.
- Rationale: Enables controlled wildcard discovery while preventing runaway ingestion.
- Alternatives considered: Regex in Phase 2 (rejected: complexity/risk), unlimited candidate set (rejected: unsafe growth).

## Decision 7: Cleanup retention semantics
- Decision: Keep versions satisfying either count-based or age-based retention rule (union retention); never delete LKG versions; support manual-only mode.
- Rationale: Safer deletion behavior and explicit rollback protection.
- Alternatives considered: Intersection retention (rejected: too aggressive), single-rule-only (rejected: lower operator flexibility).

## Decision 8: Observability and diagnostics envelope
- Decision: Emit correlation-linked diagnostics for feed selection decisions, policy outcomes, lock outcomes, cleanup actions, and override reasons.
- Rationale: Provides actionable operational traces for governance and incident response.
- Alternatives considered: Logs-only minimal diagnostics (rejected: weak operability), metrics-only summaries (rejected: inadequate root-cause detail).

## Decision 9: Boundary testing strategy
- Decision: Require unit coverage for deterministic resolution/policy transitions, integration coverage for runtime-store/nuget/source interactions, and contract tests for lock/trust/cleanup boundaries.
- Rationale: Matches constitution requirement for test and contract discipline on high-risk runtime mutation paths.
- Alternatives considered: Unit-only (rejected: poor boundary confidence), integration-only (rejected: slower fault localization).
