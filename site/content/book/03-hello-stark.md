+++
title = "3. Hello, Stark"
weight = 30
book_part = "Part I: First Contact"
book_status = "draft"
prev = "/book/02-installing-stark/"
next = "/book/04-small-tour/"
+++

# Hello, Stark

The smallest useful Stark program uses the standard library console API:

{{< stark-sample "assets/book/stdlib-samples/hello-console.stark" >}}

This is the hello-world baseline for the language: import the module you need,
call the function by its short name, and return a process status.

## Reading The Program

`import System.Console` brings the public console functions into scope. That is
why the body can call `WriteLine("Hello, World!")` instead of spelling the full
module path.

The return type is `i32[min max]`: a 32-bit signed integer with the full range
for that width. Returning `0` reports success to the shell.

Even this small example teaches the intended shape:

- imports are explicit
- IO is an ordinary standard-library call
- process status is an ordinary value

## Build It

```bash
dotnet run --project src -- site/assets/book/stdlib-samples/hello-console.stark --emit-exe -I stdlib/src -o /tmp/stark-book-hello
/tmp/stark-book-hello
```

The program prints:

```text
Hello, World!
```

and exits with code `0`.

## Next: Native Interop

The FFI example is deliberately separate from hello world. See
`examples/ffi/Ffi.stark` when you want to see Stark call a C ABI function. Hello
world should start with ordinary Stark plus the standard library, not native
interop.
