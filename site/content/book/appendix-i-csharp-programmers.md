+++
title = "Appendix I: Stark for C# Programmers"
weight = 430
book_part = "Appendices"
book_status = "draft"
prev = "/book/appendix-h-rust-programmers/"
next = "/book/appendix-j-c-programmers/"
+++

# Appendix I: Stark for C# Programmers

Stark syntax is intentionally familiar to C# programmers, but the semantics are
much more restrictive.

## Similar Instincts

- member functions use a C#-like call style
- records and object initializers should feel familiar
- lambdas use an arrow form
- generic type syntax uses angle brackets
- project metadata is separate from source files

## Major Differences

- no garbage collector
- no hidden exceptions or unwinding
- no class inheritance model
- no reflection-based programming model
- no nullable safe references
- no implicit heap allocation for ordinary values

## Modules Instead Of Namespaces

Each source file declares one `module`. Imports are explicit and wildcard
imports are forbidden.

`public` means source-visible to downstream Stark code. `export` means
ABI-visible. Do not use `export` as a replacement for C# `public`.

## Errors

Use result/status enums, `bool`, and `out` parameters for recoverable failure.
Do not model ordinary recoverable errors as exceptions.

{{< stark-sample "assets/book/samples/result-status-enum.stark" >}}

A C# API might throw for divide-by-zero or return a nullable result. Stark code
should make the recoverable cases part of the function's ordinary type shape.
The caller uses `switch` to handle each visible case.

## Allocation Habit Shift

Do not assume that familiar syntax implies managed allocation. A Stark record,
struct, array, or text value has an explicit storage story. If an operation
allocates, the API should make that visible through owned return types,
allocator-aware constructors, or result/status values.

## Interfaces And Generics

Stark traits are compile-time contracts and do not create runtime interface
objects. Generic code is static and use-site instantiated.

If a design depends on runtime interface dispatch, redesign it around static
calls, explicit `fnptr` values, or a future feature.
