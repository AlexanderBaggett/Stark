+++
title = "Book"
weight = 30
+++

The Stark Book is a Rust Book-sized learning path, but focused on Stark's own
language shape rather than copying Rust chapter-for-chapter.

The book will center on:

- restrictive semantics as a performance feature
- ownership, moves, deterministic drop, and reinitialization
- `borrow`, `mut borrow`, `retborrow`, `frozen`, and `out`
- how Stark borrowing compares with Rust borrowing
- inline, borrowed, and heap closures with explicit capture
- explicit storage classes such as `stack`, `heap`, `arena`, and globals
- no hidden allocation, no hidden exceptions, and no unwinding
- `fn`, `finite`, `law`, and `finite law`
- modules, visibility, project manifests, and package images
- native package metadata for FFI-backed libraries
- memory separation, bounded raw pointer regions, and independent loop contracts
- `dynamic T`, `init T`, and safe spare-capacity initialization
- standard-library ownership patterns
- reading diagnostics and inspecting generated IR

The published book is currently the v1.35 draft. Numbered chapters are written
as tutorial steps that build from source facts to projects, while appendices
stay compact reference material. See the [book changes](/book/changes/) page
for user-facing updates between published drafts. A generated
[single-file Markdown export](/book/stark-book.md) is also available for
review, indexing, and printable workflows.
