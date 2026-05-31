# Phase 3 - Tooling Gap Analysis

This phase covers `stark build/run/test`, project/solution manifests, native
metadata, `.stark/build/` artifact layout, release packaging, bootstrap staging,
and editor/tooling parity.

## Current Tooling Surface

| Area | Current State | Evidence |
|---|---|---|
| Low-level compiler CLI | `compiler`/`stark` accepts single-file modes: check, emit MIR/SSA/LLVM/object/lib/exe/package, inspect package, target options, native options, logs, diagnostics | `src/Compiler/CompilerCli.cs` |
| Project commands | `stark build`, `stark run`, `stark test` exist in host C# project driver | `src/Compiler/ProjectCliDriver.cs`, `docs/Userfacing/ProjectsAndSolutions.md` |
| Project manifest | `Stark.toml` supports project name/version/kind, root/output, dependencies, native metadata, profiles | `docs/Userfacing/ProjectsAndSolutions.md` |
| Solution manifest | `Stark.solution.toml` supports members, defaults, aliases, profiles | `docs/Userfacing/ProjectsAndSolutions.md` |
| Package image | `.starkpkg.json` has source-surface, typed-interface, compiler-facts, generic-templates, native dependency metadata | `docs/Internals/PackageImage.md`, `src/Compiler/PackageImage/*` |
| Build artifacts | Project driver uses `.stark/build/` by convention in tests/source, but no self-host bootstrap staging contract is documented | `ProjectCliDriver`, integration tests |
| Native toolchain | Host shells out to `clang`, linker, archiver, `pkg-config`; release packaging roadmap says bundled LLVM is planned, not done | `src/Compiler/NativeToolchain.cs`, `docs/Self-host-Prep/ToolchainPackagingRoadmap.md` |
| Tests | `stark test` runs test projects, but Stark-native `System.Testing` discovery is explicit and minimal | `docs/Userfacing/ProjectsAndSolutions.md`, `docs/StandardLibrary/System.Testing.md` |

## Tooling Gap Table

| ID | Tooling Capability | Needed By Self-hosting | Current Status | Severity |
|---|---|---|---|---|
| T01 | Parser strategy: ANTLR runtime/generator port, hand parser, or Stark-native generated parser | Every compiler front-end phase | Unspecified; host uses generated C# from `Stark.g4`; runtime package `4.13.1`, generated source says `4.13.2` | blocker |
| T02 | Bootstrap staging and snapshot compiler policy | Stage0 C# builds Stage1 Stark compiler; Stage1 builds Stage2; eventual host removal | Unspecified | blocker |
| T03 | Stage-aware `stark build/run/test` | Ported tests must run against host compiler first, then self-hosted compiler | Project commands exist but no stage selection/snapshot handling | blocker |
| T04 | Manifest parser/config in Stark | Self-hosted project driver must read `Stark.toml`, `Stark.solution.toml`, `Stark.user.toml` | Host has C# `SimpleToml`; stdlib has no TOML parser | blocker |
| T05 | Stable `.stark/build/` artifact layout | Bootstrap artifacts, package images, host/stage compiler outputs, logs, native objects | Partially implemented by host conventions; not documented as bootstrap contract | blocker |
| T06 | Incremental dependency scanning/cache | Avoid rebuilding compiler/stdlib/packages from scratch in later self-host cycles | Not a required host feature today; no stable cache format | nice-to-have for first self-host, important for productivity |
| T07 | Package image generation/loading in Stark | Package boundaries, stdlib package, imported generics, typed interfaces | Host C# only; JSON serializer gap S14 | blocker |
| T08 | Native toolchain resolver and bundled LLVM | Textual LLVM pipeline, native shims, package libs, clean-machine release | Planned in packaging roadmap; host still uses hard-coded/default tool names and overrides | blocker |
| T09 | Cross-compilation target info and SDK discovery | `--target`, target data layout, target CPU/features, macOS SDK args, Windows/Linux toolchains | Host can pass options and query `clang`; self-host needs process/path/platform APIs | blocker for parity |
| T10 | LLVM integration decision: keep textual IR + shell-out or bind `libLLVM` | Codegen backend and packaging | Current compiler emits textual LLVM and shells out | blocker decision, implementation can stay text |
| T11 | Stdlib package build/discovery for self-host | Compiler needs `System` package artifacts and source/package resolution | `stdlib/Stark.toml` and `stdlib/dist/libSystem.starkpkg.json` exist; release discovery is planned | blocker |
| T12 | Stark-native test runner/harness integration | TDD-first port and `stark test` parity | Minimal explicit `System.Testing` and project test execution exist | blocker |
| T13 | VS Code extension and editor tooling parity | Language changes during self-host prep must stay visible to users | Not audited in source tree; decision needed on whether it blocks self-host milestone | nice-to-have unless syntax changes land |
| T14 | Release packaging/doctor/clean-machine verification | Drop host compiler, distribute snapshot toolchain and stdlib | Roadmap exists but unchecked | blocker before dropping host |
| T15 | Machine-readable diagnostics/logs/metrics | Ported tests, CI, doctor, package inspection | Host has text diagnostics/logs and some metrics path | workaround-exists |

## Build/Run/Test Specific Gaps

| Command | Existing Behavior | Self-hosting Need | Gap IDs |
|---|---|---|---|
| `stark build` | Build current project or solution default/all members | Select compiler stage, rebuild stdlib package, use staged `.stark/build`, cache package image, handle native deps through self-hosted driver | T02, T03, T05, T07, T08, T11 |
| `stark run` | Build executable then spawn it | Process spawn/capture/exit propagation from Stark; stage-aware executable paths | T03, S12 |
| `stark test` | Build test executable(s) and run them; discovery is explicit in test `main` | Test discovery/runner, host-compiler target mode for ported tests, snapshots/temp dirs/process capture, platform gating | T03, T12, Phase 4 TEST-* |

## Manifest and Native Metadata Gaps

| File / Metadata | Current Role | Self-host Gap |
|---|---|---|
| `Stark.toml` | Project kind/root/output/dependencies/profiles/native metadata | Requires Stark TOML parser S13 and stage-aware compiler/build fields T03/T05 |
| `Stark.solution.toml` | Solution members/defaults/aliases/profiles | Requires TOML parser S13 and bootstrap member ordering T02/T03 |
| `Stark.user.toml` | Local tool/native paths | Requires env/user config path APIs S10-S12 and TOML parser S13 |
| `.starkpkg.json` native metadata | Package-owned native sources, include dirs, library dirs, libraries, pkg-config names | Requires JSON S14, process/pkg-config S12, native toolchain resolver T08 |
| `.stark/build/` | Build outputs under project root | Needs formal layout for stage0/stage1/stage2 compiler, stdlib package, object/native/log/metrics outputs T05 |

## VS Code / Editor Tooling

No VS Code extension source was audited in the tracked `src/`, `tests/`,
`stdlib`, or `docs` paths. If it lives elsewhere, it needs a separate pass.
Known editor-facing risks:

| Topic | Why It Must Keep Pace |
|---|---|
| Parser strategy T01 | Grammar, syntax highlighting, snippets, diagnostics, and formatter depend on the source grammar |
| Language gaps L01-L14 | New syntax such as result propagation or raw strings would require editor updates |
| Project/test commands T03/T12 | Editor test/build tasks need stage-aware commands |
| Package image/stdlib discovery T07/T11 | Completion/import resolution needs package/source discovery parity |

## Tooling Priority

1. T12 and T03 for TDD-first porting.
2. T01 parser strategy, because it determines the largest front-end port shape.
3. T02/T05/T11 for bootstrap staging and artifact layout.
4. T08/T10/T09 for native codegen and release portability.
5. T07/T04 for package/project parity.
6. T14/T13/T15 before dropping the host compiler.
