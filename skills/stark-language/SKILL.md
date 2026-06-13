---
name: stark-language
description: Stark language development guidance for writing, reviewing, explaining, and editing .stark source files, Stark.toml project manifests, and Stark.solution.toml solution manifests. Use for Stark syntax, ownership and borrowing, callable values, modules and visibility, project/test/native-package setup, FFI and assembly boundaries, memory contracts, thread-safety law declarations, and Stark source style.
---

# Stark Language

Use this skill when producing or reviewing Stark code. Keep code performance-first, explicit, and close to the existing project style. Prefer restrictive visibility, direct calls, explicit memory contracts, and safe ownership/borrow forms before raw pointers or FFI.

## Source Shape

A source file has imports, then one module declaration, then declarations:

```stark
import System.Console
import System.IO
module Demo.App

export fn i32[min max] main()
{
    if (WriteLine("Hello") != IOStatus.Ok)
    {
        return 1;
    }

    return 0;
}
```

Rules:

- One source file declares exactly one module.
- Imports appear before `module`; neither imports nor `module` use semicolons.
- Wildcard imports are forbidden.
- `export import Some.Module` is the only re-export form.
- Importing a module makes visible top-level names available by final name; use fully qualified names only for ambiguity or clarity.
- One file is one module; modules are not reopened across files.
- Comments: `//`, non-nesting `/* */`, and C#-style XML doc forms `///` and `/** */`.

Ambiguity caution: when two imported modules export the same final name (e.g. `Contains` in both `System.Testing` and `System.Text`, or `CreateTempDirectory` in both `System.Testing` and `System.FileSystem`), the bare name is rejected — in root-module code by ordinary resolution, and inside an **imported source module** (whose bodies otherwise skip type checking) by a focused scan that reports STK3003 (`Imported symbol 'X' is ambiguous between A.X, B.X. Use a fully qualified name.`) located in the imported file. Qualify ambiguous names. (Before June 2026 the imported-module case crashed `lower-mir` with `Named operand 'X' could not be resolved`; that no longer happens for function/value references — type-position and constructor-body ambiguities remain follow-ups.)

Top-level declarations include functions, structs, records, enums, traits, doctrines, aliases, constants, and globals.

## Visibility

Default to the narrowest usable visibility:

- no keyword: module-private, visible only in the current module
- `internal`: visible within the same package/library
- `public`: source API visible to downstream Stark code
- `export`: real binary symbol for FFI, runtime entrypoints, plugins, or stable ABI boundaries

`public` and `export` are intentionally different. Do not use `export` just because downstream Stark source should call something.

Visibility applies to top-level declarations and member functions. It does not apply to fields, locals, parameters, statements, expressions, or plain imports. Member functions inherit the enclosing type visibility unless explicitly narrowed. A member cannot be more visible than its type. `export` is never inherited; write it explicitly on binary-visible members.

## Functions

Function kinds:

- `fn`: general function
- `finite`: guaranteed progress and return
- `law`: pure/read-only/no visible side effects
- `finite law`: both sets of guarantees; keyword order is fixed

Use the strongest kind the body honestly satisfies; `fn` is the last resort. Kinds compose from the inside out:

- a `law` may only call other laws (STK4106); IO, allocation, process work, and general `fn` calls belong outside laws
- `out` parameters and mutation through `mut borrow` demote a function from `law` (write `finite` instead of `finite law`)
- a `finite` function may only contain `willexit` loops and call finite-or-stronger callees
- stronger function items flow into weaker `fnptr` slots, never the reverse

Common modifiers:

- `inline`, `noinline`, `inlinehint` are mutually exclusive.
- `hot` and `cold` are mutually exclusive.
- `strictfp` disables Stark's ordinary fast floating-point assumptions for that function.
- `ffi` marks a foreign boundary.
- `static` is only for member functions inside `struct` or `record`.

Use semicolon declarations for FFI and forward declarations:

```stark
unsafe ffi fn i32[min max] puts(ascii text);
```

The hosted entrypoint is:

```stark
export fn i32[min max] main()
{
    return 0;
}
```

Use `unsafe` on `main` only if the body or signature crosses an unsafe/raw/foreign boundary.

## Callable Values

Prefer direct named-function calls unless an API needs a callback value.

Function items are named functions used as callable values:

```stark
finite law i32[min max] Inc(i32[min max] value)
{
    return value + 1;
}

stack fnptr<finite law i32[min max](i32[min max])> op = Inc;
```

Use `fnptr<...>` for thin, non-capturing callbacks. The function kind is part of the type:

```stark
fnptr<fn void()>
fnptr<finite i32[min max](i32[min max])>
fnptr<law bool(borrow Item)>
fnptr<finite law i32[min max](i32[min max])>
```

`fnptr` values must come from a compatible named function or non-capturing lambda:

```stark
stack fnptr<fn i32[min max](i32[min max])> square =
    (i32[min max] value) => value * value;
```

Capturing lambdas require an explicit capture list and should use a closure type, not `fnptr`:

```stark
inline fn i32[min max] Apply(
    i32[min max] value,
    inline closure<fn i32[min max](i32[min max])> op)
{
    return op(value);
}

fn i32[min max] AddOffset(i32[min max] offset)
{
    return Apply(10, capture(copy offset) (i32[min max] value) => value + offset);
}
```

Capture modes:

- `copy x`: copy a cheap copyable value
- `move x`: move ownership into the callable
- `read x`: capture readonly access to existing storage
- `mut x`: capture mutable access for the closure lifetime
- `out x`: capture a write-only destination
- `init x`: capture uninitialized destination storage

Closure forms:

- `inline closure<...>`: callback is called by the receiving function and cannot be stored or returned
- `borrow closure<...>`: non-owning callback view; captured storage must outlive the view
- `mut borrow closure<mut ...>`: needed when calling mutates the closure environment
- `heap closure<...>`: owned closure for stored, returned, or retained callbacks
- `heap closure<once ...>`: calling consumes the closure

## Types

Integers must include explicit ranges:

```stark
u8[0 max]
i32[min max]
u64[0 max]
u32[1024 * 1024 1024 * 1024 * 1024]
```

Use `uN` for non-negative runtime ranges and use the narrowest supported width when practical. Prefer `[min max]` for full-width signed ranges and `[0 max]` for full-width unsigned ranges.

Scalar integer constants usually omit a type so Stark derives the exact value and smallest storage width:

```stark
const PageSize = 2 ** 12;
const BoardWidth = 80;
```
* signed integer widths: `i8`, `i16`, `i24`, `i32`, `i48`, `i64`, `i96`, `i128`, `i192`, `i256`, `i384`, `i512`, `i768`, `i1024`
* unsigned integer widths: `u8`, `u16`, `u24`, `u32`, `u48`, `u64`, `u96`, `u128`, `u192`, `u256`, `u384`, `u512`, `u768`, `u1024`

use the smallest integer width you need. If you only return values between 0-4 you can use an `u8` for example.


Floating types are `f16`, `f32`, `f64`, `f80`, and `f128`. Unsuffixed decimals are `f64`; suffix with `f` for `f32`.

Aggregate and view forms:

- fixed array: `T[N]` owns N elements
- slice: `T[]` is a non-owning view
- dynamic storage: `dynamic T` owns growable capacity-backed storage
- named data: `struct`, `record`
- closed variants: `enum`

`void` is valid only as a function return type.

Generic parameter lists can contain ordinary type parameters and typed
compile-time value parameters. Stark spells const generics as `comptime` because
`const` means deep interior immutability:

```stark
finite law u8[0 max] Probe<T, comptime u8[1 4] N>(borrow T[N] values)
{
    return 1;
}
```

Current compiler support covers range-typed integer `comptime` parameters used
as fixed-array lengths (`T[N]`) and inferred from concrete fixed-array
arguments. Explicit integer value arguments participate in generic identity:
write literal values directly after type arguments, and use `comptime N` when
forwarding an enclosing comptime value parameter.
Package images preserve these parameters, symbolic values, and imported generic
template substitutions for the self-hosting integer slice.

```stark
finite law u8[0 max] Length<T, comptime u8[1 8] N>(borrow T[N] values)
{
    return N;
}

stack i32[min max][3] values = { 1, 2, 3 };
stack u8[0 max] size = Length<i32[min max], 3>(values);
```

## Compile-Time Evaluation

Use `comptime expr` for compile-time expression evaluation and `comptime { ... }`
when local compile-time state or bounded loops make the code clearer. Current
compiler support covers ordinary constants, `law` / `finite law` CTFE calls,
fixed-array tables, named aggregates, enum aggregates, concrete layout facts,
ordinary `switch` statements, pattern-condition `if` / `while`, and
fixed-array traversal loops, nested local/index/field place updates, and
compile-time structural facts, including method parameter names and method
generic trait-bound type predicates/metadata, method `where` law predicate type predicates/metadata,
type/field thread-safety law attribute condition type predicates/metadata, and
implemented-trait metadata:

```stark
const PairSize = comptime sizeof(Pair);

finite law i64[min max] LayoutScore()
{
    return comptime sizeof(Pair) + comptime alignof(Pair);
}
```

`sizeof(T)` and `alignof(T)` fold only when `T` has a concrete runtime layout in
the current target context.

`willexit` loops inside CTFE have a compiler iteration budget and report
STK3053 when exhausted. Recursive compile-time `law` / `finite law` calls also
report STK3053 instead of recursing indefinitely.

`switch` inside CTFE supports the ordinary checked pattern surface: literals,
inclusive integer ranges, switch-label or-patterns, exact fixed-array list
patterns, exact aggregate property/positional patterns, enum unit/tuple/named
field patterns, `default`, `when` guards, `_`, and `var` captures. Failed
or-pattern alternatives and false guards roll captures back before trying the
next alternative.

Pattern-condition `if (expr is pattern)` and `while willexit (expr is pattern)`
also execute inside CTFE over the same pattern subset as CTFE `switch`. Captures
are scoped to the matched branch or loop iteration; failed `while` matches exit
the loop.

Explicit `for willexit (... in fixedArray)` traversal executes inside CTFE for
fixed-array constants. Read-only borrowed elements, mutable borrowed elements
with writeback to the source fixed-array place, and explicit index bindings are
supported. CTFE place updates cover locals, fixed-array elements, and named
aggregate fields. Slice and dynamic-storage traversal remain runtime-only until
CTFE has slice/dynamic constant values.

Compiler-known structural predicates live under `System.Compiler` and are
compile-time-only. Use them inside `comptime`; runtime calls are rejected.
For the complete landed fact list, see
`references/comptime-structural-facts-reference.md`.

```stark
finite law i64[min max] Score<T>()
{
    if (comptime System.Compiler.IsInteger<T>())
    {
        return sizeof(T);
    }

    if (comptime System.Compiler.IsStruct<T>())
    {
        return sizeof(T) + alignof(T);
    }

    return 0;
}
```

Available predicates: `IsBool`, `IsInteger`, `IsFloat`, `IsRawPointer`,
`IsFixedArray`, `IsSlice`, `IsDynamic`, `IsFunctionPointer`, `IsClosure`,
`IsNamed`, `IsStruct`, `IsRecord`, `IsEnum`, `IsTrait`, `IsDoctrine`,
`IsDynTrait`, and `HasConcreteLayout`.

Available type layout facts: `TypeSize<T>()` and `TypeAlign<T>()` return
`u64` concrete runtime layout facts; `TypeIsZeroSized<T>()` returns `bool`.
They require concrete layout and are compile-time-only. Use
`HasConcreteLayout<T>()` before asking these facts in generic branches where
`T` may not have layout.

Available scalar type metadata facts: `TypeIntegerBitWidth<T>()` and
`TypeFloatBitWidth<T>()` return `u64`; `TypeIntegerIsSigned<T>()`,
`TypeIntegerIsUnsigned<T>()`, `TypeIntegerIsFullRange<T>()`,
`TypeIntegerMinIs<T, Value>()`, and `TypeIntegerMaxIs<T, Value>()` return
`bool`. Bit-width facts require the matching scalar family. Min/max comparisons
use signed `i1024` compile-time value arguments; use unsignedness plus
full-range predicates for full-width `u1024`.

Available raw-pointer metadata facts: `RawPointerElementTypeIs<T, U>()`,
`RawPointerElementTypeIs*<T>()`,
`RawPointerElementTypeHasConcreteLayout<T>()`, `RawPointerIsMutable<T>()`, and
`RawPointerIsReadOnly<T>()`. They require `T` to be `rawptr<...>` or
`rawmutptr<...>`, inspect the pointee type, and are compile-time-only.

Available element-bearing type metadata facts: `TypeElementTypeIs<T, U>()`,
`TypeElementTypeIs*<T>()`, and `TypeElementTypeHasConcreteLayout<T>()`. They
require `T` to be `rawptr<...>`, `rawmutptr<...>`, `Element[N]`, `Element[]`,
or `dynamic Element`, inspect the element type, and are compile-time-only.
`TypeFixedArrayLength<T>()` returns the concrete fixed-array length as
`u64[0 max]`; `TypeFixedArrayLengthIs<T, Value>()` compares it with a typed
compile-time integer argument. Fixed-array length facts require `T` to be a
fixed array and fold through range-typed integer `comptime` generic lengths
such as `T[N]` after specialization.

Available top-level qualifier metadata facts: `TypeHasQualifiers<T>()`,
`TypeBorrowKindIsNone<T>()`, `TypeBorrowKindIsBorrow<T>()`,
`TypeBorrowKindIsRetBorrow<T>()`, `TypeBorrowKindIsStoreBorrow<T>()`,
`TypeAccessKindIsNone<T>()`, `TypeAccessKindIsShared<T>()`,
`TypeAccessKindIsFrozen<T>()`, `TypeInitializationKindIsNone<T>()`,
`TypeInitializationKindIsOut<T>()`, `TypeInitializationKindIsInit<T>()`,
`TypeIsMutableView<T>()`, and `TypeUnqualifiedTypeIs<T, U>()`.
These facts inspect only the top-level `borrow` / `retborrow` / `storeborrow`,
`shared` / `frozen`, `out` / `init`, and mutable-view markers. Most other type
facts normalize those markers away; use these when a generic needs to branch on
ownership/access/init shape directly.

Available count facts: `FieldCount<T>()` returns the ordered source field count
for structs/records and `0` for other types; `EnumVariantCount<T>()` returns
the enum variant count and `0` for other types. Both return `u64` constants.

Named-type generic metadata facts:

- `TypeGenericParameterCount<T>()` returns the ordinary type parameter count for
  a named type declaration.
- `TypeGenericParameterName<T, I>()` returns the selected ordinary type
  parameter name as an `ascii` compile-time text constant.
- `TypeComptimeGenericParameterCount<T>()` returns the typed `comptime` generic
  parameter count for a named type declaration.
- `TypeComptimeGenericParameterName<T, I>()` returns the selected typed
  `comptime` generic parameter name as an `ascii` compile-time text constant.
- `TypeComptimeGenericParameterTypeIs<T, U, I>()`,
  `TypeComptimeGenericParameterTypeIs*<T, I>()`, and
  `TypeComptimeGenericParameterTypeHasConcreteLayout<T, I>()` inspect the
  selected typed `comptime` generic parameter type. Metadata facts expose its
  display/base names and generic-shape counts.
- `TypeDisplayName<T>()` and `TypeBaseName<T>()` return compile-time `ascii`
  names for the normalized type spelling and named-type base.
- `TypeIsGenericInstantiation<T>()` returns `bool` when `T` is a named type
  with actual type arguments or `comptime` value arguments.
- `TypeArgumentCount<T>()` and `TypeComptimeArgumentCount<T>()` return actual
  type/value argument counts for an instantiated named type.
- `TypeComptimeArgumentName<T, I>()` returns the parameter name for actual
  `comptime` value argument `I` as `ascii`.
- `TypeComptimeArgumentTypeIs<T, U, I>()` returns `bool` when actual
  `comptime` value argument `I` has exact type `U`.
- `TypeComptimeArgumentTypeIs*<T, I>()`,
  `TypeComptimeArgumentTypeHasConcreteLayout<T, I>()`, and
  `TypeComptimeArgumentTypeDisplayName/BaseName/IsGenericInstantiation/ArgumentCount/ComptimeArgumentCount<T, I>()`
  inspect the type of actual `comptime` value argument `I`.
- `TypeComptimeArgumentValueIs<T, I, Value>()` returns `bool` when actual
  `comptime` value argument `I` equals the integer `Value`.
- `TypeArgumentTypeIs<T, U, I>()`, `TypeArgumentTypeIs*<T, I>()`, and
  `TypeArgumentTypeHasConcreteLayout<T, I>()` inspect actual type argument `I`
  and mirror the top-level type predicate vocabulary.
- `TypeArgumentTypeDisplayName/BaseName/IsGenericInstantiation/ArgumentCount/ComptimeArgumentCount<T, I>()`
  expose actual type argument metadata.

For instantiated types, these facts inspect the generic template declaration:
`Buffer<i32[min max], 4>` still reports the declaration parameters from
`Buffer<T, comptime u8[1 8] N>`.

```stark
finite law u64[0 max] ShapeScore<T>()
{
    if (comptime System.Compiler.FieldCount<T>() == 2)
    {
        return 20;
    }

    if (comptime System.Compiler.EnumVariantCount<T>() == 3)
    {
        return 30;
    }

    return 0;
}
```

Available indexed detail facts:

- `FieldOffset<T, I>()`, `FieldSize<T, I>()`, and `FieldAlign<T, I>()` return
  `u64` layout facts for the `I`th struct/record field.
- `FieldIsMisaligned<T, I>()` returns `bool` for packed or explicit layout
  fields whose offset does not satisfy natural alignment.
- `EnumVariantPayloadOffset<T, VariantIndex, PayloadIndex>()`,
  `EnumVariantPayloadSize<T, VariantIndex, PayloadIndex>()`, and
  `EnumVariantPayloadAlign<T, VariantIndex, PayloadIndex>()` return `u64`
  layout facts for the selected enum payload storage field after enum lowering.
- `EnumVariantPayloadIsMisaligned<T, VariantIndex, PayloadIndex>()` returns
  `bool` for enum payload storage fields whose offset does not satisfy natural
  alignment.
- `EnumVariantTag<T, I>()` returns the `I`th source variant's concrete direct
  tag value as `u64`; this may differ from source order when a later unit
  variant receives tag `0`.
- `EnumTagOffset<T>()`, `EnumTagSize<T>()`, and `EnumTagAlign<T>()` return
  `u64` layout facts for the enum tag storage field; `EnumTagIsMisaligned<T>()`
  returns `bool`.
- `StructLayoutIsAuto<T>()`, `StructLayoutIsC<T>()`, and
  `StructLayoutIsExplicit<T>()` return `bool` facts for the declared struct
  layout kind.
- `StructHasPack<T>()` / `StructPack<T>()` expose `[Pack(N)]`; `StructHasAlign<T>()`
  / `StructAlign<T>()` expose `[Align(N)]`.
- `FieldHasExplicitOffset<T, I>()` / `FieldExplicitOffset<T, I>()` expose
  field-level `[FieldOffset(N)]` metadata. `StructPack`, `StructAlign`, and
  `FieldExplicitOffset` return `0` when absent, so branch on the matching
  `Has*` fact when zero is meaningful.
- `EnumVariantPayloadCount<T, I>()` returns `u64` for the `I`th enum variant.
- `EnumVariantIsOk<T, I>()` and `EnumVariantIsErr<T, I>()` return `bool` from
  the variant's `[Ok]` / `[Err]` role attributes.
- `EnumVariantIsErrorFunnel<T, I>()` returns `bool` when the `I`th enum variant
  was declared with the `Name from ErrorType` funnel payload form.
- `EnumVariantAbsorbsErrorTypeIs<T, U, I>()` returns `bool` when that funnel
  absorbs exact type `U`, after substituting generic type and `comptime` value
  arguments from `T`.
- `FieldTypeIs<T, U, I>()` returns `bool` when the `I`th struct/record field
  has exact type `U`.
- `EnumVariantPayloadTypeIs<T, U, VariantIndex, PayloadIndex>()` returns `bool`
  when the selected enum payload field has exact type `U`.
- Field type-category facts mirror the top-level predicates for selected
  fields: `FieldTypeIsBool<T, I>()`, `FieldTypeIsInteger<T, I>()`,
  `FieldTypeIsFloat<T, I>()`, `FieldTypeIsRawPointer<T, I>()`,
  `FieldTypeIsFixedArray<T, I>()`, `FieldTypeIsSlice<T, I>()`,
  `FieldTypeIsDynamic<T, I>()`, `FieldTypeIsFunctionPointer<T, I>()`,
  `FieldTypeIsClosure<T, I>()`, `FieldTypeIsNamed<T, I>()`,
  `FieldTypeIsStruct<T, I>()`, `FieldTypeIsRecord<T, I>()`,
  `FieldTypeIsEnum<T, I>()`, `FieldTypeIsTrait<T, I>()`,
  `FieldTypeIsDoctrine<T, I>()`, and `FieldTypeHasConcreteLayout<T, I>()`.
- Field type metadata facts expose selected field type identity and generic
  shape: `FieldTypeDisplayName<T, I>()`, `FieldTypeBaseName<T, I>()`,
  `FieldTypeIsGenericInstantiation<T, I>()`, `FieldTypeArgumentCount<T, I>()`,
  and `FieldTypeComptimeArgumentCount<T, I>()`.
- Enum payload type-category facts use the same category names with the
  `EnumVariantPayloadType...<T, VariantIndex, PayloadIndex>()` prefix,
  including `EnumVariantPayloadTypeHasConcreteLayout<T, VariantIndex,
  PayloadIndex>()`.
- Enum payload type metadata facts use the same identity/generic-shape names
  with the `EnumVariantPayloadType...<T, VariantIndex, PayloadIndex>()` prefix.
- Function-pointer signature facts include `FunctionPointerParameterCount<T>()`,
  `FunctionPointerReturnTypeIs<T, U>()`,
  `FunctionPointerParameterTypeIs<T, U, I>()`, return/parameter type-category
  predicates, return/parameter nested type metadata facts (`DisplayName`,
  `BaseName`, `IsGenericInstantiation`, `ArgumentCount`, and
  `ComptimeArgumentCount`), exact function-kind predicates (`FunctionPointerKindIsFn`,
  `FunctionPointerKindIsFinite`, `FunctionPointerKindIsLaw`,
  `FunctionPointerKindIsFiniteLaw`), `FunctionPointerIsUnsafe<T>()`,
  `FunctionPointerHasFfiAbi<T>()`, and exact ABI predicates such as
  `FunctionPointerAbiIsC<T>()` and
  `FunctionPointerAbiIsWin64<T>()`. Bounded raw-pointer parameter count
  expressions are exposed with
  `FunctionPointerParameterHasRawPointerElementCountExpression<T, I>()` and
  `FunctionPointerParameterRawPointerElementCountExpression<T, I>()`; `fnptr`
  count expressions use synthetic parameter names such as `arg1`.
- Function-pointer memory-contract facts include
  `FunctionPointerParametersAreDisjoint<T, LeftIndex, RightIndex>()`,
  `FunctionPointerParametersOverlap<T, LeftIndex, RightIndex>()`, and
  `FunctionPointerParametersAreSame<T, LeftIndex, RightIndex>()`. `same`
  counts as overlap for the broad overlap query.
- Closure signature facts include `ClosureParameterCount<T>()`,
  `ClosureReturnTypeIs<T, U>()`, `ClosureParameterTypeIs<T, U, I>()`,
  return/parameter type-category predicates, return/parameter nested type
  metadata facts, exact function-kind predicates,
  storage predicates (`ClosureStorageIsBorrow`, `ClosureStorageIsHeap`,
  `ClosureStorageIsInline`), and call-capability predicates
  (`ClosureCallCapabilityIsNormal`, `ClosureCallCapabilityIsMut`,
  `ClosureCallCapabilityIsOnce`). Bounded raw-pointer parameter count
  expressions are exposed with
  `ClosureParameterHasRawPointerElementCountExpression<T, I>()` and
  `ClosureParameterRawPointerElementCountExpression<T, I>()`; closure count
  expressions also use synthetic parameter names such as `arg1`.
- Closure memory-contract facts include
  `ClosureParametersAreDisjoint<T, LeftIndex, RightIndex>()`,
  `ClosureParametersOverlap<T, LeftIndex, RightIndex>()`, and
  `ClosureParametersAreSame<T, LeftIndex, RightIndex>()`. They inspect
  `closure<...> where overlap/same` type metadata; `same` counts as overlap.
- Method bounded raw-pointer parameter count expressions are exposed with
  `MethodParameterHasRawPointerElementCountExpression<T, MethodIndex,
  ParameterIndex>()` and
  `MethodParameterRawPointerElementCountExpression<T, MethodIndex,
  ParameterIndex>()`; method count expressions preserve the source expression,
  usually a parameter name such as `length`.
- Dyn-trait metadata facts include `IsDynTrait<T>()`,
  `DynTraitIsView<T>()`, `DynTraitIsHeap<T>()`, and
  `DynTraitTargetTypeIs<T, Trait>()`. They inspect `borrow dyn Trait` /
  `heap dyn Trait` type metadata only inside `comptime`; the target comparison
  requires a dyn-trait object as its first type argument and a trait as its
  second type argument.
- `FieldName<T, I>()` returns the `I`th struct/record field name as an `ascii`
  compile-time text constant.
- `EnumVariantName<T, I>()` returns the `I`th enum variant name as an `ascii`
  compile-time text constant.
- `EnumVariantUsesNamedFields<T, I>()` returns `bool` when the `I`th enum
  variant uses named-field payload syntax.
- `EnumVariantPayloadHasName<T, VariantIndex, PayloadIndex>()` returns `bool`
  when the selected enum payload field has a source name.
- `EnumVariantPayloadName<T, VariantIndex, PayloadIndex>()` returns the selected
  enum payload name as an `ascii` compile-time text constant, or empty `ascii`
  for positional payload fields.
- `AssociatedTypeCount<T>()` returns `u64` for associated aliases declared on
  `T`.
- `AssociatedTypeName<T, I>()` returns the selected associated alias name as an
  `ascii` compile-time text constant. Associated aliases are indexed by
  deterministic ordinal name order, not source order.
- `AssociatedTypeHasTarget<T, I>()` returns `bool`; it is false for required
  trait aliases such as `alias Item;`.
- `AssociatedTypeTargetTypeIs<T, U, I>()` returns `bool` when the selected
  associated alias target exactly matches `U`, after substituting generic type
  and `comptime` value arguments from `T`.
- `AssociatedTypeTargetTypeIs*<T, I>()` and
  `AssociatedTypeTargetTypeHasConcreteLayout<T, I>()` mirror the field,
  payload, method, function-pointer, and closure type-category predicates for
  an associated alias target. Required aliases without a target return false
  for these predicates.
- `AssociatedTypeTargetTypeDisplayName<T, I>()`,
  `AssociatedTypeTargetTypeBaseName<T, I>()`,
  `AssociatedTypeTargetTypeIsGenericInstantiation<T, I>()`,
  `AssociatedTypeTargetTypeArgumentCount<T, I>()`, and
  `AssociatedTypeTargetTypeComptimeArgumentCount<T, I>()` expose associated alias
  target identity and generic-shape metadata after owner generic substitution.
  Required aliases without a target return default metadata values (`""`,
  `false`, `0`).

Indices are typed comptime integer generic arguments. Concrete out-of-range
indices are compile-time errors; symbolic indices may flow through generic CTFE
until specialization.

```stark
finite law u64[0 max] OffsetAt<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.FieldOffset<T, comptime I>();
}

finite law bool HasExplicitFieldOffset<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.FieldHasExplicitOffset<T, comptime I>();
}

finite law bool HasI32AssociatedTarget<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.AssociatedTypeHasTarget<T, comptime I>()
        && comptime System.Compiler.AssociatedTypeTargetTypeIs<T, i32[min max], comptime I>();
}

finite law bool AssociatedTargetHasConcreteLayout<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.AssociatedTypeTargetTypeHasConcreteLayout<T, comptime I>();
}

finite law bool FunnelAbsorbs<T, U, comptime u64[0 max] I>()
{
    return comptime System.Compiler.EnumVariantIsErrorFunnel<T, comptime I>()
        && comptime System.Compiler.EnumVariantAbsorbsErrorTypeIs<T, U, comptime I>();
}

finite law bool FirstComptimeParamIsU8<T>()
{
    return comptime (System.Compiler.TypeComptimeGenericParameterName<T, 0>() == "N")
        && comptime System.Compiler.TypeComptimeGenericParameterTypeIs<T, u8[1 8], 0>()
        && comptime System.Compiler.TypeComptimeGenericParameterTypeIsInteger<T, 0>();
}

finite law bool HasPayloadType<T, U, comptime u64[0 max] V, comptime u64[0 max] P>()
{
    return comptime System.Compiler.EnumVariantPayloadTypeIs<T, U, comptime V, comptime P>();
}

finite law u64[0 max] PayloadOffset<T, comptime u64[0 max] V, comptime u64[0 max] P>()
{
    return comptime System.Compiler.EnumVariantPayloadOffset<T, comptime V, comptime P>();
}

finite law u64[0 max] VariantTag<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.EnumVariantTag<T, comptime I>();
}

finite law bool FieldIsNumber<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.FieldTypeIsInteger<T, comptime I>();
}

finite law bool PayloadIsRecord<T, comptime u64[0 max] V, comptime u64[0 max] P>()
{
    return comptime System.Compiler.EnumVariantPayloadTypeIsRecord<T, comptime V, comptime P>();
}

finite law bool CallbackIsCFiniteLaw<T>()
{
    return comptime System.Compiler.FunctionPointerKindIsFiniteLaw<T>()
        && comptime System.Compiler.FunctionPointerAbiIsC<T>();
}

finite law bool CallbackArgsAreDisjoint<T, comptime u64[0 max] L, comptime u64[0 max] R>()
{
    return comptime System.Compiler.FunctionPointerParametersAreDisjoint<T, comptime L, comptime R>();
}

finite law bool ClosureIsHeapUnary<T>()
{
    return comptime System.Compiler.ClosureParameterCount<T>() == 1
        && comptime System.Compiler.ClosureStorageIsHeap<T>()
        && comptime System.Compiler.ClosureCallCapabilityIsNormal<T>();
}

finite law bool IsHeapDynTarget<T, Trait>()
{
    return comptime System.Compiler.IsDynTrait<T>()
        && comptime System.Compiler.DynTraitIsHeap<T>()
        && comptime System.Compiler.DynTraitTargetTypeIs<T, Trait>();
}

finite law bool FirstMethodReturnsI32<T>()
{
    return comptime System.Compiler.MethodCount<T>() > 0
        && comptime System.Compiler.MethodReturnTypeIs<T, i32[min max], 0>();
}

finite law bool IsFieldNamed<T, comptime u64[0 max] I>()
{
    return comptime (System.Compiler.FieldName<T, comptime I>() == "Value");
}

finite law bool HasPayloadName<T, comptime u64[0 max] V, comptime u64[0 max] P>()
{
    return comptime System.Compiler.EnumVariantPayloadHasName<T, comptime V, comptime P>();
}
```

CTFE text equality compares decoded text payloads, so structural name facts can
be compared with ordinary string literals without creating runtime reflection
metadata.

Available trait-conformance fact:

- `Implements<T, Trait>()` returns `bool` when `T` statically conforms to the
  given trait. The second generic argument must resolve to a trait type.
- `ImplementedTraitCount<T>()`, `ImplementedTraitTypeIs<T, Trait, I>()`,
  implemented-trait type predicates, and implemented-trait type metadata facts
  expose the stored trait base-list for a named type inside `comptime`.

```stark
finite law i32[min max] DrawScore<T>()
{
    if (comptime System.Compiler.Implements<T, Drawable>())
    {
        return 7;
    }

    return 0;
}
```

`Implements<T, Trait>()` follows Stark's static trait conformance model. It
does not create trait objects, vtables, hidden dispatch, or runtime reflection.
Implemented-trait metadata follows the same rule and is erased before runtime
lowering.

## Data Declarations

Use `struct` for ordinary named data with methods and constructors. Use `record` for data-oriented named aggregates. Neither supports inheritance.

```stark
struct Box
{
    i32[min max] Width;
    i32[min max] Height;
}

record Point(i32[min max] X, i32[min max] Y)
{
}
```

Create values with explicit or target-typed `new`:

```stark
stack Box a = new Box();
stack Box b = new();
stack Box c = new()
{
    Width = 3, Height = 4
};
stack Point p = new Point(1, 2);
```

Enums are closed variant families:

```stark
enum Token
{
    End,
    Integer(i32[min max]),
    Move
    {
        X: i32[min max], Y: i32[min max]
    },
}

fn i32[min max] Read(Token token)
{
    switch (token)
    {
        case Token.End:
            return 0;
        case Token.Integer(var value):
            return value;
        case Token.Move
        {
            X: var x, Y: var y
        }:
            return x + y;
    }
}
```

A two-variant enum can opt into `try` error propagation by marking its variants with the innate `[Ok]`/`[Err]` role attributes (see Control Flow).

Equality against a **unit** variant works directly with `==`/`!=`, so status checks do not need a `switch`: `status == MemoryStatus.Ok`, `entry.Kind == FileSystemEntryKind.File`, `FromConstAscii(text, "literal") == MemoryStatus.Ok`. Payload-carrying variants are read with `switch` or `is` patterns.

`struct` and `record` bodies may declare at most one destructor block: `drop { ... }` (readonly `self`) or `mut drop { ... }` (mutable `self`, e.g. to disarm a handle). Destructor blocks have no name, parameters, return type, or visibility; they run on owned drop, must not panic/synchronize/allocate, and ordinary field destruction still proceeds afterwards. Put fallible or order-sensitive teardown in an explicit method such as `Close` and keep the destructor as the deterministic backstop.

Traits name behavior contracts. A `struct`/`record` implements a trait with a base list (`struct Button : Drawable`) and provides the methods inline; `Self` is the implementing type, so receivers read `borrow Self self` (the impl writes the concrete type). A trait method with a `;` body is required of every implementer; one with a `{ ... }` body is a default the implementer may override. Trait, doctrine, struct, and record bodies may declare associated aliases: `alias Item;` is a required associated type in a trait, `alias Item = Type;` defines a concrete/default associated type, and signatures can refer to `Self.Item` or `T.Item`. Conformance requires required associated aliases, matching parameter/return types (with `Self`, trait type arguments, and associated types substituted), arity, and a function kind at least as strong as the trait's. A generic parameter is bounded with `where T: Trait`, which makes the trait's methods callable on `T` and requires every concrete type argument to implement the trait. Trait dispatch is **static by default**: concrete-receiver calls, bounded-generic calls, and not-overridden defaults all monomorphize to **direct calls** with no vtable or runtime indirection. Runtime dispatch is opt-in: a trait declared `dyn trait` can form a **trait object** — a two-word fat pointer (data + vtable). It comes in a borrowed form, `borrow dyn Trait` / `mut borrow dyn Trait` (a non-owning view, no allocation), and an owning form, `heap dyn Trait` (the value is moved into a heap box the trait object owns and drops + frees at scope exit; in a local, write the storage class then the type, e.g. `stack heap dyn Trait`, paralleling `heap closure`). A conforming concrete value coerces into a `dyn`-typed slot (the visible `dyn`/`heap` is the cost disclosure), and a call on it lowers to one indirect call through the vtable, with the method's `law`/`finite` effect contract preserved. A `dyn trait` must be object-safe (borrow-`Self` receiver, no generic methods, no by-value `Self`) and currently cannot declare associated types; `dyn` over a plain `trait` is an error. A plain `trait` is otherwise compile-time-only — no trait objects, and no calling through the trait name. Doctrines bundle `law` functions and constraints; they have no owned identity, heap allocation, or captured environment, and members are called directly by qualified name.

## Storage Classes And Globals

Every local names its storage class: `stack` (the default choice), `heap` (global-allocator-backed owned storage; never manually freed), or `register` (scalar locals with no source-visible address — `&local`, slices, and address-requiring APIs are rejected; use `stack` when an address matters). `arena` is reserved and not yet a valid executable local storage class; function-local `static` is invalid (use a top-level global). `dynamic T` is a value type, not a storage class — the owner local still says `stack`/`heap`.

Globals come in three forms: `const Name = ...;` (deeply frozen reachable object graph — strongest), `static T Name = ...;` (immutable binding; the value may still have interior mutability), and `static mut T Name = ...;` (rebindable). Scalar integer consts omit the type so the compiler derives the smallest width (`const PageSize = 2 ** 12;`); an explicit type must be the canonical bare width (`const u8 Count = 80;` is accepted, `const i32 Count = 80;` is rejected).

## Ownership And Borrows

Safe Stark has no garbage collector. Owned is the default.

- Non-borrow, non-raw values have exactly one owner.
- Ownership transfers by move.
- A moved value is unusable until reinitialized.
- Owned values drop at scope exit.
- Assignment to an initialized owned place drops the previous value first.
- Safe borrows are non-owning and never null.
- Raw pointers are the only null-capable pointer forms.
- Safe optional values use `System.Option<T>` (`Some(T)` / `None`), not nullable
  safe references.

Structural copyability: a named type with NO destructor whose contents are all
copyable (scalars, raw pointers, views; enums whose variants carry only
copyable fields; structs/records of copyable fields) is a copy type — reads
from fields, indexed places, and locals are copies, the source stays usable,
and accessors can return such values directly
(`return self.Tokens[index].Kind;`). Anything owning (`dynamic`, owned text,
heap closures) or destructor-bearing stays move-only; concrete generic
instantiations classify through their substituted payloads. To force a
scalar-only type to be move-only (e.g. a unique token), give it an empty
`drop { }`.

Generic code requiring copyability declares `where Copyable(T)` (the same
law-predicate family as `Transferable(T)`; checked at call sites with
field-chain diagnostics, forwarded by declaring the same bound). `[Copyable]`
on a struct/record/enum asserts structural copyability at the definition
(STK3051 with the responsible field chain when violated); it takes no
arguments, and Copyable cannot be granted or denied with attributes.

Borrow escape classes:

- `borrow T`: temporary access; cannot be stored, returned, or forwarded to unknown code
- `retborrow T`: may escape only through the return value
- `storeborrow T`: may be stored or otherwise escape

Use the strictest borrow that works:

```stark
struct Counter
{
    i32[min max] Value;

    finite law i32[min max] Current(borrow Counter self)
    {
        return self.Value;
    }

    finite void Add(mut borrow Counter self, i32[min max] amount)
    {
        self.Value += amount;
        return;
    }

    finite retborrow mut i32[min max] Slot(mut borrow Counter self)
    {
        return self.Value;
    }
}
```

Use `frozen T` for deeply read-only access during the borrow lifetime. Use `const T` for permanent deeply immutable reachable object graphs. Use `shared T` only for explicit shared-state domains.

## Memory Contracts

Memory-backed function parameters are non-overlapping by default for ordinary Stark functions. This applies to borrows, mutable borrows, slices, text views, `out`, `init`, bounded raw pointer regions, and similar reachable storage.

Use relational contracts only when the default is too strict or too imprecise:

```stark
fn void Copy(borrow u8[0 max][] source, borrow mut u8[0 max][] destination)
{
    return;
}

fn void MoveOverlapSafe(borrow u8[0 max][] source, borrow mut u8[0 max][] destination)
    where overlap(source, destination)
{
    return;
}

fn void RequireSame(borrow u8[0 max][] left, borrow u8[0 max][] right)
    where same(left, right)
{
    return;
}

fn void CopyWindow(
    rawptr<i8[min max]> source,
    rawmutptr<i8[min max]> destination,
    i64[0 max] sourceStart,
    i64[0 max] length)
    where disjoint(source[sourceStart, length], destination[0, length])
{
    return;
}
```

Do not write whole-parameter `disjoint` on ordinary Stark functions; default parameter non-overlap already covers it. FFI and assembly declarations do not receive the default, so explicit whole-parameter disjointness is the opt-in spelling there.

Use `if disjoint(...)` for a runtime branch that grants non-overlap only in the true branch. Use `unsafe assume disjoint(...) { ... }` only for scoped, externally proven facts the compiler cannot prove.

## Initialization And Dynamic Storage

`out T` and `init T` are write-before-read contracts:

- the callee must write required bytes before successful return
- the callee may not read previous contents
- the caller treats the destination as uninitialized until completion

```stark
fn bool TryWrite(out i32[0 max] value)
{
    value = 7;
    return true;
}
```

`dynamic T` is owned capacity-backed storage, not a storage class. Use `Length`, `Capacity`, `Reserve`, `TryReserve`, `MoveLast`, and `MoveAt` rather than exposing raw pointers.

```stark
struct IntList
{
    dynamic i32[0 max] Items;
}

fn bool Append(mut borrow IntList self, i32[0 max] value)
{
    if (!self.Items.TryReserve(1))
    {
        return false;
    }

    init self.Items[self.Items.Length] = value;
    return true;
}
```

Reading dynamic slots needs an initialization proof, and a strict length
guard provides one on the dominated path — in any function, for direct
reads, whole-value copies, and field projections alike:

```stark
finite law Kind TagAt(borrow IntTable self, u64[0 2 ** 63 - 1] index)
{
    if (index >= self.Items.Length)
    {
        return Kind.None;
    }

    return self.Items[index].Tag;    // proven by the guard above
}
```

The guard and the read must spell the index and the storage path with the
same source text (bind a local first if the index is computed). The proof
dies on any write to something it mentions: assigning or `+=`-ing the index,
passing the owner by `mut borrow`, or shadowing. Non-strict `<=` proves
nothing for reads. An equality guard against a constant length proves the
matching CONSTANT indices: `if (dyn.Length != 1) { return ...; }` then `dyn[0]`,
`if (dyn.Length == k) { dyn[0..k-1] }`, and the non-empty `if (dyn.Length != 0)`
→ `dyn[0]` (a variable index still needs `<`/`>=` or a `where` contract).
Genuinely sparse structures (hash slots, parent links) keep the explicit
`unsafe { }` sparse proof, and whole-slot MOVES still go through `MoveLast()`
regardless of guards.

Value contracts move the obligation to callers: `where index <
self.Tokens.Length` in a function's where clause proves the body's reads,
and every call site must discharge the contract (re-spelled with the actual
arguments) via a dominating comparison, its own matching `where` contract,
or constant arguments — else STK4206. All four comparison operators work at
call sites; only strict `<`-Length contracts prove body reads. Contracts
over internal fields are undischargeable outside the module — keep a guarded
public wrapper and a contracted internal core (the lexer's
`TokenAt`/`TokenAtProven` pattern).

## Raw Pointers, Unsafe, And FFI

Raw pointer forms are `rawptr<T>` and `rawmutptr<T>`. They may be null, dangling, unaligned, aliased, or point to foreign memory. Safe borrows cannot be null.

Unsafe context is required for raw pointer signatures/declarations, FFI, raw locals, address-of `&`, dereference `*`, raw pointer conversions, `slice(pointer, count)`, unsafe calls, unsafe callback erasure, and unsafe capture modes.

Bound raw pointer parameters when possible:

```stark
unsafe fn void Fill(
    i64[0 max] length,
    rawmutptr<i32[min max]>[length] destination)
{
    unsafe
    {
        stack mut i32[min max][] view = slice(destination, length);
        for willexit independent (stack mut i64[0 max] index = 0; index < length; index += 1)
        {
            view[index] = 0;
        }
    }
}
```

FFI rules:

- Declare imported FFI as `unsafe ffi fn`.
- An `ffi` function uses the target C ABI by default; spell a different convention as `ffi(abi)` (e.g. `unsafe ffi(stdcall) fn`), or `ffi(platform(...))` for per-target selection. The ABI is part of `fnptr<ffi(c) fn ...>` type identity. Supported names: `c`, `cdecl`, `stdcall`, `fastcall`, `thiscall`, `vectorcall`, `sysv`, `win64`, `aapcs`, `aapcs64`.
- C-facing aggregates use layout attributes: `[StructLayout(C)]` / `[StructLayout(Explicit)]`, `[Pack(N)]`, `[Align(N)]`, and `[FieldOffset(N)]`. Default Stark layout is not a stable ABI.
- Preserve foreign symbol spelling exactly. Underscore-leading identifiers are valid for FFI symbols such as `__error`; bare `_` remains discard.
- Use safe wrappers only when they hide raw handles, combine calls, narrow a foreign surface, or define a real Stark-level abstraction.
- Do not let foreign code unwind through Stark frames.
- Stark enums do not cross `ffi` or `export` boundaries.
- C varargs use `unsafe ffi varargs fn`; callers must pass ABI-ready values explicitly.

Assembly functions are unsafe FFI boundaries for small platform/CPU shims:

```stark
internal unsafe ffi asm(x86_64) fn i64[min max] Syscall1(
    i64[min max] number,
    i64[min max] arg1)
    in("rax") number,
    in("rdi") arg1,
    out("rax") return,
    clobber("rcx", "r11")
{
    "syscall"
}
```

Use `unsafe ffi asm(arch) fn`, `in("reg") parameter`, `out("reg") return`,
and `clobber(...)`. Supported value families are integer scalars, floating
point scalars, raw pointers, and `void` returns. Calls require unsafe context.
Avoid non-return `out("reg") parameter` in source asm bodies; it parses but is
not fully emitted yet. For full rules and target/register details, read
[`references/assembly-functions-reference.md`](references/assembly-functions-reference.md).

## Control Flow

Loops require a behavior keyword:

- `infinite`: statically unconditional, no structural exit, not allowed in `finite`
- `non-deterministic`: may or may not exit, not allowed in `finite`
- `willexit`: expected to make progress and finish; required in `finite`

`for ... in ...` traversal is explicit and borrow-based. It supports fixed
arrays, slices, and dynamic storage, and lowers to a counted loop without an
iterator object or hidden runtime dispatch:

```stark
for willexit (borrow Token token in tokens)
{
    Process(token);
}

for willexit (borrow mut Token token in tokens)
{
    Normalize(token);
}

for willexit (stack u64[0 max] index, borrow Token token in tokens)
{
    Record(index, token);
}
```

The element binding must be `borrow T` or `borrow mut T`; mutable traversal
requires mutable element storage. The optional index binding uses `stack` or
`register` storage and an integer range wide enough for every source index.

Use `independent` only when iterations have no loop-carried memory dependency:

```stark
for willexit independent (stack mut i64[0 max] index = 0; index < length; index += 1)
{
    output[index] = input[index] + 1;
}
```

A loop or `switch` may be labeled (`outer: for willexit (...)`) so an inner `break outer;` / `continue outer;` targets it directly; `continue` targets labeled loops only.

`if` and `switch` accept an optional branch-weight annotation (`w9`, `w99`) for performance tuning of expected-likelihood branches.

`switch` supports literal cases, `default`, `when` guards, `case var capture`, `_`, enum case patterns, exact aggregate patterns, aggregate property patterns (`case Box { Field: pattern }:`), exact-length list patterns over fixed arrays/slices/dynamic storage (`case [first, second]:`), switch-label or-patterns (`case A | B:`), and inclusive integer range patterns (`case 0..10:`). Property patterns must name every aggregate field exactly once. List patterns are exact-length only: fixed-array length mismatches are compile-time errors; slice/dynamic list patterns lower to a length check plus direct element tests, with no iterator protocol or hidden allocation. Range patterns are integer-only, work at top level and inside enum/aggregate/list field patterns, and are checked as intervals rather than expanded into individual values. Every `switch` must be **exhaustive**: cover all enum variants / both bools / every value of a ranged integer (e.g. `u8[0 3]` with cases `0..3`), or include a `default`. `when`-guarded arms never count toward coverage. Relatedly, a non-`void` function must **return on every path** (end paths with `return`, an `if`/`else` that returns on both sides, an exhaustive `switch` whose sections all return, or a break-free `infinite` loop) — falling off the end is a compile error, not a runtime trap.

```stark
switch (token)
{
    case Token.Integer(0..9) | Token.Integer(10..19):
        return 1;
    case Token.Integer(_):
        return 2;
    case Token.End:
        return 0;
}
```

`if` and `while` conditions also take a pattern-match form, `expr is pattern`, using the same `switch case` pattern surface: `if (Lookup(k) is Option<V>.Some(var value)) { Use(value); }` binds `value` in the then-branch only; `while willexit (next() is Option<T>.Some(var x)) { ... }` is the `while let` drain idiom (captures re-bind each iteration, loop exits on the first non-match). Move-only captures move out of the matched value (dropped at branch/body exit) exactly as in `switch`. With `is pattern` the condition is the scrutinee, not a `bool`.

Errors and absence are values, never exceptions or nullable safe references. The blessed optional-value shape is `System.Option<T>` with `[Ok] Some(T)` / `[Err] None`. Any two-variant enum becomes **propagatable** by marking its variants with the innate role attributes `[Ok]` and `[Err]`: `enum Result<T, E> { [Ok] Ok(T), [Err] Err(E) }`, `enum Option<T> { [Ok] Some(T), [Err] None }` — the stdlib result/option/status enums are annotated this way, and user enums with any names work identically (`enum FetchOutcome { [Ok] Got(Data), [Err] Failed(FetchError) }`). Roles are recognized only from the attributes, never from type names, variant names, or stdlib identity. Role rules: exactly two variants, one of each role, each carrying at most one payload; `[Ok]`/`[Err]` take no arguments. `try expr` propagates a propagatable value: it yields the `[Ok]` payload and continues, or **early-returns** the `[Err]` from the enclosing function (rewrapped in the enclosing return type's `[Err]` variant), running the same drops a `return` would. Requirements: the operand's type is a propagatable enum; the enclosing function's return type is a propagatable enum (it need not be the same enum or the same generic family); and the failure payloads are connected — both unit-like, both the same error type, or the enclosing `[Err]` payload type has a `from` funnel for the operand's error type (otherwise compile error; unit-vs-payload mixing is rejected — `try` never invents or discards an error value). The success payloads are independent; only the failure path ties the two signatures together. `try` is a visible, greppable leading keyword — not a trailing sigil — and is restricted to statement-boundary positions (a binding initializer, an assignment right side, the operand of `return`, or a bare expression statement); it may not be nested inside a larger expression. When the enclosing error type differs from the operand's, an error `enum` declares the conversion once by marking the absorbing variant with `from` (`enum LoadError { Io from IoError, Parse from ParseError }`); `try` then wraps automatically (zero-cost variant wrap) and the call sites stay bare — including across families, e.g. a stdlib `IOResult<T>` operand inside a function returning `Result<T, LoadError>` converts through `LoadError`'s `Io from IOError` funnel. Same error type needs no `from`; a cross-family `try` with no matching funnel is a compile error.

```stark
fn Result<Module, LoadError> LoadModule(ascii path)
{
    stack ascii  text = try ReadFile(path);   // Ok -> text; Err(IoError) -> return LoadError.Io(...)
    stack Ast    ast  = try Parse(text);       // Err(ParseError) -> return LoadError.Parse(...)
    stack Module mod  = try Resolve(ast);      // Err(ResolveError) -> return LoadError.Resolve(...)
    return Result<Module, LoadError>.Ok(mod);
}
```

## Expressions And Operators

Expression forms include literals, identifiers, qualified names, calls, member access, indexing/slicing, `new`, field initializers, array initializers, unary/binary operators, ternary `?:`, error propagation `try`, assignments, and compound assignments.

Operator notes:

- `^` is bitwise XOR.
- `**` is exponentiation.
- Ordinary integer overflow and oversize shifts are illegal/undefined.
- Wrapping integer arithmetic uses `+%`, `-%`, `*%`.
- Saturating integer arithmetic uses `+|`, `-|`, `*|`.
- Comparison chains such as `a < b < c` evaluate each operand once and short-circuit adjacent comparisons.
- Explicit conversions use C-style casts: `(targetType)value`. Casts may never strengthen mutability (no readonly-raw to `rawmutptr`).
- `strictfp` is required for strict IEEE-style floating point; ordinary floating point is fast-math friendly.
- `==`/`!=` compare scalars, text views, and unit enum variants; payload variants need `switch`/`is`. There is no `is not` operator, and `is` patterns appear only as the whole `if`/`while` condition.
- A bare integer literal adopts the other operand's ranged type in mixed arithmetic when its value fits, so `index + 1` on a `u64[0 …]` variable is itself `u64[0 …]` (no narrowing cast needed to store or return it) — the same outcome as var-with-var arithmetic over a shared ranged type (`end - start`). The literal takes only the operand's numeric shape (width, range, sign), never an `out`/`borrow`/`frozen` qualifier (arithmetic yields a plain value). A literal that does NOT fit the operand's range, or a negative literal against an unsigned operand (`small + 1000`, `position + -1`), keeps its own type and still needs an explicit cast. Comparisons (`index + 1 < length`) are unaffected (they yield `bool`).
- In a constructor body, `self` fields start in their zero state; assign an owning field (dynamic storage, owned text) before reading it. Reading `self.Items.Length` — or indexing `self.Items[i]` — before `self.Items = new();` is rejected as STK3055 ("Constructor reads field 'Items' of 'self' before it is assigned"). Scalars, fixed arrays, and copyable aggregates have a valid zero state and are exempt (a `bool[64]` field can be written element-wise without a prior whole-field assignment). Assignment on any earlier path counts, so a field set inside an `if` is treated as assigned afterward.

Array initializer `{ ... }` needs owning backing storage:

```stark
stack i32[min max][3] values =
{
    1, 2, 3
};
stack i32[min max][] view = values;
```

Do not assign an array initializer directly to a slice.

## Text

Text forms:

- `ascii`: UTF-8 view
- `unicode`: UTF-32 view
- `Ascii`: owned ASCII/UTF-8 container
- `Unicode`: owned Unicode container

String literals infer to `ascii` when possible. Use `(unicode)"..."` for Unicode target text. Text indexing/slicing is zero-copy:

```stark
text[]
text[index]
text[start, length]
```

Raw string literals skip escape processing: `raw"\d+"` is verbatim, and `raw"""..."""` spanning lines follows C# raw-string rules — the newline after the opening quotes and the newline before the closing quotes are NOT part of the value, and the whitespace before the closing `"""` is stripped from every content line (content on the opening-quote line or an under-indented line is a compile error; keep the closing `"""` on its own line at the indentation you want stripped). Both compose with interpolation as `$raw"..."` / `$raw"""..."""`.

C#-style interpolated text is supported. Fully compile-time interpolation folds to a text constant. Runtime interpolation needs caller-selected fixed storage:

```stark
fn Ascii Label(i32[min max] score)
{
    stack Ascii label[64] = $"Score: {score}";
    return label;
}
```

Use explicit `System.Text` APIs when overflow, allocation failure, formatting failure, or encoding conversion should be returned as data instead of trapping.

## Thread-Safety Laws

Stark has two compiler-known thread-safety laws:

- `Transferable(T)`: ownership of `T` may move to another thread.
- `Shareable(T)`: a borrow of `T` may be accessed from multiple threads.

Use them in callable `where` clauses. The compiler computes facts structurally at type-check time, including generic propagation and conditional grants:

```stark
fn T Move<T>(T value) where Transferable(T)
{
    return value;
}

struct Synchronized<T>
{
    [Grant(Shareable) where Transferable(T)]
    T Payload;
}
```

Raw pointers and `storeborrow` fields deny both laws by default. `System.Threading.Atomic*` types satisfy both laws by intrinsic compiler grant. Use `[Grant(...)]` only for audited overrides and `[Deny(...)]` for semantic opt-outs that structural derivation cannot see. Direct/member calls, explicit payload thread starts, channels, `Synchronized<T>`, and thread-entry reachable mutable statics consume these laws.

## Standard Library

Import standard-library modules explicitly when it improves readability. The root `System` module re-exports the common public modules and exposes `System.Option<T>` / `System.Result<T, E>` as aliases over `System.Core`; `System.Text`, `System.Testing`, and `System.Runtime.Buffer` are usually imported directly when needed.

Public modules:

- `System.BitOperations`: bit counting, zero counts, rotations, byte swaps, powers of two
- `System.C`: C primitive aliases, null-terminated C string views/owners/buffers, and foreign-owned C string copy/dispose helpers
- `System.Collections`: `List`, `Stack`, `Queue`, `RingQueue`, `Dictionary`, `HashSet`, `LinkedList`, `Lookup`, `SortBy<T>`, `Sort<T>`, and canonical `Eq`/`Hash`/`Ord`/`Format` contracts
- `System.Console`: console reads/writes for text, slices, and byte buffers
- `System.Core`: canonical `Option<T>` and `Result<T, E>` enum definitions backing the root aliases
- `System.FileSystem`: directories, entry information, existence/type checks, move/delete, cross-platform metadata, temp directories, recursive walk, and streaming glob traversal
- `System.IO` / `System.IO.File` / `System.IO.Path`: IO result types, owned files, whole-file text/byte helpers, atomic whole-file replacement helpers, line-oriented file reading, current/temp directory queries, glob matching, multi-part path joins, explicit temp name/path candidate helpers, path facts, full/lexical path shaping, and extension rewriting
- `System.Compiler.IntegerFacts`: bounded `i1024`/`u1024` compiler integer-fact helpers for range, storage, tag, checked arithmetic, known-bit, and two's-complement reasoning
- `System.Math`: float math including trig, `SinCos`, `Exp`/`Log`/`Pow`, min/max/rounding, fused multiply-add, reciprocal estimates, `XorShift32`
- `System.Memory`: dynamic-storage reserve/append/copy/move/fill helpers
- `System.Net` / `System.Net.Tcp`: network result types, IPv4 endpoints, TCP clients/listeners
- `System.Process`: process id/exit, Linux/macOS-backed command spawn with optional stdin, timeout, and stdout/stderr/exit-code capture, live environment reads, child environment mutation, cwd get/set, and argv/argc access
- `System.Runtime.Buffer`: fixed and dynamic byte buffers
- `System.Testing`: explicit test helpers with finite-law boolean/equality, text contains/starts/ends/occurrence counts, range, slice/List shape, root `Option`/`Result` shape predicates, structured diagnostic predicates, compile-time type assertion predicates, process output assertions/counts, effectful run-match/timeout helpers, temp fixture helpers, snapshot/golden text helpers, status, and `RunFact`/`SkipFact`/exit-code helpers used by generated `[Fact]` / `[Theory]` runners with inline data, typed indexed member-data providers, build-time platform gates, and serial collection grouping. Pure local test predicates and pure `[Fact]` / `[Theory]` bodies should also be `finite law`; keep fixture IO, process execution, output, and owned result consumption as plain `fn` unless their full callees and ownership effects justify stronger contracts.
- `System.Text`: owned text, text contains/starts/ends/occurrence scans, byte-slice-to-ASCII scans, encoding conversion, parsing, formatting, string-literal escaping and ordinary/raw string + character literal decoding
- `System.Text.Interning`: compiler ID model — `SymbolId`/`TypeId`/`ModuleId`/`PackageId` (distinct u32 wrappers with static `Hash`/`Equals`/`Compare`) plus `AsciiInterner` and per-ID interners (`TryGet(name, out id)`, `Intern(name)`, `CopyName(id)` reverse lookup, insertion-order preserved). Intern stable names once at source/package boundaries; compare typed IDs in hot paths; never depend on hash-iteration order for deterministic output
- `System.Threading`: no-payload and explicit payload thread starts (`Thread.Start<T>(entry, payload) where Transferable(T)`), joins, detach, yield, sleep; atomic types (`AtomicBool`, `AtomicI8`…`AtomicI1024`, `AtomicU8`…`AtomicU1024`) for safe seq-cst counters/flags; `Synchronized<T>` / `Locked<T>` for explicit guarded shared mutable state; MPSC channels (`Channel<T>.CreateSender()`/`.CreateReceiver()`, `Sender<T>.Send(value)` moving a `Transferable` payload, `Receiver<T>.Receive()`, sender close/drop signals completion) for worker→driver event publication

**Sharing state between threads**: functions reachable from a `ThreadEntry` or `ThreadPayloadEntry<T>` may touch a `static mut` only when the static is synchronization-backed: use an atomic type for scalar state or `Synchronized<T>` for guarded aggregate state. There is no atomic qualifier keyword and no hidden synchronized assignment/member access. One atomic struct exists per integer width plus `AtomicBool`; every operation is one indivisible seq-cst action. RMW operations (`Add`/`Sub`/`And`/`Or`/`Xor`/`Exchange`) return the **previous** value; `Add`/`Sub` wrap at the value width; `CompareExchange(expected, desired)` returns whether it swapped. Module-level declarations spell the qualified type name (unqualified imported types only resolve inside function bodies):

```stark
static mut System.Threading.AtomicI64 Counter = new System.Threading.AtomicI64(0);

fn i32[min max] Worker()
{
    Counter.Add(1);              // one atomic instruction; two threads never lose an increment
    return 0;
}
```

Use `System.Threading.Synchronized<T>` for guarded mutable aggregates:

```stark
static mut System.Threading.Synchronized<Counter> Shared =
    new System.Threading.Synchronized<Counter>(new Counter() { Value = 0 });

fn void Bump()
{
    stack mut System.Threading.Locked<Counter> guard = Shared.Lock();
    guard.Value().Value += 1;
}
```

Cost model: 8–64-bit and bool are single hardware instructions; 24/48/96/128-bit stay lock-free (only `Add`/`Sub` can retry under contention); 192-bit and wider serialize through a lock word embedded in the struct (visible in `sizeof`).

For exact public standard-library signatures, read [`references/standard-library-signatures.md`](references/standard-library-signatures.md). It is generated from `stdlib/src/System` and bundled with this skill.

## Bundled References

Use these bundled references when the task needs more detail while staying self-contained:

- [`references/syntax-quick-reference.md`](references/syntax-quick-reference.md): source structure, keywords, operators, ranges, switches, text, and callable syntax.
- [`references/comptime-structural-facts-reference.md`](references/comptime-structural-facts-reference.md): `System.Compiler` structural facts for comptime type, field, enum, callable, trait, doctrine, and associated-type branching.
- [`references/borrower-recipes.md`](references/borrower-recipes.md): choosing `borrow`, `mut borrow`, `retborrow`, `storeborrow`, `frozen`, `const`, `out`, `init`, raw pointers, and memory contracts.
- [`references/callables-closures-reference.md`](references/callables-closures-reference.md): function items, `fnptr`, lambdas, inline closures, borrowed closures, heap closures, once closures, and thread entries.
- [`references/assembly-functions-reference.md`](references/assembly-functions-reference.md): `unsafe ffi asm(arch) fn`, operands, clobbers, target selection, supported types, and current lowering limits.
- [`references/project-manifest-reference.md`](references/project-manifest-reference.md): `Stark.toml`, `Stark.solution.toml`, project kinds, profiles, dependencies, package/source-root resolver precedence, native metadata, and commands.
- [`references/compiler-test-harness-reference.md`](references/compiler-test-harness-reference.md): host compiler test protocol, persistent server mode, structured diagnostics, pass execution records, and artifact text inspection.
- [`references/ffi-native-layout-reference.md`](references/ffi-native-layout-reference.md): FFI declarations, `export`, raw pointer regions, ABI-facing layout, enum tags, safe wrappers, and native package metadata.
- [`references/llvm-metadata-reference.md`](references/llvm-metadata-reference.md): LLVM metadata and address-emission contracts, including TBAA roots, no-op GEP aliasing, and the narrow rule for `!invariant.load` versus `llvm.invariant.start`.
- [`references/performance-cookbook.md`](references/performance-cookbook.md): source-level performance recipes for kernels, non-overlap, independent loops, raw regions, `const`, allocation, numeric policy, and benchmarks.
- [`references/diagnostics-guide.md`](references/diagnostics-guide.md): common diagnostic categories and source-level fixes.
- [`references/examples-cookbook.md`](references/examples-cookbook.md): portable embedded examples for common Stark patterns.
- [`references/standard-library-signatures.md`](references/standard-library-signatures.md): generated public standard-library module summaries and signatures.

## Projects And Solutions

Use `Stark.toml` for a project:

```toml
[project]
name = "app"
version = "0.1.0"
kind = "executable"

[executable]
root = "App.stark"
output = "app"

[dependencies]
stdlib = { path = "../stdlib" }

[profiles.dev]
opt = 0

[profiles.release]
opt = 3
```

Project kinds are `executable`, `library`, and `test`. A test project compiles to an executable. Test roots that contain `[Fact]` or `[Theory]` metadata use a build-time generated explicit `main`; do not write a manual `main` in those roots. A `[Fact]` is a non-generic, no-argument `bool` function with a body, either top-level or a `static` method on a struct/record. A `[Theory]` follows the same rules but may take parameters and must have one or more data rows from `[InlineData(...)]` or typed indexed `[MemberData(provider, rowType, count, ...fields)]`; inline data supports strings, booleans, signed integers, and qualified names. Member-data providers are called once per selected row with the zero-based row index and return a typed row record/struct; optional field names map row fields to parameters by order. Use `[Platform(...)]` and `[SkipPlatform(...)]` on facts, theories, structs, or records for target-triple gates; selectors can be OS names, architecture names, `os.arch` pairs, or exact target triple strings. Use `[Collection(name, ...)]` to tag tests into one or more named collections (variadic; at least one name); it can sit on the `module` declaration (tagging every fact in the file), a `struct`/`record`, or an individual fact, and a fact's effective set is the union of its module/type/member names. `[Serial]` is shorthand for `[Collection("Serial")]`. The generated runner groups by the first listed name (source order preserved inside the group) and filters at runtime, so changing the filter never recompiles: `stark test --collection ownership,lexing` (repeatable, comma-split, union) runs only the tagged subset, `stark test --list-collections` prints the known names, an unknown name errors with the known list, and a run that selects zero facts fails. Filtering is gated on the project having any collections (untagged projects keep the old runner shape); v1 scope is per-project. Manual `main` runners are only for bootstrap tests with no generated test metadata.

Use `Stark.solution.toml` for multi-project repos:

```toml
[solution]
name = "Workspace"
members = ["app", "tests"]

[defaults]
build = ["app"]
run = "app"
test = ["tests"]

[aliases]
app = "app"
tests = "tests"
```

Everyday commands:

```bash
stark build
stark run
stark test
stark test --filter Parser
stark build app --release
stark build --target x86_64-unknown-linux-gnu --stage stage0
stark clean stage --target x86_64-unknown-linux-gnu
stark clean profile
```

For self-host prep tests that target the current C# host compiler, prefer the
structured host-test protocol over scraping CLI text. Use
`compiler --host-test-inspect request.json` for one-off inspection and
`compiler --host-test-server` for large newline-delimited batches. Requests can
compile `sourceText` or `sourcePath`, stop after a pass, return structured
diagnostics/logs/pass executions, render selected LLVM/MIR/SSA artifacts, and
write large artifacts or diagnostics to files while keeping JSON responses
small. Stark-side batched tests can drive server stdin with
`System.Process.RunCaptureWithInput` or bounded server calls with
`System.Process.RunCaptureWithInputTimeout`.
See `references/compiler-test-harness-reference.md`.

Manifest discovery searches upward. The nearest `Stark.toml` runs in project mode; the nearest `Stark.solution.toml` runs in solution mode. Project commands route current host outputs through `.stark/build/<profile>/<target-triple>/stage0/`: executables/libraries under `bin/<project>/`, saved native intermediates under `obj/<project>/`, library package images under `pkg/<project>/`, and generated test runners/test executables under `tests/<project>/`. Project builds search the active stage's `stdlib/` directory for stage-local `System` artifacts, then nearest repo `stdlib/dist` package images, then nearest repo `stdlib/src` source for source-tree development, then bundled stdlib artifacts next to the active compiler distribution; they ignore `STARK_PATH`, which remains a low-level direct compiler search-path escape hatch. If a `System.*` import cannot be resolved, project builds report the searched stdlib paths plus active profile, target, and stage. `--target <triple>` sets both codegen target and output path; `--stage stage0` selects the current C# host stage, while Stage1/Stage2 execution remains future self-hosting work. `stark clean` defaults to the active stage scope and also supports explicit `profile`, `target`, `diagnostics`, and `artifacts` scopes.

Native-backed packages keep native metadata in the package manifest:

```toml
[native]
sources = ["NativeShim.c"]
pkg-config = ["raylib"]

[native.fallback.linux]
include-dirs = ["${native.paths.raylib-src}"]
library-dirs = ["${native.paths.raylib-src}"]
libraries = ["raylib", "GL", "m"]
```

Machine-local paths belong in user config, such as `~/.config/stark/config.toml` or ignored `Stark.user.toml`:

```toml
[native.paths]
raylib-src = "/path/to/raylib/src"
```

Build outputs live under `.stark/build/<profile>/<target-triple>/stage0/` today, with executables/libraries in `bin/<project>/`, saved native intermediates in `obj/<project>/`, library package images in `pkg/<project>/`, generated test runners/test executables in `tests/<project>/`, stage-local stdlib lookup under `stdlib/`, repo development stdlib lookup through nearest `stdlib/dist` then `stdlib/src`, and installed bundled stdlib lookup next to the compiler distribution. Project builds ignore `STARK_PATH`; low-level direct compiler invocations may still use it unless `--no-stark-path` is passed. Cache and package-manager work may also use `.stark/cache` and `.stark/packages`.

## Style

Prefer surrounding code style when editing existing files. Defaults for new Stark code:

- modules, types, fields, records, functions, methods, globals, and constants: `PascalCase`
- parameters and locals: `camelCase`
- no `I` prefix for traits
- no leading `_`, `m_`, `s_`, `g_`, or casing tricks for visibility/storage
- 4 spaces, no tabs
- imports first, then `module`, blank line, then declarations
- preserve foreign FFI spellings exactly
- keep unsafe blocks small and audited
- prefer importing standard-library modules and using short names when unambiguous
- keep helpers private or `internal`; use `public` for Stark API; use `export` only for ABI
- Use Allman Braces

When in doubt, inspect nearby `.stark` files and keep edits consistent. Do not add wrappers, allocation, indirection, visibility, dynamic dispatch, or raw pointers for cosmetic reasons.
