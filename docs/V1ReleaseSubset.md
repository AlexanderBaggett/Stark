# `v1.0` Release Subset

This document defines the minimum Stark surface that must be stable for the first `1.0` release.

It is intentionally narrower than the full language reference. A feature belongs in the `v1.0` subset only when it is already exercised end-to-end by the current compiler, examples, standard-library package flow, and test suite on the primary release platform.

For current unsupported or partial areas, see [UnsupportedFeatures.md](./UnsupportedFeatures.md). For the active release checklist, see [Roadmap.md](./Roadmap.md). For the first release-line standard-library baseline, see [StandardLibraryBaseline.md](./StandardLibraryBaseline.md).

## Release Rule

- Included in `v1.0` means the feature is release-blocking. Regressions in parsing, typing, lowering, diagnostics, code generation, or standard-library behavior are `1.0` blockers.
- Out of `v1.0` means the feature is not part of the first release promise. It may already parse, type-check, or partially lower, but it is not required to ship `1.0`.

## Feature Inclusion Matrix

| Area | `v1.0` status | Included surface | Out of `v1.0` |
| --- | --- | --- | --- |
| Module system | Included | One module per file, `import`, `export import`, multi-file source builds, manifest-backed package imports | Wildcard imports and broader package-system expansion |
| Visibility | Included | Module-private, `internal`, `public`, and `export` on top-level declarations | Finer-grained member visibility rules beyond the current language model |
| Functions and effects | Included | `fn`, `finite`, `law`, `finite law`; block bodies and declaration-only forms; `inline`, `noinline`, `inlinehint`, `hot`, `cold`, `ffi`, `strictfp` | Additional effect-system expansion beyond the current frontend-derived guarantees |
| Data declarations | Included | `struct`, `record`, `enum`, destructor blocks, current object/enum construction forms that already lower | Future representation controls and unsupported construction shapes listed in [UnsupportedFeatures.md](./UnsupportedFeatures.md) |
| Compile-time abstraction surface | Out of `v1.0` | None required for the first release | Type aliases, generic instantiation strategy, runtime trait/doctrine features, constrained generics, specialization |
| Core types | Included | `bool`, `iN`, `fN`, range-constrained integers, `ascii`, `unicode`, `Ascii`, `Unicode`, fixed arrays, slices, raw pointers, current borrow qualifiers | Single-element text indexing and general runtime `ascii`/`unicode` conversion |
| Globals and local storage | Included | Top-level `const`, `static`, `static mut`; local `stack`, `register`, and `heap` storage | Local `arena` and local `static` code generation |
| Ownership and lifetime rules | Included | Move semantics, automatic drop, borrow/`retborrow`/`storeborrow`, non-null safe borrows, raw-pointer nullability, borrow-liveness enforcement | Escape hatches that weaken ownership guarantees |
| Control flow | Included | Blocks, local declarations, `if`/`else`, `while` with explicit loop behavior, `return`, `break`, `continue`, current lowerable `switch` subset | `for` loops and richer switch shapes outside the current lowered subset |
| Expressions and operators | Included | Current lowered scalar arithmetic, comparisons, assignments, aggregate field access, current array/slice/indexing subset, direct calls, object initializers, enum case construction | MIR lowering gaps documented in [UnsupportedFeatures.md](./UnsupportedFeatures.md) |
| Interop and packaging | Included | `ffi`, executable/object/static-library emission, package manifests, manifest-backed Stark imports | `ffi asm` as a release requirement, broader cross-target ABI guarantees |
| CLI and diagnostics | Included | `--check`, `--emit-mir`, `--emit-ssa`, `--emit-llvm`, `--emit-obj`, `--emit-lib`, `--emit-exe`, stable unsupported-lowering diagnostics for codegen modes | Richer release tooling beyond the current CLI surface |
| Standard library baseline | Included | `System`, `System.BitOperations`, `System.Console` output, `System.IO`, `System.IO.File`, `System.IO.Path`, `System.Math`, `System.Text` current slice | Console input, richer encoding conversion, allocation-backed text convenience APIs beyond the current helpers |

## Platform And Toolchain Support Matrix

| Host / target | Toolchain expectation | `v1.0` status | Notes |
| --- | --- | --- | --- |
| `x86_64` Linux host -> `x86_64-unknown-linux-gnu` target | `.NET 10` SDK plus `clang` available on `PATH` | Primary `v1.0` release baseline | This is the current CI platform and the only platform that must be release-ready for `1.0`. |
| `x86_64` Linux host -> inspection-only flows (`--check`, `--emit-mir`, `--emit-ssa`, `--emit-llvm`) | `.NET 10` SDK; `clang` needed for host-target discovery in LLVM-backed flows | Included | This is part of the core compiler/debugging contract for `1.0`. |
| `x86_64` Linux host -> static-library/package flow | `.NET 10` SDK plus `clang` and an archiver | Included | The standard-library package build and manifest-backed import flow are part of the `1.0` baseline. |
| Linux host -> `x86_64-pc-windows-msvc` target | Cross-target codegen toolchain varies by environment | Out of `v1.0` | Compiler and stdlib coverage exists, but this is not part of the first release guarantee. |
| Any host -> `aarch64-*` targets or target-specific asm matching | Target-specific native toolchain required | Out of `v1.0` | Keep current support working when practical, but do not block `1.0` on it. |
| Windows or macOS host | Native toolchain integration varies by host | Out of `v1.0` | Host bring-up is post-`1.0` work unless separately promoted later. |

Contributor note:

- Normal compiler use needs `.NET 10` and `clang` for LLVM-backed output modes.
- Parser regeneration additionally needs `antlr4` plus a JRE, but that is a contributor workflow requirement rather than a `1.0` user-facing language feature.

## Release Blockers vs Post-Release Work

Release blockers for `1.0`:

- Keep the included feature matrix above green on the primary Linux `x86_64` baseline.
- Keep the example set working end-to-end: hello world, arithmetic, control flow, data model, multi-module imports, and static-library/package consumption.
- Keep the standard-library baseline modules buildable and usable on the primary release platform.
- Keep unsupported lowering failures explicit and stable in code generation modes instead of allowing silent fallback behavior.
- Freeze syntax, lowering, and ABI expectations for the included subset only.

Post-release work:

- Any feature already listed as out of `v1.0` in the inclusion matrix.
- Remaining unsupported MIR lowering gaps and non-baseline lowering work such as local `arena` and local `static` code generation.
- Single-element text indexing, general runtime `ascii`/`unicode` conversion, console input, and richer text or file convenience APIs.
- Type aliases, broader generics and monomorphization strategy, and runtime trait/doctrine design.
- Windows, macOS, and `aarch64` host or target release guarantees.
