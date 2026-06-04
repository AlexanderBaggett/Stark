# Phase 16 - FFI C Primitive Type Aliases

Status: **compiler implementation mostly landed; source-alias preservation and
book-facing docs remain open.**

Stark will provide compiler-known C primitive aliases for FFI declarations and
platform ABI aggregates. These names describe C's target-dependent ABI surface,
but they resolve to ordinary Stark sized primitive types before layout, type
checking, package emission, and LLVM lowering.

The goal is to preserve C ABI shape without creating hidden conversions or a
second integer type system.

## 1. Goals

- Provide standard spellings for C primitive integer and pointer-size types.
- Map those spellings onto Stark's sized primitives for the active target.
- Keep all target dependence explicit at the FFI and platform layout boundary.
- Make `c_char` signedness visible instead of pretending it is portable.
- Model `void*` without making C `void` a first-class Stark value type.
- Keep ordinary Stark logic on ordinary Stark types unless code deliberately
  chooses to stay in the C alias surface.

## 2. Namespace And Names

C aliases live under `System.C`.

Initial aliases:

| Alias | C spelling |
|---|---|
| `System.C.c_char` | `char` |
| `System.C.c_schar` | `signed char` |
| `System.C.c_uchar` | `unsigned char` |
| `System.C.c_short` | `short` |
| `System.C.c_ushort` | `unsigned short` |
| `System.C.c_int` | `int` |
| `System.C.c_uint` | `unsigned int` |
| `System.C.c_long` | `long` |
| `System.C.c_ulong` | `unsigned long` |
| `System.C.c_longlong` | `long long` |
| `System.C.c_ulonglong` | `unsigned long long` |
| `System.C.c_size_t` | `size_t` |
| `System.C.c_ptrdiff_t` | `ptrdiff_t` |
| `System.C.c_void` | `void`, only as a raw pointer pointee |

Examples:

```stark
public unsafe ffi(c) fn System.C.c_int close(System.C.c_int fd);

public unsafe ffi(c) fn System.C.c_size_t strlen(
    rawptr<System.C.c_char> text);

public unsafe ffi(c) fn rawmutptr<System.C.c_void> malloc(
    System.C.c_size_t bytes);

public unsafe ffi(c) fn void free(rawmutptr<System.C.c_void> ptr);
```

Use `System.C.c_char` only when the C header says `char`. Use
`System.C.c_schar` for `signed char`, and `System.C.c_uchar` for
`unsigned char`.

## 3. Type Semantics

The aliases are target-resolved type aliases. They do not create distinct
runtime types, ownership classes, alias classes, or ABI identities beyond the
resolved Stark primitive.

For a selected target:

```stark
System.C.c_int
```

resolves to a Stark integer type such as:

```stark
i32[min max]
```

After resolution, ordinary Stark type rules apply:

- no implicit widening or narrowing is added for C aliases
- explicit conversions use the normal Stark conversion form
- two aliases that resolve to the same primitive type are the same runtime type
  on that target
- diagnostics should preserve the source alias spelling when reporting FFI
  declarations and platform layout facts

Example:

```stark
unsafe ffi(c) fn System.C.c_long PlatformCount();

unsafe fn i64[min max] StableCount()
{
    stack System.C.c_long nativeCount = PlatformCount();
    return (i64[min max])nativeCount;
}
```

That cast is the explicit point where code leaves the platform-width C surface
and enters a stable Stark type.

## 4. Target Mapping

The target descriptor must define the C data model used by the active target.
At minimum it must provide:

- signedness of plain `char`
- width and signedness for each C integer alias
- pointer width
- `size_t` width
- `ptrdiff_t` width
- alignment facts needed by platform ABI aggregates

Common data models:

| Alias | ILP32 | LP64 | LLP64 |
|---|---|---|---|
| `c_schar` | `i8[min max]` | `i8[min max]` | `i8[min max]` |
| `c_uchar` | `u8[0 max]` | `u8[0 max]` | `u8[0 max]` |
| `c_short` | `i16[min max]` | `i16[min max]` | `i16[min max]` |
| `c_ushort` | `u16[0 max]` | `u16[0 max]` | `u16[0 max]` |
| `c_int` | `i32[min max]` | `i32[min max]` | `i32[min max]` |
| `c_uint` | `u32[0 max]` | `u32[0 max]` | `u32[0 max]` |
| `c_long` | `i32[min max]` | `i64[min max]` | `i32[min max]` |
| `c_ulong` | `u32[0 max]` | `u64[0 max]` | `u32[0 max]` |
| `c_longlong` | `i64[min max]` | `i64[min max]` | `i64[min max]` |
| `c_ulonglong` | `u64[0 max]` | `u64[0 max]` | `u64[0 max]` |
| `c_size_t` | `u32[0 max]` | `u64[0 max]` | `u64[0 max]` on x64, `u32[0 max]` on x86 |
| `c_ptrdiff_t` | `i32[min max]` | `i64[min max]` | `i64[min max]` on x64, `i32[min max]` on x86 |

`c_char` maps to either `i8[min max]` or `u8[0 max]` according to the active
target. It must not be assumed signed or unsigned from the host machine running
the compiler.

## 5. `c_char` Signedness

Plain C `char` is a distinct source spelling from `signed char` and
`unsigned char`, but its signedness is target-dependent. Stark exposes that
fact through the compile-time bool `System.C.c_char_is_signed`, supplied by the
active target descriptor.

Rules:

- `System.C.c_char` follows the target's plain `char` signedness.
- `System.C.c_schar` is always signed 8-bit.
- `System.C.c_uchar` is always unsigned 8-bit.
- APIs that mean bytes should use `System.C.c_uchar` or Stark `u8[0 max]`.
- APIs that mean C strings should use `System.C.c_char` at the boundary and an
  explicit text conversion helper outside the boundary.

No implicit conversion exists between `rawptr<System.C.c_char>` and Stark
`ascii`. A C `char*` is a pointer to foreign bytes; decoding is a separate
operation.

## 6. `c_void`

`System.C.c_void` is an incomplete foreign pointee type. It is valid only behind
raw pointer forms:

```stark
rawptr<System.C.c_void>
rawmutptr<System.C.c_void>
```

Invalid uses:

```stark
stack System.C.c_void value;                 // error
unsafe ffi(c) fn System.C.c_void BadReturn(); // error
struct BadField
{
    System.C.c_void Value;                  // error
}
```

C functions returning `void` use Stark's existing `void` return type:

```stark
public unsafe ffi(c) fn void free(rawmutptr<System.C.c_void> ptr);
```

Conversions between `rawptr<System.C.c_void>` and `rawptr<T>` are explicit raw
pointer conversions and remain unsafe where Stark already requires unsafe raw
pointer conversion.

## 7. FFI And Platform Layout Use

The intended use is to mirror external C declarations:

```stark
public unsafe ffi(c) fn System.C.c_int open(
    rawptr<System.C.c_char> path,
    System.C.c_int flags,
    System.C.c_int mode);
```

When a declaration is modeling a C ABI, use the C alias that matches the header
instead of guessing the platform width by hand.

For ABI aggregates, C aliases preserve layout intent:

```stark
[Platform]
struct StatLike
{
    System.C.c_ulong Device;
    System.C.c_ulong Inode;
    System.C.c_long Size;
}
```

The layout engine resolves each field alias for the active target before
computing field offsets, padding, alignment, and ABI lowering.

## 8. Package Images

The compiler should keep both facts:

- the source-facing alias spelling, for diagnostics and source bridge output
- the resolved canonical Stark primitive, for target-specific layout and codegen

If package images remain target-specific, they may serialize the resolved
primitive plus the original alias spelling. If Stark later supports
target-independent package images, those images must serialize the symbolic
`System.C` alias and resolve it when imported for a concrete target.

## 9. Diagnostics

Recommended diagnostics:

| Condition |
|---|
| Target descriptor does not define a required C type mapping. |
| `System.C.c_void` used as a value, field, array element, generic argument requiring a value type, or function return. |
| Plain `System.C.c_char` signedness requested but missing from the target descriptor. |
| C alias used in a non-FFI public API where a stable Stark type would be clearer. This may start as a warning. |
| Attempted implicit conversion from C string pointer to Stark text. |
| Attempted implicit conversion between `rawptr<c_void>` and a typed raw pointer. |

## 10. Work Items

- [x] Add target C data-model facts to target descriptors.
- [x] Add `System.C` aliases and the `c_void` incomplete pointee marker.
- [x] Resolve C aliases during type resolution and layout.
- [~] Preserve source alias spelling in diagnostics and package bridge output.
      `c_void` is preserved structurally; integer aliases currently resolve to
      canonical Stark primitives in typed package surfaces.
- [~] Serialize C alias facts through package images. `c_void` has a package
      type kind; integer aliases serialize as resolved primitives for the
      target-specific image.
- [x] Teach ABI lowering and LLVM emission to consume the resolved primitives.
- [x] Add diagnostics for invalid `c_void` use and missing target mappings.
- [x] Add tests for ILP32, LP64, LLP64, `c_char` signedness, `c_void`, package
      images, FFI declarations, and platform ABI aggregates.
- [ ] Update user-facing FFI docs and Stark language skill references.

## 11. Implementation Evidence

- Compiler-known target data model: `src/Compiler/StarkCDataModelFacts.cs`.
- Type resolution and `System.C.c_char_is_signed` compile-time fact:
  `src/Compiler/StarkTypeResolver.cs`, `src/Compiler/TypeChecking.cs`.
- `c_void` type model and package codec support:
  `src/Compiler/CompilerArtifacts.cs`,
  `src/Compiler/PackageImage/Shared/PackageTypeCodec.cs`.
- Stdlib namespace surface: `stdlib/src/System/C.stark`,
  re-exported by `stdlib/src/System.stark`.
- Tests:
  `tests/compiler.Tests/TypeCheckingTests.cs`,
  `tests/compiler.Tests/LlvmIrEmissionTests.cs`,
  `tests/compiler.Tests/PackageImageArchitectureTests.cs`.
- Verification:
  `dotnet test tests/compiler.Tests/compiler.Tests.csproj` passed 1523 tests;
  `dotnet test tests/compiler.Tests/compiler.Tests.csproj --filter
  "FullyQualifiedName~SystemC"` passed 15 tests; `dotnet test
  tests/compiler.StandardLibraryTests/compiler.StandardLibraryTests.csproj
  --filter "FullyQualifiedName~StdLibSourceTextBuiltinsAndPathHelperSurfaceCompile"`
  passed 1 test.
