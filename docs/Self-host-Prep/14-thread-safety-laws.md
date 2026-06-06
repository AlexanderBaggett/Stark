# Phase 14 - Thread-Safety Laws (`Transferable` / `Shareable`)

Status: **design proposal — drafted by Alexander; awaiting design lock. No
implementation.** The original draft's three open questions are resolved into
specification in this revision (§8 auto-deny, §9 conditional grants, §12
diagnostics); those resolutions are proposed and need Alexander's confirmation as
part of the lock. Original draft preserved in git history
(`stark-thread-safety-laws.md`, commit `b6c2fec`).

Coordination consumer note: doc `22` locks the self-hosting-facing consumers of
these laws as captured thread payloads, `System.Threading.Synchronized<T>` /
`Locked<T>`, and MPSC channels. That does not imply a broader thread framework.

This phase specifies how Stark guarantees memory safety across thread boundaries:
two compiler-proven properties — the **`Transferable`** and **`Shareable`** laws —
that govern whether a value may be moved to, or shared with, another thread. The
properties are derived structurally by default and adjusted at the **field level**
through `[Grant(...)]` and `[Deny(...)]` attributes. Functions of any kind consume
the properties through `where` predicate constraints.

This is the Stark analogue of Rust's `Send`/`Sync`, with two deliberate
divergences: the markers are **laws** (predicates the compiler discharges) rather
than traits a type implements, and overrides attach to the **field that justifies
them** rather than to the type as a whole.

## 1. Goal

Give Stark the type-level vocabulary that every future concurrency feature consumes:

1. `Transferable(T)` — ownership of a `T` may move to another thread,
2. `Shareable(T)` — a borrow of `T` may be accessed from multiple threads
   concurrently,
3. structural derivation + field-level overrides + `where`-predicate consumption,

so that when the parked thread-ownership model (doc `12` pre-atomics revision),
`Synchronized<T>`, or channels land, their safety rules are one-line `where` constraints
instead of bespoke machinery.

## 2. Current State (verified)

- **No enforcement point exists yet.** Today's `Thread` entry is a non-capturing
  `fnptr`, so no user value ever crosses a thread boundary; the laws have nothing
  to check until closure-entry threads, channels, or `Synchronized<T>` exist. This design
  is deliberately specification-ahead-of-need.
- **The attribute machinery exists.** Struct/record members already accept
  attribute lists in the grammar (`structMember : attributeList* (...)`), and the
  innate attribute registry (doc `11`, `[Ok]`/`[Err]`) is the registration point
  for `[Grant]`/`[Deny]`.
- **The predicate-contract grammar shape exists.** `where disjoint(a, b)` /
  `where overlap(a, b)` are already predicate-style `where` contracts; law
  predicates (`where Transferable(T)`) join that family rather than the
  trait-bound family (`where T: Trait`).
- **Atomics (doc `12`) are the first intrinsically `Shareable` types** — they are
  the primitive that makes shared mutation safe, and receive both laws by
  intrinsic grant when this design lands.
- The `shared T` access qualifier and `capture(unsafe shared x)` exist in the
  grammar with minimal semantics; this design either absorbs or retires them
  (OQ-TS2).

## 3. Motivation

A systems language with an ownership/borrow model must prevent data races at
compile time. Two distinct guarantees are required:

1. that a value can be **moved** from one thread to another without leaving behind
   aliases that violate ownership,
2. that a value can be **shared by reference** across threads without permitting
   concurrent mutation of non-synchronized state.

Rust solves this with the `Send` and `Sync` auto-traits. That model is sound, but
it concentrates both opt-outs (`impl !Send`) and unsafe assertions
(`unsafe impl Sync`) at the type level, detached from the specific member that
causes the property to hold or fail. Stark already expresses compiler-proven
properties as **laws** and already supports C#-style **attributes**, so this design
expresses thread-safety in those existing terms and localizes every override to its
responsible field.

## 4. The Two Laws

### 4.1 `Transferable`

A type `T` satisfies `Transferable` if ownership of a `T` value may be moved to
another thread. (Rust: `Send`.)

### 4.2 `Shareable`

A type `T` satisfies `Shareable` if a shared borrow of `T` may be accessed from
another thread concurrently. (Rust: `Sync`.)

Both are **laws** in the Stark sense: properties the compiler proves about a type,
not interfaces a type implements. They never appear in interface position on a type
declaration — `struct Pool : Transferable` is not a thing; `where Transferable(T)`
is.

## 5. Structural Derivation

By default both laws are derived structurally. A type satisfies a law if and only
if every one of its fields satisfies that law.

```stark
public struct Counter
{
    i32[min max] Value;
}
// Transferable(Counter) and Shareable(Counter) both hold,
// because i32[min max] satisfies both.
```

- A type with no fields satisfies both laws vacuously.
- Generic types propagate the requirement to their parameters:
  `Transferable(Pair<A, B>)` holds iff `Transferable(A)` and `Transferable(B)`
  hold. This conditional derivation is automatic and requires no annotation.
- No syntax is written for the common case. Derivation is silent.

```stark
public struct Pair<A, B>
{
    A First;
    B Second;
}
// Transferable(Pair<A, B>) iff Transferable(A) and Transferable(B).
// Shareable likewise.
```

## 6. Field-Level Overrides

When structural derivation is wrong — either too permissive or too restrictive —
the result is corrected with an attribute placed on the **field responsible** for
the discrepancy.

### 6.1 `[Deny(Law)]`

Removes a field's contribution to a law, forcing the enclosing type to fail that
law regardless of structural derivation. Used when a field's type is nominally fine
but its semantics are not.

```stark
public struct SessionToken
{
    // Structurally just an integer, but it indexes a thread-local table:
    // moving or sharing it across threads would dangle.
    [Deny(Transferable)]
    [Deny(Shareable)]
    i64[min max] LocalSlot;
}
// Neither law holds for SessionToken: the LocalSlot field denies both.
```

### 6.2 `[Grant(Law)]`

Asserts that a field satisfies a law the compiler cannot prove on its own. This is
the safety-critical, audited assertion — the equivalent of Rust's `unsafe impl`.
The author takes responsibility for the invariant, and the justification lives next
to the field that needs it.

```stark
public struct VerifiedHandle
{
    // The platform guarantees this handle is valid process-wide and its target
    // is never reclaimed; asserting cross-thread safety is the author's
    // audited responsibility.
    [Grant(Transferable)]
    [Grant(Shareable)]
    rawptr<u8[0 max]> Raw;
}
// Both laws hold for VerifiedHandle despite the raw pointer (which would
// otherwise auto-deny, §8).
```

### 6.3 Type-Level Form

For zero-field or opaque types where no field carries the justification, the same
attribute may ride on the type declaration:

```stark
[Grant(Transferable)]
public struct OpaqueToken
{
}
```

This is the only position in which a thread-safety attribute may appear off of a
field, and is reserved for types whose representation is not expressed as ordinary
fields.

## 7. Resolution Rules

For a given law `L` and type `T`, the compiler computes `L(T)` as follows:

1. If a type-level `[Grant(L)]` is present, `L(T)` holds. (Audited assertion.)
2. Otherwise, if a type-level `[Deny(L)]` is present, `L(T)` fails.
3. Otherwise, `L(T)` holds if and only if, for every field `f`:
   - `f` carries `[Grant(L)]` (conditional grants per §9 must have their condition
     hold), **or**
   - `f` carries no attribute for `L` and `L(typeof f)` holds (structurally or by
     §8 defaults),
   - and no field carries `[Deny(L)]`.

A single `[Deny(L)]` on any field is sufficient to make `L(T)` fail, unless a
type-level `[Grant(L)]` overrides it (rule 1). Conflicting attributes on the *same*
field (`[Grant(L)]` and `[Deny(L)]` together) are a compile error (STK3050, §12).

`Grant` is the only construct that introduces a proof obligation the compiler does
not verify; all other paths are mechanically checked.

## 8. Default Law Status of Built-in Types (resolved — was Open Question 2)

> **Resolution (proposed):** unsafe-reference primitives auto-deny both laws;
> everything else derives structurally. Explicit `[Deny]` on raw-pointer fields is
> therefore never needed — the common case is silent and safe.

| Type family | `Transferable` | `Shareable` | Why |
|---|---|---|---|
| Integers, floats, `bool`, text views' owned forms, enums of these | structural | structural | Plain data |
| Owned aggregates (struct/record/enum) | structural (all fields) | structural (all fields) | §5 |
| `rawptr<T>` / `rawmutptr<T>` | **deny** | **deny** | A raw pointer's target has unknown ownership; crossing threads is exactly the use-after-free / race hazard. Override with field-level `[Grant]` + justification (§6.2). |
| Stored borrows (`storeborrow` fields) | **deny** | **deny** | Point into another invocation's storage; that storage's thread owns it. |
| `fnptr<...>` | structural (holds) | structural (holds) | Code addresses carry no state. |
| Closures | structural over the **captured environment** | structural over captures | A closure is as safe as what it captured. |
| Atomics (doc `12`) | **grant** (intrinsic) | **grant** (intrinsic) | Their operations are the definition of safe shared mutation. |
| `Synchronized<T>` / sync containers (future) | structural | **conditional grant** (§9) | The canonical conditional case. |

The practical effect: the worked example's `Connection` (§14) needs **no
attributes** to be correctly non-transferable — the raw pointer field already
denies — and the attribute surface is reserved for the two interesting cases:
auditable overrides (`[Grant]`) and semantic denials on innocent-looking fields
(`[Deny]`).

## 9. Conditional Grants (resolved — was Open Question 1)

> **Resolution (proposed):** conditional grants are required, not optional — the
> canonical synchronization container cannot be expressed without them. Syntax:
> the grant attribute takes a `where` law predicate.

A `Synchronized<T>` makes concurrent shared access safe **only if** `T` itself may be
owned by whichever thread holds the lock (a locking thread can swap the payload
out, which is a transfer). Unconditional `[Grant(Shareable)]` would be unsound;
no grant at all makes `Synchronized` useless. The grant must be conditional:

```stark
public struct Synchronized<T>
{
    // Concurrent shared access is safe because the lock serializes it —
    // provided T can be owned by whichever thread acquires the lock.
    [Grant(Shareable) where Transferable(T)]
    T Payload;

    // lock state fields...
}
// Shareable(Synchronized<T>) iff Transferable(T).
// Transferable(Synchronized<T>) is structural: iff Transferable(T).
```

Resolution-rule integration (§7, rule 3): a conditional `[Grant(L) where P(U)]` on
a field counts as a grant exactly when `P(U)` holds for the concrete instantiation;
otherwise the field falls back to structural derivation (which for `Synchronized`'s
payload field means the law fails — the safe default).

This composes with §5's generic propagation with no extra rules: the condition is
just another law predicate, discharged the same way at instantiation time.

## 10. Consuming the Laws in Functions

A function requires a law by stating it as a **predicate constraint** in a `where`
clause — the same clause family as `where disjoint(a, b)`, reflecting that laws are
discharged by the compiler rather than implemented by the type. This works
uniformly across all four function kinds.

```stark
fn void RunWorker<T>(T payload) where Transferable(T)
{
    // may hand `payload` to another thread's execution
}

finite void Broadcast<T>(borrow T value) where Shareable(T)
{
    // may expose `value` to concurrent readers
}

// Multiple constraints combine conjunctively:
fn void Distribute<T>(T value) where Transferable(T), Shareable(T)
{
}
```

A call that supplies a type for which the required law does not hold is a compile
error **at the call site**, naming the responsible field (§12).

## 11. Enforcement at Thread Boundaries

The laws are checked wherever data crosses a thread boundary. No such boundary
exists in today's `fnptr`-entry threads; the concrete enforcement points arrive
with the features that consume this design:

| Future feature (parked / planned) | Required law |
|---|---|
| Closure-entry owning threads (`Thread<T>.Start(heap capture(move x) ...)`) | every moved capture: `Transferable` |
| Scoped threads (borrowing) | borrowed captures: `Shareable` for shared borrows; exclusive mutable borrows rely on the borrow checker's existing exclusivity, lifetimes bounded by the scope |
| Channels (`Send(value)`) | `Transferable(T)` |
| `Synchronized<T>` / sync containers as statics | container itself `Shareable` (via §9) |
| `static` declarations reachable by multiple threads | `Shareable` of the static's type |

Until one of these lands, the laws are still independently useful: library authors
may write `where Transferable(T)` constraints on their own APIs, and the compiler
discharges them — the constraint vocabulary precedes its enforcement points.

## 12. Diagnostics (resolved — was Open Question 3)

> **Resolution (proposed):** law failures name the **responsible field chain**, not
> just the type; conflict and malformed-attribute errors follow the established
> attribute diagnostics style (STK3042/STK3043 precedent).

Proposed codes (final numbers assigned at implementation):

| Code | Meaning |
|---|---|
| STK3049 | Law constraint not satisfied (call site / boundary); names the field chain responsible |
| STK3050 | Conflicting or malformed law attributes (`[Grant(L)]` + `[Deny(L)]` on one field; unknown law name; arguments where none allowed) |

### 12.1 Law failure — field chain named

```text
error STK3049 [type-check]: 'Connection' does not satisfy Transferable
   8 |     RunWorker(connection);
     |     ^ RunWorker requires `where Transferable(T)` with T = Connection
  reason: Connection.Raw is 'rawptr<u8[0 max]>', and raw pointers are never
          thread-transferable by default
  help: if the pointer's target is provably safe to hand to another thread,
        assert it on the responsible field:
            [Grant(Transferable)]
            rawptr<u8[0 max]> Raw;
        otherwise keep the work on this thread.
```

For nested failures the chain extends: `Pool.Connections -> Connection.Raw` — the
deepest denying field is always named, because the field-level design makes the
cause precise.

### 12.2 Conflict

```text
error STK3050 [type-check]: field 'Cache.Inner' both grants and denies Shareable
  12 |     [Grant(Shareable)]
  13 |     [Deny(Shareable)]
     |     ^ conflicting law attributes on one field
  help: keep exactly one; a grant overrides structural derivation, a deny forces failure.
```

## 13. Comparison to Rust

| Concept | Rust | Stark |
|---|---|---|
| Move across threads | `Send` | `Transferable` law |
| Share a reference across threads | `Sync` | `Shareable` law |
| Auto-derivation | structural auto-trait | structural derivation (§5) |
| Raw pointers | `!Send`/`!Sync` by default | auto-deny (§8) |
| Opt out | `impl !Send for T` (type-level) | `[Deny(Transferable)]` (field-level) |
| Unsafe assertion | `unsafe impl Sync for T` (type-level) | `[Grant(Shareable)]` (field-level) |
| Conditional impl | `unsafe impl<T: Send> Sync for Mutex<T>` | `[Grant(Shareable) where Transferable(T)]` on `Synchronized<T>` (§9) |
| Require in a function | `where T: Send` | `where Transferable(T)` |
| Conceptual model | marker trait the type implements | law the compiler discharges |
| Override location | type | responsible field |

The two intentional divergences:

- **Laws, not traits.** `Transferable(T)` is a predicate the compiler proves,
  written in predicate position, never in interface position on the type. This
  prevents the "looks like an inherited interface" reading that a trait-style
  `T : Transferable` syntax would invite.
- **Field-level overrides.** Both the opt-out and the unsafe assertion attach to
  the field that justifies them, so the audit surface is the member rather than the
  type. The cost is slightly more verbosity than Rust's single line per type; the
  benefit is that the reason for a property is always co-located with its cause.

## 14. Worked Example

```stark
// A handle wrapping a non-thread-safe foreign resource.
// No attributes needed: the raw pointer auto-denies both laws (§8).
public struct Connection
{
    rawptr<u8[0 max]> Raw;
    i64[min max] Id;          // structurally fine; has no effect on the result
}

// A connection pool that makes concurrent shared access safe via a lock.
// Synchronized's own conditional grant (§9) does the work; Pool needs no attributes —
// but Shareable(Pool) requires Transferable(Connection), which fails...
public struct Pool
{
    Synchronized<Connection> Guard;
}

// ...so the author audits the platform contract and grants it on the
// responsible field of Connection:
public struct AuditedConnection
{
    // Platform guarantees the target is process-global and never reclaimed.
    [Grant(Transferable)]
    rawptr<u8[0 max]> Raw;
    i64[min max] Id;
}

public struct AuditedPool
{
    Synchronized<AuditedConnection> Guard;
}
// Shareable(AuditedPool): structural -> Shareable(Synchronized<AuditedConnection>)
//   -> conditional grant -> Transferable(AuditedConnection) -> holds via the
//   field-level grant. The full justification chain is readable in source.

// Generic worker: requires the moved payload be thread-transferable.
fn void RunWorker<T>(T payload) where Transferable(T)
{
    // hand payload to another thread (enforcement point per §11)
}

fn void Demo()
{
    stack AuditedConnection connection = new AuditedConnection()
    {
        Raw = null, Id = 1
    };

    RunWorker(connection);          // OK: Transferable(AuditedConnection) holds

    stack Connection raw = new Connection()
    {
        Raw = null, Id = 2
    };

    RunWorker(raw);                 // ERROR STK3049: Connection.Raw denies Transferable
    return;
}
```

## 15. Work Breakdown (TS*)

TDD-first, in dependency order. Nothing starts until the design is locked. TS06
additionally needs a real enforcement point (a consumer feature) to be meaningful
end-to-end, but TS01–TS05 are independently implementable and testable.

| ID | Item | Status |
|---|---|---|
| TS01 | Grammar: law predicates as a `where`-contract alternative (`where Transferable(T)`); `[Grant]`/`[Deny]` registered as innate attributes on fields + type declarations; regen | not started |
| TS02 | Law registry + structural derivation engine (per-type law computation, cached; generic propagation at instantiation) | not started |
| TS03 | Field/type-level `[Grant]`/`[Deny]` resolution rules (§7) + conflict diagnostics (STK3050) | not started |
| TS04 | Built-in defaults (§8): auto-deny for raw pointers and stored borrows; intrinsic grants for atomics | not started |
| TS05 | Conditional grants (`[Grant(L) where P(T)]`, §9) + instantiation-time discharge | not started |
| TS06 | `where` law predicates on functions: call-site checking + STK3049 field-chain diagnostics | not started |
| TS07 | Enforcement wiring at thread boundaries as consumer features land (parked thread model / channels / Synchronized / shareable statics) | blocked on consumers |
| TS08 | Tests (derivation, overrides, conditional grants, diagnostics) + user-facing docs + doc/roadmap sync | not started |

## 16. Open Questions (OQ-TS*)

| ID | Question | Notes / lean |
|---|---|---|
| OQ-TS1 | Conditional grant spelling | `[Grant(Shareable) where Transferable(T)]` (proposed, §9) vs `[Grant(Shareable, when: Transferable(T))]`. Lean: the `where` form — it reuses the existing predicate-contract reading. |
| OQ-TS2 | Fate of `shared T` and `capture(unsafe shared x)` | These predate the laws. Lean: retire `shared T` (no semantics today, superseded by `Shareable`); keep `capture(unsafe shared x)` as the unsafe escape hatch whose safe replacement is a `Shareable`-satisfying type. Carried from doc 12's parked OQ-TH5. |
| OQ-TS3 | Law extensibility | Are `Transferable`/`Shareable` the only compiler-known laws, or does this become a general law registry (e.g. future `Persistable`, `Relocatable`)? Lean: closed set of two until a third real need exists. |
| OQ-TS4 | `[Deny]` on generic parameters | Can a generic type deny a law for a specific parameter position (`[Deny(Shareable)] T Payload;`)? Lean: yes — it is just a field attribute; no special rule needed. Confirm during TS03. |

## 17. Relationship to Existing Docs

- **Doc `12` (atomics)** — atomic types are the first intrinsic grants (§8); the
  parked thread-ownership model (pre-atomics doc 12, git history) is the primary
  future consumer of enforcement (§11).
- **Doc `11` (error propagation)** — established the innate attribute registry and
  its diagnostics style (STK3042/STK3043), which `[Grant]`/`[Deny]` and STK3050
  follow.
- **Doc `13` (comptime)** — no interaction; laws are about runtime thread
  boundaries.
- `01-language-feature-gaps.md` **L10** / `02-stdlib-gaps.md` **S16** — this design
  is the language-side prerequisite for the threading/sync work both entries track.
- **Self-hosting** — not required: the compiler port is single-threaded. Like
  atomics, this is product-surface design, sequenced behind self-hosting blockers.
