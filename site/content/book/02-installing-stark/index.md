+++
title = "2. Installing Stark and Building Programs"
weight = 20
book_part = "Part I: First Contact"
book_status = "draft"
prev = "/book/01-why-stark/"
next = "/book/03-hello-stark/"
+++

# Installing Stark and Building Programs

Stark is distributed as a relocatable SDK archive. The compiler executable,
root `sdk.json`, bundled libraries, native payloads, licenses, and toolchain are
one installation unit. Public commands live in the archive's `bin` directory.

The website [Getting Started](/getting-started/) page tracks current
prerequisites, installation details, and first-run checks outside the book
narrative.

## Step 1: Put `stark` On Your `PATH`

Extract the complete archive into a stable directory, then add the extracted
SDK's `bin` directory to `PATH`. Do not copy the compiler binary out of the
archive: `stark` resolves the SDK root containing `sdk.json` and relative
package paths from its canonical `bin` location.

In the examples below, replace `./stark` with the path to the compiler binary
you downloaded or built.

On macOS, the default shell is usually `zsh`:

```bash
mkdir -p "$HOME/.local/stark"
tar -xzf stark-<version>-macos-arm64.tar.gz -C "$HOME/.local/stark"
printf '\nexport PATH="$HOME/.local/stark/stark-<version>-macos-arm64/bin:$PATH"\n' >> "$HOME/.zshrc"
exec zsh
```

On Linux, the default shell is often `bash`:

```bash
mkdir -p "$HOME/.local/stark"
tar -xzf stark-<version>-linux-x64.tar.gz -C "$HOME/.local/stark"
printf '\nexport PATH="$HOME/.local/stark/stark-<version>-linux-x64/bin:$PATH"\n' >> "$HOME/.bashrc"
exec bash
```

On Windows, extract the complete archive to a stable directory such as
`C:\Tools\Stark`, then add its `bin` directory to the user `Path` from
PowerShell:

```powershell
$starkBin = "C:\Tools\Stark\bin"
$current = [Environment]::GetEnvironmentVariable("Path", "User")
$parts = @($current -split ";" | Where-Object
{
    $_
})
if ($parts -notcontains $starkBin)
{
    [Environment]::SetEnvironmentVariable("Path", (($parts + $starkBin) -join ";"), "User")
}
```

Open a new terminal after changing `PATH`.

If you are working from a local compiler checkout instead of an installed
binary, use that checkout's launcher in place of `stark`.

## Step 2: Check The Compiler

Validate the compiler, SDK packages, toolchain, and host prerequisites:

```bash
stark doctor
```

Then ask for command help with `stark --help`.

## Step 3: Compile One File Directly

The low-level compiler command accepts a Stark source file. With no workflow
flag, it builds an executable when the root source exports `main`; otherwise it
builds a library/package artifact. Common explicit modes are:

- `--check`
- `--emit-lib`
- `--emit-llvm`

Example:

```bash
stark examples/arithmetic/Arithmetic.stark -o /tmp/stark-arithmetic
/tmp/stark-arithmetic
```

## Step 4: Build Through A Project Manifest

The project driver uses `Stark.toml` and `Stark.solution.toml`.

The smallest executable project manifest looks like the checked-in hello
example:

{{< file-sample "samples/hello/Stark.toml" "toml" >}}

From the `examples` directory:

```bash
stark build hello
stark run hello
```

The implemented project workflow supports `build`, `run`, and `test`. Test
projects use `kind = "test"` manifests and explicit `System.Testing` fact
runners.

A solution manifest collects several projects and names defaults:

{{< file-sample "samples/Stark.solution.toml" "toml" >}}

## Step 5: Use Official Vendor Packages Directly

Official `System.*` and `Vendor.*` modules belong to the SDK, not application
dependencies. An application can `import Vendor.Raylib` without adding Raylib
to `[dependencies]`, configuring `STARK_PATH`, or repeating native linker facts.
The package owns its archive, runtime files, and ordered link metadata.

The application manifest remains an ordinary executable manifest with no
Raylib dependency entry; the source selects the SDK package by importing it:

```stark
import Vendor.Raylib
module Game
```

`pkg-config`, source paths, and custom native fallback tables remain package
author tools for non-SDK native packages; they are not normal installation
steps for an official vendor package.

## Step 6: Let Manifests Own Build Facts

Manifests are not just convenience. They let Stark keep package boundaries,
native dependencies, and build outputs explicit. That matters for optimization
and reproducibility: a package should own its native shims and link metadata
instead of forcing downstream users to remember a long compiler command.
