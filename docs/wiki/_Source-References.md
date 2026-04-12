# Source References and Ownership Map

This file is the maintenance contract for the repository-owned wiki source set.

- **Canonical-source policy:** current repository behavior is the canon for this wiki.
- **Hybrid-hub policy:** the wiki owns orientation and onboarding; deep validation, fast-moving technical detail, and maintainer/runbook material stay repository-owned.
- **Audience policy:** every first-scope topic should clearly support an evaluator, integrator, or contributor route.

## Baseline page-to-source map

| Wiki page | Primary purpose | Primary audience | Canonical repository anchors | Drift-review triggers |
|-----------|-----------------|------------------|------------------------------|----------------------|
| `Home.md` | Introduce Nuplane and route readers | Evaluator | `README.md`, `docs/roadmap.md` | Product positioning, audience framing, or phase summary changes |
| `Overview.md` | Explain why Nuplane exists, what it does, and what it does not do | Evaluator | `README.md`, `docs/roadmap.md` | Capability list, non-goals, or plugin-boundary wording changes |
| `Getting-Started.md` | Provide the recommended first-use path and minimal mental model | Evaluator / Integrator | `README.md`, `samples/Nuplane.Sample.AspNetCore/Program.cs`, `samples/Nuplane.Sample.AspNetCore/appsettings.json`, `specs/014-query-package-catalog/quickstart.md` | Setup flow, sample host behavior, or validation handoff changes |
| `Usage-Guide.md` | Explain scenario selection and adoption paths | Integrator | `README.md`, `samples/Nuplane.Sample.AspNetCore/Program.cs`, `specs/014-query-package-catalog/quickstart.md` | Query surfaces, admin routes, loading guidance, or sample workflow changes |
| `Architecture-Guide.md` | Map Nuplane modules, control loop, and repository structure to concepts | Contributor | `README.md`, `docs/roadmap.md`, `src/Nuplane/`, `src/Nuplane.Loading/`, `src/Nuplane.Admin.Api/`, `src/Nuplane.Loading.Api/` | Module ownership, control-loop language, or roadmap phase boundaries change |
| `Concepts-and-Glossary.md` | Normalize terminology and definitions | All | `README.md`, `docs/roadmap.md`, accepted specs under `specs/` | Terminology changes, especially query/load-state or module-boundary wording |
| `_Sidebar.md` | Preserve the audience routes and baseline page list | All | `specs/016-nuplane-github-wiki/contracts/wiki-information-architecture-contract.md` | Page-set or routing changes |
| `_Footer.md` | Preserve label semantics and ownership boundary | All | `specs/016-nuplane-github-wiki/contracts/wiki-governance-and-labeling-contract.md` | Label-set or wiki/repository boundary changes |

## Topic ownership matrix

| Topic | Owned by | Baseline wiki page | Deep reference | Why the boundary exists |
|-------|----------|--------------------|----------------|-------------------------|
| Product positioning and non-goals | Wiki | `Home.md`, `Overview.md` | `README.md` | Readers need this in the wiki before deciding whether to continue |
| Recommended first-use learning path | Wiki | `Getting-Started.md` | `README.md`, `samples/Nuplane.Sample.AspNetCore/Program.cs` | The wiki should be self-sufficient for onboarding, but not duplicate every source snippet |
| Configuration-driven and code-driven adoption choices | Wiki | `Usage-Guide.md` | `README.md`, `samples/Nuplane.Sample.AspNetCore/appsettings.json` | Readers need scenario guidance, while full sample detail remains in the repo |
| Sample validation commands | Sample / Spec artifacts | `Getting-Started.md`, `Usage-Guide.md` | `specs/014-query-package-catalog/quickstart.md`, `samples/Nuplane.Sample.AspNetCore/` | The command set changes more often than the wiki narrative |
| Query-first catalog details and route composition | Wiki summary + repository detail | `Usage-Guide.md`, `Architecture-Guide.md` | `README.md`, `samples/Nuplane.Sample.AspNetCore/Program.cs`, `src/Nuplane.Admin.Api/`, `src/Nuplane.Loading.Api/` | The wiki should teach the model while code and sample routes remain canonical |
| Architecture module map | Wiki | `Architecture-Guide.md` | `docs/roadmap.md`, `src/` packages | Contributors need a narrative map before reading the source tree |
| Phase-specific roadmap evolution | Repository docs | `Architecture-Guide.md` | `docs/roadmap.md`, accepted specs in `specs/` | Detailed staged history belongs in the roadmap/spec set |
| Maintainer runbooks and operator procedures | Repository docs | `Usage-Guide.md`, `Architecture-Guide.md` | `README.md`, feature specs, future ops docs | First-scope wiki is not a maintainer portal |
| Terminology normalization | Wiki | `Concepts-and-Glossary.md` | `README.md`, accepted specs | Readers need one normalized vocabulary surface |

## Drift-review notes

Review this file when any of the following change:

1. `README.md` changes the one-sentence project positioning, capability list, non-goals, or quick-start story.
2. `docs/roadmap.md` changes phase boundaries, module ownership, or contributor-facing architecture explanations.
3. The sample host changes route names, registration surfaces, or the recommended validation flow.
4. Accepted specs introduce a terminology shift that affects evaluator, integrator, or contributor guidance.

## Update routing rules

- Update the wiki first when the change affects reader framing, onboarding flow, page purpose, or terminology normalization.
- Update repository docs or samples first when the change affects deep validation commands, route payload details, or maintainer-only procedures.
- If a request touches both, update the canonical repo source first, then reconcile the wiki summary against it.

