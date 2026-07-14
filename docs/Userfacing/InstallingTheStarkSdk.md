# Installing the Stark SDK

Stark releases are target-specific, relocatable SDK archives. Keep the
extracted archive together: `bin/stark` (or `bin/stark.exe`), the root
`sdk.json`, standard library, official vendor packages, native payloads,
licenses, and bundled toolchain are one installation unit. Copying only the
command is not a supported installation.

## Install

1. Download the archive for your host and target.
2. Extract it into a stable directory.
3. Add the extracted SDK's `bin` directory—the directory containing `stark` or
   `stark.exe`—to `PATH`.
4. Open a new terminal and run:

```text
stark doctor
```

`doctor` prints the selected SDK root, SDK kind and version, target facts,
package integrity, toolchain paths, and any platform prerequisite that cannot
be redistributed. Moving the complete extracted directory is supported; update
the `bin` entry in `PATH` and run `stark doctor` again.

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
