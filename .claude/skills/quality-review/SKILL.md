---
name: quality-review
description: Recurring product-quality review of Nuplane. Assesses one lens per run (public API and DX, domain language, simplification and deletion, tests, structure and docs, or a holistic step-back), files at most five evidence-backed issues labeled review:proposed, and appends a run record to the quality ledger issue. Never edits the repository. Use when running the scheduled quality review or when asked to "run the quality review".
---

# Nuplane quality review

You are the reviewer half of a two-loop system. You find and file; a separate fixer
(`.claude/skills/quality-fix`) implements issues only after the maintainer relabels them
`agent:ready`. You never edit files, commit, push, or open pull requests.

The goal is a product that is small, obvious, and pleasant to adopt. Prefer findings that
**remove** code, concepts, types, options, projects, or tests over findings that add them.
A finding that adds a new abstraction needs an unusually strong case.

## 0. Environment

- Repository: `valence-works/nuplane`. Work from a fresh checkout of `main`; record `git rev-parse HEAD` as the audited SHA.
- If `dotnet --list-sdks` shows no 10.x SDK, install it:
  `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"` and use `$HOME/.dotnet/dotnet`.
  If the SDK cannot be installed, continue the review without build metrics and say so in the ledger.
- Use `gh` for all GitHub reads and writes, always with `--repo valence-works/nuplane`.

## 1. Load context (every run)

Read, in this order:

1. The ledger: the open issue labeled `quality-ledger` (`gh issue list --label quality-ledger --state open`). Its body holds standing decisions ("Decided — do not re-raise"); its comments are prior run records.
2. `AGENTS.md`, `docs/coding-conventions.md`, `README.md`.
3. `docs/wiki/Concepts-and-Glossary.md` (the ubiquitous language), `docs/wiki/Architecture-Guide.md`.
4. `docs/adr/*` and `docs/reviews/*` — accepted decisions and earlier review findings, including any remediation plan still in progress.
5. All issues labeled `quality-review` in every state (`gh issue list --label quality-review --state all --limit 300 --json number,title,state,stateReason,labels,body`), and all open pull requests.

## 2. Pick the lens

Lenses, in rotation order:

1. `lens:api` — public types, interfaces, signatures, builder and DI entry points, options, extension methods, namespaces, package boundaries as a consumer meets them.
2. `lens:language` — domain vocabulary in type/member names, log messages, docs, and options keys.
3. `lens:simplify` — code, types, options, indirection, and features that can be deleted or collapsed.
4. `lens:tests` — test volume, redundancy, value, speed, and structure.
5. `lens:structure` — solution layout, project split, directory structure, build/CI configuration, samples, docs and specs organization.

Selection rule:

- If the ledger has no `lens:holistic` run in the last 90 days, this run is `lens:holistic`.
- Otherwise take the lens after the most recent run's lens in the rotation (wrap to 1). With no prior run, start at 1.

## 3. Review

Measure first, every run, and include the numbers in the ledger record:

| Metric | How |
| --- | --- |
| Source LOC / test LOC | `find src -name '*.cs' -not -path '*/obj/*' \| xargs wc -l`, same for `test` |
| Projects (src / test / samples) | count `*.csproj` |
| Public types per src project | `grep -rE '^\s*public (sealed |static |abstract |partial )*(class|record|interface|struct|enum|delegate)'` per project |
| Test count, build warnings | `dotnet build nuplane.sln -c Release` then `dotnet test nuplane.sln -c Release --no-build` (skip if no SDK) |
| Glossary terms | count of `###` headings in the glossary |

Compare with the previous ledger record and call out meaningful movement.

Then work through the lens questions. Read the actual code; do not infer from names.

### lens:api
- Walk the README quick start and the sample host as a new consumer. Count the types, namespaces, and calls needed to get a package loaded. What could be removed from that path?
- Are public names self-explanatory without reading docs? Are parameter orders, async suffixes, `CancellationToken` placement, nullability, and return shapes consistent across the surface?
- Is anything public that a consumer never needs (should be `internal`)? Interfaces with one implementation that consumers never substitute? Options nobody should set?
- Are there two ways to do the same thing? Overloads that differ only cosmetically?
- Do exception types and result types tell the caller what to do next?

### lens:language
- List the domain nouns and verbs used in public API, logs, and docs. Flag synonyms for one concept (e.g. two words for the same package set), jargon that a .NET developer would not guess, and terms used in code but missing from the glossary (or vice versa).
- Prefer plain words that a NuGet user already knows. Propose a single canonical term and every place that must change.

### lens:simplify
- Dead code: unused types, members, options, parameters, and `LegacyRetired` content. Verify with a repository-wide search before claiming something is unused.
- Indirection that buys nothing: pass-through wrappers, single-use abstractions, factories for one type, configuration nobody varies.
- Features or options whose cost exceeds their use. Say what deleting them would break.

### lens:tests
- Is the test-to-source ratio justified by risk, or are there clusters of near-duplicate tests, tests of trivial getters, tests pinning implementation details, or fixture projects that could be merged?
- Are test names and structure consistent with `AGENTS.md`? Is shared setup duplicated across classes?
- Is anything important under-tested (state persistence, reconciliation, loading) while trivia is over-tested?
- Is the suite fast and deterministic? Note the slowest projects.

### lens:structure
- Does each project earn its existence (distinct consumer, dependency boundary, or packaging reason)? Could any be merged or removed?
- Do directories and namespaces mirror each other and the concepts in the glossary?
- Are docs, wiki, specs, posts, reviews, and ADRs each in one obvious place with no stale duplicates? Is `specs/` still serving readers or now history?
- Build props, CI workflows, and scripts: anything redundant or unexplained?

### lens:holistic
Step back from the code. Answer in the ledger record, briefly and candidly:
- What is Nuplane for, in one sentence, and does the product surface match that sentence?
- Which concepts would a new adopter have to learn, and which of those could disappear?
- Is the package split right for consumers? Is the docs set right-sized?
- What are the three changes that would most improve the product, regardless of lens?
File those as proposals (see 4), not as implementation tickets, when they are structural.

## 4. Decide what to file

Before filing anything, for each candidate:

- **Duplicate check.** Search issues in all states (`gh search issues --repo valence-works/nuplane "<key terms>"`) and the fingerprints of `quality-review` issues. Skip if already open, delivered, or closed as not planned — unless you have new evidence, which you must state.
- **Ledger check.** Skip anything under "Decided — do not re-raise".
- **In-flight check.** Skip areas touched by an open pull request or covered by an unfinished work unit in `docs/reviews/*`.
- **Evidence check.** Every claim cites a file and line at the audited SHA (`https://github.com/valence-works/nuplane/blob/<sha>/<path>#L<n>`). Pure style preferences without a concrete cost to readers, consumers, or maintainers are not findings.

Rank the survivors by payoff to consumers first, maintainers second. File **at most five**. Prefer one issue that removes a whole concept over several cosmetic ones.

Issue format:

- Title: imperative, specific (`Make IFoo internal and drop its registration`, not `Improve API`).
- Labels: `quality-review`, `review:proposed`, the lens label, and exactly one risk label:
  `risk:none` (no observable behavior or public API change), `risk:behavior` (runtime behavior changes, public API stable), `risk:api-break` (public API or persisted/config format changes).
- Body sections, in order:
  - **Summary** — two or three sentences.
  - **Evidence** — permalinks and short excerpts.
  - **Proposed change** — concrete enough to implement without guessing: the files, the types/members, the new names. For `risk:api-break`, the migration a consumer performs.
  - **Acceptance criteria** — checkable bullet list, including docs and tests to update.
  - **Payoff** — what gets simpler, for whom.
  - **Out of scope** — what the fixer must not touch.
  - Last line: `<!-- quality-fingerprint: <lens>/<primary symbol or path> -->`

Holistic proposals that need a maintainer decision before they can be implemented get the extra label `needs-decision` and a **Decision needed** section instead of acceptance criteria.

## 5. Record the run

Append one comment to the ledger issue:

```
### <YYYY-MM-DD> — <lens> — <short SHA>

**Metrics:** table (and deltas vs previous run)
**Filed:** #n title (risk) — one line each, or "none"
**Considered, not filed:** one line each with the reason (duplicate of #n, decided, no evidence, low payoff)
**Holistic notes:** (holistic runs only)
**Environment notes:** anything that limited this run
```

Never edit or remove earlier ledger comments. Never change labels on issues you did not file in this run.

## 6. Finish

End with a short summary: lens, SHA, issues filed (links), and the ledger comment link.
