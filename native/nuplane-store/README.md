# Darwin control-file creation bridge

`openat-create.c` exposes fixed-signature functions for creating a control file relative to a held directory descriptor, reading the supported APFS name profile, and enumerating a held parent's exact entry spelling for an already-open regular file or directory. The C compiler supplies the platform's variadic calling convention when calling `openat` with a mode argument. On Apple ARM64, passing that mode through an ordinary fixed managed declaration is incorrect. See [Apple's ARM64 ABI](https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms) and the [.NET runtime interop discussion](https://github.com/dotnet/runtime/issues/48752).

The bridge performs no path validation, admission, locking or deletion. Name-profile and enumeration calls use only supplied held descriptors; enumeration rechecks each inode candidate with no-follow metadata lookup and returns the stored basename, never a path to reopen. The separate directory entry point accepts actual directory link counts, while the regular-file entry point still requires one hard link. Its internal managed caller validates child components and verifies identities before and after observation. It does not make an arbitrary path authoritative.

On macOS with Xcode command-line tools and Python 3:

```sh
bash native/nuplane-store/build-darwin.sh /absolute/owned/output
bash native/nuplane-store/build-darwin.sh --verify /absolute/owned/output
```

The output is a universal `libnuplane_store_native.dylib` containing arm64 and x86_64 slices, together with a provenance manifest. Verification checks the source, build tooling, binary hashes and architecture table. No compiled binary is committed. A universal binary alone does not qualify the managed adapter on both architectures; its supported-platform guard and actual runtime tests remain authoritative.

macOS project builds compile and copy the bridge automatically. Packaging on another platform requires `NuplaneDarwinNativeDirectory` to point to the verified macOS output. Package creation fails if the native artifact is absent or mismatched. CI builds the artifact from the same candidate checkout before packing. The package carries the native library in both Darwin RID directories; package consumers do not need a C compiler or Python.
