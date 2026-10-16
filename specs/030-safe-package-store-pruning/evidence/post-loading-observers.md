# Coordinated post-loading notifications

The CShells source consumer exposed a root-lock conflict: its reconciliation observer refreshed a package-backed catalog while coordinated root/state ownership was still held. The catalog can inspect active installs and materialize assemblies, so declaring that observer path-independent was incorrect. The consumer also needed deferred package loading to finish before discovering features.

`IPostLoadingNuplaneObserver` is an optional scoped-observer companion. Its additional notification runs after short root/state ownership closes, deferred loading finishes and loading failures are published. Reads acquire fresh admission. Existing scoped callbacks remain under their borrowed owner; ordinary reconciliation continues its existing completion notification without duplicate delivery. Applied packages describe application success rather than guaranteed loading success.

The reconciliation driver retains acquired graph-use owners through this awaited notification and releases its own ownership in the existing cleanup path. Transferred context ownership remains tied to actual context lifetime. Requested cancellation and admission refusals propagate; ordinary observer errors keep the existing diagnostic/continuation behavior.

## Local regression proof

Debug integration tests verify loaded-state/catalog visibility, reacquisition of native root admission, matching live graph protection, the optional notification without a deferred loader, and cleanup after cancellation or admission refusal. The original four cases passed without skips; their strict manifest is `build/test-cases/post-loading-observers.json`. The added Generate-mode case is listed in that same manifest.

A separate Runtime test verifies that ordinary reconciliation receives its normal completion exactly once and no post-loading notification.

Review also found that deferred cycle completion skipped `LockFileMode.Generate` finalization. Both ordinary middleware and deferred completion now share the same success predicate. The focused integration regression passed after restoration. Removing only the deferred finalizer call made that same test fail: the lock remained schema `1.0` rather than the required `2.0`. The production source was restored byte-for-byte before the passing rebuild.

| Evidence | SHA-256 |
|---|---|
| Four focused lifecycle cases, all passed | `2f0cff43c8c7bd881e9345972a0a10c97adac2dd65ff9f2a588c9d53921f02eb` |
| Ordinary completion case, passed | `f474b27cf9ed5f75b9083ad3f51eb2ac64af1a6a800346c2069080abbb4fd380` |
| Omitted-finalizer causal control, expected failure | `a7ce4e3351eba788b9f21abcca4d5baa6b0555a75983bcdb6313b760bcc68071` |
| Restored Generate-mode case, passed | `85b2336a878d64f38bb77c9d1a0328184e8d6bf088c2cd1bb3943206770cf244` |

The retained-loading native manifest preserves all 65 previously required cases and adds five maintenance-observability cases and these five post-loading cases, for 75 required names. Final current-head native qualification, package-backed consumer acceptance and release remain separate gates. Source-consumer preflight is diagnostic preparation, not proof of published NuGet packages.
