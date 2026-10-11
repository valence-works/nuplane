# Bounded inventory and pure retention planning

This increment implements non-destructive native inventory and pure retention classification. The later [fresh inspection increment](fresh-inspection.md) connects these mechanisms to all-member protection. Public maintenance operations, physical deletion and restart recovery remain incomplete. The mandatory before-delete gate remains unaccepted.

## Inventory

`IPackageStoreInventory.ReadAsync` carries the original admitted borrow into one validated-root callback. The native walk observes only the feed/package/version hierarchy and empty, single-link regular completion-marker metadata; it reads no package payload or archive bytes. The candidate identity has no verified archive hash. Fixed limits are 1,024 names per directory, 16,384 total names and three install-path components. Observation ambiguity or overflow returns an explicitly incomplete snapshot, never known empty. Collections are detached, copied and deterministically ordered.

Control `.nuplane-store`, root `.tmp`, canonical installer stage/prepared residues, incomplete installs and unknown entries remain distinct and excluded from candidates. Unknown rows may coexist with a complete bounded scan. Native links/special entries and ambiguous completion markers cannot become candidates. Existing unknown content and observed fixture package bytes remain unchanged.

Independent review found same-feed build-metadata versions were grouped as release aliases and incorrectly reported Unknown. A real Complete-admission regression failed against that implementation (expected two completed candidates, actual none). The correction preserves metadata in normalized full-version path keys and candidate identities while refusing true normalized aliases such as `1.0`/`1.0.0`. It uses the existing install-store native profile validator rather than duplicating its matrix. The eight native inventory cases now pass on macOS and both Linux architectures; Windows passes seven, excluding the Unix symlink/FIFO/hardlink fixture. T049/T056 are accepted after the hosted qualification below.

## Pure retention planner

`IPackageStoreRetentionPlanner.Plan` receives immutable root/epoch, inventory completeness, explicit Known/Unknown protection, exact positive protections and optional inactive-version retention. Missing retention keeps all. Zero retains only protected installs. N selects distinct NuGet VersionRelease groups among unprotected installs per case-insensitive package ID across feeds, retaining every exact install in each selected group, including metadata ties. Protected installs never consume this allowance. Active, recoverable LKG and live-use reasons survive uncertainty; Unknown is never converted into a proven empty set. Root/path/native/completion conflicts and missing protected inventory evidence refuse eligibility. Supplemental archive hash absence never erases exact protection.

Results are descriptive Retained/Eligible/Refused rows, with no filesystem access or deletion authority. A valid known-empty input produces a resolved empty plan. A causal control that bypassed positive protection under Unknown failed (`Expected Retained / Actual Refused`); restoring the exact planner source passed. Root added the positive known-empty regression. T050 and T057 are accepted for this pure mechanism and its tests, not public preview or execution.

## Root qualification and review

Final unchanged-input gates used manifest SHA-256 `4d1cdf1ff130291e26ee6eb73f3acfab7f0a510cca6e4d2fa0e4ea4b229bd07c` (937 source/test/build/workflow inputs):

| Gate | Result | Preserved log SHA-256 |
|---|---|---|
| Focused inventory and planner | 31 passed / 0 failed / 0 skipped | `ef542d02a4c53ab85d206f89f6cbf2034d76420d0c9e7a64c493da6ad4f3e052` |
| Complete Store suite | 677 passed / 0 failed / 41 platform skips | `804a93b82bd07ddcb922f545ec666969a6340dbbb3c326f77c89ddec98492368` |
| Core Release build, .NET8/9/10 | zero warnings/errors | `f241adcf73a00d469bf82e4721eafc856ad27de2ae01e04ecfc1d6565ca27fff` |

The focused test build has the pre-existing xUnit2031 warning in RootMembershipRecordTests; the production build has none. The new hosted step requires exactly 31 Unix or 30 Windows scenarios, excluding only the Unix-specific symlink/FIFO/hardlink fixture on Windows. Strict name/multiplicity/outcome accounting accepts the 31 actual macOS results and the corresponding 30-row structural Windows subset; twelve malformed controls are refused. That subset is accounting proof, not Windows execution. Eighteen existing embedded Python blocks compile and the new Bash step parses. Accounting report SHA-256: `6ca7058bba43a2357ad12a8b1995834b170fb34c0f0e169df88019508002f501`.

Root review and independent review found no remaining actionable finding in this bounded increment after correcting metadata grouping and deduplicating the native-profile validator. Planner independent review SHA-256: `78bfef476146255fee33e457fa50cda440663c15465a797bc59c9a5ecbe3a885`; corrected inventory independent review: `b7de51a0ae4ea2f8587cdd46480b5868fa82b44f7077f641d454ea92a26fee3d`. Independent review read pinned source/logs and did not execute tests. Root executed the combined gates above.

Inventory regression failure TRX SHA-256: `8355527b3755b25a291aba9250ca56bb02a60f1f7f61fc9e619e235a674a7133`; planner negative-control failure TRX: `d78044f7f8ed9f379c5f9a6489fd20166e280be60003abf82eeb857d1a7ba266`. Earlier failed evidence is retained separately. Root source/review checkpoint SHA-256: `8f2c072d7721d3a99fd411d3ba809f19539d0a67c218889dcef87e69855897b5`.

## Hosted inventory acceptance

Exact head `5942d5f381d75d1791f9311c2a6f3ec4fe604a28` passed [Validate 38025283296](https://github.com/valence-works/nuplane/actions/runs/38025283296). All six jobs passed, including Windows, macOS, Linux x64 and Linux ARM64. The required inventory/planner gate executed exactly 31 Unix or 30 Windows cases without skips, thereby qualifying eight/seven inventory cases and all 23 planner cases on their actual platforms. Full solution: **2,068 passed / 0 failed / 41 platform skips**. Both owned Linux ext4 casefold lanes passed eleven cases. The existing retained-loading, native-reader, protected-candidate, native-installer, scoped-acquisition and runtime/startup required gates also passed on all four platforms.

Terminal raw-log SHA-256: `229dc7fb06ef2d05c7ef63146432c2aac6d5b821c5a58b3cd97d87158478b09f`; parsed terminal/exact-case qualification: `7590b784351b20e824d689b41639daf9a1779593ec87681e65f14584f4cd0a7d`. The parser verified 28 required platform gates, all six full-solution suite results and both actual Linux casefold results. The prior failed fixture runs remain historical failures. This head does not contain the later inspection kernel, control recovery or deletion.

The accepted checklist is **37/128** after T049/T050/T056/T057. Public all-member/live-use inspection operations, optional Admin/API wiring, remaining runtime entry points, the reviewed real maintenance/before-read gate, safe deletion and recovery, upstream stable releases and Foundation adoption/e2e/main proof remain required. #108 and PR112 remain open and not merge/release ready.
