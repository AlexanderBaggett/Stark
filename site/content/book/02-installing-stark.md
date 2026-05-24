+++
title = "2. Installing Stark and Building Programs"
weight = 20
book_part = "Part I: First Contact"
book_status = "draft"
prev = "/book/01-why-stark/"
next = "/book/03-hello-stark/"
+++

# Installing Stark and Building Programs

This chapter assumes the Stark compiler executable is already installed and
available on your machine.

The website [Getting Started](/getting-started/) page tracks current
prerequisites, installation details, and first-run checks outside the book
narrative.

## Step 1: Put `stark` On Your `PATH`

Put the compiler binary in a stable directory, then add that directory to your
`PATH`. The `PATH` entry is the directory containing the compiler, not the
compiler file itself.

In the examples below, replace `./stark` with the path to the compiler binary
you downloaded or built.

On macOS, the default shell is usually `zsh`:

```bash
mkdir -p "$HOME/.local/bin"
cp ./stark "$HOME/.local/bin/stark"
chmod +x "$HOME/.local/bin/stark"
printf '\nexport PATH="$HOME/.local/bin:$PATH"\n' >> "$HOME/.zshrc"
exec zsh
```

On Linux, the default shell is often `bash`:

```bash
mkdir -p "$HOME/.local/bin"
cp ./stark "$HOME/.local/bin/stark"
chmod +x "$HOME/.local/bin/stark"
printf '\nexport PATH="$HOME/.local/bin:$PATH"\n' >> "$HOME/.bashrc"
exec bash
```

On Windows, put `stark.exe` in a stable directory such as `C:\Tools\Stark`,
then add that directory to the user `Path` from PowerShell:

```powershell
$starkBin = "C:\Tools\Stark"
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

Ask the compiler for help:

```bash
stark --help
```

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

{{< file-sample "static/reference/examples/hello/Stark.toml" "toml" >}}

From the `examples` directory:

```bash
stark build hello
stark run hello
```

The implemented project workflow supports `build`, `run`, and `test`. Test
projects use `kind = "test"` manifests and explicit `System.Testing` fact
runners.

A solution manifest collects several projects and names defaults:

{{< file-sample "static/reference/examples/Stark.solution.toml" "toml" >}}

## Step 5: Add Native Package Facts When Needed

Native-backed packages use the same manifest system. The Raylib wrapper in
`examples/raylib` is a real example: the Stark package owns its Stark root, its
native C shim, the preferred `pkg-config` discovery name, and the Linux fallback
link metadata.

{{< file-sample "static/reference/examples/raylib/Stark.toml" "toml" >}}

The normal path is `pkg-config = ["raylib"]`. The fallback section is for
systems where Raylib is built locally instead of installed through the system
package manager. The `${native.paths.raylib-src}` value is deliberately not a
hardcoded repository path; it is supplied by user-local configuration or the
example build script.

From the `examples` directory, a machine with Raylib available can build that
package by name:

```bash
stark build raylib
```

The native-package project chapter returns to this example later and walks
through the wrapper boundary in detail.

## Step 6: Let Manifests Own Build Facts

Manifests are not just convenience. They let Stark keep package boundaries,
native dependencies, and build outputs explicit. That matters for optimization
and reproducibility: a package should own its native shims and link metadata
instead of forcing downstream users to remember a long compiler command.
