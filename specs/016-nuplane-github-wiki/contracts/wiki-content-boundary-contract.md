# Contract — Wiki Content Boundaries

## Purpose
Define what each baseline wiki page must contain, what it may summarize, and which deeper topics must remain repository-owned references in the first implementation scope.

## Required content by page purpose

### `Home`
Must include:
- One-sentence explanation of Nuplane
- Short value proposition
- Clear navigation for evaluator, integrator, and contributor paths
- Pointer to stability/applicability labeling approach

Must not include:
- Full architecture walkthrough
- Detailed validation commands
- Maintainer runbooks

### `Overview`
Must include:
- Why Nuplane exists
- Problem statement and intended audience
- What Nuplane does
- What Nuplane does not do
- Explicit statement that Nuplane is infrastructure, not a plugin programming model
- Summary of current major capabilities

Must not include:
- Exhaustive step-by-step setup instructions
- Deep internal module-by-module technical commentary better suited for the architecture page

### `Getting Started`
Must include:
- Recommended first reading/use sequence
- Concise, copyable onboarding guidance
- Explanation of the minimum mental model needed to start
- Pointer to the sample application and one or more repository validation sources

Must not include:
- Full duplicated quickstart command sets when a maintained repository quickstart already exists
- Exhaustive troubleshooting/runbook material

### `Usage Guide`
Must include:
- Core-runtime usage path
- Query-first usage explanation
- Distinction between metadata-only/core usage and optional loading-enabled usage
- Configuration-driven and code-driven adoption summary
- Sample-backed next steps

Must not include:
- Plugin-framework framing
- Implicit requirement that optional loading is always enabled

### `Architecture Guide`
Must include:
- High-level module map
- Control-loop explanation
- Ownership boundaries between core and optional modules
- Repository-to-concept mapping
- Pointers to roadmap/spec artifacts for deeper technical evolution

Must not include:
- Maintainer-only operational runbooks as baseline content
- Exhaustive change log prose better suited to specs or repo history

### `Concepts / Glossary`
Must include:
- Definitions for desired state, actual state, feed, reconciliation, package store, active package, observer, operational state, and optional loading-related concepts when referenced elsewhere
- Consistent wording aligned with `README.md` and accepted specs
- Cross-links back to the pages where terms are applied

## Ownership boundary rules

### Wiki-owned topics
The wiki must own and explain:
- Product positioning and non-goals
- First-use learning path
- Reader-facing usage overview
- Architectural orientation for contributors and advanced adopters
- Terminology normalization

### Repository-owned topics (linked from the wiki)
The wiki must reference, not fully duplicate:
- Exhaustive command sequences for sample validation
- Runbook-style operational procedures
- Detailed feature-history evolution across phases
- Low-level implementation specifics that change frequently
- Deep sample mechanics already documented in source and feature quickstarts

## Cross-reference requirements
- Any repository-owned topic mentioned in the wiki must point to a specific repo path.
- Usage guidance that references hands-on validation must point to at least one sample or quickstart artifact.
- Architecture guidance that references evolving areas must point to the roadmap and/or relevant accepted specs.

## Rejection conditions
This contract fails if any of the following are true:
- A baseline page is missing required content for its purpose.
- A page silently omits a topic by assuming the reader will discover it elsewhere.
- The wiki duplicates deep validation or runbook material without a clear onboarding benefit.
- The wiki implies optional loading is required for all Nuplane integrations.

