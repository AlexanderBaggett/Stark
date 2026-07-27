# Installing the Stark SDK

Stark releases are target-specific, relocatable SDK archives. Keep the
extracted archive together: `bin/stark` (or `bin/stark.exe`), the root
`sdk.json`, standard library, official vendor packages, native payloads,
licenses, and compiler-private backend are one installation unit. Copying only
the command is not a supported installation.

## Install

1. Download the archive for your host and target.
2. Extract it into a stable directory.
3. Run the optional `install.sh` (macOS/Linux) or `install.ps1` (Windows), or
   keep the extracted directory in place and follow the manual PATH step below.
4. For manual installation, add the extracted SDK's `bin` directory—the
   directory containing `stark` or `stark.exe`—to `PATH`.
5. Open a new terminal and run:

```text
stark doctor --strict
```

`doctor` prints the selected SDK root, SDK kind and version, target facts,
package integrity, private backend paths, and any platform prerequisite that
is intentionally supplied by the host. Moving the complete extracted directory
is supported; update the `bin` entry in `PATH` and run `stark doctor --strict`
again. Plain
`stark doctor` is useful for an informational report; `--strict` is the install
and release-integrity check.

Like an Odin distribution, the Stark archive carries the compiler, the
compiler's private LLVM backend dependency, the System library, and the complete
official Vendor collection advertised for that target. Stage0's backend may be
a trimmed private Clang runtime; Stage1's is its qualified libLLVM runtime. The
archive does not contain or expose a complete LLVM C/C++ development kit.

Native executable linking also uses the host platform's normal development
surface: Xcode Command Line Tools/full Xcode on macOS, the supported MSVC/
Windows SDK components on Windows, and a supported Clang/native development
environment plus documented system ABI libraries on Linux. The installer and
`stark doctor` detect these prerequisites and provide the supported platform-
specific remediation. They are shared host inputs, not per-project or per-
Vendor setup.

Do not set `STARK_PATH`, add library search flags, or install a separate copy of
an official vendor library for ordinary SDK use.

## System and official vendor imports

The active SDK owns `System.*` and the official `Vendor.*` namespace. These
imports are not ordinary project dependencies:

```stark
import System.Console
import Vendor.Raylib
module Game
```

Do not add the standard library or `Vendor.Raylib` to `[dependencies]` in
`Stark.toml`. The compiler reads `sdk.json`, resolves the exact owning package,
loads its full package image, and carries its archive and native link facts into
the final link. Unused vendor packages are neither loaded nor linked.

The `bin` directory is only the public command/runtime directory. Bundled native
libraries are package-owned files under paths such as
`vendor/dist/<sdk-target>/native/raylib/`, not ambient libraries copied beside
the compiler. Their paths are relative to the SDK root and checksummed in
`sdk.json`, so moving the complete SDK remains supported.

Ordinary path dependencies remain explicit in `[dependencies]`:

```toml
[dependencies]
game-core = { path = "../game-core" }
```

## Advanced SDK selection

Normal installation needs only `PATH`. The following overrides are for compiler
development, bootstrap, CI, or testing another SDK:

```text
stark build --sdk-root /path/to/sdk
STARK_SDK_ROOT=/path/to/sdk stark build
```

`--sdk-root` wins over `STARK_SDK_ROOT`; both win over bounded discovery from
the active compiler executable. An installed `<sdk-root>/bin/stark` selects
`<sdk-root>/sdk.json`. The repository's generated root launcher remains a
development-only compatibility form and selects the sibling development
manifest. Project ancestors are never searched for an installed SDK.
`STARK_PATH` is a low-level direct-compiler source search escape hatch and is
intentionally ignored by project commands.

Changing the selected SDK root or the contents of `sdk.json` invalidates project
incremental stamps and dependency LLVM cache entries, preventing outputs from a
different SDK from being reused.

## Vendor troubleshooting

An installed official `Vendor.*` package never falls back to `pkg-config` or a
machine-local library. If `stark build` reports that an official package needs
native package metadata, first confirm that `command -v stark` (macOS/Linux) or
`Get-Command stark` (PowerShell) names the intended SDK and run:

```text
stark doctor --strict
stark clean
stark build
```

A missing package, native archive, or checksum mismatch is an SDK installation
problem; replace or re-extract the complete target archive. Do not work around
it with `STARK_PATH`, `PKG_CONFIG_PATH`, `-I`, or `-L`. If native calls link but
small C struct values are corrupt—for example, a Raylib color is red-only or
transparent—the SDK contains ABI-incompatible compiler/package artifacts and
must be replaced as a complete unit.
