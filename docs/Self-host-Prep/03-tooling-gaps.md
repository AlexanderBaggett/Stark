# Phase 3 - Tooling Gap Analysis

This phase covers `stark build/run/test`, project/solution manifests, native
metadata, `build/` artifact layout, release packaging, bootstrap staging,
and editor/tooling parity.

## Current Tooling Surface

| Area | Current State | Evidence |
|---|---|---|
| Low-level compiler CLI | `compiler`/`stark` accepts single-file modes: check, emit MIR/SSA/LLVM/object/lib/exe/package, inspect package, target options, native options, logs, diagnostics | `src/Compiler/CompilerCli.cs` |
| Project commands | `stark build`, `stark run`, `stark test` exist in host C# project driver | `src/Compiler/ProjectCliDriver.cs`, `docs/Userfacing/ProjectsAndSolutions.md` |
| Project manifest | `Stark.toml` supports project name/version/kind, root/output, dependencies, native metadata, profiles | `docs/Userfacing/ProjectsAndSolutions.md` |
| Solution manifest | `Stark.solution.toml` supports members, defaults, aliases, profiles | `docs/Userfacing/ProjectsAndSolutions.md` |
| Package image | Current host emits `.starkpkg.json` with source-surface, typed-interface, compiler-facts, generic-templates, native dependency metadata; self-hosting decision OQ-09/doc `20` moves normal loading to binary package images with JSON/text inspection rendered by `stark inspect-pkg` | `docs/Internals/PackageImage.md`, `src/Compiler/PackageImage/*`, `docs/Self-host-Prep/20-package-image-format.md` |
| Build artifacts | Project driver routes Stage0 project outputs through the formal `build/<profile>/<target-triple>/stage0/` layout for `bin`, `obj`, `pkg`, `tests`, explicit package-image inspection views under `artifacts/pkg`, stage-local `stdlib` lookup, and clean scopes; diagnostics/stdout artifact generation and real Stage1/Stage2 execution remain open | `ProjectCliDriver`, integration tests, `docs/Self-host-Prep/25-build-artifact-layout.md` |
| Native toolchain | Host shells out to `clang`, linker, archiver, `pkg-config`; self-hosting decision T10/doc `23` moves primary object emission to bundled libLLVM, with linker/native tools still resolved through the toolchain resolver | `src/Compiler/NativeToolchain.cs`, `docs/Self-host-Prep/ToolchainPackagingRoadmap.md`, `docs/Self-host-Prep/23-libllvm-integration.md` |
| Tests | `stark test` runs test projects and now generates an explicit `main` runner from `[Fact]` / `[Theory]` metadata at build time, with inline-data rows, typed indexed member-data rows, selected-test filters, target-triple platform gates, serial collection grouping, and stable `System.Testing.RunFact` / `SkipFact` reporting; manual `main` remains available for bootstrap tests with no generated test metadata | `src/Compiler/ProjectCliDriver.cs`, `src/Compiler/StarkTestRunnerGenerator.cs`, `docs/Userfacing/ProjectsAndSolutions.md`, `docs/StandardLibrary/System.Testing.md`, `docs/Self-host-Prep/04-test-infrastructure-audit.md` |

## Tooling Gap Table

| ID | Tooling Capability | Needed By Self-hosting | Current Status | Severity |
|---|---|---|---|---|
| T01 | Handwritten parser implementation against canonical `Stark.g4` | Every compiler front-end phase | Strategy specified: `Stark.g4` remains the canonical grammar reference, but the self-hosted compiler uses a handwritten parser; no ANTLR runtime/generated parser/visitor is ported | blocker |
| T02 | Bootstrap staging with C# host Stage0 | Stage0 C# builds Stage1 Stark compiler; Stage1 builds Stage2; eventual host removal | Policy specified: use the existing C# host until the Stark compiler self-hosts; no separate blessed snapshot compiler artifact | blocker |
| T03 | Stage-aware `stark build/run/test` | Ported tests must run against host compiler first, then self-hosted compiler | Partial: project commands accept stage selectors, default to `stage0`, reject unavailable Stage1/Stage2 execution, and route stage0 artifacts under the formal stage directory. Actual Stage1/Stage2 compiler invocation remains open | blocker |
| T04 | Manifest parser/config in Stark | Self-hosted project driver must read `Stark.toml`, `Stark.solution.toml`, `Stark.user.toml` | Strategy specified: add reusable `System.Toml`; project driver uses it plus typed manifest decoding instead of carrying a private `SimpleToml` clone | blocker |
| T05 | Stable `build/` artifact layout | Bootstrap artifacts, package images, host/stage compiler outputs, targeted diagnostics, native objects | Partial: stage0 project builds now resolve/pass a target triple and route final executable/library outputs to `bin/<project>/`, generated test runners and test executables to `tests/<project>/`, saved native intermediates to `obj/<project>/`, project library package images to `pkg/<project>/`, and explicit package-image JSON inspection views to `artifacts/pkg/<project>/` under `build/<profile>/<target-triple>/stage0/`; `stark clean` covers profile/target/stage/diagnostics/artifacts scopes; project builds search the active stage-local `stdlib/` path and nearest repo `stdlib/dist` / `stdlib/src` development paths. Stdlib artifact generation/routing, diagnostics/other artifact export routing, and Stage1/Stage2 routing remain open | blocker |
| T06 | Incremental dependency scanning/cache | Avoid rebuilding compiler/stdlib/packages from scratch in later self-host cycles | Not a required host feature today; no stable cache format | nice-to-have for first self-host, important for productivity |
| T07 | Package image generation/loading in Stark | Package boundaries, stdlib package, imported generics, typed interfaces | Host C# only; decision specified: binary is the normal compiler load format, JSON/text are deterministic `stark inspect-pkg` views generated on demand | blocker |
| T08 | Native toolchain resolver and bundled LLVM/libLLVM | libLLVM-primary backend, final linking, native shims, package libs, clean-machine release | Planned in packaging roadmap; host still uses hard-coded/default tool names and overrides | blocker |
| T09 | Cross-compilation target info and SDK discovery | `--target`, target data layout, target CPU/features, macOS SDK args, Windows/Linux toolchains | Host can pass options and query `clang`; self-host needs process/path/platform APIs | blocker for parity |
| T10 | libLLVM-primary backend integration | Codegen backend and packaging | Decision specified: construct LLVM modules directly through the LLVM C API; textual LLVM is printed only as a debug/inspection artifact | blocker |
| T11 | Stdlib package build/discovery for self-host | Compiler needs `System` package artifacts and source/package resolution | Partial: discovery policy is specified, and project builds now search the active stage/build-local `stdlib/` path, then nearest repo `stdlib/dist` package images, then nearest repo `stdlib/src` source tree, then installed bundled stdlib artifacts next to the compiler distribution; failed `System.*` resolution reports searched stdlib paths plus active profile/target/stage; project builds suppress `STARK_PATH` so hidden global search cannot satisfy missing dependencies. Explicit override fields, artifact generation, and compatibility validation remain open | blocker |
| T12 | Stark-native generated test runner/harness integration | TDD-first port and `stark test` parity | Generated `[Fact]` / `[Theory]` runner support is implemented for project tests, including fact enumeration, inline-data row expansion, typed indexed member-data provider expansion, repeatable selected-test filters, target-triple platform gates, stable serial collection grouping, and `System.Testing.RunFact` / `SkipFact` output; `System.Testing` now has process output/run-match/timeout helpers; initial host-compiler bridge exists through `compiler --host-test-inspect` / `--host-test-server`, and `System.Process.RunCaptureWithInputTimeout` can feed bounded server batches; Stark-side host-compiler wrappers and staged runner selection remain | blocker |
| T13 | VS Code extension and editor tooling parity | Language changes during self-host prep must stay visible to users | Decision specified: track editor/tooling updates, but do not block bootstrap on full editor parity; update syntax/completions when source syntax changes land | nice-to-have unless syntax changes land |
| T14 | Release packaging/doctor/clean-machine verification | Drop host compiler from the normal path, distribute the compiler, bundled toolchain, and stdlib | Roadmap exists but unchecked | blocker before dropping host |
| T15 | Fast diagnostic and artifact access for tests | Ported tests, stage comparisons, package inspection | Initial host runner landed: persistent/batched structured results plus selected LLVM/MIR/SSA artifact text and file export, structured diagnostics/logs/executions, and loaded-module names. Typed Stark helper APIs and additional artifact renderers remain | workaround-exists |

## Build/Run/Test Specific Gaps

| Command | Existing Behavior | Self-hosting Need | Gap IDs |
|---|---|---|---|
| `stark build` | Build current project or solution default/all members; accepts `--target <triple>` and `--stage stage0`; routes stage0 outputs into the formal profile/target/stage layout | Select real Stage1/Stage2 compilers, rebuild stdlib package, cache package image, handle native deps through self-hosted driver | T02, T03, T05, T07, T08, T11 |
| `stark run` | Builds executable into the formal stage0 layout, then spawns it | Process spawn/capture/exit propagation from Stark; real Stage1/Stage2 executable paths | T03, S12 |
| `stark test` | Build test executable(s) into the formal stage0 layout, generate an explicit `[Fact]` runner when facts are present, support selected-test filters, and run the executable | Host-compiler target wrappers for ported tests, platform gating, real staged runner selection | T03, T12, Phase 4 TEST-* |
| `stark clean` | Deletes formal build-layout scopes: active stage by default, or explicit profile/target/diagnostics/artifacts scopes | Stage-aware clean remains useful for Stage1/Stage2 once those compilers exist | T05 |

## Manifest and Native Metadata Gaps

| File / Metadata | Current Role | Self-host Gap |
|---|---|---|
| `Stark.toml` | Project kind/root/output/dependencies/profiles/native metadata | Requires `System.Toml` S13, typed manifest decoding T04, and stage-aware compiler/build fields T03/T05 |
| `Stark.solution.toml` | Solution members/defaults/aliases/profiles | Requires `System.Toml` S13, typed manifest decoding T04, and bootstrap member ordering T02/T03 |
| `Stark.user.toml` | Local tool/native paths | Requires env/user config path APIs S10-S12 plus `System.Toml` S13 and typed config decoding T04 |
| Package-image native metadata | Package-owned native sources, include dirs, library dirs, libraries, pkg-config names | Requires binary package-image codec T07/doc `20`, inspection/export support S14, process/pkg-config S12, native toolchain resolver T08 |
| `build/` | Build outputs under project root | Implement the formal OQ-18/doc `25` layout for stage0/stage1/stage2 compiler outputs, stdlib package artifacts, package images, object/native artifacts, tests, and targeted diagnostic/artifact outputs T05 |

## VS Code / Editor Tooling

Editor parity is tracked but does not block bootstrap. Syntax and completions
must still be updated when source syntax changes land. Known editor-facing
risks:

| Topic | Why It Must Keep Pace |
|---|---|
| Parser implementation T01 | `Stark.g4` remains the canonical grammar reference; syntax highlighting, snippets, diagnostics, formatter, and the handwritten parser must stay aligned with it |
| Language gaps L01-L14 | New syntax such as result propagation or raw strings would require editor updates |
| Project/test commands T03/T12 | Editor test/build tasks need stage-aware commands |
| Package image/stdlib discovery T07/T11 | Completion/import resolution needs package/source discovery parity |

## Tooling Priority

1. T03 plus the remaining T12 harness work for TDD-first porting; the generated `[Fact]` runner slice is landed.
2. T01 handwritten parser implementation, because it determines the largest front-end port shape.
3. T02/T05/T11 for bootstrap staging, the formal build layout from doc `25`,
   and stdlib discovery.
4. T08/T10/T09 for native codegen and release portability.
5. T07/T04 for package/project parity, with binary package-image loading on the hot path, `stark inspect-pkg` JSON/text output for inspection, and `System.Toml` powering project/solution/user config.
6. T10 for libLLVM-primary backend integration, after the required FFI C-string/out-pointer/opaque-handle pieces from doc `23` are available.
7. T14 before dropping the host compiler; T13/T15 only where syntax changes or
   concrete test/debug workflows require them.
