# Standard Library Baseline

This document defines the versioned Stark standard-library baseline for the first `v1.0` release line.

It is intentionally narrower than every internal stdlib source file present in the repository. Only the public baseline below is part of the first release promise.

For the language-side release cut line, see [V1ReleaseSubset.md](../Userfacing/V1ReleaseSubset.md). For unsupported or incomplete areas, see [UnsupportedFeatures.md](../Userfacing/UnsupportedFeatures.md).

## Versioning Scheme

The release-line versioning scheme is:

- compiler and bundled standard-library releases use the same `MAJOR.MINOR.PATCH` tag, for example `v1.0.0`
- `PATCH` releases are for bug fixes, diagnostics, packaging fixes, and implementation corrections that preserve the documented `v1.0` source and ABI expectations
- `MINOR` releases may add backward-compatible surface area after `v1.0`, but must not break the documented `v1.0.x` baseline
- `MAJOR` releases may break syntax, lowering, packaging, or standard-library compatibility and therefore reset the baseline

Current repository reality:

- the generated `.starkpkg.json` manifest does not yet carry an embedded package-version field
- until that changes, the versioned stdlib baseline is identified by the Stark release tag and the published package artifact that accompanies that release
- the `System` package shipped with `v1.0.x` is therefore versioned by release artifact and release documentation, not by a manifest field

## `v1.0` Baseline Module List

The first release baseline includes the following public modules:

- `System`
- `System.BitOperations`
- `System.Console`
- `System.IO`
- `System.IO.File`
- `System.IO.Path`
- `System.Math`
- `System.Text`

The first release baseline does not treat the following internal implementation modules as public compatibility surface:

- `System.Runtime`
- `System.Runtime.Buffer`
- `System.Runtime.Platform`
- `System.Runtime.Platform.Linux`
- `System.Runtime.Platform.Windows`
- `System.Syscall`

Those internal modules may be reorganized, renamed, or replaced without a public compatibility promise as long as the documented public `System.*` surface remains intact.

## Compatibility Promise

Within the `v1.0.x` line, Stark promises the following for the baseline modules listed above:

- public module names remain stable
- documented public types, functions, overload groups, and enum cases remain source-compatible unless an issue is explicitly called out in release notes
- packaged consumption through the manifest-backed `System` package remains supported on the primary `x86_64` Linux baseline
- changes that fix incorrect behavior are allowed, even when they make previously buggy programs fail more explicitly

The first release baseline does not promise stability for:

- undocumented internal modules
- unsupported features listed in [UnsupportedFeatures.md](../Userfacing/UnsupportedFeatures.md)
- ABI behavior for surfaces explicitly excluded from `v1.0`, such as Stark enums across `ffi` or `export` boundaries
- future post-`v1.0` expansions that are not part of the baseline module list above

## Packaging Notes

The baseline package identity is:

- root module: `System`
- package artifact: host-appropriate static library plus sidecar `.starkpkg.json` manifest
- primary supported release platform: `x86_64` Linux as defined in [V1ReleaseSubset.md](../Userfacing/V1ReleaseSubset.md)

For `v1.0`, the versioned baseline is defined by:

1. the release tag
2. this module list
3. the public docs under `docs/StandardLibrary/`
4. the compatibility promise above
