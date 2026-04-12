# Contract — Wiki Governance and Stability Labeling

## Purpose
Define how the wiki stays aligned with the repository, how stability/applicability labels are used, and how maintainers distinguish wiki-owned content from deeper repository references.

## Canonical-source policy
- Current repository behavior is the canonical documentation state for the first-scope wiki.
- The wiki must align with `README.md`, `docs/roadmap.md`, the sample application under `samples/Nuplane.Sample.AspNetCore/`, and relevant accepted feature artifacts used as reference material.
- When those sources differ, the implementation must resolve the wording conflict rather than copying both versions into the wiki.

## Stability / applicability label set

| Label | Use when | Reader meaning |
|-------|----------|----------------|
| `Core` | Describing baseline current behavior that applies to standard Nuplane usage | This is part of the main current product story |
| `Optional Module` | Describing loading-owned or otherwise optional capabilities | This behavior depends on an optional module or extra setup |
| `Phase-Based` | Describing roadmap or staged feature context that is not universal baseline behavior | This is tied to a phase or staged capability boundary |
| `Recently Changed` | Describing an intentionally recent terminology or behavior shift that readers may still see referenced elsewhere | Expect some surrounding materials or prior discussions to use older wording |
| `Evolving` | Describing an area that is intentionally still being refined and should not be over-described as settled | Treat this as current but still changing |

## Labeling rules
- Optional loading-related content must be labeled as `Optional Module` at the point where it becomes relevant.
- Phased capability discussions must not be presented as unconditional baseline behavior.
- Labels must be applied consistently across all pages that discuss the same topic.
- Labels must clarify applicability without turning the wiki into a warning-heavy change log.
- Baseline product statements must not be mislabeled as tentative.

## Drift-management rules
- Every baseline page must cite at least one repository source file or artifact that anchors its claims.
- Hands-on validation references must link to concrete repo documents, samples, or spec quickstarts.
- Maintainers must be able to tell whether a requested documentation change belongs in the wiki, the README, roadmap/spec artifacts, or sample docs.
- The wiki must not become the sole home of volatile technical detail that already has a better repository source of truth.

## Review checklist expectations
Implementation review must confirm:
1. The baseline page set exists.
2. Each page has one primary purpose.
3. Evaluator, integrator, and contributor paths can be followed from `Home`.
4. Stability/applicability labels are present where required and absent where misleading.
5. Cross-references point to concrete repository paths.
6. The wiki does not present maintainer-only runbooks as missing pages.

## Rejection conditions
This contract fails if any of the following are true:
- A page describes optional or staged behavior without clarifying applicability.
- The wiki conflicts with the README, roadmap, or sample guidance on a core product statement.
- Cross-references rely on unspecified future documentation.
- The content boundary between wiki pages and repository-owned detail is unclear enough that maintainers cannot tell where an update belongs.

