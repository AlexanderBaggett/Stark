+++
title = "3. Hello, Stark"
weight = 30
book_part = "Part I: First Contact"
book_status = "draft"
prev = "/book/02-installing-stark/"
next = "/book/04-small-tour/"
+++

# Hello, Stark

The smallest useful Stark program starts with an entrypoint that returns a
process status:

{{< stark-sample "assets/book/samples/hello-return-code.stark" >}}

The first IO version adds the standard library console API:

{{< stark-sample "assets/book/stdlib-samples/hello-console.stark" >}}

This is the hello-world baseline for the language: import the module you need,
call the function by its short name, and return a process status.

## Step 1: Read The Smallest Useful Program

Both hello programs write `export fn main` because they are executables.
Declarations without a visibility keyword are module-private, which is right
for helpers inside a module but not for the hosted process entrypoint. `main`
is safe Stark code here; `export` only makes the entrypoint visible as the
native symbol the linker and runtime expect.

`import System.Console` brings the public console functions into scope. That is
why the body can call `WriteLine("Hello, World!")` instead of spelling the full
module path.

The return type is `i32[min max]`: a 32-bit signed integer with the full range
for that width. Returning `0` reports success to the shell.

Even this small example teaches the intended shape:

- imports are explicit
- IO is an ordinary standard-library call
- process status is an ordinary value

## Step 2: Create One Source File

Start with one file so the first compile has as little ceremony as possible:

```bash
mkdir hello-workspace
cd hello-workspace
```

Create `hello.stark`:

```stark
import System.Console
module Hello

export fn i32[min max] main()
{
    WriteLine("Hello, World!");
    return 0;
}
```

## Step 3: Compile And Run It Directly

For a tiny prototype, the direct compiler command is enough:

```bash
stark hello.stark
./hello
```

The equivalent run command for a Windows target uses the normal `.exe` name:

```powershell
stark hello.stark
.\hello.exe
```

The program prints:

```text
Hello, World!
```

and exits with code `0`.

With no workflow flag, Stark looks at the root source. A file that exports a
body-backed top-level `main` is an executable, so `hello.stark` becomes `hello`
on Unix-like systems and `hello.exe` for Windows targets. A file without an
exported `main` is built as a library/package instead. Use `--emit-exe` or
`--emit-lib` only when you want to force that workflow explicitly.

Use `-o` only when you want a different executable name or location:

```bash
stark hello.stark -o build/greet
```

These direct commands assume your Stark installation can already find the
standard library. In a source checkout, add a search path such as
`-I stdlib/src` or set `STARK_PATH` before compiling standard-library examples.

## Step 4: Turn It Into A Project

Most Stark programs should not stay as loose one-file commands. A project gives
the compiler one place to find the root source file, output name, optimization
profile, and package dependencies.

Move the source file into a project directory:

```bash
mkdir hello
mv hello.stark hello/hello.stark
```

Then create `hello/Stark.toml`:

```toml
[project]
name = "hello"
version = "0.1.0"
kind = "executable"

[executable]
root = "hello.stark"
output = "hello"

[dependencies]
stdlib = { path = "../stdlib" }
```

This manifest assumes the standard library package lives at
`hello-workspace/stdlib`. The `stdlib` path is relative to `hello/Stark.toml`.
If your installation puts the package somewhere else, change that path once in
the manifest instead of adding `-I` flags to every build command.

## Step 5: Build And Run The Project

From the workspace root, enter the project directory and run the project
commands:

```bash
cd hello
stark build
stark run
```

`stark build` writes the executable under the project build directory, and
`stark run` builds it if needed and starts it.

## Step 6: Put It In A Solution

A solution is a workspace file for one or more projects. Go back to the
workspace root and create `Stark.solution.toml`:

```bash
cd ..
```

```toml
[solution]
name = "HelloWorkspace"
members = ["hello"]

[defaults]
build = ["hello"]
run = "hello"

[aliases]
app = "hello"
```

Now build and run from the solution root:

```bash
stark build
stark run
```

The default build set contains `hello`, and the default run target is `hello`.
You can also name the project or alias explicitly:

```bash
stark build hello
stark run app
```

This is the workflow you should expect to use for normal programs: project
manifests describe how each package builds, and solution manifests describe
which projects belong to the workspace.

## Step 7: Call A Native Function

The same workspace can hold a tiny native interop project. This one uses the
C runtime `abs` function.

This example is for hosted desktop targets where the selected native toolchain
links the platform C runtime: Linux with its C runtime, macOS with `libSystem`,
or Windows with the UCRT/MSVCRT toolchain. It is not a freestanding/no-libc
example and it does not show a third-party native library yet.

Create `native-abs/Ffi.stark`:

```bash
mkdir native-abs
```

```stark
module FfiExample

unsafe ffi fn i32[min max] abs(i32[min max] value);

fn i32[min max] DistanceFromZero(i32[min max] value)
{
    unsafe
    {
        return abs(value);
    }
}

export fn i32[min max] main()
{
    if (DistanceFromZero(-7) != 7)
    {
        return 1;
    }

    return 0;
}
```

Create `native-abs/Stark.toml`:

```toml
[project]
name = "native-abs"
version = "0.1.0"
kind = "executable"

[executable]
root = "Ffi.stark"
output = "native-abs"
```

Then add the project to `Stark.solution.toml`:

```toml
[solution]
name = "HelloWorkspace"
members = ["hello", "native-abs"]

[defaults]
build = ["hello", "native-abs"]
run = "hello"

[aliases]
app = "hello"
native = "native-abs"
```

Build and run it from the solution root:

```bash
stark build native
stark run native
```

The same project commands are used on Windows; the built executable just gets
the normal `.exe` suffix.

`unsafe ffi fn` says the body lives outside Stark. The `unsafe` block is kept
small around the foreign call, while `main` stays safe because it only calls the
checked Stark wrapper.
