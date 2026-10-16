# Serial test qualification

All six projects completed on source commit `3f8f2842060423420f26587aef62bacefbf71898` in the [build/test job](https://github.com/valence-works/nuplane/actions/runs/38835166221/job/116763055383) on 2026-10-16: **2,749 passed, zero failed, 70 explicit platform/capability skips**. Every TRX result summary is Completed with zero errors, timeouts, aborted runs or passed-but-aborted runs. The root independently read all six TRXs and verified their hashes, row counts and outcomes. This records T126; native platform acceptance is recorded separately in [platform acceptance](platform-acceptance.md).

After the Release build, the executed command was:

```bash
bash build/test-projects.sh "$RUNNER_TEMP/full-suite-results"
```

The shared validation/publication script runs each `test/*.Tests/*.csproj` serially with `--configuration Release --no-restore --no-build --blame-crash`, a project-specific TRX logger and result directory. It propagates the first failing exit code. [The result artifact](https://github.com/valence-works/nuplane/actions/runs/38835166221/artifacts/12018822980) contains these six TRXs:

| Project | Passed | Failed | Skipped | TRX SHA-256 |
|---|---:|---:|---:|---|
| Nuplane.Integration.Tests | 320 | 0 | 0 | `61d89b89540fdf5024e932b8eb187eb342f3dc50bdc29cc0ba7aa71deeebbd36` |
| Nuplane.Loading.Tests | 299 | 0 | 0 | `7591cb267ce24f8b5ca6215ff7f933cb9b3ab2f8625d7e3acf5c1ac790a732ed` |
| Nuplane.NuGet.Tests | 25 | 0 | 0 | `55c77286de8c2912d0ad55a14f53d13697f20ef2a9805b0a7f17dd5b2cc43c18` |
| Nuplane.Runtime.Tests | 984 | 0 | 2 | `feccf098b3e64c3e4a1fba3bbc42ac891427a5eb6d5d0467d8bee406a7279788` |
| Nuplane.Sources.Directory.Tests | 21 | 0 | 0 | `4da0cdfe2980ab385a7346922cec50745d249e88f4fb74bb15c4bdded006fab4` |
| Nuplane.Store.Tests | 1100 | 0 | 68 | `414acd7fc765c364d7028515b8902c105ed8c7718f8e547246d96f6abc3aeb78` |

Runtime's two skips are Windows-only source/probe cases on the Linux runner. Store's 68 skips are explicit platform/capability cases; required native cases execute separately in their designated lanes. These skips are not credited as native acceptance.

Loading completed **299/299**, including both component-native resolver modes and construction-failure ownership cases. The previous `af7d2d2` run aborted its Loading host after 293 tests and is unaccepted. Source review established a fixture double release: `NativeLibrary.Load(name, assembly, ...)` returned the ALC-owned pool handle without another OS reference, and the fixture freed it before the pool's eventual release. The fixture now leaves release to its owner. The earlier log supplied no crash stack or last-test attribution; no causal crash-control result is claimed.

The complete current run also contains each of these final regression cases exactly once and passed:

- `EnrollAsync_OrdinarySuccessfulLegacyUpdateDoesNotVerifyUnknownRecovery`
- `TwoParticipantCompositions_ReadOfflineActiveLkgWhileUnlistedThirdIsRefusedBeforeSourceCallback`
- `ParticipantLoadCrash_BetweenUsePublicationAndFirstLoaderInputIsRecoverable`
- `ParticipantLoadCrash_AtFirstManagedAssemblyMetadataReadAttemptIsRecoverable`

Actual owned-child group-pending admission covers IntentPublished, StageFlushed and StatePublished through fresh catalog-backed `AddNuplane` admission. Exact recovery or refusal preserves the intentional schema-2 runtime fence. Native strict manifests require these process cases without skips. Historical-only graphs outside the existing selected `UseLastKnownGood` set remain an explicit no-mutation refusal; no recovery-selection feature is added.

This is source/project qualification. The [package and host acceptance plan](package-and-host-acceptance.md) separately requires published archives, a fresh package-only consumer and downstream Foundation verification.
