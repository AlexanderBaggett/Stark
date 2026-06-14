# Phase 15 - Explicit FFI ABI Spelling

Status: **implementation landed; book-facing docs pending.** Stark spells
foreign calling conventions as an argument to the existing `ffi` modifier:

```stark
unsafe ffi(c) fn ...
fnptr<unsafe ffi(c) fn ...>
```

This keeps the foreign boundary explicit and avoids inventing a separate
load-bearing annotation for ABI facts.

Implementation evidence:

- Parser accepts `ffi(abi)` and `ffi(platform(...))` on declarations and function
  pointer types.
- Type checking stores ABI facts on function signatures and ABI/safety facts on
  `fnptr` types, and rejects mismatched ABI or unsafe pointer assignment/promotion.
- LLVM emission lowers explicit ABI facts onto declarations, direct calls, and
  indirect calls without changing ordinary Stark `fastcc` calls.
- Package images serialize ABI facts for functions and methods, plus ABI/safety
  facts for function pointer types, compiler facts, and imported template type
  references.
- Focused and full host test coverage passed in `compiler.Tests`.

## 1. Goals

- Tag foreign functions with an ABI / calling convention.
- Carry ABI in function pointer types.
- Keep `unsafe` and `ffi` visible at ABI boundaries.
- Define a default ABI when no ABI is written.
- Support platform-conditional ABI selection for cross-platform declarations.
- Preserve existing `ffi varargs`, `ffi asm(...)`, and ordinary `fnptr<fn ...>`
  behavior where possible.

## 2. Core Syntax

### 2.1 FFI Function Declarations

The canonical form is:

```stark
visibility unsafe ffi(abi) fn ReturnType Name(parameters);
```

Examples:

```stark
public unsafe ffi(c) fn i32[min max] puts(rawptr<i8[min max]> text);

public unsafe ffi(stdcall) fn i32[min max] MessageBoxW(
    rawptr<i8[min max]> hwnd,
    rawptr<i16[min max]> text,
    rawptr<i16[min max]> caption,
    u32[0 max] flags);
```

The ABI name is a lowercase identifier. The initial ABI set:

| ABI | Meaning |
|---|---|
| `c` | Target platform's C ABI. |
| `cdecl` | Explicit x86 C declaration convention, where supported. |
| `stdcall` | Win32 stdcall, where supported. |
| `fastcall` | Platform fastcall convention, where supported. |
| `thiscall` | C++ instance-method convention, where supported. |
| `vectorcall` | Vectorcall convention, where supported. |
| `sysv` | System V ABI, primarily x86_64 Unix targets. |
| `win64` | Windows x64 ABI. |
| `aapcs` | ARM Procedure Call Standard. |
| `aapcs64` | AArch64 Procedure Call Standard. |

Unsupported ABI/target combinations are compile-time errors.

Foreign function names are imported by their Stark declaration name. Stark
therefore permits underscore-leading identifiers so exact C spellings such as
`__error` can be represented without a symbol-alias attribute. A single `_`
continues to tokenize as discard and cannot be used as a declaration name.

### 2.2 Default ABI

An `ffi` declaration without an explicit ABI means `ffi(c)`:

```stark
public unsafe ffi fn i32[min max] puts(rawptr<i8[min max]> text);
```

is equivalent to:

```stark
public unsafe ffi(c) fn i32[min max] puts(rawptr<i8[min max]> text);
```

`c` is target-relative. For example, it resolves to the platform C calling
convention for the active target, not to one universal register/stack layout.

Ordinary Stark functions without `ffi` keep Stark's ordinary callable ABI. Ordinary
`fnptr<fn ...>` remains the existing Stark function-pointer type and does not
silently become a C ABI pointer.

### 2.3 `unsafe`

ABI and safety are separate facts.

```stark
fnptr<ffi(c) fn i32[min max](i32[min max])>
fnptr<unsafe ffi(c) fn i32[min max](rawmutptr<i8[min max]>)>
```

`ffi(c)` says how the call is made. `unsafe` says what proof is required to call it.
An unsafe function item may promote only to a function pointer type that also carries
`unsafe`. The unsafe requirement is not erased by an `unsafe` block; calling through
an unsafe function pointer requires an unsafe context at the call site.

### 2.4 `varargs`

`varargs` stays a modifier on FFI declarations:

```stark
public unsafe ffi(c) varargs fn i32[min max] printf(ascii format);
```

The old spelling remains accepted and defaults to `ffi(c)`:

```stark
public unsafe ffi varargs fn i32[min max] printf(ascii format);
```

`varargs` is valid only for ABI forms that support C-style variadic calls on the
active target. Unsupported combinations are compile-time errors.

### 2.5 `ffi asm(...)`

Assembly shims keep their existing architecture selector:

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

`ffi asm(architecture)` is not an ordinary platform ABI declaration. It supplies
register constraints directly, so `ffi(c) asm(...)` and `ffi(platform(...)) asm(...)`
are rejected.

## 3. Function Pointer Types

Function pointer types carry ABI inside the callable signature:

```stark
fnptr<ffi(c) fn i32[min max](rawptr<i8[min max]>)>
fnptr<ffi(stdcall) fn i32[min max](rawptr<i8[min max]>, u32[0 max])>
fnptr<unsafe ffi(win64) fn void(rawmutptr<i8[min max]>)>
```

The ABI is part of type identity:

```stark
fnptr<ffi(c) fn void()>              // different type
fnptr<ffi(stdcall) fn void()>        // different type
fnptr<unsafe ffi(c) fn void()>       // different type
fnptr<fn void()>                     // Stark ordinary function pointer type
```

No implicit conversion exists between different ABI or safety function pointer
types. A program must declare the correct type, or call an explicitly safe adapter
that checks the required invariants and exposes an ordinary safe signature.

Function kind remains part of the same signature:

```stark
fnptr<ffi(c) fn i32[min max](i32[min max])>
fnptr<ffi(c) finite i32[min max](i32[min max])>
fnptr<ffi(c) law bool(rawptr<i8[min max]>)>
fnptr<ffi(c) finite law bool(rawptr<i8[min max]>)>
```

Memory contracts continue to use synthetic parameter names:

```stark
fnptr<ffi(c) fn void(rawmutptr<i8[min max]>, rawmutptr<i8[min max]>)
    where overlap(arg0, arg1)>
```

## 4. Platform-Conditional ABI Selection

Some libraries need one source declaration with different ABIs on different
targets. Use `ffi(platform(...))`:

```stark
public unsafe ffi(platform(
    windows.x86: stdcall,
    windows.x64: win64,
    linux.x64: sysv,
    macos.x64: sysv,
    linux.arm64: aapcs64,
    macos.arm64: aapcs64
)) fn void RegisterCallback(fnptr<ffi(c) fn void(rawptr<i8[min max]>)> callback);
```

Shorter platform keys are allowed when unambiguous:

```stark
public unsafe ffi(platform(
    windows: win64,
    linux: sysv,
    macos: sysv
)) fn void HostCall(rawptr<i8[min max]> context);
```

`default` may be used as a fallback:

```stark
public unsafe ffi(platform(
    windows.x86: stdcall,
    default: c
)) fn i32[min max] LegacyCall(i32[min max] value);
```

Resolution rules:

1. Select entries matching the active target OS and architecture.
2. Prefer the most specific key (`windows.x86` over `windows` over `default`).
3. If two equally specific entries match, report a compile-time ambiguity.
4. If no entry matches and no `default` exists, report a compile-time error.
5. Resolve the selected ABI and then apply ordinary target support checks.

The resolved ABI is what enters the type model, package image, and lowering.

## 5. Exported ABI Functions

`export` controls binary symbol visibility. `ffi(abi)` controls the calling
convention.

For a C-callable exported Stark function, write both:

```stark
export unsafe ffi(c) fn i32[min max] PluginEntry(rawptr<i8[min max]> context)
{
    return 0;
}
```

An `export fn` without `ffi(...)` keeps Stark's existing exported-function behavior
and does not silently promise C ABI compatibility, except for language-defined
entrypoints such as the hosted `main` rule.

## 6. Type Model And Compatibility Rules

An ABI-bearing function signature contains:

- safety (`safe` / `unsafe`)
- ABI (`none` for ordinary Stark, or a resolved FFI ABI)
- function kind (`fn`, `finite`, `law`, `finite law`)
- return type
- parameter types
- memory contracts
- varargs flag, when applicable

Compatibility:

- ordinary `fnptr<fn ...>` accepts only ordinary Stark-ABI function items
- `fnptr<ffi(c) fn ...>` accepts only function items with resolved ABI `c`
- `fnptr<ffi(stdcall) fn ...>` accepts only function items with resolved ABI
  `stdcall`
- safety cannot be weakened implicitly
- function kind cannot be strengthened implicitly
- memory contracts must remain compatible exactly as today

Package images must serialize the resolved ABI for declarations and function
pointer types. Generic templates that mention ABI-bearing function pointer types
must republish those ABI facts across package boundaries.

## 7. Grammar Sketch

This is not implementation code, but shows the intended grammar shape.

```antlr
functionModifier
    : INLINE
    | NOINLINE
    | INLINEHINT
    | HOT
    | COLD
    | ffiModifier
    | VARARGS
    | UNSAFE
    | STRICTFP
    | STATIC
    ;

ffiModifier
    : FFI abiSpecifier?
    ;

abiSpecifier
    : LPAREN abiName RPAREN
    | LPAREN platformAbiSelector RPAREN
    ;

abiName
    : Identifier
    ;

platformAbiSelector
    : PLATFORM LPAREN platformAbiEntry (COMMA platformAbiEntry)* COMMA? RPAREN
    ;

platformAbiEntry
    : platformKey COLON abiName
    ;

functionPointerSignature
    : callableModifier* functionKind returnType functionPointerParameterList
      parameterMemoryContractClause*
    ;

callableModifier
    : UNSAFE
    | ffiModifier
    ;
```

The concrete implementation may use a tighter parser shape to avoid ambiguity, but
the accepted source surface should remain the examples above.

## 8. Diagnostics

Recommended diagnostics:

| Code | Condition |
|---|---|
| ABI01 | Unknown ABI name. |
| ABI02 | ABI not supported for target. |
| ABI03 | ABI mismatch in function pointer promotion or assignment. |
| ABI04 | `varargs` used with ABI that does not support C-style varargs. |
| ABI05 | `ffi(platform(...))` has no match for target. |
| ABI06 | `ffi(platform(...))` has ambiguous entries for target. |
| ABI07 | ABI specifier used without `ffi`. |
| ABI08 | `ffi(abi)` combined with `ffi asm(...)`. |

## 9. Implementation Work Items

| Status | ID | Item |
|---|---|---|
| [x] | ABI-01 | Implement explicit ABI syntax and type model for `ffi(abi)`, `ffi(platform(...))`, and ABI/safety-bearing function pointer signatures. |
| [x] | ABI-02 | Implement target ABI resolution, platform selector diagnostics, type compatibility, promotion rules, and `ffi varargs` validation against the resolved ABI. |
| [x] | ABI-03 | Lower ABI facts through declarations, definitions, direct calls, indirect calls, package images, imported templates, and LLVM emission. |
| [x] | ABI-04 | Add end-to-end parser, type-checking, package, LLVM emission, and stdlib callback smoke coverage for ABI-specific declarations and function pointers. |

## 10. Book And Reference Work

| Status | ID | Item |
|---|---|---|
| [x] | ABI-DOC-01 | Update user-facing book chapters, `LanguageReference.md`, SKILL references, and FFI checklist for explicit ABI spelling and ABI-bearing function pointer types. |
