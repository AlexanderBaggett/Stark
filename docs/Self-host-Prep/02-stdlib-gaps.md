# Phase 2 - Standard Library Gap Analysis

The self-hosted compiler needs a standard library that can replace the .NET BCL
surface used by the host compiler. This audit walks the current `System.*`
modules and the host compiler's BCL usage.

## Gap Table

| ID | Stdlib Capability | Current Stark Surface | Host Compiler Need | Severity |
|---|---|---|---|---|
| S01 | Shared `Option<T>` / `Result<T,E>` conventions | Module-specific result enums exist: `System.IO.IOResult<T>`, `System.Memory.MemoryResult<T>`, `System.Text.TextResult<T>` | Replace nullable refs, `Try*` out patterns, exceptions, parse/load failures | blocker |
| S02 | Text builder and formatted output | `System.Text.OwnedAscii`, `OwnedUnicode`, parse/format helpers, conversions | `StringBuilder`, invariant interpolation, LLVM/MIR/SSA rendering, diagnostics, JSON/TOML emitting | blocker |
| S03 | Text escaping, raw/multiline test text support | `TextLiteralDecoder` exists in host, `System.Text` has conversion/format helpers | Encode/decode Stark literals, LLVM snippets, source snippets, golden text | blocker |
| S04 | Regex or structured pattern matching helper | None found in stdlib; host uses `Regex` in project driver/tests | TOML-ish substitutions, test assertions, diagnostic/source matching | workaround-exists |
| S05 | Arbitrary precision integers | Fixed integer widths through `i1024`/`u1024`; `System.BitOperations` exists | Replace `BigInteger` for literal/range endpoints, enum tags, SSA facts, compile-time folding | blocker unless Alexander decides `i1024` is the maximum compiler integer domain |
| S06 | Compiler-grade collections | `List<T>`, `Stack<T>`, `Queue<T>`, `LinkedList<T>`, `Dictionary<K,V>`, `Lookup<T>` documented; dictionary keys initially bool/integer | String-key dictionaries, `HashSet<T>`, ordered maps/sets, deterministic iteration, copy-free lookups | blocker |
| S07 | Symbol interning | No dedicated interner found | Compiler names, module keys, symbols, type display names, package image names | workaround-exists |
| S08 | Sorting/searching helpers | Not visible as a broad public surface | Deterministic pass ordering, package image ordering, diagnostics, tool args | workaround-exists |
| S09 | File reading and whole-file helpers | `System.IO.File` supports owned handles, write, read byte slices, delete/move/exists; text-reading remains future work | Read `.stark`, TOML, JSON, package images, source snippets; write temp files/artifacts | blocker |
| S10 | Filesystem metadata/temp/walk | `System.FileSystem` supports create/delete/open directory, read entries, exists/isfile/isdir/move | Temp dirs, recursive source discovery, metadata for incremental builds, permissions, symlink handling | blocker |
| S11 | Path manipulation | `System.IO.Path` supports separator, current directory, join, normalize, extension, basename, dirname | Full paths, relative paths, multi-part joins, temp paths, change extension, platform roots | blocker |
| S12 | Process spawn/capture/env/argv | `System.Process` exposes only `CurrentId()` and `Exit(code)` | `clang`, linker, archiver, `pkg-config`, test executable runs, stdout/stderr capture, env vars, CLI args | blocker |
| S13 | TOML parser/emitter | No public TOML module found | `Stark.toml`, `Stark.solution.toml`, user config | blocker |
| S14 | JSON parser/emitter | No public JSON module found | `.starkpkg.json` load/write/inspect, optional machine-readable diagnostics/doctor | blocker unless package image changes format |
| S15 | Time/stopwatch | No broad time module found | pass durations, toolchain metrics, regression/benchmark harness | nice-to-have for self-host; blocker for parity metrics |
| S16 | Threading/sync | `System.Threading` exposes thread start/join/detach plus platform sleep/yield/futex internals; no public mutex/rwlock/once/atomics/channels | Parallel build/test, cache initialization, possible future pass parallelism | workaround-exists for single-threaded v1 |
| S17 | Allocator/arena/shared ownership | `System.Memory.Allocator` default identity and internal allocation; `arena` reserved, not executable local storage | IR arena/handle strategy, large graphs, shared references from host C# replacement | blocker |
| S18 | Testing/golden/snapshot support | `System.Testing` only has bool assertions, primitive `Equal`, `RunFact`, `ExitCode` | xUnit parity: rich assertions, fixtures, temp dirs, snapshots, process capture, theory data | blocker for TDD-first port |

## `System.*` Namespace Audit

| Namespace | APIs Existing Today | APIs Needed For Self-hosted Compiler | Missing / Gap IDs |
|---|---|---|---|
| `System` | Re-exports Console, BitOperations, Collections, FileSystem, IO, IO.File, IO.Path, Math, Memory, Net, Net.Tcp, Process, Threading; imports Runtime/Syscall/Testing | Stable prelude/import shape for compiler and tests | Mostly namespace policy; testing is intentionally not re-exported per docs |
| `System.BitOperations` | Present, not deeply audited here | Bit masks, shifts, popcount/leading-zero style helpers for range/value facts | Covered by S05 if fixed-width helpers are insufficient |
| `System.Collections` | `List<T>`, `Stack<T>`, `Queue<T>`, `RingQueue<T>`, `LinkedList<T>`, `Dictionary<K,V>`, `Lookup<T>`; dictionary key doctrine initially bool/integer | `Dictionary<Ascii,T>`, `Dictionary<Unicode,T>`, `HashSet<T>`, deterministic ordered maps/sets, sorting, iteration, value-update APIs | S06, S07, S08 |
| `System.Console` | Write/read helpers exist through runtime/platform modules | Diagnostics, test output, CLI help | Likely enough for basic output; formatting depends on S02 |
| `System.FileSystem` | Directory creation/deletion, open/read entries, exists/isfile/isdir/move, owned `Directory` | Recursive walk, temp directory, metadata, permissions, symlink, robust path errors | S10 |
| `System.IO` | `IOError`, `IOStatus`, `IOResult<T>` style public result surface | Common IO status/result model usable by compiler | Needs shared Result convention S01 |
| `System.IO.File` | Owned `File`, open/read/write byte slices, write text, seek, close/flush/sync, delete/move/exists | Read all text/bytes, line reading, UTF decoding, write-all helpers, atomic replace, file metadata | S09 |
| `System.IO.Path` | Separators, current dir, join two paths, normalize, extension/base/dirname, path facts | Absolute/full path, relative path, rooted detection, combine many parts, change extension, temp path/name, canonicalization without requiring existence | S11 |
| `System.Math` | Math module exists; old roadmap noted trig/PRNG and missing log/pow/exp, but source was not fully audited here | `Math.Pow`, min/max, possibly log helpers for heuristics/metrics | S05 and S15; confirm exact public functions before implementation |
| `System.Memory` | `MemoryError`, `MemoryStatus`, `MemoryResult<T>`, `Allocator.Default`, internal allocate/reallocate/free, dynamic storage helper functions | Public allocator strategy, arenas/handles, shared ownership decision, memory builders | S17 |
| `System.Net`, `System.Net.Tcp` | TCP clients/listeners and net result/status exist | Not required by host compiler core; used by stdlib tests | No self-host blocker except tests/platform gating |
| `System.Process` | `CurrentId`, `Exit` only | argv/env, spawn, capture, working directory, process status, executable permissions | S12 |
| `System.Runtime.*`, `System.Syscall` | Internal platform boundaries for Linux/macOS/Windows, file, directory, thread, TCP, allocation | Self-hosted compiler should avoid depending on internals directly except stdlib/platform code | Public wrappers missing under S09-S12, S16-S17 |
| `System.Testing` | `TestStatus`, `True`, `False`, primitive `Equal`, `Fail`, `RunFact`, `ExitCode`, `Exit` | Full compiler test harness: discovery, assertions, fixtures, snapshots, process capture | S18 and Phase 4 TEST-* |
| `System.Text` | Encodings, owned text, conversions, parsing/formatting many integer widths, text errors/results | Compiler-grade builders, string hashing/equality, escaping, joining, casing, splitting, efficient append, stable formatting | S02, S03, S06 |
| `System.Threading` | Thread start/join/detach; platform internals for sleep/yield/futex | Mutex/RwLock/Once/atomics/channels, worker pool if parallel build/test | S16 |

## Host BCL Call Clusters To Replace

| Host Cluster | Example Paths | Stark Replacement Needed |
|---|---|---|
| Big integer arithmetic | `src/Compiler/TypeChecking.cs`, `src/Compiler/CompileTimeExpressionEvaluator.cs`, `src/Compiler/SsaOptimization/SsaValueFactAnalyzer.cs` | S05 |
| String-key symbol tables | `src/Compiler/TypeChecking.cs` fields `_namedTypes`, `_typeAliases`, `_functions`, `_functionOverloads`; package builders/loaders | S06, S07 |
| File/path/process | `src/Compiler/CompilerCli.cs`, `src/Compiler/ProjectCliDriver.cs`, `src/Compiler/NativeToolchain.cs`, integration tests | S09, S10, S11, S12 |
| JSON package image | `src/Compiler/PackageImage/*`, `CompilerCli` inspect/emit paths | S14 |
| TOML project driver | `src/Compiler/ProjectCliDriver.cs` `SimpleToml`, `Stark.toml`, `Stark.solution.toml` | S13 |
| Text rendering | `src/Compiler/ArtifactTextRenderer.cs`, `src/Compiler/LlvmIrEmission/*`, diagnostics | S02, S03 |
| Regex/pattern tests | Feature/integration/compiler tests, `ProjectCliDriver` | S04, S18 |
| Stopwatch/timing | `CompilerPipeline`, `NativeToolchain`, tool metrics | S15 |

## Stdlib Priority For Self-hosting

1. S18 test support, because tests port first.
2. S12 process/env/argv and S09-S11 file/path/filesystem, because tests and build driver need host compiler/tool execution.
3. S01/S02/S05/S06/S13/S14, because the compiler core cannot be ported without result values, text builders, BigInt, collections, TOML, and package-image serialization.
4. S17 memory/arena/shared ownership, because large compiler IR graphs need a deliberate ownership strategy before mass porting.
5. S16/S15/S04 as parity and maintainability work, unless chosen test strategy makes them earlier blockers.
