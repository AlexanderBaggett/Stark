# Release Archive Layout

Status: accepted v1 layout for self-host-prep release packaging.

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

The v1 command lives at the archive root, not under `bin/`. Users add the
archive root to `PATH`. This keeps the active compiler distribution root simple:
`stark doctor`, bundled stdlib lookup, bundled vendor lookup, and bundled
toolchain lookup all resolve sibling directories from the compiler executable.

```text
stark-<version>-<asset-suffix>/
  stark[.exe]
  <compiler runtime support files>
  stdlib/
    Stark.toml
    src/
    templates/
    dist/
      System.starkpkg
      libSystem.a | System.lib
  vendor/
    README.md
    Stark.toml
    src/
    native/
    dist/
      *.starkpkg
      <generated native artifacts or metadata>
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
  release.json
```

For the current C# Stage0 compiler, `<compiler runtime support files>` includes
the self-contained publish output needed by the executable. After cutover, this
shrinks to the native self-hosted compiler binary and any support files it
actually needs. The release archive normalizes the command name to `stark` or
`stark.exe` regardless of the project file name used to build Stage0.

Do not require `STARK_HOME` for normal archive use. `STARK_PATH`,
`STARK_TOOLCHAIN_DIR`, `--toolchain-dir`, linker overrides, archiver overrides,
`--llvm-lib`, `STARK_CLANG`, `STARK_LINKER`, `STARK_ARCHIVER`,
`STARK_LLVM_LIB`, and native dependency overrides remain developer/user escape
hatches, not ordinary installation requirements.

## Required Runtime Contents

These files are required for ordinary use from a clean machine:

- `stark[.exe]` plus its runtime support files.
- `stdlib/dist/System.starkpkg` and the platform standard-library native
  artifact, such as `libSystem.a` or `System.lib`.
- `vendor/dist/` package images and any generated native artifacts or metadata
  required by bundled vendor bindings.
- `toolchain/llvm-22.1.8/` files selected for libLLVM, Clang, LLD, archive
  tooling, Clang builtin headers, and required redistributable support files.
- Platform SDK inputs that cannot be redistributed remain host requirements and
  must be diagnosed by `stark doctor`.

Because each archive is target-specific, `stdlib/dist/` and `vendor/dist/`
contain artifacts for that archive's target/profile only. Do not ship a
cross-target package matrix in v1 archives.

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

`RELEASE.txt` is the human-readable summary generated from the same facts. It
should include the version, commit, runtime identifier, LLVM version, target
triple, and notable platform requirements.

## Install Instructions

`INSTALL.md` must be present at the archive root and cover:

- Extract the archive.
- Add the archive root directory to `PATH`.
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

The script extracts the archive to a temporary directory, clears Stark-specific
environment overrides, verifies `stark --help` and `stark doctor`, and compiles
the same target through `--check`, `--emit-mir`, `--emit-ssa`, `--emit-llvm`,
`--emit-obj`, `--emit-lib`, and `--emit-exe`. It also runs an executable using
the bundled standard library for console, math, and file IO basics, then builds
and consumes a package-owned native C shim through package metadata. Use
`-IsolatePath` for a stricter Linux container run when proving the archive does
not depend on system LLVM tools.
