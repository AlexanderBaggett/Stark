+++
title = "Benchmarks"
+++

# Benchmarks

Stark benchmarks currently live in the repository `benchmarks/` directory.
They are source-level programs used for quick performance and codegen feedback,
not yet publication-quality comparisons.

Current coverage includes:

- allocator bucket reuse and reallocation
- `System.Collections.List<T>` and `Queue<T>` growth
- owned text allocation and path joining
- compile-only text/path buffer coverage

The v1.4 benchmark milestone will turn this into a formal C/Rust comparison
suite with stable hardware notes, repeatable runs, and regression tracking.
