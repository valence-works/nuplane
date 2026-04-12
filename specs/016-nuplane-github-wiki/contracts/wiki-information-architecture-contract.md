# Contract — Wiki Information Architecture

## Purpose
Define the minimum page set, audience routing, and page-purpose boundaries for the first-scope Nuplane GitHub wiki.

## Required baseline page set

| Page purpose | Required audience(s) | Required outcomes |
|--------------|----------------------|-------------------|
| `Home` | Evaluator, Integrator, Contributor | Introduces Nuplane, explains what the wiki is for, routes each audience to the right next page |
| `Overview` | Evaluator | Explains why Nuplane exists, what it does, what it does not do, who it is for, and why it is not a plugin model |
| `Getting Started` | Evaluator, Integrator | Explains the recommended first path, minimal setup mental model, and where to go for sample-backed validation |
| `Usage Guide` | Integrator | Explains core-runtime usage, query-first integration, optional loading usage, and where sample applications fit |
| `Architecture Guide` | Contributor, advanced Integrator | Explains module responsibilities, control loop, ownership boundaries, and how repository structure maps to product concepts |
| `Concepts / Glossary` | All | Defines Nuplane-specific terminology and acts as the reference point for consistent wording across pages |

## Audience paths

### Evaluator path
1. `Home`
2. `Overview`
3. `Getting Started`
4. Optional reference to `Concepts / Glossary`

**Reader must be able to answer**:
- Why does Nuplane exist?
- What does it do?
- What does it not do?
- Is it relevant to my kind of host/application?

### Integrator path
1. `Home`
2. `Overview`
3. `Getting Started`
4. `Usage Guide`
5. Optional reference to `Architecture Guide` and `Concepts / Glossary`

**Reader must be able to answer**:
- How do I start using Nuplane?
- When is core runtime usage enough?
- When does optional loading matter?
- Where do I find the sample and validation flow?

### Contributor path
1. `Home`
2. `Overview`
3. `Architecture Guide`
4. `Concepts / Glossary`
5. Cross-reference to roadmap and relevant specs for depth

**Reader must be able to answer**:
- What are the major modules?
- How does the control loop work conceptually?
- How do user-facing concepts map to repository structure?
- Which deeper materials remain repository-owned?

## Page-purpose rules
- Each baseline page must have one primary purpose and one primary audience emphasis.
- A page may support multiple audiences, but it must not duplicate another page’s main job.
- `Home` owns navigation and first-contact framing; it must not become the entire wiki compressed into one page.
- `Overview` owns positioning and non-goals.
- `Getting Started` owns the recommended first path.
- `Usage Guide` owns applied integration guidance.
- `Architecture Guide` owns structure and technical boundaries.
- `Concepts / Glossary` owns definitions and terminology normalization.

## Cross-link rules
- `Home` must link to every baseline page either directly or via clearly grouped audience paths.
- `Getting Started` must link to at least one concrete repository sample or validation document.
- `Usage Guide` must link to the sample application and to deeper validation/reference material.
- `Architecture Guide` must link to `docs/roadmap.md` and at least one repository implementation area or accepted spec.
- `Concepts / Glossary` must be reachable from every other baseline page.

## Rejection conditions
This contract fails if any of the following are true:
- A required baseline page purpose is missing.
- An audience path requires an undefined future page to complete its core journey.
- Two baseline pages have materially overlapping primary purposes.
- The wiki presents maintainer or runbook content as required first-scope wiki pages.

