# Comptime Structural Facts Reference

Compiler-known structural facts live under `System.Compiler`. They are
compile-time-only, must be used inside `comptime`, and erase before MIR/codegen.
Runtime use is a compile-time diagnostic.

## Predicate Facts

Return `bool`:

- `IsBool<T>()`
- `IsInteger<T>()`
- `IsFloat<T>()`
- `IsRawPointer<T>()`
- `IsFixedArray<T>()`
- `IsSlice<T>()`
- `IsDynamic<T>()`
- `IsFunctionPointer<T>()`
- `IsClosure<T>()`
- `IsNamed<T>()`
- `IsStruct<T>()`
- `IsRecord<T>()`
- `IsEnum<T>()`
- `IsTrait<T>()`
- `IsDoctrine<T>()`
- `IsDynTrait<T>()`
- `HasConcreteLayout<T>()`

## Type Layout Facts

- `TypeSize<T>() -> u64[0 max]`
- `TypeAlign<T>() -> u64[0 max]`
- `TypeIsZeroSized<T>() -> bool`

These facts require `T` to have concrete runtime layout. Use
`HasConcreteLayout<T>()` first when branching over types that may be traits,
doctrines, unresolved symbolic associated types, or other non-layout-bearing
forms. Runtime use and non-concrete targets are compile-time diagnostics.

## Scalar Type Metadata Facts

- `TypeIntegerBitWidth<T>() -> u64[0 max]`
- `TypeFloatBitWidth<T>() -> u64[0 max]`
- `TypeIntegerIsSigned<T>() -> bool`
- `TypeIntegerIsUnsigned<T>() -> bool`
- `TypeIntegerIsFullRange<T>() -> bool`
- `TypeIntegerMinIs<T, Value>() -> bool`
- `TypeIntegerMaxIs<T, Value>() -> bool`

`TypeIntegerBitWidth<T>()` requires `T` to be a sized integer type.
`TypeFloatBitWidth<T>()` requires `T` to be a sized float type. The boolean
integer facts return `false` for non-integer targets, so generic code can ask
them before choosing an integer-only branch.

`TypeIntegerIsFullRange<T>()` compares the effective integer range with the
underlying storage range. `TypeIntegerMinIs` and `TypeIntegerMaxIs` compare the
effective integer range bounds against a signed `i1024` compile-time value
argument. For full-width `u1024`, use `TypeIntegerIsUnsigned<T>()` plus
`TypeIntegerIsFullRange<T>()` instead of comparing the maximum value directly.

## Raw-Pointer Metadata Facts

- `RawPointerElementTypeIs<T, U>() -> bool`
- `RawPointerElementTypeIsBool<T>() -> bool`
- `RawPointerElementTypeIsInteger<T>() -> bool`
- `RawPointerElementTypeIsFloat<T>() -> bool`
- `RawPointerElementTypeIsRawPointer<T>() -> bool`
- `RawPointerElementTypeIsFixedArray<T>() -> bool`
- `RawPointerElementTypeIsSlice<T>() -> bool`
- `RawPointerElementTypeIsDynamic<T>() -> bool`
- `RawPointerElementTypeIsFunctionPointer<T>() -> bool`
- `RawPointerElementTypeIsClosure<T>() -> bool`
- `RawPointerElementTypeIsDynTrait<T>() -> bool`
- `RawPointerElementTypeIsNamed<T>() -> bool`
- `RawPointerElementTypeIsStruct<T>() -> bool`
- `RawPointerElementTypeIsRecord<T>() -> bool`
- `RawPointerElementTypeIsEnum<T>() -> bool`
- `RawPointerElementTypeIsTrait<T>() -> bool`
- `RawPointerElementTypeIsDoctrine<T>() -> bool`
- `RawPointerElementTypeHasConcreteLayout<T>() -> bool`
- `RawPointerIsMutable<T>() -> bool`
- `RawPointerIsReadOnly<T>() -> bool`

Raw-pointer metadata facts require `T` to be a raw pointer type. Element facts
inspect the pointee after type normalization, so aliases and package-loaded
typed aliases work the same as direct `rawptr<...>` / `rawmutptr<...>` forms.
`RawPointerIsMutable` matches `rawmutptr<...>` and `RawPointerIsReadOnly`
matches `rawptr<...>`. Wrong-target use and runtime use are compile-time
diagnostics.

## Element-Bearing Type Metadata Facts

- `TypeElementTypeIs<T, U>() -> bool`
- `TypeElementTypeIsBool<T>() -> bool`
- `TypeElementTypeIsInteger<T>() -> bool`
- `TypeElementTypeIsFloat<T>() -> bool`
- `TypeElementTypeIsRawPointer<T>() -> bool`
- `TypeElementTypeIsFixedArray<T>() -> bool`
- `TypeElementTypeIsSlice<T>() -> bool`
- `TypeElementTypeIsDynamic<T>() -> bool`
- `TypeElementTypeIsFunctionPointer<T>() -> bool`
- `TypeElementTypeIsClosure<T>() -> bool`
- `TypeElementTypeIsDynTrait<T>() -> bool`
- `TypeElementTypeIsNamed<T>() -> bool`
- `TypeElementTypeIsStruct<T>() -> bool`
- `TypeElementTypeIsRecord<T>() -> bool`
- `TypeElementTypeIsEnum<T>() -> bool`
- `TypeElementTypeIsTrait<T>() -> bool`
- `TypeElementTypeIsDoctrine<T>() -> bool`
- `TypeElementTypeHasConcreteLayout<T>() -> bool`
- `TypeFixedArrayLength<T>() -> u64[0 max]`
- `TypeFixedArrayLengthIs<T, Value>() -> bool`

Element facts require `T` to be element-bearing: `rawptr<...>`,
`rawmutptr<...>`, fixed array, slice, or `dynamic`. They inspect the element
type after alias and generic normalization. Fixed-array length facts require
`T` to be a fixed array; `TypeFixedArrayLength<T>()` returns the concrete
length and `TypeFixedArrayLengthIs<T, Value>()` compares it with a typed
compile-time integer value. Generic fixed-array lengths such as `T[N]` defer
while the generic body is open, then fold after range-typed integer `comptime`
generic substitution. Wrong-target use and runtime use are compile-time
diagnostics.

## Top-Level Qualifier Metadata Facts

- `TypeHasQualifiers<T>() -> bool`
- `TypeBorrowKindIsNone<T>() -> bool`
- `TypeBorrowKindIsBorrow<T>() -> bool`
- `TypeBorrowKindIsRetBorrow<T>() -> bool`
- `TypeBorrowKindIsStoreBorrow<T>() -> bool`
- `TypeAccessKindIsNone<T>() -> bool`
- `TypeAccessKindIsShared<T>() -> bool`
- `TypeAccessKindIsFrozen<T>() -> bool`
- `TypeInitializationKindIsNone<T>() -> bool`
- `TypeInitializationKindIsOut<T>() -> bool`
- `TypeInitializationKindIsInit<T>() -> bool`
- `TypeIsMutableView<T>() -> bool`
- `TypeUnqualifiedTypeIs<T, U>() -> bool`

These facts inspect only the top-level qualifier metadata on `T`: borrow kind
(`borrow`, `retborrow`, `storeborrow`), access kind (`shared`, `frozen`),
initialization kind (`out`, `init`), and mutable-view state. Other type
predicate, layout, element, callable, and exact-type facts normalize top-level
qualifiers away so they can reason about storage shape. `TypeUnqualifiedTypeIs`
compares both operands after stripping those top-level qualifiers.

They are useful for generic ownership/borrowing helpers in the compiler port
where `borrow mut T`, `retborrow T`, `out T`, or `frozen T` must be handled
intentionally instead of hidden behind storage-type equality. Runtime use is a
compile-time diagnostic. Package-backed typed aliases preserve the qualifier
metadata.

## Count Facts

Return `u64[0 max]`:

- `FieldCount<T>()`
- `EnumVariantCount<T>()`
- `EnumVariantPayloadCount<T, I>()`
- `FunctionPointerParameterCount<T>()`
- `ClosureParameterCount<T>()`
- `MethodCount<T>()`

`I`, `MethodIndex`, and `ParameterIndex` are typed comptime integer generic
arguments.

## Function-Pointer Facts

- `FunctionPointerReturnTypeIs<T, U>() -> bool`
- `FunctionPointerParameterTypeIs<T, U, I>() -> bool`
- `FunctionPointerReturnTypeIsBool<T>() -> bool`
- `FunctionPointerReturnTypeIsInteger<T>() -> bool`
- `FunctionPointerReturnTypeIsFloat<T>() -> bool`
- `FunctionPointerReturnTypeIsRawPointer<T>() -> bool`
- `FunctionPointerReturnTypeIsFixedArray<T>() -> bool`
- `FunctionPointerReturnTypeIsSlice<T>() -> bool`
- `FunctionPointerReturnTypeIsDynamic<T>() -> bool`
- `FunctionPointerReturnTypeIsFunctionPointer<T>() -> bool`
- `FunctionPointerReturnTypeIsClosure<T>() -> bool`
- `FunctionPointerReturnTypeIsDynTrait<T>() -> bool`
- `FunctionPointerReturnTypeIsNamed<T>() -> bool`
- `FunctionPointerReturnTypeIsStruct<T>() -> bool`
- `FunctionPointerReturnTypeIsRecord<T>() -> bool`
- `FunctionPointerReturnTypeIsEnum<T>() -> bool`
- `FunctionPointerReturnTypeIsTrait<T>() -> bool`
- `FunctionPointerReturnTypeIsDoctrine<T>() -> bool`
- `FunctionPointerReturnTypeHasConcreteLayout<T>() -> bool`
- `FunctionPointerReturnTypeDisplayName<T>() -> ascii`
- `FunctionPointerReturnTypeBaseName<T>() -> ascii`
- `FunctionPointerReturnTypeModuleName<T>() -> ascii`
- `FunctionPointerReturnTypeIsGenericInstantiation<T>() -> bool`
- `FunctionPointerReturnTypeArgumentCount<T>() -> u64[0 max]`
- `FunctionPointerReturnTypeComptimeArgumentCount<T>() -> u64[0 max]`
- `FunctionPointerReturnTypeArgumentTypeIs<T, U, ArgumentIndex>() -> bool`
- `FunctionPointerReturnTypeArgumentTypeIsDynTrait<T, ArgumentIndex>() -> bool`
- `FunctionPointerParameterTypeIsBool<T, I>() -> bool`
- `FunctionPointerParameterTypeIsInteger<T, I>() -> bool`
- `FunctionPointerParameterTypeIsFloat<T, I>() -> bool`
- `FunctionPointerParameterTypeIsRawPointer<T, I>() -> bool`
- `FunctionPointerParameterTypeIsFixedArray<T, I>() -> bool`
- `FunctionPointerParameterTypeIsSlice<T, I>() -> bool`
- `FunctionPointerParameterTypeIsDynamic<T, I>() -> bool`
- `FunctionPointerParameterTypeIsFunctionPointer<T, I>() -> bool`
- `FunctionPointerParameterTypeIsClosure<T, I>() -> bool`
- `FunctionPointerParameterTypeIsDynTrait<T, I>() -> bool`
- `FunctionPointerParameterTypeIsNamed<T, I>() -> bool`
- `FunctionPointerParameterTypeIsStruct<T, I>() -> bool`
- `FunctionPointerParameterTypeIsRecord<T, I>() -> bool`
- `FunctionPointerParameterTypeIsEnum<T, I>() -> bool`
- `FunctionPointerParameterTypeIsTrait<T, I>() -> bool`
- `FunctionPointerParameterTypeIsDoctrine<T, I>() -> bool`
- `FunctionPointerParameterTypeHasConcreteLayout<T, I>() -> bool`
- `FunctionPointerParameterTypeDisplayName<T, I>() -> ascii`
- `FunctionPointerParameterTypeBaseName<T, I>() -> ascii`
- `FunctionPointerParameterTypeModuleName<T, I>() -> ascii`
- `FunctionPointerParameterTypeIsGenericInstantiation<T, I>() -> bool`
- `FunctionPointerParameterTypeArgumentCount<T, I>() -> u64[0 max]`
- `FunctionPointerParameterTypeComptimeArgumentCount<T, I>() -> u64[0 max]`
- `FunctionPointerParameterTypeArgumentTypeIs<T, U, ParameterIndex, ArgumentIndex>() -> bool`
- `FunctionPointerParameterTypeArgumentTypeIsDynTrait<T, ParameterIndex, ArgumentIndex>() -> bool`
- Return-type qualifier facts use the `FunctionPointerReturnType...<T>()`
  prefix; parameter qualifier facts use the
  `FunctionPointerParameterType...<T, I>()` prefix. Supported suffixes are
  `HasQualifiers`, `BorrowKindIsNone`, `BorrowKindIsBorrow`,
  `BorrowKindIsRetBorrow`, `BorrowKindIsStoreBorrow`, `AccessKindIsNone`,
  `AccessKindIsShared`, `AccessKindIsFrozen`, `InitializationKindIsNone`,
  `InitializationKindIsOut`, `InitializationKindIsInit`, `IsMutableView`, and
  `UnqualifiedTypeIs` (with comparison type `U`).
- `FunctionPointerParameterHasRawPointerElementCountExpression<T, I>() -> bool`
- `FunctionPointerParameterRawPointerElementCountExpression<T, I>() -> ascii`
- `FunctionPointerKindIsFn<T>() -> bool`
- `FunctionPointerKindIsFinite<T>() -> bool`
- `FunctionPointerKindIsLaw<T>() -> bool`
- `FunctionPointerKindIsFiniteLaw<T>() -> bool`
- `FunctionPointerIsUnsafe<T>() -> bool`
- `FunctionPointerHasFfiAbi<T>() -> bool`
- `FunctionPointerAbiIsC<T>() -> bool`
- `FunctionPointerAbiIsCDecl<T>() -> bool`
- `FunctionPointerAbiIsStdCall<T>() -> bool`
- `FunctionPointerAbiIsFastCall<T>() -> bool`
- `FunctionPointerAbiIsThisCall<T>() -> bool`
- `FunctionPointerAbiIsVectorCall<T>() -> bool`
- `FunctionPointerAbiIsSysV<T>() -> bool`
- `FunctionPointerAbiIsWin64<T>() -> bool`
- `FunctionPointerAbiIsAapcs<T>() -> bool`
- `FunctionPointerAbiIsAapcs64<T>() -> bool`
- `FunctionPointerParametersAreDisjoint<T, LeftIndex, RightIndex>() -> bool`
- `FunctionPointerParametersOverlap<T, LeftIndex, RightIndex>() -> bool`
- `FunctionPointerParametersAreSame<T, LeftIndex, RightIndex>() -> bool`

Function-pointer facts inspect `fnptr<...>` type metadata. `FunctionPointerIsUnsafe`
is true only for `fnptr<unsafe ...>` signatures; ABI facts stay independent of the
safety bit. Return/parameter
category facts mirror the top-level `Is*` / `HasConcreteLayout` predicates.
Nested type argument facts inspect ordinary type arguments on the callable return
or parameter type, so a `fnptr<fn Box<heap dyn Trait>(Box<heap dyn Trait>)>`
can branch on the dyn trait inside `Box`. Nested type metadata facts mirror the
field/method/enum payload metadata facts:
display name, unqualified base name for named generic types, declaration module
name for named types, generic-instantiation status, runtime type-argument count,
and `comptime` value-argument count.
Qualifier metadata facts inspect the selected nested return or parameter type
before qualifier normalization, so `retborrow`, `borrow`, `storeborrow`,
`shared`, `frozen`, `out`, `init`, and mutable-view information remains visible
to CTFE.
Concrete out-of-range parameter indices are compile-time errors. Count, kind,
ABI, nested type metadata, raw-pointer count-expression facts, and parameter
memory-contract predicates fold directly from the type;
ordinary Stark `fnptr<fn ...>` has no FFI ABI, so
`FunctionPointerHasFfiAbi<T>()` is false. `same` is treated as a stronger form
of overlap for `FunctionPointerParametersOverlap`.
Bounded raw-pointer count expressions preserve the source expression from the
callable type, using synthetic names such as `arg1`; the expression fact returns
empty `ascii` when the selected parameter has no bounded raw-pointer count.

## Closure Facts

- `ClosureReturnTypeIs<T, U>() -> bool`
- `ClosureParameterTypeIs<T, U, I>() -> bool`
- `ClosureReturnTypeIsBool<T>() -> bool`
- `ClosureReturnTypeIsInteger<T>() -> bool`
- `ClosureReturnTypeIsFloat<T>() -> bool`
- `ClosureReturnTypeIsRawPointer<T>() -> bool`
- `ClosureReturnTypeIsFixedArray<T>() -> bool`
- `ClosureReturnTypeIsSlice<T>() -> bool`
- `ClosureReturnTypeIsDynamic<T>() -> bool`
- `ClosureReturnTypeIsFunctionPointer<T>() -> bool`
- `ClosureReturnTypeIsClosure<T>() -> bool`
- `ClosureReturnTypeIsDynTrait<T>() -> bool`
- `ClosureReturnTypeIsNamed<T>() -> bool`
- `ClosureReturnTypeIsStruct<T>() -> bool`
- `ClosureReturnTypeIsRecord<T>() -> bool`
- `ClosureReturnTypeIsEnum<T>() -> bool`
- `ClosureReturnTypeIsTrait<T>() -> bool`
- `ClosureReturnTypeIsDoctrine<T>() -> bool`
- `ClosureReturnTypeHasConcreteLayout<T>() -> bool`
- `ClosureReturnTypeDisplayName<T>() -> ascii`
- `ClosureReturnTypeBaseName<T>() -> ascii`
- `ClosureReturnTypeModuleName<T>() -> ascii`
- `ClosureReturnTypeIsGenericInstantiation<T>() -> bool`
- `ClosureReturnTypeArgumentCount<T>() -> u64[0 max]`
- `ClosureReturnTypeComptimeArgumentCount<T>() -> u64[0 max]`
- `ClosureReturnTypeArgumentTypeIs<T, U, ArgumentIndex>() -> bool`
- `ClosureReturnTypeArgumentTypeIsDynTrait<T, ArgumentIndex>() -> bool`
- `ClosureReturnTypeHasCSourceAlias<T>() -> bool`
- `ClosureReturnTypeCSourceAliasName<T>() -> ascii`
- `ClosureParameterTypeIsBool<T, I>() -> bool`
- `ClosureParameterTypeIsInteger<T, I>() -> bool`
- `ClosureParameterTypeIsFloat<T, I>() -> bool`
- `ClosureParameterTypeIsRawPointer<T, I>() -> bool`
- `ClosureParameterTypeIsFixedArray<T, I>() -> bool`
- `ClosureParameterTypeIsSlice<T, I>() -> bool`
- `ClosureParameterTypeIsDynamic<T, I>() -> bool`
- `ClosureParameterTypeIsFunctionPointer<T, I>() -> bool`
- `ClosureParameterTypeIsClosure<T, I>() -> bool`
- `ClosureParameterTypeIsDynTrait<T, I>() -> bool`
- `ClosureParameterTypeIsNamed<T, I>() -> bool`
- `ClosureParameterTypeIsStruct<T, I>() -> bool`
- `ClosureParameterTypeIsRecord<T, I>() -> bool`
- `ClosureParameterTypeIsEnum<T, I>() -> bool`
- `ClosureParameterTypeIsTrait<T, I>() -> bool`
- `ClosureParameterTypeIsDoctrine<T, I>() -> bool`
- `ClosureParameterTypeHasConcreteLayout<T, I>() -> bool`
- `ClosureParameterTypeDisplayName<T, I>() -> ascii`
- `ClosureParameterTypeBaseName<T, I>() -> ascii`
- `ClosureParameterTypeModuleName<T, I>() -> ascii`
- `ClosureParameterTypeIsGenericInstantiation<T, I>() -> bool`
- `ClosureParameterTypeArgumentCount<T, I>() -> u64[0 max]`
- `ClosureParameterTypeComptimeArgumentCount<T, I>() -> u64[0 max]`
- `ClosureParameterTypeArgumentTypeIs<T, U, ParameterIndex, ArgumentIndex>() -> bool`
- `ClosureParameterTypeArgumentTypeIsDynTrait<T, ParameterIndex, ArgumentIndex>() -> bool`
- `ClosureParameterTypeHasCSourceAlias<T, I>() -> bool`
- `ClosureParameterTypeCSourceAliasName<T, I>() -> ascii`
- Return-type qualifier facts use the `ClosureReturnType...<T>()` prefix;
  parameter qualifier facts use the `ClosureParameterType...<T, I>()` prefix.
  Supported suffixes match function-pointer nested qualifier facts:
  `HasQualifiers`, the `BorrowKindIs...`, `AccessKindIs...`, and
  `InitializationKindIs...` predicates, `IsMutableView`, and
  `UnqualifiedTypeIs` (with comparison type `U`).
- `ClosureParameterHasRawPointerElementCountExpression<T, I>() -> bool`
- `ClosureParameterRawPointerElementCountExpression<T, I>() -> ascii`
- `ClosureKindIsFn<T>() -> bool`
- `ClosureKindIsFinite<T>() -> bool`
- `ClosureKindIsLaw<T>() -> bool`
- `ClosureKindIsFiniteLaw<T>() -> bool`
- `ClosureStorageIsBorrow<T>() -> bool`
- `ClosureStorageIsHeap<T>() -> bool`
- `ClosureStorageIsInline<T>() -> bool`
- `ClosureCallCapabilityIsNormal<T>() -> bool`
- `ClosureCallCapabilityIsMut<T>() -> bool`
- `ClosureCallCapabilityIsOnce<T>() -> bool`
- `ClosureParametersAreDisjoint<T, LeftIndex, RightIndex>() -> bool`
- `ClosureParametersOverlap<T, LeftIndex, RightIndex>() -> bool`
- `ClosureParametersAreSame<T, LeftIndex, RightIndex>() -> bool`

Closure facts inspect `closure<...>` type metadata. `ClosureStorageIsBorrow`
matches the ordinary borrowed closure form after qualifiers are normalized;
`ClosureStorageIsHeap` and `ClosureStorageIsInline` match explicit `heap` and
`inline` closure forms. `ClosureCallCapabilityIsNormal` matches no call
capability marker, while `ClosureCallCapabilityIsMut` and
`ClosureCallCapabilityIsOnce` match `mut` and `once`. Return/parameter category
facts mirror the top-level `Is*` / `HasConcreteLayout` predicates, and nested
type argument facts mirror the function-pointer argument surface. Nested type
metadata facts mirror the function-pointer metadata surface, including C source
alias identity for ABI-facing return and parameter types. Alias-name
facts return empty `ascii` when the selected nested type has no C source alias.
Nested qualifier facts inspect the selected return or parameter type before
qualifier normalization, matching the function-pointer qualifier semantics.
Concrete out-of-range parameter indices and wrong-target uses are compile-time errors.
`same` is treated as a stronger form of overlap for
`ClosureParametersOverlap`.
Bounded raw-pointer count expressions preserve the source expression from the
closure type, using synthetic names such as `arg1`; the expression fact returns
empty `ascii` when absent.

## Method Facts

- `MethodCount<T>() -> u64[0 max]`
- `MethodName<T, MethodIndex>() -> ascii`
- `MethodModuleName<T, MethodIndex>() -> ascii`
- `MethodVisibilityIsModule<T, MethodIndex>() -> bool`
- `MethodVisibilityIsInternal<T, MethodIndex>() -> bool`
- `MethodVisibilityIsPublic<T, MethodIndex>() -> bool`
- `MethodVisibilityIsExport<T, MethodIndex>() -> bool`
- `MethodParameterCount<T, MethodIndex>() -> u64[0 max]`
- `MethodParameterName<T, MethodIndex, ParameterIndex>() -> ascii`
- `MethodReturnTypeIs<T, U, MethodIndex>() -> bool`
- `MethodParameterTypeIs<T, U, MethodIndex, ParameterIndex>() -> bool`
- `MethodReturnTypeDisplayName<T, MethodIndex>() -> ascii`
- `MethodReturnTypeBaseName<T, MethodIndex>() -> ascii`
- `MethodReturnTypeModuleName<T, MethodIndex>() -> ascii`
- `MethodReturnTypeIsGenericInstantiation<T, MethodIndex>() -> bool`
- `MethodReturnTypeArgumentCount<T, MethodIndex>() -> u64[0 max]`
- `MethodReturnTypeComptimeArgumentCount<T, MethodIndex>() -> u64[0 max]`
- `MethodReturnTypeArgumentTypeIs<T, U, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsBool<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsInteger<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsFloat<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsRawPointer<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsFixedArray<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsSlice<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsDynamic<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsFunctionPointer<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsClosure<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsDynTrait<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsNamed<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsStruct<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsRecord<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsEnum<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsTrait<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeIsDoctrine<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeHasConcreteLayout<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeDisplayName<T, MethodIndex, ArgumentIndex>() -> ascii`
- `MethodReturnTypeArgumentTypeBaseName<T, MethodIndex, ArgumentIndex>() -> ascii`
- `MethodReturnTypeArgumentTypeModuleName<T, MethodIndex, ArgumentIndex>() -> ascii`
- `MethodReturnTypeArgumentTypeIsGenericInstantiation<T, MethodIndex, ArgumentIndex>() -> bool`
- `MethodReturnTypeArgumentTypeArgumentCount<T, MethodIndex, ArgumentIndex>() -> u64[0 max]`
- `MethodReturnTypeArgumentTypeComptimeArgumentCount<T, MethodIndex, ArgumentIndex>() -> u64[0 max]`
- `MethodParameterTypeDisplayName<T, MethodIndex, ParameterIndex>() -> ascii`
- `MethodParameterTypeBaseName<T, MethodIndex, ParameterIndex>() -> ascii`
- `MethodParameterTypeModuleName<T, MethodIndex, ParameterIndex>() -> ascii`
- `MethodParameterTypeIsGenericInstantiation<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeArgumentCount<T, MethodIndex, ParameterIndex>() -> u64[0 max]`
- `MethodParameterTypeComptimeArgumentCount<T, MethodIndex, ParameterIndex>() -> u64[0 max]`
- `MethodParameterTypeArgumentTypeIs<T, U, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsBool<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsInteger<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsFloat<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsRawPointer<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsFixedArray<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsSlice<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsDynamic<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsFunctionPointer<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsClosure<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsDynTrait<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsNamed<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsStruct<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsRecord<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsEnum<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsTrait<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeIsDoctrine<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeHasConcreteLayout<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeDisplayName<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> ascii`
- `MethodParameterTypeArgumentTypeBaseName<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> ascii`
- `MethodParameterTypeArgumentTypeModuleName<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> ascii`
- `MethodParameterTypeArgumentTypeIsGenericInstantiation<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> bool`
- `MethodParameterTypeArgumentTypeArgumentCount<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> u64[0 max]`
- `MethodParameterTypeArgumentTypeComptimeArgumentCount<T, MethodIndex, ParameterIndex, ArgumentIndex>() -> u64[0 max]`
- Return-type qualifier facts use the `MethodReturnType...<T, MethodIndex>()`
  prefix; parameter qualifier facts use the
  `MethodParameterType...<T, MethodIndex, ParameterIndex>()` prefix. Supported
  suffixes match callable nested qualifier facts: `HasQualifiers`, the
  `BorrowKindIs...`, `AccessKindIs...`, and `InitializationKindIs...`
  predicates, `IsMutableView`, and `UnqualifiedTypeIs` (with comparison type
  `U`).
- `MethodParameterHasRawPointerElementCountExpression<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterRawPointerElementCountExpression<T, MethodIndex, ParameterIndex>() -> ascii`
- `MethodReturnTypeIsBool<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsInteger<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsFloat<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsRawPointer<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsFixedArray<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsSlice<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsDynamic<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsFunctionPointer<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsClosure<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsDynTrait<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsNamed<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsStruct<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsRecord<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsEnum<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsTrait<T, MethodIndex>() -> bool`
- `MethodReturnTypeIsDoctrine<T, MethodIndex>() -> bool`
- `MethodReturnTypeHasConcreteLayout<T, MethodIndex>() -> bool`
- `MethodParameterTypeIsBool<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsInteger<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsFloat<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsRawPointer<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsFixedArray<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsSlice<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsDynamic<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsFunctionPointer<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsClosure<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsDynTrait<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsNamed<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsStruct<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsRecord<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsEnum<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsTrait<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeIsDoctrine<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodParameterTypeHasConcreteLayout<T, MethodIndex, ParameterIndex>() -> bool`
- `MethodKindIsFn<T, MethodIndex>() -> bool`
- `MethodKindIsFinite<T, MethodIndex>() -> bool`
- `MethodKindIsLaw<T, MethodIndex>() -> bool`
- `MethodKindIsFiniteLaw<T, MethodIndex>() -> bool`
- `MethodIsStatic<T, MethodIndex>() -> bool`
- `MethodHasBody<T, MethodIndex>() -> bool`
- `MethodIsUnsafe<T, MethodIndex>() -> bool`
- `MethodIsVarargs<T, MethodIndex>() -> bool`
- `MethodHasFfiAbi<T, MethodIndex>() -> bool`
- `MethodAbiIsC<T, MethodIndex>() -> bool`
- `MethodAbiIsCDecl<T, MethodIndex>() -> bool`
- `MethodAbiIsStdCall<T, MethodIndex>() -> bool`
- `MethodAbiIsFastCall<T, MethodIndex>() -> bool`
- `MethodAbiIsThisCall<T, MethodIndex>() -> bool`
- `MethodAbiIsVectorCall<T, MethodIndex>() -> bool`
- `MethodAbiIsSysV<T, MethodIndex>() -> bool`
- `MethodAbiIsWin64<T, MethodIndex>() -> bool`
- `MethodAbiIsAapcs<T, MethodIndex>() -> bool`
- `MethodAbiIsAapcs64<T, MethodIndex>() -> bool`
- `MethodParametersAreDisjoint<T, MethodIndex, LeftIndex, RightIndex>() -> bool`
- `MethodParametersOverlap<T, MethodIndex, LeftIndex, RightIndex>() -> bool`
- `MethodParametersAreSame<T, MethodIndex, LeftIndex, RightIndex>() -> bool`
- `MethodGenericParameterCount<T, MethodIndex>() -> u64[0 max]`
- `MethodGenericParameterName<T, MethodIndex, GenericParameterIndex>() -> ascii`
- `MethodGenericParameterTraitBoundCount<T, MethodIndex, GenericParameterIndex>() -> u64[0 max]`
- `MethodGenericParameterTraitBoundTypeIs<T, U, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsBool<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsInteger<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsFloat<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsRawPointer<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsFixedArray<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsSlice<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsDynamic<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsFunctionPointer<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsClosure<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsDynTrait<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsNamed<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsStruct<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsRecord<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsEnum<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsTrait<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeIsDoctrine<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeHasConcreteLayout<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeDisplayName<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> ascii`
- `MethodGenericParameterTraitBoundTypeBaseName<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> ascii`
- `MethodGenericParameterTraitBoundTypeModuleName<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> ascii`
- `MethodGenericParameterTraitBoundTypeIsGenericInstantiation<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> bool`
- `MethodGenericParameterTraitBoundTypeArgumentCount<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> u64[0 max]`
- `MethodGenericParameterTraitBoundTypeComptimeArgumentCount<T, MethodIndex, GenericParameterIndex, BoundIndex>() -> u64[0 max]`
- `MethodComptimeGenericParameterCount<T, MethodIndex>() -> u64[0 max]`
- `MethodComptimeGenericParameterName<T, MethodIndex, GenericParameterIndex>() -> ascii`
- `MethodComptimeGenericParameterTypeIs<T, U, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsBool<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsInteger<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsFloat<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsRawPointer<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsFixedArray<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsSlice<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsDynamic<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsFunctionPointer<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsClosure<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsDynTrait<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsNamed<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsStruct<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsRecord<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsEnum<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsTrait<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeIsDoctrine<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeHasConcreteLayout<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeDisplayName<T, MethodIndex, GenericParameterIndex>() -> ascii`
- `MethodComptimeGenericParameterTypeBaseName<T, MethodIndex, GenericParameterIndex>() -> ascii`
- `MethodComptimeGenericParameterTypeModuleName<T, MethodIndex, GenericParameterIndex>() -> ascii`
- `MethodComptimeGenericParameterTypeIsGenericInstantiation<T, MethodIndex, GenericParameterIndex>() -> bool`
- `MethodComptimeGenericParameterTypeArgumentCount<T, MethodIndex, GenericParameterIndex>() -> u64[0 max]`
- `MethodComptimeGenericParameterTypeComptimeArgumentCount<T, MethodIndex, GenericParameterIndex>() -> u64[0 max]`
- `MethodThreadSafetyLawPredicateCount<T, MethodIndex>() -> u64[0 max]`
- `MethodThreadSafetyLawPredicateLawName<T, MethodIndex, PredicateIndex>() -> ascii`
- `MethodThreadSafetyLawPredicateTypeIs<T, U, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsBool<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsInteger<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsFloat<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsRawPointer<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsFixedArray<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsSlice<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsDynamic<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsFunctionPointer<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsClosure<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsDynTrait<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsNamed<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsStruct<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsRecord<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsEnum<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsTrait<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeIsDoctrine<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeHasConcreteLayout<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeDisplayName<T, MethodIndex, PredicateIndex>() -> ascii`
- `MethodThreadSafetyLawPredicateTypeBaseName<T, MethodIndex, PredicateIndex>() -> ascii`
- `MethodThreadSafetyLawPredicateTypeModuleName<T, MethodIndex, PredicateIndex>() -> ascii`
- `MethodThreadSafetyLawPredicateTypeIsGenericInstantiation<T, MethodIndex, PredicateIndex>() -> bool`
- `MethodThreadSafetyLawPredicateTypeArgumentCount<T, MethodIndex, PredicateIndex>() -> u64[0 max]`
- `MethodThreadSafetyLawPredicateTypeComptimeArgumentCount<T, MethodIndex, PredicateIndex>() -> u64[0 max]`

Each overload signature is one deterministic method slot. Slots are ordered by
member name, parameter signature, return type, source name, and resolved symbol
name. For generic owner types, return/parameter/comptime-parameter type facts
and `where` law predicate type facts substitute the owner type arguments before
comparing, checking type categories/concrete layout, or reporting display/base
names, declaration module names, and type/comptime argument counts.
Method return/parameter actual type-argument facts inspect ordinary type
arguments on the substituted method return or parameter type, so a method
returning `Box<heap dyn Trait>` can branch on the dyn trait inside `Box`.
Return/parameter qualifier facts
inspect the substituted nested type before qualifier normalization.
Method visibility facts expose the selected method's declared or inherited
visibility and use the same method index validation as `MethodName`.
Concrete out-of-range method, parameter, generic-parameter, and law-predicate
indices are compile-time errors. Package-backed typed interfaces preserve the
method metadata needed by these facts.
Bounded raw-pointer parameter count-expression facts preserve the method source
expression, usually another parameter name such as `length`; the expression
fact returns empty `ascii` when the selected method parameter has no bounded
raw-pointer count.

## Thread-Safety Law Attribute Facts

Type-level law attribute facts:

- `TypeThreadSafetyLawAttributeCount<T>() -> u64[0 max]`
- `TypeThreadSafetyLawAttributeLawName<T, AttributeIndex>() -> ascii`
- `TypeThreadSafetyLawAttributeIsGrant<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeIsDeny<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeHasCondition<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionLawName<T, AttributeIndex>() -> ascii`
- `TypeThreadSafetyLawAttributeConditionTypeIs<T, U, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsBool<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsInteger<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsFloat<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsRawPointer<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsFixedArray<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsSlice<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsDynamic<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsFunctionPointer<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsClosure<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsDynTrait<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsNamed<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsStruct<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsRecord<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsEnum<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsTrait<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeIsDoctrine<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeHasConcreteLayout<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeDisplayName<T, AttributeIndex>() -> ascii`
- `TypeThreadSafetyLawAttributeConditionTypeBaseName<T, AttributeIndex>() -> ascii`
- `TypeThreadSafetyLawAttributeConditionTypeModuleName<T, AttributeIndex>() -> ascii`
- `TypeThreadSafetyLawAttributeConditionTypeIsGenericInstantiation<T, AttributeIndex>() -> bool`
- `TypeThreadSafetyLawAttributeConditionTypeArgumentCount<T, AttributeIndex>() -> u64[0 max]`
- `TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount<T, AttributeIndex>() -> u64[0 max]`

Field-level law attribute facts:

- `FieldThreadSafetyLawAttributeCount<T, FieldIndex>() -> u64[0 max]`
- `FieldThreadSafetyLawAttributeLawName<T, FieldIndex, AttributeIndex>() -> ascii`
- `FieldThreadSafetyLawAttributeIsGrant<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeIsDeny<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeHasCondition<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionLawName<T, FieldIndex, AttributeIndex>() -> ascii`
- `FieldThreadSafetyLawAttributeConditionTypeIs<T, U, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsBool<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsInteger<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsFloat<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsRawPointer<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsFixedArray<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsSlice<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsDynamic<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsFunctionPointer<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsClosure<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsDynTrait<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsNamed<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsStruct<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsRecord<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsEnum<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsTrait<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeIsDoctrine<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeHasConcreteLayout<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeDisplayName<T, FieldIndex, AttributeIndex>() -> ascii`
- `FieldThreadSafetyLawAttributeConditionTypeBaseName<T, FieldIndex, AttributeIndex>() -> ascii`
- `FieldThreadSafetyLawAttributeConditionTypeModuleName<T, FieldIndex, AttributeIndex>() -> ascii`
- `FieldThreadSafetyLawAttributeConditionTypeIsGenericInstantiation<T, FieldIndex, AttributeIndex>() -> bool`
- `FieldThreadSafetyLawAttributeConditionTypeArgumentCount<T, FieldIndex, AttributeIndex>() -> u64[0 max]`
- `FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount<T, FieldIndex, AttributeIndex>() -> u64[0 max]`

These facts inspect `[Grant(...)]` / `[Deny(...)]` declarations on named types
and struct/record fields. For generic owner types, condition type facts
substitute the owner type and `comptime` value arguments before comparing,
checking type categories/concrete layout, or reporting display/base names and
module names and type/comptime argument counts. Attributes without a `where`
condition report an
empty condition law/name, false condition-type predicates, and zero condition
type argument counts. Concrete out-of-range type, field, and law-attribute
indices are compile-time errors. Package-backed typed interfaces preserve the
attribute metadata needed by these facts.

## Dyn-Trait Facts

- `DynTraitIsView<T>() -> bool`
- `DynTraitIsHeap<T>() -> bool`
- `DynTraitTargetTypeIs<T, Trait>() -> bool`

Dyn-trait facts inspect `borrow dyn Trait` / `heap dyn Trait` type metadata.
`IsDynTrait<T>()` is the top-level predicate. `DynTraitIsView` and
`DynTraitIsHeap` return false for non-dyn types. `DynTraitTargetTypeIs`
requires its first type argument to be a dyn-trait object type and its second
type argument to resolve to a trait. Runtime use and invalid target arguments
are compile-time diagnostics. Package-backed typed interfaces preserve dyn
trait declarations, alias targets, and storage/target metadata.

## Indexed Field Facts

- `FieldOffset<T, I>() -> u64[0 max]`
- `FieldSize<T, I>() -> u64[0 max]`
- `FieldAlign<T, I>() -> u64[0 max]`
- `FieldIsMisaligned<T, I>() -> bool`
- `FieldTypeIs<T, U, I>() -> bool`
- `FieldTypeIsBool<T, I>() -> bool`
- `FieldTypeIsInteger<T, I>() -> bool`
- `FieldTypeIsFloat<T, I>() -> bool`
- `FieldTypeIsRawPointer<T, I>() -> bool`
- `FieldTypeIsFixedArray<T, I>() -> bool`
- `FieldTypeIsSlice<T, I>() -> bool`
- `FieldTypeIsDynamic<T, I>() -> bool`
- `FieldTypeIsFunctionPointer<T, I>() -> bool`
- `FieldTypeIsClosure<T, I>() -> bool`
- `FieldTypeIsDynTrait<T, I>() -> bool`
- `FieldTypeIsNamed<T, I>() -> bool`
- `FieldTypeIsStruct<T, I>() -> bool`
- `FieldTypeIsRecord<T, I>() -> bool`
- `FieldTypeIsEnum<T, I>() -> bool`
- `FieldTypeIsTrait<T, I>() -> bool`
- `FieldTypeIsDoctrine<T, I>() -> bool`
- `FieldTypeHasConcreteLayout<T, I>() -> bool`
- `FieldTypeDisplayName<T, I>() -> ascii`
- `FieldTypeBaseName<T, I>() -> ascii`
- `FieldTypeModuleName<T, I>() -> ascii`
- `FieldTypeIsGenericInstantiation<T, I>() -> bool`
- `FieldTypeArgumentCount<T, I>() -> u64[0 max]`
- `FieldTypeComptimeArgumentCount<T, I>() -> u64[0 max]`
- `FieldTypeHasQualifiers<T, I>() -> bool`
- `FieldTypeBorrowKindIsNone<T, I>() -> bool`
- `FieldTypeBorrowKindIsBorrow<T, I>() -> bool`
- `FieldTypeBorrowKindIsRetBorrow<T, I>() -> bool`
- `FieldTypeBorrowKindIsStoreBorrow<T, I>() -> bool`
- `FieldTypeAccessKindIsNone<T, I>() -> bool`
- `FieldTypeAccessKindIsShared<T, I>() -> bool`
- `FieldTypeAccessKindIsFrozen<T, I>() -> bool`
- `FieldTypeInitializationKindIsNone<T, I>() -> bool`
- `FieldTypeInitializationKindIsOut<T, I>() -> bool`
- `FieldTypeInitializationKindIsInit<T, I>() -> bool`
- `FieldTypeIsMutableView<T, I>() -> bool`
- `FieldTypeUnqualifiedTypeIs<T, U, I>() -> bool`
- `FieldName<T, I>() -> ascii`
- `FieldVisibilityIsModule<T, I>() -> bool`
- `FieldVisibilityIsInternal<T, I>() -> bool`
- `FieldVisibilityIsPublic<T, I>() -> bool`
- `FieldVisibilityIsExport<T, I>() -> bool`

Concrete out-of-range field indices are compile-time errors. Symbolic indices
may flow through generic CTFE until specialization.

Field type metadata facts inspect the selected field type after generic
substitution. `FieldTypeBaseName` and `FieldTypeModuleName` return an empty
`ascii` value for non-named field types. Generic-argument counts return `0` for
non-generic field types.
Qualifier metadata facts inspect the selected field type before qualifier
normalization and mirror the top-level `Type*` qualifier facts. Package-backed
typed interfaces preserve field qualifier metadata. Field visibility facts expose
the selected field's declared or inherited visibility and use the same field
index validation as `FieldName`.

## Layout-Control Attribute Facts

- `StructLayoutIsAuto<T>() -> bool`
- `StructLayoutIsC<T>() -> bool`
- `StructLayoutIsExplicit<T>() -> bool`
- `StructHasPack<T>() -> bool`
- `StructPack<T>() -> u64[0 max]`
- `StructHasAlign<T>() -> bool`
- `StructAlign<T>() -> u64[0 max]`
- `FieldHasExplicitOffset<T, I>() -> bool`
- `FieldExplicitOffset<T, I>() -> u64[0 max]`

`StructPack`, `StructAlign`, and `FieldExplicitOffset` return `0` when the
metadata is absent. Use the matching `Has*` fact when `0` is meaningful, such as
`[FieldOffset(0)]`.

Concrete out-of-range field indices are compile-time errors. Symbolic indices
may flow through generic CTFE until specialization. Package-backed typed
interfaces preserve the layout metadata needed by these facts.

## Indexed Enum Facts

- `EnumVariantIsOk<T, I>() -> bool`
- `EnumVariantIsErr<T, I>() -> bool`
- `EnumVariantIsErrorFunnel<T, I>() -> bool`
- `EnumVariantAbsorbsErrorTypeIs<T, U, I>() -> bool`
- `EnumVariantName<T, I>() -> ascii`
- `EnumVariantUsesNamedFields<T, I>() -> bool`
- `EnumVariantTag<T, I>() -> u64[0 max]`
- `EnumTagOffset<T>() -> u64[0 max]`
- `EnumTagSize<T>() -> u64[0 max]`
- `EnumTagAlign<T>() -> u64[0 max]`
- `EnumTagIsMisaligned<T>() -> bool`
- `EnumVariantPayloadOffset<T, VariantIndex, PayloadIndex>() -> u64[0 max]`
- `EnumVariantPayloadSize<T, VariantIndex, PayloadIndex>() -> u64[0 max]`
- `EnumVariantPayloadAlign<T, VariantIndex, PayloadIndex>() -> u64[0 max]`
- `EnumVariantPayloadIsMisaligned<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIs<T, U, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsBool<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsInteger<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsFloat<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsRawPointer<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsFixedArray<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsSlice<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsDynamic<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsFunctionPointer<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsClosure<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsDynTrait<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsNamed<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsStruct<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsRecord<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsEnum<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsTrait<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsDoctrine<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeHasConcreteLayout<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeDisplayName<T, VariantIndex, PayloadIndex>() -> ascii`
- `EnumVariantPayloadTypeBaseName<T, VariantIndex, PayloadIndex>() -> ascii`
- `EnumVariantPayloadTypeModuleName<T, VariantIndex, PayloadIndex>() -> ascii`
- `EnumVariantPayloadTypeIsGenericInstantiation<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeArgumentCount<T, VariantIndex, PayloadIndex>() -> u64[0 max]`
- `EnumVariantPayloadTypeComptimeArgumentCount<T, VariantIndex, PayloadIndex>() -> u64[0 max]`
- `EnumVariantPayloadTypeHasQualifiers<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeBorrowKindIsNone<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeBorrowKindIsBorrow<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeBorrowKindIsRetBorrow<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeBorrowKindIsStoreBorrow<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeAccessKindIsNone<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeAccessKindIsShared<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeAccessKindIsFrozen<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeInitializationKindIsNone<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeInitializationKindIsOut<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeInitializationKindIsInit<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeIsMutableView<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadTypeUnqualifiedTypeIs<T, U, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadHasName<T, VariantIndex, PayloadIndex>() -> bool`
- `EnumVariantPayloadName<T, VariantIndex, PayloadIndex>() -> ascii`

Concrete out-of-range variant or payload indices are compile-time errors.
Symbolic indices may flow through generic CTFE until specialization.

Payload type metadata facts inspect the selected payload type after generic
substitution. `EnumVariantPayloadTypeBaseName` and
`EnumVariantPayloadTypeModuleName` return an empty `ascii` value for non-named
payload types. Generic-argument counts return `0` for non-generic payload
types.
Qualifier metadata facts inspect the selected payload type before qualifier
normalization and mirror the top-level `Type*` qualifier facts. Package-backed
typed interfaces preserve enum payload qualifier metadata.

`EnumVariantTag` returns the selected source variant's concrete direct-tag
value. This is not merely the source index: if a later unit variant becomes the
zero-tag variant, the returned values follow the direct-tag layout. Tag layout
facts expose the concrete `$tag` storage field and require concrete enum layout.
They erase before MIR/codegen and work from package-backed typed interfaces.

Payload layout facts expose the selected enum payload's concrete storage field
after enum lowering. They require concrete enum layout, erase before MIR/codegen,
work from package-backed typed interfaces, and use the same direct-layout model
as `sizeof(T)` / `alignof(T)`.

`EnumVariantPayloadName` returns an empty `ascii` compile-time string for
positional payload fields. Prefer branching on `EnumVariantPayloadHasName` or
`EnumVariantUsesNamedFields` when the distinction matters.

`EnumVariantIsErrorFunnel` reports the `Name from ErrorType` funnel payload
form used by `try`. `EnumVariantAbsorbsErrorTypeIs` compares the absorbed
error type after substituting generic type and `comptime` value arguments from
`T`. Package-backed typed interfaces preserve the funnel metadata needed by
these facts.

## Named-Type Generic Parameter Facts

- `TypeGenericParameterCount<T>() -> u64[0 max]`
- `TypeGenericParameterName<T, I>() -> ascii`
- `TypeComptimeGenericParameterCount<T>() -> u64[0 max]`
- `TypeComptimeGenericParameterName<T, I>() -> ascii`
- `TypeComptimeGenericParameterTypeIs<T, U, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsBool<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsInteger<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsFloat<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsRawPointer<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsFixedArray<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsSlice<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsDynamic<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsFunctionPointer<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsClosure<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsDynTrait<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsNamed<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsStruct<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsRecord<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsEnum<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsTrait<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeIsDoctrine<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeHasConcreteLayout<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeDisplayName<T, I>() -> ascii`
- `TypeComptimeGenericParameterTypeBaseName<T, I>() -> ascii`
- `TypeComptimeGenericParameterTypeModuleName<T, I>() -> ascii`
- `TypeComptimeGenericParameterTypeIsGenericInstantiation<T, I>() -> bool`
- `TypeComptimeGenericParameterTypeArgumentCount<T, I>() -> u64[0 max]`
- `TypeComptimeGenericParameterTypeComptimeArgumentCount<T, I>() -> u64[0 max]`
- `TypeDisplayName<T>() -> ascii`
- `TypeBaseName<T>() -> ascii`
- `TypeModuleName<T>() -> ascii`
- `TypeVisibilityIsModule<T>() -> bool`
- `TypeVisibilityIsInternal<T>() -> bool`
- `TypeVisibilityIsPublic<T>() -> bool`
- `TypeVisibilityIsExport<T>() -> bool`
- `TypeIsGenericInstantiation<T>() -> bool`
- `TypeArgumentCount<T>() -> u64[0 max]`
- `TypeComptimeArgumentCount<T>() -> u64[0 max]`
- `TypeComptimeArgumentName<T, I>() -> ascii`
- `TypeComptimeArgumentTypeIs<T, U, I>() -> bool`
- `TypeComptimeArgumentTypeIsBool<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsInteger<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsFloat<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsRawPointer<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsFixedArray<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsSlice<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsDynamic<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsFunctionPointer<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsClosure<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsDynTrait<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsNamed<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsStruct<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsRecord<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsEnum<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsTrait<T, I>() -> bool`
- `TypeComptimeArgumentTypeIsDoctrine<T, I>() -> bool`
- `TypeComptimeArgumentTypeHasConcreteLayout<T, I>() -> bool`
- `TypeComptimeArgumentTypeDisplayName<T, I>() -> ascii`
- `TypeComptimeArgumentTypeBaseName<T, I>() -> ascii`
- `TypeComptimeArgumentTypeModuleName<T, I>() -> ascii`
- `TypeComptimeArgumentTypeIsGenericInstantiation<T, I>() -> bool`
- `TypeComptimeArgumentTypeArgumentCount<T, I>() -> u64[0 max]`
- `TypeComptimeArgumentTypeComptimeArgumentCount<T, I>() -> u64[0 max]`
- `TypeComptimeArgumentValueIs<T, I, Value>() -> bool`
- `TypeArgumentTypeIs<T, U, I>() -> bool`
- `TypeArgumentTypeIsBool<T, I>() -> bool`
- `TypeArgumentTypeIsInteger<T, I>() -> bool`
- `TypeArgumentTypeIsFloat<T, I>() -> bool`
- `TypeArgumentTypeIsRawPointer<T, I>() -> bool`
- `TypeArgumentTypeIsFixedArray<T, I>() -> bool`
- `TypeArgumentTypeIsSlice<T, I>() -> bool`
- `TypeArgumentTypeIsDynamic<T, I>() -> bool`
- `TypeArgumentTypeIsFunctionPointer<T, I>() -> bool`
- `TypeArgumentTypeIsClosure<T, I>() -> bool`
- `TypeArgumentTypeIsDynTrait<T, I>() -> bool`
- `TypeArgumentTypeIsNamed<T, I>() -> bool`
- `TypeArgumentTypeIsStruct<T, I>() -> bool`
- `TypeArgumentTypeIsRecord<T, I>() -> bool`
- `TypeArgumentTypeIsEnum<T, I>() -> bool`
- `TypeArgumentTypeIsTrait<T, I>() -> bool`
- `TypeArgumentTypeIsDoctrine<T, I>() -> bool`
- `TypeArgumentTypeHasConcreteLayout<T, I>() -> bool`
- `TypeArgumentTypeDisplayName<T, I>() -> ascii`
- `TypeArgumentTypeBaseName<T, I>() -> ascii`
- `TypeArgumentTypeModuleName<T, I>() -> ascii`
- `TypeArgumentTypeIsGenericInstantiation<T, I>() -> bool`
- `TypeArgumentTypeArgumentCount<T, I>() -> u64[0 max]`
- `TypeArgumentTypeComptimeArgumentCount<T, I>() -> u64[0 max]`

Generic parameter names are indexed in declaration order. For instantiated
types, these facts inspect the generic template declaration, so
`Buffer<i32[min max], 4>` reports the parameters from
`Buffer<T, comptime u8[1 8] N>`.

Concrete out-of-range generic parameter indices are compile-time errors.
Symbolic indices may flow through generic CTFE until specialization.
Package-backed typed interfaces preserve named-type generic metadata needed by
these facts.

`TypeDisplayName` returns the normalized type display spelling.
`TypeBaseName` returns the named type base, with generic arguments stripped, or
empty `ascii` for non-named types. `TypeModuleName` and the nested
`...ModuleName` facts return the declaring module for named types and empty
`ascii` for primitives, pointers, arrays, structural callable forms, and other
non-named types. `TypeVisibilityIsModule`, `TypeVisibilityIsInternal`,
`TypeVisibilityIsPublic`, and `TypeVisibilityIsExport` expose named type
declaration visibility and return `false` for non-named types. Actual
type-argument and `comptime`
argument predicates inspect the instantiated type itself, not the generic
template declaration. Actual `comptime` argument facts expose the parameter
name, exact argument type, and integer value carried by concrete typed
`comptime` arguments. Concrete out-of-range actual argument indices are
compile-time errors.

## Trait Conformance

- `Implements<T, Trait>() -> bool`
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

The second argument must resolve to a trait. The fact follows Stark's static
trait conformance model and does not create trait objects, vtables, hidden
dispatch, or runtime reflection. Indexed implemented-trait facts expose the
stored trait base-list for a named type, reject concrete out-of-range trait and
argument indices, and work from package-backed trait metadata for imported
types.

## Associated-Type Facts

- `AssociatedTypeCount<T>() -> u64[0 max]`
- `AssociatedTypeName<T, I>() -> ascii`
- `AssociatedTypeHasTarget<T, I>() -> bool`
- `AssociatedTypeTargetTypeIs<T, U, I>() -> bool`
- `AssociatedTypeTargetTypeIsBool<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsInteger<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsFloat<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsRawPointer<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsFixedArray<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsSlice<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsDynamic<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsFunctionPointer<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsClosure<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsDynTrait<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsNamed<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsStruct<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsRecord<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsEnum<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsTrait<T, I>() -> bool`
- `AssociatedTypeTargetTypeIsDoctrine<T, I>() -> bool`
- `AssociatedTypeTargetTypeHasConcreteLayout<T, I>() -> bool`
- `AssociatedTypeTargetTypeDisplayName<T, I>() -> ascii`
- `AssociatedTypeTargetTypeBaseName<T, I>() -> ascii`
- `AssociatedTypeTargetTypeModuleName<T, I>() -> ascii`
- `AssociatedTypeTargetTypeIsGenericInstantiation<T, I>() -> bool`
- `AssociatedTypeTargetTypeArgumentCount<T, I>() -> u64[0 max]`
- `AssociatedTypeTargetTypeComptimeArgumentCount<T, I>() -> u64[0 max]`

Associated aliases are indexed by deterministic ordinal name order, matching
package-image source bridge and typed-interface ordering. `AssociatedTypeHasTarget`
is false for required trait aliases such as `alias Item;`.
`AssociatedTypeTargetTypeIs` substitutes generic type and `comptime` value
arguments from `T` before comparing with `U`.
Target type-category facts mirror the field, enum-payload, method,
function-pointer, and closure type-category predicates for the selected
associated alias target. Required aliases without targets return false for
target category predicates.
Target identity/generic-shape facts expose the selected target type's display
name, base name, declaration module name, generic-instantiation state, ordinary
type-argument count, and `comptime` value-argument count after the same owner
substitution. Required
aliases without targets return default metadata values: empty `ascii`, `false`,
or `0`.

Concrete out-of-range associated-type indices are compile-time errors. Symbolic
indices may flow through generic CTFE until specialization. Package-backed typed
interfaces preserve associated-type metadata needed by these facts.

## Text Name Branching

Name facts return ordinary `ascii` compile-time text constants:

```stark
finite law bool IsValueField<T, comptime u64[0 max] I>()
{
    return comptime (System.Compiler.FieldName<T, comptime I>() == "Value");
}

finite law bool HasPayloadName<T, comptime u64[0 max] V, comptime u64[0 max] P>()
{
    return comptime System.Compiler.EnumVariantPayloadHasName<T, comptime V, comptime P>();
}
```

CTFE text equality compares decoded text payloads, not raw literal spelling.
