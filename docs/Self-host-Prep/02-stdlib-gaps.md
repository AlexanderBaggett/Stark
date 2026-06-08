# Phase 2 - Standard Library Gap Analysis

The self-hosted compiler needs a standard library that can replace the .NET BCL
surface used by the host compiler. This audit walks the current `System.*`
modules and the host compiler's BCL usage.

## Gap Table

| ID | Stdlib Capability | Current Stark Surface | Host Compiler Need | Severity |
|---|---|---|---|---|
| S01 | Shared `Option<T>` / `Result<T,E>` conventions | `System.Option<T>` and `System.Result<T,E>` exist as ordinary `[Ok]`/`[Err]` enums; module-specific result/status enums also exist (`System.IO.IOResult<T>`, `System.Memory.MemoryResult<T>`, `System.Text.TextResult<T>`) | Migrate compiler-port APIs so nullable refs and `Try*` out patterns use `Option<T>`, while recoverable failures use `Result<T,E>` or domain result/status enums | migration blocker |
| S02 | Text builder and formatted output | `System.Text.OwnedAscii`, `OwnedUnicode`, parse/format helpers, conversions | `StringBuilder`, invariant interpolation, LLVM/MIR/SSA rendering, diagnostics, JSON/TOML emitting | blocker |
| S03 | Text escaping, literal decoding, and golden text support | Source language now has exact-preserving `raw"..."`, `raw"""..."""`, and `$raw` literals; host `TextLiteralDecoder` exists; `System.Text` has conversion/format helpers | Stark-side encode/decode helpers for Stark literals, LLVM snippets, source snippets, and golden text | blocker |
| S04 | Regex or structured pattern matching helper | None found in stdlib; host uses `Regex` in project driver/tests | TOML-ish substitutions, test assertions, diagnostic/source matching | workaround-exists |
| S05 | Compiler integer fact helpers capped at `i1024`/`u1024` | Fixed integer widths through `i1024`/`u1024`; `System.BitOperations` exists; OQ-07 rejects public BigInt/arbitrary precision for self-hosting | Replace the host's `BigInteger` convenience with bounded compiler-internal helpers for literal/range endpoints, enum tags, SSA facts, known-bit masks, and compile-time folding; overflow or values outside `i1024`/`u1024` are diagnostics | blocker |
| S06 | Compiler-grade collections | `List<T>`, `Stack<T>`, `Queue<T>`, `LinkedList<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Lookup<T>` documented; `Dictionary<K,V>` and `HashSet<T>` now have scalar fast paths, compiler-known `ascii`/`unicode` key contracts, and explicit static `Hash`/`Equals` support for non-primitive keys | Blessed OQ-08/doc `19` model still needs ordered maps/sets or sorting paths through `Ord`, explicit deterministic package/diagnostic ordering, and allocation-free borrowed lookup | blocker |
| S07 | Symbol interning | No dedicated interner found | Strongly typed compiler interners for symbols, modules, packages, types, fields, members, and artifact keys; intern at front-end/package boundaries and use distinct compact IDs in hot paths | blocker |
| S08 | Sorting/searching helpers | Not visible as a broad public surface | Deterministic pass ordering, package image ordering, diagnostics, tool args | workaround-exists |
| S09 | File reading and whole-file helpers | `System.IO.File` supports owned handles, write, read byte slices, delete/move/exists; text-reading remains future work | Read `.stark`, TOML, JSON, package images, source snippets; write temp files/artifacts | blocker |
| S10 | Filesystem metadata/temp/walk | `System.FileSystem` supports create/delete/open directory, read entries, exists/isfile/isdir/move | Temp dirs, recursive source discovery, metadata for incremental builds, permissions, symlink handling | blocker |
| S11 | Path manipulation | `System.IO.Path` supports separator, current directory, join, normalize, extension, basename, dirname | Full paths, relative paths, multi-part joins, temp paths, change extension, platform roots | blocker |
| S12 | Process spawn/capture/env/argv | `System.Process` exposes only `CurrentId()` and `Exit(code)` | `clang`, linker, archiver, `pkg-config`, test executable runs, stdout/stderr capture, env vars, CLI args | blocker |
| S13 | `System.Toml` parser/emitter | No public TOML module found; OQ-10 resolves to a reusable `System.Toml` library rather than a manifest-only parser | Parse and emit TOML for `Stark.toml`, `Stark.solution.toml`, `Stark.user.toml`, user config, tests, and tools; project driver performs typed manifest decoding on top of parsed TOML values | blocker |
| S14 | Package inspection JSON/text support | No public JSON module found; package-image format decision OQ-09 moves normal compiler loading to binary | Deterministic JSON/text output for `stark inspect-pkg` and package-image golden tests; normal dependency loading uses the binary codec tracked under T07/doc `20` | blocker for inspection/tests/tooling; not the package load path |
| S15 | Time/stopwatch | No broad time module found | targeted debug timings, regression/benchmark harness | nice-to-have for self-host; not on the cutover path |
| S16 | Threading coordination | `System.Threading` exposes thread start/join/detach, sleep/yield, and seq-cst atomic types; platform wait/wake hooks remain internal | Synchronous self-hosting needs no concurrency. Future parallel build/test needs only captured thread payloads, `System.Threading.Synchronized<T>` / `Locked<T>`, and MPSC channels for progress/results per doc `22` | workaround-exists for single-threaded v1; specified for future parallel build/test |
| S17 | Arena/table IR storage and fact tables | `System.Memory.Allocator` default identity and internal allocation; `arena` reserved, not executable local storage; OQ-16/doc `24` resolves the compiler model as arena/table storage with typed handles plus fact tables | Public allocator/table support for compiler-owned arenas, bulk release, typed-handle dense storage, fact side tables, and validation-friendly builders; `Rc`/`Arc` is not the default IR model | blocker |
| S18 | Testing/golden/snapshot support | `System.Testing` only has bool assertions, primitive `Equal`, `RunFact`, `ExitCode`; test discovery decision is build-time generated explicit `main` from `[Fact]` metadata | xUnit parity: generated runners, rich assertions, fixtures, temp dirs, snapshots, process capture, theory data | blocker for TDD-first port |

## `System.*` Namespace Audit

| Namespace | APIs Existing Today | APIs Needed For Self-hosted Compiler | Missing / Gap IDs |
|---|---|---|---|
| `System` | Re-exports Console, BitOperations, Collections, FileSystem, IO, IO.File, IO.Path, Math, Memory, Net, Net.Tcp, Process, Threading; imports Runtime/Syscall/Testing | Stable prelude/import shape for compiler and tests | Mostly namespace policy; testing is intentionally not re-exported per docs |
| `System.BitOperations` | Present, not deeply audited here | Bit masks, shifts, popcount/leading-zero style helpers for bounded `i1024`/`u1024` range/value facts | Covered by S05 if fixed-width helpers are insufficient |
| `System.Collections` | `List<T>`, `Stack<T>`, `Queue<T>`, `RingQueue<T>`, `LinkedList<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Lookup<T>`; dictionary/set key support has bool/integer and `ascii`/`unicode` fast paths plus explicit static `Hash`/`Equals` for non-primitive keys | deterministic ordered maps/sets, sorting, iteration, value-update APIs, borrowed lookup APIs, and canonical `Hash` + `Eq` wording | S06, S07, S08 |
| `System.Console` | Write/read helpers exist through runtime/platform modules | Diagnostics, test output, CLI help | Likely enough for basic output; formatting depends on S02 |
| `System.FileSystem` | Directory creation/deletion, open/read entries, exists/isfile/isdir/move, owned `Directory` | Recursive walk, temp directory, metadata, permissions, symlink, robust path errors | S10 |
| `System.IO` | `IOError`, `IOStatus`, `IOResult<T>` style public result surface | Common IO status/result model usable by compiler | Align with settled `Result`/`Option` conventions from S01 |
| `System.IO.File` | Owned `File`, open/read/write byte slices, write text, seek, close/flush/sync, delete/move/exists | Read all text/bytes, line reading, UTF decoding, write-all helpers, atomic replace, file metadata | S09 |
| `System.IO.Path` | Separators, current dir, join two paths, normalize, extension/base/dirname, path facts | Absolute/full path, relative path, rooted detection, combine many parts, change extension, temp path/name, canonicalization without requiring existence | S11 |
| `System.Math` | Math module exists; old roadmap noted trig/PRNG and missing log/pow/exp, but source was not fully audited here | `Math.Pow`, min/max, possibly log helpers for heuristics/metrics | S05 and S15; confirm exact public functions before implementation |
| `System.Memory` | `MemoryError`, `MemoryStatus`, `MemoryResult<T>`, `Allocator.Default`, internal allocate/reallocate/free, dynamic storage helper functions | Public allocator strategy, compiler-owned arena/table APIs, typed-handle dense storage helpers, bulk release, fact-table storage, memory builders | S17 |
| `System.Net`, `System.Net.Tcp` | TCP clients/listeners and net result/status exist | Not required by host compiler core; used by stdlib tests | No self-host blocker except tests/platform gating |
| `System.Process` | `CurrentId`, `Exit` only | argv/env, spawn, capture, working directory, process status, executable permissions | S12 |
| `System.Runtime.*`, `System.Syscall` | Internal platform boundaries for Linux/macOS/Windows, file, directory, thread, TCP, allocation | Self-hosted compiler should avoid depending on internals directly except stdlib/platform code | Public wrappers missing under S09-S12, S16-S17 |
| `System.Testing` | `TestStatus`, `True`, `False`, primitive `Equal`, `Fail`, `RunFact`, `ExitCode`, `Exit`; discovery will be generated into an explicit `main` rather than reflected at runtime | Full compiler test harness: generated runner support, assertions, fixtures, snapshots, process capture | S18 and Phase 4 TEST-* |
| `System.Text` | Encodings, owned text, conversions, parsing/formatting many integer widths, text errors/results; exact ordinal `ascii`/`unicode` and owned-text `Hash`/`Equals`/`Compare`/`Format` helpers | Compiler-grade builders, escaping, joining, casing, splitting, efficient append, stable formatting | S02, S03, S06 |
| `System.Threading` | Thread start/join/detach, sleep/yield, seq-cst atomics; platform wait/wake internals | Captured thread payloads, easy guarded shared state through `Synchronized<T>` / `Locked<T>`, and MPSC channels if parallel build/test is enabled | S16/doc `22` |

## Host BCL Call Clusters To Replace

| Host Cluster | Example Paths | Stark Replacement Needed |
|---|---|---|
| Bounded compiler integer-fact arithmetic replacing C# `BigInteger` | `src/Compiler/TypeChecking.cs`, `src/Compiler/CompileTimeExpressionEvaluator.cs`, `src/Compiler/SsaOptimization/SsaValueFactAnalyzer.cs` | S05 |
| String-key symbol tables | `src/Compiler/TypeChecking.cs` fields `_namedTypes`, `_typeAliases`, `_functions`, `_functionOverloads`; package builders/loaders | S06 generic text-key contracts plus S07 typed compiler interning |
| File/path/process | `src/Compiler/CompilerCli.cs`, `src/Compiler/ProjectCliDriver.cs`, `src/Compiler/NativeToolchain.cs`, integration tests | S09, S10, S11, S12 |
| Package image serialization and inspection | `src/Compiler/PackageImage/*`, `CompilerCli` inspect/emit paths | T07/doc `20` for binary load format; S14 for deterministic JSON/text inspection/export |
| TOML project driver | `src/Compiler/ProjectCliDriver.cs` private `SimpleToml`; `Stark.toml`, `Stark.solution.toml`, `Stark.user.toml` | S13 `System.Toml` plus typed manifest decoding |
| Text rendering | `src/Compiler/ArtifactTextRenderer.cs`, `src/Compiler/LlvmIrEmission/*`, diagnostics | S02, S03 |
| Regex/pattern tests | Feature/integration/compiler tests, `ProjectCliDriver` | S04, S18 |
| Stopwatch/timing | `CompilerPipeline`, `NativeToolchain`, optional tool metrics | S15 |

## Stdlib Priority For Self-hosting

1. S18 test support, because tests port first.
2. S12 process/env/argv and S09-S11 file/path/filesystem, because tests and build driver need host compiler/tool execution.
3. S01/S02/S05/S06/S07/S13/S14 plus T07/doc `20`, because the compiler core cannot be ported without result values, text builders, bounded `i1024`/`u1024` integer-fact helpers, generic collections, typed compiler interners, `System.Toml`, binary package-image loading, and deterministic package-image inspection/export.
4. S17 arena/table IR storage and fact tables, because large compiler IR graphs and backend facts need the decided typed-handle/fact-preserving model before mass porting.
5. S16/S15/S04 as parity and maintainability work. S16 is limited to doc `22`'s captured payloads, `Synchronized<T>`, and channels if parallel build/test execution is chosen before bootstrap.
