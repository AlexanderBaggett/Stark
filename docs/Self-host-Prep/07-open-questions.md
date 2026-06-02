# Phase 7 - Open Questions And Decisions Needed

Each item is phrased as a concrete decision with options and trade-offs.

## Decisions

| ID | Decision | Options | Trade-offs / Notes |
|---|---|---|---|
| OQ-01 | Where should the self-hosted compiler source live? | A. `src-stark/` beside the C# host. B. Replace `src/` after bootstrap. C. `compiler/` package with C# host archived elsewhere. | The checklist uses `src-stark/` provisionally. A keeps bootstrap clear; B is clean after M7 but disruptive early; C matches package layout if Stark projects become the normal organization. |
| OQ-02 | Parser strategy T01 | A. Port ANTLR runtime/generated parser. B. Write handwritten recursive-descent parser. C. Build/use Stark-native parser generator. | A preserves `Stark.g4` but brings a large runtime and generated-class shape. B drops ANTLR but duplicates grammar logic. C is attractive long-term but creates another compiler tool before self-hosting. |
| OQ-03 | Resolve ANTLR version mismatch | A. Regenerate with runtime `4.13.1`. B. Update package/runtime to `4.13.2`. C. Treat generated files as frozen until parser strategy changes. | `src/compiler.csproj` references `Antlr4.Runtime.Standard 4.13.1`, generated files say ANTLR `4.13.2`. This should not drift into self-hosting unnoticed. |
| OQ-04 | Error propagation surface L01 | A. Add `?`-style propagation for approved `Result`/`Option` shapes. B. Require explicit `switch` everywhere. C. Add library macros/templates if such a feature exists later. | A reduces port noise but adds language work. B is simplest semantically but bloats compiler code. C depends on future metaprogramming not audited here. |
| OQ-05 | Compiler invariant failure policy L07 | **Resolved: none of A/B/C — no trap/assert surface is added.** Invalid states are made unrepresentable instead: (1) restructure cross-pass lookups into typed handoffs, (2) exhaustive switches + definite-return analysis (STK3044/STK3045) turn impossible-state code into compile errors, (3) real environment failures are `Result` + `try`, (4) the small residual reports via `Result<T, InternalError>` to `main` or stderr + `System.Process.Exit(1)`. See doc `09` Error Model for the port convention. | A (trap APIs) was rejected as a give-up hatch contradicting "invalid states unrepresentable"; B alone is noisy; the resolution combines type-system guarantees with B for the residual. |
| OQ-06 | Optional/null replacement L11 | A. Add/use `Option<T>` in stdlib. B. Use project-local compiler enums. C. Encode optional references as nullable raw pointers only in unsafe internals. | A is reusable and testable; B avoids global stdlib design but duplicates patterns; C risks leaking unsafe nulls into safe compiler logic. |
| OQ-07 | Big integer scope S05 | A. Implement true arbitrary precision `BigInt`. B. Use `i1024/u1024` facade and declare compiler literal/range domain capped. C. Hybrid fixed-limb `BigInt` just for compiler. | A is most complete; B is fastest if language spec allows it; C keeps v1 bounded but needs a clear failure mode for oversized literals. |
| OQ-08 | Generic hashing/equality doctrine L06/S06 | A. Extend doctrines/traits and stdlib for string and user-defined keys. B. Add specialized compiler dictionaries for text/symbol keys. C. Intern all symbols and use integer IDs as dictionary keys. | A benefits all users; B is quick but compiler-specific; C is efficient and Stark-friendly but changes compiler model shape. |
| OQ-09 | Package image format S14/T07 | A. Keep `.starkpkg.json` and add JSON stdlib. B. Move to Stark-native binary package format. C. Keep JSON for inspection but use binary for compiler load. | A preserves current docs/tests; B may simplify loading/perf later but loses diffability; C adds dual-format complexity. |
| OQ-10 | TOML strategy S13/T04 | A. Add general TOML parser. B. Keep a deliberately small Stark manifest parser for current files. C. Change manifest format. | A is reusable; B matches current `SimpleToml`; C breaks user-facing docs and should be avoided unless there is a strong reason. |
| OQ-11 | Build-driver concurrency L10/S16 | A. Port driver synchronously first. B. Add thread/channel primitives and parallel build now. C. Add `async` language support. | A is lowest risk; B improves build speed but expands stdlib; C has no second strong use case in this audit. |
| OQ-12 | LLVM integration T10 | A. Keep textual LLVM and shell out. B. Bind `libLLVM`. C. Support both with text as bootstrap path. | A matches host and minimizes bootstrap scope; B may improve performance and diagnostics but increases FFI/API surface; C costs more but offers transition. |
| OQ-13 | Bootstrap snapshot policy T02 | A. Check in a blessed snapshot compiler artifact. B. Require previous release compiler to build next release. C. Keep C# host as permanent stage0. | A aids source builds but needs provenance/checksums; B is standard but complicates first release; C delays true self-hosting. |
| OQ-14 | Stark test runner design TEST-01/T12 | A. Explicit `main` runner generated from `[Fact]`. B. Runtime discovery from metadata. C. Keep handwritten explicit runners. | A gives xUnit-like ergonomics without reflection; B likely conflicts with no runtime reflection; C is simple but high-maintenance. |
| OQ-15 | Test artifact access TEST-07 | A. Add machine-readable CLI artifact/diagnostic output. B. Expose compiler-as-library APIs to Stark tests. C. Compare only textual CLI outputs. | A decouples tests from internals; B gives strongest coverage but needs stable APIs; C is easiest but loses many current pipeline assertions. |
| OQ-16 | Memory model for compiler IR S17 | A. Arena plus handle indices. B. Reference-counted shared ownership. C. Owned trees with explicit cross-reference tables. | A is idiomatic and efficient for compiler graphs; B resembles C# sharing but adds runtime cost/safety design; C is simple in some passes but awkward for SSA/MIR graphs. |
| OQ-17 | Alias/noalias misuse policy L13 | A. All wrong alias-class uses are compile-time diagnostics in type/lowering validation. B. Allow unsafe escape hatches only through `unsafe assume disjoint(...)` with explicit root checks. C. Let backend validation catch some cases. | A is safest and matches current intent. B is necessary for external facts but must remain narrow. C risks undefined behavior and should be avoided for accepted programs. |
| OQ-18 | `.stark/build/` layout T05 | A. Formalize per-profile/per-target/stage directories. B. Keep current host layout and layer stage files ad hoc. C. Let each project choose. | A supports deterministic bootstrap and CI; B is faster but fragile; C makes tooling/tests harder. |
| OQ-19 | Stdlib/package discovery T11 | A. Prefer bundled stdlib package next to compiler. B. Prefer source-tree stdlib during development. C. Require explicit dependency path always. | A is release-friendly; B is contributor-friendly; C is explicit but poor ergonomics. A+B with clear priority is likely needed. |
| OQ-20 | VS Code/editor blocking status T13 | A. Treat editor parity as required before M7. B. Track but do not block bootstrap. C. Only update if syntax changes. | A protects users; B keeps compiler focus; C is practical if self-hosting mostly changes internals. |

## Unresolved Evidence Items

| Item | Why It Matters |
|---|---|
| Exact VS Code extension location | No extension source was audited in tracked `src`, `tests`, `stdlib`, or `docs`; T13 may need a separate repo/app audit. |
| Exact public `System.Math` surface | Docs/source were not exhaustively checked for every math helper; S05 covers BigInt and compile-time integer needs regardless. |
| Final package image stability policy | `docs/Internals/PackageImage.md` says no embedded format version for v1.1 and compiler/source evolve together. Dropping the host may require stronger snapshot compatibility rules. |
| Final definition of "alias class" terminology | Current repo uses type aliases and scoped noalias groups; no distinct user-facing "alias class" feature was found. If Alexander means a separate alias-class concept, it needs a language/design decision before L13 is closed. |

## Consistency Notes

- `05-port-checklist.md` references OQ-01 only for provisional target paths.
- Roadmap milestones reference only gap IDs defined in `01-language-feature-gaps.md`, `02-stdlib-gaps.md`, `03-tooling-gaps.md`, and `04-test-infrastructure-audit.md`.
- Parser strategy is tracked as both L14 and T01 because it is a language-expression blocker for the compiler port and a tooling implementation decision.
