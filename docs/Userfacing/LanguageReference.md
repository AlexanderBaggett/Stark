# Stark Language Reference

The user facing Stark language. This document defines the source level contract: how Stark code is written, what constructs exist, and what behavior programmers can rely on.

Lower level compiler strategy and optimizer rationale live in [LanguageInternals.md](../Internals/LanguageInternals.md).

[Roadmap.md](../Internals/Roadmap.md) tracks milestone ordering and work sequencing.

## 1. Design Goals

Stark is a performance first language targeting LLVM.

The priorities:

* speed and predictable low level behavior over convenience
* explicitness over hidden work
* restrictions that enable stronger optimization
* ownership and effect rules that give the compiler more information to work with
* a default preference for static dispatch and minimal visibility

Stark is intentionally more restrictive than mainstream systems languages when that restriction produces better code.

## 2. Source File Structure

A Stark compilation unit:

```stark
import Some.Module
export import Another.Module
module Current.Module

// top-level declarations
```

Rules:

* each source file declares exactly one module
* imports appear before the `module` declaration
* the `module` declaration appears exactly once
* one source file equals one module
* `import` and `module` declarations do not end in semicolons
* wildcard imports are forbidden
* `export import` is the only re export form
* importing a module makes its visible top level declarations available by final name, so `import System.Collections` allows `List<T>` instead of `System.Collections.List<T>`

## 3. Top Level Declarations

The top level declaration categories:

* functions
* `struct` declarations
* `record` declarations
* `enum` declarations
* `trait` declarations
* `doctrine` declarations
* type alias declarations
* global constants
* global variables

## 4. Modules and Visibility

Three explicit visibility modifiers:

* `internal`
* `public`
* `export`

A top level declaration with no visibility modifier is **module private**.

The meanings:

* **module private**: visible only inside the current module
* `internal`: visible to other modules in the same package or library
* `public`: visible to downstream Stark source that imports the package
* `export`: a real binary symbol intended for FFI, runtime entrypoints, or stable binary boundaries

`public` and `export` are intentionally different:

* `public` is source visibility
* `export` is binary visibility

Visibility applies to top level declarations and to member functions inside `struct` and `record` bodies. Member functions inherit the visibility of their enclosing type unless explicitly overridden, and a member function may not be more visible than its enclosing type. `export` is the careful edge: an `export struct` or `export record` makes omitted member visibility `public`, not `export`, so binary visible member functions must write `export` explicitly.

Visibility does not apply to locals, statements, expressions, or fields.

### 4.1 Package Owned Native Dependencies

Interop packages can describe the native source files and native libraries they require so downstream users can build with ordinary Stark commands.

The current package author surface is CLI metadata:

```bash
compiler Raylib.stark --emit-lib \
  -o dist/libRaylibStark.a \
  --native-source RaylibNative.c \
  --native-pkg-config raylib
```

The package records those native dependency declarations. A downstream executable that imports the package gathers the package owned native build metadata automatically.

When the native dependency is not available through `pkg-config`, the package can use explicit metadata:

```bash
compiler Raylib.stark --emit-lib \
  -o dist/libRaylibStark.a \
  --native-source RaylibNative.c \
  --native-include-dir /path/to/raylib/src \
  --native-library-dir /path/to/raylib/src \
  --native-library raylib
```

If a named native library cannot be found during the final native link, the build reports the missing library and suggests installing it or adding its directory with `-L` or `--native-library-dir`.

If a package owned `pkg-config` dependency cannot be resolved, the build names the package and suggests installing the native package, setting `PKG_CONFIG_PATH`, or using explicit native metadata.

Native dependency declarations are for explicit FFI and package interop. They do not make those dependencies part of Stark's standard library runtime profile.

## 5. Functions

### 5.1 Function Kinds

Four source level function kinds:

* `fn`
* `finite`
* `law`
* `finite law`

The keyword order is fixed: `finite law`, not `law finite`.

The meanings:

* `fn`: general function form
* `finite`: guaranteed progress and guaranteed return
* `law`: pure, readonly, no visible side effects
* `finite law`: pure, plus guaranteed progress and return

### 5.2 Function Modifiers

The modifiers:

* `inline`
* `noinline`
* `inlinehint`
* `hot`
* `cold`
* `ffi`
* `strictfp`
* `static`

Rules:

* `inline`, `noinline`, and `inlinehint` are mutually exclusive
* `hot` and `cold` are mutually exclusive
* `ffi` marks a foreign facing function boundary
* `strictfp` selects strict IEEE style floating point semantics for the function
* `static` is only valid on member functions inside `struct` or `record` declarations

Static member functions belong to the type rather than to a value. They are called through the type name and do not receive a `self` argument:

```stark
struct Thread {
    static fn void Yield();
}

fn void Run() {
    Thread.Yield();
}
```

Instance member functions are called through a value and use an explicit receiver parameter:

```stark
struct Counter {
    i32[0 max] Value;

    finite law i32[0 max] Get(borrow Counter self) {
        return self.Value;
    }
}
```

### 5.3 Declarations and Bodies

Function declarations may appear with either:

* a block body
* a trailing `;`

Semicolon form is used for FFI functions and forward declarations.

Function parameters are written as `T name`.

Default argument syntax such as `fn i32 Add(i32 left = 1)` is not part of Stark.

### 5.4 Function Items and Function Pointers

Stark's first class callable model starts with **function items**.

A function item is the callable value represented by a named function. It is not a raw pointer by default, does not capture state, and can usually be specialized, inlined, or called directly.

```stark
fn i32[-2147483648 2147483647] Worker() {
    return 0;
}

fn void Start() {
    stack mut System.Threading.Thread worker = new(Worker);
}
```

Function items may be promoted to explicit function pointer values when a runtime pointer is required.

```stark
stack fnptr<fn i32[-2147483648 2147483647]()> entry = Worker;
stack i32[-2147483648 2147483647] result = entry();
```

Promotion to a function pointer is the point where the function becomes address taken. Ordinary function item use stays direct; indirect callable behavior is requested explicitly through a `fnptr` value.

Function pointer types carry the function kind in their signature:

```stark
fnptr<fn i32[0 max](i32[0 max])>
fnptr<finite i32[0 max](i32[0 max])>
fnptr<law bool(borrow Item)>
fnptr<finite law i32[0 max](i32[0 max])>
```

The current `fnptr` type is an ordinary safe callable pointer. Unsafe function items cannot be promoted to ordinary `fnptr` values because that would hide the unsafe requirement from later calls. Call unsafe functions directly inside an `unsafe` block, or expose a safe wrapper that checks the required conditions.

### 5.5 Lambdas and Capture Modes

Lambda syntax follows the C# arrow form:

```stark
stack fnptr<fn i32[0 max](i32[0 max])> square =
    (i32[0 max] value) => value * value;

stack fnptr<fn i32[-2147483648 2147483647](rawmutptr<State>)> worker =
    (rawmutptr<State> state) => {
        return Worker(state);
    };
```

A lambda with no capture list is non capturing. It may be used where a matching function pointer is expected.

Capturing lambdas use an explicit capture list. Capture is never implicit.

```stark
UseTransform(capture(copy scale, read table) (i32[0 max] index) => {
    return table[index] * scale;
});
```

The safe capture modes:

* `capture(copy x)`: copies a cheap copy value (bool, integer, float, raw pointer, function pointer, or readonly borrow) into the closure environment. Owned structs and owned text are not silently copied; use `move` or `read` instead.
* `capture(move x)`: moves ownership into the closure environment. The source binding is consumed.
* `capture(read x)`: captures readonly access to existing storage.
* `capture(mut x)`: captures exclusive mutable access to existing storage for the closure's lifetime.
* `capture(out x)`: captures a write only destination. The closure may write the destination but may not read the old value.
* `capture(init x)`: captures uninitialized destination storage that the closure must initialize before returning successfully.

`mut`, `out`, and `init` captures require `x` to be a writable binding such as a `mut` local or mutable destination. Immutable values may still be captured with modes such as `copy` or `read`.

Two capture modes are trusted operations and require an unsafe context:

```stark
capture(unsafe addr x)
capture(unsafe shared x)
```

* `capture(unsafe addr x)`: captures address or identity information without ordinary dereference authority. A low level escape hatch for tracking where a pointer came from and for interop.
* `capture(unsafe shared x)`: publishes a value or capability into a shared or concurrent domain that ordinary non shared Stark code cannot model by itself.

These modes are explicit because they weaken Stark's ordinary closed and non shared memory assumptions.

## 6. Types

### 6.1 Builtin Types

The builtin type families:

* `bool`
* `ascii`
* `unicode`
* `Ascii`
* `Unicode`
* signed integer widths: `i8`, `i16`, `i24`, `i32`, `i48`, `i64`, `i96`, `i128`, `i192`, `i256`, `i384`, `i512`, `i768`, `i1024`
* unsigned integer widths: `u8`, `u16`, `u24`, `u32`, `u48`, `u64`, `u96`, `u128`, `u192`, `u256`, `u384`, `u512`, `u768`, `u1024`
* floating point widths: `f16`, `f32`, `f64`, `f80`, `f128`

Examples:

* `i32[0 255]`
* `u8[0 255]`
* `i64[-9223372036854775808 9223372036854775807]`
* `i128[0 340282366920938463463374607431768211455]`
* `f16`, `f32`, `f64`, `f80`, `f128`

Integer source types are always written as explicit ranged forms over one of the supported widths.

```stark
i32[0 255]
i32[min max]
i64[0 max]
u8[min 127]
i32[10**2 10**10]
i64[1024 * 1024 1024 * 1024 * 1024]
```

Within an integer range, `min` and `max` are type relative endpoint names. For signed `iN` ranges they mean the signed minimum and maximum for that width. For unsigned `uN` ranges they mean `0` and `2**N - 1`.

Unsigned integer widths are real integer types, not aliases for signed integers with non negative ranges. For `uN`, `min` is `0` and `max` is `2**N - 1`. Negative endpoints and endpoints outside that width are rejected.

Range endpoints support compile time integer arithmetic over literals and type relative endpoint names. Supported endpoint operators: `+`, `-`, `*`, `/`, `%`, `**`, unary `-`, and parentheses. Endpoint arithmetic is checked during compile time evaluation.

Bare width names such as `i32` are convenient family labels in prose, but they are not the full Stark integer source form by themselves. The source level type must carry an explicit range.

Scalar integer constants are the exception: they should be declared without an explicit integer type. A `const` integer is compile time known and cannot change, so Stark derives both the exact single value range and the smallest supported storage width that can hold it. If a scalar integer const does name a type, it uses only the bare width form such as `i8` or `i32`; ranged forms such as `i32[min max]` are for runtime integer values, not scalar constants.

```stark
const PageSize = 2**12;      // i16 storage
const BoardWidth = 80;      // i8 storage
const BigCount = 2**16;     // i24 storage
const i8 SmallCount = 80;   // accepted explicit width
const i32 WideCount = 80;   // accepted, with a warning that storage is i8
```

For floating point constants, an unsuffixed decimal such as `80.0` is `f64`. Use an `f` suffix for `f32`, as in `80.0f`.

Floating point source types use the bare width form directly. Stark supports `f16`, `f32`, `f64`, `f80`, and `f128`.

`void` is not a first class Stark value type. It is valid only as a function return type.

### 6.2 Aggregates and Views

The aggregate and view forms:

* fixed arrays: `T[N]`
* slices: `T[]`
* named aggregates through `struct` and `record`
* named variant families through `enum`

Fixed arrays are owning aggregate values.

Slices are non owning views. A slice does not materialize or own backing storage; it refers to storage established elsewhere.

### 6.3 Type Qualifiers

The qualifiers:

* `borrow`
* `retborrow`
* `storeborrow`
* `frozen`
* `shared`
* `out`
* `init`
* `mut`

These are part of the type model, not local syntax sugar.

### 6.4 Raw Pointers

The raw pointer forms:

* `rawptr<T>`
* `rawmutptr<T>`

Safe Stark code has no null references and no nullable borrows.

`null` exists only in the raw and FFI domain. A Stark program may compare raw pointers against `null` and may store `null` in raw pointer storage, but may not assign `null` to safe values or borrows.

### 6.5 Generic Parameters and Type Aliases

Generic type parameters may appear on functions, `struct` declarations, `record` declarations, `enum` declarations, `trait` declarations, and `doctrine` declarations.

Generic parameters participate in name resolution and type substitution.

Type aliases introduce alternate names for existing types:

```stark
alias Byte = i8;
alias BufferView<T> = borrow T[];
```

A type alias does not by itself create a distinct runtime type or ABI identity. The declaration keyword is `alias`. Like other top level declarations, aliases may be module private, `internal`, `public`, or `export`. `public` and `export` aliases are published as part of the package facing Stark surface.

Internal specialization details are described in [LanguageInternals.md](../Internals/LanguageInternals.md).

## 7. Ownership, Borrowing, and Lifetimes

Safe Stark is ownership based. There is no garbage collection.

The main rules:

* values are owned by default
* moves transfer ownership
* moved values cannot be used again
* owned values are dropped automatically at scope exit
* safe borrows are non owning and non null
* raw pointers are the only null capable pointer forms
* safe code cannot use `forget` style escape hatches

The borrow classes:

* `borrow T`: cannot be stored or returned
* `retborrow T`: may escape only through the return value
* `storeborrow T`: may be stored or otherwise escape

The access qualifiers:

* `frozen T`: deeply immutable for the lifetime of the borrow
* `shared T`: explicit shared access domain

Destruction is intentionally restricted:

* trivial POD style destruction is the default
* safe destructors do not panic, synchronize, or allocate

For the full borrowing model and design rationale, see [BorrowerSystem.md](./BorrowerSystem.md).

## 8. Data Declarations

### 8.1 Structs

`struct` is the primary named aggregate form for ordinary data with associated methods and constructors.

`struct` declarations do not support inheritance.

### 8.2 Records

`record` is the data oriented named aggregate form.

`record` declarations do not support inheritance.

### 8.3 Destructors

`struct` and `record` declarations may declare one destructor block.

Readonly form:

```stark
drop {
    if (!self.Closed) {
        PlatformClose(self.Handle);
    }
}
```

Mutable form:

```stark
mut drop {
    self.Ptr = null;
    self.Closed = true;
}
```

Destructor blocks have these properties:

* not ordinary functions or methods
* no name, return type, parameter list, or visibility
* run automatically when an owned value is dropped
* `self` is implicit inside the block
* may not use `return`
* only `struct` and `record` bodies may declare them
* a type may declare at most one destructor block

The two forms differ in what they may do to `self`:

* `drop { ... }`: `self` is readonly. The destructor may inspect fields and perform cleanup work, but may not assign to `self` or to any field reachable through `self`.
* `mut drop { ... }`: `self` is mutable. For deliberate state rewrites during destruction such as disarming raw resource state before the final field drop sequence.

If a destructor is declared as `mut drop` but does not actually mutate `self`, the compiler warns and recommends the plain `drop` form instead.

Destructors are restricted:

* they do not panic
* they do not synchronize
* they do not allocate
* explicit teardown APIs such as `Close` are the right place for fallible cleanup and user controlled ordering

After the destructor block runs, ordinary field destruction still proceeds.

### 8.4 Enums

`enum` declares a closed Rust style enum family.

Enum cases may be:

* unit like: `End`
* tuple like: `Integer(i32)`
* named field: `Move { X: i32, Y: i32 }`

Example:

```stark
enum Token {
    End,
    Integer(i32),
    Move { X: i32, Y: i32 },
}
```

Value level enum construction uses `.` qualification from the enum type:

```stark
stack Token a = Token.End;
stack Token b = Token.Integer(5);
stack Token c = Token.Move { X: 1, Y: 2 };
```

Standard library types such as `Option<T>` or `Result<T, E>`, when provided, are ordinary enums rather than compiler privileged forms.

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

* directly capturing a move only enum payload with `var` moves that payload out of the matched enum value
* after such a match, the matched enum value must be reinitialized before later use or scope exit drop
* matching through `_` or through nested scalar only subpatterns does not by itself move the whole enum value
* enum destruction only applies to the active case payload that requires drop; unit like and copy only cases do not create enum drop work by themselves

The default enum runtime contract is a direct tag representation:

* every enum value carries a discriminant and exactly one active payload
* enum construction selects one case and initializes only that case's payload
* enum matching tests the discriminant and then projects fields from the matched active case
* destruction drops only the active case payload

Default enum layout is not a stable FFI contract:

* niche based enum packing is not part of the current language contract
* explicit enum `repr` or ABI controls are not part of the current language surface
* Stark enums do not cross `ffi` or `export` boundaries

### 8.5 Traits

`trait` declares a named behavior contract.

Traits group function requirements for a type or family of types. Traits do not imply class style inheritance.

Trait members are compile time only contracts. They are not directly callable as ordinary runtime functions.

Traits have no runtime representation:

* no trait objects
* no witness table or vtable style runtime dispatch values
* trait names are rejected in runtime value positions such as fields, globals, locals, parameters, and returns

### 8.6 Doctrines

`doctrine` declares a compile time only bundle of `law` functions and related constraints.

Doctrines have these properties:

* no owned runtime identity
* no owned data
* no heap allocation
* no environment capture
* no runtime dispatch representation
* members may be referenced directly through their qualified doctrine name
* static dispatch by default

## 9. Globals and Storage Classes

Top level globals use dedicated global declaration forms. Globals are written as `const`, `static`, or `static mut`. Global lifetime is implied by being a top level declaration.

Stark has three classes of globals:

* `const name = ...;` or `const T name = ...;`
  A fully frozen global object graph. `const` is stronger than an immutable binding: the value and everything transitively reachable through it are deeply immutable for the lifetime of the program.
* `static mut T name = ...;`
  A mutable global rebinding. The global binding itself may be reassigned after initialization.
* `static T name = ...;`
  An immutable global binding. The binding cannot be rebound, but the referenced value may still contain mutable heap state or other mutability that the type permits. This form also covers ordinary plain immutable global values.

The distinction:

* `const`: fully frozen global object graph
* `static`: immutable binding, not deep freeze
* `static mut`: mutable global rebinding

The source level global model:

* mutable global rebinding
* immutable global pointer to mutable heap object
* fully frozen global object graph

Specifically, `const` means:

* every reachable field and element is observed as readonly
* any pointer like or view like value reachable through the hierarchy is itself readonly
* safe code may not derive a mutable capable alias from any reachable part of the graph
* safe code may not regain mutation through explicit raw pointer or integer conversion chains

Reading from a `const` global behaves like reading through a deeply frozen view of the entire reachable hierarchy, not merely through a root binding that happens not to be assignable.

Compile time scalar numeric constants do not use an integer range. Integer consts use the smallest supported integer width that preserves the value; the source program does not spell that range. Floating point consts follow the written number: `80.0` is `f64`, and `80.0f` is `f32`. Use an explicit `const T name = ...;` form for non scalar or otherwise ambiguous constants such as raw pointer nulls, fixed arrays, or aggregate initializers.

Local variables still require an explicit storage class.

The storage classes:

* `stack`
* `heap`
* `register`
* `static`
* `arena`

`register` is a local only value style storage class. A `register` local has no stable source visible address: safe code may not take `&local`, form a slice view from it, or otherwise require something with an address. It is not a promise that a hardware register will be allocated; it is a request to keep the value in registers when possible. Use `stack` when a stable address is required.

Function local `static` storage is reserved until Stark defines how static duration locals are initialized, identified, and torn down. Use a top level `static` global for global lifetime storage.

The standardized allocation backed storage classes:

* `heap`: uses the default global general purpose allocator. Safe Stark code does not manually free `heap` values; ownership and scope still govern destruction.
* `arena`: reserved for a region allocator intended for fast bump style allocation with bulk reclamation when the lexical arena region ends. The current compiler rejects `arena` locals until arena support is implemented.

Mutability remains opt in:

* bindings are immutable by default
* `mut` enables reassignment where the binding form permits it
* `const` on a global freezes the reachable object graph rather than merely freezing the top level name

## 10. Control Flow

The statement forms:

* blocks
* local constant declarations
* local variable declarations
* `if` and `else`
* `switch`
* `while`
* `for`
* `return`
* `break`
* `continue`
* expression statements

### 10.1 Branch Weights

`if` and `switch` may carry an optional branch weight annotation such as `w9` or `w99`.

### 10.2 Loops

`while` and `for` require an explicit loop behavior keyword:

* `infinite`
* `non-deterministic`
* `willexit`

This is part of Stark's source level control flow contract, especially for `finite` functions.

Loop behavior rules:

* `infinite`
  * must use a statically unconditional condition
  * `while` must use literal `true`
  * `for` must omit the condition expression
  * may not contain a structural exit from the current loop or function such as `break` or `return`
  * is not accepted inside a declared `finite` function
* `non-deterministic`
  * may or may not exit
  * is not accepted inside a declared `finite` function
* `willexit`
  * is the only loop form accepted inside a declared `finite` function
  * if the loop condition is statically unconditional (`while willexit (true)` or `for willexit (;;)`), the body must contain at least one structural `break` or `return`

### 10.3 Switch and Patterns

The switch surface includes:

* literal cases
* `default`
* `when` guards
* `case var capture`
* discard `_`
* dot qualified enum case patterns for unit, tuple, and named field enum cases
* exact type named aggregate patterns with nested aggregate subpatterns

## 11. Expressions

The expression surface includes:

* literals
* identifiers and qualified names
* dot qualified enum constructor expressions
* calls
* member access
* indexing
* object creation with `new`
* object initializers
* array initializers
* unary operators
* binary operators
* ternary conditional `?:`
* assignments and compound assignments

Object creation supports both explicit and target typed forms:

```stark
stack Box explicitBox = new Box();        // calls Box's default constructor
stack Box defaultBox = new();             // calls the target type's default constructor
stack Box constructedBox = new(value);    // calls the corresponding constructor
stack Box initializedBox = new() { Value = value };
```

Target typed `new()` and `new(args)` require the surrounding code to already say which `struct` or `record` type is being created. That target type can come from a local declaration, assignment target, return type, object initializer field type, or fixed array element type. If Stark cannot tell which `struct` or `record` you mean, use the explicit form such as `new Box(...)`.

`new()` calls the zero argument constructor for the target `struct` or `record`. If the type declares no constructors, Stark provides an implicit default constructor that default initializes the value. `new(value)` and `new(args)` call a matching constructor if one exists. If the type declares constructors but none match the supplied arguments, compilation fails.

Text views use the existing postfix indexing form:

```stark
text[]
text[index]
text[start, length]
```

For `ascii` and `unicode`, these forms produce zero copy text views over the same backing storage.

`{ ... }` is an array initializer, not a slice literal.

An array initializer may materialize a fixed array value or participate in nested aggregate initialization where the target storage is already defined. For fixed arrays, omitted trailing elements are zero initialized. It may not target a slice type directly.

This is invalid:

```stark
stack i32[] view = { 1, 2, 3 };
```

because `i32[]` is a view type and Stark does not silently create hidden backing storage for slice targets.

Instead, the backing storage must be made explicit:

```stark
stack i32[3] values = { 1, 2, 3 };
stack i32[] view = values;
```

### 11.1 Operators

Operator families:

* arithmetic: `+`, `-`, `*`, `/`, `%`
* bitwise: `&`, `^`, `|`
* shifts: `<<`, `>>`
* wrapping integer arithmetic: `+%`, `-%`, `*%`
* saturating integer arithmetic: `+|`, `-|`, `*|`
* comparisons: `<`, `>`, `<=`, `>=`, `==`, `!=`
* logical: `&&`, `||`, `!`
* raw pointer address of: unary `&`
* raw pointer dereference: unary `*`
* unary bitwise not: `~`
* unary wrapping negate: `-%`
* conditional: `?:`
* assignment: `=`, `+=`, `-=`, `*=`, `+%=`, `-%=`, `*%=`, `+|=`, `-|=`, `*|=`, `/=`, `%=`, `&=`, `^=`, `|=`
* exponentiation: `**`

`^` is bitwise XOR, not exponentiation.

`**` is the exponent operator.

Explicit conversions use C style syntax: `(targetType)expression`.

This is the required surface for conversions Stark keeps explicit:

* integer widening and narrowing
* integer and float conversions
* raw pointer to integer and integer to raw pointer conversions
* raw pointer to raw pointer conversions
* fixed array to slice view conversions
* ascii and unicode text conversions for compile time text constants

Runtime conversion between `ascii`, UTF-16 buffers, and `unicode` values uses the explicit `System.Text` helper APIs such as `TryConvertAsciiToUnicode`, `TryConvertAsciiToUtf16`, `TryConvertUtf16ToUnicode`, `TryConvertUnicodeToAscii`, `TryConvertUnicodeToUtf16`, and `TryConvertUtf16ToAscii`, all with caller owned destination storage.

These conversions may not strengthen mutability. Safe code may not use explicit conversions to turn a readonly raw pointer into `rawmutptr<T>`, and may not strip the readonly or frozen origin off a raw pointer to regain mutation later.

### 11.2 Precedence

From highest to lowest:

1. postfix: calls, indexing, member access
2. unary: explicit conversions, unary `+`, unary `-`, `!`, `~`, raw `&`, raw `*`
3. exponentiation: `**` with right associative parsing
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

* `a < b < c`
* `min <= value < max`
* `left == middle != right`

A comparison chain:

* evaluates operand expressions left to right
* evaluates each operand expression exactly once
* compares each adjacent operand pair using the written operator
* short circuits on the first false comparison
* produces `bool`

`a < b < c` is not interpreted as `(a < b) < c`. It has the semantics of:

```stark
a < b && b < c
```

with the shared middle operand evaluated only once.

More generally, `a op1 b op2 c op3 d` behaves like:

```stark
a op1 b && b op2 c && c op3 d
```

with each operand evaluated once and then reused for the adjacent comparisons.

Each adjacent comparison must be individually legal under Stark's ordinary comparison rules. If any adjacent comparison is ill typed, the chain is ill typed.

### 11.4 Exponentiation

Exponentiation uses `**`.

* `**` is legal for floating point operands
* integer `**` is legal for integer operands and is folded in compile time constant contexts such as const numeric initializers and range endpoints
* ordinary runtime integer exponentiation is supported for integer operands

### 11.5 Floating Point Contract

Ordinary floating point code uses aggressive fast math semantics.

* ordinary floating point code uses optimizer friendly non strict floating point operations
* strict IEEE style semantics require the explicit `strictfp` function modifier
* `strictfp` is the source level escape hatch from Stark's default fast math model

### 11.6 Integer Arithmetic Contract

Ordinary integer arithmetic in Stark is performance first and intentionally strict.

* signed and unsigned overflow in ordinary arithmetic is illegal and treated as undefined behavior
* shift counts greater than or equal to the bit width of the shifted value are illegal and treated as undefined behavior
* wrapping arithmetic uses the Zig style spellings `+%`, `-%`, `*%` and the corresponding compound assignments
* saturating arithmetic uses the Zig style spellings `+|`, `-|`, `*|` and the corresponding compound assignments

## 12. Strings and Characters

Stark distinguishes two text forms:

* `ascii`
* `unicode`

The core owned text container forms:

* `Ascii`
* `Unicode`

String literals infer to:

* `ascii` by default when the decoded literal can be stored as UTF-8
* `unicode` when explicitly requested with a compile time text conversion such as `(unicode)"..."`

Supported escapes in string and character literals:

* simple escapes: `\\`, `\"`, `\'`, `\0`, `\b`, `\t`, `\n`, `\f`, `\r`
* hex escapes: `\xNN`
* unicode escapes: `\uNNNN`

Character literals follow the same inference path instead of using a dedicated standalone `char` type.

The text runtime contract:

* `ascii` is a UTF-8 text view
* `unicode` is a UTF-32 text view
* `Ascii` and `Unicode` are the owning text container forms
* `text[]` returns the full same kind text view
* `text[index]` returns a same kind one element text view
* `text[start, length]` returns another zero copy text view of the same text kind
* explicit text conversion is required where widening, narrowing, or ownership changes are involved

### 12.1 Interpolated Text

Stark supports C# style interpolated text literals. If every `{...}` hole can be folded at compile time, the whole interpolation behaves like one ordinary text constant:

```stark
finite law ascii ScoreLabel() {
    const score = 100;
    return $"Score: {score}";
}
```

Each `{...}` hole is parsed and checked as an ordinary Stark expression. Compile time holes may be integer values, floating point values, `bool`, or text literals. Constant interpolation chooses `ascii` or `unicode` from the folded literal contents and can be target typed where an ordinary text literal conversion is valid.

Runtime holes need caller selected storage:

```stark
fn Ascii ScoreLabel(i32[-2147483648 2147483647] score) {
    stack Ascii label[64] = $"Score: {score}";
    return label;
}
```

The `[64]` is the buffer capacity. Runtime interpolation writes through the `System.Text` formatting and concatenation APIs into the selected `Ascii` or `Unicode` buffer. If the selected capacity is too small, the generated code traps instead of throwing an exception or silently cutting off text. Use the `System.Text` APIs directly when overflow should be handled as a returned value.

The rules:

* `$"..."` uses the same literal decoding rules as ordinary string literals
* constant interpolation chooses `ascii` or `unicode` from the folded literal contents and can be target typed to either text view where an ordinary text literal conversion is valid
* fixed capacity runtime interpolation chooses `Ascii` or `Unicode` from the destination buffer type
* each runtime hole must already be matching text, or must have a known fixed buffer formatter such as `TryFormatI32Ascii` or `TryFormatBoolUnicode`
* fully constant interpolations fold to ordinary text constants
* runtime interpolation uses Stark's ordinary no exception failure model internally and keeps the destination capacity visible in source

### 12.2 Text Concatenation and Planned Conversion

Stark supports `+` for compile time text constants:

```stark
finite law ascii ScoreLabel() {
    return "Score: " + "100";
}
```

This is one ordinary text constant, so it does not allocate or copy at runtime.

Stark also supports the common literal prefix runtime form when the right side returns an explicit owned text result:

```stark
fn System.Memory.MemoryResult<System.Text.OwnedAscii> ScoreLabel(i64[min max] score) {
    return "Score: " + score.ToAscii();
}
```

This returns `System.Memory.MemoryResult<System.Text.OwnedAscii>`, so allocation failure remains visible to the caller.

Text concatenation is intended for readable, ordinary code. When runtime text must be copied into caller owned storage, put the capacity on the stack text buffer:

```stark
stack Ascii left = System.Console.ReadAsciiLine();
stack Ascii right = System.Console.ReadAsciiLine();
stack Ascii combined[4096] = left + right;
```

The `[4096]` is the destination storage. It must be a positive compile time integer, and this narrow syntax is currently only for stack `Ascii` and `Unicode` locals. The declaration uses fixed local storage and the same `System.Text.TryConcatAscii` or `System.Text.TryConcatUnicode` copy behavior available through explicit library code. If the joined text does not fit, execution traps instead of silently truncating.

Use the explicit `System.Text.TryConcatAscii` and `System.Text.TryConcatUnicode` APIs when overflow needs to be recoverable.

The implemented conversion surface includes:

* `ToAscii()` and `ToUnicode()` formatting for integer, floating point, bool, and enum values
* parse APIs from `ascii` and `unicode` to numeric, bool, and enum values
* fixed buffer formatting APIs for no allocation paths
* result and status based failure reporting for conversions that can fail
* locale independent defaults for ordinary numeric formatting

The first explicit owned conversion APIs live as ordinary `System.Text` functions and method style convenience calls for bool, integer, floating point, and the first concrete standard library enum values:

```stark
stack System.Memory.MemoryResult<System.Text.OwnedAscii> label =
    System.Text.ToAscii((i64)42);

stack i64[-9223372036854775808 9223372036854775807] score = 42;
stack System.Memory.MemoryResult<System.Text.OwnedAscii> methodLabel =
    score.ToAscii();

stack System.Memory.MemoryResult<System.Text.OwnedAscii> encodingLabel =
    System.Text.Encoding.UTF8.ToAscii();
```

These return `System.Memory.MemoryResult<T>` so allocation failure and unsupported formatting are visible in ordinary code.

The first implemented fixed buffer formatting primitives are `System.Text.TryFormatBoolAscii`, fixed width signed and unsigned integer `Ascii` helpers such as `TryFormatI24Ascii`, `TryFormatI32Ascii`, `TryFormatI48Ascii`, `TryFormatI128Ascii`, `TryFormatI1024Ascii`, and `TryFormatU1024Ascii`, and the matching `Unicode` forms for integer widths through 1024 bits. The integer forms write base-10 text into caller owned `Ascii` or `Unicode` storage. These APIs return `false` when the destination is missing or too small.

The first implemented no allocation floating point formatting primitives are `TryFormatF64Ascii`, `TryFormatF32Ascii`, `TryFormatF64Unicode`, and `TryFormatF32Unicode`. This initial slice writes fixed six fractional digit decimal text such as `3.250000` into caller owned storage for finite values in the supported range and returns `false` when the value is unsupported or the destination is too small.

The default value text rules are intentionally plain and locale independent:

* bool values use lowercase `true` or `false`
* integers use base 10, no digit separators, no prefixes, and no leading `+`
* negative signed integers use one leading `-`, including signed minimum values
* zero uses `0`
* the first implemented floating point formatting slice uses fixed six fractional digits for finite supported values
* complete shortest round trip floating point formatting remains future work; the current `f32` and `f64` formatting slice writes fixed six fractional digit finite values
* the first implemented enum formatting and parsing APIs cover `System.Text.Encoding` and `System.Text.TextError` using exact declared case names
* general enum formatting remains future work; the current enum formatting and parsing APIs cover `System.Text.Encoding` and `System.Text.TextError`

The first text to value parsing primitives are exact bool and integer parsers through 1024 bit widths. `System.Text.ParseBoolAscii` and `System.Text.ParseBoolUnicode` accept only exact lowercase `true` or `false`. The `ParseI*Ascii`, `ParseI*Unicode`, `ParseU*Ascii`, and `ParseU*Unicode` integer forms through `i1024` and `u1024` accept only base-10 integer text. They return `System.Text.TextResult<T>` so invalid text and overflow are handled as ordinary data:

```stark
stack System.Text.TextResult<bool> parsed = System.Text.ParseBoolAscii("true");
stack System.Text.TextResult<i64> count = System.Text.ParseI64Ascii("-42");
```

## 13. FFI and Raw Boundaries

`ffi` marks the foreign boundary.

At that boundary:

* raw pointers are allowed
* nested raw pointers are allowed
* null may appear in raw pointer values and raw pointer checks
* foreign ABI shape is preserved
* Stark enums do not cross `ffi` or `export` boundaries
* foreign code must not unwind through Stark frames

Outside that boundary:

* safe borrows are non null
* safe values may not be assigned `null`
* pointers to pointers are not part of ordinary safe Stark code
* conversions from raw pointers into safe borrows must be explicit

### 13.1 C Style Varargs

Foreign C APIs that use variadic arguments may be declared with `ffi varargs`:

```stark
public ffi varargs fn i32 printf(ascii format);
```

`varargs` is only valid on `ffi` declarations that end with `;`. Stark functions do not define C style variadic bodies.

The fixed parameters are checked normally. Extra call arguments are allowed after the fixed parameters, but they must already be safe to pass through the C varargs ABI as written:

* `i32`, `u32`, or wider integers
* `f64`
* raw pointers
* `ascii` and `unicode` text views, which pass their data pointer

Stark does not hide C's default argument promotions. If a C variadic function expects a floating point value, pass `f64`; cast `f32` to `f64` yourself. If a value is smaller than 32 bits, cast it to an explicit `i32` or `u32` first.

```stark
public ffi varargs fn i32 printf(ascii format);

fn i32 PrintScore(i32[min max] score) {
    return printf("Score: %d\n", score);
}
```

### 13.2 Unsafe Operations

Stark's unsafe model marks proof boundaries rather than disabling the language's ordinary safety rules.

Unsafe code may perform only operations that are explicitly gated as unsafe. Ownership, initialization, range, type, and ordinary borrow validation still apply inside unsafe code.

The unsafe forms:

```stark
unsafe fn rawmutptr<T> FromAddress<T>(i64[0 max] address);

fn void UseAddress(i64[0 max] address) {
    unsafe {
        stack rawmutptr<State> state = FromAddress<State>(address);
    }
}
```

Unsafe operation markers may also appear at the operation that crosses the proof boundary:

```stark
RegisterCallback(capture(unsafe addr token) () => {
    return 0;
});
```

FFI imports that expose raw platform obligations should be declared as unsafe or wrapped behind a safe Stark API. The standard library should keep unsafe raw and FFI operations inside small implementation boundaries and expose ordinary result and status based safe APIs where possible.

Unsafe requirements are not erased by ordinary callable values. Until Stark has an explicit unsafe function pointer type, an `unsafe fn` may be called only directly from an unsafe context and may not be stored in an ordinary `fnptr`.

## 14. Runtime Surface

Stark has no exceptions and no stack unwinding.

The runtime contract:

* recoverable errors are represented as ordinary values
* panic, assert, and failure paths are unrecoverable and do not unwind
* unrecoverable failure terminates execution through a trap or abort style path
* the canonical hosted entrypoint is `export ffi fn i32 main()`
* normal process termination happens by returning from `main`
* foreign unwinding into or through Stark code is unsupported

## 15. Closed by Default

Stark is designed with a closed by default bias. For ordinary Stark code, that means the language leans toward static structure and explicit boundaries.

The programmer facing consequences:

* static dispatch by default
* restrictive visibility by default
* most declarations stay inside module or package boundaries unless deliberately exposed
* open world and dynamic patterns are explicit choices, not the default style

Additional rationale for how Stark uses this model lives in [LanguageInternals.md](../Internals/LanguageInternals.md).
