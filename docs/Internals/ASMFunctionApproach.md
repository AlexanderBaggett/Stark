# ASM Function Approach

This note records the implemented v1 Stark assembly-function surface. The
feature is intentionally narrow: small unsafe FFI-style shims that lower to LLVM
inline assembly, primarily for standard-library platform boundaries such as
Linux syscalls and tiny CPU instruction wrappers.

## Implemented Source Shape

Assembly functions use `asm(architecture)` after the modifiers and before
`fn`:

```stark
module System.Syscall

internal unsafe ffi asm(x86_64) fn i64[min max] Syscall3(
    i64[min max] number,
    i64[min max] arg1,
    i64[min max] arg2,
    i64[min max] arg3)
    in("rax") number,
    in("rdi") arg1,
    in("rsi") arg2,
    in("rdx") arg3,
    out("rax") return,
    clobber("rcx", "r11")
{
    "syscall"
}
```

The current accepted declaration shape is:

```text
visibility unsafe ffi asm(architecture) fn ReturnType Name(parameters)
    asm-clause-list
{
    "template"
}
```

The v1 validator requires:

- `unsafe ffi asm(arch) fn`
- no generics
- no `finite` or `law`
- no extra modifiers besides `unsafe` and `ffi`
- an assembly template body rather than a Stark statement body

The grammar supports:

- `in("reg") parameterName`
- `out("reg") return`
- `out("reg") parameterName`
- `clobber("reg1", "reg2")`

Current LLVM body emission supports direct return outputs only. Non-return
`out("reg") parameterName` is parsed and validated, but still falls outside the
implemented source assembly emission path, so do not use it until that lowering
gap is closed.

## Target Selection

Multiple declarations with the same name may target different architectures:

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

Syntax model creation resolves the active architecture from the explicit target
triple first, then from the host process architecture. It keeps exactly one
matching declaration for each assembly function name. It reports diagnostics
when:

- the target architecture cannot be resolved
- a declaration uses an unsupported architecture name
- the same function name mixes assembly and non-assembly declarations
- no declaration matches the active target
- multiple declarations match the active target

Supported architecture names:

- `x86_64`, `amd64`
- `aarch64`, `arm64`
- `riscv64`
- `x86`, `i386`, `i486`, `i586`, `i686`
- `arm`, `arm32`, `thumb`

## Type And Register Rules

Assembly parameters may use only direct ABI scalar values:

- integer scalars
- floating point scalars
- raw pointers

Assembly returns may use those same value families or `void`.

Register validation checks the register class:

- integer and raw pointer operands must use general-purpose registers
- floating point operands must use floating point registers

Supported register spellings are intentionally explicit and target-local. The
current sets include:

- x86_64 GP: `rax`, `rbx`, `rcx`, `rdx`, `rsi`, `rdi`, `rbp`, `rsp`, `r8`-`r15`
- x86_64 FP: `xmm0`-`xmm15`
- aarch64 GP: `x0`-`x30`, `w0`-`w30`, `sp`
- aarch64 FP: `s0`-`s31`, `d0`-`d31`
- riscv64 GP: `x0`-`x31`, `zero`, `ra`, `sp`, `gp`, `tp`, `t0`-`t6`,
  `s0`-`s11`, `a0`-`a7`, `fp`
- x86 GP: `eax`, `ebx`, `ecx`, `edx`, `esi`, `edi`, `ebp`, `esp`
- x86 FP: `xmm0`-`xmm7`
- arm32 GP: `r0`-`r12`, `sp`, `lr`, `pc`

## LLVM Emission

Root source assembly bodies emit LLVM inline assembly definitions. Imported
source assembly functions emit declarations and calls; package images preserve
structured assembly metadata so package consumers can reconstruct the selected
declaration surface.

LLVM emission currently requires:

- no indirect return ABI
- direct ABI parameters only
- exactly one `out("reg") return` for non-void functions
- no return binding for `void` functions
- no non-return output operands

Emission maps operand clauses into LLVM inline-asm constraints. If an input uses
the same register as the return output, the input uses the tied `"0"` constraint.
Assembly is emitted with `sideeffect`.

The compiler always adds an implicit `memory` clobber. On x86 and x86_64 it also
adds `dirflag`, `fpsr`, and `flags`.

## Effect Model

Assembly declarations are unsafe FFI boundaries:

- they are not pure
- they do not get Stark's default memory non-overlap contract
- callers need unsafe context
- raw pointer and ABI invariants are the caller/wrapper's responsibility

Use safe wrappers around assembly declarations whenever exposing them outside an
internal platform/runtime module.
