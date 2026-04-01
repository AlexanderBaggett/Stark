
Recommendation: asm(x86_64). It's self-contained, doesn't require you to design a full attribute system yet, and reads naturally: "this is an asm block targeting x86_64." You can always promote to a general attribute syntax later if needed.
Multi-arch stdlib would then look like:



```module System.Syscall

public ffi asm(x86_64) fn i64 Syscall3(i64 number, i64 arg1, i64 arg2, i64 arg3)
    in("rax") number,
    in("rdi") arg1,
    in("rsi") arg2,
    in("rdx") arg3,
    out("rax") return,
    clobber("rcx", "r11")
{
    "syscall"
}

public ffi asm(aarch64) fn i64 Syscall3(i64 number, i64 arg1, i64 arg2, i64 arg3)
    in("x8") number,
    in("x0") arg1,
    in("x1") arg2,
    in("x2") arg3,
    out("x0") return,
    clobber("x8")
{
    "svc #0"
}```


Given Stark's current pipeline, `asm(arch)` resolution should happen during `syntax-model` or in a small dedicated pass immediately after it. At that point the compiler already knows the active build target, so it can normalize the target architecture and select the matching `asm(arch)` declaration while discarding the rest. Non-matching asm declarations never enter the declaration index, never become symbols, and never appear in package surfaces or downstream HIR/MIR/SSA artifacts. The selected asm declaration then flows through the rest of the pipeline like any other top-level function, except that its asm template, operand bindings, and clobbers are preserved as compiler-owned metadata. `function-effects` derives a conservative effect profile from the function kind together with asm-specific memory/clobber rules, `type-check` and `semantic-validate` validate operand bindings, register names, and allowed parameter/return types, and `emit-llvm` lowers the preserved asm metadata into LLVM inline-asm call syntax instead of generating a normal Stark body.

Stark asm syntax is essentially a structured frontend for LLVM's inline asm constraint strings. The compiler's job is just mapping in/out/clobber with register names into that "constraints"(operands) format.

Minimal v1 example for the current syscall-oriented surface:

```stark
module Syscall

public ffi asm(x86_64) fn i64 Syscall0(i64 number)
    in("rax") number,
    out("rax") return,
    clobber("rcx", "r11")
{
    "syscall"
}
```

For package boundaries, Stark should preserve the selected asm declaration in the package manifest as structured asm metadata. A dependent module can then reconstruct the declaration for syntax-model/type-check/target-selection purposes, while `emit-llvm` still treats manifest-backed asm functions as external ABI declarations and links the packaged library rather than re-emitting the inline asm body in the consumer.
