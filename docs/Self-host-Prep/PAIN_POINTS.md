# Stark Self-Hosting — Language Ergonomics Pain-Point Log

Living, append-only log of friction encountered while executing the self-host roadmap.
Focus: things about **Stark itself** (language/stdlib/tooling) that made a task harder
than it should have been. Per-subagent logs are written to `PAIN_POINTS.<task>.md` and
consolidated here in the final phase. Never delete or overwrite earlier entries.

Entry format:

```
## [short title]
- **Task:** which roadmap task / file
- **What happened:** the friction, concretely
- **Why it hurt:** what made it harder than it should have been
- **Workaround:** what you did instead (if anything)
- **Suggested fix:** a language/stdlib/tooling change that would prevent it (if any)
```

---

## `Location()` is silently coupled to package-image record serialization
- **Task:** Cross-module / F1–F6 compiler work; regression surfaced while wiring `System.Testing.ContainsElement`
- **What happened:** Changing what file path `TypeChecker.Location()` stamps (to give non-root bodies correct per-module diagnostic file paths — the "F2" change) silently desynced the package-image *template member-call serialization*. `Location()` feeds not just diagnostics but the `BoundOperation` / typed-template-body records the package image matches on, so an app-side `List<i32>.Push` stopped binding to its serialized ordinal and crashed lowering.
- **Why it hurt:** Nothing signals that `Location()` is load-bearing for serialization. A change that is obviously "just diagnostics" silently breaks the package-image consumption path, with no compile-time or type-level guard. Two semantically different concerns (where to *point an error* vs. what *identity to serialize a record under*) share one method.
- **Workaround:** Reverted the diagnostic-location improvement entirely; correct per-module attribution now needs a record-location-vs-diagnostic-location split.
- **Suggested fix:** Separate "diagnostic source location" from "record identity location" in the type-check model so they can evolve independently; or key serialized template member-calls by a stable structural ordinal that does not embed a file path at all.
- **Update (partly resolved):** later investigation corrected the framing — the package-image *serialized* path is already file-path-independent (ordinals by syntactic index; signature match by `{line}:{column}`), so the fear above does not hold. The *real* coupling was the in-process `BoundOperationKey.FilePath` lowering key, now removed (matches by `(EnclosingFunctionName, Line, Column)`), so a future module-aware `Location()` can no longer desync the binding. STK9999 on this path is also now enriched (next entry). Still open: making `Location()` itself module-aware. See [BUGS.md](BUGS.md) "BoundOperationKey.FilePath" (step 1 landed, step 2 deferred).

## STK9999 internal-invariant errors are opaque
- **Task:** Diagnosing the package-image member-call regression
- **What happened:** The failure surfaced as `STK9999 [lower-mir]: Imported typed-template member call was accepted but did not bind to serialized member-call facts`.
- **Why it hurt:** It's an internal invariant-violation string, not an actionable diagnostic — it names neither the offending call site in user terms, the expected vs. found ordinal, nor which package/template desynced. Debugging required reading the lowerer source and bisecting compiler commits.
- **Workaround:** Manual bisect across compiler commits + revert-tests to isolate the responsible change.
- **Suggested fix:** When an internal lowering invariant trips on imported-template binding, include the template name, the member-call ordinal, the available ordinals, and the source span — enough to point at the producing build, not just assert failure.
- **Resolved:** the three lower-mir binding-failure sites now emit the call-site rendering, the requested ordinal, the receiver/argument count, and the sorted available ordinals (`MidLevelIrLowering.FunctionMirBuilder.ImportedTemplateLowerer.cs`) instead of a bare invariant string.

## No generic `Equatable<T>` usable for stdlib element predicates
- **Task:** `System.Testing.ContainsElement` / `SequenceEqual` (roadmap 100 / Testing.stark)
- **What happened:** The canonical `Assert.Contains(collection, item)` had to be written as one overload per concrete element type (`i32`/`i64`/`u32`/`u64`/`ascii`) instead of a single `ContainsElement<T>`, because there is no `Equatable<T>` constraint imported into `Testing.stark` to give a generic `T` an `==`.
- **Why it hurt:** A textbook generic helper became five near-identical copies; adding a new element type means another copy. Pure boilerplate the type system should absorb.
- **Workaround:** Hand-wrote the concrete overload set, mirroring the existing `Equal(...)` overloads.
- **Suggested fix:** Make a canonical `Equatable<T>` (and `Comparable<T>`) contract importable into general code so `where Equatable(T)` element predicates can be written once.
- **Resolved (differently than proposed):** the contracts already existed; the real blocker was that `==` does not route through a trait and primitives have no `Eq` impl. The five overloads collapsed into a single generic `ContainsElement<T>(borrow T[] values, T expected) where Copyable(T)` (and `SequenceEqual<T>`) that calls `System.Collections.DictionaryKey.Equals` directly — see the two later entries ("Doctrines are not usable as `where` bounds" and "Going generic forces `borrow T`…"). A true `where T: Eq` with `==` on a generic remains future work (FOLLOWUP #54).

## Field name shadows same-named accessor method (STK3008)
- **Task:** `System.IO.Path.HasExtension` (roadmap-adjacent / Path.stark)
- **What happened:** `facts.HasExtension()` does not compile — `PathFacts` has a `bool HasExtension` field, and member access resolves the field first, so calling it errors with `STK3008: member 'HasExtension' of type 'bool' is not callable`. `IsRooted()` works only because its backing field has a different name (`RootLength`).
- **Why it hurt:** A perfectly natural accessor name silently collides with its own backing field, and the error ("not callable") describes the symptom, not the cause (name collision). Inconsistent: the pattern works for some accessors and not others purely by field-naming accident.
- **Workaround:** Free functions read the field directly (`facts.HasExtension`) instead of going through an accessor method.
- **Suggested fix:** Either let a method and field share a name disambiguated by call syntax, or have the diagnostic say "a field named `HasExtension` shadows this method — rename one."
- **Partly resolved (diagnostic only):** STK3008 now appends "a field named 'X' shadows a same-named method — rename one to disambiguate" when the shadow is detected. The underlying resolution is unchanged — the field still binds before the method table, so `facts.HasExtension()` still does not call the method; only the error now names the cause.

## `finite` law constraint propagates awkwardly to callers
- **Task:** `System.Text` Unicode range helpers (round-2 / Text.stark)
- **What happened:** An in-package test entry point had to be declared `fn` rather than `finite` (STK4107) solely because it transitively calls a general `fn` (`AppendUnicode`), even though the wrapper itself is trivially terminating.
- **Why it hurt:** The most-restrictive-kind discipline forces the looser `fn` to bubble up through otherwise-finite helpers, so callers can't keep the stronger `finite` guarantee they'd like.
- **Workaround:** Declared the wrapper `fn`.
- **Suggested fix:** Clearer guidance/inference for when `finite` is recoverable, or a diagnostic that points at the specific non-finite callee forcing the downgrade.
- **Partly resolved (diagnostic only):** STK4107 now names the offending callee ("finite function 'Outer' … calls non-finite function 'Maybe'"). The most-restrictive-kind propagation rule itself is unchanged.

## `dynamic` storage corrupts the heap when grown inside a test executable
- **Task:** All `tests-stark/stdlib.*` suites that build collections/owned text
- **What happened:** Code compiled directly INTO a test executable corrupts the heap when it grows `dynamic` storage; the identical operations are correct when compiled into the stdlib package. Every stark-side test suite must do all `dynamic` building inside a stdlib entry point and have `[Fact]` code only *read* results.
- **Why it hurt:** It inverts normal test authoring — you can't just build a `List`/`OwnedAscii` in a `[Fact]` and assert on it; you must add a stdlib shim per scenario. Large, recurring ceremony across every new test.
- **Workaround:** Wrap each scenario's `dynamic` building in an `internal fn` in the stdlib module under test (e.g. `AppendUnicodeRangeFrom`, `SplitAscii`) and read the result from the fact.
- **Suggested fix:** Fix the test-executable `dynamic`-growth heap corruption (root-cause the allocator/codegen difference between in-exe and in-package builds); it's the single biggest stark-side test-authoring tax.

## `compiler.Tests` never exercises the package-image consumption path
- **Task:** Self-hosting verification / test infrastructure
- **What happened:** A real regression in app-side imported-generic member calls (`List<i32>.Push` via the serialized stdlib package) landed and passed a full green `compiler.Tests` run, because that suite only ever compiles against stdlib *source*; only `compiler.StandardLibraryTests` builds a real `.starkpkg` and imports it.
- **Why it hurt:** The fast/default suite gives false confidence — "all green" did not cover the exact path self-hosting depends on (consuming the stdlib as a binary package). The gap is invisible unless you know to also run the slower suite.
- **Workaround:** Always run `compiler.StandardLibraryTests` after compiler changes touching type-check recording / lowering / package serialization.
- **Suggested fix:** Add a fast smoke test to `compiler.Tests` that builds a tiny package image and consumes a generic member call through it, so the package-image path has coverage in the default suite.
- **Resolved:** `tests/compiler.Tests/PackageImageGenericMemberSmokeTests.cs` builds a package image in-process, deletes the producer source, and consumes an imported generic instance-member call through the `.starkpkg` — failing fast in the default suite if that binding regresses. A cross-module lock-in test (`LoweringContractFactKeyRegressionTests`) covers the source-import path too.

## text->f64 parse facility (RESOLVED for the exact window; TOML floats now read)
- **Task:** TOML 1.x (`stdlib/src/System/Toml.stark`), text→f64 (`stdlib/src/System/Text.stark`)
- **What happened:** TOML floats (`3.14`, `1e6`, `-inf`, `nan`, underscored digits) need a text→`f64` decoder so a `F64At` accessor can return the value. The stdlib had only the *formatting* direction (`TryFormatF64Ascii`, `AppendFormattedF64Ascii`, `ToAscii(f64)`) plus the `ParseI8..ParseU1024` / `ParseBool` integer family — no `ParseF64`/`ParseDouble`/`ParseFloat`.
- **Why it hurt:** A *correctly-rounded* decimal-string→IEEE-754 `f64` parser is one of the genuinely hard pieces of a numeric library. The full algorithm (Eisel-Lemire fast path + big-integer slow path) is out of reach here: f64↔int casts are numeric `uitofp`/`fptosi` (not bit reinterpret), and there is no `scalbn`/`ldexp`/bitcast, so the slow path cannot be expressed. A naive `mantissa * 10**exp` over the whole input silently mis-rounds many literals, which is worse than rejecting.
- **What landed:** `System.Text.ParseF64Ascii` / `ParseF64Unicode` (+ `ParseF32*` twins that defer then range-check) implement the **Clinger exact path** over a pinned, provably correctly-rounded window: a significand of at most **2**53** and a base-10 exponent with **|exp10| <= 22**. Inside the window the result is `(f64)mantissa * 10**exp10` (or `/ 10**(-exp10)`); both operands are exactly representable in f64, so the single IEEE multiply/divide is itself correctly rounded — bit-exact, verified against `0.1`, `3.14`, `-0.0`, `1234.5`, etc. with runtime `==`. `inf`/`+inf`/`-inf`/`nan` tokens are handled. `System.Toml` now has a `TomlKind.Float` node and a `public F64At` accessor that strips underscores at parse time and defers to `ParseF64Ascii`.
- **Out of the window** (more significant digits than fit in 2**53, or `|exp10| > 22`): the parser returns `TextError.Overflow` and `F64At` surfaces `TomlError.InvalidNumber` with the node's span — an honest "unsupported precision", never a mis-rounded value.
- **Follow-up:** A full Eisel-Lemire path (extending the window to the whole IEEE-754 range) is still future work, blocked on the missing bitcast/`ldexp` primitives noted above. When those land, widen the window and keep the exact path as the fast case.

## Runtime `ascii` equality needs the named `System.Text.Equals`, not `==`
- **Task:** System.IO.Path accessors (`tests-stark/stdlib.IO.Path`)
- **What happened:** The new `FileName` facts return an `ascii` slice that must be checked against an expected literal. Runtime `ascii == ascii` is not the comparison surface — `==` text equality is a *CTFE* facility; the runtime path is the named `System.Text.Equals(ascii, ascii)` law. The pre-existing Path facts only returned `bool` predicates, so they never had to compare two runtime `ascii` values and the gap was invisible until now.
- **Why it hurt:** Minor discovery cost — had to grep `System/Text.stark` to confirm the public runtime equality function and add an extra `import System.Text` to the test module, since path facts alone don't surface a string-compare.
- **Workaround:** `import System.Text` and assert with `System.Text.Equals(FileName(path), "expected")`.
- **Suggested fix:** Nothing required structurally. A one-line note in the Path test harness pointing at `System.Text.Equals` for `ascii` accessor assertions would save the next person the grep.
- **Resolved:** the harness note landed (P9) — `tests-stark/stdlib.IO.Path/PathExtensionTests.stark` now documents using `System.Text.Equals` for runtime `ascii` accessor assertions.

## Good outcome: precomputed `PathFacts` offsets made a new accessor trivial
- **Task:** System.IO.Path accessors (`stdlib/src/System/IO/Path.stark`)
- **What happened:** The only genuine gap was a `FileName` accessor (the full last segment, extension included — distinct from `BaseName`, which strips the extension). `PathFacts` already computes `SegmentStart`/`End`, so the new `FileName`/`FileNameLength` accessors were a direct slice `Path[SegmentStart, End - SegmentStart]` with no new parsing logic, modeled exactly on the existing `BaseName`/`RootName` pairs.
- **Why it hurt:** It didn't — recorded as the positive case. The original `GetFacts` design (precomputing segment/extension offsets into the struct up front) made adding a new query a few lines. This is the shape worth emulating: pay the parse cost once, expose cheap accessors.
- **Workaround:** None needed.
- **Suggested fix:** None — this is the pattern other fact-carrying structs should follow.

## Doctrines are not usable as `where` bounds; the diagnostic misdirects (STK3050)
- **Task:** generic `ContainsElement<T>` / `SequenceEqual<T>` consolidation (`stdlib/src/System/Testing.stark`, PAIN_POINTS #3 fix)
- **What happened:** the natural-looking bound `fn F<T>(...) where DictionaryKey(T)` is rejected with `STK3050 [module-graph]: Unknown thread-safety law predicate 'DictionaryKey' … Supported laws are Transferable and Shareable.` The `where`-predicate slot only accepts thread-safety laws + memory contracts — it has no doctrine-bound form. The working pattern turned out to be an UNBOUNDED `<T>` generic that simply *calls* the doctrine-qualified static `System.Collections.DictionaryKey.Equals(a, b)` (exactly how `Dictionary<K,V>`/`HashSet<T>` use it on their unbounded key generics); the compiler-known machinery synthesizes equality for bool/integer/ascii/unicode at monomorphization.
- **Why it hurt:** the diagnostic is accurate but actively misleading — it implies `where` only knows two laws, with no hint that doctrine conformance is accessed *by call on an unbounded generic*, not by a `where` bound. A reader naturally reaches for `where Equatable(T)` / `where DictionaryKey(T)` and is told the predicate is "unknown" rather than "doctrines aren't `where` predicates — call the doctrine method directly."
- **Workaround:** drop the bound; write `fn ContainsElement<T>(borrow T[] values, borrow T expected)` and call `System.Collections.DictionaryKey.Equals(values[index], expected)`.
- **Suggested fix:** either accept `where SomeDoctrine(T)` as a real bound that lowers to the doctrine's static requirements, OR have STK3050 (when the name resolves to a known doctrine) say "‘X’ is a doctrine, not a `where` predicate — call its static members directly on an unbounded generic." A `DictionaryKey.NotEquals` (or a generic `!=`) would also avoid spelling inequality as `!DictionaryKey.Equals(...)`.
- **Partly resolved:** the STK3050 hint landed (FOLLOWUP #56) — when the predicate name resolves to a known doctrine (`DictionaryKey`), the message now says it is a doctrine and to call its static members directly. Accepting `where SomeDoctrine(T)` as a real bound, and a `DictionaryKey.NotEquals`, remain future work.

## Going generic forces `borrow T` + `where overlap`, losing by-value literal arguments (STK3002/STK3030)
- **Task:** generic `ContainsElement<T>` (`stdlib/src/System/Testing.stark`, PAIN_POINTS #3 fix)
- **What happened:** the old per-primitive overloads took `expected` **by value** with no overlap clause. The single generic must take `borrow T expected` (a generic `T` is not known-copyable), which then (a) requires the argument to be addressable storage — a literal `ContainsElement(xs, 7)` fails `STK3002`, the caller must bind `7` to a local first — and (b) needs `where overlap(values, expected)` to satisfy the default non-overlap obligation (`STK3030`).
- **Why it hurt:** a textbook by-value helper signature looked like it couldn't survive the move to generics without two extra ceremonies the caller feels (bind-literal-to-local + the `overlap` annotation). The first cut shipped `borrow T expected where overlap(...)`, which then broke real callers: the `compiler.StandardLibraryTests` System.Testing fixtures call `ContainsElement(slice, 20)` with a literal and got `STK3002` (a borrow needs addressable storage). This is the classic "per-task check passed in isolation, the full merge regressed" trap — the stark-native suites have no such caller, so only the full suite caught it.
- **RESOLVED:** the escape hatch already exists — `where Copyable(T)` permits a *by-value* generic parameter (precedent: `selfhost.Lexing` `fn bool Duplicate<T>(T value) where Copyable(T)`; Copyable is resolved via `CopyabilityFacts.cs`, not the thread-safety-law slot that STK3050 guards). The landed signature is `public finite law bool ContainsElement<T>(borrow T[] values, T expected) where Copyable(T)`: `expected` is by value, so literals work again with no `overlap` obligation, and the five overloads stay collapsed into one generic. `SequenceEqual<T>(borrow T[] expected, borrow T[] actual) where overlap(...)` keeps two slice borrows (no by-value arg, so no literal problem; `overlap` is needed because callers legitimately pass the same slice twice).
- **Remaining nit:** the diagnostic for the broken case (`STK3002` on a generic `borrow T` arg) doesn't suggest "make it `T` by value under `where Copyable(T)`"; surfacing that hint would shorten the path the next person walks.

## No `System.Testing.Equal(f64, f64)`, and no f64 bit/`ldexp` surface
- **Task:** `ParseF64Ascii` facts (`tests-stark/stdlib.Text`, PAIN_POINTS #8 fix)
- **What happened:** `System.Testing.Equal` has overloads for bool/i32/i64/u32/u64/ascii/unicode but **not** `f64`, so f64 facts assert with raw `==`. (It is bit-exact here because the parsed value and the expected literal both go through the same correctly-rounded conversion, so no epsilon is needed — but the asymmetry is surprising.) Relatedly, there is no f64 bit-reinterpret/`ToBits`, `ldexp`/`scalbn`, or hex-float literal surface, which is the concrete reason the full Eisel-Lemire f64 parser can't be written (see the text→f64 entry above).
- **Why it hurt:** minor — reaching for `==` on f64 in a fact feels like it might be CTFE-only (as it is for text), so it needs a second look to trust; an explicit `Testing.Equal(f64, f64)` (exact, plus an optional ULP/epsilon variant) would remove the doubt.
- **Workaround:** assert with `==` on f64 (valid at runtime for scalars; bit-exact for in-window parse results).
- **RESOLVED (partial):** `System.Testing.Equal(f64, f64)` (exact) and `ApproxEqual(f64, f64, ulps)` landed; the `ParseF64Ascii` facts now assert via `Testing.Equal`. `ApproxEqual` is a documented *relative-epsilon* approximation because a true ULP comparison needs the still-missing f64 bit-reinterpret. The f64 bitcast/`ldexp` primitive (to unblock the full-range parser) remains open — FOLLOWUP #51.

## Fixed-array global `const` spelling is undiscoverable (no stdlib precedent)
- **Task:** `POW10` power-of-ten table (`stdlib/src/System/Text.stark`, PAIN_POINTS #8 fix)
- **What happened:** a fixed-array global constant had **no precedent anywhere in the stdlib**, and the working spelling is `const f64[23] Name = { … }` (dimension on the *type*, per the grammar), not the C-style `const f64 Name[23]`. There was nothing to copy from.
- **Why it hurt:** small discovery cost with no example to model on; the grammar is `type variableDeclarators`, so the array dimension rides the type, but that is easy to get backwards coming from C/C#.
- **RESOLVED:** a fixed-array `const` example landed in SKILL.md (Storage Classes And Globals) and LanguageReference.md §9, and the stdlib now has a real one to copy from — `const f64[23] ParseF64Pow10 = { 1e0 .. 1e22 };` in `System.Text` (the power-of-ten table the LLVM-emission fix unblocked).

## No `System.Math.Abs(f64)` (magnitude must be hand-rolled)
- **Task:** `System.Testing.ApproxEqual` (`stdlib/src/System/Testing.stark`, FOLLOWUP #55)
- **What happened:** `System.Math` has `Min`/`Max`/`Floor`/`Ceiling`/`Round`/`Truncate`/etc. but **no `Abs(f64)`**, so absolute value is written inline as the `v < 0.0 ? 0.0 - v : v` ternary (used three times in `ApproxEqual`, and already open-coded the same way in `System.Text` at `Text.stark:6264`).
- **Why it hurt:** minor but recurring — any tolerance/magnitude/distance code re-derives abs; it reads worse and is easy to get subtly wrong around `-0.0`.
- **Workaround:** the `v < 0.0 ? 0.0 - v : v` idiom (matches the existing `System.Text` usage).
- **Suggested fix:** add `public finite law f64 Abs(f64 value)` (and an `f32` twin) to `System.Math`, alongside the existing `Min`/`Max`; cheap, removes a recurring open-coding.

---
