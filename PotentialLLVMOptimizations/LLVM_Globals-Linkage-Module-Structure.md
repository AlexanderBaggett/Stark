# LLVM Optimization Reference: Globals, Linkage, and Module Structure

## Overview

This document covers how global variable attributes, linkage types, visibility/preemption specifiers, thread-local storage models, and module structure (including LTO) interact with LLVM's optimization passes. For a language frontend author, these choices determine the optimizer's ability to inline functions across modules, eliminate dead code, merge duplicate constants, propagate constants through globals, and generate efficient position-independent code.

The key insight is that **the more constrained a global value is, the more LLVM can optimize around it**. Every attribute here tells LLVM "this global has property X, so you may assume Y"—and the optimizer exploits those assumptions aggressively.

---

## 1. Global Variable Attributes

### 1.1 `constant`

A global variable marked `constant` tells LLVM the contents will never be modified after initialization. This is one of the highest-impact global attributes.

```llvm
; Mutable global — optimizer must reload on every access
@mutable_table = global [4 x i32] [i32 10, i32 20, i32 30, i32 40]

; Constant global — optimizer can fold loads at compile time
@const_table = constant [4 x i32] [i32 10, i32 20, i32 30, i32 40]
```

**What it enables:**

- **Constant folding of loads**: A load from a known offset of a `constant` global can be replaced with the value from the initializer. `load i32, ptr getelementptr ([4 x i32], ptr @const_table, i64 0, i64 2)` → `i32 30`.
- **Dead store elimination**: Stores to a constant are UB, so any store instruction targeting a constant global can be assumed unreachable (and surrounding code optimized accordingly).
- **Placement in read-only sections**: The linker puts `constant` globals in `.rodata` or equivalent, which the OS maps read-only. This improves security (W^X) and allows page sharing across processes.
- **IPSCCP propagation**: Interprocedural Sparse Conditional Constant Propagation can trace constant values through the program more effectively.

**Language design recommendation:** Any value that the language semantics guarantee will not change after initialization (enum variant tables, vtables in a sealed class hierarchy, string literals, numeric lookup tables) should be emitted as `constant`. LLVM explicitly permits *declarations* to be marked constant even if the definition is elsewhere — but the language must guarantee this is sound for the translation unit.

**Gotcha:** A variable that needs runtime initialization (e.g., a constructor runs to fill it) cannot be `constant` at IR level, because there is a store during initialization. The workaround is to use `llvm.invariant.start` after initialization completes (covered in the Intrinsics document).

### 1.2 `unnamed_addr` and `local_unnamed_addr`

These attributes tell LLVM that the *address* of the global is not meaningful — only its *content* matters.

```llvm
; Address matters (default) — two globals with identical content must remain distinct
@str1 = constant [6 x i8] c"hello\00"
@str2 = constant [6 x i8] c"hello\00"

; Address doesn't matter — LLVM can merge these into one
@str3 = unnamed_addr constant [6 x i8] c"hello\00"
@str4 = unnamed_addr constant [6 x i8] c"hello\00"

; Address doesn't matter within this module (but might at link time)
@str5 = local_unnamed_addr constant [6 x i8] c"hello\00"
```

**What they enable:**

- **`unnamed_addr`**: The global's address is insignificant *globally*. Constants with identical initializers and `unnamed_addr` can be merged program-wide. A constant with a significant address can absorb an `unnamed_addr` one (the merged result has the significant address). This also enables the linker to place such symbols in special sections or apply ICF (Identical Code Folding) more aggressively.
- **`local_unnamed_addr`**: The address is insignificant *within the module*, but might be significant across link units. This is the more conservative (and more commonly applicable) variant. It enables intra-module optimizations like constant merging without requiring whole-program guarantees.

**What `unnamed_addr` enables for functions:** Functions with `unnamed_addr` or `local_unnamed_addr` can be merged with identical functions (Identical Code Folding). This is significant for template-heavy or monomorphized code.

**Language design recommendation:** Most constants in a well-designed language do not need address identity. String literals, numeric tables, function bodies, vtables, and type metadata can almost always be marked `local_unnamed_addr` (or even `unnamed_addr` if the language has no way to observe their address across link units). Only mark a global as address-significant if the language actually exposes pointer equality on globals.

**Impact ranking:** Medium-high for code size (deduplication), low for runtime performance (minor cache benefits from deduplication).

### 1.3 `externally_initialized`

```llvm
; Normal: LLVM assumes the initial value is valid before global initializers run
@normal = global i32 0

; Externally initialized: LLVM cannot assume the initializer is the true value
;   at program start — some external mechanism may have changed it first
@ext_init = externally_initialized global i32 0
```

By default, LLVM's GlobalOpt pass assumes that global variables defined within a module have not been modified from their initial values before global initializers run — even for globals with external linkage or those in `@llvm.used`. The `externally_initialized` marker suppresses this assumption.

**When you need it:** If your language runtime or linker modifies globals before the IR-level initialization code runs (e.g., dynamic linker relocations fill in function pointers, or a custom loader patches tables), those globals must be `externally_initialized` or GlobalOpt may incorrectly constant-fold their initial values.

**Language design recommendation:** Avoid `externally_initialized` if possible — it blocks important optimizations. If you can guarantee all globals are initialized purely through their IR-level initializers, don't use it.

### 1.4 Alignment on Globals

```llvm
; Default alignment (target-determined)
@data = global [1024 x float] zeroinitializer

; Over-aligned for SIMD (32-byte for AVX)
@aligned_data = global [1024 x float] zeroinitializer, align 32
```

Explicit alignment on globals interacts with the vectorizer. When LLVM can prove that a memory access is aligned to the vector width, it can emit aligned vector loads/stores (which are faster or required on some architectures). An array of floats at `align 32` allows 256-bit (AVX) aligned operations without runtime alignment checks.

**Gotcha:** If a global has an assigned section, targets and optimizers are *not allowed* to over-align it beyond what is specified. This is because code might assume dense packing within a section and iterate over globals as an array.

---

## 2. Linkage Types

Linkage determines how symbols are resolved across translation units and what optimizations LLVM may perform. The choice of linkage is one of the most consequential decisions a frontend makes.

### 2.1 Linkage Type Reference

Listed roughly from **most constrained (best for optimization) to least constrained**:

#### `private`

```llvm
define private i32 @helper() { ... }
@table = private constant [4 x i32] [i32 1, i32 2, i32 3, i32 4]
```

- Only accessible within the current LLVM module. Does not appear in any symbol table.
- All references are internal, so LLVM can freely rename, inline, delete, or transform without considering external users.
- **Optimization benefits:** Maximum. LLVM can inline, change calling convention to `fastcc`, delete if unused, constant-propagate, turn into SSA values, SROA the global, etc.

#### `internal`

```llvm
define internal i32 @helper() { ... }
@table = internal constant [4 x i32] [i32 1, i32 2, i32 3, i32 4]
```

- Same as `private` but appears as a local symbol (`STB_LOCAL` in ELF) in the object file. Useful for debugging.
- Corresponds to C's `static` keyword.
- **Optimization benefits:** Identical to `private` from the optimizer's perspective.
- **Always implicitly `dso_local`.**

#### `external` (the default)

```llvm
define i32 @public_api(i32 %x) { ... }
@shared_var = global i32 0
```

- Exactly one definition in the linked program. The linker errors on duplicates.
- Can be referenced from other modules.
- **Optimization limitations:** LLVM cannot delete the symbol (it may be referenced externally), cannot change its calling convention without LTO, and for non-`dso_local` symbols on ELF with default visibility, must assume it could be interposed (preempted).

#### `linkonce_odr`

```llvm
define linkonce_odr i32 @templated_func() { ... }
$templated_func = comdat any
```

- Multiple identical definitions may exist across modules (ODR guarantee). The linker picks one and discards duplicates.
- The `odr` suffix is critical — it tells LLVM that all definitions are semantically equivalent, so **inlining is safe**. Without `odr`, LLVM cannot inline because it doesn't know if this definition will be the one that survives linking.
- Can be discarded if unreferenced.
- **Use for:** Template instantiations, inline function definitions, any construct where the language guarantees one definition rule.

#### `weak_odr`

```llvm
define weak_odr i32 @weak_func() { ... }
```

- Like `linkonce_odr` but cannot be discarded even if unreferenced (the symbol must survive to the final link).
- `odr` still allows inlining.
- **Use for:** Rare cases where you need ODR merging but must guarantee the symbol exists.

#### `linkonce` (without `odr`)

```llvm
define linkonce i32 @might_differ() { ... }
```

- Multiple definitions may exist and may *differ*. The linker picks one.
- **LLVM cannot inline this** — the visible definition may not be the one chosen at link time, so inlining could change program behavior.
- **Language design note:** If your language has a one-definition rule (most do), always use the `_odr` variants.

#### `weak` (without `odr`)

- Same as `linkonce` but cannot be discarded if unreferenced.
- Inlining not permitted (non-odr).

#### `available_externally`

```llvm
define available_externally i32 @imported_func() {
  ; Full body available for inlining, but won't be emitted as a definition
  ret i32 42
}
```

- Never emitted into the object file. From the linker's perspective, it's an external declaration.
- Exists purely to allow inlining and other optimizations with knowledge of the function body.
- Can be discarded at will by the optimizer.
- **This is ThinLTO's mechanism** for importing function bodies cross-module without duplicating definitions.

**Language design recommendation:** Use this when you want to provide a function body for optimization purposes without committing to emitting a definition (e.g., providing library function bodies to the optimizer in header-like fashion).

#### `common`

```llvm
@tentative = common global i32 0
```

- C's tentative definitions (`int x;` at file scope). Merged like weak symbols.
- Cannot have an explicit section, must have zero initializer, cannot be `constant`.
- **Avoid in new languages.** This exists solely for C compatibility. Use `external` with an explicit initializer instead.

#### `extern_weak`

- Symbol may or may not exist at link time. If absent, the pointer is null.
- Useful for optional runtime features. Not commonly needed in new languages.

### 2.2 The `isExactDefinition()` and `isInterposable()` Distinction

LLVM uses two key predicates internally:

- **`isExactDefinition()`**: Returns true for `external`, `internal`, `private` (non-interposable, non-ODR linkages). When a definition is "exact," LLVM can derive properties from the current definition (e.g., infer `readnone`, `noalias` return, etc.) because the definition cannot be replaced.
- **`isInterposable()`**: Returns true for `linkonce`, `weak`, `common`, `extern_weak` (but NOT `linkonce_odr` or `weak_odr` for inlining purposes). When a symbol is interposable, LLVM cannot derive new properties from examining the function body, because a different definition might be chosen at link time.

The ODR variants occupy a middle ground: they allow inlining (since all definitions are guaranteed equivalent), but LLVM still cannot derive new interprocedural attributes from the body because a "differently optimized variant of the same function can have different observable or undefined behavior."

**Concrete example of the danger:**

```llvm
; If foo is linkonce (not odr), and we see it currently reads no globals:
define linkonce void @foo() {
  ret void
}
; We CANNOT infer readnone — the linker might replace this with a version
; that does read globals. If we inferred readnone, we might DSE a store
; that the actual runtime version of foo depends on.
```

**Language design recommendation:** For maximum optimization, prefer `internal`/`private` linkage wherever possible. For anything that must be visible across modules, use `linkonce_odr` (with comdat on COFF/ELF) when ODR semantics apply. Use `external` for true public API symbols.

### 2.3 Optimization Impact Hierarchy

From most to least optimizer-friendly:

1. **`private` / `internal`** — full interprocedural optimization, calling convention changes, dead code elimination, SROA of globals, constant propagation
2. **`linkonce_odr` / `weak_odr`** — inlining permitted, but cannot derive new interprocedural properties; may be discarded (linkonce) or not (weak)
3. **`external` + `dso_local`** — no inlining across modules without LTO, but at least no indirection through GOT/PLT
4. **`external` (default, possibly preemptable)** — worst case: GOT indirection for data, PLT indirection for calls, no cross-module optimization without LTO
5. **`linkonce` / `weak`** (without odr) — cannot even inline the body; avoid in new languages

---

## 3. Visibility and Runtime Preemption

### 3.1 Visibility Styles

```llvm
; Default — visible to all, can be preempted on ELF with -fPIC
define default i32 @visible_func() { ... }

; Hidden — not exported from shared library, direct access guaranteed
define hidden i32 @internal_to_dso() { ... }

; Protected — exported but not preemptable (direct access within DSO)
define protected i32 @stable_abi_func() { ... }
```

**`hidden` visibility** is the most optimization-friendly for shared libraries:
- No GOT/PLT indirection needed for calls/data access within the shared library
- The symbol is not exported, so it behaves like an internal symbol from the linker's perspective while still being reachable across translation units within the same DSO
- Functions with `hidden` visibility are implicitly `dso_local`

**`protected` visibility** is a middle ground:
- The symbol is exported (other DSOs can reference it)
- But it is *not* preemptable within its defining DSO, so direct calls are still possible
- Note: `protected` has known issues on some platforms (e.g., copy relocations on x86-64 can cause problems)

**`default` visibility** on ELF with position-independent code means the symbol could be preempted by another DSO's definition at runtime (symbol interposition). This forces indirect access through the GOT/PLT.

### 3.2 Runtime Preemption Specifiers

```llvm
; Cannot be preempted — direct access is safe
define dso_local i32 @definitely_here() { ... }

; Might be preempted at runtime (default if not specified)
define dso_preemptable i32 @maybe_elsewhere() { ... }
```

**`dso_local`** tells LLVM that the symbol will resolve to a definition within the current linkage unit (shared library or executable). This is a critical annotation:

- **Calls become direct**: No PLT stub needed (saves an indirect jump per call).
- **Data access avoids GOT**: Load the address PC-relative instead of through the Global Offset Table (saves a memory indirection).
- **Enables more aggressive optimization**: LLVM can assume the function won't be replaced, enabling better alias analysis around calls.

Internal and private linkage symbols are always implicitly `dso_local`. Symbols with hidden or protected visibility are also `dso_local`.

**Language design recommendation:** For a new language, the simplest and most optimization-friendly approach is:

1. Mark ALL internal symbols `private` or `internal` (automatically `dso_local`)
2. For public API symbols that won't be interposed, mark them `dso_local` with `hidden` visibility if they don't need to be part of the public ABI
3. Only use default visibility + `dso_preemptable` for symbols that genuinely need dynamic linking interposition
4. When building executables (not shared libraries), use `-fno-semantic-interposition` equivalent — mark everything `dso_local`

### 3.3 Performance Impact of Preemption

The cost of `dso_preemptable` vs `dso_local` varies by target, but on x86-64 ELF:

**Function calls:**
```asm
; dso_preemptable: call goes through PLT
call func@PLT         ; indirect: jmp *GOT_entry -> actual function
                      ; first call also triggers lazy binding overhead

; dso_local: direct call
call func             ; direct PC-relative call, no indirection
```

**Global variable access:**
```asm
; dso_preemptable: load address from GOT, then load value
mov  rax, [rip + var@GOTPCREL]   ; load pointer from GOT
mov  eax, [rax]                   ; load actual value (2 loads)

; dso_local: direct PC-relative access
mov  eax, [rip + var]             ; single load
```

The overhead per access is one extra memory indirection, which is typically an L1 cache hit in hot code but still costs a cycle and a micro-op. For call-heavy code, PLT overhead compounds.

---

## 4. Thread-Local Storage (TLS) Models

LLVM supports four TLS models that correspond to the ELF TLS models, listed from most expensive to cheapest:

### 4.1 `generaldynamic` (default)

```llvm
@tls_var = thread_local global i32 0
; or equivalently:
@tls_var = thread_local(generaldynamic) global i32 0
```

- Most general model. Works for any TLS variable in any context (shared library, executable, dynamically loaded).
- Generates a call to `__tls_get_addr` for each access (or a TLS descriptor call on AArch64).
- **Cost:** Function call overhead per access, plus dynamic linker resolution overhead.

### 4.2 `localdynamic`

```llvm
@tls_var = thread_local(localdynamic) global i32 0
```

- For variables only used within the current shared library.
- One call to `__tls_get_addr` to get the base of the module's TLS block, then cheap offsets for individual variables.
- **Cost:** One function call to get the base, then simple additions. Amortized well when accessing multiple TLS variables from the same module.

### 4.3 `initialexec`

```llvm
@tls_var = thread_local(initialexec) global i32 0
```

- For variables in modules that are part of the initial load set (not loaded via `dlopen`).
- The GOT contains the offset directly; no function call needed.
- **Cost:** One GOT load to get offset, then TP-relative access. No function call.
- **Restriction:** The module must be loaded at program start, not dynamically.

### 4.4 `localexec`

```llvm
@tls_var = thread_local(localexec) global i32 0
```

- For variables defined in the main executable, used only within it.
- The offset from the thread pointer is a link-time constant.
- **Cost:** A single TP-relative access with a constant offset. Essentially the same cost as accessing a regular global via a base register. This is the cheapest possible TLS access.

### 4.5 Performance Comparison

| Model | Access Cost (x86-64) | Restriction |
|-------|---------------------|-------------|
| `generaldynamic` | Function call (`__tls_get_addr`) | None |
| `localdynamic` | One function call + offset | Same shared library |
| `initialexec` | GOT load + TP-relative | Not `dlopen`'d |
| `localexec` | Constant offset from TP | Main executable only |

**Language design recommendation:**

- If your language compiles to an executable (not a shared library), use `localexec` for all TLS. This is the common case and the cheapest.
- If building shared libraries, use `initialexec` if you can guarantee the library is always part of the initial load set (i.e., linked at build time, not `dlopen`'d).
- Use `localdynamic` if you have many TLS variables accessed together (the base call is amortized).
- Only use `generaldynamic` if the code might be `dlopen`'d and you can't know at compile time.
- The linker can relax more general models to more specific ones (e.g., `generaldynamic` → `localexec` when linking an executable), but emitting the right model initially avoids relocation overhead and enables better codegen.

---

## 5. Module Structure and Link-Time Optimization

### 5.1 Full (Fat) LTO

Full LTO merges all LLVM bitcode modules into a single monolithic module, then runs the optimization pipeline on it. This enables:

- **Internalization**: The linker provides a list of symbols that must remain externally visible. Everything else is changed to `internal` linkage, which unlocks the full interprocedural optimization suite (constant propagation, dead argument elimination, calling convention changes, global variable optimizations).
- **Cross-module inlining**: With all code in one module, the inliner can inline any function into any caller.
- **Cross-module constant propagation**: IPSCCP can propagate constants across what were previously module boundaries.
- **Dead global elimination**: Unreachable `internal` globals (functions and variables) are deleted.
- **Global variable optimization (GlobalOpt)**: The pass can SRA (Scalar Replacement of Aggregates) on globals, track stores and loads to determine if a global is only ever stored once, shrink globals to booleans, and convert heap allocations to global memory.
- **Calling convention optimization**: GlobalOpt changes internal functions with C calling convention to `fastcc` when all callers are visible.

**Cost:** All IR must be in memory simultaneously. For large programs, this can require tens of GB of RAM and serial optimization time.

### 5.2 ThinLTO

ThinLTO achieves most of LTO's benefits while remaining scalable:

1. **Pre-link phase**: Each module is independently compiled with a simplified optimization pipeline (primarily canonicalization, not heavy optimization). A compact summary of each module is emitted alongside the bitcode.

2. **Thin link phase**: Summaries are merged into a combined summary index. Fast whole-program analysis determines:
   - Which functions to import cross-module (based on size heuristics and call graph)
   - Which symbols can be internalized
   - Other global summary-based decisions

3. **Post-link phase (parallel)**: Each module, now augmented with imported function definitions (as `available_externally`), is optimized in full and then code-generated. Modules are processed in parallel.

**Key optimizations ThinLTO enables:**

- **Function importing**: Small functions (typically <100 instructions) are imported as `available_externally` into calling modules, enabling inlining across module boundaries without merging all IR.
- **Internalization**: Symbols not referenced by other modules (determined from the combined index) are internalized.
- **Whole-program devirtualization**: Using type metadata summaries.
- **Dead symbol stripping**: Unreachable functions identified from the summary index.

**Language design interaction:** Your frontend's choice of module granularity matters:

- Emitting many small modules (e.g., one per function) may cause excessive overhead in the thin-link step.
- Emitting one huge module gives up parallelism.
- The sweet spot is typically one module per source file (as Clang and Rustc do), which gives ThinLTO good parallelism while keeping individual modules manageable.

### 5.3 How Internalization Works

During LTO (both full and thin), the linker provides LLVM with the set of "roots" — symbols that must remain externally visible (typically `main`, exported symbols, symbols referenced by dynamic loaders). The internalization pass then:

1. Iterates over all global values in the module
2. Any global not in the root set and not in `@llvm.used` or `@llvm.compiler.used` gets its linkage changed to `internal`
3. Subsequent optimization passes (GlobalOpt, IPSCCP, etc.) exploit the new `internal` linkage

This is enormously impactful because it retroactively gives all non-exported symbols the optimization benefits of `private`/`internal` linkage, even if they were originally `external`.

**Language design recommendation:** Design your language's compilation model to emit symbols with the most restrictive linkage that is *correct*, then rely on LTO internalization to further tighten things up. But don't rely on LTO alone — even without LTO, restrictive linkage helps the single-module optimizer.

### 5.4 `@llvm.used` and `@llvm.compiler.used`

```llvm
; Prevents the symbol from being deleted by the optimizer AND the linker
@llvm.used = appending global [1 x ptr] [ptr @must_keep_always]

; Prevents deletion by the optimizer, but the linker may discard it
@llvm.compiler.used = appending global [1 x ptr] [ptr @keep_during_compilation]
```

- `@llvm.used`: Globals listed here are unconditionally retained. They won't be optimized away, dead-stripped, or constant-merged. LTO internalization still applies but won't delete them.
- `@llvm.compiler.used`: Weaker — prevents compiler-level deletion but the linker's dead-stripping can still remove them.

**Language design recommendation:** Use `@llvm.used` sparingly — only for globals that must survive even with no visible references (e.g., exported FFI symbols, plugin entry points, globals referenced only from inline assembly). Every entry in `@llvm.used` is a global that cannot be dead-stripped.

---

## 6. Comdat Groups

```llvm
$group_name = comdat any

define linkonce_odr i32 @templated_func() comdat($group_name) { ... }
@vtable = linkonce_odr unnamed_addr constant [3 x ptr] [...] , comdat($group_name)
```

Comdats group related global objects so the linker either keeps or discards the entire group as a unit. Selection kinds:

- **`any`**: The linker picks one group and discards duplicates. Most common.
- **`exactmatch`**: All groups must have identical content; linker errors on mismatch.
- **`largest`**: The linker keeps the largest group.
- **`nodeduplicate`**: All groups are kept (no deduplication). No COMDAT behavior — used for special cases.
- **`samesize`**: All groups must have the same size; linker errors otherwise.

**Why comdats matter for optimization:**

On ELF and COFF, `linkonce_odr` / `weak_odr` functions and their associated data (vtables, RTTI, string constants) must be in the same comdat group to ensure atomic deduplication. Without comdat, the linker might keep a function from one TU but the vtable from another, leading to inconsistency.

**Language design recommendation:** Whenever you emit `linkonce_odr` functions (templates, generic instantiations), group them with their associated data in a comdat. This is required for correctness on COFF and best practice on ELF.

---

## 7. GlobalOpt: The Key Optimization Pass for Globals

The `GlobalOpt` pass (`lib/Transforms/IPO/GlobalOpt.cpp`) is LLVM's primary pass for optimizing global variables. Understanding what it does tells you exactly what to optimize for in your frontend:

### What GlobalOpt does to `internal` globals:

1. **Constant promotion**: If an internal global is only stored to once (with a constant), and that store dominates all loads, GlobalOpt marks it `constant` and replaces all loads with the stored value.

2. **SROA of globals**: If an internal global struct/array has fields accessed independently, GlobalOpt splits it into individual scalar globals. This enables further optimization of each field independently.

3. **Global-to-boolean shrinking**: If a global pointer is only ever compared to null or non-null, GlobalOpt replaces the pointer with a boolean.

4. **Malloc-to-global**: If a global pointer is always the result of a specific fixed-size malloc, GlobalOpt replaces the heap allocation with a global array and turns the pointer into a direct reference.

5. **Dead global elimination**: Unreferenced internal globals are deleted. Globals that are only stored to (never loaded) are deleted along with their stores.

6. **Calling convention change**: Internal functions with the C calling convention are changed to `fastcc`, which allows more efficient parameter passing and may enable tail call optimization.

7. **Cold calling convention**: Internal functions that are only called from cold code paths may be changed to `coldcc`.

### What GlobalOpt CANNOT do with `external` globals:

- Cannot prove a global is only stored once (external code might store)
- Cannot SROA an external global (external code might access the whole thing)
- Cannot delete an external global (external code might reference it)
- Cannot change calling convention (external callers exist)

**This is why `internal` linkage is so powerful — it gives GlobalOpt full visibility into all uses.**

---

## 8. Putting It All Together: Language Design Recommendations

### Priority 1: Maximize `internal`/`private` linkage (HIGH IMPACT)

- Emit every function and global that doesn't need to be visible outside the current compilation unit as `internal` or `private`.
- If your language has a module system, use it to inform linkage: unexported module members get `internal` linkage.
- Rely on LTO internalization to further tighten exported symbols when building final binaries.

### Priority 2: Mark all immutable data as `constant` (HIGH IMPACT)

- String literals, numeric tables, vtables for sealed types, enum variant tables, format strings — all `constant`.
- Combine with `unnamed_addr` (or `local_unnamed_addr`) to enable merging of duplicate constants.

### Priority 3: Use `dso_local` aggressively (MEDIUM-HIGH IMPACT)

- When building executables, mark everything `dso_local`.
- When building shared libraries, mark everything with `hidden` visibility unless it's part of the public ABI.
- Avoid symbol interposition semantics unless your language specifically requires it.

### Priority 4: Use `linkonce_odr` (not `linkonce`) for templates/generics (MEDIUM IMPACT)

- If your language has generics/templates that produce multiple identical definitions across TUs, use `linkonce_odr` with comdat groups.
- The `odr` suffix is the difference between "can inline" and "cannot inline."

### Priority 5: Design for LTO (MEDIUM IMPACT)

- Emit LLVM bitcode (or enable LTO in your toolchain) to unlock cross-module optimization.
- ThinLTO gives most of the benefit of full LTO with much better scalability.
- The combination of LTO internalization + GlobalOpt is extremely powerful — it retroactively applies `internal`-level optimization to most symbols in the program.

### Priority 6: Choose the right TLS model (LOW-MEDIUM IMPACT)

- For executables, default to `localexec`.
- For shared libraries, use `initialexec` when possible.
- Avoid `generaldynamic` unless necessary for `dlopen` compatibility.

### Priority 7: Use `unnamed_addr` / `local_unnamed_addr` on constants and functions (LOW IMPACT)

- Enables constant merging and ICF.
- Low per-item impact but compounds across a large program.

---

## 9. Interactions with Other Categories

### Globals × Exception Handling
If your language uses no exceptions (`nounwind` everywhere), all calls become non-throwing. This means GlobalOpt can more aggressively reason about stores to globals — a call cannot unwind and observe a partially-updated state. Combined with `internal` linkage, this enables more global-to-constant promotions.

### Globals × Calling Conventions
GlobalOpt automatically changes `internal` functions from `ccc` (C calling convention) to `fastcc`. This interacts with your calling convention choices: if you use `fastcc` for all internal functions from the start, GlobalOpt has less work to do but the effect is the same. The key enabler is `internal` linkage.

### Globals × Memory Model
If your language has a single-threaded mode, globals that would otherwise need atomic access can be plain loads/stores with `internal` linkage. This avoids the fence/atomic overhead and enables the optimizer to freely reorder and eliminate accesses.

### Globals × UB Contracts
If your language guarantees no data races on globals (e.g., globals are always behind locks, or are truly read-only after initialization), this justifies emitting non-atomic loads/stores and marking globals as `constant` after initialization (via `llvm.invariant.start`).

### Module Structure × Inlining
The biggest practical effect of LTO is expanded inlining. For hot call paths that cross module boundaries, LTO's cross-module inlining typically provides 5-20% speedup on real-world programs. ThinLTO's function import mechanism (`available_externally`) is specifically designed to enable this without full module merging.

---

## 10. Appendix: Quick Reference

### Global Variable Syntax

```llvm
@<name> = [Linkage] [PreemptionSpecifier] [Visibility] [DLLStorageClass]
          [ThreadLocal] [(unnamed_addr|local_unnamed_addr)]
          [AddrSpace] [ExternallyInitialized]
          <global | constant> <type> [<initializer>]
          [, section "name"] [, partition "name"]
          [, comdat [($name)]] [, align <alignment>]
          [, code_model "model"]
          (, !name !N)*
```

### Function Definition Syntax

```llvm
define [linkage] [PreemptionSpecifier] [visibility] [DLLStorageClass]
       [cconv] [ret attrs]
       <ResultType> @<FunctionName> ([argument list])
       [(unnamed_addr|local_unnamed_addr)]
       [AddrSpace] [fn Attrs] [section "name"]
       [partition "name"] [comdat [($name)]]
       [align N] [gc] [prefix Constant]
       [prologue Constant] [personality Constant]
       (!name !N)* { ... }
```

### Linkage Cheat Sheet

| Linkage | Can Inline? | Can Delete Unused? | Can Derive Properties? | GlobalOpt Full Power? |
|---------|------------|-------------------|----------------------|---------------------|
| `private` | Yes | Yes | Yes | Yes |
| `internal` | Yes | Yes | Yes | Yes |
| `external` | No (without LTO) | No | Yes (exact def) | No (external uses) |
| `linkonce_odr` | Yes | Yes | No (non-exact) | No |
| `weak_odr` | Yes | No | No (non-exact) | No |
| `linkonce` | **No** | Yes | No | No |
| `weak` | **No** | No | No | No |
| `available_externally` | Yes | Yes | No | No |
