# Phase 0 Research — Nuplane GitHub Wiki

## Decision 1: Keep the wiki source versioned inside the repository under `docs/wiki/`

**Decision**: Author the first-scope GitHub wiki pages as repository-managed Markdown files in `docs/wiki/`, using page-oriented files that can be copied or synchronized to the GitHub wiki while remaining reviewable in the main repo.

**Rationale**:
- The feature is documentation architecture, and the repository already treats `README.md`, `docs/roadmap.md`, and sample docs as canonical source material.
- Keeping wiki source in-repo allows normal PR review, branch-based iteration, and drift review alongside the code and samples the content describes.
- A repository folder makes it practical to validate cross-references to `README.md`, roadmap material, sample hosts, and feature quickstarts without editing a separate GitHub wiki repository during implementation.
- `docs/` is already the project’s long-lived documentation home, so `docs/wiki/` fits existing structure better than adding a parallel top-level documentation root.

**Alternatives considered**:
- **Direct editing in the GitHub wiki repository**: Rejected because it splits the documentation change workflow from the main repo and makes branch-scoped planning, review, and traceability harder.
- **Keeping wiki pages only as root-level Markdown files**: Rejected because it blurs the difference between repository docs and the wiki-specific page set.
- **Treating the wiki as a generated artifact only**: Rejected because the first implementation needs human-authored information architecture, not just a publishing transform.

## Decision 2: Use a hybrid-hub information model with explicit stability labels

**Decision**: The wiki will be self-sufficient for evaluation and onboarding, but deeper validation details, runbook-style guidance, and rapidly changing technical reference remain in repository docs and samples. Pages will describe current repository behavior as canonical while explicitly labeling optional, phase-based, recently changed, or evolving areas.

**Rationale**:
- This aligns with the accepted clarifications in `spec.md` and avoids duplicating the most volatile material.
- Nuplane’s feature set spans core runtime behavior, optional loading, staged roadmap work, and sample-driven validation; explicit labels reduce the risk of presenting all capabilities as equally mature or universally applicable.
- A hybrid hub gives first-time readers a coherent story without making the wiki another copy of every quickstart, roadmap note, or validation matrix.

**Alternatives considered**:
- **Standalone manual**: Rejected because it would create unnecessary duplication and higher documentation drift risk.
- **Thin navigation layer**: Rejected because it would not satisfy the requirement for a self-sufficient onboarding and evaluation experience.
- **Stable-release-only documentation**: Rejected because the repository’s current behavior is the practical source of truth for this planning scope.

## Decision 3: The initial page set should be fixed at the purpose level, not the exact final filenames

**Decision**: Plan against a minimum page set with these purposes: Home, Overview, Getting Started, Usage Guide, Architecture Guide, and Concepts/Glossary. Implementation may refine page titles or split/merge minor sections if each required purpose remains clearly covered.

**Rationale**:
- The feature needs concrete deliverables for planning and task decomposition.
- The accepted clarification allows naming flexibility while still requiring complete coverage of the first-scope reader journeys.
- A purpose-based minimum set keeps the documentation navigable and keeps each page reviewable against a single responsibility.

**Alternatives considered**:
- **Outcome-only structure**: Rejected because it would leave page ownership ambiguous during implementation.
- **Fully fixed sitemap with exact titles**: Rejected because it over-constrains wording and implementation details before authors draft the content.

## Decision 4: Provide guided onboarding, not a full tutorial clone of repository quickstarts

**Decision**: The Getting Started and Usage pages should include concise, copyable onboarding guidance and a recommended first path, but should link to repository quickstarts, sample applications, and validation-oriented documents for full end-to-end commands and deep verification.

**Rationale**:
- This is the best fit for the hybrid-hub model and the user stories around practical usage without excessive duplication.
- The repo already contains hands-on material, including the sample host and feature quickstarts such as `specs/014-query-package-catalog/quickstart.md`.
- Concise onboarding helps evaluators and integrators start quickly while keeping complex validation steps in source-controlled technical docs that can evolve with implementation.

**Alternatives considered**:
- **Conceptual-only onboarding**: Rejected because the wiki would feel too abstract for integrators.
- **Full end-to-end tutorial inside the wiki**: Rejected because it duplicates volatile commands and sample validation steps that are better maintained in the repository.

## Decision 5: Treat the wiki as a user-facing interface with explicit content contracts

**Decision**: Define contracts for (1) information architecture and audience routing, (2) page content boundaries and required sections, and (3) stability/cross-reference governance.

**Rationale**:
- The wiki is an external reader interface even though it is documentation rather than code.
- Content contracts make the implementation reviewable and provide clear acceptance boundaries for a documentation-heavy feature.
- Separating architecture, page-purpose, and governance concerns matches the constitution’s decomposition discipline.

**Alternatives considered**:
- **No contracts for documentation features**: Rejected because it weakens reviewability and makes page scope subjective.
- **A single monolithic documentation contract**: Rejected because it would mix navigation, content, and governance concerns into one artifact.

## Decision 6: Validate the feature through content review, source alignment, and path verification rather than runtime test execution

**Decision**: Validation will focus on manual review of the wiki page set, verification of cross-links and source references, and confirmation that the evaluator, integrator, and contributor paths can be completed from the implemented page set. No new runtime behavior or test suites are required unless implementation introduces tooling for documentation checks.

**Rationale**:
- This feature changes documentation architecture rather than executable runtime behavior.
- The relevant evidence is coverage, correctness, cross-reference quality, and alignment with current repository materials.
- The constitution’s testing discipline is still satisfied by defining explicit review and verification steps for the affected boundary: public documentation contracts.

**Alternatives considered**:
- **Mandatory automated doc tooling in first scope**: Rejected because the spec does not require new tooling and the repo does not currently establish a wiki-validation toolchain.
- **No explicit validation flow**: Rejected because documentation drift and incomplete audience paths would be hard to detect.

