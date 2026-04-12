# Tasks: Nuplane GitHub Wiki

**Input**: Design documents from `/specs/016-nuplane-github-wiki/`
**Prerequisites**: `plan.md` (required), `spec.md` (required), `research.md`, `data-model.md`, `contracts/`, `quickstart.md`, `quickstart-validation.md`

**Tests**: No automated runtime test tasks are required for this documentation-only feature. Validation is performed through documentation review, link/path verification, and audience-journey walkthroughs defined in `specs/016-nuplane-github-wiki/quickstart.md`.

**Organization**: Tasks are grouped by user story so the evaluator-facing wiki surface can ship first, onboarding guidance can follow, and architecture/reference content can be added without reopening the product narrative.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel when prerequisites are complete and the task touches a different file
- **[Story]**: User story label for story-phase tasks only (`[US1]`, `[US2]`, `[US3]`)
- Every task includes an exact file path

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the shared validation and navigation scaffolding for the repository-owned wiki source set.

- [X] T001 Create the feature validation evidence scaffold, including reviewer persona, timing method, question-set, elapsed-time, and pass/fail fields for SC-002 and SC-003 plus a topic-ownership review section, in `specs/016-nuplane-github-wiki/quickstart-validation.md`
- [X] T002 Create the initial wiki navigation skeleton and baseline page list in `docs/wiki/_Sidebar.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish shared governance artifacts that every wiki page depends on before story-specific authoring begins.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T003 [P] Create the shared stability/applicability legend and repository-owned boundary footer in `docs/wiki/_Footer.md`
- [X] T004 [P] Create the page-to-source reference map and wiki-vs-repository ownership matrix for `README.md`, `docs/roadmap.md`, samples, and accepted specs in `docs/wiki/_Source-References.md`

**Checkpoint**: Shared navigation, labeling, and source-reference rules exist so each user-story page can be authored against the same documentation contract.

---

## Phase 3: User Story 1 - Understand Nuplane Quickly (Priority: P1) 🎯 MVP

**Goal**: Deliver the landing and overview content that explains why Nuplane exists, what it does, what it does not do, and who should keep reading.

**Independent Test**: Review `docs/wiki/Home.md`, `docs/wiki/Overview.md`, and the repository entry point in `README.md`, then confirm a first-time reader can identify Nuplane’s purpose, core capabilities, non-goals, and plugin-model boundary without reading source code.

### Implementation for User Story 1

- [X] T005 [P] [US1] Author the landing page with Nuplane’s value proposition, audience routes, and wiki scope in `docs/wiki/Home.md`
- [X] T006 [P] [US1] Author the product overview with problem statement, capabilities, non-goals, and plugin-model boundary in `docs/wiki/Overview.md`
- [X] T007 [US1] Add a discoverable GitHub wiki entry point for evaluators in `README.md`

**Checkpoint**: Evaluators can discover the wiki and understand Nuplane’s purpose and boundaries from the landing experience alone.

---

## Phase 4: User Story 2 - Learn How To Use Nuplane (Priority: P2)

**Goal**: Provide a practical onboarding path that helps integrators move from first-read understanding to sample-backed first use.

**Independent Test**: Follow the integrator path through `docs/wiki/Getting-Started.md` and `docs/wiki/Usage-Guide.md`, then confirm a reader can identify the recommended setup path, common workflows, and the distinction between core-runtime and optional-loading scenarios.

### Implementation for User Story 2

- [X] T008 [P] [US2] Author the recommended first-use path, minimum mental model, and validation handoff in `docs/wiki/Getting-Started.md`
- [X] T009 [P] [US2] Author the usage guide for configuration-driven, code-driven, core-runtime, and optional-loading scenarios, including explicit stability/applicability labels for optional or non-baseline content, in `docs/wiki/Usage-Guide.md`

**Checkpoint**: Integrators can understand how to start with Nuplane, when optional loading matters, and where to go for deeper sample-backed validation.

---

## Phase 5: User Story 3 - Understand Architecture And Technical Design (Priority: P3)

**Goal**: Explain Nuplane’s architecture, terminology, and repository-to-concept mapping for contributors and advanced adopters.

**Independent Test**: Review `docs/wiki/Architecture-Guide.md`, `docs/wiki/Concepts-and-Glossary.md`, and the roadmap cross-reference in `docs/roadmap.md`, then confirm a contributor can explain the major modules, control loop, technical vocabulary, and deeper reference boundaries.

### Implementation for User Story 3

- [X] T010 [P] [US3] Author the architecture guide with module map, control loop, ownership boundaries, repository-to-concept mapping, and explicit stability/applicability labels for optional, phase-based, or evolving areas in `docs/wiki/Architecture-Guide.md`
- [X] T011 [P] [US3] Author the concepts/glossary page with normalized Nuplane terminology and backlinks in `docs/wiki/Concepts-and-Glossary.md`
- [X] T012 [US3] Add a contributor-oriented wiki cross-reference in `docs/roadmap.md`

**Checkpoint**: Contributors and advanced adopters can understand how Nuplane works architecturally without the wiki becoming a maintainer runbook.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Reconcile navigation, source references, and validation evidence across the complete wiki page set.

- [X] T013 [P] Reconcile the final page-to-source mappings, topic ownership matrix, and drift-review notes in `docs/wiki/_Source-References.md`
- [X] T014 [P] Update the validation walkthrough with the finalized wiki filenames, timed-review protocol, and ownership-boundary review steps in `specs/016-nuplane-github-wiki/quickstart.md`
- [X] T015 Capture final audience-path, labeling, timed-review, ownership-boundary, and cross-reference review evidence, including reviewer persona, timing method, question-set, elapsed-time, and pass/fail results, in `specs/016-nuplane-github-wiki/quickstart-validation.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies; can start immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1; blocks all user-story work because it establishes shared wiki governance artifacts.
- **Phase 3 (US1)**: Depends on Phase 2; delivers the MVP landing and overview experience.
- **Phase 4 (US2)**: Depends on Phase 2 and benefits from US1 because onboarding guidance should build on the final product narrative.
- **Phase 5 (US3)**: Depends on Phase 2 and benefits from US1 terminology so architecture content aligns with the finalized positioning language.
- **Phase 6 (Polish)**: Depends on all implemented user stories.

### User Story Dependencies

- **US1 (P1)**: Starts after Foundational; no dependency on other user stories.
- **US2 (P2)**: Starts after Foundational; should follow the US1 narrative so the usage guidance reuses the same positioning and terminology.
- **US3 (P3)**: Starts after Foundational; should follow US1 terminology alignment and can run alongside US2 once the shared governance files exist.

### Within Each User Story

- Shared navigation, labeling, and source-reference artifacts must exist before page authoring begins.
- Story pages should be written before repository entry-point or roadmap cross-reference updates.
- Story-specific page content must be complete before final validation evidence is captured.
- Each story is complete only when its independent test criteria can be satisfied without relying on undefined future pages.

### Suggested Completion Order

1. **Setup** → **Foundational**
2. **US1** (MVP landing + overview)
3. **US2** (guided onboarding + usage)
4. **US3** (architecture + glossary)
5. **Polish**

---

## Parallel Opportunities

- **Setup**: No meaningful parallel split is needed for this small phase.
- **Foundational**: `T003` and `T004` can run in parallel after `T002`.
- **US1**: `T005` and `T006` can run in parallel, then `T007` follows once the landing/overview structure is settled.
- **US2**: `T008` and `T009` can run in parallel after the shared navigation and labeling files exist.
- **US3**: `T010` and `T011` can run in parallel, then `T012` follows once the contributor-facing architecture path is finalized.
- **Polish**: `T013` and `T014` can run in parallel before `T015` captures final review evidence.

### Parallel Example: User Story 1

```bash
# Draft the evaluator-facing wiki pages in parallel
T005
T006
```

### Parallel Example: User Story 2

```bash
# Draft the integrator-facing wiki pages in parallel
T008
T009
```

### Parallel Example: User Story 3

```bash
# Draft the contributor-facing wiki pages in parallel
T010
T011
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup.
2. Complete Phase 2: Foundational.
3. Complete Phase 3: US1.
4. Validate that evaluators can discover and understand Nuplane from the wiki without consulting source code.
5. Stop and confirm the positioning, non-goals, and narrative boundaries before authoring deeper usage or architecture material.

### Incremental Delivery

1. Finish Setup + Foundational to establish the wiki source structure and governance rules.
2. Ship **US1** as the MVP landing experience.
3. Add **US2** so integrators get a practical onboarding and usage path.
4. Add **US3** so contributors and advanced adopters get architecture orientation.
5. Finish with navigation/source reconciliation and validation evidence.

### Parallel Team Strategy

1. One maintainer sets up the validation scaffold and shared wiki chrome.
2. After Foundational completion:
   - Writer A: `Home.md` + `Overview.md`
   - Writer B: `Getting-Started.md` + `Usage-Guide.md`
   - Writer C: `Architecture-Guide.md` + `Concepts-and-Glossary.md`
3. Reconcile shared references and walkthrough evidence together before publishing.

---

## Notes

- `[P]` tasks are safe to parallelize only after their prerequisites are complete and no two tasks edit the same file simultaneously.
- `[USx]` labels trace each story-phase task back to the feature specification.
- This plan keeps the wiki self-sufficient for evaluation and onboarding while still routing deeper validation, roadmap, and implementation detail to repository-owned sources.
- `US1` is the suggested MVP scope.
- No task creates a maintainer-only runbook page; first-scope depth beyond onboarding and architecture orientation stays in repository docs, samples, and specs.

[US1]: #phase-3-user-story-1---understand-nuplane-quickly-priority-p1--mvp
[US2]: #phase-4-user-story-2---learn-how-to-use-nuplane-priority-p2
[US3]: #phase-5-user-story-3---understand-architecture-and-technical-design-priority-p3

