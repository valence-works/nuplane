# Implementation Plan: Nuplane GitHub Wiki

**Branch**: `[016-nuplane-github-wiki]` | **Date**: 2026-04-12 | **Spec**: `/Users/sipke/Projects/ValenceWorks/nuplane/main/specs/016-nuplane-github-wiki/spec.md`
**Input**: Feature specification from `/Users/sipke/Projects/ValenceWorks/nuplane/main/specs/016-nuplane-github-wiki/spec.md`

## Summary

Create a versioned repository-owned GitHub wiki source set for Nuplane under `docs/wiki/` that acts as a hybrid hub: self-sufficient for evaluation and onboarding, but intentionally linked to repository docs and samples for deep validation and fast-evolving reference material. The implementation should deliver a concrete baseline page set covering Home, Overview, Getting Started, Usage Guide, Architecture Guide, and Concepts/Glossary; describe current repository behavior as canonical; explicitly label optional, phase-based, recently changed, or evolving areas; and keep maintainer/runbook detail in repository-owned documents rather than duplicating it into the first-scope wiki.

## Technical Context

**Language/Version**: Markdown documentation authored in a repository whose product code targets `.NET 8/9/10`  
**Primary Dependencies**: Existing `README.md`, `docs/roadmap.md`, `docs/coding-conventions.md`, `samples/Nuplane.Sample.AspNetCore`, accepted feature specs and quickstarts under `specs/`, GitHub wiki Markdown/linking conventions  
**Storage**: Version-controlled Markdown files in the repository (planned under `docs/wiki/`); no runtime data store changes  
**Testing**: Documentation review, path/link verification, and audience-journey walkthroughs defined in `quickstart.md`, with validation evidence captured in `quickstart-validation.md`; no runtime test-suite changes required unless implementation adds doc tooling  
**Target Platform**: GitHub repository/wiki readers and maintainers on the web, with repository authors working locally on macOS/Linux/Windows  
**Project Type**: Documentation feature for a multi-project .NET OSS repository  
**Performance Goals**: Meet spec success criteria for reader comprehension: evaluators answer core product questions within 5 minutes, integrators find the first-use path within 10 minutes, and maintainers can map every first-scope topic to a baseline page  
**Constraints**: Preserve Nuplane’s host-neutral/plugin-neutral boundary; use a hybrid-hub model; treat current repository behavior as canonical; apply explicit stability/applicability labels where needed; avoid duplicating deep runbook/validation content already owned by repo docs or samples; and keep the initial scope limited to evaluator, integrator, and architecture-oriented contributor journeys  
**Scale/Scope**: Planned changes center on new wiki source material under `docs/wiki/`, plus any minimal navigation or cross-reference updates needed in `README.md` or existing docs to point readers toward the new wiki materials. Publication or synchronization into the hosted GitHub wiki is not part of this first implementation scope.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Initial Gate Assessment

- Deterministic reconciliation: PASS. The feature is documentation-only and does not change reconciliation behavior; the plan instead documents deterministic reconciliation accurately using existing repository sources.
- Transactional store safety: PASS. No runtime write-path or store semantics change is introduced; the wiki must explain existing transactional and LKG boundaries without redefining them.
- Source integrity: PASS. The design relies only on repository-owned documentation sources and sample code; it introduces no new package/feed/source behavior and must not imply weaker trust boundaries than the current product.
- Observability: PASS. The wiki will document observability and operational-state concepts consistently with the existing product narrative, but does not change logs, metrics, or health implementation.
- Test discipline: PASS. `quickstart.md` defines documentation-boundary validation through content review, source alignment, and audience-path walkthroughs for the public documentation interface.
- Decomposition discipline: PASS. The work decomposes cleanly into information architecture, page-content boundaries, governance/stability labeling, and validation artifacts rather than mixing unrelated implementation layers.
- Options validation discipline: PASS. No new runtime options or validators are introduced by the plan.

### Post-Design Gate Assessment

- Deterministic reconciliation: PASS. `research.md`, `data-model.md`, and `contracts/wiki-content-boundary-contract.md` keep the wiki aligned to existing reconciliation terminology and explicitly prevent plugin-model or feature-scope misrepresentation.
- Transactional store safety: PASS. `contracts/wiki-content-boundary-contract.md` and `contracts/wiki-governance-and-labeling-contract.md` preserve existing transactional-store explanations as documented product behavior only, with no new implementation path implied.
- Source integrity: PASS. `research.md` chooses repository-managed Markdown and repo-anchored source references only; `contracts/wiki-governance-and-labeling-contract.md` requires concrete repo paths for any deep reference.
- Observability: PASS. `data-model.md` introduces `StabilityLabel` and `SourceReference` entities, and `quickstart.md` verifies that optional, phased, or evolving areas are labeled consistently rather than silently blurred.
- Test discipline: PASS. `quickstart.md` defines repeatable validation for the baseline page set, audience paths, stability labeling, and cross-reference discipline.
- Decomposition discipline: PASS. The design keeps page architecture, content scope, labeling/governance, and validation as separate planning artifacts that can later map one concern per implementation task group.
- Options validation discipline: PASS. The design adds no configuration surface and therefore no options-validation work.

## Project Structure

### Documentation (this feature)

```text
specs/016-nuplane-github-wiki/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── quickstart-validation.md
├── contracts/
│   ├── wiki-information-architecture-contract.md
│   ├── wiki-content-boundary-contract.md
│   └── wiki-governance-and-labeling-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
README.md
docs/
├── coding-conventions.md
├── roadmap.md
└── wiki/                         # planned implementation target for versioned wiki source

samples/
└── Nuplane.Sample.AspNetCore/
   ├── Program.cs
   └── appsettings.json

specs/
├── 014-query-package-catalog/
│   └── quickstart.md
└── 016-nuplane-github-wiki/

src/
├── Nuplane/
├── Nuplane.Abstractions/
├── Nuplane.Loading/
└── ...

test/
├── Nuplane.Runtime.Tests/
├── Nuplane.Loading.Tests/
└── ...
```

**Structure Decision**: Keep implementation anchored in the existing repository documentation layout by adding a versioned wiki source folder under `docs/wiki/`. The wiki pages will summarize and route into canonical repository materials (`README.md`, `docs/roadmap.md`, sample host code, and accepted specs/quickstarts) rather than creating a parallel documentation tree at the repo root or requiring direct editing of a separate GitHub wiki repository during feature implementation.

## Delivery Stages

1. **Stage 1 — Shared wiki foundation and evaluator entry path (Setup, Foundational, US1)**: Establish navigation, governance, source-reference scaffolding, and the evaluator-facing Home/Overview entry experience.
2. **Stage 2 — Integrator onboarding path (US2)**: Add Getting Started and Usage Guide content for first-use learning, scenario selection, and sample-backed validation handoff.
3. **Stage 3 — Contributor architecture and terminology path (US3, Polish)**: Add Architecture Guide and Concepts/Glossary content, finalize labeling and source references, and validate the complete baseline page set as a hybrid hub rather than a maintainer portal.

## Complexity Tracking

No constitution violations or extra complexity justifications are required at planning time.
