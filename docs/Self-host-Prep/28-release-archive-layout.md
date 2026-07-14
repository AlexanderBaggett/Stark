# Release Archive Layout

Status: accepted conventional v1 layout for self-host-prep release packaging.

This document defines the archive shape that release assembly, `stark doctor`,
stdlib discovery, vendor discovery, and clean-machine smoke tests should target.
It is a layout contract, not proof that the release workflow already builds the
archive this way.

## Archive Naming

Release assets use one archive per supported host/runtime:

```text
stark-<version>-linux-x64.tar.gz
stark-<version>-windows-x64.zip
stark-<version>-macos-arm64.tar.gz
```

Optional `macos-x64` and `linux-arm64` archives may be added after the primary
matrix is working. The unpacked root directory uses the same base name without
the archive extension.

## Root Layout

The v1 commands live under `bin/`. Users add that directory to `PATH`; the SDK
manifest, bundled libraries, toolchain, and release metadata remain at the SDK
root. The compiler resolves its canonical executable path and selects the
manifest at the parent SDK root, so the layout stays relocatable and works when
the command is reached through a symlink.

```text
stark-<version>-<asset-suffix>/
  bin/
    stark[.exe]
    <compiler runtime support files>
  sdk.json
  release.json
  stdlib/
    Stark.toml
    src/
    templates/
    dist/<sdk-target>/
      System.starkpkg
      libSystem.a | System.lib
  vendor/
    README.md
    Stark.toml
    src/
    native/
    dist/<sdk-target>/
      libVendorRaylib.starkpkg
      libVendorRaylib.a | VendorRaylib.lib
      native/raylib/...
    licenses/
  toolchain/
    llvm-22.1.8/
      bin/
      lib/
      include/
      share/
      licenses/
      provenance/
      manifest.json
  licenses/
    Stark/
    LLVM/
    vendor/
  docs/
  INSTALL.md
  RELEASE.txt
```

For the retained C# Stage0 compiler, `<compiler runtime support files>` includes
the self-contained publish output needed by the executable. A self-hosted
release instead carries the native self-hosted compiler binary and any support
files it actually needs. The release archive normalizes the command name to
`stark` or `stark.exe` regardless of the project file name used to build
Stage0.

Do not require `STARK_HOME` for normal archive use. `STARK_PATH`,
`STARK_TOOLCHAIN_DIR`, `--toolchain-dir`, linker overrides, archiver overrides,
`--llvm-lib`, `STARK_CLANG`, `STARK_LINKER`, `STARK_ARCHIVER`,
`STARK_LLVM_LIB`, and native dependency overrides remain developer/user escape
hatches, not ordinary installation requirements.

`sdk.json` is the runtime identity and resolution contract. The compiler uses
its exact module ownership index and relative package/native paths; it does not
scan `stdlib/`, `vendor/`, the application repository, or parent directories to
guess an installed SDK. `release.json` records release provenance and archive
contents separately.

## Required Runtime Contents

These files are required for ordinary use from a clean machine:

- `bin/stark[.exe]` plus its runtime support files under `bin/`.
- `sdk.json`, with checksums covering every required package and native file.
- `stdlib/dist/<sdk-target>/System.starkpkg` and the platform standard-library native
  artifact, such as `libSystem.a` or `System.lib`.
- `vendor/dist/<sdk-target>/` package images and complete native artifacts
  required by bundled vendor bindings.
- `toolchain/llvm-22.1.8/` files selected for the active compiler backend,
  Clang, LLD, archive tooling, Clang builtin headers, and required
  redistributable support files. The retained Stage0 textual-LLVM backend does
  not require a loadable `libLLVM`; a Stage1 release using the direct C API
  must additionally contain and validate one. In particular, the official
  LLVM 22.1.8 macOS arm64 archive contains static component libraries rather
  than `libLLVM.dylib`, so it is not sufficient for that later Stage1 gate.
- Platform SDK inputs that cannot be redistributed remain host requirements and
  must be diagnosed by `stark doctor`.

Because each archive is target-specific, its `sdk.json` advertises exactly one
target and the named `<sdk-target>` directories contain only that target's
release-profile artifacts. Do not ship a cross-target package matrix in v1
archives. The resolver follows the manifest rather than inferring target
identity from directory names.

## Included Source And Reference Contents

Release archives include source by default:

- `stdlib/src/` and `stdlib/templates/` for debugging, reference, bootstrap
  investigation, and package rebuilds.
- `vendor/src/` plus `vendor/native/` for the official vendor library, including
  Stark bindings and supporting native/shim source.
- `docs/` for offline reference when useful.

Ordinary builds should prefer bundled binary package images and native artifacts.
Source fallback is for diagnostics, rebuilds, and development, not the hot path.

## Toolchain Metadata

`toolchain/llvm-22.1.8/manifest.json` records:

- LLVM version and asset runtime identifier.
- Original official LLVM archive name or URL.
- Upstream archive checksum.
- Source archive name, URL, and checksum.
- Files copied into the Stark archive.
- Included LLVM, Clang, LLD, and runtime license files.
- Omitted optional files when the archive is trimmed.

`toolchain/llvm-22.1.8/provenance/` stores the downloaded signature and GitHub
attestation sidecars for the binary package and source archive. The toolchain
manifest supports `stark doctor`, release auditing, and future LLVM upgrades.
It does not make package managers part of Stark distribution.

## Release Metadata

`release.json` is the machine-readable archive manifest. It should include:

- schema version
- Stark release version
- compiler version
- git commit
- asset suffix and runtime identifier
- default target triple and data layout
- LLVM version
- archive kind
- paths to stdlib, vendor, toolchain, and license roots
- package-image names and checksums for bundled stdlib/vendor artifacts

`sdk.json` is independently required and contains the compiler compatibility
line, package format, structured target/ABI facts, exact module ownership,
package dependency graph, relative image/library/native/runtime/license paths,
ordered native link facts, and required file checksums.

The compiler's assembly informational version, the Stark release version in
`release.json`, and `sdkVersion` in `sdk.json` must match exactly. The release
workflow stamps the compiler at publish time, and SDK assembly rejects an
unstamped or differently stamped compiler before archive creation. The
`stark-sdk-v1` compatibility line remains a separate runtime contract.

`RELEASE.txt` is the human-readable summary generated from the same facts. It
should include the version, commit, runtime identifier, LLVM version, target
triple, and notable platform requirements.

## Install Instructions

`INSTALL.md` must be present at the archive root and cover:

- Extract the archive.
- Add `<sdk-root>/bin` to `PATH`.
- Open a new shell and run `stark doctor`.
- Compile a hello-world or check-only sample.
- macOS requirement for a locally installed Xcode or Command Line Tools SDK.
- Windows requirement for any SDK/CRT pieces the current executable-generation
  path requires.
- Linux policy that Stark-owned runtime and stdlib code is syscall-backed and
  no-libc; libc/pkg-config diagnostics apply only to selected native/vendor
  dependencies that require them.
- Optional environment variables and override flags for advanced use.

Install instructions must not recommend Homebrew, Scoop, apt, npm, or any other
package-manager installation path. Downloadable relocatable archives are the
release path.

## Release Smoke Verification

`scripts/smoke-release-archive.ps1` is the canonical archive smoke entrypoint:

```powershell
./scripts/smoke-release-archive.ps1 `
  -ArchivePath artifacts/release/stark-<version>-<asset-suffix>.tar.gz `
  -TargetTriple <target-triple>
```

The script extracts the archive outside the checkout, clears Stark-specific
environment overrides, puts only the extracted SDK's `bin` directory on
`PATH`, verifies
`stark --help` and `stark doctor`, and compiles the same target through
`--check`, `--emit-mir`, `--emit-ssa`, `--emit-llvm`, `--emit-obj`,
`--emit-lib`, and `--emit-exe`. It builds external projects that import System
and `Vendor.Raylib` without `-I`, project dependencies, `STARK_PATH`, or
`pkg-config`, performs the real native link without opening a graphical window,
moves the SDK, and repeats the builds. Use `-IsolatePath` for a stricter
container run when proving the archive does not depend on system LLVM tools.

The root-level `stark`/`stark.cmd` launcher generated by a repository build is
a bounded development compatibility form. It selects the sibling development
`sdk.json`, but release assembly must not publish that shape as an alternative
to `bin/stark[.exe]`.

See [Installing the Stark SDK](../Userfacing/InstallingTheStarkSdk.md) for the
user flow and [SDK Layout and Resolution](../Internals/SdkLayoutAndResolution.md)
for the compiler/development contract.
