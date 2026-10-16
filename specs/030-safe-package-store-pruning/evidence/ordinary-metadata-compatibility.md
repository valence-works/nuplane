# Ordinary metadata reader compatibility

The [selected support policy](pruning-support-policy.md) preserves ordinary Nuplane behavior. Default metadata reads use the released BCL path and parser, including missing-file diagnostics, relative paths and install aliases. Incidental coordination data does not select participation or establish Unenrolled authority. Explicit scoped borrow and graph-use lease readers retain their native ownership checks; guarded unscoped reads remain available internally for their existing refusal tests.

Capability and Loading consumers without a borrow or graph lease retain ordinary metadata behavior. A real scoped capability regression separately proves that admission refusal is propagated without memoizing absence: after removing the test-owned conflicting authority, the same live owner and correlation retry the native read successfully. Participant service isolation remains a separate prerequisite; this correction does not accept T100/T101.

Root reviewed the implementation, final consumer tests and actual qualification results. Local net10 qualification passed 31 exact native metadata cases, 34 affected Runtime cases, 13 Loading cases, eight exact scoped acquisition/capability cases and both ordinary consumer regressions without skips. Compiling the former mandatory-native default makes both selected ordinary metadata regressions fail; restoring the source byte-exact makes both pass. The stale exploratory no-borrow refusal expectation remains recorded as a failed exploratory run and is not accepted evidence.

Hosted qualification of the combined metadata and resolution corrections is pending. The accepted `04dcfd4` run qualifies the preceding source only. Recursive deletion and public maintenance execution remain disabled.
