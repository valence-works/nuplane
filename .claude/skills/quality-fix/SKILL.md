---
name: quality-fix
description: Recurring fixer for Nuplane. Takes the oldest open issue labeled agent:ready, implements it on a branch, verifies the full build and test gate, self-reviews, opens a PR, waits for CI, squash-merges, and closes the issue with evidence. One issue per run. Use when running the scheduled quality fixer or when asked to "run the quality fixer".
---

# Nuplane quality fixer

You are the fixer half of a two-loop system. The reviewer (`.claude/skills/quality-review`) files
issues as `review:proposed`; the maintainer promotes the ones they want to `agent:ready`. Any issue
the maintainer labels `agent:ready` is in scope, whoever filed it.

You deliver **one issue per run**, end to end, and merge it yourself when — and only when — the gate
below passes. Every push to `main` publishes a preview package, so a bad merge ships. When in doubt,
stop and hand back to the maintainer rather than merge.

## 0. Environment

- Repository `valence-works/nuplane`; use `gh ... --repo valence-works/nuplane` throughout.
- If `dotnet --list-sdks` shows no 10.x SDK, install it:
  `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"`,
  then `export PATH="$HOME/.dotnet:$PATH"`. If it cannot be installed, stop without claiming an issue.
- Read `AGENTS.md` and `docs/coding-conventions.md` before writing code. They are binding.

## 1. Resume or pick

1. **Resume first.** If an open PR exists with label `agent:fix`, finish that one (go to step 5 or 6 as appropriate) instead of starting new work.
2. Otherwise list candidates: open issues labeled `agent:ready` and not `agent:in-progress`, `agent:blocked`, or `needs-decision`, oldest first.
3. None → end the run with "No agent:ready issues."
4. Claim the oldest: add `agent:in-progress`, remove `agent:ready`, comment `Picked up by the quality fixer.`

## 2. Understand

- Read the issue and all comments. The issue's **Proposed change**, **Acceptance criteria**, and **Out of scope** define the work. Maintainer comments override the issue body.
- Confirm the problem still exists on current `main`. If it is already fixed or no longer applies, comment with evidence (SHA, file:line), close as not planned, remove `agent:in-progress`, and end.
- If the issue is ambiguous enough that two reasonable implementations would differ in public API or behavior, do not guess: go to **Hand back**.

## 3. Implement

- Branch from `origin/main`: `quality/<issue-number>-<short-slug>`.
- Touch only what the issue requires. No drive-by refactors.
- Behavior changes: write or adjust the test first and see it fail.
- Public API changes: update XML docs, `README.md`, `docs/wiki/`, and samples that reference the changed API in the same PR.
- Renames: search the whole repository, including docs, samples, specs, config keys, and log messages.
- Deletions: prove with a repository-wide search that nothing references the removed code.

## 4. Gate (all must pass)

```bash
dotnet restore nuplane.sln
dotnet build nuplane.sln --configuration Release --no-restore   # 0 warnings, 0 errors
dotnet test nuplane.sln --configuration Release --no-restore --no-build
./build/validate-secrets.sh
```

Record warnings, per-project test counts, and totals. A failing or newly skipped test is a blocker even if it looks unrelated — investigate it; if it is pre-existing on `main`, prove that by running it on `origin/main` and say so.

## 5. Self-review, then PR

- Review your own diff on two axes before opening the PR. If the Agent tool is available, use two fresh subagents in parallel; otherwise do both passes yourself, deliberately:
  - **Standards:** does the diff follow `AGENTS.md` and `docs/coding-conventions.md`, and avoid unnecessary code?
  - **Spec:** does it satisfy every acceptance criterion, and nothing beyond the issue's scope?
- Fix every must-fix finding and re-run the gate. At most two review rounds; still failing → **Hand back**.
- Commit with a message whose last line is `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Push and open the PR with label `agent:fix`. Never use a GitHub closing keyword; write `Refs #<n>`. PR body:
  - **Summary** and `Refs #<n>`
  - **Changes** — bullet list
  - **Breaking changes** — required for `risk:api-break`: what changed and the consumer migration
  - **Verification** — gate commands with warnings and test counts
  - last line: `🤖 Generated with [Claude Code](https://claude.com/claude-code)`

## 6. Merge gate

Check immediately before merging; re-check after any push:

1. `gh pr checks <pr>` — poll every 30 s for at most 30 minutes. No `PENDING`, no `FAILURE`/`CANCELLED`. On failure, read the log (`gh run view <id> --log-failed`), fix if the diff caused it, re-run the gate, push, and poll again (at most two fix attempts).
2. No unresolved review threads on the PR.
3. `gh pr view <pr> --json mergeable,mergeStateStatus` is `MERGEABLE` and not `BEHIND`/`DIRTY`. If behind, rebase on `origin/main`, re-run the gate, `git push --force-with-lease`, and go back to 1.

Then: `gh pr merge <pr> --squash --delete-branch` and confirm `state == MERGED`.

## 7. Close out

- Comment on the PR with the gate evidence (merge SHA, warnings, per-project test counts).
- Close the issue as completed with a comment naming the PR and merge SHA; remove `agent:in-progress`.
- If the change is `risk:api-break`, add to the issue comment: "Downstream consumers pinning Nuplane may need a pin bump."
- End with a short summary: issue, PR, merge SHA.

## Hand back

When you stop without merging: push the branch if it has useful work, open or keep a **draft** PR if
so, comment on the issue with what you did, what blocked you, and the specific question for the
maintainer; swap `agent:in-progress` for `agent:blocked`. Never merge to get past a blocker, never
weaken or delete a test to make the gate pass, and never resolve a review thread you did not address.
