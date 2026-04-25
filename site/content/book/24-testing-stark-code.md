+++
title = "24. Testing Stark Code"
weight = 240
book_part = "Part IV: The Standard Library"
book_status = "future"
prev = "/book/23-threading-tcp/"
next = "/book/25-performance-model/"
+++

# Testing Stark Code

Stark does not have test projects yet.

This chapter is reserved for the v2.0 testing work. For now, do not write book
examples that imply `stark test` exists.

## Planned Shape

The intended testing story is:

- test-project manifests
- a `System.Testing` standard-library module
- xUnit-inspired assertions
- assertion failure without hidden exceptions or stack unwinding
- `stark test`
- solution-level test sets

## Why It Is Deferred

The current repository has many C# xUnit tests for the compiler and standard
library, but those are not Stark test projects. Porting that vocabulary into
Stark needs a real standard-library testing module and a project model that can
build and run test executables predictably.

That belongs in v2.0 because it touches package manifests, standard-library
API design, CLI behavior, diagnostics, and result reporting.

## What To Use Today

Today, examples should be written as ordinary executable programs whose `main`
returns `0` on success and a non-zero code on failure:

{{< stark-sample "assets/book/samples/manual-test-executable.stark" >}}

That pattern is simple, explicit, and compatible with the current compiler and
CI workflows.

Use one return code per failed check when the program is small enough that the
code itself is the report. For larger examples, return a status enum inside the
program and collapse it to a process exit code at the boundary.

This is not a replacement for Stark-native test projects. It is the current
book-sample pattern: compile a real executable, keep the checks deterministic,
and avoid implying that assertion macros, reflection-based discovery, or
`stark test` exist today.

Until that work lands, Stark's compiler and standard library tests are still
hosted by the repository's C# test projects.
