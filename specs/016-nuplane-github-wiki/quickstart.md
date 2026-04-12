# Quickstart — Nuplane GitHub Wiki

## Goal
Validate that the first-scope Nuplane GitHub wiki provides a hybrid-hub documentation experience for evaluators, integrators, and architecture-oriented contributors; reflects current repository behavior with explicit stability/applicability labels; and delivers the required repository-owned baseline page set under `docs/wiki/` without duplicating repository-owned runbook or validation detail.

## Preconditions
- Feature branch `016-nuplane-github-wiki` is checked out.
- The wiki source files have been created under the required first-scope implementation path `docs/wiki/`, including `Home.md`, `Overview.md`, `Getting-Started.md`, `Usage-Guide.md`, `Architecture-Guide.md`, `Concepts-and-Glossary.md`, `_Sidebar.md`, `_Footer.md`, and `_Source-References.md`.
- Repository reference materials still exist and are readable, including `README.md`, `docs/roadmap.md`, `samples/Nuplane.Sample.AspNetCore/Program.cs`, and any linked sample/quickstart artifacts.

## Verification command set

Run from repository root:

```bash
find docs/wiki -maxdepth 1 -type f -name '*.md' | sort
grep -Hn "README.md\|docs/roadmap.md\|samples/Nuplane.Sample.AspNetCore\|specs/014-query-package-catalog/quickstart.md" docs/wiki/*.md
grep -Hn "Applicability:\|Core\|Optional Module\|Phase-Based\|Recently Changed\|Evolving" docs/wiki/*.md
git --no-pager diff -- docs/wiki README.md docs/roadmap.md samples/Nuplane.Sample.AspNetCore/Program.cs | cat
```

The first-scope implementation path is `docs/wiki/`; if a later feature introduces a publication or synchronization workflow, validate that separately rather than changing this quickstart path.

## Timed review protocol

Use this protocol to validate the comprehension-oriented success criteria.

1. Select a reviewer who has not read Nuplane source code during this review session.
2. Record the reviewer persona and the timing method used (for example: wall-clock timer, stopwatch app, or screen recording timestamps).
3. Start timing when the reviewer opens `docs/wiki/Home.md`.
4. For evaluator review, record the exact question set below and whether the reviewer can answer each item within 5 minutes:
   - Why does Nuplane exist?
   - What does it do?
   - What does it not do?
   - Why is it not a plugin framework?
5. For integrator review, record the exact question set below and whether the reviewer can identify each item within 10 minutes:
   - The recommended getting-started path
   - At least one sample-backed validation path
   - The distinction between core-runtime usage and optional loading usage
6. Record reviewer persona, timing method, question set used, elapsed time, and pass/fail outcome for both reviews in `quickstart-validation.md`.

## 1) Validate the minimum page set
1. Confirm the implemented wiki contains the required baseline pages: `docs/wiki/Home.md`, `docs/wiki/Overview.md`, `docs/wiki/Getting-Started.md`, `docs/wiki/Usage-Guide.md`, `docs/wiki/Architecture-Guide.md`, and `docs/wiki/Concepts-and-Glossary.md`.
2. Confirm each page has one obvious primary purpose rather than duplicating another page’s job.
3. Confirm no evaluator, integrator, or contributor journey depends on an undefined future page.

## 2) Validate evaluator onboarding
1. Open `docs/wiki/Home.md` and follow the evaluator path to `docs/wiki/Overview.md` and `docs/wiki/Getting-Started.md`.
2. Confirm a first-time reader can identify why Nuplane exists, what it does, what it does not do, and why it is not a plugin framework.
3. Confirm the path is self-sufficient for evaluation and does not require source-code reading.

## 3) Validate integrator onboarding
1. Follow the integrator path from `docs/wiki/Home.md` through `docs/wiki/Overview.md`, `docs/wiki/Getting-Started.md`, and `docs/wiki/Usage-Guide.md`.
2. Confirm the wiki explains the recommended first-use path, query-first integration guidance, and the distinction between core-runtime usage and optional loading usage.
3. Confirm hands-on or validation-heavy details link to concrete repository materials rather than being absent or vaguely deferred.

## 4) Validate contributor / architecture orientation
1. Follow the contributor path from `docs/wiki/Home.md` through `docs/wiki/Overview.md` into `docs/wiki/Architecture-Guide.md` and `docs/wiki/Concepts-and-Glossary.md`.
2. Confirm the wiki explains the control loop, module boundaries, repository-to-concept mapping, and current terminology consistently with `README.md` and `docs/roadmap.md`.
3. Confirm deeper roadmap or implementation-evolution material is linked rather than exhaustively duplicated.

## 5) Validate stability and applicability labeling
1. Review pages that discuss optional loading, staged capabilities, or recently changed terminology.
2. Confirm those sections are explicitly marked so readers can tell whether the material is core, optional, phase-based, recently changed, or evolving.
3. Confirm baseline current behavior is not mislabeled as tentative.

## 6) Validate content-boundary discipline
1. Confirm the wiki includes concise onboarding guidance rather than only abstract concepts.
2. Confirm the wiki does not duplicate full runbooks, exhaustive validation matrices, or volatile implementation detail when a repository source already owns that material.
3. Confirm maintainer-only or operator-runbook content is clearly referenced as repository-owned detail, not as a missing wiki page.

## 7) Validate ownership mapping
1. Review `docs/wiki/_Source-References.md`.
2. Confirm each major first-scope topic has an explicit ownership decision: wiki-owned, repository-doc-owned, or sample/spec-owned.
3. Confirm maintainers can determine where to update content when product messaging, onboarding flow, validation steps, or deeper technical detail changes.
4. Confirm the ownership matrix explicitly preserves the hybrid-hub boundary instead of silently treating maintainer/runbook content as a missing wiki page.

## Expected review evidence
- A complete baseline page set with clear audience routing.
- Consistent explanation of Nuplane’s purpose, capabilities, non-goals, and host-neutral boundaries.
- A guided onboarding path that points to sample-backed repository validation material.
- Architecture pages that align with existing repository terminology and module ownership.
- Explicit stability/applicability labels on optional, phase-based, recently changed, or evolving topics.
- Clear separation between wiki-owned onboarding content and repository-owned deep reference material.
- Timed-review evidence for evaluator and integrator comprehension targets.
- An explicit topic ownership matrix showing which material belongs in the wiki versus repository docs, samples, or specs.

## Expected outcomes
- Evaluators can determine whether Nuplane is relevant to them quickly.
- Integrators can find the recommended first-use path and the correct next step for deeper validation.
- Contributors can understand the architecture without the initial wiki becoming a full maintainer portal.
- Maintainers can tell which future documentation updates belong in the wiki versus repository docs or samples.

