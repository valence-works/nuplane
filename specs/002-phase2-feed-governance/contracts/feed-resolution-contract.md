# Contract: Multi-Feed Resolution

## Interface Boundary
- Runtime requests package resolution from NuGet resolution boundary using:
  - package ID
  - version range or lock constraint
  - optional explicit feed
  - configured feed definitions (name, priority, trust level)
- Resolver returns deterministic result:
  - selected feed
  - selected version
  - deterministic decision path metadata

## Behavioral Contract
- If request specifies `FeedName`, only that feed is eligible.
- Otherwise selection order is deterministic:
  1. Higher-priority feed eligibility
  2. Highest matching version within eligibility
  3. Lexicographically smallest feed name for priority/version ties
- In strict outage mode:
  - packages requiring unavailable feeds fail explicitly,
  - unrelated packages continue.

## Error Contract
- Resolution failures are stage-classified diagnostics (`resolve`) with correlation ID.
- Feed outage diagnostics include feed identity and policy mode.
- Resolver failures MUST NOT mutate active store state directly.

## Test Contract
- Must verify deterministic result stability over repeated identical runs.
- Must verify explicit-feed behavior bypasses all-feed search.
- Must verify strict outage scope only fails impacted packages.
