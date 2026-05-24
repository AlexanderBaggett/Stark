# Stark Closure Proposal

This document defines the closure model Stark uses for immediate-mode UI,
callback-heavy standard-library helpers, and performance-sensitive higher-order
code. It focuses on the three closure forms that matter for that programming
style:

- `inline closure<...>` for call-now code that specializes away.
- `borrow closure<...>` for non-escaping runtime callbacks.
- `heap closure<...>` for stored, returned, or retained callbacks.

`fnptr<...>` remains the existing thin function pointer. It accepts named
function items and non-capturing lambdas only. A `fnptr` does not carry closure
storage and never captures local state.

Closures are not implicit-capture C# closures and not Rust's exact
`Fn`/`FnMut`/`FnOnce` trait model. Stark closures expose capture mode, storage,
escape, call capability, function guarantees, and memory-region contracts in
the type or target expression.

## 1. Design Rules

The closure rules are:

- capture is always explicit
- closure storage is always visible
- closure escape is checked by the borrower system
- mutation of a closure environment is part of the closure type
- consuming a closure environment is part of the closure type
- function kind is preserved through closure calls
- memory-region contracts are preserved through closure calls
- accepted closure programs lower directly; invalid closure programs fail
  before MIR

LLVM has no native closure concept. Stark lowers closures into ordinary
functions, environment storage, fat callable values, specialization clones, and
LLVM attributes. The source contract is what makes the lowered IR optimizable.

## 2. Closure Type Shape

A closure signature has a call capability, function kind, return type,
parameters, and optional memory contract:

```stark
closure<fn i32[min max](i32[min max])>
closure<finite i32[min max](i32[min max])>
closure<law bool(borrow Item)>
closure<finite law i32[min max](i32[min max])>

closure<mut fn void(i32[min max])>
closure<mut finite void(i32[min max])>

closure<once fn Packet()>
closure<once finite Packet()>

closure<fn void(mut borrow Buffer, mut borrow Buffer) where overlap(arg0, arg1)>
closure<fn void(rawptr<i32[min max]>[arg1], u8[1 10])>
```

The call capability is:

- no marker: repeated calls are allowed and invocation does not mutate or
  consume the closure environment
- `mut`: repeated calls are allowed, but invocation may mutate the closure
  environment and requires mutable access to the closure value
- `once`: invocation consumes the closure value

The initial rule is intentionally strict:

- `mut` closures may be `fn` or `finite`
- `once` closures may be `fn` or `finite`
- `law` and `finite law` closures use the no-marker capability

This keeps `law` closure calls free of hidden environment mutation and hidden
consumption. If a later design needs a private scratch-mutating law closure,
that is a separate feature with a separate proof obligation.

Because closure type signatures do not name parameters, memory contracts use
synthetic names:

```stark
arg0
arg1
arg2
```

Default non-overlap applies to memory-backed closure call arguments unless the
closure type declares `where overlap(...)` or `where same(...)`.

## 3. Capture Syntax

Capture is never implicit. A lambda body can use only:

- lambda parameters
- globals and constants visible in scope
- named function items
- names listed in the capture clause

The capture clause is:

```stark
capture(copy scale, read table) (u64[0 max] index) =>
{
    return table[index] * scale;
}
```

Capture modes:

- `capture(copy x)`: copies a cheap copyable value into the closure
  environment or inline specialization record.
- `capture(move x)`: moves ownership into the closure environment. The source
  binding is consumed.
- `capture(read x)`: captures readonly access to existing storage.
- `capture(mut x)`: captures exclusive mutable access to existing storage.
- `capture(out x)`: captures a write-only destination.
- `capture(init x)`: captures uninitialized destination storage that must be
  initialized before successful return.
- `capture(unsafe addr x)`: captures address or identity information without
  ordinary dereference authority.
- `capture(unsafe shared x)`: publishes a value or capability into a shared or
  concurrent domain.

Unsafe capture modes require an unsafe context and keep the `unsafe` marker in
the capture list.

Duplicate capture names are rejected. A captured name shadows no lambda
parameter. A lambda parameter cannot reuse a captured name.

## 4. Inline Closures

`inline closure<...>` is the primary form for egui-style immediate APIs. It is
not a runtime value. It is a compile-time callable parameter that causes the
callee to specialize for the closure body and capture facts.

```stark
module Demo

struct Ui
{
    fn void Label(mut borrow Ui self, ascii text)
    {
        return;
    }

    fn void TextEdit(mut borrow Ui self, mut borrow ascii text)
    {
        return;
    }
}

inline fn void Horizontal(
    mut borrow Ui ui,
    inline closure<fn void(mut borrow Ui)> body)
{
    body(ui);
    return;
}

fn void Draw(mut borrow Ui ui, mut borrow ascii name)
{
    Horizontal(
        ui,
        capture(mut name) (mut borrow Ui row) =>
        {
            row.Label("Name");
            row.TextEdit(name);
            return;
        });

    return;
}
```

The compiler specializes `Horizontal` for the lambda body. The optimized result
is equivalent to a direct block:

```stark
fn void Draw(mut borrow Ui ui, mut borrow ascii name)
{
    ui.Label("Name");
    ui.TextEdit(name);
    return;
}
```

No closure object is allocated. No function pointer is formed. No indirect call
is required.

### 4.1 Inline Closure Use Cases

Inline closures are used for helpers that call the callback during the current
operation:

```stark
inline fn void Window(
    mut borrow Ui ui,
    ascii title,
    inline closure<fn void(mut borrow Ui)> body)
{
    ui.BeginWindow(title);
    body(ui);
    ui.EndWindow();
    return;
}

inline fn void DisabledIf(
    mut borrow Ui ui,
    bool disabled,
    inline closure<fn void(mut borrow Ui)> body)
{
    ui.PushDisabled(disabled);
    body(ui);
    ui.PopDisabled();
    return;
}

fn void DrawSettings(mut borrow Ui ui, mut borrow Settings settings)
{
    Window(
        ui,
        "Settings",
        capture(mut settings) (mut borrow Ui panel) =>
        {
            panel.Checkbox(settings.Enabled, "Enabled");

            DisabledIf(
                panel,
                !settings.Enabled,
                capture(mut settings) (mut borrow Ui inner) =>
                {
                    inner.Slider(settings.Volume, 0, 100);
                    return;
                });

            return;
        });

    return;
}
```

Nested inline closures specialize through nested helper calls. The optimizer
sees the same UI borrow and the same captured `settings` borrow after
specialization.

### 4.2 Inline Closure Restrictions

An `inline closure<...>`:

- cannot be stored in a local or field
- cannot be returned
- cannot be placed in an array
- cannot be converted to `fnptr`
- cannot cross a package ABI boundary as a runtime value
- can appear only as an inline closure parameter or an equivalent compile-time
  generic specialization input

An inline closure parameter is part of the callee's specialization key. Package
images must preserve enough typed body information for imported inline closure
specialization, or reject the use before MIR with a precise diagnostic.

### 4.3 Inline Closure Call Capability

Inline closures still use the call capability in the type:

```stark
inline closure<fn void(mut borrow Ui)>
inline closure<mut fn void(i32[min max])>
inline closure<once fn Packet()>
```

For no-marker inline closures, the body may be invoked repeatedly and may not
mutate the closure environment.

For `mut` inline closures, the body may be invoked repeatedly and may mutate
captured `mut`, `out`, or `init` state:

```stark
inline fn void Repeat(
    u64[0 max] count,
    inline closure<mut fn void(u64[0 max])> body)
{
    for willexit (stack mut u64[0 max] index = 0; index < count; index += 1)
    {
        body(index);
    }

    return;
}

fn i32[min max] Count4()
{
    stack mut i32[min max] total = 0;

    Repeat(
        4,
        capture(mut total) (u64[0 max] index) =>
        {
            total += 1;
            return;
        });

    return total;
}
```

For `once` inline closures, the receiving function may invoke the closure at
most once. If the body is invoked, the closure environment is consumed:

```stark
inline fn Packet BuildWith(
    inline closure<once fn Packet()> producer)
{
    return producer();
}
```

Ownership validation rejects a second invocation of the same `once` inline
closure parameter inside the specialized callee.

## 5. Borrow Closures

`borrow closure<...>` is a non-escaping runtime callback view. It is used when
the callback must be passed as a runtime value during the current call graph,
but it is not stored for later.

```stark
module Demo

struct Ui
{
    fn void BeginGroup(mut borrow Ui self)
    {
        return;
    }

    fn void EndGroup(mut borrow Ui self)
    {
        return;
    }
}

fn void WithGroup(
    mut borrow Ui ui,
    borrow closure<fn void(mut borrow Ui)> body)
{
    ui.BeginGroup();
    body(ui);
    ui.EndGroup();
    return;
}

fn void Draw(mut borrow Ui ui, mut borrow Settings settings)
{
    WithGroup(
        ui,
        capture(mut settings) (mut borrow Ui group) =>
        {
            group.Checkbox(settings.Enabled, "Enabled");
            return;
        });

    return;
}
```

The closure expression creates a temporary closure environment valid for the
call to `WithGroup`. `WithGroup` can pass the borrowed closure to other
non-escaping helpers, but it cannot store it or return it.

### 5.1 Borrow Variations

Borrow closures compose with Stark's borrow classes:

```stark
borrow closure<fn void(mut borrow Ui)>
mut borrow closure<mut fn void(i32[min max])>
retborrow closure<fn bool()>
storeborrow closure<fn void()>
```

The normal forms are:

- `borrow closure<...>` for non-mutating closure invocation or immutable access
  to the closure value
- `mut borrow closure<mut ...>` for invoking a closure that mutates its
  environment

`retborrow closure<...>` is valid only when returning a borrowed closure view
tied to an input closure or input storage. It cannot return a closure
environment built from the current stack frame.

`storeborrow closure<...>` is valid only for storage-bearing APIs that
explicitly model borrowed callback storage. It is not the default UI shape.
Most egui-style APIs use `inline closure` or non-escaping `borrow closure`.

### 5.2 Mutable Borrow Closure Example

```stark
module Demo

fn void PushEvent(
    mut borrow closure<mut fn void(i32[min max])> sink,
    i32[min max] value)
{
    sink(value);
    return;
}

fn i32[min max] Run()
{
    stack mut i32[min max] total = 0;

    stack mut closure<mut fn void(i32[min max])> add =
        capture(mut total) (i32[min max] value) =>
        {
            total += value;
            return;
        };

    PushEvent(add, 4);
    PushEvent(add, 8);

    return total;
}
```

Calling `closure<mut ...>` requires mutable access to the closure value because
the environment can change. Passing it as `borrow closure<mut ...>` is rejected.

### 5.3 Borrow Closure Capture Rules

A borrowed closure may capture:

- `copy` values
- `read` borrows whose lifetime covers the borrowed closure
- `mut` borrows whose exclusive lifetime covers the borrowed closure
- `out` and `init` destinations whose write contracts are satisfied by the
  closure body and call path
- `move` values, as long as the temporary closure environment owns and drops
  the moved values before the borrowed closure expires

Borrowed closures cannot extend the lifetime of captured `read`, `mut`, `out`,
or `init` storage. The closure environment lifetime is the intersection of the
captured lifetimes and the borrow of the closure value.

### 5.4 Borrow Closure Lowering

A borrowed closure lowers to a fat value:

```text
{ invoke_pointer, environment_pointer }
```

The invoke function has this internal shape:

```text
R invoke(ptr env, arg0, arg1, ...)
```

For a call-site temporary, the environment is stack allocated in the caller.
For a named `stack closure`, the environment is the closure local's storage.
The borrowed closure view passed to the callee contains only pointers.

The compiler emits:

- `nonnull` for source-visible closure and environment pointers
- `dereferenceable(N)` and `align(N)` for known environment layouts
- `noalias` when capture and borrower facts prove exclusive environment storage
- `captures(none)` or equivalent no-capture facts when the callee cannot retain
  the environment pointer
- `memory(read)` or `memory(argmem: read)` for readonly closure bodies where
  valid
- `mustprogress` and `willreturn` for `finite` closure invoke functions where
  valid

If the closure target is statically known at a call site after inlining or
devirtualization, the call lowers to a direct invoke-function call and normal
LLVM inlining can remove the fat value.

## 6. Heap Closures

`heap closure<...>` is an owned runtime closure whose environment is heap
allocated. It is the form for retained UI callbacks, event handlers, queued
commands, timers, and callbacks returned from factories.

```stark
module Demo

struct Button
{
    heap closure<fn void()> OnClick;
}

fn void Configure(mut borrow Button button, Command command)
{
    button.OnClick = heap capture(move command) () =>
    {
        command.Execute();
        return;
    };

    return;
}
```

The `heap` marker is deliberate. It says the closure environment outlives the
current call and requires heap storage.

### 6.1 Returning Heap Closures

```stark
module Demo

fn heap closure<fn i32[min max](i32[min max])> MakeAdder(i32[min max] offset)
{
    return heap capture(copy offset) (i32[min max] value) => value + offset;
}

fn i32[min max] Run()
{
    heap closure<fn i32[min max](i32[min max])> addTen = MakeAdder(10);
    return addTen(5);
}
```

The returned closure owns its environment. The captured `offset` lives in heap
closure storage, not in the factory stack frame.

### 6.2 Heap Closure Capture Rules

Heap closures may capture:

- `copy` values
- `move` owned values
- function pointers
- safe readonly values with permanent `const` provenance when the type system
  can prove the provenance
- `unsafe shared` values in an unsafe context

Heap closures do not capture ordinary stack borrows by default. The following
is rejected:

```stark
fn heap closure<fn i32[min max]()> Bad()
{
    stack i32[min max] value = 7;

    return heap capture(read value) () => value;
}
```

The fix is to copy or move data into the heap closure environment:

```stark
fn heap closure<fn i32[min max]()> Good()
{
    stack i32[min max] value = 7;

    return heap capture(copy value) () => value;
}
```

Mutable heap closures require explicit shared or owned state. Capturing a local
mutable borrow into heap storage is rejected because the local does not live as
long as the heap closure:

```stark
fn heap closure<mut fn void(i32[min max])> BadCounter()
{
    stack mut i32[min max] total = 0;

    return heap capture(mut total) (i32[min max] value) =>
    {
        total += value;
        return;
    };
}
```

The valid version moves an owned state object into the environment:

```stark
struct Counter
{
    i32[min max] Total;
}

fn heap closure<mut fn void(i32[min max])> MakeCounter()
{
    heap Counter counter = new Counter()
    {
        Total = 0
    };

    return heap capture(move counter) (i32[min max] value) =>
    {
        counter.Total += value;
        return;
    };
}
```

### 6.3 Heap Closure Ownership

A heap closure is an owned value. Moving the closure moves ownership of the
environment. Dropping the closure drops the environment and any captured owned
values.

```stark
fn void Register(heap closure<fn void()> callback)
{
    callback();
    return;
}

fn void Run(Command command)
{
    heap closure<fn void()> callback =
        heap capture(move command) () =>
        {
            command.Execute();
            return;
        };

    Register(callback);

    // Rejected: `callback` was moved into `Register`.
    callback();
    return;
}
```

A heap `closure<once ...>` is consumed by invocation:

```stark
struct Packet
{
    i32[min max] Code;
}

fn Packet RunOnce(heap closure<once fn Packet()> producer)
{
    return producer();
}

fn Packet Build()
{
    heap Packet packet = new Packet()
    {
        Code = 42
    };

    heap closure<once fn Packet()> producer =
        heap capture(move packet) () =>
        {
            return packet;
        };

    return RunOnce(producer);
}
```

After `producer()` is called, the closure value is consumed. Ownership
validation rejects a second call.

### 6.4 Heap Closure Lowering

A heap closure value is represented as:

```text
{ invoke_pointer, environment_pointer, drop_pointer? }
```

The drop pointer can be omitted when the environment has a statically known
drop path from the closure type and capture layout. The compiler model must
retain enough capture layout information to generate the correct drop path.

The environment allocation stores captured values in declaration order from the
capture clause. Captured owned values are moved into the environment. Captured
copy values are copied into the environment. The closure value owns the
environment pointer.

Heap closure invoke functions receive `ptr env` as their first internal
parameter, followed by source parameters. Mutable heap closure invocation uses
mutable access to the closure value and mutable access to the environment.

## 7. Conversion And Target Typing

Lambda expressions are target typed. A lambda without a closure or `fnptr`
target is rejected.

Valid targets:

```stark
fnptr<fn i32[min max](i32[min max])>
inline closure<fn void(mut borrow Ui)>
borrow closure<fn void(mut borrow Ui)>
mut borrow closure<mut fn void(i32[min max])>
heap closure<fn void()>
heap closure<once fn Packet()>
```

Conversions:

- non-capturing lambda to `fnptr<...>`: allowed
- capturing lambda to `fnptr<...>`: rejected
- lambda to `inline closure<...>`: allowed when used as a specialization input
- lambda to `borrow closure<...>`: allowed when the environment lifetime covers
  the borrow
- lambda to `heap closure<...>`: allowed when all captures are heap-safe
- non-capturing lambda to closure: allowed and represented as a closure with an
  empty environment
- named function item to `fnptr<...>`: allowed
- named function item to closure: allowed as an empty-environment closure only
  when a closure target is explicit

Function kind compatibility follows the existing callable rules. A stronger
body can satisfy a weaker target. A weaker body cannot satisfy a stronger
target.

## 8. Compiler Implementation Notes

### 8.1 Parser

Add closure type parsing beside `fnptr`:

```antlr
closureType
    : storageClass? borrowClass? 'closure' '<' closureSignature '>'
    ;

closureSignature
    : closureCallCapability? functionKind returnType '(' parameterTypes? ')' whereClause?
    ;

closureCallCapability
    : 'mut'
    | 'once'
    ;
```

The exact grammar can reuse existing function-pointer signature parsing. The
important implementation rule is that `closure<...>` and `fnptr<...>` share
parameter, return, function-kind, raw-pointer-bound, and memory-contract
parsing.

`inline closure<...>` is parsed as an inline closure parameter form, not as a
runtime storage class.

`heap closure<...>` is parsed as an owned storage class plus closure type.

### 8.2 Type Model

Add a closure type symbol with:

- storage requirement: inline, borrowed view, owned heap
- borrow class: none, `borrow`, `mut borrow`, `retborrow`, `storeborrow`
- call capability: normal, `mut`, `once`
- function kind: `fn`, `finite`, `law`, `finite law`
- return type
- parameter types
- parameter memory contracts
- raw pointer bound expressions
- capture layout for closure expressions
- environment mutability and ownership facts

Closure type equality includes call capability, function kind, return type,
parameter types, raw pointer bounds, and memory contracts. Storage and borrow
class are part of the containing type expression in the same way they are for
other Stark storage and borrower forms.

### 8.3 Type Checking

Type checking performs target-typed lambda binding:

1. Resolve the expected target type.
2. Validate lambda parameter count and parameter types against the target.
3. Validate the lambda body against the target return type.
4. Validate the lambda body under the target function kind.
5. Validate capture names and capture modes.
6. Reject implicit outer local use.
7. Build a typed lambda record with capture records.
8. Validate target-specific capture legality.

For `inline closure`, type checking records the lambda body and captures as
specialization inputs.

For `borrow closure`, type checking records a runtime closure expression with a
non-escaping environment.

For `heap closure`, type checking validates heap-safe captures and records an
owned environment layout.

### 8.4 Semantic And Ownership Validation

Validation enforces:

- no implicit capture
- no duplicate captures
- no capture of moved bindings
- `copy` captures only copyable values
- `move` captures consume the source binding
- `read` captures do not expose writable bindings
- `mut` captures require writable bindings and exclusive access
- `out` captures are write-only
- `init` captures are write-only initialization storage
- unsafe captures require unsafe context
- `borrow closure` environments do not escape
- `retborrow closure` returns are tied to input closure/storage roots
- `storeborrow closure` storage is explicit and lifetime-valid
- `heap closure` captures are heap-safe
- `closure<mut ...>` calls require mutable closure access
- `closure<once ...>` calls consume the closure value
- `law` closure bodies satisfy law restrictions
- `finite` closure bodies satisfy finite restrictions
- closure call arguments satisfy default non-overlap and explicit memory
  contracts

### 8.5 MIR

MIR needs direct closure forms rather than lowering closures through ad hoc
calls:

- `CreateBorrowClosure`
- `CreateHeapClosure`
- `InvokeClosure`
- `InvokeInlineClosure`
- `MoveClosure`
- `DropClosure`

`InvokeInlineClosure` exists only before specialization. After specialization,
it is replaced by direct MIR for the lambda body.

Runtime closure creation records:

- invoke function symbol
- environment allocation kind
- capture field layout
- capture initialization operations
- drop path

### 8.6 SSA

SSA represents runtime closure values as typed aggregate values or addressable
pairs, depending on ABI lowering:

```text
closure = { invoke_ptr, env_ptr }
```

Heap closures also carry drop information in the compiler model. SSA does not
need to materialize a drop pointer when static drop lowering is available.

SSA invocation extracts the invoke pointer and environment pointer, then emits
an indirect call:

```text
result = call invoke(env, arg0, arg1)
```

If the invoke pointer is statically known, optimization rewrites the indirect
call to a direct call.

### 8.7 LLVM

Runtime closure invoke functions lower as internal functions:

```llvm
define internal fastcc <ret> @lambda.invoke(ptr <env>, <args...>)
```

The environment pointer receives attributes from capture facts:

- `nonnull` when the environment is real
- `dereferenceable(N)` for known layout size
- `align(N)` for known layout alignment
- `noalias` for exclusive environment storage
- `captures(none)` or equivalent no-capture facts for non-escaping borrowed
  closures
- `readonly` or `memory(argmem: read)` for readonly environments

Closure call sites use direct calls when the invoke target is statically known.
Indirect calls preserve function-kind attributes and memory effects when the
closure type proves them.

Inline closures do not emit runtime closure IR. They emit specialized caller
IR after clone/substitution.

### 8.8 Package Images

Package images encode:

- closure type signatures
- call capability
- function kind
- parameter and return types
- memory contracts
- raw pointer bound expressions
- public function signatures involving closure types
- typed bodies needed for inline closure specialization across package
  boundaries

Package images do not serialize runtime closure instances. They serialize
types, bodies, and optimization facts needed to compile consumers.

If an imported function requires inline closure specialization and the package
does not contain the required typed body, compilation fails before MIR with a
diagnostic explaining that the inline closure target cannot be specialized.

## 9. Diagnostics

Required diagnostics:

- lambda expression requires explicit target type
- capturing lambda cannot convert to `fnptr<...>`
- unknown capture mode
- unsafe capture mode requires unsafe context
- duplicate capture name
- captured name is not in scope
- lambda body uses uncaptured local
- `copy` capture requires copyable value
- `move` capture consumes source binding
- `mut` capture requires writable binding
- `out` capture cannot be read
- `init` capture must be initialized before return
- borrowed closure environment escapes
- heap closure cannot capture stack borrow
- mutable closure call requires mutable closure access
- once closure was already consumed
- closure body violates target function kind
- closure call violates memory contract
- inline closure target body unavailable for specialization

Diagnostics are front-end diagnostics. Closure lowering is not a language
validity filter.

## 10. Required Tests

Parser tests:

- all closure type forms parse
- nested closure signatures parse
- memory contracts in closure signatures parse
- raw pointer bounds in closure signatures parse
- invalid closure capability ordering is rejected

Type-checking tests:

- non-capturing lambdas still convert to `fnptr`
- capturing lambdas reject `fnptr`
- inline closure target typing works
- borrow closure target typing works
- heap closure target typing works
- function-kind compatibility is enforced
- memory contracts are enforced at closure call sites

Ownership tests:

- `move` capture consumes source binding
- `mut` capture creates exclusive access
- borrowed closure cannot escape local captures
- heap closure rejects stack borrows
- heap closure owns moved captures
- mutable closure requires mutable access
- once closure use-after-call is rejected

MIR/SSA tests:

- inline closures specialize before backend emission
- borrow closures create environment storage and invoke records
- heap closures create heap environment initialization and drop paths
- closure invocation carries environment pointer as first internal argument
- known closure invoke targets devirtualize to direct calls

LLVM tests:

- inline closure produces no closure environment allocation
- borrow closure environment pointer receives non-null and no-capture facts
- heap closure emits correct environment initialization and drop
- finite closure invoke emits `mustprogress` and `willreturn`
- law closure invoke emits valid memory-effect attributes
- closure memory contracts emit `noalias` facts where valid

Package-image tests:

- closure signatures round-trip through package images
- inline closure specialization works through imported typed bodies
- missing inline closure typed body fails before MIR
- imported closure memory contracts survive into consumer lowering

## 11. Implementation Order

The implementation order is:

1. Preserve current `fnptr` and non-capturing lambda behavior.
2. Implement closure type parsing and type symbols.
3. Implement explicit capture binding in the typed lambda model.
4. Implement `inline closure<...>` specialization.
5. Implement `borrow closure<...>` runtime fat views.
6. Implement `heap closure<...>` owned environments and drop paths.
7. Add package-image support for closure signatures and inline closure typed
   bodies.
8. Add book and language-reference chapters with egui-style examples.

This order gives Stark the immediate-mode UI programming model first, while
keeping runtime closure storage explicit and measurable.
