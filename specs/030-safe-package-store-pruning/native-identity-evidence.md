# Native state-slot identity evidence

This is the T024 native observation increment, not enrollment, publication, component-wise authority or deletion acceptance. T024 remains open until its complete identity integration is qualified. No runtime registration enables the pruning protocol.

## Implemented observation

The stable slot contains physical parent identity, a versioned observed name profile and the native canonical basename. The final file identity is returned separately. Observation checks a regular single-link file, reopens its canonical name beneath the held parent, and rechecks parent/file identity. A successful observation does not acknowledge or authorize a replacement.

Windows x64 on positively identified local NTFS queries the actual directory case flag. Relative native lookup applies `OBJ_CASE_INSENSITIVE` only when that flag is off and verifies it did not change across lookup. `GetFinalPathNameByHandleW` supplies only a canonical leaf candidate; the returned full path is never authority or reopened. The original supplied spelling is rechecked as well as the canonical spelling.

Darwin ARM64 APFS obtains case sensitivity from validated native volume capabilities and preserves exact enumerated UTF-8 spelling. Linux x64/ARM64 currently recognizes ext4 and observes the actual directory casefold flag. Other name profiles refuse. Both Unix implementations enumerate an independent held-parent stream and compare full native identity, type and link count; an uninspectable matching candidate refuses. Neither diagnostic paths nor managed Unicode/case folding supplies identity.

## Root review and local verification

Root reviewed the shared contracts and both native implementations. Independent source review found no remaining concrete blocker. Review corrections included native-sized Linux ioctl storage, Darwin's required volume-info attribute group, Windows supplied-name revalidation, and xUnit v2-compatible capability reporting. Test integration also corrected a missing import, numeric literal types and the owned alias fixture path. Failed attempts remain preserved alongside passing evidence.

Production builds passed for .NET 8, 9 and 10 with zero warnings/errors. The final Mac Store suite passed **141**, failed **0**, and explicitly skipped **14**: thirteen Windows-only cases and the dedicated Linux casefold case. All ten selected Unix adapter/identity cases executed and passed. These local skips are not platform acceptance.

The causal control weakened the common observer binding guard from OR to AND. Exactly two mismatch cases then failed because refusal was absent, with zero passes/skips. Root restored the source byte-for-byte and reran the normal build and suite successfully.

| Evidence | SHA-256 |
|---|---|
| All-TFM build log (`identity-core-build-v3.log`) | `3944a7bdf04586637bb6ce3812fe3eb18e3d44cd2dca4bcba9496cfab0ff36e0` |
| Final Store log (`identity-store-tests-v6.log`) | `fccb765475f7cf0c6d50f4348026544fc90bdf6c4bdf05d341e60e470818a345` |
| Final Store source-input manifest | `fcd642f6d5a9aaed272db565f11a89cf453d348fd41c64d9731fffab350ae213` |
| Expected-failure mutation log | `b2699315fa1fe6b34de997656dfd5124a757b7b187213a068ab920e74097b26b` |
| Restored common identity source | `5dc15ec9feb541eecc506a280dc19944966af6c0e886364f87d4eb0627a10cfd` |

Logs, manifests, TRX and review reports are retained under the owned delivery artifact directory `prune-admission-authorization-audit/native-filesystem-gates/`. Source inputs remained unchanged during each gate. Only the workflow filter changed between the final all-TFM build and Store run; production/native inputs are identical.

## First hosted candidate and ARM64 correction

The first candidate workflow required all ten Unix native cases without skips on macOS ARM64, Linux x64 and Linux ARM64. Both Linux lanes also create and mount their own disposable ext4 casefold image and demand the casefold alias scenario pass without skips. No host filesystem feature is changed. The mount is released on step exit.

Windows requires five state-slot scenarios to execute and pass in addition to the existing eleven adapter/parser cases. Only the 8.3-specific scenario may explicitly skip when the owned test volume does not create aliases; such a skip leaves that scenario unqualified. Parent-alias convergence tests establish physical identity only; the later component-wise authority walker remains separate.

[Validate 37922359243](https://github.com/valence-works/nuplane/actions/runs/37922359243) at exact commit `450673d331d0db316132677389e2c8c14223377e` completed with **five successful jobs and one failed Linux ARM64 job**. Windows passed all eleven adapter/parser and all six identity cases without skips, including an actual 8.3 alias. macOS and Linux x64 each passed ten native cases without skips. Linux x64's demanded ext4 casefold case also passed. Ubuntu's full solution passed 1,403 tests with fourteen explicit Windows/casefold skips in the general suite; the specialized lanes exercised those cases. The Linux package-asset check passed. This run is not overall green and does not qualify ARM64.

The ARM64 lane failed nine of ten native cases during namespace opening with native error 22; its casefold step did not run. Root traced this to incorrectly reused x64 `O_DIRECTORY`/`O_NOFOLLOW` values. [Linux ARM64 UAPI](https://github.com/torvalds/linux/blob/master/arch/arm64/include/uapi/asm/fcntl.h) overrides those flags to bits 14/15, while [generic x64 flags](https://github.com/torvalds/linux/blob/master/include/uapi/asm-generic/fcntl.h) use bits 16/17. The correction selects by process architecture and shares the directory-stream flags with enumeration and test setup. Independent review found no additional architecture-specific mismatch in the used Linux ABI.

The correction adds a direct native-open regression against directory and file symlinks, bypassing managed pre-inspection. Disabling Darwin's native no-follow flag caused that regression to fail because the link was followed; root restored the original source byte-for-byte. Its expected-failure log SHA-256 is `11a339a15f36f1f4adba215ed868f987426989bf89fc651aa4222d26432d71d9`. The workflow now requires eleven Unix native cases and the direct regression by name.

Final correction checks passed after restoration: all three production TFMs built with zero warnings/errors; Mac Store **142 passed, zero failed, fourteen explicit Windows/casefold skips**, including eleven actual Unix native cases. Build/test source-input manifests match exactly (`dd1cefeffdb0c7de8510a12a60487984d976547574dfce9fa35efb7d5335422b`). The final build log SHA-256 is `5f8cdfdb2475394fb68e3d7693096fbd74f2a04e9a7620f56c8754b0ae6cb27b`; Store log SHA-256 is `5cb77eb98706d81cbbb8d587500d199c04da804023c138dba67e8a704bd107ab`. A new hosted run remains pending; no ARM64, admission or deletion acceptance is inferred from review or the passing other lanes.
