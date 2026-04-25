+++
title = "Stark"
+++

# Stark

Stark is a restrictive, performance-focused language targeting LLVM. The
language keeps the surface area intentionally small so the compiler can make
strong optimization guarantees from ordinary safe code.

The current compiler can build native executables, package-image-backed
libraries, multi-module programs, standard-library examples, and native-backed
packages. The language is intentionally stricter than Rust in places where
extra restrictions buy clearer ownership, aliasing, allocation, and backend
facts.

This site collects the public documentation surface while the repository
Markdown files remain the source of truth.
