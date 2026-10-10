# GitHub access from cloud sessions (REST only)

Claude Code cloud sessions block GitHub GraphQL, so `gh issue …`, `gh pr …`, `gh label …` and
`gh search …` fail with HTTP 403. `gh auth status` also reports an invalid token — ignore it; REST
calls through `gh api` work with full repository permissions, and `git push` works normally.

```bash
R=repos/valence-works/nuplane
```

## Issues

| Task | Command |
| --- | --- |
| List by label (oldest first) | `gh api "$R/issues?labels=agent:ready&state=open&sort=created&direction=asc&per_page=100" --jq '.[] \| select(.pull_request \| not) \| {number, title, labels: [.labels[].name]}'` |
| All issues with a label, any state | `gh api --paginate "$R/issues?labels=quality-review&state=all&per_page=100" --jq '.[] \| select(.pull_request \| not) \| {number, title, state, state_reason, body}'` |
| Search | `gh api -X GET search/issues -f q='repo:valence-works/nuplane is:issue <terms>' --jq '.items[] \| {number, title, state}'` |
| Read one, with comments | `gh api "$R/issues/<n>"` and `gh api --paginate "$R/issues/<n>/comments"` |
| Create | `gh api "$R/issues" -f title='<title>' -F body=@body.md -f 'labels[]=quality-review' -f 'labels[]=review:proposed' --jq .html_url` |
| Comment | `gh api "$R/issues/<n>/comments" -F body=@comment.md --jq .html_url` |
| Add labels | `gh api "$R/issues/<n>/labels" -f 'labels[]=agent:in-progress'` |
| Remove a label | `gh api -X DELETE "$R/issues/<n>/labels/agent:ready"` |
| Close | `gh api -X PATCH "$R/issues/<n>" -f state=closed -f state_reason=completed` (or `not_planned`) |

Write bodies to a file and pass `-F body=@file` so Markdown survives shell quoting.

## Pull requests

| Task | Command |
| --- | --- |
| Open PRs | `gh api "$R/pulls?state=open&per_page=100" --jq '.[] \| {number, title, head: .head.ref, labels: [.labels[].name]}'` |
| Files in a PR | `gh api --paginate "$R/pulls/<pr>/files" --jq '.[].filename'` |
| Create | `gh api "$R/pulls" -f title='<title>' -f head=<branch> -f base=main -F body=@pr.md -F draft=false --jq .number`; label it via `gh api "$R/issues/<pr>/labels" -f 'labels[]=agent:fix'` |
| Head SHA | `gh api "$R/pulls/<pr>" --jq .head.sha` |
| Check runs | `gh api "$R/commits/<sha>/check-runs?per_page=100" --jq '.check_runs[] \| {id, name, status, conclusion}'` |
| Failed job log | `gh api "$R/actions/jobs/<check-run-id>/logs" \| tail -150` |
| Reviews / review comments | `gh api "$R/pulls/<pr>/reviews" --jq '.[] \| {user: .user.login, state}'` and `gh api "$R/pulls/<pr>/comments" --jq '.[] \| {user: .user.login, path, line, body}'` |
| Mergeability | `gh api "$R/pulls/<pr>" --jq '{mergeable, mergeable_state}'` (`null` means GitHub is still computing — retry after 10 s) |
| Squash merge | `gh api -X PUT "$R/pulls/<pr>/merge" -f merge_method=squash -f commit_title='<PR title> (#<pr>)' --jq .sha` |
| Delete branch | `gh api -X DELETE "$R/git/refs/heads/<branch>"` |

Check-run states: pending means `status != "completed"`; failed means `conclusion` is
`failure`, `cancelled`, `timed_out`, or `action_required`; `success`, `skipped`, and `neutral` are fine.
