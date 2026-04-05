# Stark Language Reference

Remember this languge aims to be faster than idiomatic C or Rust on most projects, we must chose the best posible optimization strategy and explore optimization opportunities.

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
- `enum` declarations
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

The `strictfp` modifier is reserved in the surface syntax, but the current compiler rejects it until strict floating-point lowering is implemented.

The current compiler enforces the modifier exclusivity rules above as declaration errors.

### 5.3 Declarations and Bodies

Function declarations may appear with either:

- a block body
- a trailing `;`

Semicolon form is used for declarations such as FFI functions or forward declarations.

## 6. Types

### 6.1 Builtin Types

The builtin type families are:

- `bool`
- `ascii`
- `unicode`
- `Ascii`
- `Unicode`
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

The current implemented surface accepts both ordinary width-based integers such as `i32` and explicit range-constrained integers such as `i32[0 255]`.

`void` is not a first-class Stark value type. It is valid only as a function return type.

### 6.2 Aggregates and Views

The aggregate/view forms are:

- fixed arrays: `T[N]`
- slices: `T[]`
- named aggregates through `struct` and `record`
- named variant families through `enum`

Fixed arrays are owning aggregate values.

Slices are non-owning views. A slice does not materialize or own backing
storage; it refers to storage established elsewhere.

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

Safe Stark code does not have null references or nullable borrows.

`null` exists only in the raw/FFI domain. A Stark program may compare raw
pointers against `null` and may store `null` in raw-pointer storage, but may
not assign `null` to safe values or borrows.

### 6.5 Generic Parameters and Type Aliases

Generic type parameters may appear on functions, `struct` declarations, `record` declarations, `enum` declarations, `trait` declarations, and `doctrine` declarations.

Generic parameters participate in name resolution and type substitution.

Generic instantiation is monomorphized by default.

Constrained generics, `where`-clause semantics, and specialization are deferred to `v2.0`.

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

### 8.3 Destructors

`struct` and `record` declarations may declare one destructor block.

The read-only form is:

```stark
drop {
    if (!self.Closed) {
        PlatformClose(self.Handle);
    }
}
```

The mutable form is:

```stark
mut drop {
    self.Ptr = null;
    self.Closed = true;
}
```

Destructor blocks have the following properties:

- they are not ordinary functions or methods
- they do not declare a name, return type, parameter list, or visibility
- they run automatically when an owned value is dropped
- `self` is implicit inside the block
- they may not use `return`
- only `struct` and `record` bodies may declare them
- a type may declare at most one destructor block

The two forms differ in what they may do to `self`:

- `drop { ... }`
  `self` is read-only. The destructor may inspect fields and perform cleanup work, but it may not assign to `self` or to any field reachable through `self`.
- `mut drop { ... }`
  `self` is mutable. This form is for deliberate state rewrites during destruction such as disarming raw-resource state before the final field drop sequence.

If a destructor is declared as `mut drop` but does not actually mutate `self`, the compiler warns and recommends the plain `drop` form instead.

Destructors remain restricted:

- they do not panic
- they do not synchronize
- they do not allocate
- explicit teardown APIs such as `Close` remain the right place for fallible cleanup and user-controlled ordering

After the destructor block runs, ordinary field destruction still proceeds.

### 8.4 Enums

`enum` declares a closed Rust-style enum family.

Enum cases may be:

- unit-like: `End`
- tuple-like: `Integer(i32)`
- named-field: `Move { X: i32, Y: i32 }`

Example:

```stark
enum Token {
    End,
    Integer(i32),
    Move { X: i32, Y: i32 },
}
```

Value-level enum construction uses `.` qualification from the enum type:

```stark
stack Token a = Token.End;
stack Token b = Token.Integer(5);
stack Token c = Token.Move { X: 1, Y: 2 };
```

Standard library types such as `Option<T>` or `Result<T, E>`, when provided, are ordinary enums rather than compiler-privileged forms.

Pattern matching uses the same case qualification:

```stark
switch (token) {
    case Token.End:
        return 0;
    case Token.Integer(var value):
        return value;
    case Token.Move { X: var x, Y: var y }:
        return x + y;
}
```

For ownership:

- directly capturing a move-only enum payload with `var` moves that payload out of the matched enum value
- after such a match, the matched enum value must be reinitialized before later use or scope-exit drop
- matching through `_` or through nested scalar-only subpatterns does not by itself move the whole enum value
- enum destruction only applies to the active case payload that actually requires drop; unit-like and copy-only cases do not create enum drop work by themselves

The default enum runtime contract is a direct-tag representation.

- every enum value carries a discriminant and exactly one active payload
- enum construction selects one case and initializes only that case's payload
- enum matching tests the discriminant and then projects fields from the matched active case
- destruction drops only the active case payload

Default enum layout is not a stable FFI contract.

- niche-based enum packing is not part of the current language contract
- explicit enum `repr` or ABI controls are not part of the current language surface
- Stark enums do not cross `ffi` or `export` boundaries

### 8.5 Traits

`trait` declares a named behavior contract.

Traits group function requirements for a type or family of types. Traits do not imply class-style inheritance.

Trait members are compile-time-only contracts. They participate in declaration modeling, package surfaces, and future conformance machinery, but are not directly callable as ordinary runtime functions.

In Stark v1.x, traits have no runtime representation.

- no trait objects
- no witness-table or vtable-style runtime dispatch values
- trait names are rejected in runtime value positions such as fields, globals, locals, parameters, and returns
- any future runtime dispatch design is explicitly post-v1.x work

### 8.6 Doctrines

`doctrine` declares a compile-time-only bundle of `law` functions and related constraints.

Doctrines have the following properties:

- no owned runtime identity
- no owned data
- no heap allocation
- no environment capture
- no runtime dispatch representation in v1.x
- members may be referenced directly through their qualified doctrine name
- static dispatch by default
- specialization-friendly in the closed-world model

In the current compiler, closed-world trait/doctrine optimization follows these rules:

- source-available `trait` and `doctrine` declarations are sealed by default for the current build
- manifest-backed or ABI-only imported `trait` and `doctrine` surfaces are treated as ABI boundaries rather than closed-world bodies
- doctrine members devirtualize to direct calls or direct ABI calls; trait members remain compile-time-only contracts with no runtime call lowering
- doctrine members use shared code by default, while eligible imported non-`export` law members may additionally expose law-caller-specialized clone paths
- trait-constrained monomorphization and other constrained-generic specialization remain deferred to `v2.0`

## 9. Globals and Storage Classes

Top-level globals use dedicated global declaration forms. In the current implemented syntax, globals are written as `const`, `static`, or `static mut`, and global lifetime is implied by being a top-level declaration.

Stark has three classes of globals:

- `const T name = ...;`
  A fully frozen global object graph. `const` is stronger than an immutable binding: the value and everything transitively reachable through it are deeply immutable for the lifetime of the program.
- `static mut T name = ...;`
  A mutable global rebinding. The global binding itself may be reassigned after initialization.
- `static T name = ...;`
  An immutable global binding. The binding itself cannot be rebound, but the referenced value may still contain mutable heap state or other mutability that the type permits. This form also covers ordinary plain immutable global values.

The intended distinction is:

- `const` means fully frozen global object graph
- `static` means immutable binding, not deep freeze
- `static mut` means mutable global rebinding

This gives Stark the following source-level global model:

- mutable global rebinding
- immutable global pointer to mutable heap object
- fully frozen global object graph

More concretely, `const` means:

- every reachable field and element is observed as readonly
- any pointer-like or view-like value reachable through the hierarchy is itself readonly
- safe code may not derive a mutable-capable alias from any reachable part of the graph
- safe code may not regain mutation through explicit raw-pointer or integer conversion chains

Conceptually, reading from a `const` global behaves like reading through a deeply frozen view of the entire reachable hierarchy, not merely through a root binding that happens not to be assignable.

Local variables still require an explicit storage class.

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

Mutability remains opt-in:

- bindings are immutable by default
- `mut` enables reassignment where the binding form permits it
- `const` on a global freezes the reachable object graph rather than merely freezing the top-level name

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

The current loop behavior rules are:

- `infinite`
  - must use a statically unconditional condition
  - `while` must use literal `true`
  - `for` must omit the condition expression
  - may not contain a structural exit from the current loop or function such as `break` or `return`
  - is not accepted inside a declared `finite` function
- `non-deterministic`
  - may or may not exit
  - is not accepted inside a declared `finite` function
- `willexit`
  - is the only loop form accepted inside a declared `finite` function
  - if the loop condition is statically unconditional (`while willexit (true)` or `for willexit (;;)`), the body must contain at least one structural `break` or `return`

### 10.3 Switch and Patterns

The switch surface includes:

- literal cases
- `default`
- `when` guards
- `case var capture`
- discard `_`
- dot-qualified enum case patterns for unit, tuple, and named-field enum cases
- exact-type named aggregate patterns with nested aggregate subpatterns

## 11. Expressions

The expression surface includes:

- literals
- identifiers and qualified names
- dot-qualified enum constructor expressions
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

Text slices use the existing postfix indexing form with two expressions:

```stark
text[start, length]
```

For `ascii` and `unicode`, this produces another zero-copy text view over the
same backing storage.

`{ ... }` is an array initializer, not a slice literal.

An array initializer may materialize a fixed array value or participate in
nested aggregate initialization where the target storage is already defined.
It may not target a slice type directly.

This is invalid:

```stark
stack i32[] view = { 1, 2, 3 };
```

because `i32[]` is a view type and Stark does not silently create hidden
backing storage for slice targets.

Instead, the backing storage must be made explicit:

```stark
stack i32[3] values = { 1, 2, 3 };
stack i32[] view = values;
```

### 11.1 Operators

Operator families include:

- arithmetic: `+`, `-`, `*`, `/`, `%`
- bitwise: `&`, `^`, `|`
- shifts: `<<`, `>>`
- wrapping integer arithmetic: `+%`, `-%`, `*%`
- saturating integer arithmetic: `+|`, `-|`, `*|`
- comparisons: `<`, `>`, `<=`, `>=`, `==`, `!=`
- logical: `&&`, `||`, `!`
- raw pointer address-of: unary `&`
- raw pointer dereference: unary `*`
- unary bitwise not: `~`
- unary wrapping negate: `-%`
- conditional: `?:`
- assignment: `=`, `+=`, `-=`, `*=`, `+%=`, `-%=`, `*%=`, `+|=`, `-|=`, `*|=`, `/=`, `%=`, `&=`, `^=`, `|=`
- exponentiation: `**`

`^` is bitwise XOR, not exponentiation.

`**` is the exponent operator.

Explicit conversions use C-style syntax: `(targetType)expression`.

This is the required surface for conversions that Stark keeps explicit, including:

- integer widening and narrowing
- integer/float conversions
- raw pointer to integer and integer to raw pointer conversions
- raw pointer to raw pointer conversions
- fixed-array to slice view conversions
- ascii/unicode text conversions for compile-time text constants

These conversions may not strengthen mutability. In particular, safe code may not use explicit conversions to turn a readonly raw pointer into `rawmutptr<T>`, and may not erase readonly or frozen provenance from a raw pointer in order to regain mutation later.

### 11.2 Precedence

From highest to lowest, the intended precedence is:

1. postfix: calls, indexing, member access
2. unary: explicit conversions, unary `+`, unary `-`, `!`, `~`, raw `&`, raw `*`
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

Comparison chains are formed at comparison precedence.

### 11.3 Comparison Chains

Comparison operators may be chained.

Examples:

- `a < b < c`
- `min <= value < max`
- `left == middle != right`

A comparison chain:

- evaluates operand expressions left to right
- evaluates each operand expression exactly once
- compares each adjacent operand pair using the written operator
- short-circuits on the first false comparison
- produces `bool`

`a < b < c` is therefore not interpreted as `(a < b) < c`.

Instead, it has the semantics of:

```stark
a < b && b < c
```

with the shared middle operand evaluated only once.

More generally, `a op1 b op2 c op3 d` behaves like:

```stark
a op1 b && b op2 c && c op3 d
```

with each operand evaluated once and then reused for the adjacent comparisons.

Each adjacent comparison in the chain must be individually legal under Stark's
ordinary comparison rules. If any adjacent comparison is ill-typed, the chain is
ill-typed.

### 11.4 Exponentiation

Exponentiation is floating-point only.

- `**` is legal for floating-point operands
- integer exponentiation is not part of Stark
- there is no implicit integer-power form

### 11.5 Floating-Point Contract

Ordinary floating-point code uses aggressive fast-math semantics.

- ordinary floating-point code uses optimizer-friendly non-strict floating-point operations
- strict IEEE-style semantics require the explicit `strictfp` function modifier
- `strictfp` is the source-level escape hatch from Stark's default fast-math model

### 11.6 Integer Arithmetic Contract

Ordinary integer arithmetic in Stark is performance-first and intentionally strict.

- signed and unsigned overflow in ordinary arithmetic is illegal and treated as undefined behavior
- shift counts greater than or equal to the bit width of the shifted value are illegal and treated as undefined behavior
- wrapping arithmetic uses the Zig-style spellings `+%`, `-%`, `*%` and the corresponding compound assignments
- saturating arithmetic uses the Zig-style spellings `+|`, `-|`, `*|` and the corresponding compound assignments

## 12. Strings and Characters

Stark distinguishes two text forms:

- `ascii`
- `unicode`

The core owned text container forms are:

- `Ascii`
- `Unicode`

String literals infer to:

- `ascii` by default when the decoded literal can be stored as UTF-8
- `unicode` when explicitly requested with a compile-time text conversion such as `(unicode)"..."`

Supported escapes in string and character literals are:

- simple escapes: `\\`, `\"`, `\'`, `\0`, `\b`, `\t`, `\n`, `\f`, `\r`
- hex escapes: `\xNN`
- unicode escapes: `\uNNNN`

Character literals follow the same inference path instead of using a dedicated standalone `char` type.

The current implemented runtime model is:

- both `ascii` and `unicode` lower to text views with runtime layout `{ ptr, i64 }`
- for `ascii`, the pointer references UTF-8 bytes and the `i64` length is the byte length of the referenced text
- for `unicode`, the pointer references UTF-32 code units and the `i64` length is the code-unit count of the referenced text
- compiler-emitted `ascii` literals are stored as UTF-8 bytes in static data with a trailing `\0`, but the Stark value length excludes that terminator
- compiler-emitted `unicode` literals are stored as UTF-32 code units in static data with a trailing zero code unit, but the Stark value length excludes that terminator
- the core owned text containers are `Ascii` and `Unicode`, each using pointer/length/capacity storage and requiring no module import
- `Ascii.Data` is `rawmutptr<i8>`, `Ascii.Length` counts UTF-8 code units, and `Ascii.Capacity` counts allocated UTF-8 code units
- `Unicode.Data` is `rawmutptr<i32>`, `Unicode.Length` counts UTF-32 code units, and `Unicode.Capacity` counts allocated UTF-32 code units
- `System.Text.AsciiView(Ascii)` and `System.Text.UnicodeView(Unicode)` project those owning containers back to zero-copy immutable `ascii` and `unicode` views
- `System.Text.AsciiData(ascii)`, `System.Text.AsciiLength(ascii)`, `System.Text.UnicodeData(unicode)`, and `System.Text.UnicodeLength(unicode)` expose the pointer/length parts of immutable text views explicitly for low-level stdlib and FFI work
- `System.Text.TryConcatAscii` and `System.Text.TryConcatUnicode` implement the current non-hidden-allocation concatenation path by writing into caller-provided owned buffers through explicit `rawmutptr` destinations and returning `bool` success instead of allocating implicitly
- explicit `ascii` / `unicode` widening and narrowing conversions are currently implemented only for compile-time text constants; general runtime text conversion still awaits explicit allocator-backed construction APIs that bridge immutable text views and those owning text containers
- `text[start, length]` is implemented for both `ascii` and `unicode` and returns another zero-copy text view of the same text kind
- single-element text indexing is not implemented yet

## 13. FFI and Raw Boundaries

`ffi` marks the foreign boundary.

At that boundary:

- raw pointers are allowed
- nested raw pointers are allowed
- null may appear in raw-pointer values and raw-pointer checks
- foreign ABI shape is preserved
- Stark enums do not cross `ffi` or `export` boundaries
- foreign code must not unwind through Stark frames

Outside that boundary:

- safe borrows are non-null
- safe values may not be assigned `null`
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

In the current compiler, closed-world inlining is intentionally conservative:

- root-module doctrine and other supported law bodies use direct shared code in the current module
- the compiler may strengthen the default inline preference to an always-inline lowering intent for eligible module-private root-module `law` or `finite law` bodies
- the compiler may also do this for eligible module-private helpers inside source-loaded imported modules when those helpers are already declared `law` or `finite law` and are called only from same-module law bodies
- the compiler may additionally do this for eligible non-export source-loaded imported law entrypoints when every known caller in the current closed-world build is also a law body
- when a root-module law call targets an eligible non-export imported law body, the current compiler may materialize an internal root-side clone of the imported law chain so LLVM can optimize it without changing the original module ABI
- those root-side clones are selected per law caller, so mixed law/non-law callers in the same build may split: law callers use the internal specialized clone path while non-law callers keep using the original imported ABI
- imported ABI-only doctrine and law surfaces fall back to direct ABI calls instead of closed-world body specialization
- this only happens when no explicit inline modifier was written
- recursive law helpers are excluded
- `export` ABI surfaces are still excluded from this rule
