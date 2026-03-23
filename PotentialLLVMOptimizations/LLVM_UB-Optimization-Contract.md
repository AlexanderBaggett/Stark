# 5. Undefined Behavior as an Optimization Contract

## LLVM's UB Hierarchy

LLVM has three tiers of undefined behavior, from weakest to strongest:

```
concrete value < freeze(poison) < undef < poison < immediate UB
```

**Immediate UB**: Division by zero, illegal memory access. Anything can happen. The optimizer may assume code paths leading to immediate UB are unreachable.

**Poison**: A deferred UB marker returned by instructions with violated flags (nsw, nuw, exact, etc.). Propagates through the value DAG — most instructions taking a poison input produce poison output. Becomes immediate UB only when it reaches a side-effecting operation (branch, store, return with `noundef`).

**The key insight for language design**: Poison exists specifically to allow speculative execution. An `add nsw` that overflows doesn't crash immediately — it produces poison, which is harmless unless it influences control flow or observable state. This means LLVM can hoist flagged operations out of loops without proving the loop executes.

---

## Complete Inventory of Poison-Generating Flags

### Arithmetic: `nsw` and `nuw`

**Applies to**: `add`, `sub`, `mul`, `shl`

```llvm
%r = add nsw i32 %a, %b   ; poison if signed overflow
%r = add nuw i32 %a, %b   ; poison if unsigned overflow  
%r = add nuw nsw i32 %a, %b ; poison if either
%r = shl nsw i32 %a, %b   ; poison if shifts out sign bits
%r = shl nuw i32 %a, %b   ; poison if shifts out any bits
```

**Optimizations enabled by `nsw`**:

1. **Induction variable widening** (highest impact). A 32-bit loop counter `i` used as a 64-bit index requires a sign extension each iteration. With `add nsw`, SCEV can prove the IV doesn't wrap, enabling promotion to 64-bit and eliminating the sext. This was the *original motivation* for nsw's creation. SCEV reported a 13% speedup on Eigen3 microbenchmarks from better nsw propagation.

2. **Comparison simplification**. `x + 1 >s x` → `true` (only valid with nsw; without it, INT_MAX + 1 wraps to INT_MIN).

3. **Loop trip count computation**. SCEV needs nsw/nuw to compute `(end - start) / step` without worrying about wraparound.

4. **Strength reduction**. Replacing `i * stride` with an accumulating add requires knowing the multiply doesn't wrap.

5. **Sign extension elimination**. `sext(add nsw (trunc x))` can simplify when nsw guarantees the truncated add doesn't overflow back.

```llvm
; WITHOUT nsw — SCEV cannot widen IV
loop:
  %i = phi i32 [0, %entry], [%i.next, %loop]
  %idx = sext i32 %i to i64              ; required every iteration
  %ptr = getelementptr i32, ptr %arr, i64 %idx
  %i.next = add i32 %i, 1                ; might wrap — SCEV must be conservative
  ...

; WITH nsw — IV widened to i64, sext eliminated
loop:
  %i = phi i64 [0, %entry], [%i.next, %loop]
  %ptr = getelementptr i32, ptr %arr, i64 %i
  %i.next = add nuw nsw i64 %i, 1        ; no wrap → safe to use i64 directly
  ...
```

**Optimizations enabled by `nuw`**:

1. **Zero extension elimination** (mirrors nsw/sext).
2. **Unsigned comparison simplification**: `x + 1 >u x` → `true`.
3. **Bounds check elimination**: If `x + offset` is nuw, then `x + offset >= x` is trivially true.

### Division/Shift: `exact`

**Applies to**: `udiv`, `sdiv`, `lshr`, `ashr`

```llvm
%r = sdiv exact i32 %a, 4   ; poison if %a is not evenly divisible by 4
%r = lshr exact i32 %a, 2   ; poison if any shifted-out bits are non-zero
```

**Optimizations enabled**:

1. **Reversibility**: `sdiv exact` can be reversed — `(x / 4) * 4 == x` is guaranteed true.
2. **Shift-to-divide lowering**: `sdiv exact i32 %x, 2` → `ashr exact i32 %x, 1` (no rounding correction needed).
3. **Pointer difference arithmetic**: `(p2 - p1) / sizeof(T)` uses exact because pointer subtraction in typed languages is always element-aligned.

### GEP Flags: `inbounds`, `nusw`, `nuw`

```llvm
%p = getelementptr inbounds i32, ptr %base, i64 %idx
; poison if result goes outside the allocated object

%p = getelementptr nusw i8, ptr %base, i64 %offset
; poison if (unsigned ptr) + (signed offset) wraps the address space

%p = getelementptr nuw i8, ptr %base, i64 %offset
; poison if (unsigned ptr) + (unsigned offset) wraps
```

**`inbounds`** implies `nusw`. It's the strongest GEP flag — enables alias analysis (two inbounds GEPs into different allocations can't alias) and proves non-null (inbounds GEP of non-null base with non-zero offset is non-null).

**`nuw`** (new in 2024) fixes two long-standing problems:
- Proves `ptr + offset >= ptr` for unsigned offsets → eliminates bounds checks.
- Proves field accesses can't reach *before* the struct start (e.g., `vec.elems[i]` can't access `vec.len` via negative `i`).

**`nusw`** (new in 2024) is the weaker component of `inbounds` — the wrapping guarantee without the full "must be within allocated object" requirement. Rust is experimenting with this as an alternative to `inbounds`, since it doesn't require the hard-to-formalize provenance semantics.

**Language design recommendation**: If your language has array indexing that's always non-negative (e.g., unsigned indices), emit `getelementptr inbounds nuw`. If you can't guarantee the result stays in-bounds of the allocation but can guarantee the arithmetic doesn't wrap, use `nusw nuw`.

### Truncation Flags: `nsw`, `nuw` on `trunc`

```llvm
%r = trunc nuw i32 %x to i16  ; poison if truncated bits aren't all zero
%r = trunc nsw i32 %x to i16  ; poison if truncated bits aren't all sign bits
```

New in 2024. The key optimization property:
- `zext(trunc nuw x)` → `x`
- `sext(trunc nsw x)` → `x`

Common in boolean i1↔i8 conversions and IV widening. Note: as of late 2024, some of the motivating folds are not yet fully implemented.

### Other Flags

**`or disjoint`** (2023): Poison if any bit position is set in *both* operands. Allows treating `or` as `add` (for addressing modes) or `xor` interchangeably. Introduced because InstCombine canonicalizes `add` → `or` when bits are disjoint, losing addition semantics.

```llvm
%r = or disjoint i32 %a, %b  ; acts as add, or, and xor simultaneously
```

**`zext nneg`** (2023): Poison if the source value is negative. Allows treating `zext nneg` as `sext` when profitable (e.g., on RISC-V which prefers sign extensions).

```llvm
%r = zext nneg i32 %x to i64  ; same as sext if %x >= 0
```

---

## The `freeze` Instruction

```llvm
%safe = freeze i32 %maybe_poison
```

`freeze` is the **escape hatch** for when you need to use a potentially-poison value in a context that would trigger immediate UB (branching, storing). It returns a non-deterministic but consistent value — all uses of a single `freeze` see the same arbitrary bit pattern.

**When a frontend needs `freeze`**:

1. **Uninitialized variables**: If your language allows reading uninitialized memory, the loaded value is `undef`. Before branching on it, you need `freeze`.

2. **Value-dependent control flow over untrusted input**: If the value might be poison (from a prior flagged operation) and you need to branch on it.

3. **Loop unswitching safety**: When hoisting a branch condition out of a loop, the condition might be poison on paths where the loop doesn't execute. The optimizer inserts `freeze` automatically in this case.

**Language design recommendation**: If your language initializes all variables (like Rust, Go, or Java), you almost never need to emit `freeze` manually. If your language guarantees no-overflow semantics through runtime checks, you'll never produce poison from arithmetic, and `freeze` is unnecessary. The optimizer will insert `freeze` where it needs to internally.

---

## The Safety↔Optimization Tradeoff Per Flag

This is the central language design question: for each flag, what do you gain and what does it cost to guarantee the precondition?

### `nsw` on signed arithmetic

| Strategy | What you emit | Optimization | Safety |
|---|---|---|---|
| UB on overflow (C model) | `add nsw` | Full SCEV/IV opts | Unsound if overflow occurs |
| Checked arithmetic | `llvm.sadd.with.overflow` → branch | **None from nsw** (no flag emitted) | Fully safe, traps on overflow |
| Checked, then assert | `llvm.sadd.with.overflow` → branch to unreachable on overflow path, then use result with `add nsw` | Full opts (optimizer sees the check eliminates overflow) | Safe — check happens first |
| Wrapping arithmetic | `add` (no flags) | Loses IV widening, trip count, comparison folding | Fully safe, wraps |

**The cost of dropping `nsw`**: LLVM's official frontend performance tips document explicitly calls out nsw as having high impact. Without it, SCEV falls back to conservative analysis for loop IVs. The SCEV flag transfer mechanism requires that poison from the flagged instruction must reach a side-effecting instruction (return with `noundef`, a store, etc.) to prove the flag is valid. Without `noundef` on function returns, SCEV can't even transfer nsw from standalone arithmetic.

**Recommendation**: The "checked, then assert" strategy is ideal. Emit the overflow check, branch to an abort/panic on overflow, and then use `add nsw` on the success path. This gives you both safety *and* full optimization. This is essentially what Rust does for debug builds (minus the nsw emission).

**Critical caveat from LLVM's frontend tips**: "Avoid using arithmetic intrinsics unless you are *required* by your source language specification... The optimizer is quite good at reasoning about general control flow and arithmetic, it is not anywhere near as strong at reasoning about the various intrinsics." Emit `add nsw` + branch-on-overflow checks rather than the overflow intrinsics where possible.

### `nuw` on unsigned arithmetic

Same tradeoff matrix as nsw. The unsigned case is simpler because many languages naturally use unsigned types for sizes/indices, and you can prove nuw from type-level guarantees (e.g., an index that comes from a bounds check can't overflow when added to a base pointer).

### `exact` on division/shift

Lower stakes. Useful mainly for pointer difference computations and known-aligned divisions. If your language has a "division is always exact" context (e.g., dividing a byte offset by type size), emit it. Otherwise, omit — the optimizer will infer it when it can.

### `inbounds` / `nuw` on GEP

**Very high value, low risk**. If your language has:
- Array bounds checking → `inbounds` is always sound after the check passes
- Unsigned indices → `nuw` is sound  
- Struct field access → always `inbounds nuw` (field offsets are known non-negative and within the struct)

### `nsw`/`nuw` on `shl`

`shl nsw` means no signed bits are shifted out. `shl nuw` means no bits at all are shifted out. Primary use: multiply-by-power-of-2 patterns where the optimizer converts `mul nsw x, 8` → `shl nsw x, 3`.

---

## `noundef` and Flag Propagation

A subtle but crucial interaction: SCEV can only transfer nsw/nuw flags from IR instructions to its internal representation when it can prove that violating the flag causes UB (not just poison). This requires that the poison value *must* reach a side-effecting instruction.

```llvm
; SCEV CANNOT transfer nsw here — function might just return poison
define i32 @no_transfer(i32 %a, i32 %b) {
  %r = add nsw i32 %a, %b
  ret i32 %r
}

; SCEV CAN transfer nsw — returning poison with noundef is UB
define noundef i32 @can_transfer(i32 %a, i32 %b) {
  %r = add nsw i32 %a, %b
  ret i32 %r
}
```

**Language design recommendation**: Mark function return values as `noundef` whenever your language guarantees defined return values (which is virtually always). This is *free* optimization enablement.

---

## Impact Ranking (within this category)

1. **`nsw`/`nuw` on arithmetic** — Highest impact. Unlocks SCEV, IV widening, trip count computation, comparison simplification. The LLVM frontend tips document specifically highlights this.

2. **`inbounds` on GEP** — High impact. Enables alias analysis disambiguation, null pointer inference, and SCEV reasoning about pointer-based IVs.

3. **`nuw` on GEP** (new) — Medium-high impact. Fixes long-standing bounds check elimination failures.

4. **`noundef` on returns/parameters** — Medium impact but *free*. Enables SCEV flag transfer.

5. **`exact` on div/shift** — Low-medium impact. Mainly helps specific patterns (pointer arithmetic, known-aligned access).

6. **`or disjoint`**, **`zext nneg`**, **`trunc nsw`/`nuw`** — Low-medium impact. Prevent information loss during canonicalization. The frontend rarely needs to emit these directly; the optimizer infers them.

7. **`freeze`** — Not an optimization enabler; it's a safety mechanism. Language design should minimize the need for it by initializing values and checking before flagging.

---

## Cross-Category Interactions

- **nsw + `noundef`**: As described above, `noundef` is required for SCEV to fully utilize nsw flags. Always pair them.

- **nsw + loop metadata (`mustprogress`)**: A loop marked `mustprogress` with an `add nsw` IV gives SCEV maximum information — it knows the loop terminates and the IV doesn't wrap.

- **`inbounds` + `noalias`**: Inbounds GEPs into different `noalias` allocations are trivially non-aliasing. Together they give LLVM's alias analysis the strongest possible information.

- **nsw + exception handling model**: If you choose no-exceptions (`nounwind` everywhere), every call becomes a simple branch rather than an invoke. This means the optimizer doesn't need to reason about exception edges when propagating nsw/nuw through call boundaries. The combination of nounwind + nsw + mustprogress on loops is the "maximum optimization" trifecta.

- **`nuw` on GEP + calling conventions**: The `nuw` GEP flag helps prove pointer offsets are non-negative, which interacts with `fastcc`'s ability to pass pointers in registers — if the optimizer can prove aliasing facts about the pointers being passed, it can avoid spills around calls.

---

## Gotchas and Known Issues

1. **Flag preservation through optimization**: Optimizers *drop* nsw/nuw flags when they can't prove the flag survives a transform. This is conservative and correct, but means flags emitted by the frontend may not survive to codegen. This is expected — emit all flags you can justify.

2. **`freeze` can block optimization**: Inserting `freeze` stops poison propagation, which means downstream optimizations can't assume the value has any particular property. Minimize freeze usage.

3. **`undef` is being phased out**: LLVM is moving toward a model where only `poison` and `freeze` exist. Frontends should prefer `poison` over `undef` for "don't care" values (e.g., uninitialized allocas). Use `freeze(poison)` when you need a non-deterministic but non-UB value.

4. **Alive2 is the truth**: When uncertain whether a flag is sound, verify with [Alive2](https://alive2.llvm.org/ce/). It's the authoritative tool for checking LLVM transform correctness with respect to poison semantics.

5. **The `sdiv exact` constant folding issue** (reported Jan 2026): `sdiv exact i32 1, 2` was being constant-folded to 0 instead of poison. While likely a minor edge case, it illustrates that flag semantics in LLVM are still being refined at the edges.

6. **SCEV flag semantics differ from IR flag semantics**: In IR, `add nsw` on a particular instruction means *that specific add* doesn't overflow. In SCEV, flags on an add recurrence `{start,+,step}<nsw>` mean the recurrence doesn't wrap *over the loop's actual iteration count*. This distinction matters when transferring flags between representations.
