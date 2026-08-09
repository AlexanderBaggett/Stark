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
- `symbol(qualifiedName)` for a typed symbol referenced opaquely by the template
- `memory(none)` or `memory(read(pointer), write(pointer), readwrite(pointer))`

Current LLVM body emission supports direct return outputs only. Non-return
`out("reg") parameterName` is parsed and validated, but still falls outside the
implemented source assembly emission path, so do not use it until that lowering
gap is closed.

## Grammar Change And Source Compatibility

The current grammar extends the original assembly clause list, which accepted
only `in`, `out`, and `clobber`, with two contextual clauses:

```text
asm-clause-list  := asm-clause ("," asm-clause)* ","?
asm-clause       := input | output | clobber | symbol | memory
symbol           := "symbol" "(" qualified-name ")"
memory           := "memory" "(" "none" ")"
                  | "memory" "(" memory-access ("," memory-access)* ","? ")"
memory-access    := "read" "(" parameter-name ")"
                  | "write" "(" parameter-name ")"
                  | "readwrite" "(" parameter-name ")"
```

`symbol` and `memory` are contextual spellings in an assembly clause list, not
new globally reserved words. Clauses come after any function `where` contracts
and before the template body. Clause kinds may be interleaved and the final
clause may have a trailing comma. A declaration may contain multiple distinct
`symbol(...)` clauses but at most one `memory(...)` clause.

This is source-compatible with assembly declarations produced before these
clauses existed. An omitted memory clause deliberately retains the old,
conservative behavior: the assembly may access arbitrary memory and LLVM gets
the universal `~{memory}` clobber. Existing assembly does not have to be edited
to remain correct.

The new clauses are optimization and reachability contracts, not descriptive
comments. Their required use is:

- Add `memory(none)` only after proving the template touches no process memory.
  Register and flag access alone qualifies. MMIO, stack accesses not represented
  by an operand, hidden globals, and memory reached by an integer address do not.
- Add named memory accesses only when they completely describe every memory
  location the template may touch. If any access cannot be expressed through a
  bounded raw-pointer input, omit the clause and keep the conservative barrier.
- Add `symbol(Name)` for every function or global whose linker symbol appears
  only inside the opaque template. Do not add it for an ordinary Stark call or
  function-address use, because those already create normal LLVM reachability.
- Rebuild package images after adopting either clause. Current packages preserve
  the structured facts, but older package metadata cannot reconstruct a source
  contract that did not exist when it was built.

An incorrect `memory(none)` or incomplete named memory list can let LLVM reorder
or remove surrounding memory operations and therefore can miscompile the
program. The declaration is already an unsafe boundary; the compiler validates
the shape of the proof, while the author remains responsible for its truth.

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

## Opaque Symbols And Memory

`symbol(Name)` declares that the assembly template contains an opaque reference
to the named Stark function or global. The argument is a qualified source name,
not a string or linker spelling. It must resolve to exactly one accessible
symbol. Unqualified names resolve relative to the assembly declaration's owning
module; use a qualified name for imported or otherwise ambiguous symbols.
Duplicate references are rejected. This is the only path that roots a
template-only symbol: the compiler never searches target assembly text for
names.

`symbol(Name)` is a retention declaration, not template interpolation. It does
not rewrite the assembly string or substitute the target's linker spelling.
The template must still contain the correct target-assembler symbol spelling;
in practice, template-only cross-symbol references should normally target an
explicitly exported or otherwise stable ABI symbol. A live assembly call or
bridge causes the typed symbol to be retained. An unused assembly declaration
does not keep its symbol references alive merely by existing.

For example, a Linux x86_64 template that calls a stable exported target names
the typed source symbol separately from the opaque template text:

```stark
export fn void DispatchTarget()
{
}

internal unsafe ffi asm(x86_64) fn void InvokeDispatch()
    symbol(DispatchTarget)
{
    "call DispatchTarget"
}
```

The omitted memory clause is intentional: the nested call may access arbitrary
memory. `symbol(DispatchTarget)` solves reachability only; it does not imply any
memory effect for `DispatchTarget`.

An omitted `memory(...)` clause remains maximally conservative and produces
LLVM's `~{memory}` clobber. `memory(none)` proves register-only behavior. A
named clause describes all memory reachable from the template:

```stark
internal unsafe ffi asm(x86_64) fn void CopyBytes(
    rawmutptr<i8[min max]>[length] destination,
    rawptr<i8[min max]>[length] source,
    u64[0 max] length)
    where disjoint(destination, source)
    in("r8") destination,
    in("r9") source,
    in("r10") length,
    clobber("rdi", "rsi", "rcx"),
    memory(read(source), write(destination))
{
    "cld\nmovq %r8, %rdi\nmovq %r9, %rsi\nmovq %r10, %rcx\nrep movsb"
}
```

Each named memory operand must be a bounded `rawptr<T>[count]` or
`rawmutptr<T>[count]` parameter and must also appear as an `in` operand so LLVM
can associate the access with argument memory. Writes require `rawmutptr`.
Duplicate entries are rejected; use `readwrite(pointer)` for combined access.
FFI/asm declarations still need explicit `disjoint`, `overlap`, or `same`
contracts when alias relationships matter.

The memory clause and the `where` clause prove different things. `memory(...)`
states what the template reads or writes; `where disjoint/overlap/same` states
how parameter regions may alias. Neither substitutes for the other. Do not add
`clobber("memory")`: omitting `memory(...)` is Stark's spelling for arbitrary
memory effects.

## LLVM Emission

The compiler preserves the selected assembly model as structured package-image
metadata: architecture, template, inputs, outputs, clobbers, opaque symbol
references, and memory effects. A package consumer therefore lowers the same
assembly plan even when the original `.stark` source is absent.

A direct call lowers to an LLVM inline-assembler expression at the call site.
It does not call a wrapper symbol. This applies equally to root-source and
package-image-backed calls, removes a hot-path call/return boundary, and lets
ordinary reachability and dead-code elimination discard unused assembly
declarations.

A real bridge definition is emitted only when a stable address is required:

- an `export` assembly function keeps its exact external ABI symbol;
- a non-exported function used as a function value gets a module-qualified,
  `dso_local hidden` bridge using Stark's internal calling convention; and
- a consumer that takes the address of a public package assembly function may
  materialize a deduplicated `linkonce_odr` bridge from package metadata.

Direct-only assembly functions emit neither a wrapper nor an imported
declaration. Exported address references remain ordinary undefined references,
so static-archive indexing and extraction work without `@llvm.used`.

LLVM emission currently requires:

- no indirect return ABI
- direct ABI parameters only
- exactly one `out("reg") return` for non-void functions
- no return binding for `void` functions
- no non-return output operands

Emission maps operand clauses into LLVM inline-asm constraints. If an input uses
the same register as the return output, the input uses the tied `"0"` constraint.
The output and input register names are otherwise fixed physical-register
constraints. They do not need an early-clobber marker because distinct fixed
registers cannot be co-allocated, while a shared input/output register is
explicitly tied. A future register-class or allocator-selected operand surface
must re-evaluate early clobbering.

Assembly is emitted with `sideeffect` and `nounwind`. Each call carries LLVM
`!srcloc` cookies derived from the Stark call site (or the declaration for a
bridge) so assembler diagnostics map back to source lines.

When the memory clause is omitted, the compiler adds an implicit `memory`
clobber and deliberately emits no contradictory LLVM memory attribute. For
`memory(none)` it removes that clobber and emits `memory(none)`. Named pointer
accesses remove the universal clobber and emit the narrow union
`memory(argmem: read)`, `write`, or `readwrite`; existing bounded-region and
alias facts remain available on the pointer operands. On x86 and x86_64 the
compiler also adds `dirflag`, `fpsr`, and `flags`.

`@llvm.used` is not emitted for normal calls or function addresses. Each
explicit `symbol(Name)` use registers one proven invisible reference and emits
a deduplicated `@llvm.used` entry for the resolved linker symbol. Package
consumers recreate the declaration and retention fact from structured package
metadata, allowing indexed archives and ThinLTO to preserve the owner without
source fallback.

The lowering follows the current LLVM Language Reference sections for
[inline-assembler expressions](https://llvm.org/docs/LangRef.html#inline-assembler-expressions),
[constraint ordering and early-clobber outputs](https://llvm.org/docs/LangRef.html#inline-asm-constraint-string),
[`!srcloc` metadata](https://llvm.org/docs/LangRef.html#inline-asm-metadata),
[`@llvm.used`](https://llvm.org/docs/LangRef.html#the-llvm-used-global-variable),
[visibility](https://llvm.org/docs/LangRef.html#visibility-styles), and
[runtime preemption](https://llvm.org/docs/LangRef.html#runtime-preemption-specifiers).
LLVM explicitly requires correctness analyses to use operands, constraints, and
flags rather than interpreting target assembly text; that is why Stark will not
infer hidden symbol references or memory effects by parsing the template.

## Effect Model

Assembly declarations are unsafe FFI boundaries:

- they are not pure
- they do not get Stark's default memory non-overlap contract
- callers need unsafe context
- raw pointer and ABI invariants are the caller/wrapper's responsibility

Use safe wrappers around assembly declarations whenever exposing them outside an
internal platform/runtime module.

The release matrix runs `scripts/qualify-assembly-bridge.ps1` on every selected
64-bit target. It builds an optimized package bitcode archive, removes source,
links a consumer through ThinLTO, verifies inline lowering, precise memory, and
opaque retention in IR, runs the final executable, and uploads a checksum-bound
JSON report. Downloaded release archives separately rebuild and run a System
file-I/O smoke with `stdlib/src` hidden, preserving evidence that packaged
standard-library assembly survived archive extraction and final linking.
