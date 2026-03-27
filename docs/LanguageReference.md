# Stark Language Reference

This document is the consolidated reference for Stark.

It defines the source-level Stark language contract.

[Roadmap.md](./Roadmap.md) tracks milestone ordering and work sequencing. This document defines the language itself.

## 1. Design Goals

Stark is a performance-first language targeting LLVM.

The core design priorities are:

- speed and predictable low-level behavior over convenience
- explicitness over hidden work
- restrictions that enable stronger optimization
- ownership and effect rules that allow the frontend to derive strong LLVM facts
- a default closed-world model with minimal visibility

Stark is intentionally more restrictive than mainstream systems languages when that restriction helps produce better code.

## 2. Source File Structure

A Stark compilation unit has the following shape:

```stark
import Some.Module
export import Another.Module
module Current.Module

// top-level declarations
```

The rules are:

- each source file declares exactly one module
- imports appear before the module declaration
- the module declaration appears exactly once
- one source file corresponds to one module
- wildcard imports are forbidden
- `export import` is the only re-export form

## 3. Top-Level Declarations

The top-level declaration categories are:

- functions
- `struct` declarations
- `record` declarations
- `trait` declarations
- `doctrine` declarations
- type alias declarations
- global constants
- global variables

## 4. Modules and Visibility

Stark has three explicit visibility modifiers for top-level declarations:

- `internal`
- `public`
- `export`

If no visibility modifier is present, the declaration is module-private.

The meanings are:

- module-private
  Visible only inside the current module.
- `internal`
  Visible to other modules inside the same package or library.
- `public`
  Visible to downstream Stark source importing the package.
- `export`
  ABI-visible symbol intended for FFI, runtime entrypoints, or explicitly stable binary boundaries.

`public` and `export` are intentionally different:

- `public` is source visibility
- `export` is binary visibility

Visibility applies to top-level declarations, not to locals, statements, expressions, or fields.

## 5. Functions

### 5.1 Function Kinds

Stark has four source-level function kinds:

- `fn`
- `finite`
- `law`
- `finite law`

The keyword order is fixed: `finite law`, not `law finite`.

The intended meanings are:

- `fn`
  General function form.
- `finite`
  Guaranteed progress and guaranteed return.
- `law`
  Pure/read-only function form with no visible side effects.
- `finite law`
  A pure function that also guarantees progress and return.

### 5.2 Function Modifiers

The function modifiers are:

- `inline`
- `noinline`
- `inlinehint`
- `hot`
- `cold`
- `ffi`
- `strictfp`

Rules:

- `inline`, `noinline`, and `inlinehint` are mutually exclusive
- `hot` and `cold` are mutually exclusive
- non-`ffi` internal Stark functions use an internal fast calling convention
- `ffi` marks a foreign-facing function boundary and disables the default internal calling convention behavior
- `strictfp` selects strict IEEE-style floating-point semantics for the function

### 5.3 Declarations and Bodies

Function declarations may appear with either:

- a block body
- a trailing `;`

Semicolon form is used for declarations such as FFI functions or forward declarations.

## 6. Types

### 6.1 Builtin Scalar Types

The builtin scalar families are:

- `bool`
- `ascii`
- `unicode`
- integer widths written as `iN`
- floating-point widths written as `fN`

Examples:

- `i32`
- `i64`
- `f32`
- `f64`

Range-constrained integers are written as:

```stark
i32[0 255]
```

ALL Declared integers are range constrained.
It is an error to declare an integer without a range constraint.

`void` is not a first-class Stark value type. It is valid only as a function return type.

### 6.2 Aggregates and Views

The aggregate/view forms are:

- fixed arrays: `T[N]`
- slices: `T[]`
- named aggregates through `struct` and `record`

### 6.3 Type Qualifiers

The type qualifiers are:

- `borrow`
- `retborrow`
- `storeborrow`
- `frozen`
- `shared`
- `out`
- `init`
- `mut`

These qualifiers are part of the type model, not just local syntax sugar.

### 6.4 Raw Pointers

The raw pointer forms are:

- `rawptr<T>`
- `rawmutptr<T>`

Safe Stark code does not have null references or nullable borrows. Null exists only in the raw/FFI domain.

### 6.5 Generic Parameters and Type Aliases

Generic type parameters may appear on functions, `struct` declarations, `record` declarations, `trait` declarations, and `doctrine` declarations.

Generic parameters participate in name resolution, constraint checking, and type substitution.

Generic instantiation is monomorphized by default.

Specialization is permitted when a more specific implementation is available and the closed-world rules select it unambiguously.

Type aliases introduce alternate names for existing types.

A type alias does not by itself create a distinct runtime type or ABI identity.

## 7. Ownership, Borrowing, and Lifetime Rules

Stark safe code is ownership-based and does not use garbage collection.

The main rules are:

- values are owned by default
- moves transfer ownership
- moved values cannot be used again
- owned values are dropped automatically at scope exit
- safe borrows are non-owning and non-null
- raw pointers are the only null-capable pointer forms
- safe code cannot use `forget`-style escape hatches

The borrow classes are:

- `borrow T`
  Cannot be stored or returned.
- `retborrow T`
  May escape only through the return value.
- `storeborrow T`
  May be stored or otherwise escape.

The access qualifiers are:

- `frozen T`
  Deeply immutable for the lifetime of the borrow.
- `shared T`
  Explicit shared-access domain.

Destruction is intentionally restricted:

- trivial/POD-like destruction is the default
- safe destructors do not panic, synchronize, or allocate

## 8. Data Declarations

### 8.1 Structs

`struct` is the primary named aggregate form for ordinary data with associated methods and constructors.

Stark `struct` declarations do not support inheritance.

### 8.2 Records

`record` is the data-oriented named aggregate form.

Stark `record` declarations do not support inheritance.

### 8.3 Traits

`trait` declares a named behavior contract.

Traits group function requirements for a type or family of types. Traits do not imply class-style inheritance.

### 8.4 Doctrines

`doctrine` declares a compile-time-only bundle of `law` functions and related constraints.

Doctrines have the following properties:

- no owned runtime identity
- no owned data
- no heap allocation
- no environment capture
- static dispatch by default
- specialization-friendly in the closed-world model

## 9. Globals and Storage Classes

Globals come in two forms:

- `const T name = ...;`
- `<storage-class> mut? T name = ...;`

Local variables also require an explicit storage class.

The storage classes are:

- `stack`
- `heap`
- `register`
- `static`
- `arena`

The standardized allocation-backed storage classes are:

- `heap`
  Uses the default global general-purpose allocator. Safe Stark code does not manually free `heap` values; ownership and scope still govern destruction.
- `arena`
  Uses a region allocator intended for fast bump-style allocation with bulk reclamation when the lexical arena region ends.

Mutability is opt-in:

- bindings are immutable by default
- `mut` enables reassignment or mutation where the type permits it

## 10. Control Flow

The statement forms are:

- blocks
- local constant declarations
- local variable declarations
- `if` / `else`
- `switch`
- `while`
- `for`
- `return`
- `break`
- `continue`
- expression statements

### 10.1 Branch Weights

`if` and `switch` may carry an optional branch weight annotation such as `w9` or `w99`.

### 10.2 Loops

`while` and `for` require an explicit loop behavior keyword:

- `infinite`
- `non-deterministic`
- `willexit`

This is part of Stark's source-level control-flow contract, especially for `finite` functions.

### 10.3 Switch and Patterns

The switch surface includes:

- literal cases
- `default`
- `when` guards
- `case var capture`
- discard `_`

## 11. Expressions

The expression surface includes:

- literals
- identifiers and qualified names
- calls
- member access
- indexing
- object creation with `new`
- object initializers
- array initializers
- unary operators
- binary operators
- ternary conditional `?:`
- assignments and compound assignments

### 11.1 Operators

Operator families include:

- arithmetic: `+`, `-`, `*`, `/`, `%`
- bitwise: `&`, `^`, `|`
- shifts: `<<`, `>>`
- wrapping integer arithmetic: `+%`, `-%`, `*%`
- saturating integer arithmetic: `+|`, `-|`, `*|`
- comparisons: `<`, `>`, `<=`, `>=`, `==`, `!=`
- logical: `&&`, `||`, `!`
- unary bitwise not: `~`
- unary wrapping negate: `-%`
- conditional: `?:`
- assignment: `=`, `+=`, `-=`, `*=`, `+%=`, `-%=`, `*%=`, `+|=`, `-|=`, `*|=`, `/=`, `%=`, `&=`, `^=`, `|=`
- exponentiation: `**`

`^` is bitwise XOR, not exponentiation.

`**` is the exponent operator.

### 11.2 Precedence

From highest to lowest, the intended precedence is:

1. postfix: calls, indexing, member access
2. unary: unary `+`, unary `-`, `!`, `~`
3. exponentiation: `**` with right-associative parsing
4. multiplicative: `*`, `/`, `%`
5. additive: `+`, `-`
6. shifts: `<<`, `>>`
7. bitwise and: `&`
8. bitwise xor: `^`
9. bitwise or: `|`
10. equality and relational comparisons
11. logical `&&`
12. logical `||`
13. conditional `?:`
14. assignment

### 11.3 Exponentiation

Exponentiation is floating-point only.

- `**` is legal for floating-point operands
- integer exponentiation is not part of Stark
- there is no implicit integer-power form

### 11.4 Floating-Point Contract

Ordinary floating-point code uses aggressive fast-math semantics.

- ordinary floating-point code uses optimizer-friendly non-strict floating-point operations
- strict IEEE-style semantics require the explicit `strictfp` function modifier
- `strictfp` is the source-level escape hatch from Stark's default fast-math model

### 11.5 Integer Arithmetic Contract

Ordinary integer arithmetic in Stark is performance-first and intentionally strict.

- signed and unsigned overflow in ordinary arithmetic is illegal and treated as undefined behavior
- shift counts greater than or equal to the bit width of the shifted value are illegal and treated as undefined behavior
- wrapping arithmetic uses the Zig-style spellings `+%`, `-%`, `*%` and the corresponding compound assignments
- saturating arithmetic uses the Zig-style spellings `+|`, `-|`, `*|` and the corresponding compound assignments

## 12. Strings and Characters

Stark distinguishes two text forms:

- `ascii`
- `unicode`

String literals infer to:

- `ascii` when the literal contents are ASCII
- `unicode` otherwise

Character literals follow the same inference path instead of using a dedicated standalone `char` type.

## 13. FFI and Raw Boundaries

`ffi` marks the foreign boundary.

At that boundary:

- raw pointers are allowed
- nested raw pointers are allowed
- null may appear
- foreign ABI shape is preserved
- foreign code must not unwind through Stark frames

Outside that boundary:

- safe borrows are non-null
- pointers-to-pointers are not part of ordinary safe Stark code
- conversions from raw pointers into safe borrows must be explicit

## 14. Runtime Surface

Stark has no exceptions and no stack unwinding, ever.

The runtime contract is:

- recoverable errors are represented as ordinary values
- panic/assert/failure paths are unrecoverable and do not unwind
- unrecoverable failure terminates execution through a trap-or-abort style path
- the canonical hosted entrypoint is `export ffi fn i32 main()`
- normal process termination happens by returning from `main`
- foreign unwinding into or through Stark code is unsupported

## 15. Closed-World Bias

Stark is designed around closed-world optimization by default.

The source model assumes:

- static dispatch by default
- restrictive visibility by default
- limited externally visible symbols
- internalization-friendly code generation
- generic instantiation is monomorphized by default
- specialization is an explicit closed-world optimization tool

Dynamic dispatch and open-world behavior are explicit concessions, not the default model.
