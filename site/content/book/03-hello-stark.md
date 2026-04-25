+++
title = "3. Hello, Stark"
weight = 30
book_part = "Part I: First Contact"
book_status = "draft"
prev = "/book/02-installing-stark/"
next = "/book/04-small-tour/"
+++

# Hello, Stark

The smallest executable Stark program returns a process exit code.

{{< stark-sample "assets/book/samples/hello-return-code.stark" >}}

The `export ffi fn main` spelling is the current raw entrypoint convention.
Later hosted entrypoint forms are tracked separately on the roadmap.

## Reading The Signature

The return type is `i32[min max]`: a 32-bit signed integer with the full range
for that width.

The `export` marker means the function is visible as an ABI symbol. Ordinary
Stark APIs should prefer `public`; `export` is for entrypoints, FFI, and other
stable binary boundaries.

The `ffi fn` part is also deliberate. The current entrypoint is the raw native
boundary shape: the operating system calls a symbol with a C-compatible calling
convention, and Stark code returns a process status. There is no hidden hosted
runtime wrapper in this first program.

That makes the example plain, but it also teaches three Stark habits early:

- boundary functions are marked as boundaries
- return codes are ordinary values
- the source says which facts downstream tools may rely on

## Build It

```bash
dotnet run --project src -- site/assets/book/samples/hello-return-code.stark --emit-exe -o /tmp/stark-book-hello
/tmp/stark-book-hello
```

The program prints nothing and exits with code `0`.

For now, a non-zero return from `main` is the simplest way for a tiny Stark
program to report failure to a shell script or CI step. Later chapters use that
same convention for checked book samples.

## Console Output

The repository also has a standard-library-backed hello example at
`examples/hello.stark`. That sample imports `System` and uses
`System.Console.WriteLine`, which makes allocation, IO status, and package
consumption part of the example instead of hiding them in the language.
