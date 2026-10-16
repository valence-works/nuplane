# Actual deferred packaged native-library lifetime

Test-only worker commit `55a5eeb0fc0426c5eb1cbe8c0a2a908e198e3851` is integrated
locally as `c7bb82b`. The fixture compiles a tiny owned C export into
`Shared.Dependency/runtimes/<current RID>/native` before the completion marker,
enrollment and graph protection are captured. Only Root.Second v1 declares its
P/Invoke; ordinary fixture defaults are preserved.

The test uses actual participant DI and `IScopedPackageLoader`, retains the exact
loaded marker Type, disposes the external graph-use owner and retires the old loader
context. Real reconciliation commits Root.Second v2. Coordinated state verification
proves the exact old Root.Second v1 install is absent from Active and recoverable
LKG graphs before the **first** invocation of the packaged native export.

The export returns its input-dependent expected result. The retained Type still
belongs to the exact old context; its whole-graph use remains Live and its sentinel
cannot be acquired. Ending the helper scope containing that Type permits actual collectible weak death,
followed by direct acquisition of the same native sentinel before maintenance
inspection. The subsequent inspection reports Stale; state, use-record bytes and
install evidence remain unchanged.

## Evidence and limits

Final focused attempt005 on Darwin arm64 passes **1/1**, no skips. Earlier compile
and unscoped-test-state-read failures are preserved separately; attempt004 also
passed before final helper corrections. The final compiler helper resolves Windows
`cl.exe` to an absolute path from the initialized VsDevCmd PATH and bounds compiler,
post-termination and output-drain waits. Missing compiler/toolchain fails this proof
instead of skipping it or substituting a system library.

The strict retained-Loading manifest preserves all prior50 cases and adds the exact
native-export row. At `a7a27d2`, the root combined suite passes **51/51**, zero failed
or skipped; its exact-name verifier accepts every required row once and Passed.
Independent source review verifies the merged fixture preserves both slices and
ordinary defaults. Hosted platform qualification remains pending.
The old Root.Second v1 is live-use-only after reconciliation; Shared.Dependency and
its native asset may remain protected by other Active/LKG graphs, so this evidence
does not claim that the native asset itself has only live-use protection.

Windows, Linux and Intel macOS execution remain unqualified at this checkpoint.
Existing HostIntegrated process-exit lifetime proofs remain separate; this new test
does not make an actual native call through a HostIntegrated context. Full T086
acceptance and public pruning activation remain open.

## Windows environment-capture correction

Published `fb51265` / Validate `38246344860` is terminal failure. Its Windows
retained-Loading run passed 50 cases and failed this native-export case while
draining redirected VsDevCmd environment output, before MSVC compilation or the
lifetime assertions. The reason the pipe failed to reach EOF is not established.

Worker `3bfae1ef9e22d4d3b4b7a13ce7b8066293ef7c08`, integrated as `46c9bef`,
replaces that bootstrap capture with owned environment/setup-log files. The batch
script is ASCII and receives the resolved script path through the Unicode child
environment. `cmd /u` produces the environment file; strict UTF-16LE parsing checks
encoding, duplicate names and nonempty PATH/INCLUDE/LIB before resolving absolute
`cl.exe`. Actual exit-code checks, the 30-second command limit and five-second
termination grace remain. The packaged C export and lifetime assertions are
unchanged; missing compilers still fail this proof.

Final local focused verification passes five cases without failures or skips on
Darwin arm64: four parser rows plus the real owned-C export lifetime test.
Independent source/result review confirms those exact rows and finds no bounded
blocker. Artifacts are retained under
`catalog-runtime-integration-v1/native-library-lifetime/windows-compiler/` in the
program artifact directory. This local run does not execute Windows cmd, VsDevCmd
or MSVC; fresh exact-head hosted Windows qualification remains required.

## Lifetime scope and complete owned cleanup correction

Published `9c5e4bf` / Validate `38257498196` completed with six successful jobs and
one failed Windows job. All four Unix native lanes passed the strict 51-case
retained-Loading gate and the 13-case actual-process partial recovery gate. The
Windows retained-Loading TRX contains 50 passes and one failure, with no skipped,
aborted or timed-out rows. The native-export lifetime row reports an IOException
while fixture disposal deletes a graph-use sentinel. Cleanup may have masked an
earlier assertion failure; the run does not establish its underlying cause.
Later Windows native qualification steps were skipped.

The test now confines the strong reflected Type and native invocation to a separate
non-inlined helper. The returned receipt contains a weak context reference and
immutable evidence, with no Type, Assembly or load-context reference. Weak-death
and exact sentinel-release checks execute after that helper finishes. Owned
cleanup unloads and drains every context, including the newer reconciled
generation, while the passive observer remains available, then disposes the
provider and fixture. Original assertion and cleanup failures are preserved
individually or together. No production code, timeout or strict test manifest is
changed.

On Darwin arm64, the restored focused native-export test passes once without
skips. A deliberate control retains the reflected Type in the receipt across the
weak-death check: it fails with both the original collection assertion and the
cleanup drain diagnostic. The reviewed source is restored byte-for-byte after
that control. Artifacts are retained under
`catalog-runtime-integration-v1/native-library-lifetime/windows-cleanup/`.
The affected Release/net10.0 integration run completed with **283 passed, one
failed, zero skipped**. Every exact row in retained-Loading51 and partial-recovery13
passed once. The sole failure was the unchanged source-precedence test waiting
30 seconds for a real directory-watcher probe. Machine load rose from 12.30 to
26.81 on eight cores; this is timing context, not proof of its cause. The full
suite is not accepted as green. Independent source/control review found no
bounded blocker in the lifetime correction.

A successful new Windows run and green whole CI are still required; this test
correction does not accept T086, T118 or the full pruning feature.

## Windows mapped-native cleanup correction

Validate `38262605678` completed with six successful jobs and one failed Windows
job. The Windows retained-Loading TRX has 50 passed, one failed and zero skipped
rows. The failed row is the deferred native-export test; the direct failure is
`UnauthorizedAccessException` deleting the fixture's exact
`NuplaneDeferredNativeProbe.dll` during `PackageStoreFixture.Dispose`. The test's
cleanup helper collects the body failure and every cleanup failure, throwing an
aggregate if more than one occurred. The direct fixture-delete exception therefore
shows the complete test body and earlier cleanup steps passed. The exact denial is
consistent with the native DLL remaining mapped, but the TRX alone does not identify
which runtime owner still held the module.

The lifetime owner and passive observer used a short weak reference. Short weak
references clear before finalization, while collectible `AssemblyLoadContext`
finalization initiates asynchronous unload. Microsoft documents unload as
asynchronous and uses a resurrection-tracking weak reference plus GC/finalizer waits
to detect completed unload before deleting a managed assembly file:
[How to use and debug assembly unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability).
Nuplane resolves this probe through `PackageGraphLoadContext.LoadUnmanagedDll`,
which calls `LoadUnmanagedDllFromPath`; the .NET runtime implementation of that
method calls `NativeLibrary.Load`:
[runtime `AssemblyLoadContext.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Runtime/Loader/AssemblyLoadContext.cs).
Those sources establish the asynchronous ALC lifetime and how this path loads the
native module, but do not independently guarantee when a Windows native handle stops
blocking deletion.
The production owner now uses resurrection tracking for collectible targets, and
the integration probe waits on the same completion boundary. A focused finalizer
gate regression ensures the observer cannot claim/release a collectible lease while
its target is finalizing. The unload coordinator also uses resurrection tracking,
matching its `Unloaded` contract (“fully unloaded and garbage collected”).
Noncollectible lifetime retention is unchanged.

The end-to-end test continues to delete the fixture tree directly with no retry or
suppression, so passing Windows CI remains the required native-handle proof. Local
focused results and exact source hashes are recorded under
`resume-2026-10-12/windows-native-finalize/` in the program artifact directory.
