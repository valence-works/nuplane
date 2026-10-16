# Release build qualification

The [build/test job](https://github.com/valence-works/nuplane/actions/runs/38835166221/job/116763055383) passed on source commit `3f8f2842060423420f26587aef62bacefbf71898` in [Validate 38835166221](https://github.com/valence-works/nuplane/actions/runs/38835166221) on 2026-10-16. The Release build produced all 24 production project/framework outputs with zero warnings and zero errors. This records T125; broader platform and release acceptance is tracked separately in [platform acceptance](platform-acceptance.md).

```bash
dotnet restore nuplane.sln
dotnet build nuplane.sln --configuration Release --no-restore
```

All eight source projects built for `net8.0`, `net9.0`, and `net10.0`:

- Nuplane
- Nuplane.Abstractions
- Nuplane.Admin
- Nuplane.Admin.Api
- Nuplane.Loading
- Nuplane.Loading.Abstractions
- Nuplane.Loading.Api
- Nuplane.Sources.Directory

The job first built and downloaded the universal Darwin native shim and verified that Linux packaging retained byte-identical assets for both Darwin architectures. Production warnings-as-errors and XML documentation checks remained enabled. The build log SHA-256 is `665feea407c2ec47a746708a90f0e788874e3449af597295d13070b5cbc8b50c`. Earlier failed or cancelled candidates remain historical evidence and do not supply this result.
