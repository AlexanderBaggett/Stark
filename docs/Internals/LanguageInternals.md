# Stark Language Internals

This document describes compiler-facing and backend-facing implementation details for Stark.
For syntax, source rules, and the user-facing language contract, see [LanguageReference.md](../Userfacing/LanguageReference.md).

The goal of this document is explanatory rather than normative.
If there is ever a conflict, the source-level contract belongs in the language reference.

## 1. Backend Relationship

Stark is intentionally stricter than mainstream systems languages in a number of places because those restrictions let the compiler prove more about a program.

The current implementation targets LLVM.
That does not make LLVM details part of the language surface, but it does explain why some Stark rules were chosen to make aliasing, control flow, effects, purity, and floating-point behavior easier to communicate precisely to the backend.

## 2. Borrowing and Emitted Facts

The borrower system is one of Stark's main sources of optimizer-facing information.
The user-facing rules live in [BorrowerSystem.md](../Userfacing/BorrowerSystem.md); this section focuses on what those rules let the compiler emit when the proof is available.

Common consequences include:

- non-escaping borrow classes can justify `captures(...)` facts and, in some cases, `returned`
- null-free and well-defined safe borrows can justify `nonnull`, `noundef`, and `dereferenceable`
- exclusive ownership, unique destinations, and proven non-overlap can justify `noalias`
- `frozen` access and proven law-like read behavior can justify `readonly` and `memory(...)`
- constrained destruction and explicit shared-state rules can justify `nounwind`, `nosync`, and `nofree`
- `finite` reasoning can justify `willreturn` and `mustprogress`
- `out` and `init` contracts can justify `initializes(...)`, writable-destination reasoning, and `dead_on_return`
- stronger slice and array qualifiers can justify `align`, better range reasoning, and more aggressive loop/vectorization facts

These are compiler outputs, not language syntax.
They are emitted only when the implementation can prove them from the source rules plus body analysis.

## 3. Internal ABI and FFI Boundaries

At the source level, `ffi` marks a foreign-facing function boundary.
Internally, the compiler treats ordinary non-`ffi` Stark calls differently from `ffi` calls.

The current implementation uses a faster internal calling convention for non-`ffi` internal Stark calls when it can.
By contrast, `ffi` boundaries preserve foreign ABI expectations and should be treated as the stable interop-facing surface.

This is an implementation detail.
It matters for code generation and interop, but it is not meant to change how ordinary Stark code is written.

## 4. Generic Instantiation and Specialization

The current compiler monomorphizes generics by default.
In practice, that means a generic function such as `Identity<i32>` is usually realized as a concrete specialized body for that exact use when a body is available.

This matches Stark's speed-first design.
The baseline plan is to prefer a concrete specialized body, not to avoid specialization.

There are a few important implementation-level variations:

- declaration-only imports may need an ABI fallback path because no body is available to specialize
- some imported helpers may use a more aggressive caller-specific cloning strategy
- `cold` and `noinline` annotations can discourage the most duplicative specialization paths

The compiler also computes a small deterministic body-complexity score for generic function bodies.
That score is only a planning hint.
It does not affect type checking, semantic correctness, or the meaning of a Stark program.

In the current implementation, this score is mainly used to decide how aggressive specialization should be beyond the normal owned concrete body path.
It is not primarily used to decide whether ordinary specialization happens at all.

## 5. Closed-World Compilation Bias

Stark is designed with a closed-world bias, and the compiler takes advantage of that.

The implementation generally assumes:

- static dispatch by default
- restrictive visibility by default
- a small set of externally visible symbols
- aggressive internalization when module and package boundaries permit it
- generic specialization as a normal tool rather than an exceptional optimization

Dynamic dispatch and open-world behavior are still possible where the language provides them, but they are treated as explicit concessions rather than the default compilation model.

## 6. Doctrines and Static Realization

`doctrine` declarations are compile-time-only and do not have a runtime representation.
That makes them a natural fit for Stark's static dispatch and closed-world specialization model.

This is useful compiler context, but the user-facing rules for writing doctrines remain part of [LanguageReference.md](../Userfacing/LanguageReference.md).
