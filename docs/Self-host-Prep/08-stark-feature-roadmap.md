# Stark Feature Roadmap For Self-hosting

Status: WIP. This is a living task list for language and stdlib features that
help Stark become a practical implementation language for its own compiler.

The main design pressure is reuse without hidden behavior. Stark should keep
runtime costs, dispatch, allocation, aliasing, and failure explicit.

## Compile-time Reuse

- [x] Strengthen compile-time-only `trait` and `doctrine` support.
- [x] Add default method bodies for compile-time-only traits/doctrines.
- [x] Add associated types for compile-time contracts.
- [x] Define canonical `Hash`, `Eq`, `Ord`, and `Format` style contracts.
- [x] Keep ordinary traits/doctrines as compile-time contracts, not runtime objects.
- [x] Do not add hidden trait objects, hidden vtables, or implicit dynamic dispatch.

Notes:

Default method bodies, associated type requirements/defaults, and canonical
`Eq`, `Hash`, `Ord`, and `Format` contracts make generic compiler code much
easier to write without hidden trait objects. The compiler resolves ordinary
trait/doctrine contracts statically and emits concrete code.

Reference: current associated-type surface:

- Traits, doctrines, structs, and records may declare associated aliases inside
  their body.
- `alias Name;` is a required associated type in a trait.
- `alias Name = Type;` defines a concrete/default associated type.
- Implementers must define every required trait associated type; missing
  definitions are compile-time diagnostics.
- `Self.Name` and `T.Name` are valid type positions for associated types.
- Generic instantiation resolves concrete associated aliases before SSA/LLVM
  validation and emission, preserving direct static dispatch.
- Package images preserve associated type requirements/defaults in the typed
  interface, source bridge, and compiler facts.
- `dyn trait` currently rejects associated types until Stark has an explicit
  object spelling for associated-type bindings.

Reference: `Dictionary<K, V>` and `HashSet<T>` accept non-primitive key types
when the key type declares explicit static `finite law` methods:
`u64[0 max] Hash(borrow K value)` and
`bool Equals(borrow K left, borrow K right) where overlap(left, right)`. Bool
and integer keys still use the compiler-known scalar fast path.

Blessed self-hosting model: public collections use generic `Hash` + `Eq`,
`Ord`, and `Format` contracts; the compiler interns stable names at front-end
and package boundaries and then uses distinct typed IDs in hot paths. See
`19-generic-collections-and-interning.md`.

Useful self-hosting targets:

- `Dictionary<K, V>` and `HashSet<T>` keys through canonical `Hash` + `Eq`
  contracts; exact ordinal `ascii`/`unicode` and owned-text helpers have
  landed.
- Generic equality and ordering for deterministic compiler output via
  `System.Collections.Eq` and `System.Collections.Ord`.
- Generic hashing via `System.Collections.Hash` with associated `Code`.
- Generic formatting via `System.Collections.Format` with required associated
  `Writer`.
- Strongly typed compiler ID keys such as `SymbolId`, `TypeId`, `ModuleId`, and
  `PackageId` for interned compiler names.
- Reusable collection algorithms without a runtime dispatch layer.

## Compile-Time Structural Facts

- [x] Keep structural facts compile-time-only and erased before MIR/codegen.
- [x] Freeze the current `System.Compiler` structural-fact surface as part of
      the pre-self-host `comptime` baseline. The host compiler has broad
      partial support across type, field, enum, callable, layout,
      associated-type, trait/doctrine, ABI-facing alias, visibility, and
      package-visible metadata; this surface is maintained before bootstrap but
      not expanded as a self-hosting blocker.
- [x] Reject runtime use of structural facts as a compile-time diagnostic.
- [x] Allow instantiated generic `law` / `finite law` CTFE bodies to branch on
      substituted type parameters and range-typed integer `comptime` parameters.
- [ ] Revisit structural-fact gap closure after self-hosting: identify useful
      missing facts from real Stark compiler code, implement them in the
      self-hosted compiler architecture, preserve them through package images
      when needed, and verify runtime erasure.

Reference: current type-predicate surface:

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

The landed predicates are `IsBool`, `IsInteger`, `IsFloat`, `IsRawPointer`,
`IsFixedArray`, `IsSlice`, `IsDynamic`, `IsFunctionPointer`, `IsClosure`,
`IsNamed`, `IsStruct`, `IsRecord`, `IsEnum`, `IsTrait`, `IsDoctrine`,
`IsDynTrait`, and `HasConcreteLayout`. They produce ordinary `bool` constants
only inside `comptime`; runtime use is rejected.

Current type-level concrete layout facts are `TypeSize<T>()`,
`TypeAlign<T>()`, and `TypeIsZeroSized<T>()`. They require concrete runtime
layout, work from package-backed typed interfaces, and are rejected with STK3054
for runtime use or non-concrete targets.

Current scalar type metadata facts are `TypeIntegerBitWidth<T>()`,
`TypeFloatBitWidth<T>()`, `TypeIntegerIsSigned<T>()`,
`TypeIntegerIsUnsigned<T>()`, `TypeIntegerIsFullRange<T>()`,
`TypeIntegerMinIs<T, Value>()`, and `TypeIntegerMaxIs<T, Value>()`. Bit-width
facts require the matching integer/float family; boolean integer metadata facts
fold for generic CTFE branching; min/max comparisons use signed `i1024`
comptime value arguments.

Current raw-pointer metadata facts are `RawPointerElementTypeIs<T, U>()`,
`RawPointerElementTypeIs*<T>()`, `RawPointerElementTypeHasConcreteLayout<T>()`,
`RawPointerIsMutable<T>()`, and `RawPointerIsReadOnly<T>()`. They require
`T` to be `rawptr<...>` or `rawmutptr<...>`, inspect the pointee after type
normalization, fold from local and package-backed typed aliases, reject
wrong-target/runtime use with STK3054, and erase before MIR/codegen.

Current element-bearing type metadata facts are `TypeElementTypeIs<T, U>()`,
`TypeElementTypeIs*<T>()`, and `TypeElementTypeHasConcreteLayout<T>()`. They
require an element-bearing type (`rawptr`, `rawmutptr`, fixed array, slice, or
`dynamic`) and inspect the element type after generic and alias normalization.
Current fixed-array length facts are `TypeFixedArrayLength<T>()` and
`TypeFixedArrayLengthIs<T, Value>()`; they require a fixed-array target and
fold through range-typed integer `comptime` generic substitutions such as
`T[N]`. Wrong-target/runtime use is rejected with STK3054, open generic
structural facts defer during generic body type checking, and all facts erase
before MIR/codegen.

Current top-level type qualifier metadata facts are `TypeHasQualifiers<T>()`,
`TypeBorrowKindIsNone<T>()`, `TypeBorrowKindIsBorrow<T>()`,
`TypeBorrowKindIsRetBorrow<T>()`, `TypeBorrowKindIsStoreBorrow<T>()`,
`TypeAccessKindIsNone<T>()`, `TypeAccessKindIsShared<T>()`,
`TypeAccessKindIsFrozen<T>()`, `TypeInitializationKindIsNone<T>()`,
`TypeInitializationKindIsOut<T>()`, `TypeInitializationKindIsInit<T>()`,
`TypeIsMutableView<T>()`, and `TypeUnqualifiedTypeIs<T, U>()`. They inspect
only the top-level `borrow` / `retborrow` / `storeborrow`, `shared` /
`frozen`, `out` / `init`, and mutable-view state, while the existing type
category/layout/exact facts continue normalizing those qualifiers away. These
facts fold from direct types and package-backed typed aliases, reject runtime
use with STK3054, and erase before MIR/codegen.

Current count facts:

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

`FieldCount<T>()` returns the ordered source field count for structs and
records, and `0` for other types. `EnumVariantCount<T>()` returns the variant
count for enums, and `0` for other types. Both return `u64` constants only
inside `comptime`; runtime use is rejected.

Current indexed detail facts:

```stark
finite law u64[0 max] OffsetAt<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.FieldOffset<T, comptime I>();
}

finite law u64[0 max] OutcomeScore<T>()
{
    if (comptime System.Compiler.EnumVariantIsOk<T, 0>())
    {
        return comptime System.Compiler.EnumVariantPayloadCount<T, 0>();
    }

    return 0;
}
```

Field layout facts are `FieldOffset<T, I>()`, `FieldSize<T, I>()`,
`FieldAlign<T, I>()`, and `FieldIsMisaligned<T, I>()`. Enum variant facts are
`EnumVariantPayloadCount<T, I>()`, `EnumVariantTag<T, I>()`,
`EnumVariantIsOk<T, I>()`, and `EnumVariantIsErr<T, I>()`. `I` is a typed
comptime integer generic argument; concrete out-of-range indices are
compile-time errors, and symbolic indices can flow through generic CTFE until
specialization. These facts use the compiler's typed/layout models and emit no
runtime calls.

Current enum payload layout facts:

```stark
finite law u64[0 max] PayloadOffset<T, comptime u64[0 max] V, comptime u64[0 max] P>()
{
    return comptime System.Compiler.EnumVariantPayloadOffset<T, comptime V, comptime P>();
}
```

`EnumVariantPayloadOffset<T, VariantIndex, PayloadIndex>()`,
`EnumVariantPayloadSize<T, VariantIndex, PayloadIndex>()`, and
`EnumVariantPayloadAlign<T, VariantIndex, PayloadIndex>()` return `u64` CTFE
constants for the selected payload storage field after enum layout lowering.
`EnumVariantPayloadIsMisaligned<T, VariantIndex, PayloadIndex>()` returns
`bool`. The facts require concrete enum layout, reject runtime use and concrete
out-of-range indices with STK3054, support symbolic indices through generic
CTFE until specialization, work from package-backed typed interfaces, and erase
before MIR/codegen.

Current enum discriminant/tag facts:

```stark
finite law u64[0 max] VariantTag<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.EnumVariantTag<T, comptime I>();
}
```

`EnumVariantTag<T, I>()` returns the selected source variant's concrete
direct-tag value as a `u64` CTFE constant. This is a layout fact, not a source
ordinal shortcut: when a later unit variant is selected as the zero-tag variant,
the reported values follow the direct-tag layout. `EnumTagOffset<T>()`,
`EnumTagSize<T>()`, and `EnumTagAlign<T>()` expose the concrete tag storage
field layout; `EnumTagIsMisaligned<T>()` returns `bool`. These facts reject
runtime use and concrete out-of-range variant indices with STK3054, support
symbolic indices through generic CTFE until specialization, work from
package-backed typed interfaces, and erase before MIR/codegen.

Current layout-control attribute facts:

```stark
finite law u64[0 max] LayoutScore<T, comptime u64[0 max] I>()
{
    if (comptime System.Compiler.StructLayoutIsC<T>())
    {
        return comptime System.Compiler.StructPack<T>()
            + comptime System.Compiler.StructAlign<T>()
            + comptime System.Compiler.FieldExplicitOffset<T, comptime I>();
    }

    return 0;
}
```

`StructLayoutIsAuto<T>()`, `StructLayoutIsC<T>()`, and
`StructLayoutIsExplicit<T>()` return the declared struct layout kind.
`StructHasPack<T>()` / `StructPack<T>()` expose `[Pack(N)]`, and
`StructHasAlign<T>()` / `StructAlign<T>()` expose `[Align(N)]`.
`FieldHasExplicitOffset<T, I>()` / `FieldExplicitOffset<T, I>()` expose
field-level `[FieldOffset(N)]` metadata for structs and records. Numeric facts
return `0` when the metadata is absent, so code should branch on the matching
`Has*` fact when `0` is a meaningful value, especially for explicit field
offsets. Concrete out-of-range field indices are STK3054 compile-time errors;
symbolic indices flow through generic CTFE until specialization. The facts erase
before MIR/codegen and are preserved through package-backed typed interfaces.

Current indexed exact type facts:

```stark
finite law bool HasFieldType<T, U, comptime u64[0 max] I>()
{
    return comptime System.Compiler.FieldTypeIs<T, U, comptime I>();
}

finite law bool HasPayloadType<T, U, comptime u64[0 max] V, comptime u64[0 max] P>()
{
    return comptime System.Compiler.EnumVariantPayloadTypeIs<T, U, comptime V, comptime P>();
}
```

`FieldTypeIs<T, U, I>()` returns whether the `I`th struct/record field has
exact type `U`. `EnumVariantPayloadTypeIs<T, U, VariantIndex, PayloadIndex>()`
returns whether the selected enum payload field has exact type `U`. Both return
`bool` constants only inside `comptime`, reject concrete out-of-range indices at
type check, accept symbolic indices through generic CTFE until specialization,
and erase before MIR/codegen.

Current indexed field/payload type-category facts:

```stark
finite law bool FieldIsNumber<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.FieldTypeIsInteger<T, comptime I>();
}

finite law bool PayloadIsRecord<T, comptime u64[0 max] V, comptime u64[0 max] P>()
{
    return comptime System.Compiler.EnumVariantPayloadTypeIsRecord<T, comptime V, comptime P>();
}
```

Field category facts are `FieldTypeIsBool<T, I>()`,
`FieldTypeIsInteger<T, I>()`, `FieldTypeIsFloat<T, I>()`,
`FieldTypeIsRawPointer<T, I>()`, `FieldTypeIsFixedArray<T, I>()`,
`FieldTypeIsSlice<T, I>()`, `FieldTypeIsDynamic<T, I>()`,
`FieldTypeIsFunctionPointer<T, I>()`, `FieldTypeIsClosure<T, I>()`,
`FieldTypeIsDynTrait<T, I>()`, `FieldTypeIsNamed<T, I>()`,
`FieldTypeIsStruct<T, I>()`,
`FieldTypeIsRecord<T, I>()`, `FieldTypeIsEnum<T, I>()`,
`FieldTypeIsTrait<T, I>()`, `FieldTypeIsDoctrine<T, I>()`, and
`FieldTypeHasConcreteLayout<T, I>()`.

Enum payload category facts use the same category names with the
`EnumVariantPayloadType...<T, VariantIndex, PayloadIndex>()` prefix, including
`EnumVariantPayloadTypeHasConcreteLayout<T, VariantIndex, PayloadIndex>()`.
They mirror the top-level `System.Compiler.Is*<T>()` /
`HasConcreteLayout<T>()` semantics for a selected field or enum payload. These
facts return `bool` constants only inside `comptime`, reject concrete
out-of-range indices at type check, accept symbolic indices through generic
CTFE until specialization, erase before MIR/codegen, and work from
package-backed typed interfaces.

Current indexed field/payload type metadata facts:

- `FieldTypeDisplayName<T, I>()` and
  `EnumVariantPayloadTypeDisplayName<T, VariantIndex, PayloadIndex>()` return
  the normalized selected field/payload type spelling as `ascii`.
- `FieldTypeBaseName<T, I>()` and
  `EnumVariantPayloadTypeBaseName<T, VariantIndex, PayloadIndex>()` return the
  selected named type's non-instantiated base name as `ascii`, or empty `ascii`
  for non-named types.
- `FieldTypeIsGenericInstantiation<T, I>()` and
  `EnumVariantPayloadTypeIsGenericInstantiation<T, VariantIndex,
  PayloadIndex>()` return whether the selected type has actual type arguments or
  `comptime` value arguments.
- `FieldTypeArgumentCount<T, I>()` /
  `FieldTypeComptimeArgumentCount<T, I>()` and the matching
  `EnumVariantPayloadType...Count<T, VariantIndex, PayloadIndex>()` facts return
  selected type argument counts.
- Field and enum-payload qualifier metadata facts mirror the top-level
  `Type*` qualifier facts for the selected declaration-contained type:
  `HasQualifiers`, the `BorrowKindIs*`, `AccessKindIs*`, and
  `InitializationKindIs*` predicates, `IsMutableView`, and
  `UnqualifiedTypeIs`. These inspect the selected field or payload type before
  qualifier normalization, reject invalid runtime use and concrete out-of-range
  indices, and are covered through package-backed typed interfaces.

These metadata facts fold from local and package-backed typed interfaces, reject
concrete out-of-range indices with STK3054, accept symbolic indices through
generic CTFE until specialization, and erase before MIR/codegen.

Current function-pointer structural facts:

```stark
alias Callback = fnptr<ffi(c) finite law i32[min max](i32[min max], rawptr<i8[min max]>)>;

finite law bool FirstParameterIsNumber<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.FunctionPointerParameterTypeIsInteger<T, comptime I>();
}

finite law i32[min max] Score()
{
    if (comptime System.Compiler.FunctionPointerKindIsFiniteLaw<Callback>())
    {
        if (comptime System.Compiler.FunctionPointerAbiIsC<Callback>())
        {
            if (comptime FirstParameterIsNumber<Callback, 0>())
            {
                return 7;
            }
        }
    }

    return 0;
}
```

Function-pointer facts are `FunctionPointerParameterCount<T>()`,
`FunctionPointerReturnTypeIs<T, U>()`,
`FunctionPointerParameterTypeIs<T, U, I>()`, return-type category facts with
the `FunctionPointerReturnTypeIs...<T>()` prefix, parameter category facts with
the `FunctionPointerParameterTypeIs...<T, I>()` prefix,
`FunctionPointerReturnTypeHasConcreteLayout<T>()`,
`FunctionPointerParameterTypeHasConcreteLayout<T, I>()`,
return-type metadata facts `FunctionPointerReturnTypeDisplayName<T>()`,
`FunctionPointerReturnTypeBaseName<T>()`,
`FunctionPointerReturnTypeIsGenericInstantiation<T>()`,
`FunctionPointerReturnTypeArgumentCount<T>()`, and
`FunctionPointerReturnTypeComptimeArgumentCount<T>()`, parameter metadata facts
`FunctionPointerParameterTypeDisplayName<T, I>()`,
`FunctionPointerParameterTypeBaseName<T, I>()`,
`FunctionPointerParameterTypeIsGenericInstantiation<T, I>()`,
`FunctionPointerParameterTypeArgumentCount<T, I>()`, and
`FunctionPointerParameterTypeComptimeArgumentCount<T, I>()`,
return/parameter actual type-argument facts
`FunctionPointerReturnTypeArgumentTypeIs<T, U, ArgumentIndex>()`,
`FunctionPointerParameterTypeArgumentTypeIs<T, U, ParameterIndex, ArgumentIndex>()`,
matching category predicates such as
`FunctionPointerReturnTypeArgumentTypeIsInteger<T, ArgumentIndex>()`, and
matching display/base/module/generic-shape metadata facts,
return/parameter qualifier metadata facts for borrow kind, access kind,
initialization kind, mutable view, qualifier presence, and unqualified type
comparison,
bounded raw-pointer parameter count-expression facts
`FunctionPointerParameterHasRawPointerElementCountExpression<T, I>()` and
`FunctionPointerParameterRawPointerElementCountExpression<T, I>()`,
`FunctionPointerKindIsFn<T>()`, `FunctionPointerKindIsFinite<T>()`,
`FunctionPointerKindIsLaw<T>()`, `FunctionPointerKindIsFiniteLaw<T>()`,
`FunctionPointerIsUnsafe<T>()`, `FunctionPointerHasFfiAbi<T>()`, exact ABI predicates for `c`, `cdecl`,
`stdcall`, `fastcall`, `thiscall`, `vectorcall`, `sysv`, `win64`, `aapcs`, and
`aapcs64`, plus parameter memory-contract facts
`FunctionPointerParametersAreDisjoint<T, LeftIndex, RightIndex>()`,
`FunctionPointerParametersOverlap<T, LeftIndex, RightIndex>()`, and
`FunctionPointerParametersAreSame<T, LeftIndex, RightIndex>()`. Category facts
mirror top-level `System.Compiler.Is*<T>()` / `HasConcreteLayout<T>()`
semantics for selected return and parameter types. Count/kind/safety/ABI predicates
fold from the function-pointer type itself; indexed parameter facts and
parameter-pair facts reject concrete out-of-range parameter indices with
STK3054. Count-expression facts return the preserved source bound, such as
`arg1`, or empty `ascii` when the selected parameter is not a bounded raw
pointer. `same` is treated as a stronger form of overlap for the broad
`FunctionPointerParametersOverlap` query. Nested type metadata display names
use the compiler's current type display spelling; imported package-backed named
types include their module qualification in display names while `BaseName`
returns the unqualified generic base. All facts are `comptime`-only, erase
before MIR/codegen, and work from package-backed typed aliases and type
references.

Current closure structural facts:

```stark
alias Callback = heap closure<fn i32[min max](i32[min max])>;

finite law bool IsUnaryHeapClosure<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.ClosureParameterCount<T>() == 1
        && comptime System.Compiler.ClosureParameterTypeIsInteger<T, comptime I>()
        && comptime System.Compiler.ClosureReturnTypeIsInteger<T>()
        && comptime System.Compiler.ClosureKindIsFn<T>()
        && comptime System.Compiler.ClosureStorageIsHeap<T>()
        && comptime System.Compiler.ClosureCallCapabilityIsNormal<T>();
}
```

Closure facts are `ClosureParameterCount<T>()`,
`ClosureReturnTypeIs<T, U>()`, `ClosureParameterTypeIs<T, U, I>()`,
return-type category facts with the `ClosureReturnTypeIs...<T>()` prefix,
parameter category facts with the `ClosureParameterTypeIs...<T, I>()` prefix,
`ClosureReturnTypeHasConcreteLayout<T>()`,
`ClosureParameterTypeHasConcreteLayout<T, I>()`, return-type metadata facts
`ClosureReturnTypeDisplayName<T>()`, `ClosureReturnTypeBaseName<T>()`,
`ClosureReturnTypeIsGenericInstantiation<T>()`,
`ClosureReturnTypeArgumentCount<T>()`, and
`ClosureReturnTypeComptimeArgumentCount<T>()`,
`ClosureReturnTypeHasCSourceAlias<T>()`, and
`ClosureReturnTypeCSourceAliasName<T>()`, parameter metadata facts
`ClosureParameterTypeDisplayName<T, I>()`,
`ClosureParameterTypeBaseName<T, I>()`,
`ClosureParameterTypeIsGenericInstantiation<T, I>()`,
`ClosureParameterTypeArgumentCount<T, I>()`,
`ClosureParameterTypeComptimeArgumentCount<T, I>()`,
`ClosureParameterTypeHasCSourceAlias<T, I>()`, and
`ClosureParameterTypeCSourceAliasName<T, I>()`,
return/parameter actual type-argument facts
`ClosureReturnTypeArgumentTypeIs<T, U, ArgumentIndex>()`,
`ClosureParameterTypeArgumentTypeIs<T, U, ParameterIndex, ArgumentIndex>()`,
matching category predicates such as
`ClosureReturnTypeArgumentTypeIsInteger<T, ArgumentIndex>()`, and matching
display/base/module/generic-shape metadata facts,
return/parameter qualifier metadata facts for borrow kind, access kind,
initialization kind, mutable view, qualifier presence, and unqualified type
comparison,
bounded raw-pointer parameter count-expression facts
`ClosureParameterHasRawPointerElementCountExpression<T, I>()` and
`ClosureParameterRawPointerElementCountExpression<T, I>()`, exact function-kind
predicates, exact storage predicates for `borrow`, `heap`, and `inline`
closures, exact call-capability predicates for normal/no-marker, `mut`, and
`once` closures, plus `ClosureParametersAreDisjoint`,
`ClosureParametersOverlap`, and `ClosureParametersAreSame`. The facts fold from
`closure<...>` type metadata, including aliases and direct type references,
reject wrong targets and concrete out-of-range indices with STK3054, treat
`same` as a stronger form of overlap, preserve imported package-backed display
names the same way function-pointer metadata does, and erase before MIR/codegen.
Count-expression facts return synthetic callable parameter names such as
`arg1`, matching the `closure<...>` type syntax, or empty `ascii` when absent.

Current method structural facts:

```stark
struct Box<T>
{
    T Value;

    finite law T Echo(borrow Box<T> self, T fallback)
    {
        return fallback;
    }
}

finite law bool FirstMethodReturnsI32()
{
    return comptime System.Compiler.MethodCount<Box<i32[min max]>>() == 1
        && comptime (System.Compiler.MethodName<Box<i32[min max]>, 0>() == "Echo")
        && comptime (System.Compiler.MethodParameterName<Box<i32[min max]>, 0, 1>() == "fallback")
        && comptime System.Compiler.MethodReturnTypeIs<Box<i32[min max]>, i32[min max], 0>()
        && comptime System.Compiler.MethodParameterTypeIsInteger<Box<i32[min max]>, 0, 1>();
}
```

Method facts treat each overload signature as a deterministic method slot,
ordered by member name, parameter signature, return type, source name, and
resolved symbol name. The current surface includes `MethodCount<T>()`,
`MethodName<T, MethodIndex>()`, `MethodModuleName<T, MethodIndex>()`,
`MethodVisibilityIsModule<T, MethodIndex>()`,
`MethodVisibilityIsInternal<T, MethodIndex>()`,
`MethodVisibilityIsPublic<T, MethodIndex>()`,
`MethodVisibilityIsExport<T, MethodIndex>()`,
`MethodParameterCount<T, MethodIndex>()`,
`MethodParameterName<T, MethodIndex, ParameterIndex>()`,
`MethodReturnTypeIs<T, U, MethodIndex>()`,
`MethodParameterTypeIs<T, U, MethodIndex, ParameterIndex>()`, return/parameter
type identity/generic-shape metadata facts, return/parameter type-category
predicates with the `MethodReturnTypeIs...` and
`MethodParameterTypeIs...` prefixes, return/parameter actual type-argument
facts with the `MethodReturnTypeArgumentType...` and
`MethodParameterTypeArgumentType...` prefixes, return/parameter qualifier metadata facts
for borrow kind, access kind, initialization kind, mutable view, qualifier
presence, and unqualified type comparison, `MethodKindIsFn`, `MethodKindIsFinite`,
`MethodKindIsLaw`, `MethodKindIsFiniteLaw`, `MethodIsStatic`, `MethodHasBody`,
`MethodIsUnsafe`, `MethodIsVarargs`, `MethodHasFfiAbi`, exact ABI predicates
for `c`, `cdecl`, `stdcall`, `fastcall`, `thiscall`, `vectorcall`, `sysv`,
`win64`, `aapcs`, and `aapcs64`, parameter memory-contract predicates
`MethodParametersAreDisjoint`, `MethodParametersOverlap`, and
`MethodParametersAreSame`, bounded raw-pointer parameter count-expression facts
`MethodParameterHasRawPointerElementCountExpression` and
`MethodParameterRawPointerElementCountExpression`, and method
generic/comptime-generic metadata facts
`MethodGenericParameterCount`, `MethodGenericParameterName`,
`MethodGenericParameterTraitBoundCount`,
`MethodGenericParameterTraitBoundTypeIs`, method generic trait-bound type
predicate/metadata facts,
`MethodComptimeGenericParameterCount`, `MethodComptimeGenericParameterName`,
`MethodComptimeGenericParameterTypeIs`, and method `comptime` generic
parameter type predicate/metadata facts.
Method count-expression facts return the preserved source parameter name or
expression, such as `length`, and empty `ascii` when absent.

For generic owner types, method return/parameter/comptime-parameter type
queries substitute the owner type arguments before comparison or metadata
reporting. Concrete out-of-range method, parameter, and generic-parameter
indices are STK3054 compile-time errors; symbolic indices may flow through
generic CTFE until specialization. These facts are `comptime`-only, erase before
MIR/codegen, and work from package-backed typed interfaces.

Current dyn-trait structural facts:

```stark
dyn trait Speaker
{
    finite law i32[min max] Speak(borrow Self self);
}

finite law bool IsSpeakerView<T>()
{
    return comptime System.Compiler.IsDynTrait<T>()
        && comptime System.Compiler.DynTraitIsView<T>()
        && comptime System.Compiler.DynTraitTargetTypeIs<T, Speaker>();
}
```

Dyn-trait facts are `IsDynTrait<T>()`, `DynTraitIsView<T>()`,
`DynTraitIsHeap<T>()`, and `DynTraitTargetTypeIs<T, Trait>()`. They fold from
`borrow dyn Trait` / `heap dyn Trait` type metadata, reject runtime use with
STK3054, reject non-dyn first arguments and non-trait second arguments for the
target comparison, erase before MIR/codegen, and survive typed package-image
round trips, source bridge rendering, and imported typed aliases. Nested
category predicates such as `FieldTypeIsDynTrait`, `MethodReturnTypeIsDynTrait`,
`FunctionPointerParameterTypeIsDynTrait`, `TypeArgumentTypeIsDynTrait`,
`EnumVariantPayloadTypeIsDynTrait`, and `AssociatedTypeTargetTypeIsDynTrait`
use the same dyn-trait predicate.

Current indexed name facts:

```stark
finite law ascii FieldNameAt<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.FieldName<T, comptime I>();
}

finite law bool IsOkName<T, comptime u64[0 max] I>()
{
    return comptime (System.Compiler.EnumVariantName<T, comptime I>() == "Ok");
}
```

`FieldName<T, I>()` returns the `I`th struct/record field name as an `ascii`
compile-time text constant. `FieldVisibilityIsModule<T, I>()`,
`FieldVisibilityIsInternal<T, I>()`, `FieldVisibilityIsPublic<T, I>()`, and
`FieldVisibilityIsExport<T, I>()` expose the selected field's declared or
inherited visibility. `EnumVariantName<T, I>()` returns the `I`th enum variant
name as an `ascii` compile-time text constant. These facts reject runtime use
and concrete out-of-range indices with STK3054, support symbolic indices through
generic CTFE until specialization, and erase before MIR/codegen. CTFE text
equality compares decoded text payloads, so generated names can be compared with
ordinary string literals.

Current enum payload name facts:

```stark
finite law ascii PayloadNameAt<T, comptime u64[0 max] V, comptime u64[0 max] P>()
{
    return comptime System.Compiler.EnumVariantPayloadName<T, comptime V, comptime P>();
}

finite law bool IsNamedPayload<T, comptime u64[0 max] V, comptime u64[0 max] P>()
{
    return comptime System.Compiler.EnumVariantPayloadHasName<T, comptime V, comptime P>();
}
```

`EnumVariantUsesNamedFields<T, I>()` returns whether the `I`th enum variant uses
named-field payload syntax. `EnumVariantPayloadHasName<T, VariantIndex,
PayloadIndex>()` returns whether the selected payload field has a source name.
`EnumVariantPayloadName<T, VariantIndex, PayloadIndex>()` returns the selected
payload name as an `ascii` compile-time text constant, or an empty `ascii`
constant for positional payload fields. All reject runtime use and concrete
out-of-range indices with STK3054, support symbolic indices through generic CTFE
until specialization, and erase before MIR/codegen.

Current enum error-funnel facts:

```stark
finite law bool FunnelAbsorbs<T, U, comptime u64[0 max] I>()
{
    return comptime System.Compiler.EnumVariantIsErrorFunnel<T, comptime I>()
        && comptime System.Compiler.EnumVariantAbsorbsErrorTypeIs<T, U, comptime I>();
}
```

`EnumVariantIsErrorFunnel<T, I>()` returns whether the `I`th enum variant was
declared with the `Name from ErrorType` funnel payload form used by `try`.
`EnumVariantAbsorbsErrorTypeIs<T, U, I>()` returns whether that funnel absorbs
exact type `U`, after substituting generic type and `comptime` value arguments
from `T`. Both reject runtime use and concrete out-of-range variant indices
with STK3054, support symbolic indices through generic CTFE until
specialization, erase before MIR/codegen, and work from package-backed typed
interfaces.

Current named-type generic parameter facts:

```stark
finite law bool FirstComptimeParamIsU8<T, comptime u64[0 max] I>()
{
    return comptime (System.Compiler.TypeComptimeGenericParameterName<T, comptime I>() == "N")
        && comptime System.Compiler.TypeComptimeGenericParameterTypeIs<T, u8[1 8], comptime I>();
}

finite law bool FirstActualArgumentIsInteger<T>()
{
    return comptime System.Compiler.TypeIsGenericInstantiation<T>()
        && comptime System.Compiler.TypeArgumentCount<T>() > 0
        && comptime System.Compiler.TypeArgumentTypeIsInteger<T, 0>();
}
```

`TypeGenericParameterCount<T>()` and
`TypeComptimeGenericParameterCount<T>()` return declaration parameter counts for
named types. `TypeGenericParameterName<T, I>()` and
`TypeComptimeGenericParameterName<T, I>()` return source parameter names in
declaration order. `TypeComptimeGenericParameterTypeIs<T, U, I>()`,
`TypeComptimeGenericParameterTypeIs*<T, I>()`, and
`TypeComptimeGenericParameterTypeHasConcreteLayout<T, I>()` inspect the selected
`comptime` generic parameter's range-typed integer type after resolving the
generic base declaration for instantiated types such as `Buffer<i32[min max],
4>`. Metadata facts expose its display/base names and generic-shape counts.
Indexed facts reject runtime use and concrete out-of-range indices with STK3054,
support symbolic indices through generic CTFE until specialization, erase before
MIR/codegen, and work from package-backed
typed interfaces.

Current named/generic type metadata facts:

- `TypeDisplayName<T>()` returns the normalized source display spelling of `T`
  as `ascii`.
- `TypeBaseName<T>()` returns the named type's non-instantiated base name as
  `ascii`, or empty `ascii` for non-named types.
- `TypeModuleName<T>()` returns the declaring module name for named Stark
  declarations as `ascii`, including package-imported declarations and concrete
  generic instantiations, or empty `ascii` for non-named types.
- `TypeVisibilityIsModule<T>()`, `TypeVisibilityIsInternal<T>()`,
  `TypeVisibilityIsPublic<T>()`, and `TypeVisibilityIsExport<T>()` expose the
  declaration visibility of named Stark types and fold through package-backed
  typed interfaces.
- `TypeIsGenericInstantiation<T>()` returns whether `T` is a named type with
  actual type arguments or `comptime` value arguments.
- `TypeArgumentCount<T>()` returns the number of actual type arguments on `T`.
- `TypeComptimeArgumentCount<T>()` returns the number of actual `comptime`
  value arguments on `T`.
- `TypeComptimeArgumentName<T, I>()` returns the parameter name for actual
  `comptime` value argument `I` as `ascii`.
- `TypeComptimeArgumentTypeIs<T, U, I>()` returns whether actual `comptime`
  value argument `I` has exact type `U`.
- `TypeComptimeArgumentTypeIs*<T, I>()`,
  `TypeComptimeArgumentTypeHasConcreteLayout<T, I>()`, and
  `TypeComptimeArgumentTypeDisplayName/BaseName/IsGenericInstantiation/ArgumentCount/ComptimeArgumentCount<T, I>()`
  inspect the type of actual `comptime` value argument `I`.
- `TypeComptimeArgumentValueIs<T, I, Value>()` returns whether actual
  `comptime` value argument `I` equals the integer `Value`.
- `TypeArgumentTypeIs<T, U, I>()`, `TypeArgumentTypeIs*<T, I>()`, and
  `TypeArgumentTypeHasConcreteLayout<T, I>()` inspect actual type argument `I`
  and mirror the top-level type predicate vocabulary.
- `TypeArgumentTypeDisplayName/BaseName/IsGenericInstantiation/ArgumentCount/ComptimeArgumentCount<T, I>()`
  expose actual type argument metadata using the same nested-type vocabulary as
  field, method, associated-type, and enum payload metadata facts.

Indexed actual type/comptime-argument facts reject runtime use and concrete
out-of-range indices with STK3054. All named/generic type metadata facts erase before
MIR/codegen and work from package-backed type references.

Current trait-conformance fact:

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

`Implements<T, Trait>()` returns a `bool` constant only inside `comptime`.
The second generic argument must resolve to a trait type. The fact follows
the same static conformance model as `struct Widget : Drawable` and
`where T: Drawable`; it does not create trait objects or runtime dispatch.

Indexed implemented-trait facts expose the stored base-list contracts of a named
type:

- `ImplementedTraitCount<T>() -> u64[0 max]`
- `ImplementedTraitTypeIs<T, Trait, I>() -> bool`
- `ImplementedTraitTypeIsBool<T, I>() -> bool`
- `ImplementedTraitTypeIsInteger<T, I>() -> bool`
- `ImplementedTraitTypeIsFloat<T, I>() -> bool`
- `ImplementedTraitTypeIsRawPointer<T, I>() -> bool`
- `ImplementedTraitTypeIsFixedArray<T, I>() -> bool`
- `ImplementedTraitTypeIsSlice<T, I>() -> bool`
- `ImplementedTraitTypeIsDynamic<T, I>() -> bool`
- `ImplementedTraitTypeIsFunctionPointer<T, I>() -> bool`
- `ImplementedTraitTypeIsClosure<T, I>() -> bool`
- `ImplementedTraitTypeIsDynTrait<T, I>() -> bool`
- `ImplementedTraitTypeIsNamed<T, I>() -> bool`
- `ImplementedTraitTypeIsStruct<T, I>() -> bool`
- `ImplementedTraitTypeIsRecord<T, I>() -> bool`
- `ImplementedTraitTypeIsEnum<T, I>() -> bool`
- `ImplementedTraitTypeIsTrait<T, I>() -> bool`
- `ImplementedTraitTypeIsDoctrine<T, I>() -> bool`
- `ImplementedTraitTypeHasConcreteLayout<T, I>() -> bool`
- `ImplementedTraitTypeDisplayName<T, I>() -> ascii`
- `ImplementedTraitTypeBaseName<T, I>() -> ascii`
- `ImplementedTraitTypeModuleName<T, I>() -> ascii`
- `ImplementedTraitTypeIsGenericInstantiation<T, I>() -> bool`
- `ImplementedTraitTypeArgumentCount<T, I>() -> u64[0 max]`
- `ImplementedTraitTypeComptimeArgumentCount<T, I>() -> u64[0 max]`
- `ImplementedTraitTypeArgumentTypeIs<T, U, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsBool<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsInteger<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsFloat<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsRawPointer<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsFixedArray<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsSlice<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsDynamic<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsFunctionPointer<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsClosure<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsDynTrait<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsNamed<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsStruct<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsRecord<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsEnum<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsTrait<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeIsDoctrine<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeHasConcreteLayout<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeDisplayName<T, I, J>() -> ascii`
- `ImplementedTraitTypeArgumentTypeBaseName<T, I, J>() -> ascii`
- `ImplementedTraitTypeArgumentTypeModuleName<T, I, J>() -> ascii`
- `ImplementedTraitTypeArgumentTypeIsGenericInstantiation<T, I, J>() -> bool`
- `ImplementedTraitTypeArgumentTypeArgumentCount<T, I, J>() -> u64[0 max]`
- `ImplementedTraitTypeArgumentTypeComptimeArgumentCount<T, I, J>() -> u64[0 max]`
- `ImplementedTraitTypeComptimeArgumentName<T, I, J>() -> ascii`
- `ImplementedTraitTypeComptimeArgumentTypeIs<T, U, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsBool<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsInteger<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsFloat<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsRawPointer<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsFixedArray<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsSlice<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsDynamic<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsFunctionPointer<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsClosure<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsDynTrait<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsNamed<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsStruct<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsRecord<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsEnum<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsTrait<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeIsDoctrine<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeHasConcreteLayout<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeDisplayName<T, I, J>() -> ascii`
- `ImplementedTraitTypeComptimeArgumentTypeBaseName<T, I, J>() -> ascii`
- `ImplementedTraitTypeComptimeArgumentTypeModuleName<T, I, J>() -> ascii`
- `ImplementedTraitTypeComptimeArgumentTypeIsGenericInstantiation<T, I, J>() -> bool`
- `ImplementedTraitTypeComptimeArgumentTypeArgumentCount<T, I, J>() -> u64[0 max]`
- `ImplementedTraitTypeComptimeArgumentTypeComptimeArgumentCount<T, I, J>() -> u64[0 max]`
- `ImplementedTraitTypeComptimeArgumentValueIs<T, I, J, Value>() -> bool`

The indexed facts are compile-time-only, reject out-of-range trait and argument
indices, and work from package-backed trait metadata for imported types.

Current associated-type facts:

```stark
finite law bool HasI32Item<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.AssociatedTypeHasTarget<T, comptime I>()
        && comptime System.Compiler.AssociatedTypeTargetTypeIs<T, i32[min max], comptime I>();
}

finite law bool AssociatedTargetHasConcreteLayout<T, comptime u64[0 max] I>()
{
    return comptime System.Compiler.AssociatedTypeTargetTypeHasConcreteLayout<T, comptime I>();
}

finite law bool AssociatedTargetIsGenericHolder<T, comptime u64[0 max] I>()
{
    return comptime (System.Compiler.AssociatedTypeTargetTypeBaseName<T, comptime I>() == "Holder")
        && comptime System.Compiler.AssociatedTypeTargetTypeIsGenericInstantiation<T, comptime I>()
        && comptime System.Compiler.AssociatedTypeTargetTypeArgumentCount<T, comptime I>() == 1
        && comptime System.Compiler.AssociatedTypeTargetTypeComptimeArgumentCount<T, comptime I>() == 1;
}
```

`AssociatedTypeCount<T>()` returns the number of associated aliases declared on
`T`. `AssociatedTypeName<T, I>()` returns the associated alias name at index `I`
as an `ascii` compile-time text constant. `AssociatedTypeHasTarget<T, I>()`
distinguishes required trait aliases such as `alias Item;` from aliases with a
target type such as `alias Item = i32[min max];`.
`AssociatedTypeTargetTypeIs<T, U, I>()` compares the selected associated alias
target against `U`, after substituting generic type and `comptime` value
arguments from `T`. `AssociatedTypeTargetTypeIs*<T, I>()` and
`AssociatedTypeTargetTypeHasConcreteLayout<T, I>()` mirror the landed field,
payload, method, function-pointer, and closure category predicates for the
selected associated alias target. `AssociatedTypeTargetTypeDisplayName<T, I>()`,
`AssociatedTypeTargetTypeBaseName<T, I>()`,
`AssociatedTypeTargetTypeIsGenericInstantiation<T, I>()`,
`AssociatedTypeTargetTypeArgumentCount<T, I>()`, and
`AssociatedTypeTargetTypeComptimeArgumentCount<T, I>()` expose selected target
type identity and generic-shape metadata after the same owner substitution.
Required aliases without a target return false for target category predicates
and default metadata values (`""`, `false`, `0`) for target metadata facts.
Associated aliases are indexed in deterministic ordinal name order, matching
package-image source bridge and typed-interface ordering. These facts reject
runtime use and concrete out-of-range indices with STK3054, support symbolic
indices through generic CTFE until specialization, erase before MIR/codegen, and
work from package-backed typed interfaces.

## Alias/Noalias Proofs

- [x] Decide the proof model: explicit compile-time-only proof carriers for
      APIs that need alias facts.
- [x] Require wrong alias/noalias proof use to be a compile-time diagnostic,
      not backend undefined behavior.
- [x] Keep external alias facts fenced behind `unsafe assume disjoint(...)`
      with explicit memory-root checks.
- [x] Implement proof-carrier symbols/types, validation, package-image
      preservation, and diagnostics.

Notes:

Proof carriers make alias-sensitive compiler APIs explicit without adding
runtime cost. They are visible in Stark source, checked by type/lowering/SSA
validation, and erased before codegen. The compiler may still lower verified
facts into scoped noalias groups, memory root keys, and LLVM metadata, but LLVM
is not the first line of defense.

Implementation status:

- Declaration memory contracts (`where disjoint`, `where overlap`, `where same`)
  are preserved in package images.
- Runtime `if disjoint(...)` and trusted `assume disjoint(...)` facts become
  typed `AliasProofCarrier` values attached to scoped noalias SSA metadata.
- SSA validation rejects missing carrier ids, mismatched carrier/group roots,
  duplicate or blank memory roots, invalid root-key shapes, and roots that do
  not name parameters of the owning function.

## Threading Coordination

- [x] Decide build-driver concurrency scope: synchronous self-hosting driver
      first, no `async`/`await`.
- [x] Limit future parallel build/test support to explicit payload thread starts,
      ergonomic guarded shared state, and channels.
- [x] Add payload thread starts checked by `Transferable`.
- [x] Add `System.Threading.Synchronized<T>` and `Locked<T>` as the blessed easy
      shared-state primitive.
- [x] Add MPSC channels for progress, diagnostics, and result publication.
- [ ] Keep thread pools, work stealing, `RwLock`, `Once`, condition variables,
      semaphores, thread locals, and parallel compiler passes out of the
      self-hosting scope unless a later decision reopens them.

Notes:

This is deliberately small. Stark already has threads and atomics; doc `22`
defines the coordination layer needed if project builds or test execution become
parallel. Shared mutable data should be wrapped in `Synchronized<T>` and accessed
through an owned guard. Workers should publish events through channels, and the
driver should aggregate diagnostics/artifacts in deterministic order.

## libLLVM Backend Integration

- [x] Decide LLVM integration: libLLVM is the primary backend through the LLVM C
      API.
- [x] Keep textual LLVM as a debug, diagnostic, golden-test, stage-comparison,
      and artifact-inspection output.
- [~] Implement the verified FFI pieces still required by libLLVM: C strings,
      out-pointer patterns, typed opaque handles, deterministic foreign-resource
      disposal, and C enum/bitflag constants. Core `System.C` C-string helpers
      have landed; the remaining libLLVM FFI pieces are still open.
- [ ] Add the initial direct LLVM module-construction and object-emission path
      through typed wrappers.
- [ ] Expand direct LLVM module construction until it covers the full backend.

Notes:

This direction treats libLLVM as roadmap work, not as a blocker to avoid. The
binding must use LLVM's C API only. Textual LLVM remains valuable for debugging
and golden artifacts, but it must be printed from the in-memory module and never
parsed as a bootstrap or production object-emission path. See
`23-libllvm-integration.md`.

## Explicit Runtime Dispatch

- [x] Add a blessed pattern for explicit runtime dispatch using ops tables.
- [x] Keep ops tables visible in source as ordinary structs.
- [x] Require dispatch function pointers to spell their function kind, such as
      `fn`, `finite`, `law`, or `finite law`.
- [x] Make any type-erased context pointer or unsafe boundary explicit.
- [x] Prefer closed-world enums when the set of implementations is known.
- [x] Reserve ops tables for genuinely open runtime extension points.

An explicit ops table is basically a vtable, but it is not hidden. The caller
can see the context pointer, the ops table, and the function pointer types.
That keeps the cost model and safety boundary Stark-shaped.
Function-pointer fields in ordinary structs now lower as first-class indirect
calls when called through a field path, so `handle.Ops.Resolve(...)` works
without copying the slot into a temporary local.

Example shape:

```stark
struct ModuleResolverOps
{
    fnptr<unsafe finite law ResolveResult(
        rawmutptr<i8[min max]> context,
        ascii moduleName)> Resolve;
}

struct ModuleResolverHandle
{
    rawmutptr<i8[min max]> Context;
    ModuleResolverOps Ops;
}
```

Open questions for this pattern:

- [x] Should the context pointer always be raw, or should Stark offer a typed
      erased-handle wrapper? V1 uses explicit raw context pointers.
- [ ] Should ops tables be `const` by convention?
- [x] Should ops functions carry explicit memory contracts such as
      `where disjoint(...)` when they touch caller buffers? Yes: use the
      existing memory-contract syntax on the `fnptr` type when the callback
      touches caller-owned buffers.
- [x] Should there be a standard naming convention: `FooOps`, `FooHandle`,
      `FooContext`? Yes: use those names unless a domain term is clearer.

## Closed-world Runtime Choice

- [x] Prefer `enum` plus exhaustive `switch` for runtime variation when all
      implementations are known.
- [x] Use this for compiler-internal choices such as module resolver kind,
      diagnostic output mode, package section kind, target platform, and pass
      kind where practical.

Example shape:

```stark
enum ModuleResolver
{
    Empty(EmptyModuleResolver),
    FileSystem(FileSystemModuleResolver),
    InMemory(InMemoryModuleResolver),
    Package(PackageImageResolver),
}
```

This keeps dispatch visible and gives the compiler exhaustiveness checks.

## Pattern Matching

- [x] Add switch-label or-pattern alternatives: `case A | B:`.
- [x] Require alternatives that share a switch body to bind the same capture
      names with the same types.
- [x] Preserve native literal-switch lowering for literal-only or-patterns.
- [x] Add inclusive integer range patterns for dense numeric/compiler-token
      classification: `case 0..10:`.
- [x] Support range patterns inside enum/aggregate field patterns and typed
      package-image templates.
- [x] Add aggregate property patterns: `case Box { Field: pattern }:`.
- [x] Add exact-length list patterns over fixed arrays, slices, and dynamic
      storage: `case [first, second]:`.
- [x] Preserve property/list pattern facts in typed package-image templates.

Notes:

Or-patterns are section-local alternatives: `case A | B when guard:` tests
`A`, then `B`, and either successful alternative flows into the same guarded
body. Capture-bearing alternatives must agree on names and types so the body's
locals are definitely initialized regardless of which alternative matched.

Range patterns are inclusive and integer-only. Type checking rejects empty or
non-overlapping ranges, coverage uses intervals rather than value expansion,
and lowering emits equality, one-sided, or two-sided comparisons depending on
the target type bounds.

Property patterns name aggregate fields explicitly and must mention every field
exactly once. List patterns are exact-length only: fixed-array length mismatches
are compile-time errors, while slice and dynamic-storage patterns lower to a
runtime length check followed by direct element tests. No iterator protocol,
hidden allocation, or runtime dispatch is involved.

## Traversal Loops

- [x] Decide the pre-self-hosting traversal surface: exactly three explicit
      `for ... in ...` loop forms.
- [x] Do not add a general iterator protocol, `yield`, LINQ-style traversal,
      hidden iterator allocation, or hidden runtime dispatch before
      self-hosting.
- [x] Implement borrowed element traversal:

```stark
for willexit (borrow Token token in tokens)
{
    Process(token);
}
```

- [x] Implement mutable borrowed element traversal:

```stark
for willexit (borrow mut Token token in tokens)
{
    Normalize(token);
}
```

- [x] Implement indexed borrowed traversal:

```stark
for willexit (stack u64[0 max] index, borrow Token token in tokens)
{
    Record(index, token);
}
```

Notes:

These are loop syntax conveniences over optimized collection/slice traversal.
They must preserve Stark's explicit loop behavior keyword (`willexit`,
`independent`, and so on) and must not allocate iterator objects. Hot compiler
paths and shapes outside these three forms should continue to use explicit
`Length` / index / slice APIs. Mutating with an index remains an explicit
C-style indexed loop before any broader traversal design.

Implementation status: landed for fixed arrays, slices, and dynamic storage.
Traversal lowers to a counted loop over existing element-address operations; it
does not allocate an iterator object or introduce hidden runtime dispatch.
Typed package-image generic bodies preserve traversal source/index/element
bindings explicitly, so imported generic helpers lower without falling back to
body text or generated parser source.

## Comptime Generics

- [x] Decide Stark's const-generic spelling: typed `comptime` generic value
      parameters.
- [x] Use `comptime`, not `const`, because Stark `const` means deep interior
      immutability while this feature means compile-time generic
      specialization.
- [x] Implement typed comptime generic value parameters:

```stark
struct FixedBuffer<T, comptime u64[0 max] N>
{
    Items: T[N];
}
```

- [x] Allow range-typed integer comptime generic values in fixed-array lengths
      and materialize specialized values as scalar expressions such as
      `return N`.
- [x] Define monomorphization identity, overload resolution, diagnostics,
      and package-image/source-bridge representation for comptime generic
      arguments.
- [x] Initial implementation support: parse `comptime` generic parameters, preserve
      range-typed integer value parameters in typed signatures/named types,
      allow symbolic fixed-array lengths such as `T[N]`, infer `N` from
      concrete fixed-array arguments during overload resolution, reject
      out-of-range inferred lengths, and include comptime values in function
      instantiation keys.
- [x] Add explicit integer value-argument syntax at type and function call
      sites, including symbolic forwarding with `comptime N`.
- [x] Remaining implementation work: value substitution through imported
      template bodies and full package/source-surface metadata for comptime
      parameter declarations.

Notes:

`comptime` in a generic parameter list is a declaration marker, not expression
syntax. It binds a typed compile-time value parameter that participates in
generic specialization:

```stark
fn u64[0 max] Length<T, comptime u64[0 max] N>(borrow T[N] items)
{
    return N;
}

stack i32[min max][3] values = { 10, 20, 30 };
stack u64[0 max] count = Length<i32[min max], 3>(values);
```

Initial self-hosting support focuses on range-typed integer values for
array sizes, layout facts, fixed-capacity buffers, and table shapes. Additional
compile-time value kinds can be added when a concrete compiler or stdlib use
requires them.

## Comptime

- [x] Decide `comptime` scope split: preserve the current pre-self-host
      baseline and defer broad compile-time branching over program structure
      until after bootstrap.
- [x] Keep program-structure facts compile-time-only and erased before backend
      lowering; do not add runtime reflection as part of this feature.
- [x] Freeze the pre-self-host `comptime` baseline at the currently landed host
      capability: expression/block CTFE, typed integer `comptime` generics,
      deterministic local mutation, bounded CTFE loops/traversal, aggregate
      constants, layout queries, switch/pattern execution, supported
      finite/law calls, existing compile-time-only `System.Compiler` facts,
      diagnostics for unsupported compile-time execution, and erasure before
      runtime lowering.
- [ ] Revisit broad `comptime` after self-hosting. That phase owns evaluator
      parity, the stable structural-fact surface, complete package/source
      preservation for broad CTFE helper bodies, and self-hosted compiler
      conformance tests.

Notes:

`comptime` is still ordinary Stark code selected to run during compilation.
Before self-hosting, this is a maintained baseline rather than an expanding
feature area. Broad compile-time branching over program structure should use
visible structural facts and ordinary `if` / `switch`, not hidden runtime
reflection or a separate macro sublanguage, and is deferred until the
self-hosted compiler architecture can own it.

## Error And Optional Values

- [x] Define shared `Option<T>` and `Result<T, E>` conventions.
- [x] Use leading `try` propagation; do not add a `?`-style propagation operator.
- [x] Do not add a compiler-invariant failure API; make invalid states
      unrepresentable and report the residual through explicit error values or
      process exit.

Self-hosting needs a replacement for C# nullability, exceptions, and
`TryGet(... out value)` patterns. Recoverable failures should remain values.
The blessed replacement for C# nullable values is `System.Option<T>` with
`[Ok] Some(T)` / `[Err] None`. Safe Stark references and borrows are never
nullable; raw `null` remains only for raw pointers and FFI. Project-local
option-shaped enums remain legal because propagation is structural over
`[Ok]`/`[Err]`, but compiler-port code should default to `System.Option<T>`.
Internal compiler bugs use the error model documented in
`09-self-hosted-compiler-architecture.md`.

## Compiler Text Literals

- [x] Add exact-preserving single-line raw string literals: `raw"..."`.
- [x] Add exact-preserving multiline raw string literals: `raw"""..."""`.
- [x] Compose raw literals with interpolation: `$raw"..."` and
      `$raw"""..."""`.
- [x] Keep raw literals in the existing `StringLiteral` token family so
      parser, typing, lowering, and package-image code continue to use the
      ordinary text-literal pipeline.
- [x] Add Stark-side text escaping/decoding helpers in `System.Text` for
      diagnostics, LLVM text, golden files, and source snippets. `System.Text`
      now exposes allocation-aware string-literal escaping plus ordinary/raw
      string literal and character literal decoding. Track remaining
      compiler-grade text rendering under stdlib gap S02 and test golden-file
      machinery under S18.

Rules landed in the host compiler:

- raw literals do not interpret escape sequences
- multiline raw literals preserve content exactly between the delimiters
- raw single-line literals cannot contain an unescaped `"` or a line break
- raw multiline literals close at the next `"""`
- interpolation holes still use `{...}`, with `{{` and `}}` for literal braces

## Compiler-grade Standard Library

- [x] String-key dictionaries for `ascii`, `unicode`, `OwnedAscii`, and
      `OwnedUnicode` through canonical `Hash` + `Eq` contracts.
- [x] `HashSet<T>`.
- [x] Strongly typed compiler symbol/name interning.
- [~] Deterministic sorting and ordered set/map helpers. `SortBy<T>` has
      landed as an in-place comparator-based slice sort, and `Sort<T>` has
      landed as the direct `T: Ord` slice-sort path; ordered set/map helpers
      remain.
- [ ] Text builder and formatting APIs.
- [ ] JSON support for `.starkpkg.json`, unless the package image format changes.
- [ ] Reusable `System.Toml` parser/emitter for `Stark.toml`,
      `Stark.solution.toml`, `Stark.user.toml`, tests, tools, and user code.
- [x] File read-all/write-all and line-oriented file reading helpers.
- [~] Temp directory helpers, filesystem metadata, recursive walk, and streaming
      glob traversal. Platform-backed temp roots and cross-platform
      Linux/macOS/Windows metadata support have landed; cross-platform walk
      parity remains.
- [~] Process spawn with stdout/stderr capture: Linux-backed `System.Process.RunCapture` has landed; cross-platform backend parity remains.
- [~] Environment and argv APIs: Linux-backed `System.Process` environment and argv APIs have landed; cross-platform backend parity remains.

## Memory Model Work

- [x] Decide compiler IR storage strategy: arena/table ownership with typed
      handle indices, first-class extensible fact tables, explicit lowering
      policies, package-image durable facts, and phase-boundary validation. See
      [24-ir-memory-and-fact-model.md](24-ir-memory-and-fact-model.md).
- [ ] Define typed compiler handles for MIR, SSA, types, symbols, packages,
      artifacts, and backend state. Handles are distinct types, not
      interchangeable integers.
- [ ] Define compiler fact categories for values, functions, blocks, types,
      symbols, packages, diagnostics, alias proofs, ABI, layout, alignment,
      integer ranges, ownership/drop, and future backend facts.
- [ ] Add fact-transfer helpers and validation so lowering policies preserve,
      translate, consume, recompute, or intentionally drop facts according to the
      declared rule.
- [ ] Preserve durable compiler facts through package images; keep transient
      pass-local facts in compiler IR tables unless a package section explicitly
      needs a stable summary.
- [ ] Keep `arena` explicit if it becomes an executable storage class. The
      compiler IR decision does not require source-level `arena` before
      self-hosting if library/compiler-owned tables satisfy the model.
- [ ] Keep unsafe shared ownership visible in source. `Rc`/`Arc` is not the
      default compiler IR model for self-hosting.
