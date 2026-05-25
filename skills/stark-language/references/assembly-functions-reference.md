# Assembly Functions Reference

Stark assembly functions are unsafe FFI-style declarations that lower to LLVM
inline assembly. Use them for tiny platform or CPU shims, then wrap them behind
safe Stark APIs. Do not use assembly functions as ordinary application logic.

## Source Shape

```stark
visibility unsafe ffi asm(architecture) fn ReturnType Name(parameters)
    in("register") parameterName,
    out("register") return,
    clobber("register1", "register2")
{
    "assembly template"
}
```

Example Linux x86_64 syscall shim:

```stark
module Platform.Syscall

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

Calls require unsafe context:

```stark
unsafe fn i64[min max] RawSyscall1(i64[min max] number, i64[min max] value)
{
    return Syscall1(number, value);
}
```

## Required Declaration Form

Current v1 assembly declarations must use:

- `unsafe ffi asm(arch) fn`
- no generics
- no `finite`, `law`, `inline`, `noinline`, `hot`, `cold`, or `strictfp`
- a single string assembly template body

The body is not a Stark statement block. It is one string passed to LLVM inline
assembly.

## Target Selection

You may declare multiple same-name assembly functions for different targets:

```stark
internal unsafe ffi asm(x86_64) fn i64[min max] Syscall0(i64[min max] number)
    in("rax") number,
    out("rax") return,
    clobber("rcx", "r11")
{
    "syscall"
}

internal unsafe ffi asm(aarch64) fn i64[min max] Syscall0(i64[min max] number)
    in("x8") number,
    out("x0") return
{
    "svc #0"
}
```

The compiler keeps exactly the declaration that matches the active target. It
rejects a group if no declaration matches or more than one declaration matches.
Do not mix an asm declaration and a non-asm declaration with the same function
name.

Supported architecture spellings:

- `x86_64`, `amd64`
- `aarch64`, `arm64`
- `riscv64`
- `x86`, `i386`, `i486`, `i586`, `i686`
- `arm`, `arm32`, `thumb`

## Operands

Input:

```stark
in("rdi") value
```

Return output:

```stark
out("rax") return
```

Clobbers:

```stark
clobber("rcx", "r11")
```

Non-void assembly functions must bind exactly one return value with
`out("reg") return`. `void` assembly functions must not bind `return`.

Current caution: the grammar accepts `out("reg") parameterName` for output
parameters, but source assembly body emission currently supports only direct
return outputs. Avoid non-return output operands until that lowering is
completed.

## Types

Assembly parameters may be:

- integer scalars
- floating point scalars
- raw pointers

Assembly return types may be:

- integer scalars
- floating point scalars
- raw pointers
- `void`

Do not expose text views, borrows, slices, structs, records, enums, dynamic
storage, or ordinary owned objects directly through assembly declarations. Use a
safe Stark wrapper that converts to raw pointers and scalar values at the
boundary.

## Register Classes

The compiler validates register classes:

- integer and raw pointer values use general-purpose registers
- floating point values use floating point registers

Common register sets:

- x86_64 GP: `rax`, `rbx`, `rcx`, `rdx`, `rsi`, `rdi`, `rbp`, `rsp`, `r8`-`r15`
- x86_64 FP: `xmm0`-`xmm15`
- aarch64 GP: `x0`-`x30`, `w0`-`w30`, `sp`
- aarch64 FP: `s0`-`s31`, `d0`-`d31`
- riscv64 GP: `x0`-`x31`, `zero`, `ra`, `sp`, `gp`, `tp`, `t0`-`t6`,
  `s0`-`s11`, `a0`-`a7`, `fp`
- x86 GP: `eax`, `ebx`, `ecx`, `edx`, `esi`, `edi`, `ebp`, `esp`
- x86 FP: `xmm0`-`xmm7`
- arm32 GP: `r0`-`r12`, `sp`, `lr`, `pc`

## Memory Contracts

Assembly declarations are external ABI boundaries. They do not receive Stark's
default non-overlap contract for memory-backed parameters.

Use explicit `where disjoint(...)`, `where overlap(...)`, or `where same(...)`
when the wrapper needs those memory facts:

```stark
internal unsafe ffi asm(x86_64) fn void CopyBytes(
    rawmutptr<i8[min max]> destination,
    rawptr<i8[min max]> source,
    i64[0 max] length)
    where disjoint(destination[0, length], source[0, length])
    in("r8") destination,
    in("r9") source,
    in("r10") length,
    clobber("rdi", "rsi", "rcx")
{
    "cld\nmovq %r8, %rdi\nmovq %r9, %rsi\nmovq %r10, %rcx\nrep movsb"
}
```

## Lowering Notes

Root source assembly bodies emit LLVM inline assembly definitions. Imported
source assembly functions emit declarations and calls. Package images preserve
assembly metadata for consumers.

The compiler emits assembly as side-effecting inline assembly, adds an implicit
`memory` clobber, and on x86/x86_64 also adds `dirflag`, `fpsr`, and `flags`.

Keep assembly declarations `internal` unless the package intentionally exposes a
low-level unsafe surface. Prefer a small safe wrapper for public APIs.
